//! A fake SMS Dock server that speaks protocol version 1 of `API.md`.
//!
//! It keeps all data in memory and sends nothing. The tests talk to it
//! over real TLS with a throwaway certificate made by `rcgen`. It is a
//! port of `python/tests/fake_server.py`; where `API.md` leaves something
//! open, a comment names the assumption.

#![allow(dead_code)]

use std::collections::{HashMap, HashSet, VecDeque};
use std::net::{Shutdown, TcpListener, TcpStream};
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{Arc, Mutex, MutexGuard, OnceLock};
use std::thread::{self, JoinHandle};
use std::time::{Duration, Instant, SystemTime, UNIX_EPOCH};

use rustls::pki_types::{CertificateDer, PrivateKeyDer, PrivatePkcs8KeyDer};
use rustls::{ServerConfig, ServerConnection, StreamOwned};
use serde_json::{json, Map, Value};
use sha2::{Digest, Sha256};
use smsdock::{ClientBuilder, SmsDockClient};
use tungstenite::handshake::server::{ErrorResponse, Request, Response};
use tungstenite::http::StatusCode;
use tungstenite::protocol::frame::coding::CloseCode;
use tungstenite::protocol::{CloseFrame, WebSocketConfig};
use tungstenite::{Message, WebSocket};

pub const PASSWORD: &str = "secret123";
pub const BASE_TIME: i64 = 1_790_672_531_000;
pub const ANNA: &str = "+4369912345678";
pub const BEN: &str = "+4369087654321";
pub const EVENT_NAMES: [&str; 7] = [
    "sms.received",
    "sms.queued",
    "sms.sent",
    "sms.delivered",
    "sms.failed",
    "sms.deleted",
    "conversations.changed",
];

const MAX_FRAME_BYTES: usize = 64 * 1024;
const TICK: Duration = Duration::from_millis(5);

type JsonObject = Map<String, Value>;
type Socket = WebSocket<StreamOwned<ServerConnection, TcpStream>>;

/// A throwaway self-signed certificate.
pub struct Certificate {
    der: Vec<u8>,
    key: Vec<u8>,
    /// SHA-256 of the DER certificate as `AB:CD:…`, computed here and not
    /// by the library under test.
    pub fingerprint: String,
}

impl Certificate {
    fn create() -> Self {
        let made = rcgen::generate_simple_self_signed(vec!["smsdock.test".to_owned()])
            .expect("a self-signed certificate");
        let der = made.cert.der().to_vec();
        let pairs: Vec<String> = Sha256::digest(&der)
            .iter()
            .map(|byte| format!("{byte:02X}"))
            .collect();
        Self {
            der,
            key: made.signing_key.serialize_der(),
            fingerprint: pairs.join(":"),
        }
    }

    fn server_config(&self) -> Arc<ServerConfig> {
        let provider = Arc::new(rustls::crypto::ring::default_provider());
        let key = PrivateKeyDer::Pkcs8(PrivatePkcs8KeyDer::from(self.key.clone()));
        let config = ServerConfig::builder_with_provider(provider)
            .with_safe_default_protocol_versions()
            .expect("protocol versions")
            .with_no_client_auth()
            .with_single_cert(vec![CertificateDer::from(self.der.clone())], key)
            .expect("a usable certificate");
        Arc::new(config)
    }
}

/// Certificate of the fake server.
pub fn certificate() -> &'static Certificate {
    static CERTIFICATE: OnceLock<Certificate> = OnceLock::new();
    CERTIFICATE.get_or_init(Certificate::create)
}

/// A second certificate that the client does not pin.
pub fn other_certificate() -> &'static Certificate {
    static CERTIFICATE: OnceLock<Certificate> = OnceLock::new();
    CERTIFICATE.get_or_init(Certificate::create)
}

/// An error response, optionally followed by a close frame.
#[derive(Debug, Clone)]
struct Failure {
    error: Value,
    close_code: Option<u16>,
}

impl Failure {
    fn new(code: &str, message: impl Into<String>) -> Self {
        Self {
            error: json!({"code": code, "message": message.into()}),
            close_code: None,
        }
    }

    fn closing(mut self, close_code: u16) -> Self {
        self.close_code = Some(close_code);
        self
    }

    fn with(mut self, extra: Value) -> Self {
        if let (Some(error), Value::Object(extra)) = (self.error.as_object_mut(), extra) {
            error.extend(extra);
        }
        self
    }
}

/// What a connection thread sends next.
enum Outgoing {
    Text(String),
    Close(u16, String),
    Abort,
}

struct Client {
    id: u64,
    authenticated: bool,
    events: HashSet<String>,
    address: Option<String>,
    request_times: VecDeque<Instant>,
    refused: usize,
    outbox: VecDeque<Outgoing>,
    /// The connection is being closed; it counts no longer.
    closing: bool,
}

/// Options of a fake server.
#[derive(Clone)]
pub struct Options {
    pub password: String,
    pub port: u16,
    pub auth_timeout: Duration,
    pub max_clients: usize,
    pub send_delay: Duration,
    pub delivery_reports: bool,
    pub requests_per_second: usize,
    pub max_refused: usize,
    /// Present the second certificate.
    pub other_certificate: bool,
    /// Store the 29 messages of [`fill`].
    pub fill: bool,
}

impl Default for Options {
    fn default() -> Self {
        Self {
            password: PASSWORD.to_owned(),
            port: 0,
            auth_timeout: Duration::from_secs(10),
            max_clients: 8,
            send_delay: Duration::from_millis(20),
            delivery_reports: false,
            requests_per_second: 20,
            max_refused: 20,
            other_certificate: false,
            fill: true,
        }
    }
}

/// Everything the server knows; tests may read and change it through
/// [`FakeServer::state`].
pub struct State {
    options: Options,
    pub epoch: i64,
    pub protocol_version: i64,
    pub licence_enforced: bool,
    pub licensed: bool,
    pub delete_mode: String,
    failed_logins: u32,
    lock_count: u32,
    locked_until: Option<Instant>,
    sims: Vec<Value>,
    pub contacts: Vec<Value>,
    pub messages: Vec<Value>,
    pub outbox: JsonObject,
    pub fail_numbers: HashSet<String>,
    /// Messages handed to the (imaginary) radio, one entry per SMS.
    pub sent: Vec<Value>,
    /// Every request object received and not refused, in order.
    pub requests: Vec<Value>,
    /// Number of frames answered with `rate_limited` by the limiter.
    pub refused_count: usize,
    pub connection_count: usize,
    pub login_count: usize,
    next_message_id: i64,
    next_client_id: u64,
    conversation_ids: Vec<(String, i64)>,
    bodies: HashMap<String, String>,
    sequence: i64,
    clients: Vec<Client>,
    drops: HashMap<String, VecDeque<bool>>,
    failures: HashMap<String, VecDeque<Failure>>,
    results: HashMap<String, VecDeque<Value>>,
    ignored: HashMap<String, usize>,
    frames_before_response: Vec<String>,
    hold: usize,
    held: Vec<String>,
}

