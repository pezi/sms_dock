//! Tests of the command line `smsdock`: the built binary against the fake
//! server.

mod common;

use std::io::Write;
use std::process::{Command, Stdio};
use std::thread;
use std::time::Duration;

use common::{other_certificate, wait_until, FakeServer, Options, ANNA, BEN, PASSWORD};
use serde_json::{json, Value};

const URL: &str = "wss://192.168.1.50:9443/ws";
const WARNING: &str = "smsdock: WARNING:";

fn fingerprint() -> String {
    vec!["AB"; 32].join(":")
}

/// What a run of the command line left behind.
struct Run {
    code: i32,
    out: String,
    err: String,
}

impl Run {
    fn json(&self) -> Value {
        serde_json::from_str(&self.out).expect("JSON on standard output")
    }

    fn lines(&self) -> Vec<&str> {
        self.out.lines().collect()
    }
}

/// One run of the binary. Every run starts with the password set and no
/// fingerprint in the environment.
struct Cli {
    arguments: Vec<String>,
    environment: Vec<(String, String)>,
    input: Option<String>,
}

impl Cli {
    fn new(arguments: &[&str]) -> Self {
        Self {
            arguments: arguments.iter().map(|text| (*text).to_owned()).collect(),
            environment: vec![("SMSDOCK_PASSWORD".to_owned(), PASSWORD.to_owned())],
            input: None,
        }
    }

    /// A command for this server: URL, fingerprint and a short timeout.
    fn to(server: &FakeServer, command: &str, options: &[&str]) -> Self {
        let url = server.url();
        let mut arguments = vec![command, &url, "--fingerprint", &server.fingerprint];
        arguments.extend(["--timeout", "5"]);
        arguments.extend(options);
        Self::new(&arguments)
    }

    fn env(mut self, name: &str, value: &str) -> Self {
        self.environment.retain(|(known, _)| known != name);
        self.environment.push((name.to_owned(), value.to_owned()));
        self
    }

    fn without_password(mut self) -> Self {
        self.environment
            .retain(|(name, _)| name != "SMSDOCK_PASSWORD");
        self
    }

    fn input(mut self, text: &str) -> Self {
        self.input = Some(text.to_owned());
        self
    }

    fn run(self) -> Run {
        let mut command = Command::new(env!("CARGO_BIN_EXE_smsdock"));
        command
            .args(&self.arguments)
            .env_remove("SMSDOCK_PASSWORD")
            .env_remove("SMSDOCK_FINGERPRINT")
            .envs(self.environment)
            .stdin(if self.input.is_some() {
                Stdio::piped()
            } else {
                Stdio::null()
            })
            .stdout(Stdio::piped())
            .stderr(Stdio::piped());
        let mut child = command.spawn().expect("the smsdock binary");
        if let Some(text) = self.input {
            let mut stdin = child.stdin.take().expect("standard input");
            stdin.write_all(text.as_bytes()).expect("written input");
        }
        let output = child.wait_with_output().expect("the end of smsdock");
        let err = String::from_utf8(output.stderr).expect("UTF-8 errors");
        Run {
            code: output.status.code().expect("an exit code"),
            out: String::from_utf8(output.stdout).expect("UTF-8 output"),
            err,
        }
    }
}

fn words(line: &str) -> Vec<&str> {
    line.split_whitespace().collect()
}

// ----------------------------------------------------------------------
// Arguments
// ----------------------------------------------------------------------

#[test]
fn usage_errors() {
    let cases: [&[&str]; 9] = [
        &[],
        &["unknown"],
        &["status"],
        &["messages", URL, "--direction", "sideways"],
        &["messages", URL, "--limit", "0"],
        &["messages", URL, "--since", "yesterday"],
        &["send", URL, "--text", "no recipient"],
        &["delete", URL, "--message", "1", "--conversation", "2"],
        &["listen", URL, "--events", "sms.unknown"],
    ];
    for arguments in cases {
        let run = Cli::new(arguments).run();
        assert_eq!(run.code, 2, "{arguments:?}");
        assert_eq!(run.out, "", "{arguments:?}");
        let explained = run.err.contains("Usage:") || run.err.starts_with("error:");
        assert!(explained, "{arguments:?}: {}", run.err);
    }
}

