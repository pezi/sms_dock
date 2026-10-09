//! Command-line tool `smsdock` for the SMS Dock WebSocket API.
//!
//! Exit codes: 0 success, 1 the server answered with an error, 2 wrong
//! usage, 3 connection or TLS problem.

#![forbid(unsafe_code)]

use std::io::{self, IsTerminal, Read, Write};
use std::process::ExitCode;
use std::time::Duration;

use chrono::{DateTime, Local, NaiveDate, NaiveDateTime, TimeZone};
use clap::builder::PossibleValuesParser;
use clap::{Args, Parser, Subcommand};
use serde::Serialize;
use serde_json::Value;
use smsdock::{
    fetch_fingerprint, ConversationFilter, Direction, Error, Event, MessageFilter, SmsDockClient,
    DEFAULT_API_PORT, FINGERPRINT_ENV, MAX_PAGE_SIZE, PASSWORD_ENV,
};

const EXIT_OK: u8 = 0;
const EXIT_API_ERROR: u8 = 1;
const EXIT_USAGE: u8 = 2;
const EXIT_CONNECTION: u8 = 3;

/// Longest text in a table, in characters.
const TEXT_WIDTH: usize = 60;

const TIME_HELP: &str = "must be a date such as 2026-09-29 or 2026-09-29T08:00, \
                         or milliseconds since the epoch";

/// Read, send and watch SMS through the SMS Dock app.
#[derive(Debug, Parser)]
#[command(
    name = "smsdock",
    after_help = "The password is read from the environment variable SMSDOCK_PASSWORD, \
                  never from the command line."
)]
struct Cli {
    #[command(subcommand)]
    command: Command,
}

#[derive(Debug, Subcommand)]
enum Command {
    /// Print the certificate fingerprint of a server
    #[command(
        long_about = "Print the SHA-256 fingerprint of the certificate the server presents. \
                      Compare it with the Server screen of the app before you use it."
    )]
    Fingerprint {
        /// Address of the phone; the default port is 9443
        #[arg(value_name = "HOST[:PORT]")]
        host: String,
        /// Seconds to wait
        #[arg(long, value_name = "SECONDS", default_value_t = 10.0)]
        timeout: f64,
    },
    /// Show phone, SIMs, permissions and licence
    Status {
        #[command(flatten)]
        connection: Connection,
    },
    /// List conversations, newest first
    Conversations {
        #[command(flatten)]
        connection: Connection,
        /// Search text, numbers and contact names
        #[arg(long, value_name = "TEXT")]
        search: Option<String>,
        /// Only conversations with unread messages
        #[arg(long)]
        unread: bool,
        /// Only this subscription id
        #[arg(long, value_name = "ID")]
        sim: Option<i64>,
        /// Number of conversations
        #[arg(long, value_name = "N", default_value_t = 50, value_parser = positive)]
        limit: usize,
    },
    /// List messages, newest first
    Messages {
        #[command(flatten)]
        connection: Connection,
        /// Only messages from or to this number
        #[arg(long, value_name = "NUMBER")]
        address: Option<String>,
        /// Only this conversation
        #[arg(long, value_name = "ID")]
        conversation: Option<i64>,
        /// Incoming, outgoing or both
        #[arg(long, default_value = "all", value_parser = PossibleValuesParser::new(["in", "out", "all"]))]
        direction: String,
        /// Search the text
        #[arg(long, value_name = "TEXT")]
        search: Option<String>,
        /// Not older than this: 2026-09-29, 2026-09-29T08:00 or milliseconds
        #[arg(long, value_name = "TIME", value_parser = timestamp)]
        since: Option<i64>,
        /// Not newer than this
        #[arg(long, value_name = "TIME", value_parser = timestamp)]
        until: Option<i64>,
        /// Only unread messages
        #[arg(long)]
        unread: bool,
        /// Only this subscription id
        #[arg(long, value_name = "ID")]
        sim: Option<i64>,
        /// Number of messages
        #[arg(long, value_name = "N", default_value_t = 50, value_parser = positive)]
        limit: usize,
    },
    /// Send an SMS
    #[command(long_about = "Send an SMS. Without --text the text is read from standard input.")]
    Send {
        #[command(flatten)]
        connection: Connection,
        /// Phone number of the recipient
        #[arg(long, value_name = "NUMBER")]
        to: String,
        /// Text of the message
        #[arg(long)]
        text: Option<String>,
        /// Subscription id of the SIM
        #[arg(long, value_name = "ID")]
        sim: Option<i64>,
        /// Your own id; the same id is sent only once
        #[arg(long, value_name = "ID")]
        request_id: Option<String>,
        /// Wait until the message was sent or failed
        #[arg(long)]
        wait: bool,
        /// Longest time to wait with --wait
        #[arg(long, value_name = "SECONDS", default_value_t = 60.0)]
        wait_timeout: f64,
    },
    /// Delete a message or a conversation
    Delete {
        #[command(flatten)]
        connection: Connection,
        #[command(flatten)]
        target: DeleteTarget,
    },
    /// Mark a conversation as read
    #[command(
        name = "mark-read",
        long_about = "Mark all messages of a conversation as read. SMS Dock must be the \
                      default SMS app of the phone."
    )]
    MarkRead {
        #[command(flatten)]
        connection: Connection,
        /// Id of the conversation
        #[arg(long, value_name = "ID")]
        conversation: i64,
    },
    /// List the contacts of the phone
    Contacts {
        #[command(flatten)]
        connection: Connection,
        /// Search names and numbers
        #[arg(long, value_name = "TEXT")]
        search: Option<String>,
        /// Number of contacts
        #[arg(long, value_name = "N", default_value_t = 50, value_parser = positive)]
        limit: usize,
    },
    /// Print incoming SMS as they arrive
    #[command(long_about = "Print events as they arrive until Ctrl+C.")]
    Listen {
        #[command(flatten)]
        connection: Connection,
        /// Only SMS events of this number
        #[arg(long, value_name = "NUMBER")]
        address: Option<String>,
        /// Events to print; * means all
        #[arg(
            long,
            value_name = "EVENT",
            num_args = 1..,
            default_values = ["sms.received"],
            value_parser = PossibleValuesParser::new([
                "sms.received", "sms.queued", "sms.sent", "sms.delivered",
                "sms.failed", "sms.deleted", "conversations.changed", "*",
            ])
        )]
        events: Vec<String>,
        /// Stop after this many events
        #[arg(long, value_name = "N", value_parser = positive)]
        count: Option<usize>,
    },
}

