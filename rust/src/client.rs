//! Synchronous, reconnecting client for the SMS Dock WebSocket API.
//!
//! The protocol is described in `API.md` at the top of the repository. A
//! client is used from one thread at a time; it can be moved to another
//! thread.

use std::collections::{HashMap, HashSet, VecDeque};
use std::fmt;
use std::marker::PhantomData;
use std::net::Shutdown;
use std::thread;
use std::time::{Duration, Instant, SystemTime, UNIX_EPOCH};

use serde::de::DeserializeOwned;
use serde_json::{Map, Value};
use tungstenite::client::client_with_config;
use tungstenite::http::Uri;
use tungstenite::protocol::WebSocketConfig;
use tungstenite::{HandshakeError, Message as Frame, WebSocket};

use crate::error::{Error, Result};
use crate::models::{
    AppInfo, Contact, Conversation, DeleteResult, Event, GroupSendResult, MarkReadResult, Message,
    OutboxEntry, Page, PingResult, SendResult, Status, Subscription,
};
use crate::tls::{self, Digest32, TlsFailure, TlsStream};
use crate::{
    API_PROTOCOL_VERSION, FINGERPRINT_ENV, MAX_FRAME_BYTES, MAX_INCOMING_BYTES, MAX_PAGE_SIZE,
    PASSWORD_ENV,
};

/// Called with the client after every reconnect; see
/// [`ClientBuilder::on_reconnect`].
pub type ReconnectHook = Box<dyn FnMut(&mut SmsDockClient) -> Result<()> + Send>;

type JsonObject = Map<String, Value>;
type Socket = WebSocket<TlsStream>;

const URL_HELP: &str = "SMS Dock needs a wss:// URL, for example wss://192.168.1.50:9443/ws";
const ID_HELP: &str = "'id' is the id of the request; the client sets it";
const NO_CONNECTION: &str = "No connection to SMS Dock";
const MAX_BACKOFF_EXPONENT: u32 = 16;
const FIRST_POLL: Duration = Duration::from_millis(100);
/// Longest single wait for data; the deadline is looked at in between.
const READ_SLICE: Duration = Duration::from_secs(5);
/// A ping is sent when the server was silent for this long while the
/// client waited. The server itself pings every 20 seconds.
const PING_AFTER: Duration = Duration::from_secs(25);
/// The connection counts as lost after this much silence.
const LOST_AFTER: Duration = Duration::from_secs(45);
const OUTCOME_EVENTS: [&str; 3] = ["sms.sent", "sms.delivered", "sms.failed"];

/// A failure of the connection itself; the cases in which the client
/// reconnects, unless the close code forbids it.
#[derive(Debug)]
enum Transport {
    /// The server closed the WebSocket with a close frame.
    Closed { code: u16, reason: Option<String> },
    /// The time to wait is over.
    Timeout(String),
    /// Anything else, as text.
    Broken(String),
}

/// What can go wrong while talking to the server.
#[derive(Debug)]
enum Failure {
    /// The connection failed; a new attempt may help.
    Transport(Transport),
    /// An error to hand to the caller as it is.
    Error(Error),
}

impl From<Error> for Failure {
    fn from(error: Error) -> Self {
        Failure::Error(error)
    }
}

impl From<Transport> for Failure {
    fn from(transport: Transport) -> Self {
        Failure::Transport(transport)
    }
}

type Attempt<T> = std::result::Result<T, Failure>;

/// One read from the WebSocket.
enum Incoming {
    /// A text frame.
    Text(String),
    /// A ping or pong; nothing for the caller.
    Control,
    /// Nothing arrived in the time allowed.
    Nothing,
}

/// `"in"`, `"out"` or both; a filter of [`MessageFilter`].
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub enum Direction {
    /// Incoming and outgoing messages.
    #[default]
    All,
    /// Incoming messages.
    In,
    /// Outgoing messages.
    Out,
}

impl Direction {
    /// The value of the field `direction`: `"all"`, `"in"` or `"out"`.
    pub fn as_str(self) -> &'static str {
        match self {
            Direction::All => "all",
            Direction::In => "in",
            Direction::Out => "out",
        }
    }
}

/// Filters of [`SmsDockClient::messages`] and
/// [`SmsDockClient::iter_messages`].
///
/// ```
/// use smsdock::{Direction, MessageFilter};
///
/// let filter = MessageFilter::new()
///     .address("+4369912345678")
///     .direction(Direction::In)
///     .q("invoice");
/// assert_eq!(filter.q.as_deref(), Some("invoice"));
/// ```
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct MessageFilter {
    /// Only messages from or to this number.
    pub address: Option<String>,
    /// Only this conversation; wins if `address` is given too.
    pub conversation_id: Option<i64>,
    /// Incoming, outgoing or both.
    pub direction: Direction,
    /// Search in the message text.
    pub q: Option<String>,
    /// Not older than this, in milliseconds since the epoch, inclusive.
    /// Sent as the field `from`.
    pub since: Option<i64>,
    /// Not newer than this, in milliseconds since the epoch, inclusive.
    /// Sent as the field `to`.
    pub until: Option<i64>,
    /// Only this SIM (subscription id).
    pub sim: Option<i64>,
    /// Only unread messages.
    pub unread: bool,
}

impl MessageFilter {
    /// A filter that lets every message pass.
    pub fn new() -> Self {
        Self::default()
    }

    /// Keep messages from or to one number.
    pub fn address(mut self, address: impl Into<String>) -> Self {
        self.address = Some(address.into());
        self
    }

    /// Keep the messages of one conversation.
    pub fn conversation_id(mut self, conversation_id: i64) -> Self {
        self.conversation_id = Some(conversation_id);
        self
    }

    /// Keep incoming or outgoing messages.
    pub fn direction(mut self, direction: Direction) -> Self {
        self.direction = direction;
        self
    }

    /// Search the text.
    pub fn q(mut self, q: impl Into<String>) -> Self {
        self.q = Some(q.into());
        self
    }

    /// Keep messages not older than this many milliseconds since the epoch.
    pub fn since(mut self, milliseconds: i64) -> Self {
        self.since = Some(milliseconds);
        self
    }

    /// Keep messages not older than this moment.
    pub fn since_time(self, moment: SystemTime) -> Self {
        self.since(millis(moment))
    }

    /// Keep messages not newer than this many milliseconds since the epoch.
    pub fn until(mut self, milliseconds: i64) -> Self {
        self.until = Some(milliseconds);
        self
    }

    /// Keep messages not newer than this moment.
    pub fn until_time(self, moment: SystemTime) -> Self {
        self.until(millis(moment))
    }

    /// Keep the messages of one SIM (subscription id).
    pub fn sim(mut self, sim: i64) -> Self {
        self.sim = Some(sim);
        self
    }

    /// Keep only unread messages.
    pub fn unread(mut self, unread: bool) -> Self {
        self.unread = unread;
        self
    }

    fn fields(&self) -> JsonObject {
        let mut fields = JsonObject::new();
        put(&mut fields, "address", self.address.as_deref());
        put(&mut fields, "conversationId", self.conversation_id);
        if self.direction != Direction::All {
            put(&mut fields, "direction", Some(self.direction.as_str()));
        }
        put(&mut fields, "q", self.q.as_deref());
        put(&mut fields, "from", self.since);
        put(&mut fields, "to", self.until);
        put(&mut fields, "sim", self.sim);
        put(&mut fields, "unread", self.unread.then_some(true));
        fields
    }
}

/// Filters of [`SmsDockClient::conversations`] and
/// [`SmsDockClient::iter_conversations`].
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct ConversationFilter {
    /// Search in text, numbers and contact names.
    pub q: Option<String>,
    /// Only conversations with unread messages.
    pub unread: bool,
    /// Only this SIM (subscription id).
    pub sim: Option<i64>,
}

impl ConversationFilter {
    /// A filter that lets every conversation pass.
    pub fn new() -> Self {
        Self::default()
    }

    /// Search text, numbers and contact names.
    pub fn q(mut self, q: impl Into<String>) -> Self {
        self.q = Some(q.into());
        self
    }

    /// Keep only conversations with unread messages.
    pub fn unread(mut self, unread: bool) -> Self {
        self.unread = unread;
        self
    }

    /// Keep the conversations of one SIM (subscription id).
    pub fn sim(mut self, sim: i64) -> Self {
        self.sim = Some(sim);
        self
    }

    fn fields(&self) -> JsonObject {
        let mut fields = JsonObject::new();
        put(&mut fields, "q", self.q.as_deref());
        put(&mut fields, "unread", self.unread.then_some(true));
        put(&mut fields, "sim", self.sim);
        fields
    }
}

/// Options of [`SmsDockClient::wait_for_result_with`].
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct WaitOptions {
    /// Longest time to wait; `None` means the timeout of the client.
    pub timeout: Option<Duration>,
    /// Wait for the delivery report; the status `sent` is not enough.
    /// Some networks never send one.
    pub delivered: bool,
    /// Longest pause between two questions to the server.
    pub poll_interval: Duration,
}

impl Default for WaitOptions {
    fn default() -> Self {
        Self {
            timeout: None,
            delivered: false,
            poll_interval: Duration::from_secs(1),
        }
    }
}

/// Collects the options of a [`SmsDockClient`].
///
/// ```no_run
/// use std::time::Duration;
/// use smsdock::SmsDockClient;
///
/// # fn main() -> smsdock::Result<()> {
/// let mut dock = SmsDockClient::builder("wss://192.168.1.50:9443/ws")
///     .timeout(Duration::from_secs(30))
///     .reconnect_attempts(None)
///     .connect()?;
/// println!("{}", dock.ping()?.time);
/// # Ok(())
/// # }
/// ```
pub struct ClientBuilder {
    url: String,
    password: Option<String>,
    fingerprint: Option<String>,
    insecure: bool,
    timeout: Duration,
    connect_timeout: Duration,
    reconnect_attempts: Option<u32>,
    reconnect_delay: Duration,
    max_reconnect_delay: Duration,
    event_queue_size: usize,
    rate_limit_retries: u32,
    on_reconnect: Option<ReconnectHook>,
}