#[test]
fn help() {
    let run = Cli::new(&["--help"]).run();
    assert_eq!(run.code, 0);
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
        assert!(run.out.contains(command), "{command}");
    }
    assert!(run.out.contains("SMSDOCK_PASSWORD"));
    let run = Cli::new(&["send", "--help"]).run();
    assert_eq!(run.code, 0);
    assert!(!run.out.contains("--password "));
    assert!(run.out.contains("--request-id"));
}

#[test]
fn the_password_is_no_argument() {
    let run = Cli::new(&[
        "status",
        URL,
        "--fingerprint",
        &fingerprint(),
        "--password",
        PASSWORD,
    ])
    .run();
    assert_eq!(run.code, 2);
    assert!(run.err.contains("unexpected argument"));
}

#[test]
fn refuses_other_urls() {
    for url in [
        "ws://192.168.1.50:9443/ws",
        "https://192.168.1.50:8443/",
        "192.168.1.50",
    ] {
        let run = Cli::new(&["status", url, "--fingerprint", &fingerprint()]).run();
        assert_eq!((run.code, run.out.as_str()), (2, ""), "{url}");
        assert!(run.err.contains("wss://"), "{url}");
    }
}

#[test]
fn needs_a_fingerprint() {
    let run = Cli::new(&["status", URL]).run();
    assert_eq!(run.code, 2);
    assert!(run.err.contains("--fingerprint") && run.err.contains("SMSDOCK_FINGERPRINT"));
    let run = Cli::new(&["status", URL, "--fingerprint", "AB:CD"]).run();
    assert_eq!(run.code, 2);
    assert!(run.err.contains("SHA-256"));
}

#[test]
fn needs_a_password() {
    // Standard input is no terminal here, so nobody can be asked.
    let run = Cli::new(&["status", URL, "--fingerprint", &fingerprint()])
        .without_password()
        .run();
    assert_eq!(run.code, 2);
    assert_eq!(
        run.err,
        "smsdock: No password. Set the environment variable SMSDOCK_PASSWORD.\n"
    );
}

#[test]
fn refuses_impossible_options() {
    let cases: [&[&str]; 2] = [&["--reconnect-attempts", "-2"], &["--timeout", "0"]];
    for options in cases {
        let mut arguments = vec!["status", URL, "--fingerprint"];
        let pin = fingerprint();
        arguments.push(&pin);
        arguments.extend(options);
        let run = Cli::new(&arguments).run();
        assert_eq!((run.code, run.out.as_str()), (2, ""), "{options:?}");
        assert!(run.err.starts_with("smsdock: "), "{}", run.err);
    }
}

#[test]
fn other_password_variable() {
    let server = FakeServer::new();
    let run = Cli::to(&server, "status", &["--password-env", "OFFICE_PHONE"])
        .env("SMSDOCK_PASSWORD", "wrong-password")
        .env("OFFICE_PHONE", PASSWORD)
        .run();
    assert_eq!(run.code, 0, "{}", run.err);
}

// ----------------------------------------------------------------------
// Commands
// ----------------------------------------------------------------------

#[test]
fn fingerprint_command() {
    let server = FakeServer::new();
    let run = Cli::new(&["fingerprint", &format!("127.0.0.1:{}", server.port)]).run();
    assert_eq!(run.code, 0, "{}", run.err);
    assert_eq!(run.out, format!("{}\n", server.fingerprint));
    assert!(run.err.contains("Compare"));
    let run = Cli::new(&["fingerprint", &server.url()]).run();
    assert_eq!(
        (run.code, run.out),
        (0, format!("{}\n", server.fingerprint))
    );
    assert!(server.requests().is_empty());
    let run = Cli::new(&["fingerprint", "192.168.1.50:port"]).run();
    assert_eq!(run.code, 2);
    assert_eq!(
        run.err,
        "smsdock: '192.168.1.50:port' is not HOST or HOST:PORT\n"
    );
}

#[test]
fn fingerprint_of_nothing() {
    let mut server = FakeServer::new();
    server.stop();
    let address = format!("127.0.0.1:{}", server.port);
    let run = Cli::new(&["fingerprint", &address, "--timeout", "2"]).run();
    assert_eq!((run.code, run.out.as_str()), (3, ""));
    assert!(run.err.starts_with("smsdock: "));
}

