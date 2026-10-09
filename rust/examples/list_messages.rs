//! Print the last 10 incoming SMS.
//!
//! Usage:
//!
//! ```text
//! export SMSDOCK_PASSWORD='your password'
//! export SMSDOCK_FINGERPRINT='AB:CD:…'      # from the Server screen of the app
//! cargo run --example list_messages -- wss://192.168.1.50:9443/ws
//! ```

use std::process::ExitCode;

use chrono::{Local, TimeZone};
use smsdock::{Direction, MessageFilter, SmsDockClient};

const USAGE: &str = "Print the last 10 incoming SMS.

Usage:

    export SMSDOCK_PASSWORD='your password'
    export SMSDOCK_FINGERPRINT='AB:CD:…'      # from the Server screen of the app
    cargo run --example list_messages -- wss://192.168.1.50:9443/ws";

fn run(url: &str) -> smsdock::Result<()> {
    // The password comes from SMSDOCK_PASSWORD, the fingerprint from
    // SMSDOCK_FINGERPRINT. It pins the self-signed certificate of the phone.
    let mut dock = SmsDockClient::builder(url).connect()?;
    let incoming = MessageFilter::new().direction(Direction::In);
    let page = dock.messages(&incoming, 10, None)?;
    dock.close();

    for message in &page.items {
        // Timestamps are milliseconds since the epoch; show local time.
        let received = Local
            .timestamp_millis_opt(message.timestamp)
            .single()
            .map(|moment| moment.format("%Y-%m-%d %H:%M").to_string())
            .unwrap_or_default();
        let sender = message.contact.as_ref().unwrap_or(&message.address);
        println!("{received}  {sender}: {}", message.body);
    }

    // page.next_cursor leads to the next 10; see iter_messages() for a
    // loop that follows it.
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
            eprintln!("list_messages: {error}");
            ExitCode::FAILURE
        }
    }
}
