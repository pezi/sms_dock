//! Hook for incoming SMS: call a function for every SMS that arrives.
//!
//! Usage:
//!
//! ```text
//! export SMSDOCK_PASSWORD='your password'
//! export SMSDOCK_FINGERPRINT='AB:CD:…'      # from the Server screen of the app
//! cargo run --example listen_incoming -- wss://192.168.1.50:9443/ws
//! ```
//!
//! Stop with Ctrl+C.

use std::collections::HashSet;
use std::process::ExitCode;
use std::sync::{Arc, Mutex};

use chrono::{Local, TimeZone};
use smsdock::{Direction, Message, MessageFilter, SmsDockClient};

const USAGE: &str = "Hook for incoming SMS: call a function for every SMS that arrives.

Usage:

    export SMSDOCK_PASSWORD='your password'
    export SMSDOCK_FINGERPRINT='AB:CD:…'      # from the Server screen of the app
    cargo run --example listen_incoming -- wss://192.168.1.50:9443/ws

Stop with Ctrl+C.";

/// Your hook. `message` is a Message object of API.md.
fn on_sms(message: &Message) {
    // Timestamps are milliseconds since the epoch; show local time.
    let received = Local
        .timestamp_millis_opt(message.timestamp)
        .single()
        .map(|moment| moment.format("%H:%M:%S").to_string())
        .unwrap_or_default();
    let sender = message.contact.as_ref().unwrap_or(&message.address);
    println!("{received}  {sender}: {}", message.body);
}

/// Ids of the messages the hook has seen, and the time of the newest one.
#[derive(Default)]
struct Inbox {
    seen: HashSet<i64>,
    newest: i64,
}

impl Inbox {
    /// Call the hook once per message.
    fn handle(&mut self, message: &Message) {
        if let Some(id) = message.id {
            if !self.seen.insert(id) {
                return;
            }
        }
        self.newest = self.newest.max(message.timestamp);
        on_sms(message);
    }
}

/// Read what arrived while the connection was down.
///
/// The phone does not send events again. The client calls this function
/// after every reconnect, when the subscription is in force again.
fn catch_up(dock: &mut SmsDockClient, inbox: &Mutex<Inbox>) -> smsdock::Result<()> {
    if dock.server_restarted() || dock.missed_events() != Some(0) {
        let newest = inbox.lock().map(|inbox| inbox.newest).unwrap_or(0);
        // Messages of the same millisecond come again; handle() knows them.
        let filter = MessageFilter::new().direction(Direction::In).since(newest);
        let missed: Vec<Message> = dock
            .iter_messages(&filter, 50, None)?
            .collect::<smsdock::Result<_>>()?;
        if let Ok(mut inbox) = inbox.lock() {
            // Oldest first.
            for message in missed.iter().rev() {
                inbox.handle(message);
            }
        }
    }
    Ok(())
}

fn run(url: &str) -> smsdock::Result<()> {
    let inbox = Arc::new(Mutex::new(Inbox::default()));
    let shared = Arc::clone(&inbox);

    // Password and fingerprint come from the environment.
    let mut dock = SmsDockClient::builder(url)
        // Keep trying when the phone is out of reach for a while.
        .reconnect_attempts(None)
        .on_reconnect(move |dock| catch_up(dock, &shared))
        .connect()?;

    // Only messages from now on: remember the time of the phone.
    let now = dock.ping()?.time;
    if let Ok(mut inbox) = inbox.lock() {
        inbox.newest = now;
    }
    // A new connection gets no events. Ask for the ones you need.
    dock.subscribe(&["sms.received"], None)?;
    println!("Waiting for SMS. Press Ctrl+C to stop.");

    // next_event(None) waits without limit. After a lost connection it
    // reconnects, logs in and subscribes again, then calls catch_up().
    while let Some(event) = dock.next_event(None)? {
        if let (Some(message), Ok(mut inbox)) = (event.message(), inbox.lock()) {
            inbox.handle(&message);
        }
    }
    Ok(())
}

fn main() -> ExitCode {
    let arguments: Vec<String> = std::env::args().skip(1).collect();
    let [url] = arguments.as_slice() else {
        println!("{USAGE}");
        return ExitCode::from(2);
    };
    match run(url) {
        Ok(()) => ExitCode::SUCCESS,
        Err(error) => {
            eprintln!("listen_incoming: {error}");
            ExitCode::FAILURE
        }
    }
}