#[test]
fn status() {
    let server = FakeServer::new();
    let run = Cli::to(&server, "status", &[]).run();
    assert_eq!(run.code, 0, "{}", run.err);
    assert_eq!(
        run.out,
        format!(
            "App:             SMS Dock 1.0.0 (protocol 1)\n\
             Default SMS app: no\n\
             Delete mode:     hidden\n\
             Licence:         not enforced\n\
             Permissions:     all granted\n\
             \n\
             SIM  SLOT  NAME  NUMBER          DEFAULT\n\
             1    0     Play                  yes\n\
             2    1     Work  {BEN}  no\n"
        )
    );
    assert_eq!(run.err, "");
    assert!(!format!("{}{}", run.out, run.err).contains(PASSWORD));

    let run = Cli::to(&server, "status", &["--json"]).run();
    assert_eq!(run.code, 0);
    let status = run.json();
    assert_eq!(status["app"]["protocolVersion"], 1);
    assert_eq!(status["sims"].as_array().unwrap().len(), 2);
    assert_eq!(status["licence"]["remaining"], Value::Null);
    assert!(run
        .out
        .starts_with("{\n  \"app\": {\n    \"name\": \"SMS Dock\","));
}

#[test]
fn conversations() {
    let server = FakeServer::new();
    let run = Cli::to(&server, "conversations", &[]).run();
    assert_eq!(run.code, 0, "{}", run.err);
    let lines = run.lines();
    assert_eq!(
        words(lines[0]),
        ["ID", "TIME", "NUMBER", "NAME", "MSGS", "UNREAD", "DIR", "TEXT"]
    );
    assert_eq!(lines.len(), 4);
    assert!(lines[1].contains(ANNA) && lines[1].contains("Anna Example"));
    assert!(
        lines[1].ends_with("  25    3       in   Anna 24"),
        "{}",
        lines[1]
    );
    assert!(lines[3].contains("A1"));

    let options = ["--json", "--unread", "--search", "bill", "--limit", "5"];
    let run = Cli::to(&server, "conversations", &options).run();
    assert_eq!(run.code, 0);
    assert_eq!(run.json()[0]["address"], "A1");
    assert_eq!(run.json().as_array().unwrap().len(), 1);
    let run = Cli::to(&server, "conversations", &["--sim", "2", "--json"]).run();
    assert_eq!(run.json()[0]["address"], BEN);
}

#[test]
fn last_ten_incoming_messages() {
    let server = FakeServer::new();
    let run = Cli::to(&server, "messages", &["--direction", "in", "--limit", "10"]).run();
    assert_eq!(run.code, 0, "{}", run.err);
    let lines = run.lines();
    assert_eq!(
        words(lines[0]),
        ["ID", "TIME", "DIR", "NUMBER", "NAME", "STATUS", "TEXT"]
    );
    assert_eq!(lines.len(), 11);
    assert!(lines[1..].iter().all(|line| line.contains(" in ")));
    assert!(lines[1].ends_with("received  Anna 24"));
    assert!(lines[10].ends_with("received  Anna 6"));
}

#[test]
fn messages_json_and_filters() {
    let server = FakeServer::new();
    let options = [
        "--json",
        "--address",
        BEN,
        "--search",
        "invoice",
        "--direction",
        "out",
        "--sim",
        "2",
    ];
    let run = Cli::to(&server, "messages", &options).run();
    assert_eq!(run.code, 0, "{}", run.err);
    let messages = run.json();
    assert_eq!(messages.as_array().unwrap().len(), 1);
    assert_eq!(messages[0]["body"], "Your invoice is attached");
    assert_eq!(messages[0]["sentTimestamp"], Value::Null);
    let request = server.last_request("messages.list");
    assert_eq!(request["address"], BEN);
    assert_eq!(request["q"], "invoice");
    assert_eq!(request["direction"], "out");
    assert_eq!(request["sim"], 2);

    let run = Cli::to(&server, "messages", &["--json", "--unread"]).run();
    assert_eq!(run.json().as_array().unwrap().len(), 4);
    let run = Cli::to(&server, "messages", &["--json", "--since", "1790672553000"]).run();
    let bodies: Vec<Value> = run
        .json()
        .as_array()
        .unwrap()
        .iter()
        .map(|m| m["body"].clone())
        .collect();
    assert_eq!(bodies, ["Anna 24", "Anna 23", "Anna 22"]);
}