/// Options of all commands that talk to the API.
#[derive(Debug, Args)]
struct Connection {
    /// wss:// URL shown by the app, e.g. wss://192.168.1.50:9443/ws
    url: String,
    /// SHA-256 fingerprint of the certificate as shown by the app
    /// (default: environment variable SMSDOCK_FINGERPRINT)
    #[arg(long, value_name = "HEX")]
    fingerprint: Option<String>,
    /// Do not check the certificate (for tests only)
    #[arg(long)]
    insecure: bool,
    /// Environment variable with the password
    #[arg(long, value_name = "NAME", default_value = PASSWORD_ENV)]
    password_env: String,
    /// Seconds to wait for a response
    #[arg(long, value_name = "SECONDS", default_value_t = 30.0)]
    timeout: f64,
    /// Reconnect attempts after a lost connection; -1 means without limit
    #[arg(
        long,
        value_name = "N",
        default_value_t = 5,
        allow_negative_numbers = true
    )]
    reconnect_attempts: i64,
    /// Print JSON instead of text
    #[arg(long)]
    json: bool,
}

/// What `delete` removes: exactly one of the two.
#[derive(Debug, Args)]
#[group(required = true, multiple = false)]
struct DeleteTarget {
    /// Id of the message
    #[arg(long, value_name = "ID")]
    message: Option<i64>,
    /// Id of the conversation
    #[arg(long, value_name = "ID")]
    conversation: Option<i64>,
}

/// Why a command ended without success.
#[derive(Debug)]
enum Fault {
    /// The command line cannot be carried out as given.
    Usage(String),
    /// The library reported an error.
    Client(Error),
    /// Standard output or input failed.
    Io(io::Error),
}

impl From<Error> for Fault {
    fn from(error: Error) -> Self {
        Fault::Client(error)
    }
}

impl From<io::Error> for Fault {
    fn from(error: io::Error) -> Self {
        Fault::Io(error)
    }
}

type Outcome = Result<u8, Fault>;

fn main() -> ExitCode {
    let cli = Cli::parse();
    let code = match run(cli.command) {
        Ok(code) => code,
        // Nobody reads the output any more, for example `smsdock … | head`.
        Err(Fault::Io(error)) if error.kind() == io::ErrorKind::BrokenPipe => EXIT_OK,
        Err(Fault::Usage(message)) => complain(&message, EXIT_USAGE),
        Err(Fault::Client(error)) => {
            let code = match &error {
                Error::Api { .. } => EXIT_API_ERROR,
                Error::InvalidArgument(_) => EXIT_USAGE,
                _ => EXIT_CONNECTION,
            };
            complain(&error.to_string(), code)
        }
        Err(Fault::Io(error)) => complain(&error.to_string(), EXIT_CONNECTION),
    };
    ExitCode::from(code)
}

fn complain(message: &str, code: u8) -> u8 {
    eprintln!("smsdock: {message}");
    code
}

fn run(command: Command) -> Outcome {
    match command {
        Command::Fingerprint { host, timeout } => fingerprint(&host, timeout),
        Command::Status { connection } => status(&connection),
        Command::Conversations {
            connection,
            search,
            unread,
            sim,
            limit,
        } => {
            let filter = ConversationFilter {
                q: search,
                unread,
                sim,
            };
            conversations(&connection, &filter, limit)
        }
        Command::Messages {
            connection,
            address,
            conversation,
            direction,
            search,
            since,
            until,
            unread,
            sim,
            limit,
        } => {
            let filter = MessageFilter {
                address,
                conversation_id: conversation,
                direction: match direction.as_str() {
                    "in" => Direction::In,
                    "out" => Direction::Out,
                    _ => Direction::All,
                },
                q: search,
                since,
                until,
                sim,
                unread,
            };
            messages(&connection, &filter, limit)
        }
        Command::Send {
            connection,
            to,
            text,
            sim,
            request_id,
            wait,
            wait_timeout,
        } => {
            let wait = wait.then_some(wait_timeout);
            send(&connection, &to, text, sim, request_id.as_deref(), wait)
        }
        Command::Delete { connection, target } => delete(&connection, &target),
        Command::MarkRead {
            connection,
            conversation,
        } => mark_read(&connection, conversation),
        Command::Contacts {
            connection,
            search,
            limit,
        } => contacts(&connection, search.as_deref(), limit),
        Command::Listen {
            connection,
            address,
            events,
            count,
        } => listen(&connection, address.as_deref(), &events, count),
    }
}

