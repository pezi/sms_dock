//! The examples run on their own against the fake server.
//!
//! `cargo test` builds the examples before it runs this file.

mod common;

use std::io::{BufRead, BufReader};
use std::path::{Path, PathBuf};
use std::process::{Child, Command, Output, Stdio};
use std::sync::mpsc::{self, Receiver};
use std::thread;
use std::time::Duration;

use common::{wait_until, FakeServer, ANNA, BEN, PASSWORD};
use serde_json::json;

fn example(name: &str) -> PathBuf {
    let binaries = Path::new(env!("CARGO_BIN_EXE_smsdock"))
        .parent()
        .expect("the directory of the binaries");
    let path = binaries.join("examples").join(name);
    let path = if path.exists() {
        path
    } else {
        path.with_extension("exe")
    };
    assert!(
        path.exists(),
        "{name} is not built; run `cargo test` or `cargo build --examples` first"
    );
    path
}

fn command(name: &str, fingerprint: Option<&str>, arguments: &[&str]) -> Command {
    let mut command = Command::new(example(name));
    command
        .args(arguments)
        .env("SMSDOCK_PASSWORD", PASSWORD)
        .env_remove("SMSDOCK_FINGERPRINT")
        .stdin(Stdio::null())
        .stdout(Stdio::piped())
        .stderr(Stdio::piped());
    if let Some(fingerprint) = fingerprint {
        command.env("SMSDOCK_FINGERPRINT", fingerprint);
    }
    command
}

/// Run an example to its end.
fn run(name: &str, server: &FakeServer, arguments: &[&str]) -> Output {
    command(name, Some(&server.fingerprint), arguments)
        .output()
        .expect("the example")
}

fn text(bytes: &[u8]) -> String {
    String::from_utf8_lossy(bytes).into_owned()
}

/// An example that runs until it is stopped.
struct Running {
    child: Child,
    lines: Receiver<String>,
}

impl Running {
    fn start(name: &str, server: &FakeServer) -> Self {
        let url = server.url();
        let mut child = command(name, Some(&server.fingerprint), &[&url])
            .spawn()
            .expect("the example");
        let output = child.stdout.take().expect("standard output");
        let (sender, lines) = mpsc::channel();
        thread::spawn(move || {
            for line in BufReader::new(output).lines().map_while(Result::ok) {
                if sender.send(line).is_err() {
                    return;
                }
            }
        });
        Self { child, lines }
    }

    /// Read lines of the output, but do not wait for ever.
    fn read(&self, count: usize) -> Vec<String> {
        let mut lines = Vec::new();
        while lines.len() < count {
            match self.lines.recv_timeout(Duration::from_secs(20)) {
                Ok(line) => lines.push(line),
                Err(_) => break,
            }
        }
        lines
    }
}

impl Drop for Running {
    fn drop(&mut self) {
        let _ = self.child.kill();
        let _ = self.child.wait();
    }
}

fn subscribed(server: &FakeServer) -> bool {
    wait_until(|| server.count("subscribe") >= 1, Duration::from_secs(20))
}

#[test]
fn list_messages() {
    let server = FakeServer::new();
    let output = run("list_messages", &server, &[&server.url()]);
    assert_eq!(output.status.code(), Some(0), "{}", text(&output.stderr));
    let out = text(&output.stdout);
    let lines: Vec<&str> = out.lines().collect();
    assert_eq!(lines.len(), 10);
    assert!(lines[0].ends_with("  Anna Example: Anna 24"));
    assert!(lines[9].ends_with("  Anna Example: Anna 6"));
}

#[test]
fn send_sms() {
    let server = FakeServer::new();
    let output = run("send_sms", &server, &[&server.url(), BEN, "Hello"]);
    assert_eq!(output.status.code(), Some(0), "{}", text(&output.stderr));
    let out = text(&output.stdout);
    let lines: Vec<&str> = out.lines().collect();
    assert_eq!(lines[0], format!("1 part(s) for {BEN}"));
    assert!(lines[1].starts_with("Sent, message id 28"));
    let sent = server.sent();
    assert_eq!(sent.len(), 1);
    assert_eq!(
        (&sent[0]["to"], &sent[0]["body"]),
        (&json!(BEN), &json!("Hello"))
    );

    let output = run("send_sms", &server, &[&server.url(), "abc", "Hello"]);
    assert_eq!(output.status.code(), Some(1));
    assert!(text(&output.stdout).starts_with("Not sent: invalid_number"));
}