#[test]
fn messages_limit_over_one_page() {
    let server = FakeServer::new();
    for index in 0..230 {
        let body = format!("More {index}");
        server
            .state()
            .add_message(ANNA, &body, "in", None, true, Some(1), None);
    }
    let run = Cli::to(&server, "messages", &["--json", "--limit", "220"]).run();
    assert_eq!(run.code, 0, "{}", run.err);
    assert_eq!(run.json().as_array().unwrap().len(), 220);
    let limits: Vec<Value> = server
        .state()
        .requests_of("messages.list")
        .iter()
        .map(|request| request["limit"].clone())
        .collect();
    assert_eq!(limits, [json!(200), json!(20)]);
}

#[test]
fn nothing_found() {
    let server = FakeServer::new();
    let run = Cli::to(&server, "messages", &["--search", "no such text"]).run();
    assert_eq!((run.code, run.out.as_str()), (0, "Nothing found.\n"));
    let run = Cli::to(&server, "messages", &["--search", "no such text", "--json"]).run();
    assert_eq!((run.code, run.out.as_str()), (0, "[]\n"));
}

#[test]
fn long_text_is_cut_in_tables() {
    let server = FakeServer::new();
    let body = format!("First line\nsecond line {}", "x".repeat(100));
    server
        .state()
        .add_message(ANNA, &body, "in", None, true, Some(1), None);
    let run = Cli::to(&server, "messages", &["--limit", "1"]).run();
    assert_eq!(run.code, 0);
    let line = run.lines()[1];
    assert!(line.contains("First line second line xxx"));
    assert!(line.ends_with('…'));
    let run = Cli::to(&server, "messages", &["--limit", "1", "--json"]).run();
    assert_eq!(run.json()[0]["body"], body);
}

#[test]
fn send() {
    let server = FakeServer::new();
    let options = ["--to", BEN, "--text", "All systems running."];
    let run = Cli::to(&server, "send", &options).run();
    assert_eq!(run.code, 0, "{}", run.err);
    let sent = server.sent();
    let request_id = sent[0]["requestId"].as_str().unwrap();
    assert_eq!(
        run.out,
        format!("Queued: {BEN}, 1 part(s), requestId {request_id}\n")
    );
    assert_eq!(sent[0]["body"], "All systems running.");
}

#[test]
fn send_from_standard_input() {
    let server = FakeServer::new();
    let options = [
        "--to",
        BEN,
        "--sim",
        "2",
        "--request-id",
        "cron-1",
        "--json",
    ];
    let run = Cli::to(&server, "send", &options)
        .input("Line one\nLine two\n")
        .run();
    assert_eq!(run.code, 0, "{}", run.err);
    assert_eq!(
        run.out,
        format!(
            "{{\n  \"requestId\": \"cron-1\",\n  \"to\": \"{BEN}\",\n  \"parts\": 1,\n  \
             \"status\": \"queued\"\n}}\n"
        )
    );
    assert_eq!(
        server.sent(),
        [json!({"requestId": "cron-1", "to": BEN, "body": "Line one\nLine two", "sim": 2})]
    );
}

#[test]
fn send_needs_a_text() {
    let server = FakeServer::new();
    let run = Cli::to(&server, "send", &["--to", BEN]).input("").run();
    assert_eq!(run.code, 2);
    assert_eq!(run.err, "smsdock: The text is empty.\n");
    assert!(server.sent().is_empty());
}

#[test]
fn send_and_wait() {
    let server = FakeServer::new();
    let options = ["--to", BEN, "--text", "Hello", "--wait"];
    let run = Cli::to(&server, "send", &options).run();
    assert_eq!(run.code, 0, "{}", run.err);
    assert!(run
        .out
        .starts_with(&format!("Sent: {BEN}, 1 part(s), requestId ")));

    let options = ["--to", BEN, "--text", "Hello", "--wait", "--json"];
    let run = Cli::to(&server, "send", &options).run();
    assert_eq!(run.code, 0);
    let entry = run.json();
    assert_eq!(
        (&entry["status"], &entry["partsSent"]),
        (&json!("sent"), &json!(1))
    );
}