/// Connect and log in with the options of the command line.
fn client(connection: &Connection) -> Result<SmsDockClient, Fault> {
    let fingerprint = connection
        .fingerprint
        .clone()
        .or_else(|| std::env::var(FINGERPRINT_ENV).ok())
        .filter(|fingerprint| !fingerprint.is_empty());
    if !connection.insecure && fingerprint.is_none() {
        return Err(Fault::Usage(format!(
            "No certificate fingerprint. Pass --fingerprint or set {FINGERPRINT_ENV}. \
             'smsdock fingerprint HOST' prints the fingerprint of a server."
        )));
    }
    let attempts =
        match connection.reconnect_attempts {
            -1 => None,
            number => Some(u32::try_from(number).map_err(|_| {
                Fault::Usage("--reconnect-attempts cannot be less than -1".to_owned())
            })?),
        };
    let password = password(&connection.password_env)?;
    let mut builder = SmsDockClient::builder(&connection.url)
        .password(password)
        .insecure(connection.insecure)
        .timeout(seconds(connection.timeout)?)
        .reconnect_attempts(attempts);
    if let Some(fingerprint) = fingerprint {
        builder = builder.fingerprint(fingerprint);
    }
    Ok(builder.connect()?)
}

/// The password: from the environment, or asked for on a terminal.
fn password(variable: &str) -> Result<String, Fault> {
    if let Some(password) = std::env::var(variable).ok().filter(|text| !text.is_empty()) {
        return Ok(password);
    }
    if io::stdin().is_terminal() {
        let password = rpassword::prompt_password("SMS Dock password: ")?;
        if !password.is_empty() {
            return Ok(password);
        }
    }
    Err(Fault::Usage(format!(
        "No password. Set the environment variable {variable}."
    )))
}

fn seconds(value: f64) -> Result<Duration, Fault> {
    Duration::try_from_secs_f64(value).map_err(|_| {
        Fault::Client(Error::InvalidArgument(
            "Timeouts must be positive".to_owned(),
        ))
    })
}

fn fingerprint(host: &str, timeout: f64) -> Outcome {
    let (name, port) = host_and_port(host)
        .ok_or_else(|| Fault::Usage(format!("'{host}' is not HOST or HOST:PORT")))?;
    let fingerprint = fetch_fingerprint(&name, port, seconds(timeout)?)?;
    print_line(&fingerprint)?;
    eprintln!(
        "Compare this fingerprint with the Server screen of the app. Use it only if \
         both are the same."
    );
    Ok(EXIT_OK)
}

fn status(connection: &Connection) -> Outcome {
    let mut dock = client(connection)?;
    let status = dock.status()?;
    if connection.json {
        print_json(&status)?;
        return Ok(EXIT_OK);
    }
    let licence = if status.licence.licensed {
        "full licence".to_owned()
    } else if !status.licence.enforced {
        "not enforced".to_owned()
    } else {
        format!(
            "free version, {} of {} SMS sent today",
            status.licence.sent_today, status.licence.daily_limit
        )
    };
    let missing = status.permissions.missing();
    let permissions = if missing.is_empty() {
        "all granted".to_owned()
    } else {
        format!("missing: {}", missing.join(", "))
    };
    let app = format!(
        "{} {} (protocol {})",
        status.app.name, status.app.version, status.app.protocol_version
    );
    let lines = [
        ("App", app),
        ("Default SMS app", yes_no(status.default_sms_app).to_owned()),
        ("Delete mode", status.delete_mode.clone()),
        ("Licence", licence),
        ("Permissions", permissions),
    ];
    let mut text = String::new();
    for (label, value) in lines {
        text.push_str(&format!("{:<17}{value}\n", format!("{label}:")));
    }
    if !status.sims.is_empty() {
        text.push('\n');
        let rows = status
            .sims
            .iter()
            .map(|sim| {
                vec![
                    sim.subscription_id.to_string(),
                    sim.slot.to_string(),
                    sim.name.clone(),
                    sim.number.clone().unwrap_or_default(),
                    yes_no(sim.is_default).to_owned(),
                ]
            })
            .collect();
        text.push_str(&table(&["SIM", "SLOT", "NAME", "NUMBER", "DEFAULT"], rows));
    }
    print_text(&text)?;
    Ok(EXIT_OK)
}

fn conversations(connection: &Connection, filter: &ConversationFilter, limit: usize) -> Outcome {
    let mut dock = client(connection)?;
    let items = dock
        .iter_conversations(filter, page_size(limit), Some(limit))?
        .collect::<smsdock::Result<Vec<_>>>()?;
    if connection.json {
        print_json(&items)?;
        return Ok(EXIT_OK);
    }
    let rows = items
        .iter()
        .map(|item| {
            vec![
                item.id.to_string(),
                time(item.timestamp, false),
                item.address.clone(),
                item.contact.clone().unwrap_or_default(),
                item.message_count.to_string(),
                item.unread_count.to_string(),
                item.direction.clone(),
                one_line(&item.snippet),
            ]
        })
        .collect();
    let headers = [
        "ID", "TIME", "NUMBER", "NAME", "MSGS", "UNREAD", "DIR", "TEXT",
    ];
    print_text(&table(&headers, rows))?;
    Ok(EXIT_OK)
}