impl ClientBuilder {
    fn new(url: &str) -> Self {
        Self {
            url: url.to_owned(),
            password: None,
            fingerprint: None,
            insecure: false,
            timeout: Duration::from_secs(30),
            connect_timeout: Duration::from_secs(10),
            reconnect_attempts: Some(5),
            reconnect_delay: Duration::from_secs(1),
            max_reconnect_delay: Duration::from_secs(10),
            event_queue_size: 1000,
            rate_limit_retries: 2,
            on_reconnect: None,
        }
    }

    /// The password set in the app. Without it the environment variable
    /// `SMSDOCK_PASSWORD` is read.
    pub fn password(mut self, password: impl Into<String>) -> Self {
        self.password = Some(password.into());
        self
    }

    /// The SHA-256 fingerprint of the certificate as shown on the Server
    /// screen of the app: hex, with or without colons, in any case.
    /// Without it the environment variable `SMSDOCK_FINGERPRINT` is read.
    pub fn fingerprint(mut self, fingerprint: impl Into<String>) -> Self {
        self.fingerprint = Some(fingerprint.into());
        self
    }

    /// `true` skips the check of the certificate and prints a warning to
    /// standard error. For tests only.
    pub fn insecure(mut self, insecure: bool) -> Self {
        self.insecure = insecure;
        self
    }

    /// Time to wait for a response. The default is 30 seconds.
    pub fn timeout(mut self, timeout: Duration) -> Self {
        self.timeout = timeout;
        self
    }

    /// Time to open the connection. The default is 10 seconds.
    pub fn connect_timeout(mut self, connect_timeout: Duration) -> Self {
        self.connect_timeout = connect_timeout;
        self
    }

    /// Reconnect attempts after a lost connection: `Some(0)` for none,
    /// `None` for no limit. The default is `Some(5)`.
    pub fn reconnect_attempts(mut self, reconnect_attempts: Option<u32>) -> Self {
        self.reconnect_attempts = reconnect_attempts;
        self
    }

    /// Time before the first reconnect attempt; doubled for each further
    /// one. The default is 1 second.
    pub fn reconnect_delay(mut self, reconnect_delay: Duration) -> Self {
        self.reconnect_delay = reconnect_delay;
        self
    }

    /// Upper limit of the delay between reconnect attempts. The default
    /// is 10 seconds.
    pub fn max_reconnect_delay(mut self, max_reconnect_delay: Duration) -> Self {
        self.max_reconnect_delay = max_reconnect_delay;
        self
    }

    /// Events kept while nobody reads them; after that the oldest are
    /// dropped and counted in [`SmsDockClient::dropped_events`]. The
    /// default is 1000.
    pub fn event_queue_size(mut self, event_queue_size: usize) -> Self {
        self.event_queue_size = event_queue_size;
        self
    }

    /// How often a request is repeated after the error `rate_limited`.
    /// The default is 2.
    pub fn rate_limit_retries(mut self, rate_limit_retries: u32) -> Self {
        self.rate_limit_retries = rate_limit_retries;
        self
    }

    /// A function called with the client after every reconnect, once the
    /// subscription is restored. Use it to read what was missed, for
    /// example with [`SmsDockClient::iter_messages`]. An error it returns
    /// closes the connection and ends the call that reconnected.
    pub fn on_reconnect<F>(mut self, hook: F) -> Self
    where
        F: FnMut(&mut SmsDockClient) -> Result<()> + Send + 'static,
    {
        self.on_reconnect = Some(Box::new(hook));
        self
    }

    /// Check the options and return a client that is not connected yet.
    ///
    /// The first request connects. Wrong options are an
    /// [`Error::InvalidArgument`].
    pub fn build(self) -> Result<SmsDockClient> {
        let (host, port) = parse_url(&self.url)?;
        let password = self
            .password
            .or_else(|| std::env::var(PASSWORD_ENV).ok())
            .filter(|password| !password.is_empty())
            .ok_or_else(|| {
                invalid(format!(
                    "No password: pass one or set the environment variable {PASSWORD_ENV}"
                ))
            })?;
        let fingerprint = self.fingerprint.or_else(|| {
            std::env::var(FINGERPRINT_ENV)
                .ok()
                .filter(|fingerprint| !fingerprint.is_empty())
        });
        if fingerprint.is_none() && !self.insecure {
            return Err(invalid(format!(
                "No certificate fingerprint: pass the fingerprint shown on the Server \
                 screen of the app or set the environment variable {FINGERPRINT_ENV}"
            )));
        }
        if self.timeout.is_zero() || self.connect_timeout.is_zero() {
            return Err(invalid("Timeouts must be positive"));
        }
        if self.event_queue_size < 1 {
            return Err(invalid("event_queue_size must be at least 1"));
        }
        let pin = match (&fingerprint, self.insecure) {
            (Some(fingerprint), false) => Some(tls::parse_pin(fingerprint)?),
            _ => None,
        };
        Ok(SmsDockClient {
            url: self.url,
            host,
            port,
            password,
            pin,
            insecure: self.insecure,
            timeout: self.timeout,
            connect_timeout: self.connect_timeout,
            reconnect_attempts: self.reconnect_attempts,
            reconnect_delay: self.reconnect_delay,
            max_reconnect_delay: self.max_reconnect_delay,
            event_queue_size: self.event_queue_size,
            rate_limit_retries: self.rate_limit_retries,
            on_reconnect: self.on_reconnect,
            socket: None,
            server_info: None,
            events: VecDeque::new(),
            dropped_events: 0,
            connection_count: 0,
            request_number: 0,
            outstanding: HashSet::new(),
            responses: HashMap::new(),
            subscription: None,
            warned: false,
            epoch: None,
            latest_seq: None,
            restarted: false,
            missed: Some(0),
            quiet: Duration::ZERO,
            pinged: false,
        })
    }

    /// Check the options, connect and log in.
    pub fn connect(self) -> Result<SmsDockClient> {
        let mut client = self.build()?;
        client.connect()?;
        Ok(client)
    }
}

impl fmt::Debug for ClientBuilder {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        // The password stays out of diagnostics.
        formatter
            .debug_struct("ClientBuilder")
            .field("url", &self.url)
            .field("insecure", &self.insecure)
            .finish_non_exhaustive()
    }
}

/// One authenticated connection to SMS Dock.
///
/// The URL is the `wss://` address the app shows, for example
/// `wss://192.168.1.50:9443/ws`. Any other scheme is refused.
///
/// The client compares the fingerprint with the certificate of the server
/// inside the TLS handshake and sends nothing if they differ.
///
/// After a lost connection the client reconnects up to
/// `reconnect_attempts` times. It waits `reconnect_delay` before the first
/// attempt and doubles the delay each time up to `max_reconnect_delay`.
/// After reconnecting it logs in again and restores the subscription.
/// Events that happened while the connection was down are lost;
/// [`missed_events`](Self::missed_events) and
/// [`server_restarted`](Self::server_restarted) tell whether there were
/// any.
///
/// When the server answers `rate_limited` and names the seconds to wait,
/// the client waits and repeats the request, at most `rate_limit_retries`
/// times.
///
/// Dropping the client closes the connection.
///
/// ```no_run
/// use smsdock::SmsDockClient;
///
/// # fn main() -> smsdock::Result<()> {
/// // Password and fingerprint come from SMSDOCK_PASSWORD and
/// // SMSDOCK_FINGERPRINT.
/// let mut dock = SmsDockClient::builder("wss://192.168.1.50:9443/ws").connect()?;
/// println!("{:?}", dock.status()?.sims);
/// # Ok(())
/// # }
/// ```
pub struct SmsDockClient {
    url: String,
    host: String,
    port: u16,
    password: String,
    pin: Option<Digest32>,
    insecure: bool,
    timeout: Duration,
    connect_timeout: Duration,
    reconnect_attempts: Option<u32>,
    reconnect_delay: Duration,
    max_reconnect_delay: Duration,
    event_queue_size: usize,
    rate_limit_retries: u32,
    on_reconnect: Option<ReconnectHook>,
    socket: Option<Socket>,
    server_info: Option<AppInfo>,
    events: VecDeque<Event>,
    dropped_events: u64,
    connection_count: u64,
    request_number: u64,
    outstanding: HashSet<String>,
    responses: HashMap<String, JsonObject>,
    subscription: Option<Subscription>,
    warned: bool,
    epoch: Option<i64>,
    latest_seq: Option<i64>,
    restarted: bool,
    missed: Option<u64>,
    /// Time spent waiting since the server was last heard.
    quiet: Duration,
    /// Whether a ping was sent in this quiet time.
    pinged: bool,
}

impl fmt::Debug for SmsDockClient {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        // The password stays out of diagnostics.
        formatter
            .debug_struct("SmsDockClient")
            .field("url", &self.url)
            .field("connected", &self.connected())
            .finish_non_exhaustive()
    }
}

impl Drop for SmsDockClient {
    fn drop(&mut self) {
        self.close();
    }
}

impl SmsDockClient {
    /// Start a client with options; see [`ClientBuilder`].
    pub fn builder(url: &str) -> ClientBuilder {
        ClientBuilder::new(url)
    }

    /// A client with the default options that is not connected yet.
    ///
    /// Password and fingerprint come from the environment variables
    /// `SMSDOCK_PASSWORD` and `SMSDOCK_FINGERPRINT`.
    pub fn new(url: &str) -> Result<Self> {
        ClientBuilder::new(url).build()
    }

    // ------------------------------------------------------------------
    // Connection
    // ------------------------------------------------------------------

    /// The URL of the server.
    pub fn url(&self) -> &str {
        &self.url
    }

    /// Whether the certificate of the server is left unchecked.
    pub fn insecure(&self) -> bool {
        self.insecure
    }

    /// Whether a connection is open as far as the client knows.
    pub fn connected(&self) -> bool {
        self.socket.is_some()
    }

    /// Name, version and protocol version of the app, known after the
    /// login.
    pub fn server_info(&self) -> Option<&AppInfo> {
        self.server_info.as_ref()
    }

    /// The subscription in force, or `None` without one.
    pub fn subscription(&self) -> Option<&Subscription> {
        self.subscription.as_ref()
    }