#[test]
fn send_and_wait_failed() {
    let server = FakeServer::new();
    server.state().fail_numbers.insert("+43123456".to_owned());
    let options = ["--to", "+43123456", "--text", "Hello", "--wait"];
    let run = Cli::to(&server, "send", &options).run();
    assert_eq!((run.code, run.out.as_str()), (1, ""));
    let request_id = server.sent()[0]["requestId"].as_str().unwrap().to_owned();
    assert_eq!(
        run.err,
        format!("smsdock: Sending to +43123456 failed (error code 1, requestId {request_id})\n")
    );

    let options = ["--to", "+43123456", "--text", "Hello", "--wait", "--json"];
    let run = Cli::to(&server, "send", &options).run();
    assert_eq!(run.code, 1);
    assert_eq!(run.json()["status"], "failed");
}

#[test]
fn send_again_tells_the_old_outcome() {
    let server = FakeServer::new();
    server.state().fail_numbers.insert("+43123456".to_owned());
    let failing = [
        "--to",
        "+43123456",
        "--text",
        "Hello",
        "--request-id",
        "job-1",
    ];
    let run = Cli::to(&server, "send", &failing).run();
    assert_eq!(
        (run.code, run.out.as_str()),
        (0, "Queued: +43123456, 1 part(s), requestId job-1\n")
    );
    assert!(wait_until(
        || server.outbox_status("job-1") == "failed",
        Duration::from_secs(3)
    ));
    server.state().fail_numbers.clear();
    // The repeat sends nothing and reports the failure of the first attempt.
    let run = Cli::to(&server, "send", &failing).run();
    assert_eq!((run.code, run.out.as_str()), (1, ""));
    assert_eq!(
        run.err,
        "smsdock: Sending to +43123456 failed (requestId job-1)\n"
    );
    assert_eq!(server.sent().len(), 1);

    let fine = ["--to", BEN, "--text", "Hello", "--request-id", "job-2"];
    let mut waiting = fine.to_vec();
    waiting.push("--wait");
    assert_eq!(Cli::to(&server, "send", &waiting).run().code, 0);
    let run = Cli::to(&server, "send", &fine).run();
    assert_eq!(
        (run.code, run.out),
        (0, format!("Sent: {BEN}, 1 part(s), requestId job-2\n"))
    );
    assert_eq!(server.sent().len(), 2);
}

#[test]
fn send_and_wait_times_out() {
    let server = FakeServer::start(Options {
        send_delay: Duration::from_secs(30),
        ..Options::default()
    });
    let options = [
        "--to",
        BEN,
        "--text",
        "Hello",
        "--wait",
        "--wait-timeout",
        "0.3",
    ];
    let run = Cli::to(&server, "send", &options).run();
    assert_eq!(run.code, 3);
    assert!(run.err.contains("No result"));
}

#[test]
fn delete() {
    let server = FakeServer::new();
    let newest = server.state().messages[24]["id"].to_string();
    let run = Cli::to(&server, "delete", &["--message", &newest]).run();
    assert_eq!(run.code, 0, "{}", run.err);
    assert_eq!(
        run.out,
        "Deleted 1 message(s): hidden in SMS Dock, still on the phone\n"
    );
    assert_eq!(server.state().messages.len(), 28);

    server.state().delete_mode = "provider".to_owned();
    let conversation = server.state().messages[0]["conversationId"].to_string();
    let run = Cli::to(&server, "delete", &["--conversation", &conversation]).run();
    assert_eq!(run.code, 0);
    assert_eq!(run.out, "Deleted 24 message(s): removed from the phone\n");
    let conversation = server.state().messages[0]["conversationId"].to_string();
    let run = Cli::to(
        &server,
        "delete",
        &["--conversation", &conversation, "--json"],
    )
    .run();
    assert_eq!(
        run.out,
        "{\n  \"deleted\": 3,\n  \"mode\": \"provider\"\n}\n"
    );
}

#[test]
fn mark_read() {
    let server = FakeServer::new();
    let conversation = server.state().messages[0]["conversationId"].to_string();
    let options = ["--conversation", conversation.as_str()];
    let run = Cli::to(&server, "mark-read", &options).run();
    assert_eq!((run.code, run.out.as_str()), (1, ""));
    assert!(run.err.starts_with("smsdock: not_default_app: "));

    server.state().delete_mode = "provider".to_owned();
    let run = Cli::to(&server, "mark-read", &options).run();
    assert_eq!(
        (run.code, run.out),
        (0, format!("Marked conversation {conversation} as read\n"))
    );
    let id: i64 = conversation.parse().expect("a conversation id");
    let unread = server
        .state()
        .messages
        .iter()
        .filter(|m| m["conversationId"] == id && m["read"] == false)
        .count();
    assert_eq!(unread, 0);
    let run = Cli::to(
        &server,
        "mark-read",
        &["--conversation", &conversation, "--json"],
    )
    .run();
    assert_eq!((run.code, run.json()), (0, json!({"ok": true})));
}