impl State {
    fn new(options: Options) -> Self {
        Self {
            options,
            epoch: now_millis(),
            protocol_version: 1,
            licence_enforced: false,
            licensed: false,
            delete_mode: "hidden".to_owned(),
            failed_logins: 0,
            lock_count: 0,
            locked_until: None,
            sims: vec![
                json!({"subscriptionId": 1, "slot": 0, "name": "Play",
                       "number": null, "isDefault": true}),
                json!({"subscriptionId": 2, "slot": 1, "name": "Work",
                       "number": BEN, "isDefault": false}),
            ],
            contacts: Vec::new(),
            messages: Vec::new(),
            outbox: JsonObject::new(),
            fail_numbers: HashSet::new(),
            sent: Vec::new(),
            requests: Vec::new(),
            refused_count: 0,
            connection_count: 0,
            login_count: 0,
            next_message_id: 2800,
            next_client_id: 0,
            conversation_ids: Vec::new(),
            bodies: HashMap::new(),
            sequence: 0,
            clients: Vec::new(),
            drops: HashMap::new(),
            failures: HashMap::new(),
            results: HashMap::new(),
            ignored: HashMap::new(),
            frames_before_response: Vec::new(),
            hold: 0,
            held: Vec::new(),
        }
    }

    /// Number of open connections.
    pub fn client_count(&self) -> usize {
        self.clients.iter().filter(|client| !client.closing).count()
    }

    /// The actions the server received, in order.
    pub fn actions(&self) -> Vec<String> {
        self.requests
            .iter()
            .map(|request| request["action"].as_str().unwrap_or("").to_owned())
            .collect()
    }

    /// The requests with this action, in order.
    pub fn requests_of(&self, action: &str) -> Vec<Value> {
        self.requests
            .iter()
            .filter(|request| request["action"] == action)
            .cloned()
            .collect()
    }

    /// Add a contact with one number.
    pub fn add_contact(&mut self, name: &str, number: &str, label: &str) {
        self.contacts.push(json!({
            "name": name, "numbers": [{"number": number, "label": label}],
        }));
    }

    /// Store a message without sending an event.
    #[allow(clippy::too_many_arguments)]
    pub fn add_message(
        &mut self,
        address: &str,
        body: &str,
        direction: &str,
        timestamp: Option<i64>,
        read: bool,
        sim: Option<i64>,
        request_id: Option<&str>,
    ) -> Value {
        let timestamp = timestamp.unwrap_or_else(now_millis);
        let incoming = direction == "in";
        self.next_message_id += 1;
        let message = json!({
            "id": self.next_message_id,
            "conversationId": self.conversation_id(address),
            "address": address,
            "contact": self.contact_name(address),
            "direction": direction,
            "body": body,
            "timestamp": timestamp,
            "sentTimestamp": if incoming { json!(timestamp - 2000) } else { Value::Null },
            "read": read,
            "status": if incoming { "received" } else { "sent" },
            "sim": sim,
            "requestId": request_id,
        });
        self.messages.push(message.clone());
        message
    }

    fn client(&mut self, id: u64) -> Option<&mut Client> {
        self.clients.iter_mut().find(|client| client.id == id)
    }

    fn push(&mut self, id: u64, outgoing: Outgoing) {
        if let Some(client) = self.client(id) {
            if matches!(outgoing, Outgoing::Close(..) | Outgoing::Abort) {
                client.closing = true;
            }
            client.outbox.push_back(outgoing);
        }
    }

    fn send(&mut self, id: u64, frame: String) {
        self.push(id, Outgoing::Text(frame));
    }

    fn respond(&mut self, id: u64, response: Value) {
        for frame in std::mem::take(&mut self.frames_before_response) {
            self.send(id, frame);
        }
        if self.hold > 0 {
            self.held.push(response.to_string());
            if self.held.len() < self.hold {
                return;
            }
            self.hold = 0;
            for frame in std::mem::take(&mut self.held).into_iter().rev() {
                self.send(id, frame);
            }
            return;
        }
        self.send(id, response.to_string());
    }

    fn drop_all(&mut self, code: Option<u16>, reason: &str) {
        let ids: Vec<u64> = self.clients.iter().map(|client| client.id).collect();
        for id in ids {
            match code {
                Some(code) => self.push(id, Outgoing::Close(code, reason.to_owned())),
                None => self.push(id, Outgoing::Abort),
            }
        }
    }

    fn emit(&mut self, name: &str, data: Value) {
        self.sequence += 1;
        let event = json!({"event": name, "seq": self.sequence, "data": data}).to_string();
        let wanting: Vec<u64> = self
            .clients
            .iter()
            .filter(|client| client.authenticated && !client.closing && wants(client, name, &data))
            .map(|client| client.id)
            .collect();
        for id in wanting {
            self.send(id, event.clone());
        }
    }

    /// Count a frame; at most `requests_per_second` fit in a second.
    fn within_rate(&mut self, id: u64) -> bool {
        let limit = self.options.requests_per_second;
        let Some(client) = self.client(id) else {
            return true;
        };
        let now = Instant::now();
        while client
            .request_times
            .front()
            .is_some_and(|first| now.duration_since(*first) >= Duration::from_secs(1))
        {
            client.request_times.pop_front();
        }
        if client.request_times.len() >= limit {
            return false;
        }
        client.request_times.push_back(now);
        true
    }

    fn conversation_id(&mut self, address: &str) -> i64 {
        for (known, identifier) in &self.conversation_ids {
            if same_peer(known, address) {
                return *identifier;
            }
        }
        let identifier = 170 + self.conversation_ids.len() as i64;
        self.conversation_ids.push((address.to_owned(), identifier));
        identifier
    }

    fn contact_name(&self, address: &str) -> Value {
        for contact in &self.contacts {
            for number in contact["numbers"].as_array().into_iter().flatten() {
                if same_peer(number["number"].as_str().unwrap_or(""), address) {
                    return contact["name"].clone();
                }
            }
        }
        Value::Null
    }

    fn conversations(&self) -> Vec<Value> {
        let mut order: Vec<i64> = Vec::new();
        let mut grouped: HashMap<i64, Vec<&Value>> = HashMap::new();
        for message in &self.messages {
            let identifier = message["conversationId"].as_i64().unwrap_or(0);
            if !grouped.contains_key(&identifier) {
                order.push(identifier);
            }
            grouped.entry(identifier).or_default().push(message);
        }
        let mut items: Vec<Value> = order
            .iter()
            .map(|identifier| {
                let messages = &grouped[identifier];
                let newest = messages
                    .iter()
                    .max_by_key(|m| sort_key(m))
                    .expect("a message");
                let snippet: String = text(newest, "body").chars().take(160).collect();
                json!({
                    "id": identifier,
                    "address": newest["address"],
                    "contact": newest["contact"],
                    "snippet": snippet,
                    "timestamp": newest["timestamp"],
                    "direction": newest["direction"],
                    "messageCount": messages.len(),
                    "unreadCount": messages.iter().filter(|m| m["read"] == false).count(),
                    "sim": newest["sim"],
                })
            })
            .collect();
        items.sort_by_key(|item| std::cmp::Reverse(sort_key(item)));
        items
    }