fn messages(connection: &Connection, filter: &MessageFilter, limit: usize) -> Outcome {
    let mut dock = client(connection)?;
    let items = dock
        .iter_messages(filter, page_size(limit), Some(limit))?
        .collect::<smsdock::Result<Vec<_>>>()?;
    if connection.json {
        print_json(&items)?;
        return Ok(EXIT_OK);
    }
    let rows = items
        .iter()
        .map(|item| {
            vec![
                item.id.map(|id| id.to_string()).unwrap_or_default(),
                time(item.timestamp, false),
                item.direction.clone(),
                item.address.clone(),
                item.contact.clone().unwrap_or_default(),
                item.status.clone(),
                one_line(&item.body),
            ]
        })
        .collect();
    let headers = ["ID", "TIME", "DIR", "NUMBER", "NAME", "STATUS", "TEXT"];
    print_text(&table(&headers, rows))?;
    Ok(EXIT_OK)
}

fn send(
    connection: &Connection,
    to: &str,
    text: Option<String>,
    sim: Option<i64>,
    request_id: Option<&str>,
    wait: Option<f64>,
) -> Outcome {
    let mut dock = client(connection)?;
    let text = match text {
        Some(text) => text,
        None => {
            if io::stdin().is_terminal() {
                return Err(Fault::Usage(
                    "No text. Pass --text or write it to standard input.".to_owned(),
                ));
            }
            let mut text = String::new();
            io::stdin().read_to_string(&mut text)?;
            if text.ends_with('\n') {
                text.pop();
            }
            text
        }
    };
    if text.is_empty() {
        return Err(Fault::Usage("The text is empty.".to_owned()));
    }
    let result = dock.send(to, &text, sim, request_id)?;
    // Also without --wait: a repeated request id tells the old outcome.
    let (status, to, parts, request_id, error_code, json) = match wait {
        Some(timeout) => {
            let entry = dock.wait_for_result(&result.request_id, Some(seconds(timeout)?))?;
            let json = to_json(&entry)?;
            (
                entry.status,
                entry.to,
                entry.parts,
                entry.request_id,
                entry.error_code,
                json,
            )
        }
        None => {
            let json = to_json(&result)?;
            (
                result.status,
                result.to,
                result.parts,
                result.request_id,
                None,
                json,
            )
        }
    };
    let failed = status == "failed";
    if connection.json {
        print_text(&json)?;
    } else if failed {
        let cause = error_code.map_or(String::new(), |code| format!("error code {code}, "));
        eprintln!("smsdock: Sending to {to} failed ({cause}requestId {request_id})");
    } else {
        print_line(&format!(
            "{}: {to}, {parts} part(s), requestId {request_id}",
            capitalize(&status)
        ))?;
    }
    Ok(if failed { EXIT_API_ERROR } else { EXIT_OK })
}

fn delete(connection: &Connection, target: &DeleteTarget) -> Outcome {
    let mut dock = client(connection)?;
    let result = match (target.message, target.conversation) {
        (Some(message), _) => dock.delete_message(message)?,
        (None, Some(conversation)) => dock.delete_conversation(conversation)?,
        (None, None) => {
            return Err(Fault::Usage(
                "one of --message and --conversation is required".to_owned(),
            ));
        }
    };
    if connection.json {
        print_json(&result)?;
        return Ok(EXIT_OK);
    }
    let place = if result.mode == "provider" {
        "removed from the phone"
    } else {
        "hidden in SMS Dock, still on the phone"
    };
    print_line(&format!("Deleted {} message(s): {place}", result.deleted))?;
    Ok(EXIT_OK)
}

fn mark_read(connection: &Connection, conversation: i64) -> Outcome {
    let mut dock = client(connection)?;
    let result = dock.mark_read(conversation)?;
    if connection.json {
        print_json(&result)?;
    } else {
        print_line(&format!("Marked conversation {conversation} as read"))?;
    }
    Ok(EXIT_OK)
}

fn contacts(connection: &Connection, search: Option<&str>, limit: usize) -> Outcome {
    let mut dock = client(connection)?;
    let items = dock
        .iter_contacts(search, page_size(limit), Some(limit))?
        .collect::<smsdock::Result<Vec<_>>>()?;
    if connection.json {
        print_json(&items)?;
        return Ok(EXIT_OK);
    }
    let mut rows = Vec::new();
    for contact in &items {
        if contact.numbers.is_empty() {
            rows.push(vec![contact.name.clone(), String::new(), String::new()]);
        }
        for number in &contact.numbers {
            rows.push(vec![
                contact.name.clone(),
                number.number.clone(),
                number.label.clone().unwrap_or_default(),
            ]);
        }
    }
    print_text(&table(&["NAME", "NUMBER", "LABEL"], rows))?;
    Ok(EXIT_OK)
}