#[test]
fn contacts() {
    let server = FakeServer::new();
    let run = Cli::to(&server, "contacts", &[]).run();
    assert_eq!(run.code, 0, "{}", run.err);
    assert_eq!(
        run.out,
        format!(
            "NAME          NUMBER          LABEL\n\
             Anna Example  {ANNA}  Mobile\n\
             Ben Example   {BEN}  Work\n"
        )
    );
    let run = Cli::to(&server, "contacts", &["--search", "ben", "--json"]).run();
    assert_eq!(
        run.json(),
        json!([{"name": "Ben Example", "numbers": [{"number": BEN, "label": "Work"}]}])
    );
}

/// Run `listen` while another thread plays the phone.
fn listen(server: &FakeServer, options: &[&str], phone: impl FnOnce() + Send) -> Run {
    thread::scope(|scope| {
        scope.spawn(phone);
        Cli::to(server, "listen", options).run()
    })
}

fn patient(condition: impl FnMut() -> bool) -> bool {
    wait_until(condition, Duration::from_secs(10))
}

#[test]
fn listen_prints_incoming_sms() {
    let server = FakeServer::new();
    let run = listen(&server, &["--count", "2"], || {
        if patient(|| server.count("subscribe") == 1) {
            server.receive_sms(ANNA, "STATUS");
            server.receive_sms("A1", "Your bill");
        }
    });
    assert_eq!(run.code, 0, "{}", run.err);
    let lines = run.lines();
    assert_eq!(lines.len(), 2);
    assert!(
        lines[0].ends_with(&format!("  {ANNA} (Anna Example): STATUS")),
        "{}",
        lines[0]
    );
    assert!(lines[1].ends_with("  A1: Your bill"));
    // Local time with seconds, as in 2026-09-29 12:55:30.
    assert_eq!(lines[0].find("  "), Some(19));
    assert_eq!(
        run.err,
        "Listening for sms.received. Press Ctrl+C to stop.\n"
    );
    let request = server.last_request("subscribe");
    assert_eq!(request["events"], json!(["sms.received"]));
    assert!(request.get("address").is_none());
}

#[test]
fn listen_json_with_address() {
    let server = FakeServer::new();
    let run = listen(
        &server,
        &["--json", "--address", BEN, "--count", "1"],
        || {
            if patient(|| server.count("subscribe") == 1) {
                server.receive_sms(ANNA, "ignored");
                server.receive_sms(BEN, "wanted");
            }
        },
    );
    assert_eq!(run.code, 0, "{}", run.err);
    assert_eq!(run.err, "");
    assert_eq!(run.lines().len(), 1);
    let event = run.json();
    assert_eq!(event["event"], "sms.received");
    assert_eq!(event["data"]["message"]["body"], "wanted");
    // One object per line, written as Python writes it.
    assert!(run.out.starts_with(
        "{\"event\": \"sms.received\", \"seq\": 2, \"data\": {\"message\": {\"id\": "
    ));
}

#[test]
fn listen_to_other_events() {
    let server = FakeServer::new();
    let options = ["--events", "sms.delivered", "sms.failed", "--count", "1"];
    let run = listen(&server, &options, || {
        if patient(|| server.count("subscribe") == 1) {
            let data = json!({"requestId": "r1", "id": 7, "conversationId": 171, "to": BEN});
            server.emit("sms.delivered", data);
        }
    });
    assert_eq!(run.code, 0, "{}", run.err);
    let expected = format!("  sms.delivered requestId=r1 id=7 conversationId=171 to={BEN}\n");
    assert!(run.out.ends_with(&expected), "{}", run.out);
    assert_eq!(
        run.err,
        "Listening for sms.delivered, sms.failed. Press Ctrl+C to stop.\n"
    );
    assert_eq!(
        server.last_request("subscribe")["events"],
        json!(["sms.delivered", "sms.failed"])
    );
}