    /// Number of events that wait in the queue.
    pub fn pending_events(&self) -> usize {
        self.events.len()
    }

    /// Number of events dropped because the event queue was full.
    pub fn dropped_events(&self) -> u64 {
        self.dropped_events
    }

    /// Number of connections opened so far, reconnects included.
    pub fn connection_count(&self) -> u64 {
        self.connection_count
    }

    /// The `epoch` of the last login; `None` before the first.
    ///
    /// It identifies one run of the app. Sequence numbers begin at 1
    /// again when it changes.
    pub fn epoch(&self) -> Option<i64> {
        self.epoch
    }

    /// Sequence number of the newest event the client knows of.
    ///
    /// This is the `latestSeq` of the last login or the `seq` of an event
    /// received since, whichever is higher. The phone counts all events,
    /// so a client that subscribed to some of them sees gaps.
    pub fn latest_seq(&self) -> Option<i64> {
        self.latest_seq
    }

    /// Whether the app was restarted before the last reconnect.
    ///
    /// `true` when the `epoch` of the current connection differs from the
    /// one of the connection before. Events were lost then, and their
    /// number is not known.
    pub fn server_restarted(&self) -> bool {
        self.restarted
    }

    /// Number of events of the phone during the last interruption.
    ///
    /// Counted over all events, subscribed or not. `Some(0)` after the
    /// first connect, `None` when the app was restarted and the number is
    /// not known.
    pub fn missed_events(&self) -> Option<u64> {
        self.missed
    }

    /// Replace the function called after every reconnect; see
    /// [`ClientBuilder::on_reconnect`].
    pub fn set_on_reconnect<F>(&mut self, hook: F)
    where
        F: FnMut(&mut SmsDockClient) -> Result<()> + Send + 'static,
    {
        self.on_reconnect = Some(Box::new(hook));
    }

    /// Remove the function called after every reconnect.
    pub fn clear_on_reconnect(&mut self) {
        self.on_reconnect = None;
    }

    /// Open the connection, check the certificate and log in.
    ///
    /// Makes one attempt. The errors are [`Error::CertificateMismatch`],
    /// [`Error::Api`] (wrong password, locked, no licence),
    /// [`Error::Protocol`] and [`Error::ConnectionInterrupted`].
    pub fn connect(&mut self) -> Result<()> {
        self.close();
        match self.open() {
            Ok(()) => Ok(()),
            Err(Failure::Error(error)) => Err(error),
            Err(Failure::Transport(transport)) => {
                self.close();
                let message = format!("Cannot connect to {}", self.url);
                Err(final_error(&message, &transport))
            }
        }
    }

    /// Close the connection. Queued events are kept.
    pub fn close(&mut self) {
        self.outstanding.clear();
        self.responses.clear();
        let Some(mut socket) = self.socket.take() else {
            return;
        };
        let patience = self.timeout.min(Duration::from_secs(5));
        let _ = socket.get_ref().sock.set_nonblocking(false);
        let _ = socket.get_ref().sock.set_write_timeout(Some(patience));
        let _ = socket.close(None);
        let _ = socket.flush();
        socket.get_mut().conn.send_close_notify();
        let _ = std::io::Write::flush(socket.get_mut());
        let _ = socket.get_ref().sock.shutdown(Shutdown::Both);
    }

    // ------------------------------------------------------------------
    // Requests
    // ------------------------------------------------------------------

    /// Send one request and return the `result` of its response.
    ///
    /// `fields` holds the fields of the action as named in `API.md`: a
    /// JSON object, or `Value::Null` for none. The request is sent again
    /// after a lost connection, and after the error `rate_limited` when
    /// the server names the seconds to wait. The errors are [`Error::Api`]
    /// when the server answers with an error and
    /// [`Error::ConnectionInterrupted`] when the connection could not be
    /// restored.
    ///
    /// ```no_run
    /// # fn main() -> smsdock::Result<()> {
    /// # let mut dock = smsdock::SmsDockClient::new("wss://192.168.1.50:9443/ws")?;
    /// let page = dock.request("messages.list", serde_json::json!({"limit": 1}))?;
    /// println!("{}", page["items"]);
    /// # Ok(())
    /// # }
    /// ```
    pub fn request(&mut self, action: &str, fields: Value) -> Result<Value> {
        self.request_with(action, fields, true, None)
    }

    /// Like [`request`](Self::request), with two more options.
    ///
    /// Pass `retry = false` for anything that must not happen twice: the
    /// request is then neither repeated after a lost connection nor after
    /// `rate_limited`. `timeout` replaces the timeout of the client.
    pub fn request_with(
        &mut self,
        action: &str,
        fields: Value,
        retry: bool,
        timeout: Option<Duration>,
    ) -> Result<Value> {
        let fields = request_fields(fields)?;
        self.perform(action, &fields, retry, timeout)
            .map(Value::Object)
    }

    /// Send a request without waiting and return its id.
    ///
    /// Several requests can be on their way at once; the server may
    /// answer them in any order. Fetch each response with
    /// [`wait_for_response`](Self::wait_for_response). These two methods
    /// do not reconnect and do not repeat a request that was answered
    /// with `rate_limited`.
    pub fn send_request(&mut self, action: &str, fields: Value) -> Result<String> {
        let fields = request_fields(fields)?;
        let sent = self
            .ensure_open()
            .and_then(|()| self.send_frame(action, &fields));
        match sent {
            Ok(key) => Ok(key),
            Err(Failure::Error(error)) => Err(error),
            Err(Failure::Transport(transport)) => {
                self.close();
                let message = format!("Connection interrupted while sending '{action}'");
                Err(final_error(&message, &transport))
            }
        }
    }

    /// Return the result of a request sent with
    /// [`send_request`](Self::send_request).
    ///
    /// The errors are [`Error::Api`] when the response is an error,
    /// [`Error::Timeout`] when none arrives in time and
    /// [`Error::ConnectionInterrupted`] when the connection was lost.
    pub fn wait_for_response(
        &mut self,
        request_id: &str,
        timeout: Option<Duration>,
    ) -> Result<Value> {
        if !self.outstanding.contains(request_id) && !self.responses.contains_key(request_id) {
            return Err(invalid(format!(
                "No request with the id '{request_id}' is open"
            )));
        }
        let deadline = Instant::now() + self.timeout_or_default(timeout);
        let label = format!("request {request_id}");
        match self.await_response(request_id, &label, deadline) {
            Ok(result) => Ok(Value::Object(result)),
            Err(Failure::Error(error)) => Err(error),
            Err(Failure::Transport(Transport::Timeout(message))) => Err(Error::Timeout(message)),
            Err(Failure::Transport(transport)) => {
                self.close();
                let message = format!("Connection interrupted while waiting for '{request_id}'");
                Err(final_error(&message, &transport))
            }
        }
    }

    /// Return phone, SIMs, permissions, licence and limits.
    pub fn status(&mut self) -> Result<Status> {
        self.call("status", JsonObject::new())
    }

    /// Check the connection. Returns the time of the phone.
    pub fn ping(&mut self) -> Result<PingResult> {
        self.call("ping", JsonObject::new())
    }

    /// Return one page of conversations, newest first.
    ///
    /// `limit` is 1 to 200. `cursor` is the `next_cursor` of the previous
    /// page.
    pub fn conversations(
        &mut self,
        filter: &ConversationFilter,
        limit: u32,
        cursor: Option<&str>,
    ) -> Result<Page<Conversation>> {
        let mut fields = filter.fields();
        paging(&mut fields, limit, cursor)?;
        self.call("conversations.list", fields)
    }