fn listen(
    connection: &Connection,
    address: Option<&str>,
    events: &[String],
    count: Option<usize>,
) -> Outcome {
    let mut dock = client(connection)?;
    dock.set_on_reconnect(report_reconnect);
    let names: Vec<&str> = events.iter().map(String::as_str).collect();
    dock.subscribe(&names, address)?;
    if !connection.json {
        eprintln!("Listening for {}. Press Ctrl+C to stop.", events.join(", "));
    }
    let mut seen = 0;
    while let Some(event) = dock.next_event(None)? {
        if connection.json {
            print_line(&python_json(&event)?)?;
        } else {
            print_line(&describe(&event, now()))?;
        }
        seen += 1;
        if count.is_some_and(|count| seen >= count) {
            break;
        }
    }
    Ok(EXIT_OK)
}

/// Tell on standard error what a lost connection may have cost.
fn report_reconnect(dock: &mut SmsDockClient) -> smsdock::Result<()> {
    let lost = match dock.missed_events() {
        _ if dock.server_restarted() => "the app was restarted, events may be missing".to_owned(),
        Some(missed) if missed > 0 => format!("{missed} event(s) of the phone were not received"),
        _ => "no event was missed".to_owned(),
    };
    eprintln!("smsdock: reconnected; {lost}");
    Ok(())
}

/// Return one event as a line of text. `now` is the time in milliseconds.
fn describe(event: &Event, now: i64) -> String {
    let empty = serde_json::Map::new();
    let data = event.data.as_object().unwrap_or(&empty);
    let message = data
        .get("message")
        .and_then(Value::as_object)
        .unwrap_or(&empty);
    if event.event == "sms.received" {
        let mut sender = shown(message.get("address"));
        if let Some(contact) = message.get("contact").filter(|value| truthy(value)) {
            sender = format!("{sender} ({})", shown(Some(contact)));
        }
        let moment = message.get("timestamp").and_then(Value::as_i64);
        return format!(
            "{}  {sender}: {}",
            moment.map_or(String::new(), |moment| time(moment, true)),
            shown(message.get("body"))
        );
    }
    let now = time(now, true);
    if event.event == "sms.sent" && !message.is_empty() {
        return format!(
            "{now}  sent to {}: {}",
            shown(message.get("address")),
            shown(message.get("body"))
        );
    }
    let details: Vec<String> = data
        .iter()
        .filter(|(_, value)| !value.is_object() && !value.is_array())
        .map(|(key, value)| format!("{key}={}", shown(Some(value))))
        .collect();
    format!("{now}  {} {}", event.event, details.join(" "))
        .trim_end()
        .to_owned()
}

/// A JSON value inside a line of text; a missing one reads `None`.
fn shown(value: Option<&Value>) -> String {
    match value {
        None | Some(Value::Null) => "None".to_owned(),
        Some(Value::String(text)) => text.clone(),
        Some(Value::Bool(true)) => "True".to_owned(),
        Some(Value::Bool(false)) => "False".to_owned(),
        Some(other) => other.to_string(),
    }
}

fn truthy(value: &Value) -> bool {
    match value {
        Value::Null => false,
        Value::Bool(flag) => *flag,
        Value::String(text) => !text.is_empty(),
        Value::Number(number) => number.as_f64() != Some(0.0),
        Value::Array(items) => !items.is_empty(),
        Value::Object(fields) => !fields.is_empty(),
    }
}

/// Split `HOST`, `HOST:PORT`, `[IPv6]:PORT` or a `wss://` URL.
fn host_and_port(text: &str) -> Option<(String, u16)> {
    let mut value = text.trim();
    if let Some((_, rest)) = value.split_once("://") {
        value = rest;
    }
    let end = value.find(['/', '?', '#']).unwrap_or(value.len());
    let mut authority = &value[..end];
    if let Some((_, host)) = authority.rsplit_once('@') {
        authority = host;
    }
    let (host, port) = if let Some(rest) = authority.strip_prefix('[') {
        let (host, tail) = rest.split_once(']')?;
        match tail.strip_prefix(':') {
            Some(port) => (host, port),
            None if tail.is_empty() => (host, ""),
            None => return None,
        }
    } else if authority.matches(':').count() > 1 {
        // A bare IPv6 address.
        (authority, "")
    } else {
        authority.split_once(':').unwrap_or((authority, ""))
    };
    if host.is_empty() {
        return None;
    }
    let port = if port.is_empty() {
        DEFAULT_API_PORT
    } else if port.bytes().all(|byte| byte.is_ascii_digit()) {
        port.parse().ok()?
    } else {
        return None;
    };
    Some((host.to_ascii_lowercase(), port))
}

fn positive(text: &str) -> Result<usize, String> {
    match text.parse::<usize>() {
        Ok(value) if value >= 1 => Ok(value),
        _ => Err("must be a number from 1".to_owned()),
    }
}

/// Read milliseconds since the epoch or an ISO 8601 date (local time).
fn timestamp(text: &str) -> Result<i64, String> {
    let help = || TIME_HELP.to_owned();
    if !text.is_empty() && text.bytes().all(|byte| byte.is_ascii_digit()) {
        return text.parse().map_err(|_| help());
    }
    if let Ok(date) = NaiveDate::parse_from_str(text, "%Y-%m-%d") {
        return local_millis(date.and_hms_opt(0, 0, 0).ok_or_else(help)?).ok_or_else(help);
    }
    let text = text.replacen(' ', "T", 1);
    for format in ["%Y-%m-%dT%H:%M:%S%.f", "%Y-%m-%dT%H:%M"] {
        if let Ok(moment) = NaiveDateTime::parse_from_str(&text, format) {
            return local_millis(moment).ok_or_else(help);
        }
    }
    let zoned = text.strip_suffix('Z').map(|start| format!("{start}+00:00"));
    let text = zoned.as_deref().unwrap_or(&text);
    for format in ["%Y-%m-%dT%H:%M:%S%.f%:z", "%Y-%m-%dT%H:%M%:z"] {
        if let Ok(moment) = DateTime::parse_from_str(text, format) {
            return Ok(moment.timestamp_millis());
        }
    }
    Err(help())
}