#[test]
fn examples_report_a_missing_fingerprint() {
    let server = FakeServer::new();
    let output = command("list_messages", None, &[&server.url()])
        .output()
        .expect("the example");
    assert_eq!(output.status.code(), Some(1));
    let errors = text(&output.stderr);
    assert!(errors.starts_with("list_messages: ") && errors.contains("SMSDOCK_FINGERPRINT"));
    assert!(!errors.contains("panicked"));
}

#[test]
fn examples_explain_their_usage() {
    let server = FakeServer::new();
    for name in ["list_messages", "send_sms", "listen_incoming", "auto_reply"] {
        let output = run(name, &server, &[]);
        assert_eq!(output.status.code(), Some(2), "{name}");
        assert!(text(&output.stdout).contains("Usage:"), "{name}");
    }
}

#[test]
fn listen_incoming() {
    let server = FakeServer::new();
    let process = Running::start("listen_incoming", &server);
    assert!(subscribed(&server));
    server.receive_sms(ANNA, "STATUS");
    server.receive_sms("A1", "Your bill");
    let lines = process.read(3);
    assert_eq!(lines.len(), 3, "{lines:?}");
    assert!(lines[0].starts_with("Waiting for SMS"));
    assert!(lines[1].ends_with("  Anna Example: STATUS"));
    assert!(lines[2].ends_with("  A1: Your bill"));
}

#[test]
fn listen_incoming_reads_what_it_missed() {
    let server = FakeServer::new();
    let process = Running::start("listen_incoming", &server);
    assert!(subscribed(&server));
    server.receive_sms(ANNA, "first");
    assert_eq!(process.read(2).len(), 2);
    server.drop_connections(None, "");
    assert!(wait_until(
        || server.client_count() == 0,
        Duration::from_secs(3)
    ));
    server.receive_sms(BEN, "while away");
    server.restart();
    server.receive_sms(ANNA, "after the restart");
    let mut lines = process.read(2);
    assert!(wait_until(
        || server.client_count() == 1,
        Duration::from_secs(20)
    ));
    server.receive_sms(ANNA, "live again");
    lines.extend(process.read(1));
    // Old messages of the phone are not shown, new ones only once.
    let bodies: Vec<&str> = lines
        .iter()
        .filter_map(|line| line.split_once(": ").map(|(_, body)| body))
        .collect();
    assert_eq!(bodies, ["while away", "after the restart", "live again"]);
}

#[test]
fn auto_reply() {
    let server = FakeServer::new();
    let process = Running::start("auto_reply", &server);
    assert!(subscribed(&server));
    server.receive_sms(ANNA, "status");
    server.receive_sms(ANNA, "STATUS");
    server.receive_sms("A1", "Your bill");
    server.receive_sms("8810", "Your code is 1234");
    {
        let mut state = server.state();
        let message = state.add_message(BEN, "Hello?", "in", None, false, Some(2), None);
        drop(state);
        server.emit("sms.received", json!({"message": message}));
    }
    let lines = process.read(6);
    assert_eq!(lines.len(), 6, "{lines:?}");
    assert!(lines[1].starts_with(&format!("{ANNA}: answered, requestId ")));
    assert_eq!(
        lines[2],
        format!("{ANNA}: no answer (answered a short time ago)")
    );
    assert_eq!(lines[3], "A1: no answer (not a phone number)");
    assert_eq!(lines[4], "8810: no answer (not a phone number)");
    assert!(lines[5].starts_with(&format!("{BEN}: answered, requestId ")));
    let sent: Vec<(String, String, i64)> = server
        .sent()
        .iter()
        .map(|m| {
            (
                m["to"].as_str().unwrap_or("").to_owned(),
                m["body"].as_str().unwrap_or("").to_owned(),
                m["sim"].as_i64().unwrap_or(0),
            )
        })
        .collect();
    assert_eq!(
        sent,
        [
            (ANNA.to_owned(), "All systems running.".to_owned(), 1),
            (BEN.to_owned(), "Commands: STATUS".to_owned(), 2),
        ]
    );
}