    /// Iterate over conversations, newest first, page after page.
    ///
    /// `page_size` is the `limit` of each request. `max_items` ends the
    /// iteration after that many conversations; `None` yields all.
    pub fn iter_conversations(
        &mut self,
        filter: &ConversationFilter,
        page_size: u32,
        max_items: Option<usize>,
    ) -> Result<Items<'_, Conversation>> {
        Items::new(
            self,
            "conversations.list",
            filter.fields(),
            page_size,
            max_items,
        )
    }

    /// Return one page of messages, newest first.
    ///
    /// `limit` is 1 to 200. `cursor` is the `next_cursor` of the previous
    /// page.
    ///
    /// ```no_run
    /// use smsdock::{Direction, MessageFilter};
    ///
    /// # fn main() -> smsdock::Result<()> {
    /// # let mut dock = smsdock::SmsDockClient::new("wss://192.168.1.50:9443/ws")?;
    /// // The last 10 incoming SMS
    /// let filter = MessageFilter::new().direction(Direction::In);
    /// for message in dock.messages(&filter, 10, None)?.items {
    ///     println!("{} {}", message.address, message.body);
    /// }
    /// # Ok(())
    /// # }
    /// ```
    pub fn messages(
        &mut self,
        filter: &MessageFilter,
        limit: u32,
        cursor: Option<&str>,
    ) -> Result<Page<Message>> {
        let mut fields = filter.fields();
        paging(&mut fields, limit, cursor)?;
        self.call("messages.list", fields)
    }

    /// Iterate over messages, newest first, following `next_cursor`.
    ///
    /// `page_size` is the `limit` of each request. `max_items` ends the
    /// iteration after that many messages; `None` yields all of them.
    /// Nothing is requested before the first item is asked for.
    pub fn iter_messages(
        &mut self,
        filter: &MessageFilter,
        page_size: u32,
        max_items: Option<usize>,
    ) -> Result<Items<'_, Message>> {
        Items::new(self, "messages.list", filter.fields(), page_size, max_items)
    }

    /// Return one message. The error is `not_found` if it does not exist.
    pub fn get_message(&mut self, message_id: i64) -> Result<Message> {
        self.call("messages.get", object([("messageId", message_id.into())]))
    }

    /// Send an SMS.
    ///
    /// `to` is the phone number, `body` the text (at most 10 SMS parts),
    /// `sim` a subscription id (default: the SMS SIM of the phone). The
    /// message is on its way when this method returns; use
    /// [`wait_for_result`](Self::wait_for_result) for the outcome.
    ///
    /// `request_id` makes the request safe to repeat: the phone sends a
    /// message with the same `request_id` only once and remembers the id
    /// for 30 days. A repeat returns the result of the first request with
    /// the current `status`, which may be `sent` already. A repeat never
    /// sends again, also not after the first attempt failed: the status
    /// is `failed` then, and a new attempt needs a new `request_id`. A
    /// repeat with another recipient or text fails with `invalid_request`.
    ///
    /// Without `request_id` the client creates a random one. If the
    /// connection drops before the response arrives, the request is
    /// repeated with the same id.
    ///
    /// In the result `to` is the number as it is sent, without spaces and
    /// other ignored characters.
    pub fn send(
        &mut self,
        to: &str,
        body: &str,
        sim: Option<i64>,
        request_id: Option<&str>,
    ) -> Result<SendResult> {
        if to.trim().is_empty() {
            return Err(invalid("'to' must be a phone number"));
        }
        if body.is_empty() {
            return Err(invalid("'body' must be a text"));
        }
        let request_id = match request_id {
            None => uuid::Uuid::new_v4().simple().to_string(),
            Some(given) if is_request_id(given) => given.to_owned(),
            Some(_) => {
                return Err(invalid(
                    "request_id must be 1 to 64 characters of A-Z a-z 0-9 . _ -",
                ));
            }
        };
        let mut fields = object([
            ("to", to.into()),
            ("body", body.into()),
            ("requestId", request_id.into()),
        ]);
        put(&mut fields, "sim", sim);
        self.call("messages.send", fields)
    }

    /// Send one text to several numbers, one SMS each.
    ///
    /// `to` holds 1 to 20 phone numbers; repeated numbers are sent once.
    /// Every recipient gets a separate SMS and does not see the others. The
    /// result holds one [`SendResult`] per recipient; their request ids are
    /// `<request_id>.1`, `<request_id>.2` and so on, and
    /// [`wait_for_result`](Self::wait_for_result) and
    /// [`message_status`](Self::message_status) take them like those of
    /// single messages.
    ///
    /// The phone checks the whole group before it sends anything: a number
    /// that is not a phone number (`invalid_number`), a text that is too
    /// long, fewer messages left in the daily limit of the free version
    /// than the group has recipients (`quota_exceeded`) or more SMS parts
    /// than the 20 of one minute, parts times recipients
    /// (`invalid_request`), refuse the whole group. A refusal by the phone
    /// for one recipient does not stop the others; that member has the
    /// status `failed` in `messages`.
    ///
    /// `request_id` has at most 61 characters here, so that the member
    /// suffix fits, and works as for [`send`](Self::send): the same id with
    /// the same text and numbers is not sent again but answered with the
    /// stored members and their current status.
    pub fn send_group(
        &mut self,
        to: &[&str],
        body: &str,
        sim: Option<i64>,
        request_id: Option<&str>,
    ) -> Result<GroupSendResult> {
        if to.is_empty() || to.iter().any(|number| number.trim().is_empty()) {
            return Err(invalid("'to' must be a list of phone numbers"));
        }
        if to.len() > crate::MAX_GROUP_RECIPIENTS {
            return Err(invalid(format!(
                "'to' holds more than {} numbers",
                crate::MAX_GROUP_RECIPIENTS
            )));
        }
        if body.is_empty() {
            return Err(invalid("'body' must be a text"));
        }
        let request_id = match request_id {
            None => uuid::Uuid::new_v4().simple().to_string(),
            Some(given) if is_group_request_id(given) => given.to_owned(),
            Some(_) => {
                return Err(invalid(
                    "request_id of a group must be 1 to 61 characters of A-Z a-z 0-9 . _ -",
                ));
            }
        };
        let numbers: Vec<Value> = to.iter().map(|number| Value::from(*number)).collect();
        let mut fields = object([
            ("to", numbers.into()),
            ("body", body.into()),
            ("requestId", request_id.into()),
        ]);
        put(&mut fields, "sim", sim);
        self.call("messages.sendGroup", fields)
    }

    /// Return the outbox entry of a message sent through SMS Dock.
    pub fn message_status(&mut self, request_id: &str) -> Result<OutboxEntry> {
        self.call(
            "messages.status",
            object([("requestId", request_id.into())]),
        )
    }

    /// Wait until a sent message succeeded or failed.
    ///
    /// Returns the outbox entry once its `status` is `sent`, `delivered`
    /// or `failed`. Check `status` and `error_code` of the result; a
    /// failed message is no error. The error is [`Error::Timeout`] after
    /// `timeout` (default: the timeout of the client).
    pub fn wait_for_result(
        &mut self,
        request_id: &str,
        timeout: Option<Duration>,
    ) -> Result<OutboxEntry> {
        let options = WaitOptions {
            timeout,
            ..WaitOptions::default()
        };
        self.wait_for_result_with(request_id, &options)
    }

    /// Like [`wait_for_result`](Self::wait_for_result), with more options.
    ///
    /// With `delivered` the status `sent` is not enough: the method waits
    /// for the delivery report, which some networks never send.
    ///
    /// The method asks the server with `messages.status`: soon after the
    /// start, then less often, at last every `poll_interval`. If the
    /// connection is subscribed to `sms.sent`, `sms.delivered` and
    /// `sms.failed` it asks as soon as such an event arrives. The events
    /// stay in the queue.
    pub fn wait_for_result_with(
        &mut self,
        request_id: &str,
        options: &WaitOptions,
    ) -> Result<OutboxEntry> {
        if options.poll_interval.is_zero() {
            return Err(invalid("poll_interval must be positive"));
        }
        let deadline = Instant::now() + self.timeout_or_default(options.timeout);
        let mut pause = FIRST_POLL.min(options.poll_interval);
        loop {
            let entry = self.message_status(request_id)?;
            let done = match entry.status.as_str() {
                "delivered" | "failed" => true,
                "sent" => !options.delivered,
                _ => false,
            };
            if done {
                return Ok(entry);
            }
            let Some(remaining) = tls::remaining(deadline) else {
                return Err(Error::Timeout(format!(
                    "No result for the message '{request_id}' in time; its status is '{}'",
                    entry.status
                )));
            };
            self.wait_for_outcome_event(request_id, Instant::now() + pause.min(remaining))?;
            pause = (pause * 2).min(options.poll_interval);
        }
    }

    /// Delete one message.
    ///
    /// With mode `hidden` the message disappears from SMS Dock but stays
    /// on the phone. If the connection drops after the server deleted the
    /// message, the repeated request fails with `not_found`.
    pub fn delete_message(&mut self, message_id: i64) -> Result<DeleteResult> {
        self.call(
            "messages.delete",
            object([("messageId", message_id.into())]),
        )
    }

    /// Delete a conversation. `deleted` is the number of its messages.
    pub fn delete_conversation(&mut self, conversation_id: i64) -> Result<DeleteResult> {
        self.call(
            "conversations.delete",
            object([("conversationId", conversation_id.into())]),
        )
    }

    /// Mark all messages of a conversation as read.
    ///
    /// Android lets only the default SMS app change messages; otherwise
    /// the error is `not_default_app`.
    pub fn mark_read(&mut self, conversation_id: i64) -> Result<MarkReadResult> {
        self.call(
            "conversations.markRead",
            object([("conversationId", conversation_id.into())]),
        )
    }

    /// Return one page of contacts, sorted by name.
    ///
    /// `q` searches names and numbers.
    pub fn contacts(
        &mut self,
        q: Option<&str>,
        limit: u32,
        cursor: Option<&str>,
    ) -> Result<Page<Contact>> {
        let mut fields = JsonObject::new();
        put(&mut fields, "q", q);
        paging(&mut fields, limit, cursor)?;
        self.call("contacts.list", fields)
    }

    /// Iterate over contacts, sorted by name, page after page.
    pub fn iter_contacts(
        &mut self,
        q: Option<&str>,
        page_size: u32,
        max_items: Option<usize>,
    ) -> Result<Items<'_, Contact>> {
        let mut fields = JsonObject::new();
        put(&mut fields, "q", q);
        Items::new(self, "contacts.list", fields, page_size, max_items)
    }

    // ------------------------------------------------------------------
    // Events
    // ------------------------------------------------------------------

    /// Ask for events and return the subscription now in force.
    ///
    /// `events` holds event names, or `"*"` for all; the names are added
    /// to the subscription. `address` limits the `sms.*` events to one
    /// number and replaces the limit set before; without it the limit is
    /// removed. Numbers are compared as the phone does: `+43 699 1234567`
    /// and `0699 1234567` are the same.
    ///
    /// In the result `*` is replaced by the names it stands for. The
    /// client remembers the subscription and restores it after a
    /// reconnect.
    pub fn subscribe(&mut self, events: &[&str], address: Option<&str>) -> Result<Subscription> {
        let mut fields = object([("events", event_names(events)?)]);
        put(&mut fields, "address", address);
        let result = self.perform("subscribe", &fields, true, None)?;
        self.remember_subscription(result)
    }

    /// Stop events and return the subscription now in force.
    ///
    /// `events` holds event names, or `"*"` for all. When no name is
    /// left, the limit to one number is removed too.
    pub fn unsubscribe(&mut self, events: &[&str]) -> Result<Subscription> {
        let fields = object([("events", event_names(events)?)]);
        let result = self.perform("unsubscribe", &fields, true, None)?;
        self.remember_subscription(result)
    }

    /// Return the next event.
    ///
    /// Events that were queued while a request waited for its response
    /// come first. `timeout` is the longest time to wait; `Ok(None)`
    /// means it passed. `None` waits without limit. Subscribe first, a
    /// new connection gets no events.
    ///
    /// After a lost connection the method reconnects and goes on. The
    /// error is [`Error::ConnectionInterrupted`] when that fails.
    ///
    /// ```no_run
    /// # fn main() -> smsdock::Result<()> {
    /// # let mut dock = smsdock::SmsDockClient::new("wss://192.168.1.50:9443/ws")?;
    /// dock.subscribe(&["sms.received"], None)?;
    /// while let Some(event) = dock.next_event(None)? {
    ///     if let Some(message) = event.message() {
    ///         dock.send(&message.address, "All systems running.", message.sim, None)?;
    ///     }
    /// }
    /// # Ok(())
    /// # }
    /// ```
    pub fn next_event(&mut self, timeout: Option<Duration>) -> Result<Option<Event>> {
        if let Some(event) = self.events.pop_front() {
            return Ok(Some(event));
        }
        let deadline = timeout.map(|timeout| Instant::now() + timeout);
        let Some(event) = self.read_event(deadline, "waiting for events")? else {
            return Ok(None);
        };
        if self.events.is_empty() {
            return Ok(Some(event));
        }
        // Events queued by the reconnect hook are older than this one.
        self.queue_event(event);
        Ok(self.events.pop_front())
    }

    /// Iterate over events as they arrive; see
    /// [`next_event`](Self::next_event).
    ///
    /// The iteration ends when no event arrived for `timeout`, or after
    /// an error. The iterator borrows the client; use `next_event` in a
    /// loop to send requests between the events.
    pub fn events(&mut self, timeout: Option<Duration>) -> Events<'_> {
        Events {
            client: self,
            timeout,
            finished: false,
        }
    }

    /// Return the next event with one of these names.
    ///
    /// Other events stay in the queue. `"*"` stands for every name. The
    /// error is [`Error::Timeout`] after `timeout` (default: the timeout
    /// of the client).
    pub fn wait_for_event(&mut self, names: &[&str], timeout: Option<Duration>) -> Result<Event> {
        self.wait_for_event_where(names, timeout, |_| true)
    }

    /// Like [`wait_for_event`](Self::wait_for_event); `predicate` is
    /// called with each candidate and returns `true` for the wanted one.
    pub fn wait_for_event_where<F>(
        &mut self,
        names: &[&str],
        timeout: Option<Duration>,
        mut predicate: F,
    ) -> Result<Event>
    where
        F: FnMut(&Event) -> bool,
    {
        event_names(names)?;
        let mut wanted = |event: &Event| {
            (names.contains(&"*") || names.contains(&event.event.as_str())) && predicate(event)
        };
        if let Some(index) = self.events.iter().position(&mut wanted) {
            if let Some(event) = self.events.remove(index) {
                return Ok(event);
            }
        }
        let deadline = Instant::now() + self.timeout_or_default(timeout);
        let mut sorted = names.to_vec();
        sorted.sort_unstable();
        sorted.dedup();
        let label = sorted.join(", ");
        loop {
            let what = format!("waiting for {label}");
            let Some(event) = self.read_event(Some(deadline), &what)? else {
                return Err(Error::Timeout(format!("No event {label} in time")));
            };
            if wanted(&event) {
                return Ok(event);
            }
            self.queue_event(event);
        }
    }

    // ------------------------------------------------------------------
    // Internals
    // ------------------------------------------------------------------

    fn timeout_or_default(&self, timeout: Option<Duration>) -> Duration {
        timeout
            .filter(|timeout| !timeout.is_zero())
            .unwrap_or(self.timeout)
    }

    /// Perform an action of the API and read its result as a Rust type.
    fn call<T: DeserializeOwned>(&mut self, action: &str, fields: JsonObject) -> Result<T> {
        let result = self.perform(action, &fields, true, None)?;
        typed(Value::Object(result), action)
    }

    /// Send a request; reconnect and repeat it after a lost connection.
    fn perform(
        &mut self,
        action: &str,
        fields: &JsonObject,
        retry: bool,
        timeout: Option<Duration>,
    ) -> Result<JsonObject> {
        let timeout = self.timeout_or_default(timeout);
        let retries = if retry { self.rate_limit_retries } else { 0 };
        let what = format!("performing '{action}'");
        let mut attempt = 0;
        loop {
            let outcome = self
                .ensure_open()
                .and_then(|()| self.request_patiently(action, fields, timeout, retries));
            match outcome {
                Ok(result) => return Ok(result),
                Err(Failure::Error(error)) => return Err(error),
                Err(Failure::Transport(transport)) if !retry => {
                    self.close();
                    let message = format!("Connection interrupted while {what}");
                    return Err(final_error(&message, &transport));
                }
                Err(Failure::Transport(transport)) => {
                    self.recover(&what, &transport, attempt)?;
                    attempt += 1;
                }
            }
        }
    }

    fn ensure_open(&mut self) -> Attempt<()> {
        if self.socket.is_none() {
            self.open()?;
        }
        Ok(())
    }

    /// Connect, pin the certificate, log in, restore the subscription.
    fn open(&mut self) -> Attempt<()> {
        if self.insecure && !self.warned {
            self.warned = true;
            eprintln!(
                "smsdock: WARNING: the certificate of the server is not checked \
                 (insecure). Anyone on the network can read the password and the \
                 messages."
            );
        }
        let socket = self.dial()?;
        self.socket = Some(socket);
        self.connection_count += 1;
        self.quiet = Duration::ZERO;
        self.pinged = false;
        let reconnected = self.epoch.is_some();
        let mut outcome = self.handshake();
        if outcome.is_ok() && reconnected {
            outcome = self.run_hook().map_err(Failure::Error);
        }
        if outcome.is_err() {
            self.close();
        }
        outcome
    }

    fn run_hook(&mut self) -> Result<()> {
        let Some(mut hook) = self.on_reconnect.take() else {
            return Ok(());
        };
        let outcome = hook(self);
        if self.on_reconnect.is_none() {
            self.on_reconnect = Some(hook);
        }
        outcome
    }

    /// Open TCP, TLS and the WebSocket. The certificate is compared with
    /// the pin inside the TLS handshake, before the WebSocket request.
    fn dial(&self) -> Attempt<Socket> {
        let deadline = Instant::now() + self.connect_timeout;
        let tcp = tls::tcp_connect(&self.host, self.port, deadline)
            .map_err(|error| Transport::Broken(error.to_string()))?;
        let stream = match tls::handshake(tcp, &self.host, self.pin.as_ref(), deadline) {
            Ok(stream) => stream,
            Err(TlsFailure::Mismatch { expected, actual }) => {
                return Err(Error::CertificateMismatch { expected, actual }.into());
            }
            Err(TlsFailure::NoCertificate) if self.pin.is_some() => {
                return Err(Error::CertificateMismatch {
                    expected: self.pin.as_ref().map(format_pin).unwrap_or_default(),
                    actual: "no certificate".to_owned(),
                }
                .into());
            }
            Err(TlsFailure::NoCertificate) => {
                return Err(
                    Transport::Broken("the server presented no certificate".to_owned()).into(),
                );
            }
            Err(TlsFailure::TimedOut) => {
                return Err(
                    Transport::Timeout("timed out during the TLS handshake".to_owned()).into(),
                );
            }
            Err(TlsFailure::Other(cause)) => return Err(Transport::Broken(cause).into()),
        };
        let wait = tls::remaining(deadline).ok_or_else(|| {
            Transport::Timeout("timed out while opening the connection".to_owned())
        })?;
        set_timeouts(&stream, wait, wait)?;
        let config = WebSocketConfig::default()
            .max_message_size(Some(MAX_INCOMING_BYTES))
            .max_frame_size(Some(MAX_INCOMING_BYTES));
        let socket = match client_with_config(self.url.as_str(), stream, Some(config)) {
            Ok((socket, _)) => socket,
            Err(HandshakeError::Interrupted(_)) => {
                return Err(Transport::Timeout(
                    "timed out during the WebSocket handshake".to_owned(),
                )
                .into());
            }
            Err(HandshakeError::Failure(error)) => {
                return Err(Transport::Broken(error.to_string()).into());
            }
        };
        set_timeouts(socket.get_ref(), self.timeout, self.timeout)?;
        Ok(socket)
    }

    fn handshake(&mut self) -> Attempt<()> {
        let hello = self.receive(Some(Instant::now() + self.timeout))?;
        if hello.get("event").and_then(Value::as_str) != Some("hello") {
            return Err(
                protocol("Expected 'hello' from the server, received something else").into(),
            );
        }
        let version = hello.get("protocolVersion");
        if version.and_then(Value::as_i64) != Some(API_PROTOCOL_VERSION) {
            return Err(protocol(format!(
                "The server speaks protocol version {}, this client version \
                 {API_PROTOCOL_VERSION}",
                shown(version)
            ))
            .into());
        }
        let password = object([("password", self.password.clone().into())]);
        let result = self.request_patiently(
            "authenticate",
            &password,
            self.timeout,
            self.rate_limit_retries,
        )?;
        let version = result.get("protocolVersion");
        if version.and_then(Value::as_i64) != Some(API_PROTOCOL_VERSION) {
            return Err(protocol(format!(
                "The login answered with protocol version {}",
                shown(version)
            ))
            .into());
        }
        self.server_info = result
            .get("app")
            .filter(|app| app.is_object())
            .and_then(|app| serde_json::from_value(app.clone()).ok());
        self.note_login(result.get("epoch"), result.get("latestSeq"))?;
        let restore = self
            .subscription
            .as_ref()
            .filter(|subscription| !subscription.events.is_empty())
            .map(|subscription| {
                let mut fields = object([("events", Value::from(subscription.events.clone()))]);
                put(&mut fields, "address", subscription.address.as_deref());
                fields
            });
        if let Some(fields) = restore {
            self.request_patiently("subscribe", &fields, self.timeout, self.rate_limit_retries)?;
        }
        Ok(())
    }

    /// Compare epoch and sequence number with the connection before.
    fn note_login(&mut self, epoch: Option<&Value>, latest: Option<&Value>) -> Result<()> {
        let (Some(epoch), Some(latest)) = (integer(epoch), integer(latest)) else {
            return Err(protocol(
                "The login answered without 'epoch' and 'latestSeq'",
            ));
        };
        match self.epoch {
            None => {
                self.restarted = false;
                self.missed = Some(0);
            }
            Some(known) if known != epoch => {
                self.restarted = true;
                self.missed = None;
            }
            Some(_) => {
                let known = self.latest_seq.unwrap_or(0);
                self.restarted = false;
                self.missed = Some(u64::try_from(latest.saturating_sub(known)).unwrap_or(0));
            }
        }
        self.epoch = Some(epoch);
        self.latest_seq = Some(latest);
        Ok(())
    }

    /// Send a request; wait and repeat it while it is rate limited.
    ///
    /// Only when the server names the seconds to wait. The limit of
    /// messages per minute comes without them and is reported at once.
    fn request_patiently(
        &mut self,
        action: &str,
        fields: &JsonObject,
        timeout: Duration,
        retries: u32,
    ) -> Attempt<JsonObject> {
        let mut refused = 0;
        loop {
            let error = match self.request_once(action, fields, timeout) {
                Err(Failure::Error(error)) => error,
                other => return other,
            };
            let wait = match (error.code(), error.retry_after()) {
                (Some("rate_limited"), Some(wait)) => wait,
                _ => return Err(error.into()),
            };
            if wait as f64 > timeout.as_secs_f64() || refused >= retries {
                return Err(error.into());
            }
            refused += 1;
            pause(Duration::from_secs(u64::try_from(wait).unwrap_or(0)));
        }
    }

    /// Close after a failure and wait before the next attempt.
    ///
    /// Returns the error instead when the failure is final or no attempt
    /// is left.
    fn recover(&mut self, what: &str, transport: &Transport, attempt: u32) -> Result<()> {
        self.close();
        if let Some(fatal) = fatal_error(transport) {
            return Err(fatal);
        }
        if self
            .reconnect_attempts
            .is_some_and(|limit| attempt >= limit)
        {
            let message = format!("Connection interrupted while {what}");
            return Err(interrupted(&message, transport));
        }
        pause(backoff(
            self.reconnect_delay,
            self.max_reconnect_delay,
            attempt,
        ));
        Ok(())
    }

    fn request_once(
        &mut self,
        action: &str,
        fields: &JsonObject,
        timeout: Duration,
    ) -> Attempt<JsonObject> {
        let key = self.send_frame(action, fields)?;
        let label = format!("'{action}'");
        self.await_response(&key, &label, Instant::now() + timeout)
    }

    fn send_frame(&mut self, action: &str, fields: &JsonObject) -> Attempt<String> {
        if self.socket.is_none() {
            return Err(no_connection().into());
        }
        // Learn what arrived meanwhile; a close frame tells why the
        // server ended the connection and whether a new login is allowed.
        self.drain()?;
        self.request_number += 1;
        let key = format!("rs-{}", self.request_number);
        let mut payload = JsonObject::new();
        payload.insert("id".to_owned(), key.clone().into());
        payload.insert("action".to_owned(), action.into());
        for (name, value) in fields {
            payload.insert(name.clone(), value.clone());
        }
        let frame = Value::Object(payload).to_string();
        if frame.len() > MAX_FRAME_BYTES {
            return Err(invalid(format!(
                "The request is larger than {} KiB",
                MAX_FRAME_BYTES / 1024
            ))
            .into());
        }
        let socket = self.socket.as_mut().ok_or_else(no_connection)?;
        if let Err(error) = socket.send(Frame::text(frame)) {
            // The server may have said why it closed the connection.
            self.drain()?;
            return Err(broken(error).into());
        }
        self.outstanding.insert(key.clone());
        Ok(key)
    }

    fn await_response(&mut self, key: &str, label: &str, deadline: Instant) -> Attempt<JsonObject> {
        let late = || Transport::Timeout(format!("No response to {label} in time"));
        loop {
            if let Some(response) = self.responses.remove(key) {
                self.outstanding.remove(key);
                return response_result(response, label);
            }
            if tls::remaining(deadline).is_none() {
                return Err(late().into());
            }
            match self.receive(Some(deadline)) {
                Ok(message) => self.dispatch(message)?,
                Err(Failure::Transport(Transport::Timeout(_))) => return Err(late().into()),
                Err(failure) => return Err(failure),
            }
        }
    }

    /// Queue an event, or keep a response for the request that waits.
    fn dispatch(&mut self, message: JsonObject) -> Result<()> {
        if message.contains_key("event") {
            let event = to_event(message)?;
            self.queue_event(event);
        } else {
            self.file_response(message);
        }
        Ok(())
    }

    /// Keep a response for the request that waits for it.
    fn file_response(&mut self, response: JsonObject) {
        let mut key = match response.get("id") {
            None | Some(Value::Null) => None,
            Some(Value::String(text)) => Some(text.clone()),
            Some(other) => Some(other.to_string()),
        };
        if key.is_none() && self.outstanding.len() == 1 {
            // The server could not read the id of the only open request.
            key = self.outstanding.iter().next().cloned();
        }
        if let Some(key) = key.filter(|key| self.outstanding.contains(key)) {
            self.responses.insert(key, response);
        }
        // Anything else answers a request nobody waits for any more.
    }

    /// Read one JSON object from the server; `None` waits without limit.
    fn receive(&mut self, deadline: Option<Instant>) -> Attempt<JsonObject> {
        loop {
            let mut wait = READ_SLICE;
            if let Some(deadline) = deadline {
                let remaining = tls::remaining(deadline)
                    .ok_or_else(|| Transport::Timeout("timed out".to_owned()))?;
                wait = wait.min(remaining);
            }
            let socket = self.socket.as_ref().ok_or_else(no_connection)?;
            socket
                .get_ref()
                .sock
                .set_read_timeout(Some(wait.max(Duration::from_millis(1))))
                .map_err(|error| Transport::Broken(error.to_string()))?;
            let started = Instant::now();
            match self.read_once()? {
                Incoming::Text(text) => return Ok(self.decode(&text)?),
                Incoming::Control => {}
                Incoming::Nothing => self.keep_alive(started.elapsed())?,
            }
        }
    }

    /// Handle everything that has arrived already, without waiting.
    fn drain(&mut self) -> Attempt<()> {
        let Some(socket) = self.socket.as_ref() else {
            return Ok(());
        };
        if socket.get_ref().sock.set_nonblocking(true).is_err() {
            return Ok(());
        }
        let outcome = self.drain_ready();
        if let Some(socket) = self.socket.as_ref() {
            socket
                .get_ref()
                .sock
                .set_nonblocking(false)
                .map_err(|error| Transport::Broken(error.to_string()))?;
        }
        outcome
    }

    fn drain_ready(&mut self) -> Attempt<()> {
        loop {
            match self.read_once()? {
                Incoming::Text(text) => {
                    let message = self.decode(&text)?;
                    self.dispatch(message)?;
                }
                Incoming::Control => {}
                Incoming::Nothing => return Ok(()),
            }
        }
    }

    /// Read one WebSocket message. A read that runs into the timeout of
    /// the socket loses nothing: what arrived stays buffered.
    fn read_once(&mut self) -> Attempt<Incoming> {
        let socket = self.socket.as_mut().ok_or_else(no_connection)?;
        let message = match socket.read() {
            Ok(message) => message,
            Err(tungstenite::Error::Io(error)) if tls::is_timeout(&error) => {
                return Ok(Incoming::Nothing);
            }
            Err(error) => return Err(broken(error).into()),
        };
        self.quiet = Duration::ZERO;
        self.pinged = false;
        match message {
            Frame::Text(text) => Ok(Incoming::Text(text.as_str().to_owned())),
            Frame::Binary(_) => Err(protocol("The server sent a binary frame").into()),
            Frame::Close(frame) => Err(match frame {
                Some(frame) => Transport::Closed {
                    code: frame.code.into(),
                    reason: Some(frame.reason.as_str().to_owned()).filter(|text| !text.is_empty()),
                },
                None => Transport::Broken("closed by the server".to_owned()),
            }
            .into()),
            Frame::Ping(_) | Frame::Pong(_) | Frame::Frame(_) => {
                // Answer a ping now rather than with the next read.
                let socket = self.socket.as_mut().ok_or_else(no_connection)?;
                match socket.flush() {
                    Ok(()) => Ok(Incoming::Control),
                    Err(tungstenite::Error::Io(error)) if tls::is_timeout(&error) => {
                        Ok(Incoming::Control)
                    }
                    Err(error) => Err(broken(error).into()),
                }
            }
        }
    }

    /// Count the silence of the server; ask whether it is still there.
    fn keep_alive(&mut self, waited: Duration) -> Attempt<()> {
        self.quiet += waited;
        if self.quiet >= LOST_AFTER {
            return Err(Transport::Broken("the server does not answer any more".to_owned()).into());
        }
        if self.quiet >= PING_AFTER && !self.pinged {
            self.pinged = true;
            let socket = self.socket.as_mut().ok_or_else(no_connection)?;
            socket
                .send(Frame::Ping(Vec::new().into()))
                .map_err(broken)?;
        }
        Ok(())
    }

    fn decode(&mut self, text: &str) -> Result<JsonObject> {
        let decoded: Value =
            serde_json::from_str(text).map_err(|_| protocol("The server sent invalid JSON"))?;
        let Value::Object(message) = decoded else {
            return Err(protocol("The frame is not a JSON object"));
        };
        if message.contains_key("event") {
            if let Some(sequence) = integer(message.get("seq")) {
                if self.latest_seq.is_none_or(|known| sequence > known) {
                    self.latest_seq = Some(sequence);
                }
            }
        }
        Ok(message)
    }

    fn queue_event(&mut self, event: Event) {
        if self.events.len() >= self.event_queue_size {
            self.events.pop_front();
            self.dropped_events += 1;
        }
        self.events.push_back(event);
    }

    /// Read from the connection until an event arrives.
    ///
    /// Returns the event without queueing it, or `None` when the deadline
    /// passed. Reconnects when needed.
    fn read_event(&mut self, deadline: Option<Instant>, what: &str) -> Result<Option<Event>> {
        let mut attempt = 0;
        loop {
            match self.ensure_open() {
                Ok(()) => {}
                Err(Failure::Error(error)) => return Err(error),
                Err(Failure::Transport(transport)) => {
                    self.recover(what, &transport, attempt)?;
                    attempt += 1;
                    continue;
                }
            }
            if deadline.is_some_and(|deadline| tls::remaining(deadline).is_none()) {
                return Ok(None);
            }
            match self.receive(deadline) {
                Ok(message) => {
                    attempt = 0;
                    if message.contains_key("event") {
                        return to_event(message).map(Some);
                    }
                    self.file_response(message);
                }
                Err(Failure::Error(error)) => return Err(error),
                Err(Failure::Transport(Transport::Timeout(_))) => return Ok(None),
                Err(Failure::Transport(transport)) => {
                    self.recover(what, &transport, attempt)?;
                    attempt += 1;
                }
            }
        }
    }

    /// Return at the deadline, or earlier when an outcome arrives.
    fn wait_for_outcome_event(&mut self, request_id: &str, deadline: Instant) -> Result<()> {
        loop {
            let Some(event) = self.read_event(Some(deadline), "waiting for the result")? else {
                return Ok(());
            };
            let outcome = OUTCOME_EVENTS.contains(&event.event.as_str())
                && event.request_id() == Some(request_id);
            self.queue_event(event);
            if outcome {
                return Ok(());
            }
        }
    }

    fn remember_subscription(&mut self, result: JsonObject) -> Result<Subscription> {
        let subscription: Subscription = serde_json::from_value(Value::Object(result))
            .map_err(|_| protocol("Malformed subscription"))?;
        self.subscription = Some(subscription.clone());
        Ok(subscription)
    }
}