    // ------------------------------------------------------------------
    // Actions
    // ------------------------------------------------------------------

    fn authenticate(&mut self, id: u64, request: &JsonObject) -> Result<Value, Failure> {
        self.login_count += 1;
        // Assumption, as in the app: the licence is looked at before the
        // password, so a login without licence never counts as wrong.
        if self.licence_enforced && !self.licensed {
            return Err(Failure::new(
                "licence_required",
                "The API needs the full licence of SMS Dock.",
            )
            .closing(4403));
        }
        let now = Instant::now();
        if let Some(until) = self.locked_until.filter(|until| *until > now) {
            let remaining = (until - now).as_secs_f64().ceil() as u64;
            return Err(Failure::new(
                "locked_out",
                format!("Too many attempts. Try again in {remaining} seconds."),
            )
            .closing(4401)
            .with(json!({"retryAfter": remaining})));
        }
        if request.get("password").and_then(Value::as_str) != Some(self.options.password.as_str()) {
            self.failed_logins += 1;
            if self.failed_logins >= 5 {
                let seconds = (30_u64 << self.lock_count.min(10)).min(900);
                self.lock_count += 1;
                self.failed_logins = 0;
                self.locked_until = Some(now + Duration::from_secs(seconds));
            }
            return Err(Failure::new("invalid_password", "Wrong password.").closing(4401));
        }
        self.failed_logins = 0;
        self.lock_count = 0;
        if let Some(client) = self.client(id) {
            client.authenticated = true;
        }
        Ok(json!({
            "protocolVersion": self.protocol_version,
            "app": {"name": "SMS Dock", "version": "1.0.0",
                    "protocolVersion": self.protocol_version},
            "latestSeq": self.sequence,
            "epoch": self.epoch,
        }))
    }

    fn status(&self) -> Value {
        json!({
            "app": {"name": "SMS Dock", "version": "1.0.0",
                    "protocolVersion": self.protocol_version},
            "sims": self.sims,
            "permissions": {
                "readSms": true, "sendSms": true, "receiveSms": true,
                "contacts": true, "phoneState": true, "notifications": true,
            },
            "defaultSmsApp": self.delete_mode == "provider",
            "deleteMode": self.delete_mode,
            "licence": {
                "enforced": self.licence_enforced,
                "licensed": self.licensed,
                "dailyLimit": 10,
                "sentToday": self.sent.len(),
                "remaining": null,
            },
            "popup": true,
        })
    }

    fn conversations_list(&self, request: &JsonObject) -> Result<Value, Failure> {
        let query = optional_text(request, "q")?;
        let unread = optional_flag(request, "unread")?.unwrap_or(false);
        let sim = optional_integer(request, "sim")?;
        let mut items = self.conversations();
        if let Some(query) = query.filter(|query| !query.is_empty()) {
            let needle = query.to_lowercase();
            let matching: HashSet<i64> = self
                .messages
                .iter()
                .filter(|message| text(message, "body").to_lowercase().contains(&needle))
                .filter_map(|message| message["conversationId"].as_i64())
                .collect();
            items.retain(|item| {
                matching.contains(&item["id"].as_i64().unwrap_or(0))
                    || text(item, "address").to_lowercase().contains(&needle)
                    || text(item, "contact").to_lowercase().contains(&needle)
            });
        }
        if unread {
            items.retain(|item| item["unreadCount"].as_i64().unwrap_or(0) > 0);
        }
        if let Some(sim) = sim {
            items.retain(|item| item["sim"] == sim);
        }
        page(items, request, false)
    }

    fn messages_list(&self, request: &JsonObject) -> Result<Value, Failure> {
        let address = optional_text(request, "address")?;
        let conversation = optional_integer(request, "conversationId")?;
        let direction = optional_text(request, "direction")?.unwrap_or("all");
        let query = optional_text(request, "q")?;
        let start = optional_integer(request, "from")?;
        let end = optional_integer(request, "to")?;
        let sim = optional_integer(request, "sim")?;
        let unread = optional_flag(request, "unread")?.unwrap_or(false);
        if !["in", "out", "all"].contains(&direction) {
            return Err(Failure::new(
                "invalid_request",
                "'direction' must be 'in', 'out' or 'all'.",
            ));
        }
        let mut items = self.messages.clone();
        items.sort_by_key(|item| std::cmp::Reverse(sort_key(item)));
        if let Some(conversation) = conversation {
            // conversationId wins if address is given too.
            items.retain(|m| m["conversationId"] == conversation);
        } else if let Some(address) = address {
            items.retain(|m| same_peer(text(m, "address"), address));
        }
        if direction != "all" {
            items.retain(|m| m["direction"] == direction);
        }
        if let Some(query) = query.filter(|query| !query.is_empty()) {
            let needle = query.to_lowercase();
            items.retain(|m| text(m, "body").to_lowercase().contains(&needle));
        }
        if let Some(start) = start {
            items.retain(|m| m["timestamp"].as_i64().unwrap_or(0) >= start);
        }
        if let Some(end) = end {
            items.retain(|m| m["timestamp"].as_i64().unwrap_or(0) <= end);
        }
        if let Some(sim) = sim {
            items.retain(|m| m["sim"] == sim);
        }
        if unread {
            items.retain(|m| m["read"] == false);
        }
        page(items, request, false)
    }

    fn find_message(&self, request: &JsonObject) -> Result<usize, Failure> {
        let identifier = required_integer(request, "messageId")?;
        self.messages
            .iter()
            .position(|message| message["id"] == identifier)
            .ok_or_else(|| {
                Failure::new("not_found", format!("No message with the id {identifier}."))
            })
    }

    fn messages_status(&self, request: &JsonObject) -> Result<Value, Failure> {
        let request_id = required_text(request, "requestId")?;
        self.outbox.get(request_id).cloned().ok_or_else(|| {
            Failure::new(
                "not_found",
                format!("No message with the requestId {request_id}."),
            )
        })
    }

    fn messages_delete(
        &mut self,
        request: &JsonObject,
        after: &mut Vec<(String, Value)>,
    ) -> Result<Value, Failure> {
        let index = self.find_message(request)?;
        let message = self.messages.remove(index);
        after.push((
            "sms.deleted".to_owned(),
            json!({
                "id": message["id"],
                "conversationId": message["conversationId"],
                "address": message["address"],
                "mode": self.delete_mode,
            }),
        ));
        Ok(json!({"deleted": 1, "mode": self.delete_mode}))
    }