#[test]
fn listen_reports_a_reconnect() {
    let server = FakeServer::new();
    let run = listen(&server, &["--count", "1"], || {
        if !patient(|| server.count("subscribe") == 1) {
            return;
        }
        server.drop_connections(None, "");
        patient(|| server.client_count() == 0);
        server.receive_sms(ANNA, "lost");
        server.receive_sms(ANNA, "lost too");
        if patient(|| server.count("subscribe") == 2) {
            server.restart();
        }
        if patient(|| server.count("subscribe") == 3) {
            server.receive_sms(ANNA, "received");
        }
    });
    assert_eq!(run.code, 0, "{}", run.err);
    assert!(run.out.trim_end().ends_with(": received"));
    let lines: Vec<&str> = run.err.lines().collect();
    assert_eq!(lines.len(), 3, "{}", run.err);
    assert_eq!(
        lines[1],
        "smsdock: reconnected; 2 event(s) of the phone were not received"
    );
    assert_eq!(
        lines[2],
        "smsdock: reconnected; the app was restarted, events may be missing"
    );
}

// ----------------------------------------------------------------------
// Exit codes
// ----------------------------------------------------------------------

#[test]
fn exit_code_1_for_api_errors() {
    let server = FakeServer::new();
    let run = Cli::to(&server, "delete", &["--message", "1"]).run();
    assert_eq!((run.code, run.out.as_str()), (1, ""));
    assert_eq!(run.err, "smsdock: not_found: No message with the id 1.\n");

    let run = Cli::to(&server, "send", &["--to", "abc", "--text", "x"]).run();
    assert_eq!(run.code, 1);
    assert!(run.err.contains("invalid_number"));

    server.fail_next(
        "messages.send",
        "quota_exceeded",
        "Daily limit reached.",
        json!({}),
    );
    let run = Cli::to(&server, "send", &["--to", BEN, "--text", "x"]).run();
    assert_eq!(run.code, 1);
    assert_eq!(run.err, "smsdock: quota_exceeded: Daily limit reached.\n");
}

#[test]
fn exit_code_1_for_a_wrong_password() {
    let server = FakeServer::new();
    let run = Cli::to(&server, "status", &[])
        .env("SMSDOCK_PASSWORD", "wrong-password")
        .run();
    assert_eq!((run.code, run.out.as_str()), (1, ""));
    assert_eq!(run.err, "smsdock: invalid_password: Wrong password.\n");
    assert_eq!(server.login_count(), 1);
}

#[test]
fn exit_code_1_without_licence() {
    let server = FakeServer::new();
    server.state().licence_enforced = true;
    let run = Cli::to(&server, "status", &[]).run();
    assert_eq!(run.code, 1);
    assert!(run.err.contains("licence_required"));
}

#[test]
fn exit_code_3_when_nothing_listens() {
    let mut server = FakeServer::new();
    server.stop();
    let url = server.url();
    let pin = fingerprint();
    let run = Cli::new(&[
        "status",
        &url,
        "--fingerprint",
        &pin,
        "--reconnect-attempts",
        "0",
    ])
    .run();
    assert_eq!((run.code, run.out.as_str()), (3, ""));
    assert!(run
        .err
        .starts_with(&format!("smsdock: Cannot connect to {url}: ")));
}

#[test]
fn exit_code_3_for_another_certificate() {
    let server = FakeServer::new();
    let other = &other_certificate().fingerprint;
    let run = Cli::new(&["status", &server.url(), "--fingerprint", other]).run();
    assert_eq!((run.code, run.out.as_str()), (3, ""));
    assert!(run.err.contains(&server.fingerprint) && run.err.contains(other));
    assert!(run
        .err
        .starts_with("smsdock: The certificate of the server does not match"));
    assert!(server.requests().is_empty());
    assert_eq!(server.connection_count(), 0);
}

#[test]
fn fingerprint_from_the_environment_is_used() {
    let server = FakeServer::new();
    let run = Cli::new(&["status", &server.url()])
        .env("SMSDOCK_FINGERPRINT", &server.fingerprint.to_lowercase())
        .run();
    assert_eq!(run.code, 0, "{}", run.err);
}

#[test]
fn insecure_warns() {
    let server = FakeServer::new();
    let run = Cli::new(&["status", &server.url(), "--insecure", "--timeout", "5"]).run();
    assert_eq!(run.code, 0, "{}", run.err);
    assert!(run.err.starts_with(WARNING));
    assert!(run.err.contains("not checked"));
    assert_eq!(run.err.matches("WARNING").count(), 1);
    assert!(run.out.contains("App:"));
}