/// Iterator over the items of a list, page after page.
///
/// Returned by [`SmsDockClient::iter_messages`],
/// [`SmsDockClient::iter_conversations`] and
/// [`SmsDockClient::iter_contacts`]. It requests a page when it needs one
/// and ends after the first error.
pub struct Items<'a, T> {
    client: &'a mut SmsDockClient,
    action: &'static str,
    filters: JsonObject,
    page_size: u32,
    max_items: Option<usize>,
    cursor: Option<String>,
    count: usize,
    page: VecDeque<Value>,
    /// What ends the iteration once the page is used up.
    end: Option<Option<Error>>,
    marker: PhantomData<T>,
}

impl<'a, T> Items<'a, T> {
    fn new(
        client: &'a mut SmsDockClient,
        action: &'static str,
        filters: JsonObject,
        page_size: u32,
        max_items: Option<usize>,
    ) -> Result<Self> {
        check_limit(page_size)?;
        Ok(Self {
            client,
            action,
            filters,
            page_size,
            max_items,
            cursor: None,
            count: 0,
            page: VecDeque::new(),
            end: None,
            marker: PhantomData,
        })
    }

    fn finish(&mut self, error: Error) -> Option<Result<T>> {
        self.page.clear();
        self.end = Some(None);
        Some(Err(error))
    }