    fn conversations_delete(
        &mut self,
        request: &JsonObject,
        after: &mut Vec<(String, Value)>,
    ) -> Result<Value, Failure> {
        let identifier = required_integer(request, "conversationId")?;
        let doomed: Vec<&Value> = self
            .messages
            .iter()
            .filter(|m| m["conversationId"] == identifier)
            .collect();
        let Some(newest) = doomed.iter().max_by_key(|m| sort_key(m)) else {
            return Err(Failure::new(
                "not_found",
                "This conversation does not exist.",
            ));
        };
        let address = newest["address"].clone();
        let count = doomed.len();
        self.messages.retain(|m| m["conversationId"] != identifier);
        after.push((
            "sms.deleted".to_owned(),
            json!({
                "id": null, "conversationId": identifier,
                "address": address, "mode": self.delete_mode,
            }),
        ));
        Ok(json!({"deleted": count, "mode": self.delete_mode}))
    }

    fn conversations_mark_read(&mut self, request: &JsonObject) -> Result<Value, Failure> {
        // API.md names no order of the two errors. Assumption, as in the
        // app: the default SMS app is checked first.
        let identifier = required_integer(request, "conversationId")?;
        if self.delete_mode != "provider" {
            return Err(Failure::new(
                "not_default_app",
                "Only the default SMS app can mark messages as read.",
            ));
        }
        let mut found = false;
        for message in &mut self.messages {
            if message["conversationId"] == identifier {
                message["read"] = Value::Bool(true);
                found = true;
            }
        }
        if !found {
            return Err(Failure::new(
                "not_found",
                "This conversation does not exist.",
            ));
        }
        Ok(json!({"ok": true}))
    }

    fn contacts_list(&self, request: &JsonObject) -> Result<Value, Failure> {
        let query = optional_text(request, "q")?;
        let mut items = self.contacts.clone();
        items.sort_by_key(|contact| text(contact, "name").to_lowercase());
        if let Some(query) = query.filter(|query| !query.is_empty()) {
            let needle = query.to_lowercase();
            items.retain(|contact| {
                text(contact, "name").to_lowercase().contains(&needle)
                    || contact["numbers"]
                        .as_array()
                        .into_iter()
                        .flatten()
                        .any(|number| text(number, "number").contains(&needle))
            });
        }
        page(items, request, true)
    }

    /// Accept a message. Returns the result and, for a new message, the
    /// request id to transmit.
    fn messages_send(&mut self, request: &JsonObject) -> Result<(Value, Option<String>), Failure> {
        let to = required_text(request, "to")?;
        let body = required_text(request, "body")?;
        let sim = optional_integer(request, "sim")?;
        let request_id = optional_text(request, "requestId")?;
        if request_id.is_some_and(|id| !is_request_id(id)) {
            return Err(Failure::new(
                "invalid_request",
                "'requestId' must be 1 to 64 characters of A-Z a-z 0-9 . _ -.",
            ));
        }
        let number = normalize_number(to);
        let digits = number.strip_prefix('+').unwrap_or(&number);
        if !(3..=15).contains(&digits.len()) || !digits.bytes().all(|b| b.is_ascii_digit()) {
            return Err(Failure::new(
                "invalid_number",
                "'to' is not a phone number.",
            ));
        }
        if body.is_empty() {
            return Err(Failure::new("invalid_request", "'body' is empty."));
        }
        let parts = count_parts(body);
        if parts > 10 {
            return Err(Failure::new(
                "message_too_long",
                format!("The message needs {parts} SMS parts; at most 10 are allowed."),
            ));
        }
        if let Some(known) = request_id.and_then(|id| self.outbox.get(id)) {
            // A repeat: never sent twice, answered with the current status.
            let known_id = text(known, "requestId");
            if self.bodies.get(known_id).map(String::as_str) != Some(body)
                || !same_peer(text(known, "to"), &number)
            {
                return Err(Failure::new(
                    "invalid_request",
                    "'requestId' was already used for another message.",
                ));
            }
            let result = json!({
                "requestId": known["requestId"], "to": known["to"],
                "parts": known["parts"], "status": known["status"],
            });
            return Ok((result, None));
        }
        let sim = match sim {
            Some(sim) if self.sims.iter().all(|s| s["subscriptionId"] != sim) => {
                return Err(Failure::new(
                    "sim_unavailable",
                    format!("No SIM with the subscription id {sim}."),
                ));
            }
            Some(sim) => sim,
            None => 1,
        };
        let request_id = match request_id {
            Some(request_id) => request_id.to_owned(),
            None => format!("generated-{}", self.outbox.len() + 1),
        };
        let entry = json!({
            "requestId": request_id, "to": number, "parts": parts,
            "partsSent": 0, "status": "queued", "errorCode": null,
            "messageId": null, "sim": sim, "source": "api",
            "createdAt": now_millis(),
        });
        self.outbox.insert(request_id.clone(), entry);
        self.bodies.insert(request_id.clone(), body.to_owned());
        self.sent.push(json!({
            "requestId": request_id, "to": number, "body": body, "sim": sim,
        }));
        self.emit(
            "sms.queued",
            json!({
                "requestId": request_id, "to": number, "parts": parts,
                "sim": sim, "source": "api",
            }),
        );
        let result = json!({
            "requestId": request_id, "to": number, "parts": parts, "status": "queued",
        });
        Ok((result, Some(request_id)))
    }

    fn subscribe(&mut self, id: u64, request: &JsonObject) -> Result<Value, Failure> {
        // subscribe adds the names; its address replaces the filter, and
        // without address (or with null) the filter is removed.
        let names = event_names(request)?;
        let address = optional_text(request, "address")?.map(str::to_owned);
        let client = self.client(id).expect("the client of the request");
        client.events.extend(names);
        client.address = address;
        Ok(subscription(client))
    }

    fn unsubscribe(&mut self, id: u64, request: &JsonObject) -> Result<Value, Failure> {
        let names = event_names(request)?;
        let client = self.client(id).expect("the client of the request");
        for name in names {
            client.events.remove(&name);
        }
        if client.events.is_empty() {
            client.address = None;
        }
        Ok(subscription(client))
    }
}

struct Shared {
    state: Mutex<State>,
    stop: AtomicBool,
    tls: Arc<ServerConfig>,
}