fn local_millis(moment: NaiveDateTime) -> Option<i64> {
    Local
        .from_local_datetime(&moment)
        .earliest()
        .map(|moment| moment.timestamp_millis())
}

/// Milliseconds since the epoch as local time.
fn time(milliseconds: i64, seconds: bool) -> String {
    let format = if seconds {
        "%Y-%m-%d %H:%M:%S"
    } else {
        "%Y-%m-%d %H:%M"
    };
    match Local.timestamp_millis_opt(milliseconds).single() {
        Some(moment) => moment.format(format).to_string(),
        None => milliseconds.to_string(),
    }
}

fn now() -> i64 {
    Local::now().timestamp_millis()
}

/// A text on one line, cut to the width of a table cell.
fn one_line(text: &str) -> String {
    let line = text.split_whitespace().collect::<Vec<_>>().join(" ");
    if line.chars().count() > TEXT_WIDTH {
        let start: String = line.chars().take(TEXT_WIDTH - 1).collect();
        return format!("{start}…");
    }
    line
}

fn yes_no(value: bool) -> &'static str {
    if value {
        "yes"
    } else {
        "no"
    }
}

fn capitalize(text: &str) -> String {
    let mut characters = text.chars();
    match characters.next() {
        Some(first) => {
            first.to_uppercase().collect::<String>() + &characters.as_str().to_lowercase()
        }
        None => String::new(),
    }
}

fn page_size(limit: usize) -> u32 {
    u32::try_from(limit).map_or(MAX_PAGE_SIZE, |limit| limit.min(MAX_PAGE_SIZE))
}

/// Lay out rows in columns; every line ends with a line feed.
fn table(headers: &[&str], rows: Vec<Vec<String>>) -> String {
    if rows.is_empty() {
        return "Nothing found.\n".to_owned();
    }
    let width = |text: &str| text.chars().count();
    let widths: Vec<usize> = headers
        .iter()
        .enumerate()
        .map(|(index, header)| {
            rows.iter()
                .map(|row| width(&row[index]))
                .fold(width(header), usize::max)
        })
        .collect();
    let header: Vec<String> = headers.iter().map(|header| (*header).to_owned()).collect();
    let mut text = String::new();
    for row in std::iter::once(&header).chain(rows.iter()) {
        let cells: Vec<String> = row
            .iter()
            .zip(&widths)
            .map(|(cell, size)| format!("{cell}{}", " ".repeat(size - width(cell))))
            .collect();
        text.push_str(cells.join("  ").trim_end());
        text.push('\n');
    }
    text
}

fn to_json<T: Serialize>(value: &T) -> Result<String, Fault> {
    let mut text = serde_json::to_string_pretty(value).map_err(io::Error::from)?;
    text.push('\n');
    Ok(text)
}

fn print_json<T: Serialize>(value: &T) -> Result<(), Fault> {
    print_text(&to_json(value)?)
}

/// One line of JSON with a space after `,` and `:`, as Python prints it.
fn python_json<T: Serialize>(value: &T) -> Result<String, Fault> {
    let mut buffer = Vec::new();
    let mut serializer = serde_json::Serializer::with_formatter(&mut buffer, Spaced);
    value.serialize(&mut serializer).map_err(io::Error::from)?;
    String::from_utf8(buffer).map_err(|error| Fault::Io(io::Error::other(error)))
}

/// JSON on one line with `", "` and `": "` as separators.
struct Spaced;

impl serde_json::ser::Formatter for Spaced {
    fn begin_array_value<W>(&mut self, writer: &mut W, first: bool) -> io::Result<()>
    where
        W: ?Sized + Write,
    {
        if first {
            Ok(())
        } else {
            writer.write_all(b", ")
        }
    }

    fn begin_object_key<W>(&mut self, writer: &mut W, first: bool) -> io::Result<()>
    where
        W: ?Sized + Write,
    {
        if first {
            Ok(())
        } else {
            writer.write_all(b", ")
        }
    }

    fn begin_object_value<W>(&mut self, writer: &mut W) -> io::Result<()>
    where
        W: ?Sized + Write,
    {
        writer.write_all(b": ")
    }
}

fn print_line(text: &str) -> Result<(), Fault> {
    print_text(&format!("{text}\n"))
}

/// Write to standard output at once, so that a reader sees whole lines.
fn print_text(text: &str) -> Result<(), Fault> {
    let mut output = io::stdout().lock();
    output.write_all(text.as_bytes())?;
    output.flush()?;
    Ok(())
}

#[cfg(test)]
mod tests {
    use clap::CommandFactory;
    use serde_json::json;

    use super::*;

    const URL: &str = "wss://192.168.1.50:9443/ws";

    fn event(value: Value) -> Event {
        serde_json::from_value(value).unwrap()
    }

    fn parse(arguments: &[&str]) -> Result<Cli, clap::Error> {
        Cli::try_parse_from(std::iter::once("smsdock").chain(arguments.iter().copied()))
    }