    /// Request the next page. Returns an error that ends the iteration.
    fn fetch(&mut self, left: Option<usize>) -> Result<()> {
        let limit = left.map_or(self.page_size, |left| {
            self.page_size.min(u32::try_from(left).unwrap_or(u32::MAX))
        });
        let mut fields = self.filters.clone();
        paging(&mut fields, limit, self.cursor.as_deref())?;
        let mut page = self.client.perform(self.action, &fields, true, None)?;
        let Some(Value::Array(items)) = page.remove("items") else {
            return Err(protocol(format!(
                "{} returned no list of items",
                self.action
            )));
        };
        self.page = items.into();
        match page.remove("nextCursor") {
            None | Some(Value::Null) => self.end = Some(None),
            Some(Value::String(next)) if Some(&next) != self.cursor.as_ref() => {
                self.cursor = Some(next);
            }
            Some(other) => {
                self.end = Some(Some(protocol(format!(
                    "{} returned the cursor {other}",
                    self.action
                ))));
            }
        }
        Ok(())
    }
}

impl<T: DeserializeOwned> Iterator for Items<'_, T> {
    type Item = Result<T>;

    fn next(&mut self) -> Option<Self::Item> {
        loop {
            let left = self.max_items.map(|max| max.saturating_sub(self.count));
            if left == Some(0) {
                return None;
            }
            if let Some(item) = self.page.pop_front() {
                self.count += 1;
                return match typed(item, self.action) {
                    Ok(item) => Some(Ok(item)),
                    Err(error) => self.finish(error),
                };
            }
            match self.end.take() {
                Some(None) => {
                    self.end = Some(None);
                    return None;
                }
                Some(Some(error)) => return self.finish(error),
                None => {}
            }
            if let Err(error) = self.fetch(left) {
                return self.finish(error);
            }
        }
    }
}