impl Shared {
    fn lock(&self) -> MutexGuard<'_, State> {
        self.state
            .lock()
            .unwrap_or_else(|poison| poison.into_inner())
    }

    fn on_frame(self: &Arc<Self>, id: u64, frame: &str) {
        let mut state = self.lock();
        if state.client(id).is_none_or(|client| client.closing) {
            // The connection is as good as gone; the frame came too late.
            return;
        }
        let request: Option<Value> = serde_json::from_str(frame).ok();
        if !state.within_rate(id) {
            // Every frame counts, the login and unreadable ones as well.
            let identifier = request
                .as_ref()
                .and_then(|request| request.get("id"))
                .filter(|identifier| identifier.is_string() || identifier.is_number())
                .cloned()
                .unwrap_or(Value::Null);
            state.refused_count += 1;
            let refusal = json!({
                "id": identifier, "ok": false,
                "error": {"code": "rate_limited", "message": "Too many requests.",
                          "retryAfter": 1},
            });
            state.send(id, refusal.to_string());
            let limit = state.options.max_refused;
            let too_many = state.client(id).is_some_and(|client| {
                client.refused += 1;
                client.refused > limit
            });
            if too_many {
                state.push(id, Outgoing::Close(4429, "Too many requests.".to_owned()));
            }
            return;
        }
        if let Some(client) = state.client(id) {
            client.refused = 0;
        }
        let Some(Value::Object(request)) = request else {
            state.respond(
                id,
                json!({
                    "id": null, "ok": false,
                    "error": {"code": "invalid_json",
                              "message": "The frame is not a JSON object."},
                }),
            );
            return;
        };
        state.requests.push(Value::Object(request.clone()));
        let identifier = request.get("id").cloned().unwrap_or(Value::Null);
        let action = request.get("action").and_then(Value::as_str).unwrap_or("");
        let planned_drop = state
            .drops
            .get(action)
            .and_then(|drops| drops.front().copied());
        if planned_drop == Some(true) {
            state.drops.get_mut(action).and_then(VecDeque::pop_front);
            state.push(id, Outgoing::Abort);
            return;
        }

        let mut after = Vec::new();
        let (response, close_code) = match self.dispatch(&mut state, id, &request, &mut after) {
            Ok(None) => return,
            Ok(Some(result)) => (
                json!({"id": identifier, "ok": true, "result": result}),
                None,
            ),
            Err(failure) => (
                json!({"id": identifier, "ok": false, "error": failure.error}),
                failure.close_code,
            ),
        };
        if planned_drop == Some(false) {
            state.drops.get_mut(action).and_then(VecDeque::pop_front);
            state.push(id, Outgoing::Abort);
        } else {
            let reason = response["error"]["code"].as_str().unwrap_or("").to_owned();
            state.respond(id, response);
            if let Some(code) = close_code {
                state.push(id, Outgoing::Close(code, reason));
            }
        }
        for (name, data) in after {
            state.emit(&name, data);
        }
    }

    fn dispatch(
        self: &Arc<Self>,
        state: &mut State,
        id: u64,
        request: &JsonObject,
        after: &mut Vec<(String, Value)>,
    ) -> Result<Option<Value>, Failure> {
        let Some(action) = request.get("action").and_then(Value::as_str) else {
            return Err(Failure::new(
                "invalid_request",
                "The field 'action' is missing.",
            ));
        };
        let identifier = request.get("id").unwrap_or(&Value::Null);
        if !(identifier.is_null() || identifier.is_string() || identifier.is_number()) {
            return Err(Failure::new(
                "invalid_request",
                "'id' must be a string or a number.",
            ));
        }
        if let Some(left) = state.ignored.get_mut(action).filter(|left| **left > 0) {
            *left -= 1;
            return Ok(None);
        }
        if action == "authenticate" {
            return state.authenticate(id, request).map(Some);
        }
        if !state.client(id).is_some_and(|client| client.authenticated) {
            return Err(Failure::new("not_authenticated", "Authenticate first."));
        }
        if let Some(failure) = state.failures.get_mut(action).and_then(VecDeque::pop_front) {
            return Err(failure);
        }
        if let Some(result) = state.results.get_mut(action).and_then(VecDeque::pop_front) {
            return Ok(Some(result));
        }
        let result = match action {
            "status" => state.status(),
            "ping" => json!({"time": now_millis()}),
            "conversations.list" => state.conversations_list(request)?,
            "messages.list" => state.messages_list(request)?,
            "messages.get" => {
                let index = state.find_message(request)?;
                state.messages[index].clone()
            }
            "messages.status" => state.messages_status(request)?,
            "messages.delete" => state.messages_delete(request, after)?,
            "conversations.delete" => state.conversations_delete(request, after)?,
            "conversations.markRead" => state.conversations_mark_read(request)?,
            "contacts.list" => state.contacts_list(request)?,
            "messages.send" => {
                let (result, transmit) = state.messages_send(request)?;
                if let Some(request_id) = transmit {
                    let shared = Arc::clone(self);
                    thread::spawn(move || shared.transmit(&request_id));
                }
                result
            }
            "subscribe" => state.subscribe(id, request)?,
            "unsubscribe" => state.unsubscribe(id, request)?,
            _ => {
                return Err(Failure::new(
                    "unknown_action",
                    format!("Unknown action '{action}'."),
                ));
            }
        };
        Ok(Some(result))
    }

    /// Play the radio: after a while the message is sent or has failed.
    fn transmit(&self, request_id: &str) {
        let (delay, reports, number, parts, fails) = {
            let state = self.lock();
            let entry = &state.outbox[request_id];
            let number = text(entry, "to").to_owned();
            let fails = state.fail_numbers.contains(&number);
            (
                state.options.send_delay,
                state.options.delivery_reports,
                number,
                entry["parts"].as_i64().unwrap_or(1),
                fails,
            )
        };
        if parts > 1 && !fails {
            // Some parts are sent, some are left.
            thread::sleep(delay / 2);
            let mut state = self.lock();
            state.outbox[request_id]["status"] = json!("sending");
            state.outbox[request_id]["partsSent"] = json!(1);
            drop(state);
            thread::sleep(delay / 2);
        } else {
            thread::sleep(delay);
        }
        let mut state = self.lock();
        if state.fail_numbers.contains(&number) {
            // The phone has not stored the message: no id, no conversation.
            state.outbox[request_id]["status"] = json!("failed");
            state.outbox[request_id]["errorCode"] = json!(1);
            state.emit(
                "sms.failed",
                json!({
                    "requestId": request_id, "id": null, "conversationId": null,
                    "to": number, "errorCode": 1, "reason": "generic_failure",
                }),
            );
            return;
        }
        let body = state.bodies[request_id].clone();
        let sim = state.outbox[request_id]["sim"].as_i64();
        let message = state.add_message(&number, &body, "out", None, true, sim, Some(request_id));
        state.outbox[request_id]["status"] = json!("sent");
        state.outbox[request_id]["partsSent"] = json!(parts);
        state.outbox[request_id]["messageId"] = message["id"].clone();
        state.emit(
            "sms.sent",
            json!({
                "requestId": request_id, "conversationId": message["conversationId"],
                "to": number, "message": message,
            }),
        );
        if !reports {
            return;
        }
        drop(state);
        thread::sleep(delay);
        let mut state = self.lock();
        state.outbox[request_id]["status"] = json!("delivered");
        if let Some(stored) = state.messages.iter_mut().find(|m| m["id"] == message["id"]) {
            stored["status"] = json!("delivered");
        }
        state.emit(
            "sms.delivered",
            json!({
                "requestId": request_id, "id": message["id"],
                "conversationId": message["conversationId"], "to": number,
            }),
        );
    }

    /// Talk to one client until the connection ends.
    fn serve(self: Arc<Self>, tcp: TcpStream) {
        let patience = Some(Duration::from_secs(5));
        let _ = tcp.set_nodelay(true);
        if tcp.set_read_timeout(patience).is_err() || tcp.set_write_timeout(patience).is_err() {
            return;
        }
        let Ok(connection) = ServerConnection::new(Arc::clone(&self.tls)) else {
            return;
        };
        let stream = StreamOwned::new(connection, tcp);
        let config = WebSocketConfig::default()
            .max_message_size(Some(MAX_FRAME_BYTES))
            .max_frame_size(Some(MAX_FRAME_BYTES));
        // A client that refuses the certificate never gets this far.
        let Ok(mut socket) = tungstenite::accept_hdr_with_config(stream, check_path, Some(config))
        else {
            return;
        };
        let (id, auth_timeout) = {
            let mut state = self.lock();
            if state.client_count() >= state.options.max_clients {
                drop(state);
                close(&mut socket, 4409, "too many clients");
                return;
            }
            state.connection_count += 1;
            state.next_client_id += 1;
            let id = state.next_client_id;
            state.clients.push(Client {
                id,
                authenticated: false,
                events: HashSet::new(),
                address: None,
                request_times: VecDeque::new(),
                refused: 0,
                outbox: VecDeque::new(),
                closing: false,
            });
            let hello = json!({
                "event": "hello",
                "protocolVersion": state.protocol_version,
                "authenticationRequired": true,
            });
            state.send(id, hello.to_string());
            (id, state.options.auth_timeout)
        };
        self.converse(&mut socket, id, auth_timeout);
        self.lock().clients.retain(|client| client.id != id);
    }

    fn converse(self: &Arc<Self>, socket: &mut Socket, id: u64, auth_timeout: Duration) {
        let started = Instant::now();
        let _ = socket.get_ref().sock.set_read_timeout(Some(TICK));
        loop {
            match socket.read() {
                Ok(Message::Text(frame)) => self.on_frame(id, frame.as_str()),
                Ok(Message::Binary(_)) => return close(socket, 4400, "binary frame"),
                Ok(Message::Close(_)) => return,
                Ok(_) => {}
                Err(tungstenite::Error::Io(error)) if is_timeout(&error) => {}
                Err(tungstenite::Error::Capacity(_)) => {
                    return close(socket, 1009, "frame too large");
                }
                Err(_) => return,
            }
            let (outgoing, authenticated) = {
                let mut state = self.lock();
                match state.client(id) {
                    Some(client) => (
                        client.outbox.drain(..).collect::<Vec<_>>(),
                        client.authenticated,
                    ),
                    None => return,
                }
            };
            for item in outgoing {
                match item {
                    Outgoing::Text(frame) => {
                        if socket.send(Message::text(frame)).is_err() {
                            return;
                        }
                    }
                    Outgoing::Close(code, reason) => return close(socket, code, &reason),
                    Outgoing::Abort => {
                        let _ = socket.get_ref().sock.shutdown(Shutdown::Both);
                        return;
                    }
                }
            }
            if !authenticated && started.elapsed() > auth_timeout {
                return close(socket, 4401, "not authenticated in time");
            }
            if self.stop.load(Ordering::SeqCst) {
                return close(socket, 1001, "server is stopping");
            }
        }
    }
}