    #[test]
    fn the_command_line_is_consistent() {
        Cli::command().debug_assert();
    }

    #[test]
    fn parser_reads_all_flags() {
        let cli = parse(&[
            "messages",
            URL,
            "--fingerprint",
            "AB",
            "--address",
            "+4369912345678",
            "--direction",
            "in",
            "--search",
            "invoice",
            "--limit",
            "10",
            "--unread",
            "--sim",
            "2",
            "--json",
            "--timeout",
            "7",
            "--since",
            "1790672531000",
            "--until",
            "2026-09-29T08:00",
            "--conversation",
            "179",
            "--reconnect-attempts",
            "2",
        ])
        .unwrap();
        let Command::Messages {
            connection,
            address,
            conversation,
            direction,
            search,
            since,
            until,
            unread,
            sim,
            limit,
        } = cli.command
        else {
            panic!("not the messages command");
        };
        assert_eq!(connection.url, URL);
        assert_eq!(connection.fingerprint.as_deref(), Some("AB"));
        assert_eq!(address.as_deref(), Some("+4369912345678"));
        assert_eq!(
            (direction.as_str(), search.as_deref()),
            ("in", Some("invoice"))
        );
        assert_eq!((limit, unread, sim), (10, true, Some(2)));
        assert!(connection.json && !connection.insecure);
        assert_eq!(connection.timeout, 7.0);
        assert_eq!(since, Some(1_790_672_531_000));
        assert!(until.unwrap() > 1_700_000_000_000);
        assert_eq!(conversation, Some(179));
        assert_eq!(connection.reconnect_attempts, 2);
    }

    #[test]
    fn parser_defaults() {
        let Command::Messages {
            connection,
            direction,
            limit,
            unread,
            ..
        } = parse(&["messages", URL]).unwrap().command
        else {
            panic!("not the messages command");
        };
        assert_eq!((direction.as_str(), limit, unread), ("all", 50, false));
        assert_eq!(connection.timeout, 30.0);
        assert_eq!(connection.reconnect_attempts, 5);
        assert!(!connection.json && !connection.insecure);
        assert_eq!(connection.fingerprint, None);
        assert_eq!(connection.password_env, "SMSDOCK_PASSWORD");
        let Command::Listen {
            events,
            address,
            count,
            ..
        } = parse(&["listen", URL]).unwrap().command
        else {
            panic!("not the listen command");
        };
        assert_eq!(events, ["sms.received"]);
        assert_eq!((address, count), (None, None));
        let Command::Send { text, wait, .. } =
            parse(&["send", URL, "--to", "+43123"]).unwrap().command
        else {
            panic!("not the send command");
        };
        assert_eq!((text, wait), (None, false));
        let Command::Listen { events, .. } = parse(&[
            "listen",
            URL,
            "--events",
            "sms.sent",
            "*",
            "--reconnect-attempts",
            "-1",
        ])
        .unwrap()
        .command
        else {
            panic!("not the listen command");
        };
        assert_eq!(events, ["sms.sent", "*"]);
    }

    #[test]
    fn usage_errors() {
        let cases: [&[&str]; 15] = [
            &[],
            &["unknown"],
            &["status"],
            &["status", URL, "--password", "secret123"],
            &["messages", URL, "--direction", "sideways"],
            &["messages", URL, "--limit", "0"],
            &["messages", URL, "--limit", "many"],
            &["messages", URL, "--since", "yesterday"],
            &["send", URL, "--text", "no recipient"],
            &["delete", URL],
            &["delete", URL, "--message", "1", "--conversation", "2"],
            &["mark-read", URL],
            &["mark-read", URL, "--conversation", "first"],
            &["listen", URL, "--events", "sms.unknown"],
            &["fingerprint"],
        ];
        for arguments in cases {
            let error = parse(arguments).unwrap_err();
            assert_eq!(error.exit_code(), 2, "{arguments:?}");
        }
    }

    #[test]
    fn help_names_the_commands_but_no_password_option() {
        let help = Cli::command().render_long_help().to_string();
        for command in [
            "fingerprint",
            "status",
            "conversations",
            "messages",
            "send",
            "delete",
            "mark-read",
            "contacts",
            "listen",
        ] {
            assert!(help.contains(command), "{command}");
        }
        assert!(help.contains("SMSDOCK_PASSWORD"));
        let error = parse(&["send", "--help"]).unwrap_err();
        assert_eq!(error.exit_code(), 0);
        assert!(!error.to_string().contains("--password "));
    }

    #[test]
    fn splits_host_and_port() {
        let cases = [
            ("192.168.1.50", ("192.168.1.50", 9443)),
            ("192.168.1.50:9555", ("192.168.1.50", 9555)),
            ("phone.local:9443", ("phone.local", 9443)),
            ("wss://192.168.1.50:9555/ws", ("192.168.1.50", 9555)),
            ("wss://192.168.1.50/ws", ("192.168.1.50", 9443)),
            ("[fe80::1]:9555", ("fe80::1", 9555)),
            ("fe80::1", ("fe80::1", 9443)),
        ];
        for (text, (host, port)) in cases {
            assert_eq!(host_and_port(text), Some((host.to_owned(), port)), "{text}");
        }
        for text in ["", ":9443", "192.168.1.50:port", "host:99999", "[fe80::1"] {
            assert_eq!(host_and_port(text), None, "{text}");
        }
    }