impl<T> fmt::Debug for Items<'_, T> {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        formatter
            .debug_struct("Items")
            .field("action", &self.action)
            .field("count", &self.count)
            .finish_non_exhaustive()
    }
}

/// Iterator over events; returned by [`SmsDockClient::events`].
#[derive(Debug)]
pub struct Events<'a> {
    client: &'a mut SmsDockClient,
    timeout: Option<Duration>,
    finished: bool,
}

impl Iterator for Events<'_> {
    type Item = Result<Event>;

    fn next(&mut self) -> Option<Self::Item> {
        if self.finished {
            return None;
        }
        let next = self.client.next_event(self.timeout).transpose();
        self.finished = !matches!(next, Some(Ok(_)));
        next
    }
}

fn invalid(message: impl Into<String>) -> Error {
    Error::InvalidArgument(message.into())
}

fn protocol(message: impl Into<String>) -> Error {
    Error::Protocol(message.into())
}

fn no_connection() -> Error {
    Error::ConnectionInterrupted {
        message: NO_CONNECTION.to_owned(),
        close_code: None,
        close_reason: None,
    }
}

fn broken(error: tungstenite::Error) -> Transport {
    match error {
        tungstenite::Error::ConnectionClosed | tungstenite::Error::AlreadyClosed => {
            Transport::Broken("the connection is closed".to_owned())
        }
        other => Transport::Broken(other.to_string()),
    }
}

/// Return the error for a close code that forbids a retry.
fn fatal_error(transport: &Transport) -> Option<Error> {
    let Transport::Closed { code, reason } = transport else {
        return None;
    };
    let text = |fallback: &str| reason.clone().unwrap_or_else(|| fallback.to_owned());
    match code {
        4401 => Some(Error::from_close_code(
            "not_authenticated",
            text("The server closed the connection: not logged in"),
            *code,
        )),
        4410 => Some(Error::from_close_code(
            "not_authenticated",
            text("The password was changed in the app"),
            *code,
        )),
        4403 => Some(Error::from_close_code(
            "licence_required",
            text("The API needs the full licence of the app"),
            *code,
        )),
        1009 => Some(protocol(
            "The server closed the connection: frame larger than 64 KiB",
        )),
        4400 => Some(protocol(match reason {
            Some(reason) => format!("The server closed the connection: protocol error ({reason})"),
            None => "The server closed the connection: protocol error".to_owned(),
        })),
        _ => None,
    }
}

fn interrupted(message: &str, transport: &Transport) -> Error {
    match transport {
        Transport::Closed { code, reason } => {
            let mut cause = format!("closed by the server with code {code}");
            if let Some(reason) = reason {
                cause.push_str(&format!(" ({reason})"));
            }
            Error::ConnectionInterrupted {
                message: format!("{message}: {cause}"),
                close_code: Some(*code),
                close_reason: reason.clone(),
            }
        }
        Transport::Timeout(cause) | Transport::Broken(cause) => Error::ConnectionInterrupted {
            message: format!("{message}: {cause}"),
            close_code: None,
            close_reason: None,
        },
    }
}

/// The error of a failure after which no new attempt is made.
fn final_error(message: &str, transport: &Transport) -> Error {
    fatal_error(transport).unwrap_or_else(|| interrupted(message, transport))
}

fn response_result(mut response: JsonObject, label: &str) -> Attempt<JsonObject> {
    let ok = response.get("ok").and_then(Value::as_bool);
    match (ok, response.remove("error")) {
        (Some(true), _) => match response.remove("result") {
            Some(Value::Object(result)) => Ok(result),
            _ => Err(protocol(format!("The result of {label} is not a JSON object")).into()),
        },
        (Some(false), Some(Value::Object(error))) => Err(Error::from_error_object(&error).into()),
        // The response itself is not shown: it may hold message texts.
        _ => Err(protocol(format!("Malformed response to {label}")).into()),
    }
}

fn to_event(message: JsonObject) -> Result<Event> {
    serde_json::from_value(Value::Object(message))
        .map_err(|_| protocol("The server sent a malformed event"))
}

/// Read a result as a Rust type. The cause is left out of the error: it
/// could quote a message text.
fn typed<T: DeserializeOwned>(value: Value, action: &str) -> Result<T> {
    serde_json::from_value(value).map_err(|_| {
        protocol(format!(
            "The result of '{action}' does not look like protocol version \
             {API_PROTOCOL_VERSION}"
        ))
    })
}

fn request_fields(fields: Value) -> Result<JsonObject> {
    let fields = match fields {
        Value::Null => JsonObject::new(),
        Value::Object(fields) => fields,
        _ => return Err(invalid("The fields of a request must be a JSON object")),
    };
    if fields.contains_key("id") {
        return Err(invalid(ID_HELP));
    }
    Ok(fields)
}

fn parse_url(url: &str) -> Result<(String, u16)> {
    let uri: Uri = url.parse().map_err(|_| invalid(URL_HELP))?;
    let secure = uri
        .scheme_str()
        .is_some_and(|scheme| scheme.eq_ignore_ascii_case("wss"));
    let host = uri
        .host()
        .unwrap_or("")
        .trim_start_matches('[')
        .trim_end_matches(']');
    if !secure || host.is_empty() {
        return Err(invalid(URL_HELP));
    }
    Ok((host.to_owned(), uri.port_u16().unwrap_or(443)))
}

fn set_timeouts(stream: &TlsStream, read: Duration, write: Duration) -> Attempt<()> {
    stream
        .sock
        .set_read_timeout(Some(read))
        .and_then(|()| stream.sock.set_write_timeout(Some(write)))
        .map_err(|error| Transport::Broken(error.to_string()).into())
}

fn format_pin(pin: &Digest32) -> String {
    let pairs: Vec<String> = pin.iter().map(|byte| format!("{byte:02X}")).collect();
    pairs.join(":")
}

fn object<const N: usize>(fields: [(&str, Value); N]) -> JsonObject {
    fields
        .into_iter()
        .map(|(name, value)| (name.to_owned(), value))
        .collect()
}