/// Send a close frame and wait a moment for the answer of the client.
fn close(socket: &mut Socket, code: u16, reason: &str) {
    let frame = CloseFrame {
        code: CloseCode::from(code),
        reason: reason.to_owned().into(),
    };
    if socket.close(Some(frame)).is_err() {
        return;
    }
    let deadline = Instant::now() + Duration::from_millis(500);
    while Instant::now() < deadline {
        match socket.read() {
            Ok(_) => {}
            Err(tungstenite::Error::Io(error)) if is_timeout(&error) => {}
            Err(_) => return,
        }
    }
}

fn is_timeout(error: &std::io::Error) -> bool {
    matches!(
        error.kind(),
        std::io::ErrorKind::WouldBlock | std::io::ErrorKind::TimedOut
    )
}

#[allow(clippy::result_large_err)]
fn check_path(request: &Request, response: Response) -> Result<Response, ErrorResponse> {
    if request.uri().path() == "/ws" {
        return Ok(response);
    }
    let mut refusal = ErrorResponse::new(Some("Not found\n".to_owned()));
    *refusal.status_mut() = StatusCode::NOT_FOUND;
    Err(refusal)
}

/// In-memory SMS Dock server on `wss://127.0.0.1:PORT/ws`.
pub struct FakeServer {
    shared: Arc<Shared>,
    acceptor: Option<JoinHandle<()>>,
    pub port: u16,
    /// Fingerprint of the certificate the server presents.
    pub fingerprint: String,
}

impl FakeServer {
    /// Start a server with the default options and the data of [`fill`].
    pub fn new() -> Self {
        Self::start(Options::default())
    }

    /// Start a server in background threads.
    pub fn start(options: Options) -> Self {
        let certificate = if options.other_certificate {
            other_certificate()
        } else {
            certificate()
        };
        let listener = TcpListener::bind(("127.0.0.1", options.port)).expect("a free port");
        listener
            .set_nonblocking(true)
            .expect("a listener that does not block");
        let port = listener.local_addr().expect("the address").port();
        let with_data = options.fill;
        let mut state = State::new(options);
        if with_data {
            fill(&mut state);
        }
        let shared = Arc::new(Shared {
            state: Mutex::new(state),
            stop: AtomicBool::new(false),
            tls: certificate.server_config(),
        });
        let accepting = Arc::clone(&shared);
        let acceptor = thread::spawn(move || {
            while !accepting.stop.load(Ordering::SeqCst) {
                match listener.accept() {
                    Ok((tcp, _)) => {
                        let _ = tcp.set_nonblocking(false);
                        let shared = Arc::clone(&accepting);
                        thread::spawn(move || shared.serve(tcp));
                    }
                    Err(_) => thread::sleep(TICK),
                }
            }
        });
        Self {
            shared,
            acceptor: Some(acceptor),
            port,
            fingerprint: certificate.fingerprint.clone(),
        }
    }

    /// The address of the API.
    pub fn url(&self) -> String {
        format!("wss://127.0.0.1:{}/ws", self.port)
    }

