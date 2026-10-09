//! Hook that answers incoming SMS.
//!
//! An SMS with the text STATUS gets the answer "All systems running.";
//! everything else gets a short help text.
//!
//! An answering machine can end up in a loop, for example with another
//! answering machine or with a service that answers every SMS. Three
//! guards prevent that:
//!
//! 1. A number gets at most one answer in `COOLDOWN_MINUTES`.
//! 2. Senders that are not ordinary phone numbers get no answer: names
//!    such as "A1" or "Post" and short codes such as 8810.
//! 3. At most `MAX_REPLIES_PER_HOUR` answers are sent in total.
//!
//! Usage:
//!
//! ```text
//! export SMSDOCK_PASSWORD='your password'
//! export SMSDOCK_FINGERPRINT='AB:CD:…'      # from the Server screen of the app
//! cargo run --example auto_reply -- wss://192.168.1.50:9443/ws
//! ```
//!
//! Stop with Ctrl+C. Every answer is an SMS that your tariff may charge.

use std::collections::{HashMap, VecDeque};
use std::process::ExitCode;
use std::time::{Duration, Instant};

use smsdock::{Error, SmsDockClient};

const COOLDOWN_MINUTES: u64 = 10;
const MAX_REPLIES_PER_HOUR: usize = 20;
const HOUR: Duration = Duration::from_secs(3600);

const USAGE: &str = "Hook that answers incoming SMS.

An SMS with the text STATUS gets the answer \"All systems running.\";
everything else gets a short help text.

Usage:

    export SMSDOCK_PASSWORD='your password'
    export SMSDOCK_FINGERPRINT='AB:CD:…'      # from the Server screen of the app
    cargo run --example auto_reply -- wss://192.168.1.50:9443/ws

Stop with Ctrl+C. Every answer is an SMS that your tariff may charge.";

/// A phone number: optional +, then 7 to 15 digits. Shorter ones are short
/// codes; anything with letters is a sender name that cannot be answered.
fn is_phone_number(number: &str) -> bool {
    let digits = number.strip_prefix('+').unwrap_or(number);
    (7..=15).contains(&digits.len()) && digits.bytes().all(|byte| byte.is_ascii_digit())
}

/// Decides whether a sender may get an answer now.
struct ReplyGuard {
    cooldown: Duration,
    replies_per_hour: usize,
    last_reply: HashMap<String, Duration>,
    recent: VecDeque<Duration>,
}

impl ReplyGuard {
    fn new(cooldown: Duration, replies_per_hour: usize) -> Self {
        Self {
            cooldown,
            replies_per_hour,
            last_reply: HashMap::new(),
            recent: VecDeque::new(),
        }
    }

    /// Return why this sender gets no answer, or `None` if it gets one.
    /// `now` is the time since the program started.
    fn refusal(&mut self, address: &str, now: Duration) -> Option<&'static str> {
        let number: String = address
            .chars()
            .filter(|c| !c.is_whitespace() && !matches!(c, '-' | '.' | '(' | ')'))
            .collect();
        if !is_phone_number(&number) {
            return Some("not a phone number");
        }
        if let Some(last) = self.last_reply.get(&number) {
            if now.saturating_sub(*last) < self.cooldown {
                return Some("answered a short time ago");
            }
        }
        while self
            .recent
            .front()
            .is_some_and(|first| now.saturating_sub(*first) >= HOUR)
        {
            self.recent.pop_front();
        }
        if self.recent.len() >= self.replies_per_hour {
            return Some("too many answers in the last hour");
        }
        // Count the answer before it is sent: a failure must not open the
        // door for a loop.
        self.last_reply.insert(number, now);
        self.recent.push_back(now);
        None
    }
}

/// Return the answer to a message text.
fn answer_for(text: &str) -> &'static str {
    if text.trim().eq_ignore_ascii_case("STATUS") {
        "All systems running."
    } else {
        "Commands: STATUS"
    }
}

fn run(url: &str) -> smsdock::Result<()> {
    let started = Instant::now();
    let mut guard = ReplyGuard::new(
        Duration::from_secs(COOLDOWN_MINUTES * 60),
        MAX_REPLIES_PER_HOUR,
    );
    let mut dock = SmsDockClient::builder(url)
        .reconnect_attempts(None)
        .connect()?;
    dock.subscribe(&["sms.received"], None)?;
    println!("Answering SMS. Press Ctrl+C to stop.");

    // SMS that arrive while the connection is down get no answer: the
    // phone does not send events again, and a late answer helps nobody.
    // listen_incoming.rs shows how to read such messages.
    while let Some(event) = dock.next_event(None)? {
        let Some(message) = event.message() else {
            continue;
        };
        let sender = &message.address;
        if let Some(refusal) = guard.refusal(sender, started.elapsed()) {
            println!("{sender}: no answer ({refusal})");
            continue;
        }
        // Answer on the SIM that received the message.
        match dock.send(sender, answer_for(&message.body), message.sim, None) {
            Ok(result) => println!("{sender}: answered, requestId {}", result.request_id),
            Err(Error::Api { code, message, .. }) => {
                println!("{sender}: not answered, {code}: {message}");
            }
            Err(error) => return Err(error),
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
            eprintln!("auto_reply: {error}");
            ExitCode::FAILURE
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn at(seconds: u64) -> Duration {
        Duration::from_secs(seconds)
    }

    #[test]
    fn reply_guard() {
        let mut guard = ReplyGuard::new(at(600), 3);
        assert_eq!(guard.refusal("+4369912345678", at(0)), None);
        assert!(guard.refusal("+4369912345678", at(599)).is_some());
        assert!(guard.refusal("+43 699 123-456.78", at(599)).is_some());
        assert_eq!(guard.refusal("+4369912345678", at(600)), None);
        for sender in ["A1", "Post", "8810", "12345", "", "+43 abc"] {
            assert_eq!(guard.refusal(sender, at(700)), Some("not a phone number"));
        }
        assert_eq!(guard.refusal("+4369087654321", at(700)), None);
        // Three answers in the last hour: the fourth sender has to wait.
        assert_eq!(
            guard.refusal("+436601111111", at(800)),
            Some("too many answers in the last hour")
        );
        assert_eq!(guard.refusal("+436601111111", at(3600)), None);
    }

    #[test]
    fn answers() {
        assert_eq!(answer_for(" status\n"), "All systems running.");
        assert_eq!(answer_for("Hello"), "Commands: STATUS");
    }
}