    #[test]
    fn reads_times() {
        assert_eq!(timestamp("1790672531000"), Ok(1_790_672_531_000));
        let midnight = timestamp("2026-09-29").unwrap();
        let morning = timestamp("2026-09-29T08:00").unwrap();
        assert_eq!(morning - midnight, 8 * 3_600_000);
        assert_eq!(timestamp("2026-09-29 08:00:00"), Ok(morning));
        assert_eq!(timestamp("2026-09-29T08:00:00.500"), Ok(morning + 500));
        assert_eq!(
            timestamp("2026-09-29T08:00:00+00:00"),
            Ok(1_790_668_800_000)
        );
        assert_eq!(timestamp("2026-09-29T10:00+02:00"), Ok(1_790_668_800_000));
        assert_eq!(timestamp("2026-09-29T08:00:00Z"), Ok(1_790_668_800_000));
        for text in ["yesterday", "", "2026-13-01", "-5"] {
            assert!(timestamp(text).is_err(), "{text}");
        }
        assert_eq!(time(midnight, false), "2026-09-29 00:00");
        assert_eq!(time(morning + 5000, true), "2026-09-29 08:00:05");
    }

    #[test]
    fn counts_from_one() {
        assert_eq!(positive("220"), Ok(220));
        for text in ["0", "-1", "many", ""] {
            assert!(positive(text).is_err());
        }
        assert_eq!((page_size(10), page_size(220)), (10, 200));
    }

    #[test]
    fn long_text_is_cut_in_tables() {
        let long = format!("First line\nsecond  line {}", "x".repeat(100));
        let line = one_line(&long);
        assert!(line.starts_with("First line second line xxx"));
        assert!(line.ends_with('…'));
        assert_eq!(line.chars().count(), 60);
        assert_eq!(one_line(" short\ttext "), "short text");
        assert_eq!(one_line(&"ü".repeat(60)), "ü".repeat(60));
    }

    #[test]
    fn tables() {
        assert_eq!(table(&["A"], Vec::new()), "Nothing found.\n");
        let rows = vec![
            vec!["1".to_owned(), "Grüße".to_owned(), String::new()],
            vec!["200".to_owned(), "x".to_owned(), "yes".to_owned()],
        ];
        assert_eq!(
            table(&["ID", "TEXT", "DEFAULT"], rows),
            "ID   TEXT   DEFAULT\n1    Grüße\n200  x      yes\n"
        );
    }

    #[test]
    fn words() {
        assert_eq!(capitalize("queued"), "Queued");
        assert_eq!(capitalize("SENT"), "Sent");
        assert_eq!(capitalize(""), "");
        assert_eq!((yes_no(true), yes_no(false)), ("yes", "no"));
    }

    #[test]
    fn describes_events() {
        let received = event(json!({
            "event": "sms.received", "seq": 1,
            "data": {"message": {"address": "+4369912345678", "contact": "Anna Example",
                                 "body": "STATUS", "timestamp": 1_790_672_531_000_i64}},
        }));
        let line = describe(&received, 0);
        assert!(
            line.ends_with("  +4369912345678 (Anna Example): STATUS"),
            "{line}"
        );
        assert!(line.starts_with(&time(1_790_672_531_000, true)));

        let unknown = event(json!({
            "event": "sms.received", "seq": 2,
            "data": {"message": {"address": "A1", "contact": null, "body": "Your bill",
                                 "timestamp": 1_790_672_531_000_i64}},
        }));
        assert!(describe(&unknown, 0).ends_with("  A1: Your bill"));

        let delivered = event(json!({
            "event": "sms.delivered", "seq": 3,
            "data": {"requestId": "r1", "id": 7, "conversationId": 171,
                     "to": "+4369087654321"},
        }));
        assert_eq!(
            describe(&delivered, 1_790_672_531_000),
            format!(
                "{}  sms.delivered requestId=r1 id=7 conversationId=171 to=+4369087654321",
                time(1_790_672_531_000, true)
            )
        );

        let failed = event(json!({
            "event": "sms.failed", "seq": 4,
            "data": {"requestId": "r1", "id": null, "errorCode": 1},
        }));
        assert!(describe(&failed, 0).ends_with("  sms.failed requestId=r1 id=None errorCode=1"));

        let sent = event(json!({
            "event": "sms.sent", "seq": 5,
            "data": {"requestId": "r1", "to": "+43123",
                     "message": {"address": "+43123", "body": "Hello"}},
        }));
        assert!(describe(&sent, 0).ends_with("  sent to +43123: Hello"));

        let changed = event(json!({"event": "conversations.changed", "seq": 6, "data": {}}));
        assert!(describe(&changed, 0).ends_with("  conversations.changed"));
    }

    #[test]
    fn json_on_one_line_like_python() {
        let value = json!({"event": "sms.deleted", "seq": 3,
                           "data": {"id": null, "list": [1, 2], "text": "Grüße"}});
        assert_eq!(
            python_json(&value).unwrap(),
            r#"{"event": "sms.deleted", "seq": 3, "data": {"id": null, "list": [1, 2], "text": "Grüße"}}"#
        );
        assert_eq!(
            python_json(&json!({"data": {}, "items": []})).unwrap(),
            r#"{"data": {}, "items": []}"#
        );
    }
}