    /// Read or change what the server knows.
    pub fn state(&self) -> MutexGuard<'_, State> {
        self.shared.lock()
    }

    /// Stop the server; clients see the close code 1001.
    pub fn stop(&mut self) {
        self.shared.stop.store(true, Ordering::SeqCst);
        if let Some(acceptor) = self.acceptor.take() {
            let _ = acceptor.join();
        }
        wait_until(|| self.state().clients.is_empty(), Duration::from_secs(3));
    }

    // ------------------------------------------------------------------
    // What tests look at
    // ------------------------------------------------------------------

    pub fn actions(&self) -> Vec<String> {
        self.state().actions()
    }

    pub fn count(&self, action: &str) -> usize {
        self.state().requests_of(action).len()
    }

    pub fn requests(&self) -> Vec<Value> {
        self.state().requests.clone()
    }

    /// The newest request with this action.
    pub fn last_request(&self, action: &str) -> Value {
        self.state()
            .requests_of(action)
            .pop()
            .expect("a request with this action")
    }

    pub fn sent(&self) -> Vec<Value> {
        self.state().sent.clone()
    }

    pub fn login_count(&self) -> usize {
        self.state().login_count
    }

    pub fn connection_count(&self) -> usize {
        self.state().connection_count
    }

    pub fn client_count(&self) -> usize {
        self.state().client_count()
    }

    pub fn outbox_status(&self, request_id: &str) -> String {
        self.state().outbox[request_id]["status"]
            .as_str()
            .unwrap_or("")
            .to_owned()
    }

    // ------------------------------------------------------------------
    // Controls
    // ------------------------------------------------------------------

    /// Let an SMS arrive: store it and send `sms.received`.
    pub fn receive_sms(&self, address: &str, body: &str) -> Value {
        let mut state = self.state();
        let message = state.add_message(address, body, "in", None, false, Some(1), None);
        state.emit("sms.received", json!({"message": message}));
        message
    }

    /// Send an event to the subscribed clients.
    pub fn emit(&self, name: &str, data: Value) {
        self.state().emit(name, data);
    }

    /// Behave like a restart of the app.
    ///
    /// All connections are lost, the sequence numbers begin at 1 again and
    /// `epoch` changes. Messages and the outbox are kept.
    pub fn restart(&self) {
        let mut state = self.state();
        state.drop_all(None, "");
        state.sequence = 0;
        state.epoch = (state.epoch + 1).max(now_millis());
    }

    /// Close all connections: abruptly, or with a close code.
    pub fn drop_connections(&self, code: Option<u16>, reason: &str) {
        self.state().drop_all(code, reason);
    }

    /// Drop the connection at the next request with this action: before
    /// the request is carried out, or after it was carried out but before
    /// the response is sent. Calling it again queues one more drop.
    pub fn drop_on(&self, action: &str, before: bool) {
        self.state()
            .drops
            .entry(action.to_owned())
            .or_default()
            .push_back(before);
    }

    /// Answer the next request with this action with an error. `extra`
    /// holds further fields of the error object.
    pub fn fail_next(&self, action: &str, code: &str, message: &str, extra: Value) {
        self.state()
            .failures
            .entry(action.to_owned())
            .or_default()
            .push_back(Failure::new(code, message).with(extra));
    }

    /// Answer the next request with this action with this result.
    pub fn answer_next(&self, action: &str, result: Value) {
        self.state()
            .results
            .entry(action.to_owned())
            .or_default()
            .push_back(result);
    }

    /// Do not answer the next requests with this action at all.
    pub fn ignore_next(&self, action: &str, times: usize) {
        *self.state().ignored.entry(action.to_owned()).or_default() += times;
    }

    /// Send these frames to the client right before the next response.
    pub fn before_next_response(&self, frames: &[Value]) {
        let frames = frames.iter().map(Value::to_string);
        self.state().frames_before_response.extend(frames);
    }

    /// Send this text as a frame right before the next response.
    pub fn raw_before_next_response(&self, frame: &str) {
        self.state().frames_before_response.push(frame.to_owned());
    }

    /// Hold the next `count` responses and send them in reverse order.
    pub fn reverse_next_responses(&self, count: usize) {
        self.state().hold = count;
    }

    /// Lock logins as after too many wrong passwords.
    pub fn lock_logins(&self, time: Duration) {
        self.state().locked_until = Some(Instant::now() + time);
    }
}

impl Drop for FakeServer {
    fn drop(&mut self) {
        self.stop();
    }
}

/// Store 29 messages in three conversations.
pub fn fill(state: &mut State) {
    state.add_contact("Anna Example", ANNA, "Mobile");
    state.add_contact("Ben Example", BEN, "Work");
    for index in 0..25 {
        let direction = if index % 2 == 0 { "in" } else { "out" };
        state.add_message(
            ANNA,
            &format!("Anna {index}"),
            direction,
            Some(BASE_TIME + index * 1000),
            index < 22,
            Some(1),
            None,
        );
    }
    let ben = [
        ("Your invoice is attached", "out", 5000),
        ("Thanks for the invoice", "in", 4000),
        ("See you", "in", 3000),
    ];
    for (body, direction, age) in ben {
        state.add_message(
            BEN,
            body,
            direction,
            Some(BASE_TIME - age),
            true,
            Some(2),
            None,
        );
    }
    state.add_message(
        "A1",
        "Your bill is ready",
        "in",
        Some(BASE_TIME - 9000),
        false,
        Some(1),
        None,
    );
}

/// Options of a client for this server: short pauses, its fingerprint.
pub fn builder(server: &FakeServer) -> ClientBuilder {
    SmsDockClient::builder(&server.url())
        .password(PASSWORD)
        .fingerprint(server.fingerprint.clone())
        .timeout(Duration::from_secs(5))
        .reconnect_delay(Duration::from_millis(10))
}

/// A client that is connected and logged in.
pub fn connect(server: &FakeServer) -> SmsDockClient {
    builder(server)
        .connect()
        .expect("a connection to the fake server")
}

/// Poll until the condition holds; return whether it did.
pub fn wait_until(mut condition: impl FnMut() -> bool, timeout: Duration) -> bool {
    let deadline = Instant::now() + timeout;
    while Instant::now() < deadline {
        if condition() {
            return true;
        }
        thread::sleep(Duration::from_millis(5));
    }
    condition()
}

/// An `sms.received` event as the server would send it.
pub fn event_frame(sequence: i64, body: &str) -> Value {
    json!({
        "event": "sms.received",
        "seq": sequence,
        "data": {"message": {
            "id": 9000 + sequence, "conversationId": 1, "address": ANNA,
            "contact": null, "direction": "in", "body": body,
            "timestamp": BASE_TIME, "sentTimestamp": BASE_TIME, "read": false,
            "status": "received", "sim": 1, "requestId": null,
        }},
    })
}

pub fn now_millis() -> i64 {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map(|time| time.as_millis() as i64)
        .unwrap_or(0)
}

fn text<'a>(value: &'a Value, name: &str) -> &'a str {
    value[name].as_str().unwrap_or("")
}

fn sort_key(item: &Value) -> (i64, i64) {
    (
        item["timestamp"].as_i64().unwrap_or(0),
        item["id"].as_i64().unwrap_or(0),
    )
}

