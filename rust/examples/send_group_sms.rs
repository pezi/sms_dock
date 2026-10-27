//! Send one text to several numbers, one SMS each, and wait for the results.
//!
//! Usage:
//!
//! ```text
//! export SMSDOCK_PASSWORD='your password'
//! export SMSDOCK_FINGERPRINT='AB:CD:…'      # from the Server screen of the app
//! cargo run --example send_group_sms -- wss://192.168.1.50:9443/ws "Hello all" \
//!     +4369912345678 +436641112223
//! ```

use std::process::ExitCode;
use std::time::Duration;

use smsdock::{Error, SmsDockClient};

const USAGE: &str = "Send one text to several numbers, one SMS each, and wait for the results.

Usage:

    export SMSDOCK_PASSWORD='your password'
    export SMSDOCK_FINGERPRINT='AB:CD:…'      # from the Server screen of the app
    cargo run --example send_group_sms -- wss://192.168.1.50:9443/ws \"Hello all\" \\
        +4369912345678 +436641112223";

/// Returns whether every message was sent.
fn run(url: &str, text: &str, numbers: &[&str]) -> smsdock::Result<bool> {
    // Password and fingerprint come from the environment.
    let mut dock = SmsDockClient::builder(url).connect()?;

    // send_group() returns as soon as the phone has queued one SMS per
    // recipient. The phone checks the whole group first: one bad number
    // refuses all of them, nothing is sent then. The client adds a random
    // request id; the members get the ids id.1, id.2 and so on.
    let result = match dock.send_group(numbers, text, None, None) {
        Ok(result) => result,
        // For example invalid_number, message_too_long, quota_exceeded.
        Err(Error::Api { code, message, .. }) => {
            println!("Not sent: {code}: {message}");
            return Ok(false);
        }
        Err(error) => return Err(error),
    };
    println!(
        "{} recipient(s), {} part(s) each",
        result.to.len(),
        result.parts
    );

    // Wait for every member; one the phone refused at once is already
    // failed and needs no waiting.
    let mut failed = 0;
    for member in &result.messages {
        if member.status == "failed" {
            println!("{}: refused by the phone", member.to);
            failed += 1;
            continue;
        }
        let outcome = dock.wait_for_result(&member.request_id, Some(Duration::from_secs(60)))?;
        if outcome.status == "failed" {
            let code = outcome
                .error_code
                .map_or("unknown".to_owned(), |code| code.to_string());
            println!("{}: failed, error code {code}", member.to);
            failed += 1;
        } else {
            let id = outcome
                .message_id
                .map_or("unknown".to_owned(), |id| id.to_string());
            println!("{}: {}, message id {id}", member.to, outcome.status);
        }
    }
    Ok(failed == 0)
}

fn main() -> ExitCode {
    let arguments: Vec<String> = std::env::args().skip(1).collect();
    let [url, text, numbers @ ..] = arguments.as_slice() else {
        println!("{USAGE}");
        return ExitCode::from(2);
    };
    if numbers.is_empty() {
        println!("{USAGE}");
        return ExitCode::from(2);
    }
    let numbers: Vec<&str> = numbers.iter().map(String::as_str).collect();
    match run(url, text, &numbers) {
        Ok(true) => ExitCode::SUCCESS,
        Ok(false) => ExitCode::FAILURE,
        Err(error) => {
            eprintln!("send_group_sms: {error}");
            ExitCode::FAILURE
        }
    }
}
