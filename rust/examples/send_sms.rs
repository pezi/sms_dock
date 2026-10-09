//! Send one SMS and wait for the result.
//!
//! Usage:
//!
//! ```text
//! export SMSDOCK_PASSWORD='your password'
//! export SMSDOCK_FINGERPRINT='AB:CD:…'      # from the Server screen of the app
//! cargo run --example send_sms -- wss://192.168.1.50:9443/ws +4369912345678 "Hello"
//! ```

use std::process::ExitCode;
use std::time::Duration;

use smsdock::{Error, SmsDockClient};

const USAGE: &str = "Send one SMS and wait for the result.

Usage:

    export SMSDOCK_PASSWORD='your password'
    export SMSDOCK_FINGERPRINT='AB:CD:…'      # from the Server screen of the app
    cargo run --example send_sms -- wss://192.168.1.50:9443/ws +4369912345678 \"Hello\"";

/// Returns whether the message was sent.
fn run(url: &str, number: &str, text: &str) -> smsdock::Result<bool> {
    // Password and fingerprint come from the environment.
    let mut dock = SmsDockClient::builder(url).connect()?;

    // send() returns as soon as the phone has queued the message. The
    // client adds a random requestId, so a repeat after a lost connection
    // cannot send the message twice. Pass your own request id if the whole
    // program may run again.
    let result = match dock.send(number, text, None, None) {
        Ok(result) => result,
        // For example invalid_number, message_too_long, quota_exceeded.
        Err(Error::Api { code, message, .. }) => {
            println!("Not sent: {code}: {message}");
            return Ok(false);
        }
        Err(error) => return Err(error),
    };
    println!("{} part(s) for {}", result.parts, result.to);

    // Wait until the network accepted the message or refused it.
    let outcome = dock.wait_for_result(&result.request_id, Some(Duration::from_secs(60)))?;
    if outcome.status == "failed" {
        let code = outcome
            .error_code
            .map_or("unknown".to_owned(), |code| code.to_string());
        println!("Failed, error code {code}");
        return Ok(false);
    }
    let id = outcome
        .message_id
        .map_or("unknown".to_owned(), |id| id.to_string());
    println!("Sent, message id {id}");
    Ok(true)
}

fn main() -> ExitCode {
    let arguments: Vec<String> = std::env::args().skip(1).collect();
    let [url, number, text] = arguments.as_slice() else {
        println!("{USAGE}");
        return ExitCode::from(2);
    };
    match run(url, number, text) {
        Ok(true) => ExitCode::SUCCESS,
        Ok(false) => ExitCode::FAILURE,
        Err(error) => {
            eprintln!("send_sms: {error}");
            ExitCode::FAILURE
        }
    }
}