fn malformed(name: &str) -> Failure {
    Failure::new(
        "invalid_request",
        format!("The field '{name}' is malformed."),
    )
}

fn missing(name: &str) -> Failure {
    Failure::new("invalid_request", format!("The field '{name}' is missing."))
}

fn optional_text<'a>(request: &'a JsonObject, name: &str) -> Result<Option<&'a str>, Failure> {
    match request.get(name) {
        None | Some(Value::Null) => Ok(None),
        Some(Value::String(text)) => Ok(Some(text)),
        Some(_) => Err(malformed(name)),
    }
}

fn optional_integer(request: &JsonObject, name: &str) -> Result<Option<i64>, Failure> {
    match request.get(name) {
        None | Some(Value::Null) => Ok(None),
        Some(value) => value.as_i64().map(Some).ok_or_else(|| malformed(name)),
    }
}

fn optional_flag(request: &JsonObject, name: &str) -> Result<Option<bool>, Failure> {
    match request.get(name) {
        None | Some(Value::Null) => Ok(None),
        Some(Value::Bool(flag)) => Ok(Some(*flag)),
        Some(_) => Err(malformed(name)),
    }
}

fn required_text<'a>(request: &'a JsonObject, name: &str) -> Result<&'a str, Failure> {
    optional_text(request, name)?.ok_or_else(|| missing(name))
}

fn required_integer(request: &JsonObject, name: &str) -> Result<i64, Failure> {
    optional_integer(request, name)?.ok_or_else(|| missing(name))
}

/// The event names of a request, with `*` replaced.
fn event_names(request: &JsonObject) -> Result<Vec<String>, Failure> {
    let names: Option<Vec<&str>> = request
        .get("events")
        .and_then(Value::as_array)
        .filter(|names| !names.is_empty())
        .and_then(|names| names.iter().map(Value::as_str).collect());
    let Some(names) = names else {
        return Err(Failure::new("invalid_request", "'events' is missing."));
    };
    if names.contains(&"*") {
        return Ok(EVENT_NAMES.iter().map(|name| (*name).to_owned()).collect());
    }
    for name in &names {
        if !EVENT_NAMES.contains(name) {
            return Err(Failure::new(
                "invalid_request",
                format!("Unknown event '{name}'."),
            ));
        }
    }
    Ok(names.into_iter().map(str::to_owned).collect())
}

fn subscription(client: &Client) -> Value {
    let events: Vec<&str> = EVENT_NAMES
        .iter()
        .copied()
        .filter(|name| client.events.contains(*name))
        .collect();
    json!({"events": events, "address": client.address})
}

/// Whether a client subscribed to an event and its filter lets it pass.
///
/// The filter applies to `sms.*` events that name a peer. Events whose
/// number is not known are always delivered.
fn wants(client: &Client, name: &str, data: &Value) -> bool {
    if !client.events.contains(name) {
        return false;
    }
    let Some(filter) = client
        .address
        .as_deref()
        .filter(|_| name.starts_with("sms."))
    else {
        return true;
    };
    let peer = data["message"]["address"]
        .as_str()
        .or_else(|| data["to"].as_str())
        .or_else(|| data["address"].as_str());
    peer.is_none_or(|peer| same_peer(peer, filter))
}

fn page(items: Vec<Value>, request: &JsonObject, by_index: bool) -> Result<Value, Failure> {
    let limit = match request.get("limit") {
        None => 50,
        Some(value) => match value.as_u64() {
            Some(limit) if (1..=200).contains(&limit) => limit as usize,
            _ => {
                return Err(Failure::new(
                    "invalid_request",
                    "'limit' must be between 1 and 200.",
                ));
            }
        },
    };
    let bad_cursor = || Failure::new("invalid_request", "'cursor' is invalid.");
    let key = |item: &Value| format!("{}:{}", item["timestamp"], item["id"]);
    let start = match optional_text(request, "cursor")? {
        None => 0,
        Some(cursor) if by_index => cursor.parse::<usize>().map_err(|_| bad_cursor())?,
        Some(cursor) => {
            let index = items.iter().position(|item| key(item) == cursor);
            index.ok_or_else(bad_cursor)? + 1
        }
    };
    let chunk: Vec<Value> = items.iter().skip(start).take(limit).cloned().collect();
    let mut next_cursor = Value::Null;
    if let Some(last) = chunk.last().filter(|_| start + limit < items.len()) {
        next_cursor = if by_index {
            json!((start + chunk.len()).to_string())
        } else {
            json!(key(last))
        };
    }
    Ok(json!({"items": chunk, "nextCursor": next_cursor}))
}

fn is_request_id(text: &str) -> bool {
    (1..=64).contains(&text.len())
        && text
            .bytes()
            .all(|byte| byte.is_ascii_alphanumeric() || matches!(byte, b'.' | b'_' | b'-'))
}

/// Remove the characters that are ignored in phone numbers: spaces,
/// dashes, dots, slashes and brackets.
pub fn normalize_number(number: &str) -> String {
    number
        .trim()
        .chars()
        .filter(|c| !c.is_whitespace() && !matches!(c, '-' | '.' | '/' | '(' | ')'))
        .collect()
}

/// Whether an address is a phone number and not a sender name.
pub fn is_number(address: &str) -> bool {
    let number = normalize_number(address);
    let digits = number.strip_prefix('+').unwrap_or(&number);
    !digits.is_empty() && digits.bytes().all(|byte| byte.is_ascii_digit())
}

/// A key that is equal for equal addresses where that is known: the
/// international form for numbers written with `+` or `00`, the bare
/// digits for other numbers, lower case for sender names.
fn address_key(address: &str) -> String {
    if !is_number(address) {
        return address.trim().to_lowercase();
    }
    let digits = normalize_number(address);
    match digits.strip_prefix("00") {
        Some(rest) if digits.len() > 4 => format!("+{rest}"),
        _ => digits,
    }
}

/// Whether two addresses mean the same peer ("Phone numbers" in API.md).
pub fn same_peer(first: &str, second: &str) -> bool {
    let (a, b) = (address_key(first), address_key(second));
    if a == b {
        return true;
    }
    if !is_number(first) || !is_number(second) {
        return false;
    }
    let ends = |full: &str, national: &str| {
        let significant = national.trim_start_matches('0');
        significant.len() >= 7 && full.ends_with(significant)
    };
    match (a.starts_with('+'), b.starts_with('+')) {
        (true, true) => false,
        (true, false) => ends(&a, &b),
        (false, true) => ends(&b, &a),
        (false, false) => ends(&a, &b) || ends(&b, &a),
    }
}

/// The number of SMS parts of a text (simplified alphabet test).
pub fn count_parts(body: &str) -> usize {
    let length = body.chars().count();
    let (single, multi) = if body.is_ascii() {
        (160, 153)
    } else {
        (70, 67)
    };
    if length <= single {
        1
    } else {
        length.div_ceil(multi)
    }
}