fn put<T: Into<Value>>(fields: &mut JsonObject, name: &str, value: Option<T>) {
    if let Some(value) = value {
        fields.insert(name.to_owned(), value.into());
    }
}

fn check_limit(limit: u32) -> Result<()> {
    if (1..=MAX_PAGE_SIZE).contains(&limit) {
        Ok(())
    } else {
        Err(invalid(format!(
            "limit must be between 1 and {MAX_PAGE_SIZE}"
        )))
    }
}

fn paging(fields: &mut JsonObject, limit: u32, cursor: Option<&str>) -> Result<()> {
    check_limit(limit)?;
    fields.insert("limit".to_owned(), limit.into());
    put(fields, "cursor", cursor);
    Ok(())
}

fn event_names(events: &[&str]) -> Result<Value> {
    if events.is_empty() || events.iter().any(|name| name.is_empty()) {
        return Err(invalid("events must be one event name or several"));
    }
    Ok(Value::from(events.to_vec()))
}

fn is_request_id(text: &str) -> bool {
    (1..=64).contains(&text.len())
        && text
            .bytes()
            .all(|byte| byte.is_ascii_alphanumeric() || matches!(byte, b'.' | b'_' | b'-'))
}

/// Like [`is_request_id`], but short enough for the member suffix `.NN`.
fn is_group_request_id(text: &str) -> bool {
    text.len() <= 61 && is_request_id(text)
}

fn integer(value: Option<&Value>) -> Option<i64> {
    match value {
        Some(Value::Number(number)) => number.as_i64(),
        _ => None,
    }
}

/// A value of the server for an error message.
fn shown(value: Option<&Value>) -> String {
    match value {
        None | Some(Value::Null) => "None".to_owned(),
        Some(Value::Number(number)) => number.to_string(),
        Some(_) => "of an unexpected kind".to_owned(),
    }
}

fn millis(moment: SystemTime) -> i64 {
    match moment.duration_since(UNIX_EPOCH) {
        Ok(after) => i64::try_from(after.as_millis()).unwrap_or(i64::MAX),
        Err(before) => -i64::try_from(before.duration().as_millis()).unwrap_or(i64::MAX),
    }
}

fn backoff(delay: Duration, limit: Duration, attempt: u32) -> Duration {
    let factor = 1_u32 << attempt.min(MAX_BACKOFF_EXPONENT);
    delay.saturating_mul(factor).min(limit)
}

fn pause(time: Duration) {
    if !time.is_zero() {
        thread::sleep(time);
    }
}

#[cfg(test)]
mod tests {
    use serde_json::json;

    use super::*;

    const URL: &str = "wss://192.168.1.50:9443/ws";

    fn fingerprint() -> String {
        vec!["AB"; 32].join(":")
    }

    fn builder() -> ClientBuilder {
        SmsDockClient::builder(URL)
            .password("secret123")
            .fingerprint(fingerprint())
    }

    fn message_of(result: Result<SmsDockClient>) -> String {
        match result {
            Err(Error::InvalidArgument(message)) => message,
            other => panic!("expected InvalidArgument, got {other:?}"),
        }
    }

    #[test]
    fn refuses_everything_but_wss() {
        for url in [
            "ws://192.168.1.50:9443/ws",
            "http://192.168.1.50:9443/ws",
            "https://192.168.1.50:9443/ws",
            "192.168.1.50:9443",
            "wss:///ws",
            "",
        ] {
            let result = SmsDockClient::builder(url)
                .password("secret123")
                .fingerprint(fingerprint())
                .build();
            assert!(message_of(result).contains("wss://"), "{url}");
        }
    }

    #[test]
    fn reads_host_and_port_from_the_url() {
        assert_eq!(parse_url(URL).unwrap(), ("192.168.1.50".to_owned(), 9443));
        assert_eq!(
            parse_url("WSS://phone.local/ws").unwrap(),
            ("phone.local".to_owned(), 443)
        );
        assert_eq!(
            parse_url("wss://[fe80::1]:9443/ws").unwrap(),
            ("fe80::1".to_owned(), 9443)
        );
    }

    #[test]
    fn needs_a_fingerprint_unless_insecure() {
        // An explicit empty fingerprint is malformed, whatever the
        // environment holds.
        let result = SmsDockClient::builder(URL)
            .password("secret123")
            .fingerprint("")
            .build();
        assert!(message_of(result).contains("SHA-256"));
        let client = SmsDockClient::builder(URL)
            .password("secret123")
            .insecure(true)
            .build()
            .unwrap();
        assert!(client.insecure() && client.pin.is_none());
    }

    #[test]
    fn refuses_malformed_fingerprints() {
        for text in [
            "AB:CD",
            &"zz".repeat(32),
            &"AB".repeat(31),
            &"AB".repeat(33),
        ] {
            let result = SmsDockClient::builder(URL)
                .password("secret123")
                .fingerprint(text)
                .build();
            assert!(message_of(result).contains("SHA-256"), "{text}");
        }
        let client = builder().build().unwrap();
        assert_eq!(client.pin, Some([0xAB; 32]));
    }

    #[test]
    fn refuses_impossible_options() {
        assert!(builder().timeout(Duration::ZERO).build().is_err());
        assert!(builder().connect_timeout(Duration::ZERO).build().is_err());
        assert!(builder().event_queue_size(0).build().is_err());
        assert!(builder().password("").build().is_err());
    }

    #[test]
    fn a_new_client_knows_nothing_yet() {
        let client = builder().build().unwrap();
        assert!(!client.connected());
        assert_eq!(client.epoch(), None);
        assert_eq!(client.latest_seq(), None);
        assert_eq!(client.missed_events(), Some(0));
        assert!(!client.server_restarted());
        assert_eq!(client.pending_events(), 0);
        assert_eq!(client.connection_count(), 0);
        assert!(client.subscription().is_none() && client.server_info().is_none());
    }

    #[test]
    fn debug_output_does_not_show_the_password() {
        let secret = builder().password("very-secret");
        assert!(!format!("{secret:?}").contains("very-secret"));
        let client = secret.build().unwrap();
        let text = format!("{client:?}");
        assert!(!text.contains("very-secret"));
        assert!(text.contains(URL));
    }

    #[test]
    fn backoff_is_bounded() {
        let second = Duration::from_secs(1);
        let limit = Duration::from_secs(10);
        let delays: Vec<u64> = (0..6)
            .map(|attempt| backoff(second, limit, attempt).as_secs())
            .collect();
        assert_eq!(delays, [1, 2, 4, 8, 10, 10]);
        assert_eq!(backoff(second, limit, 1000), limit);
        assert_eq!(backoff(Duration::ZERO, limit, 3), Duration::ZERO);
    }

    #[test]
    fn close_codes_that_forbid_a_retry() {
        let closed = |code| Transport::Closed { code, reason: None };
        for (code, api_code) in [
            (4401, "not_authenticated"),
            (4410, "not_authenticated"),
            (4403, "licence_required"),
        ] {
            let error = fatal_error(&closed(code)).unwrap();
            assert_eq!(error.code(), Some(api_code));
            assert_eq!(error.close_code(), Some(code));
        }
        assert!(
            matches!(fatal_error(&closed(1009)), Some(Error::Protocol(m)) if m.contains("64 KiB"))
        );
        let binary = Transport::Closed {
            code: 4400,
            reason: Some("binary frame".to_owned()),
        };
        assert!(
            matches!(fatal_error(&binary), Some(Error::Protocol(m)) if m.contains("binary frame"))
        );
        for code in [1000, 1001, 4408, 4409, 4429] {
            assert!(fatal_error(&closed(code)).is_none(), "{code}");
        }
        assert!(fatal_error(&Transport::Broken("reset".to_owned())).is_none());
        assert!(fatal_error(&Transport::Timeout("late".to_owned())).is_none());
    }

    #[test]
    fn interrupted_names_the_close_frame() {
        let transport = Transport::Closed {
            code: 4409,
            reason: Some("too many clients".to_owned()),
        };
        let error = interrupted("Cannot connect", &transport);
        assert_eq!(
            error.to_string(),
            "Cannot connect: closed by the server with code 4409 (too many clients)"
        );
        assert_eq!(error.close_code(), Some(4409));
    }

    #[test]
    fn filters_become_fields() {
        let filter = MessageFilter::new()
            .address("+4369912345678")
            .conversation_id(7)
            .direction(Direction::In)
            .q("Anna 1")
            .since(1)
            .until(2)
            .sim(2)
            .unread(true);
        assert_eq!(
            Value::Object(filter.fields()).to_string(),
            r#"{"address":"+4369912345678","conversationId":7,"direction":"in","q":"Anna 1","from":1,"to":2,"sim":2,"unread":true}"#
        );
        assert!(MessageFilter::new().fields().is_empty());
        assert!(MessageFilter::new().unread(false).fields().is_empty());
        let filter = ConversationFilter::new().q("x").unread(true).sim(1);
        assert_eq!(
            Value::Object(filter.fields()),
            json!({"q": "x", "unread": true, "sim": 1})
        );
        let moment = UNIX_EPOCH + Duration::from_millis(1_790_672_554_000);
        assert_eq!(
            MessageFilter::new().since_time(moment).since,
            Some(1_790_672_554_000)
        );
    }

    #[test]
    fn limits_and_request_ids() {
        for limit in [0, 201, 500] {
            assert!(check_limit(limit).is_err());
        }
        assert!(check_limit(1).is_ok() && check_limit(200).is_ok());
        for id in ["", "a b", "ä", &"x".repeat(65), "a/b"] {
            assert!(!is_request_id(id), "{id}");
        }
        assert!(is_request_id("order-42.confirm_1") && is_request_id(&"x".repeat(64)));
        assert!(event_names(&[]).is_err() && event_names(&[""]).is_err());
    }

    #[test]
    fn the_request_id_belongs_to_the_client() {
        assert!(request_fields(json!({"id": 1})).is_err());
        assert!(request_fields(json!([1])).is_err());
        assert!(request_fields(Value::Null).unwrap().is_empty());
    }
}
