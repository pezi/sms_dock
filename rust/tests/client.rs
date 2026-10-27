//! Tests of `SmsDockClient` against the fake server, over real TLS.

mod common;

use std::sync::{Arc, Mutex};
use std::thread;
use std::time::{Duration, Instant, UNIX_EPOCH};

use common::{
    builder, connect, event_frame, other_certificate, wait_until, FakeServer, Options, ANNA,
    BASE_TIME, BEN, PASSWORD,
};
use serde_json::{json, Value};
use smsdock::{
    fetch_fingerprint, ConversationFilter, Direction, Error, Event, Message, MessageFilter, Page,
    SmsDockClient, WaitOptions, EVENT_NAMES,
};

fn ms(milliseconds: u64) -> Duration {
    Duration::from_millis(milliseconds)
}

fn secs(seconds: u64) -> Option<Duration> {
    Some(Duration::from_secs(seconds))
}

fn all() -> MessageFilter {
    MessageFilter::new()
}

fn bodies(page: &Page<Message>) -> Vec<&str> {
    page.items.iter().map(|m| m.body.as_str()).collect()
}

fn event_bodies(events: &[Event]) -> Vec<String> {
    events
        .iter()
        .map(|event| event.message().expect("a message").body)
        .collect()
}

fn anna(range: impl Iterator<Item = i64>) -> Vec<String> {
    range.map(|index| format!("Anna {index}")).collect()
}

/// Collect the events that arrive until none comes for `timeout`.
fn events(client: &mut SmsDockClient, timeout: Duration) -> Vec<Event> {
    client
        .events(Some(timeout))
        .collect::<smsdock::Result<Vec<_>>>()
        .expect("events")
}

/// The error code of a failed call.
fn code<T: std::fmt::Debug>(result: smsdock::Result<T>) -> String {
    match result {
        Err(error) => error.code().unwrap_or("no api error").to_owned(),
        Ok(value) => panic!("expected an error, got {value:?}"),
    }
}

fn is_invalid<T>(result: &smsdock::Result<T>) -> bool {
    matches!(result, Err(Error::InvalidArgument(_)))
}

/// A complete message for answers made up by a test.
fn message_json(id: i64) -> Value {
    json!({
        "id": id, "conversationId": 1, "address": ANNA, "contact": null,
        "direction": "in", "body": format!("Message {id}"), "timestamp": BASE_TIME,
        "sentTimestamp": null, "read": true, "status": "received", "sim": 1,
        "requestId": null,
    })
}

// ----------------------------------------------------------------------
// Handshake
// ----------------------------------------------------------------------

#[test]
fn logs_in() {
    let server = FakeServer::new();
    let client = connect(&server);
    assert!(client.connected());
    let info = client.server_info().unwrap();
    assert_eq!(
        (
            info.name.as_str(),
            info.version.as_str(),
            info.protocol_version
        ),
        ("SMS Dock", "1.0.0", 1)
    );
    assert_eq!(client.connection_count(), 1);
    assert_eq!(server.actions(), ["authenticate"]);
    assert_eq!(server.requests()[0]["password"], PASSWORD);
    assert_eq!(server.login_count(), 1);
}

#[test]
fn dropping_the_client_closes_the_connection() {
    let server = FakeServer::new();
    {
        let mut client = builder(&server).build().unwrap();
        assert!(!client.connected());
        assert!(client.ping().unwrap().time > 0);
        assert_eq!(server.client_count(), 1);
    }
    assert!(wait_until(|| server.client_count() == 0, ms(3000)));

    let mut client = connect(&server);
    client.close();
    assert!(!client.connected());
    assert!(wait_until(|| server.client_count() == 0, ms(3000)));
}

#[test]
fn wrong_password() {
    let server = FakeServer::new();
    let mut client = builder(&server).password("wrong-password").build().unwrap();
    let error = client.connect().unwrap_err();
    assert_eq!(error.code(), Some("invalid_password"));
    assert!(error.is_api() && error.is_authentication());
    assert!(!format!("{error} {error:?}").contains("wrong-password"));
    assert!(!client.connected());
    // A wrong password counts towards the lock: it must not be repeated.
    assert_eq!(server.login_count(), 1);
}

#[test]
fn wrong_password_is_not_retried_by_requests() {
    let server = FakeServer::new();
    let mut client = builder(&server).password("wrong-password").build().unwrap();
    assert!(client.status().unwrap_err().is_authentication());
    assert_eq!(server.login_count(), 1);
}

#[test]
fn locked_out() {
    let server = FakeServer::new();
    server.lock_logins(Duration::from_secs(30));
    let error = builder(&server).connect().unwrap_err();
    assert_eq!(error.code(), Some("locked_out"));
    assert!(error.is_authentication());
    let wait = error.retry_after().unwrap();
    assert!((1..=30).contains(&wait));
    let Error::Api { details, .. } = &error else {
        panic!("not an API error");
    };
    assert_eq!(Value::Object(details.clone()), json!({"retryAfter": wait}));
    assert_eq!(server.login_count(), 1);
}

#[test]
fn licence_required() {
    let server = FakeServer::new();
    server.state().licence_enforced = true;
    let error = builder(&server).connect().unwrap_err();
    assert_eq!(error.code(), Some("licence_required"));
    assert!(error.is_api() && error.is_licence_required() && !error.is_authentication());
    assert_eq!(server.login_count(), 1);
}

#[test]
fn refuses_other_protocol_versions() {
    let server = FakeServer::new();
    server.state().protocol_version = 2;
    let error = builder(&server).connect().unwrap_err();
    assert!(matches!(&error, Error::Protocol(text) if text.contains("version 2")));
    // The password is not sent to a server of another protocol version.
    assert!(server.requests().is_empty());
}

#[test]
fn close_code_4401_while_logging_in() {
    let server = FakeServer::start(Options {
        auth_timeout: ms(200),
        ..Options::default()
    });
    server.ignore_next("authenticate", 1);
    let mut client = builder(&server).build().unwrap();
    let error = client.connect().unwrap_err();
    assert_eq!(error.code(), Some("not_authenticated"));
    assert_eq!(error.close_code(), Some(4401));
    assert_eq!(client.connection_count(), 1);
    assert_eq!(server.login_count(), 0);
}

#[test]
fn too_many_clients() {
    let server = FakeServer::start(Options {
        max_clients: 1,
        ..Options::default()
    });
    let _first = connect(&server);
    let error = builder(&server).connect().unwrap_err();
    assert!(matches!(error, Error::ConnectionInterrupted { .. }));
    assert_eq!(error.close_code(), Some(4409));
}

#[test]
fn nothing_listens() {
    let mut server = FakeServer::new();
    let mut client = builder(&server)
        .connect_timeout(Duration::from_secs(2))
        .build()
        .unwrap();
    server.stop();
    let error = client.connect().unwrap_err();
    assert!(
        matches!(&error, Error::ConnectionInterrupted { message, .. }
        if message.contains("Cannot connect"))
    );
}

// ----------------------------------------------------------------------
// Certificate pinning over real TLS
// ----------------------------------------------------------------------

#[test]
fn pins_the_certificate() {
    let server = FakeServer::new();
    let colons = server.fingerprint.clone();
    let plain = colons.replace(':', "");
    for form in [
        colons.clone(),
        colons.to_lowercase(),
        plain.clone(),
        plain.to_lowercase(),
        format!("sha256 Fingerprint={colons}\n"),
    ] {
        let mut client = builder(&server).fingerprint(form).connect().unwrap();
        assert!(client.ping().unwrap().time > 0);
    }
}

#[test]
fn certificate_mismatch() {
    let server = FakeServer::new();
    let other = &other_certificate().fingerprint;
    assert_ne!(other, &server.fingerprint);
    let mut client = builder(&server).fingerprint(other).build().unwrap();
    let error = client.connect().unwrap_err();
    let Error::CertificateMismatch { expected, actual } = &error else {
        panic!("expected a mismatch, got {error:?}");
    };
    assert_eq!((expected, actual), (other, &server.fingerprint));
    let text = error.to_string();
    assert!(text.contains(other) && text.contains(&server.fingerprint));
    assert!(!client.connected());
    thread::sleep(ms(100));
    // Nothing reached the server: no WebSocket, no password.
    assert_eq!(server.connection_count(), 0);
    assert!(server.requests().is_empty());
}

#[test]
fn certificate_mismatch_is_not_retried() {
    let server = FakeServer::new();
    let other = &other_certificate().fingerprint;
    let mut client = builder(&server).fingerprint(other).build().unwrap();
    let started = Instant::now();
    assert!(matches!(
        client.status(),
        Err(Error::CertificateMismatch { .. })
    ));
    assert_eq!(client.connection_count(), 0);
    assert!(started.elapsed() < ms(900));
    assert_eq!(server.connection_count(), 0);
}

#[test]
fn new_certificate_after_reconnect() {
    let mut first = FakeServer::new();
    let mut client = connect(&first);
    assert!(client.ping().is_ok());
    first.stop();
    let second = FakeServer::start(Options {
        other_certificate: true,
        port: first.port,
        ..Options::default()
    });
    assert!(matches!(
        client.status(),
        Err(Error::CertificateMismatch { .. })
    ));
    thread::sleep(ms(100));
    assert!(second.requests().is_empty());
    assert_eq!(second.connection_count(), 0);
}

#[test]
fn fetches_the_fingerprint() {
    let server = FakeServer::new();
    let fingerprint = fetch_fingerprint("127.0.0.1", server.port, Duration::from_secs(5));
    assert_eq!(fingerprint.unwrap(), server.fingerprint);
    thread::sleep(ms(100));
    assert!(server.requests().is_empty());

    let mut gone = FakeServer::new();
    gone.stop();
    let error = fetch_fingerprint("127.0.0.1", gone.port, Duration::from_secs(2)).unwrap_err();
    assert!(matches!(error, Error::ConnectionInterrupted { .. }));
}

#[test]
fn insecure_skips_the_check() {
    let server = FakeServer::new();
    let mut client = SmsDockClient::builder(&server.url())
        .password(PASSWORD)
        .insecure(true)
        .reconnect_delay(ms(10))
        .connect()
        .unwrap();
    assert!(client.insecure());
    assert!(client.ping().unwrap().time > 0);
    server.drop_connections(None, "");
    assert!(client.ping().unwrap().time > 0);
}

// ----------------------------------------------------------------------
// Requests
// ----------------------------------------------------------------------

#[test]
fn status() {
    let server = FakeServer::new();
    let mut client = connect(&server);
    let status = client.status().unwrap();
    assert_eq!(status.app.name, "SMS Dock");
    assert_eq!(status.app.protocol_version, 1);
    assert_eq!(status.delete_mode, "hidden");
    assert_eq!(status.sims[0].subscription_id, 1);
    assert_eq!(status.sims[1].number.as_deref(), Some(BEN));
    assert!(status.permissions.send_sms);
    assert_eq!(status.licence.remaining, None);
    assert!(!status.default_sms_app);
}

#[test]
fn ping() {
    let server = FakeServer::new();
    let mut client = connect(&server);
    let now = UNIX_EPOCH.elapsed().unwrap().as_millis() as i64;
    assert!((client.ping().unwrap().time - now).abs() < 60_000);
}

#[test]
fn conversations() {
    let server = FakeServer::new();
    let mut client = connect(&server);
    let page = client
        .conversations(&ConversationFilter::new(), 50, None)
        .unwrap();
    let addresses: Vec<&str> = page.items.iter().map(|c| c.address.as_str()).collect();
    assert_eq!(addresses, [ANNA, BEN, "A1"]);
    assert_eq!(page.next_cursor, None);
    let anna = &page.items[0];
    assert_eq!(anna.contact.as_deref(), Some("Anna Example"));
    assert_eq!((anna.message_count, anna.unread_count), (25, 3));
    assert_eq!(anna.snippet, "Anna 24");
    assert_eq!(
        server.last_request("conversations.list").to_string(),
        r#"{"id":"rs-2","action":"conversations.list","limit":50}"#
    );
}

#[test]
fn conversations_filters() {
    let server = FakeServer::new();
    let mut client = connect(&server);
    let mut addresses = |filter: ConversationFilter| -> Vec<String> {
        let page = client.conversations(&filter, 50, None).unwrap();
        page.items.into_iter().map(|c| c.address).collect()
    };
    assert_eq!(addresses(ConversationFilter::new().q("invoice")), [BEN]);
    assert_eq!(addresses(ConversationFilter::new().q("anna")), [ANNA]);
    assert_eq!(
        addresses(ConversationFilter::new().unread(true)),
        [ANNA, "A1"]
    );
    assert_eq!(addresses(ConversationFilter::new().sim(2)), [BEN]);
    let request = server.last_request("conversations.list");
    assert_eq!(request["sim"], 2);
    assert!(request.get("unread").is_none() && request.get("q").is_none());
}

#[test]
fn conversations_paging() {
    let server = FakeServer::new();
    let mut client = connect(&server);
    let filter = ConversationFilter::new();
    let first = client.conversations(&filter, 2, None).unwrap();
    assert_eq!(first.items.len(), 2);
    let cursor = first.next_cursor.expect("a cursor");
    let second = client.conversations(&filter, 2, Some(&cursor)).unwrap();
    assert_eq!(second.items[0].address, "A1");
    assert_eq!((second.items.len(), second.next_cursor), (1, None));
    let addresses: Vec<String> = client
        .iter_conversations(&filter, 1, None)
        .unwrap()
        .map(|conversation| conversation.unwrap().address)
        .collect();
    assert_eq!(addresses, [ANNA, BEN, "A1"]);
}

#[test]
fn last_ten_incoming_messages() {
    let server = FakeServer::new();
    let mut client = connect(&server);
    let page = client
        .messages(&all().direction(Direction::In), 10, None)
        .unwrap();
    assert_eq!(bodies(&page), anna((3..=12).rev().map(|index| index * 2)));
    assert!(page.items.iter().all(|m| m.direction == "in"));
    assert!(page.next_cursor.is_some());
    assert_eq!(
        server.last_request("messages.list").to_string(),
        r#"{"id":"rs-2","action":"messages.list","direction":"in","limit":10}"#
    );
}

#[test]
fn messages_of_a_number() {
    let server = FakeServer::new();
    let mut client = connect(&server);
    let cases = [
        (BEN, 3),
        ("+43 690 876-54321", 3),
        ("0043 690 87654321", 3),
        ("0690 / 876 54 321", 3),
        ("(0690) 876.54.321", 3),
        ("87654321", 3),
        ("654321", 0),
        ("+49 690 87654321", 0),
        ("a1", 1),
        ("A1", 1),
        ("A1 Telekom", 0),
    ];
    for (address, count) in cases {
        let page = client.messages(&all().address(address), 50, None).unwrap();
        assert_eq!(page.items.len(), count, "{address}");
    }
}

#[test]
fn conversation_wins_over_address() {
    let server = FakeServer::new();
    let mut client = connect(&server);
    let found = client
        .conversations(&ConversationFilter::new().q("invoice"), 50, None)
        .unwrap();
    let ben = found.items[0].id;
    let filter = all().conversation_id(ben).address(ANNA);
    let page = client.messages(&filter, 50, None).unwrap();
    assert!(page.items.iter().all(|m| m.address == BEN));
    let request = server.last_request("messages.list");
    assert_eq!(
        (&request["conversationId"], &request["address"]),
        (&json!(ben), &json!(ANNA))
    );
}

#[test]
fn messages_filters() {
    let server = FakeServer::new();
    let mut client = connect(&server);
    let mut list =
        |filter: MessageFilter, limit: u32| client.messages(&filter, limit, None).unwrap();
    assert_eq!(list(all().direction(Direction::Out), 200).items.len(), 13);
    assert_eq!(
        bodies(&list(all().q("INVOICE"), 50)),
        ["Thanks for the invoice", "Your invoice is attached"]
    );
    assert_eq!(list(all().sim(2), 50).items.len(), 3);
    assert_eq!(
        bodies(&list(all().unread(true), 50)),
        ["Anna 24", "Anna 23", "Anna 22", "Your bill is ready"]
    );
    let combined = all().address(ANNA).direction(Direction::In).q("Anna 1");
    assert_eq!(
        bodies(&list(combined, 3)),
        ["Anna 18", "Anna 16", "Anna 14"]
    );
    assert_eq!(
        server.last_request("messages.list").to_string(),
        format!(
            r#"{{"id":"rs-6","action":"messages.list","address":"{ANNA}","direction":"in","q":"Anna 1","limit":3}}"#
        )
    );
}

#[test]
fn messages_time_range() {
    let server = FakeServer::new();
    let mut client = connect(&server);
    let filter = all().since(BASE_TIME + 3000).until(BASE_TIME + 5000);
    let page = client.messages(&filter, 50, None).unwrap();
    assert_eq!(bodies(&page), ["Anna 5", "Anna 4", "Anna 3"]);
    let request = server.last_request("messages.list");
    assert_eq!(request["from"], BASE_TIME + 3000);
    assert_eq!(request["to"], BASE_TIME + 5000);

    let moment = UNIX_EPOCH + ms((BASE_TIME + 23_000) as u64);
    let page = client
        .messages(&all().since_time(moment), 50, None)
        .unwrap();
    assert_eq!(bodies(&page), ["Anna 24", "Anna 23"]);
    assert_eq!(
        server.last_request("messages.list")["from"],
        BASE_TIME + 23_000
    );
}

#[test]
fn messages_paging() {
    let server = FakeServer::new();
    let mut client = connect(&server);
    let filter = all().address(ANNA);
    let first = client.messages(&filter, 10, None).unwrap();
    let second = client
        .messages(&filter, 10, first.next_cursor.as_deref())
        .unwrap();
    let third = client
        .messages(&filter, 10, second.next_cursor.as_deref())
        .unwrap();
    let found: Vec<&str> = [&first, &second, &third]
        .into_iter()
        .flat_map(bodies)
        .collect();
    assert_eq!(found, anna((0..25).rev()));
    assert_eq!(third.next_cursor, None);
}

#[test]
fn iter_messages_follows_the_cursor() {
    let server = FakeServer::new();
    let mut client = connect(&server);
    let messages: Vec<Message> = client
        .iter_messages(&all().address(ANNA), 10, None)
        .unwrap()
        .collect::<smsdock::Result<_>>()
        .unwrap();
    let found: Vec<&str> = messages.iter().map(|m| m.body.as_str()).collect();
    assert_eq!(found, anna((0..25).rev()));
    let requests = server.state().requests_of("messages.list");
    let cursors: Vec<Value> = requests.iter().map(|r| r["cursor"].clone()).collect();
    assert_eq!(
        cursors,
        [
            Value::Null,
            json!(format!("{}:{}", BASE_TIME + 15000, messages[9].id.unwrap())),
            json!(format!("{}:{}", BASE_TIME + 5000, messages[19].id.unwrap())),
        ]
    );
    assert!(requests.iter().all(|r| r["limit"] == 10));
}

#[test]
fn iter_messages_is_lazy_and_bounded() {
    let server = FakeServer::new();
    let mut client = connect(&server);
    let iterator = client.iter_messages(&all(), 10, Some(12)).unwrap();
    assert_eq!(server.count("messages.list"), 0);
    assert_eq!(iterator.count(), 12);
    let limits: Vec<Value> = server
        .state()
        .requests_of("messages.list")
        .iter()
        .map(|r| r["limit"].clone())
        .collect();
    assert_eq!(limits, [json!(10), json!(2)]);
    assert_eq!(
        client.iter_messages(&all(), 50, Some(0)).unwrap().count(),
        0
    );
    assert_eq!(server.count("messages.list"), 2);
    assert_eq!(client.iter_messages(&all(), 200, None).unwrap().count(), 29);
}

#[test]
fn iter_messages_refuses_a_cursor_that_does_not_move() {
    let server = FakeServer::new();
    let mut client = connect(&server);
    let page = json!({"items": [message_json(1)], "nextCursor": "again"});
    server.answer_next("messages.list", page.clone());
    server.answer_next("messages.list", page);
    let results: Vec<smsdock::Result<Message>> =
        client.iter_messages(&all(), 50, None).unwrap().collect();
    assert_eq!(results.len(), 3);
    assert!(results[0].is_ok() && results[1].is_ok());
    assert!(matches!(&results[2], Err(Error::Protocol(text)) if text.contains("cursor")));
    assert_eq!(server.count("messages.list"), 2);
}

#[test]
fn iter_messages_refuses_a_page_without_items() {
    let server = FakeServer::new();
    let mut client = connect(&server);
    server.answer_next("messages.list", json!({"nextCursor": null}));
    let results: Vec<smsdock::Result<Message>> =
        client.iter_messages(&all(), 50, None).unwrap().collect();
    assert_eq!(results.len(), 1);
    assert!(matches!(&results[0], Err(Error::Protocol(text)) if text.contains("items")));
}

#[test]
fn a_result_of_another_shape_is_a_protocol_error() {
    let server = FakeServer::new();
    let mut client = connect(&server);
    server.answer_next("messages.get", json!({"id": 1, "body": "a secret text"}));
    let error = client.get_message(1).unwrap_err();
    assert!(matches!(&error, Error::Protocol(text) if !text.contains("secret")));
    assert!(client.ping().is_ok());
}

#[test]
fn pages_may_be_shorter_than_the_limit() {
    let server = FakeServer::new();
    let mut client = connect(&server);
    server.answer_next(
        "messages.list",
        json!({"items": [message_json(1)], "nextCursor": "1:1"}),
    );
    server.answer_next("messages.list", json!({"items": [], "nextCursor": "1:0"}));
    server.answer_next(
        "messages.list",
        json!({"items": [message_json(2)], "nextCursor": null}),
    );
    let ids: Vec<Option<i64>> = client
        .iter_messages(&all(), 50, None)
        .unwrap()
        .map(|message| message.unwrap().id)
        .collect();
    assert_eq!(ids, [Some(1), Some(2)]);
}

#[test]
fn refuses_wrong_limits_before_anything_is_sent() {
    let server = FakeServer::new();
    let mut client = connect(&server);
    for limit in [0, 201] {
        assert!(is_invalid(&client.messages(&all(), limit, None)));
        assert!(is_invalid(&client.conversations(
            &ConversationFilter::new(),
            limit,
            None
        )));
        assert!(is_invalid(&client.contacts(None, limit, None)));
    }
    // Iterators check their arguments at once.
    assert!(is_invalid(&client.iter_messages(&all(), 500, None)));
    assert!(is_invalid(&client.iter_contacts(None, 0, None)));
    assert_eq!(server.actions(), ["authenticate"]);
}

#[test]
fn get_message() {
    let server = FakeServer::new();
    let mut client = connect(&server);
    let newest = client.messages(&all(), 1, None).unwrap().items.remove(0);
    let id = newest.id.unwrap();
    assert_eq!(client.get_message(id).unwrap(), newest);
    assert!(newest.extra.is_empty());
    assert_eq!(
        server.last_request("messages.get").to_string(),
        format!(r#"{{"id":"rs-3","action":"messages.get","messageId":{id}}}"#)
    );
}

#[test]
fn get_message_not_found() {
    let server = FakeServer::new();
    let mut client = connect(&server);
    let error = client.get_message(1).unwrap_err();
    assert_eq!(error.code(), Some("not_found"));
    assert!(error.to_string().contains("not_found"));
    assert!(!error.is_authentication());
    assert!(client.ping().is_ok());
}

#[test]
fn send() {
    let server = FakeServer::new();
    let mut client = connect(&server);
    let result = client
        .send("+43 690 876-54321", "All systems running.", None, None)
        .unwrap();
    assert_eq!(result.to, BEN);
    assert_eq!((result.parts, result.status.as_str()), (1, "queued"));
    let request_id = &result.request_id;
    assert_eq!(request_id.len(), 32);
    assert!(request_id.bytes().all(|byte| byte.is_ascii_hexdigit()));
    assert_eq!(
        server.last_request("messages.send"),
        json!({
            "id": "rs-2", "action": "messages.send", "to": "+43 690 876-54321",
            "body": "All systems running.", "requestId": request_id,
        })
    );
    assert_eq!(
        server.sent(),
        [json!({
            "requestId": request_id, "to": BEN, "body": "All systems running.", "sim": 1,
        })]
    );
}

#[test]
fn send_creates_a_new_request_id_each_time() {
    let server = FakeServer::new();
    let mut client = connect(&server);
    let first = client.send(BEN, "one", None, None).unwrap();
    let second = client.send(BEN, "one", None, None).unwrap();
    assert_ne!(first.request_id, second.request_id);
    assert_eq!(server.sent().len(), 2);
}

#[test]
fn send_with_own_request_id_and_sim() {
    let server = FakeServer::new();
    let mut client = connect(&server);
    let id = "order-42.confirm_1";
    let result = client.send(BEN, "Grüße", Some(2), Some(id)).unwrap();
    assert_eq!(
        (result.request_id.as_str(), result.status.as_str()),
        (id, "queued")
    );
    assert_eq!(server.sent()[0]["sim"], 2);
    assert_eq!(server.sent()[0]["body"], "Grüße");
    assert_eq!(client.wait_for_result(id, secs(5)).unwrap().status, "sent");
    // A repeat is not sent again; it tells the status of today.
    let again = client.send(BEN, "Grüße", Some(2), Some(id)).unwrap();
    assert_eq!(again.status, "sent");
    assert_eq!((again.to, again.parts), (result.to, result.parts));
    assert_eq!(server.sent().len(), 1);
    // Frames are compact JSON with the text as it is.
    assert_eq!(
        server.state().requests_of("messages.send")[0].to_string(),
        format!(
            r#"{{"id":"rs-2","action":"messages.send","to":"{BEN}","body":"Grüße","requestId":"{id}","sim":2}}"#
        )
    );
}

#[test]
fn repeat_with_the_number_written_differently() {
    let server = FakeServer::new();
    let mut client = connect(&server);
    let first = client
        .send("+43 690 876 54 321", "Hello", None, Some("r-1"))
        .unwrap();
    assert_eq!(first.to, BEN);
    for number in ["0043 (690) 876-54321", "0690/876.54.321", BEN] {
        assert_eq!(
            client.send(number, "Hello", None, Some("r-1")).unwrap().to,
            BEN
        );
    }
    assert_eq!(server.sent().len(), 1);
}

#[test]
fn request_id_of_another_message() {
    let server = FakeServer::new();
    let mut client = connect(&server);
    client.send(BEN, "Hello", None, Some("r-1")).unwrap();
    for (to, body) in [(BEN, "Another text"), (ANNA, "Hello")] {
        assert_eq!(
            code(client.send(to, body, None, Some("r-1"))),
            "invalid_request"
        );
    }
    assert_eq!(server.sent().len(), 1);
}

#[test]
fn repeat_after_a_failure_does_not_send_again() {
    let server = FakeServer::new();
    let mut client = connect(&server);
    let number = "+43123456789";
    server.state().fail_numbers.insert(number.to_owned());
    let first = client.send(number, "Hello", None, Some("r-1")).unwrap();
    assert_eq!(first.status, "queued");
    assert_eq!(
        client.wait_for_result("r-1", secs(5)).unwrap().status,
        "failed"
    );
    server.state().fail_numbers.clear();
    let again = client.send(number, "Hello", None, Some("r-1")).unwrap();
    assert_eq!(again.status, "failed");
    assert_eq!(server.sent().len(), 1);
    // A new attempt needs a new request id.
    let retry = client.send(number, "Hello", None, Some("r-2")).unwrap();
    let outcome = client.wait_for_result(&retry.request_id, secs(5)).unwrap();
    assert_eq!(outcome.status, "sent");
    assert_eq!(server.sent().len(), 2);
}

#[test]
fn request_ids_outlive_a_restart_of_the_app() {
    let server = FakeServer::new();
    let mut client = connect(&server);
    client.send(BEN, "Hello", None, Some("r-1")).unwrap();
    assert_eq!(
        client.wait_for_result("r-1", secs(5)).unwrap().status,
        "sent"
    );
    server.restart();
    assert_eq!(
        client.send(BEN, "Hello", None, Some("r-1")).unwrap().status,
        "sent"
    );
    assert_eq!(server.sent().len(), 1);
}

#[test]
fn status_of_a_long_message() {
    let server = FakeServer::start(Options {
        send_delay: ms(600),
        ..Options::default()
    });
    let mut client = connect(&server);
    let result = client.send(BEN, &"x".repeat(400), None, None).unwrap();
    assert_eq!((result.parts, result.status.as_str()), (3, "queued"));
    let id = result.request_id;
    assert!(wait_until(
        || server.outbox_status(&id) == "sending",
        ms(3000)
    ));
    let entry = client.message_status(&id).unwrap();
    assert_eq!((entry.status.as_str(), entry.parts_sent), ("sending", 1));
    // Some parts are left: this is not the result yet.
    let entry = client.wait_for_result(&id, secs(5)).unwrap();
    assert_eq!((entry.status.as_str(), entry.parts_sent), ("sent", 3));
}

#[test]
fn send_group() {
    let server = FakeServer::new();
    let mut client = connect(&server);
    let result = client
        .send_group(
            &["+43 690 876-54321", ANNA, BEN],
            "Hello all",
            Some(2),
            Some("news-1"),
        )
        .unwrap();
    assert_eq!(result.request_id, "news-1");
    assert_eq!(result.to, [BEN, ANNA]);
    assert_eq!((result.parts, result.status.as_str()), (1, "queued"));
    let members: Vec<(&str, &str, i64, &str)> = result
        .messages
        .iter()
        .map(|m| {
            (
                m.request_id.as_str(),
                m.to.as_str(),
                m.parts,
                m.status.as_str(),
            )
        })
        .collect();
    assert_eq!(
        members,
        [
            ("news-1.1", BEN, 1, "queued"),
            ("news-1.2", ANNA, 1, "queued")
        ]
    );
    assert_eq!(result.failed_count(), 0);
    assert_eq!(
        server.last_request("messages.sendGroup"),
        json!({
            "id": "rs-2", "action": "messages.sendGroup",
            "to": ["+43 690 876-54321", ANNA, BEN], "body": "Hello all",
            "requestId": "news-1", "sim": 2,
        })
    );
    assert_eq!(
        server.sent(),
        [
            json!({"requestId": "news-1.1", "to": BEN, "body": "Hello all", "sim": 2, "group": "news-1"}),
            json!({"requestId": "news-1.2", "to": ANNA, "body": "Hello all", "sim": 2, "group": "news-1"}),
        ]
    );
    assert_eq!(
        client.wait_for_result("news-1.2", secs(5)).unwrap().status,
        "sent"
    );
    assert_eq!(client.message_status("news-1.1").unwrap().to, BEN);
    assert_eq!(code(client.message_status("news-1")), "not_found");
}

#[test]
fn send_group_creates_a_request_id() {
    let server = FakeServer::new();
    let mut client = connect(&server);
    let result = client.send_group(&[BEN], "one", None, None).unwrap();
    assert_eq!(result.request_id.len(), 32);
    assert!(result
        .request_id
        .bytes()
        .all(|byte| byte.is_ascii_hexdigit()));
    assert_eq!(
        result.messages[0].request_id,
        format!("{}.1", result.request_id)
    );
}

#[test]
fn send_group_repeats_only_once() {
    let server = FakeServer::new();
    let mut client = connect(&server);
    let first = client
        .send_group(&[BEN, ANNA], "Same", None, Some("news-2"))
        .unwrap();
    let again = client
        .send_group(&["0690/876.54.321", ANNA], "Same", None, Some("news-2"))
        .unwrap();
    assert_eq!(again, first);
    assert_eq!(server.sent().len(), 2);
    assert_eq!(
        client.wait_for_result("news-2.1", secs(5)).unwrap().status,
        "sent"
    );
    let later = client
        .send_group(&[ANNA, BEN], "Same", None, Some("news-2"))
        .unwrap();
    assert!(["sent", "delivered"].contains(&later.messages[0].status.as_str()));
    assert_eq!(
        later
            .messages
            .iter()
            .map(|m| m.request_id.as_str())
            .collect::<Vec<_>>(),
        ["news-2.1", "news-2.2"]
    );
    for (to, body) in [(&[BEN, ANNA][..], "Other"), (&[BEN][..], "Same")] {
        assert_eq!(
            code(client.send_group(to, body, None, Some("news-2"))),
            "invalid_request"
        );
    }
    assert_eq!(server.sent().len(), 2);
}

#[test]
fn send_group_refuses_the_whole_group() {
    let server = FakeServer::new();
    let mut client = connect(&server);
    let long = "x".repeat(1531);
    let seven_parts = "x".repeat(1000);
    let cases: [(&[&str], &str, &str); 3] = [
        (&[BEN, "abc"], "Hello", "invalid_number"),
        (&[BEN, ANNA], long.as_str(), "message_too_long"),
        (
            &[BEN, ANNA, "+43123456789"],
            seven_parts.as_str(),
            "invalid_request",
        ),
    ];
    for (to, body, expected) in cases {
        assert_eq!(code(client.send_group(to, body, None, None)), expected);
    }
    assert!(server.sent().is_empty());
}

#[test]
fn send_group_goes_on_after_a_refused_member() {
    let server = FakeServer::new();
    let mut client = connect(&server);
    server.state().refuse_numbers.insert(ANNA.to_owned());
    let result = client
        .send_group(&[BEN, ANNA], "Hello", None, Some("news-3"))
        .unwrap();
    let statuses: Vec<&str> = result.messages.iter().map(|m| m.status.as_str()).collect();
    assert_eq!(statuses, ["queued", "failed"]);
    assert_eq!(result.status, "queued");
    assert_eq!(result.failed_count(), 1);
    assert_eq!(
        client.wait_for_result("news-3.1", secs(5)).unwrap().status,
        "sent"
    );
    assert_eq!(client.message_status("news-3.2").unwrap().status, "failed");
    server.state().refuse_numbers.insert(BEN.to_owned());
    let all = client
        .send_group(&[BEN, ANNA], "Hello", None, Some("news-4"))
        .unwrap();
    assert_eq!(all.status, "failed");
}

#[test]
fn send_group_refuses_bad_values() {
    let server = FakeServer::new();
    let mut client = connect(&server);
    let many: Vec<String> = (0..21).map(|i| format!("+4369900000{i:02}")).collect();
    let many: Vec<&str> = many.iter().map(String::as_str).collect();
    let cases: [(&[&str], &str, Option<&str>); 6] = [
        (&[], "Hello", None),
        (&[BEN, " "], "Hello", None),
        (&[BEN], "", None),
        (&[BEN], "Hello", Some("has space")),
        (&[BEN], "Hello", Some("x".repeat(62).leak())),
        (many.as_slice(), "Hello", None),
    ];
    for (to, body, request_id) in cases {
        assert!(
            is_invalid(&client.send_group(to, body, None, request_id)),
            "{to:?} {body:?} {request_id:?}"
        );
    }
    assert!(server.sent().is_empty());
    assert_eq!(server.actions(), ["authenticate"]);
}

#[test]
fn send_refuses_empty_values_and_malformed_request_ids() {
    let server = FakeServer::new();
    let mut client = connect(&server);
    for (to, body) in [("", "text"), ("   ", "text"), (BEN, "")] {
        assert!(is_invalid(&client.send(to, body, None, None)));
    }
    let long = "x".repeat(65);
    for request_id in ["", "a b", "ä", long.as_str(), "a/b"] {
        let result = client.send(BEN, "text", None, Some(request_id));
        assert!(matches!(&result, Err(Error::InvalidArgument(m)) if m.contains("request_id")));
    }
    assert!(server.sent().is_empty());
    assert_eq!(server.actions(), ["authenticate"]);
}

#[test]
fn send_errors() {
    let server = FakeServer::new();
    let mut client = connect(&server);
    let plain = "x".repeat(1531);
    let unicode = "ü".repeat(671);
    let cases = [
        ("not a number", "text", None, "invalid_number"),
        (BEN, plain.as_str(), None, "message_too_long"),
        (BEN, unicode.as_str(), None, "message_too_long"),
        (BEN, "text", Some(7), "sim_unavailable"),
    ];
    for (to, body, sim, expected) in cases {
        assert_eq!(code(client.send(to, body, sim, None)), expected);
    }
    assert!(server.sent().is_empty());
    // The longest messages that fit.
    assert_eq!(
        client
            .send(BEN, &"x".repeat(1530), None, None)
            .unwrap()
            .parts,
        10
    );
    assert_eq!(
        client
            .send(BEN, &"ü".repeat(670), None, None)
            .unwrap()
            .parts,
        10
    );
}

#[test]
fn error_details() {
    let server = FakeServer::new();
    let mut client = connect(&server);
    server.fail_next(
        "messages.send",
        "permission_missing",
        "The permission to send SMS is missing.",
        json!({"permission": "sendSms"}),
    );
    let error = client.send(BEN, "text", None, None).unwrap_err();
    assert_eq!(error.code(), Some("permission_missing"));
    assert_eq!(error.permission(), Some("sendSms"));
    assert_eq!(error.retry_after(), None);
    assert_eq!(
        error.to_string(),
        "permission_missing: The permission to send SMS is missing."
    );
    let Error::Api {
        message, details, ..
    } = &error
    else {
        panic!("not an API error");
    };
    assert_eq!(message, "The permission to send SMS is missing.");
    assert_eq!(
        Value::Object(details.clone()),
        json!({"permission": "sendSms"})
    );

    server.fail_next(
        "messages.send",
        "quota_exceeded",
        "Daily limit reached.",
        json!({}),
    );
    assert_eq!(code(client.send(BEN, "text", None, None)), "quota_exceeded");
    assert!(client.status().is_ok());
}

#[test]
fn message_status() {
    let server = FakeServer::new();
    let mut client = connect(&server);
    let id = client
        .send(BEN, &"x".repeat(200), None, None)
        .unwrap()
        .request_id;
    assert!(wait_until(|| server.outbox_status(&id) == "sent", ms(3000)));
    let entry = client.message_status(&id).unwrap();
    assert_eq!(entry.status, "sent");
    assert_eq!((entry.parts, entry.parts_sent), (2, 2));
    assert_eq!((entry.source.as_str(), entry.error_code), ("api", None));
    let stored = client.get_message(entry.message_id.unwrap()).unwrap();
    assert_eq!(stored.request_id.as_deref(), Some(id.as_str()));
    assert_eq!(code(client.message_status("unknown")), "not_found");
}

#[test]
fn wait_for_result_by_polling() {
    let server = FakeServer::start(Options {
        send_delay: ms(200),
        ..Options::default()
    });
    let mut client = connect(&server);
    let id = client.send(BEN, "text", None, None).unwrap().request_id;
    let options = WaitOptions {
        timeout: secs(5),
        poll_interval: ms(50),
        ..WaitOptions::default()
    };
    let entry = client.wait_for_result_with(&id, &options).unwrap();
    assert_eq!(
        (entry.status.as_str(), entry.request_id.as_str()),
        ("sent", id.as_str())
    );
    assert!(client.subscription().is_none());
    assert!(server.count("messages.status") >= 2);
}

#[test]
fn wait_for_result_wakes_up_on_the_event() {
    let server = FakeServer::start(Options {
        send_delay: ms(2000),
        ..Options::default()
    });
    let mut client = connect(&server);
    client
        .subscribe(&["sms.queued", "sms.sent", "sms.failed"], None)
        .unwrap();
    let id = client.send(BEN, "text", None, None).unwrap().request_id;
    let started = Instant::now();
    let options = WaitOptions {
        timeout: secs(10),
        poll_interval: Duration::from_secs(8),
        ..WaitOptions::default()
    };
    let entry = client.wait_for_result_with(&id, &options).unwrap();
    // The client asks after 0.1, 0.3, 0.7, 1.5 and 3.1 seconds. The
    // message is sent after 2 seconds; only the event can tell that early.
    let waited = started.elapsed();
    assert!(ms(1900) < waited && waited < ms(2900), "{waited:?}");
    assert_eq!(entry.status, "sent");
    // The events are still there for the application.
    let names: Vec<String> = events(&mut client, ms(50))
        .into_iter()
        .map(|e| e.event)
        .collect();
    assert_eq!(names, ["sms.queued", "sms.sent"]);
}

#[test]
fn wait_for_result_failed() {
    let server = FakeServer::new();
    let mut client = connect(&server);
    server.state().fail_numbers.insert("+43123456".to_owned());
    client.subscribe(&["sms.failed"], None).unwrap();
    let id = client
        .send("+43123456", "text", None, None)
        .unwrap()
        .request_id;
    let entry = client.wait_for_result(&id, secs(5)).unwrap();
    assert_eq!(
        (entry.status.as_str(), entry.error_code),
        ("failed", Some(1))
    );
    let event = client.wait_for_event(&["sms.failed"], secs(1)).unwrap();
    assert_eq!(event.request_id(), Some(id.as_str()));
    assert_eq!(
        event.data,
        json!({
            "requestId": id, "id": null, "conversationId": null,
            "to": "+43123456", "errorCode": 1, "reason": "generic_failure",
        })
    );
}

#[test]
fn wait_for_result_delivered() {
    let server = FakeServer::start(Options {
        send_delay: ms(100),
        delivery_reports: true,
        ..Options::default()
    });
    let mut client = connect(&server);
    client.subscribe(&["*"], None).unwrap();
    let id = client.send(BEN, "text", None, None).unwrap().request_id;
    let options = WaitOptions {
        timeout: secs(5),
        delivered: true,
        poll_interval: ms(50),
    };
    let entry = client.wait_for_result_with(&id, &options).unwrap();
    assert_eq!(entry.status, "delivered");
    let event = client.wait_for_event(&["sms.delivered"], secs(1)).unwrap();
    let message = client.get_message(entry.message_id.unwrap()).unwrap();
    assert_eq!(
        event.data,
        json!({
            "requestId": id, "id": entry.message_id,
            "conversationId": message.conversation_id, "to": BEN,
        })
    );
    assert_eq!(message.status, "delivered");
    let sent = client.wait_for_event(&["sms.sent"], secs(1)).unwrap();
    assert_eq!(sent.message().unwrap().id, entry.message_id);
}

#[test]
fn wait_for_result_times_out() {
    let server = FakeServer::start(Options {
        send_delay: Duration::from_secs(30),
        ..Options::default()
    });
    let mut client = connect(&server);
    let id = client.send(BEN, "text", None, None).unwrap().request_id;
    let options = WaitOptions {
        timeout: Some(ms(300)),
        poll_interval: ms(100),
        ..WaitOptions::default()
    };
    let error = client.wait_for_result_with(&id, &options).unwrap_err();
    assert!(matches!(&error, Error::Timeout(text) if text.contains("queued")));
    assert!(server.count("messages.status") >= 2);
    let zero = WaitOptions {
        poll_interval: Duration::ZERO,
        ..WaitOptions::default()
    };
    assert!(is_invalid(&client.wait_for_result_with(&id, &zero)));
    assert!(client.ping().is_ok());
}

#[test]
fn delete_message() {
    let server = FakeServer::new();
    let mut client = connect(&server);
    let id = client.messages(&all(), 1, None).unwrap().items[0]
        .id
        .unwrap();
    let result = client.delete_message(id).unwrap();
    assert_eq!((result.deleted, result.mode.as_str()), (1, "hidden"));
    assert_eq!(
        server.last_request("messages.delete").to_string(),
        format!(r#"{{"id":"rs-3","action":"messages.delete","messageId":{id}}}"#)
    );
    assert_eq!(server.state().messages.len(), 28);
    assert_eq!(code(client.delete_message(id)), "not_found");
}

#[test]
fn delete_conversation() {
    let server = FakeServer::new();
    let mut client = connect(&server);
    server.state().delete_mode = "provider".to_owned();
    let filter = ConversationFilter::new().q("anna");
    let anna = client.conversations(&filter, 50, None).unwrap().items[0].id;
    let result = client.delete_conversation(anna).unwrap();
    assert_eq!((result.deleted, result.mode.as_str()), (25, "provider"));
    assert_eq!(
        server.last_request("conversations.delete").to_string(),
        format!(r#"{{"id":"rs-3","action":"conversations.delete","conversationId":{anna}}}"#)
    );
    let left = client
        .conversations(&ConversationFilter::new(), 50, None)
        .unwrap();
    let addresses: Vec<&str> = left.items.iter().map(|c| c.address.as_str()).collect();
    assert_eq!(addresses, [BEN, "A1"]);
    assert_eq!(code(client.delete_conversation(anna)), "not_found");
}

#[test]
fn delete_sends_an_event() {
    let server = FakeServer::new();
    let mut client = connect(&server);
    client.subscribe(&["sms.deleted"], None).unwrap();
    let newest = client.messages(&all(), 1, None).unwrap().items.remove(0);
    client.delete_message(newest.id.unwrap()).unwrap();
    let event = client.wait_for_event(&["sms.deleted"], secs(2)).unwrap();
    assert_eq!(
        event.data,
        json!({
            "id": newest.id, "conversationId": newest.conversation_id,
            "address": newest.address, "mode": "hidden",
        })
    );
    client
        .delete_conversation(newest.conversation_id.unwrap())
        .unwrap();
    let event = client.wait_for_event(&["sms.deleted"], secs(2)).unwrap();
    assert_eq!(
        event.data,
        json!({
            "id": null, "conversationId": newest.conversation_id,
            "address": ANNA, "mode": "hidden",
        })
    );
}

#[test]
fn mark_read() {
    let server = FakeServer::new();
    let mut client = connect(&server);
    let filter = ConversationFilter::new().q("anna");
    let anna = client
        .conversations(&filter, 50, None)
        .unwrap()
        .items
        .remove(0);
    assert_eq!(anna.unread_count, 3);
    // Android lets only the default SMS app change messages.
    assert!(!client.status().unwrap().default_sms_app);
    assert_eq!(code(client.mark_read(anna.id)), "not_default_app");
    assert_eq!(
        client.conversations(&filter, 50, None).unwrap().items[0].unread_count,
        3
    );

    server.state().delete_mode = "provider".to_owned();
    assert!(client.mark_read(anna.id).unwrap().ok);
    assert_eq!(
        server.last_request("conversations.markRead")["conversationId"],
        anna.id
    );
    assert_eq!(
        client.conversations(&filter, 50, None).unwrap().items[0].unread_count,
        0
    );
    let unread = all().address(ANNA).unread(true);
    assert!(client.messages(&unread, 50, None).unwrap().items.is_empty());
    assert_eq!(code(client.mark_read(1)), "not_found");
}

#[test]
fn contacts() {
    let server = FakeServer::new();
    let mut client = connect(&server);
    server
        .state()
        .add_contact("Zoe Example", "+43660111222", "Mobile");
    let names = |page: &Page<smsdock::Contact>| -> Vec<String> {
        page.items.iter().map(|c| c.name.clone()).collect()
    };
    let page = client.contacts(None, 50, None).unwrap();
    assert_eq!(names(&page), ["Anna Example", "Ben Example", "Zoe Example"]);
    let number = &page.items[1].numbers[0];
    assert_eq!(
        (number.number.as_str(), number.label.as_deref()),
        (BEN, Some("Work"))
    );
    assert_eq!(
        names(&client.contacts(Some("zoe"), 50, None).unwrap()),
        ["Zoe Example"]
    );
    assert_eq!(
        names(&client.contacts(Some("5678"), 50, None).unwrap()),
        ["Anna Example"]
    );
    let first = client.contacts(None, 2, None).unwrap();
    let second = client
        .contacts(None, 2, first.next_cursor.as_deref())
        .unwrap();
    assert_eq!(names(&second), ["Zoe Example"]);
    let two: Vec<String> = client
        .iter_contacts(None, 1, Some(2))
        .unwrap()
        .map(|contact| contact.unwrap().name)
        .collect();
    assert_eq!(two, ["Anna Example", "Ben Example"]);
}

#[test]
fn generic_request() {
    let server = FakeServer::new();
    let mut client = connect(&server);
    let page = client
        .request("messages.list", json!({"limit": 1, "direction": "out"}))
        .unwrap();
    assert_eq!(page["items"][0]["body"], "Anna 23");
    assert_eq!(
        code(client.request("messages.archive", Value::Null)),
        "unknown_action"
    );
    // The id of a request belongs to the client.
    assert!(is_invalid(
        &client.request("messages.get", json!({"id": 1}))
    ));
    assert!(is_invalid(
        &client.send_request("ping", json!({"id": "mine"}))
    ));
    assert_eq!(
        server.actions(),
        ["authenticate", "messages.list", "messages.archive"]
    );
}

#[test]
fn answers_may_be_larger_than_64_kib() {
    let server = FakeServer::new();
    let mut client = connect(&server);
    for index in 0..200 {
        let body = format!("{index} {}", "x".repeat(1500));
        server
            .state()
            .add_message("+436601234567", &body, "in", None, true, Some(1), None);
    }
    let page = client
        .messages(&all().address("+436601234567"), 200, None)
        .unwrap();
    assert_eq!(page.items.len(), 200);
    assert!(page.items.iter().map(|m| m.body.len()).sum::<usize>() > 300_000);
}

#[test]
fn refuses_requests_over_64_kib() {
    let server = FakeServer::new();
    let mut client = connect(&server);
    let result = client.send(BEN, &"x".repeat(70_000), None, None);
    assert!(matches!(&result, Err(Error::InvalidArgument(text)) if text.contains("64 KiB")));
    assert_eq!(server.actions(), ["authenticate"]);
    assert!(client.ping().is_ok());
}

// ----------------------------------------------------------------------
// Events
// ----------------------------------------------------------------------

#[test]
fn subscribe_and_unsubscribe() {
    let server = FakeServer::new();
    let mut client = connect(&server);
    assert!(client.subscription().is_none());
    let first = client.subscribe(&["sms.received"], None).unwrap();
    assert_eq!(
        (first.events.clone(), first.address.clone()),
        (vec!["sms.received".to_owned()], None)
    );
    // subscribe adds names; its address replaces the filter.
    let second = client
        .subscribe(&["sms.sent", "sms.failed"], Some(ANNA))
        .unwrap();
    assert_eq!(second.events, ["sms.received", "sms.sent", "sms.failed"]);
    assert_eq!(second.address.as_deref(), Some(ANNA));
    assert_eq!(client.subscription(), Some(&second));
    let third = client.unsubscribe(&["sms.sent"]).unwrap();
    assert_eq!(third.events, ["sms.received", "sms.failed"]);
    assert_eq!(third.address.as_deref(), Some(ANNA));
    // Without address the filter is removed.
    let fourth = client.subscribe(&["sms.queued"], None).unwrap();
    assert_eq!(fourth.events, ["sms.received", "sms.queued", "sms.failed"]);
    assert_eq!(fourth.address, None);
    assert!(server.last_request("subscribe").get("address").is_none());
    // When no name is left, the filter is removed too.
    client.subscribe(&["sms.queued"], Some(BEN)).unwrap();
    let none = client.unsubscribe(&["*"]).unwrap();
    assert!(none.events.is_empty() && none.address.is_none());
    assert_eq!(client.subscription(), Some(&none));
    // In the answer * is replaced by the names it stands for.
    assert_eq!(client.subscribe(&["*"], None).unwrap().events, EVENT_NAMES);
    assert_eq!(
        server.last_request("subscribe").to_string(),
        r#"{"id":"rs-8","action":"subscribe","events":["*"]}"#
    );
    let rest = client
        .unsubscribe(&["sms.deleted", "conversations.changed"])
        .unwrap();
    assert_eq!(rest.events, EVENT_NAMES[..5]);
    assert!(is_invalid(&client.subscribe(&[], None)));
    assert_eq!(
        code(client.subscribe(&["sms.unknown"], None)),
        "invalid_request"
    );
}

#[test]
fn a_new_connection_gets_no_events() {
    let server = FakeServer::new();
    let mut client = connect(&server);
    server.receive_sms(ANNA, "Nobody listens");
    assert!(events(&mut client, ms(200)).is_empty());
}

#[test]
fn incoming_sms() {
    let server = FakeServer::new();
    let mut client = connect(&server);
    client.subscribe(&["sms.received"], None).unwrap();
    let stored = server.receive_sms(ANNA, "STATUS");
    let event = client.wait_for_event(&["sms.received"], secs(2)).unwrap();
    assert_eq!((event.event.as_str(), event.seq), ("sms.received", 1));
    assert_eq!(event.data["message"], stored);
    let message = event.message().unwrap();
    assert_eq!(message.address, ANNA);
    assert_eq!(message.contact.as_deref(), Some("Anna Example"));
    assert_eq!((message.direction.as_str(), message.read), ("in", false));
    assert_eq!(
        client.get_message(message.id.unwrap()).unwrap().body,
        "STATUS"
    );
}

#[test]
fn react_to_an_incoming_sms() {
    let server = FakeServer::new();
    let mut client = connect(&server);
    client.subscribe(&["sms.received"], None).unwrap();
    server.receive_sms(ANNA, "STATUS");
    server.receive_sms(BEN, "STATUS");
    // Requests can be sent between two events.
    while let Some(event) = client.next_event(Some(ms(500))).unwrap() {
        let message = event.message().unwrap();
        client
            .send(&message.address, "All systems running.", message.sim, None)
            .unwrap();
    }
    let sent = server.sent();
    let recipients: Vec<&Value> = sent.iter().map(|message| &message["to"]).collect();
    assert_eq!(recipients, [&json!(ANNA), &json!(BEN)]);
    assert!(sent
        .iter()
        .all(|message| message["body"] == "All systems running."));
}

#[test]
fn events_between_request_and_response_are_queued() {
    let server = FakeServer::new();
    let mut client = connect(&server);
    client.subscribe(&["sms.received"], None).unwrap();
    server.before_next_response(&[event_frame(1, "first"), event_frame(2, "second")]);
    assert_eq!(client.status().unwrap().delete_mode, "hidden");
    assert_eq!(client.pending_events(), 2);
    server.before_next_response(&[event_frame(3, "third")]);
    assert!(client.ping().unwrap().time > 0);
    assert_eq!(client.pending_events(), 3);
    let found = events(&mut client, ms(100));
    assert_eq!(found.iter().map(|e| e.seq).collect::<Vec<_>>(), [1, 2, 3]);
    assert_eq!(event_bodies(&found), ["first", "second", "third"]);
    assert_eq!(client.pending_events(), 0);
}

#[test]
fn events_end_after_the_timeout() {
    let server = FakeServer::new();
    let mut client = connect(&server);
    client.subscribe(&["*"], None).unwrap();
    let started = Instant::now();
    assert!(events(&mut client, ms(200)).is_empty());
    let waited = started.elapsed();
    assert!(ms(150) < waited && waited < ms(2000), "{waited:?}");
    assert_eq!(client.next_event(Some(Duration::ZERO)).unwrap(), None);
    assert!(client.connected());
}

#[test]
fn events_arrive_in_order() {
    let server = FakeServer::new();
    let mut client = connect(&server);
    client.subscribe(&["sms.received"], None).unwrap();
    for index in 0..5 {
        server.receive_sms(ANNA, &format!("Message {index}"));
    }
    let found = events(&mut client, ms(300));
    let expected: Vec<String> = (0..5).map(|index| format!("Message {index}")).collect();
    assert_eq!(event_bodies(&found), expected);
    assert_eq!(
        found.iter().map(|e| e.seq).collect::<Vec<_>>(),
        [1, 2, 3, 4, 5]
    );
}

#[test]
fn wait_for_event_keeps_the_others() {
    let server = FakeServer::new();
    let mut client = connect(&server);
    client.subscribe(&["*"], None).unwrap();
    server.receive_sms(ANNA, "one");
    server.emit("conversations.changed", json!({}));
    server.receive_sms(BEN, "two");
    let changed = client
        .wait_for_event(&["conversations.changed"], secs(2))
        .unwrap();
    assert_eq!(changed.data, json!({}));
    assert_eq!(client.pending_events(), 1);
    let from_ben = client
        .wait_for_event_where(&["sms.received"], secs(2), |event| {
            event
                .message()
                .is_some_and(|message| message.address == BEN)
        })
        .unwrap();
    assert_eq!(from_ben.message().unwrap().body, "two");
    assert_eq!(client.pending_events(), 1);
    let one = client
        .wait_for_event(&["sms.sent", "sms.received"], secs(2))
        .unwrap();
    assert_eq!(one.message().unwrap().body, "one");
    assert_eq!(client.pending_events(), 0);
}

#[test]
fn wait_for_event_times_out() {
    let server = FakeServer::new();
    let mut client = connect(&server);
    client.subscribe(&["*"], None).unwrap();
    server.emit("conversations.changed", json!({}));
    let error = client
        .wait_for_event(&["sms.received"], Some(ms(200)))
        .unwrap_err();
    assert!(matches!(&error, Error::Timeout(text) if text.contains("sms.received")));
    assert_eq!(client.pending_events(), 1);
    assert!(client.connected());
    assert!(is_invalid(&client.wait_for_event(&[], None)));
}

#[test]
fn address_filter() {
    let server = FakeServer::new();
    let mut client = connect(&server);
    client.subscribe(&["sms.received"], Some(BEN)).unwrap();
    server.receive_sms(ANNA, "from Anna");
    server.receive_sms(BEN, "from Ben");
    assert_eq!(event_bodies(&events(&mut client, ms(300))), ["from Ben"]);

    // A number without country code matches every number it ends.
    client
        .subscribe(&["sms.received"], Some("0690 876 54 321"))
        .unwrap();
    server.receive_sms(BEN, "international");
    server.receive_sms("0043 690 87654321", "with 00");
    server.receive_sms("+49 690 87654321", "another country");
    server.receive_sms("0690 876 54 322", "another number");
    assert_eq!(
        event_bodies(&events(&mut client, ms(300))),
        ["international", "with 00", "another country"]
    );
    // Events that name no peer always pass.
    client.subscribe(&["*"], Some(BEN)).unwrap();
    server.emit("conversations.changed", json!({}));
    server.emit(
        "sms.deleted",
        json!({"id": null, "conversationId": 1, "address": null, "mode": "hidden"}),
    );
    server.emit(
        "sms.queued",
        json!({"requestId": "r", "to": ANNA, "parts": 1}),
    );
    let names: Vec<String> = events(&mut client, ms(300))
        .into_iter()
        .map(|e| e.event)
        .collect();
    assert_eq!(names, ["conversations.changed", "sms.deleted"]);
}

#[test]
fn full_event_queue_drops_the_oldest() {
    let server = FakeServer::new();
    let mut client = builder(&server).event_queue_size(2).connect().unwrap();
    let frames: Vec<Value> = (1..=4).map(|n| event_frame(n, "Hi")).collect();
    server.before_next_response(&frames);
    client.ping().unwrap();
    assert_eq!(client.pending_events(), 2);
    assert_eq!(client.dropped_events(), 2);
    let sequence: Vec<i64> = events(&mut client, ms(50)).iter().map(|e| e.seq).collect();
    assert_eq!(sequence, [3, 4]);
}

#[test]
fn responses_in_another_order() {
    let server = FakeServer::new();
    let mut client = connect(&server);
    server.reverse_next_responses(3);
    let first = client
        .send_request("messages.list", json!({"limit": 1}))
        .unwrap();
    let second = client.send_request("status", Value::Null).unwrap();
    let third = client.send_request("contacts.list", Value::Null).unwrap();
    assert!(first != second && second != third && first != third);
    let status = client.wait_for_response(&second, secs(2)).unwrap();
    assert!(status.get("deleteMode").is_some());
    let page = client.wait_for_response(&first, secs(2)).unwrap();
    assert_eq!(page["items"][0]["body"], "Anna 24");
    let contacts = client.wait_for_response(&third, secs(2)).unwrap();
    assert_eq!(contacts["items"].as_array().unwrap().len(), 2);
    assert!(is_invalid(&client.wait_for_response(&first, None)));
    assert!(client.ping().is_ok());
}

#[test]
fn response_error_of_a_pipelined_request() {
    let server = FakeServer::new();
    let mut client = connect(&server);
    server.reverse_next_responses(2);
    let missing = client
        .send_request("messages.get", json!({"messageId": 1}))
        .unwrap();
    let fine = client.send_request("ping", Value::Null).unwrap();
    assert_eq!(
        code(client.wait_for_response(&missing, secs(2))),
        "not_found"
    );
    assert!(client.wait_for_response(&fine, secs(2)).unwrap()["time"].as_i64() > Some(0));
}

#[test]
fn pipelined_response_times_out() {
    let server = FakeServer::new();
    let mut client = connect(&server);
    server.ignore_next("status", 1);
    let id = client.send_request("status", Value::Null).unwrap();
    let error = client.wait_for_response(&id, Some(ms(200))).unwrap_err();
    assert!(matches!(&error, Error::Timeout(text) if text.contains("No response")));
    assert!(client.connected());
    assert_eq!(client.connection_count(), 1);
}

#[test]
fn stale_responses_are_ignored() {
    let server = FakeServer::new();
    let mut client = connect(&server);
    server.before_next_response(&[
        json!({"id": "rs-0", "ok": true, "result": {"stale": true}}),
        json!({"id": 77, "ok": false,
               "error": {"code": "internal_error", "message": "stale"}}),
    ]);
    assert_eq!(client.status().unwrap().delete_mode, "hidden");
    assert!(client.ping().unwrap().time > 0);
}

#[test]
fn response_without_id_answers_the_open_request() {
    let server = FakeServer::new();
    let mut client = connect(&server);
    server.before_next_response(&[json!({
        "id": null, "ok": false,
        "error": {"code": "invalid_json", "message": "Not a JSON object."},
    })]);
    assert_eq!(code(client.status()), "invalid_json");
    // The late real response is dropped; the next request works.
    assert!(client.ping().unwrap().time > 0);
}

#[test]
fn malformed_frames() {
    for (frame, expected) in [
        ("this is not JSON", "invalid JSON"),
        ("[1, 2, 3]", "not a JSON object"),
    ] {
        let server = FakeServer::new();
        let mut client = connect(&server);
        server.raw_before_next_response(frame);
        let error = client.status().unwrap_err();
        assert!(
            matches!(&error, Error::Protocol(text) if text.contains(expected)),
            "{error:?}"
        );
    }
    let server = FakeServer::new();
    let mut client = connect(&server);
    server.before_next_response(&[json!({"id": "rs-2", "ok": false})]);
    let error = client.status().unwrap_err();
    assert!(matches!(&error, Error::Protocol(text) if text.contains("Malformed")));
}

// ----------------------------------------------------------------------
// Reconnect
// ----------------------------------------------------------------------

#[test]
fn reconnects_and_logs_in_again() {
    let server = FakeServer::new();
    let mut client = connect(&server);
    assert!(client.ping().is_ok());
    server.drop_connections(None, "");
    assert_eq!(client.status().unwrap().delete_mode, "hidden");
    assert_eq!(client.connection_count(), 2);
    assert_eq!(server.login_count(), 2);
    assert_eq!(
        server.actions(),
        ["authenticate", "ping", "authenticate", "status"]
    );
}

#[test]
fn reconnects_after_a_close_frame() {
    let server = FakeServer::new();
    let mut client = connect(&server);
    for (close_code, reason) in [
        (1001, "server is stopping"),
        (4408, "no answer to pings"),
        (4429, "too many requests"),
    ] {
        server.drop_connections(Some(close_code), reason);
        assert!(client.ping().is_ok(), "{close_code}");
    }
    assert_eq!(client.connection_count(), 4);
}

#[test]
fn request_lost_before_it_was_handled() {
    let server = FakeServer::new();
    let mut client = connect(&server);
    server.drop_on("status", true);
    server.drop_on("status", true);
    assert_eq!(client.status().unwrap().delete_mode, "hidden");
    assert_eq!(server.count("status"), 3);
    assert_eq!(client.connection_count(), 3);
}

#[test]
fn restores_the_subscription() {
    let server = FakeServer::new();
    let mut client = connect(&server);
    client
        .subscribe(&["sms.received", "sms.failed"], Some(BEN))
        .unwrap();
    server.drop_connections(None, "");
    assert!(client.ping().is_ok());
    assert_eq!(
        server.actions(),
        [
            "authenticate",
            "subscribe",
            "authenticate",
            "subscribe",
            "ping"
        ]
    );
    let restored = &server.requests()[3];
    assert_eq!(restored["events"], json!(["sms.received", "sms.failed"]));
    assert_eq!(restored["address"], BEN);
    server.receive_sms(ANNA, "filtered out");
    server.receive_sms(BEN, "after the reconnect");
    let event = client.wait_for_event(&["sms.received"], secs(2)).unwrap();
    assert_eq!(event.message().unwrap().body, "after the reconnect");
    assert_eq!(client.pending_events(), 0);
}

#[test]
fn does_not_restore_an_empty_subscription() {
    let server = FakeServer::new();
    let mut client = connect(&server);
    client.subscribe(&["sms.received"], None).unwrap();
    client.unsubscribe(&["sms.received"]).unwrap();
    server.drop_connections(None, "");
    assert!(client.ping().is_ok());
    assert_eq!(server.count("subscribe"), 1);
}

#[test]
fn waiting_for_events_reconnects() {
    let server = FakeServer::new();
    let mut client = connect(&server);
    client.subscribe(&["sms.received"], None).unwrap();
    server.drop_connections(None, "");
    let event = thread::scope(|scope| {
        scope.spawn(|| {
            if wait_until(|| server.count("subscribe") == 2, ms(3000)) {
                server.receive_sms(ANNA, "after the reconnect");
            }
        });
        client.next_event(secs(5)).unwrap()
    });
    assert_eq!(
        event.unwrap().message().unwrap().body,
        "after the reconnect"
    );
    assert_eq!(client.connection_count(), 2);
}

#[test]
fn send_is_not_repeated_after_a_lost_response() {
    let server = FakeServer::new();
    let mut client = connect(&server);
    server.drop_on("messages.send", false);
    let result = client.send(BEN, "Only once", None, None).unwrap();
    // The repeat tells the status of now; the message may be out already.
    assert!(["queued", "sent"].contains(&result.status.as_str()));
    let requests = server.state().requests_of("messages.send");
    assert_eq!(requests.len(), 2);
    assert_eq!(requests[0]["requestId"], result.request_id);
    assert_eq!(requests[1]["requestId"], result.request_id);
    assert_ne!(requests[0]["id"], requests[1]["id"]);
    assert_eq!(client.connection_count(), 2);
    let outcome = client.wait_for_result(&result.request_id, secs(5)).unwrap();
    assert_eq!(outcome.status, "sent");
    // The radio was used once although the request arrived twice.
    let sent = server.sent();
    assert_eq!(sent.len(), 1);
    assert_eq!(sent[0]["body"], "Only once");
    let stored = server
        .state()
        .messages
        .iter()
        .filter(|m| m["body"] == "Only once")
        .count();
    assert_eq!(stored, 1);
}

#[test]
fn wait_for_result_survives_a_reconnect() {
    let server = FakeServer::start(Options {
        send_delay: ms(300),
        ..Options::default()
    });
    let mut client = connect(&server);
    client.subscribe(&["sms.sent", "sms.failed"], None).unwrap();
    let id = client.send(BEN, "text", None, None).unwrap().request_id;
    server.drop_connections(None, "");
    let options = WaitOptions {
        timeout: secs(5),
        poll_interval: ms(100),
        ..WaitOptions::default()
    };
    assert_eq!(
        client.wait_for_result_with(&id, &options).unwrap().status,
        "sent"
    );
    assert_eq!(server.sent().len(), 1);
}

#[test]
fn gives_up_after_the_last_attempt() {
    let mut server = FakeServer::new();
    let mut client = builder(&server)
        .reconnect_attempts(Some(3))
        .connect_timeout(Duration::from_secs(2))
        .connect()
        .unwrap();
    server.stop();
    let error = client.status().unwrap_err();
    assert!(
        matches!(&error, Error::ConnectionInterrupted { message, .. }
        if message.contains("'status'")),
        "{error:?}"
    );
    assert!(!client.connected());
    assert_eq!(client.connection_count(), 1);
}

#[test]
fn no_retry_on_request() {
    let server = FakeServer::new();
    let mut client = connect(&server);
    server.drop_on("status", true);
    let result = client.request_with("status", Value::Null, false, None);
    assert!(matches!(result, Err(Error::ConnectionInterrupted { .. })));
    assert_eq!(client.connection_count(), 1);
    assert!(client.status().is_ok());
    assert_eq!(client.connection_count(), 2);
}

#[test]
fn zero_reconnect_attempts() {
    let server = FakeServer::new();
    let mut client = builder(&server)
        .reconnect_attempts(Some(0))
        .connect()
        .unwrap();
    server.drop_on("status", true);
    assert!(matches!(
        client.status(),
        Err(Error::ConnectionInterrupted { .. })
    ));
    assert_eq!(client.connection_count(), 1);
}

#[test]
fn pipelined_requests_do_not_reconnect() {
    let server = FakeServer::new();
    let mut client = connect(&server);
    server.drop_on("status", false);
    let id = client.send_request("status", Value::Null).unwrap();
    let result = client.wait_for_response(&id, secs(2));
    assert!(matches!(result, Err(Error::ConnectionInterrupted { .. })));
    assert_eq!(client.connection_count(), 1);
}

#[test]
fn close_codes_that_forbid_a_retry() {
    for (close_code, error_code) in [
        (4410, "not_authenticated"),
        (4401, "not_authenticated"),
        (4403, "licence_required"),
    ] {
        let server = FakeServer::new();
        let mut client = connect(&server);
        server.drop_connections(Some(close_code), "");
        let error = client.status().unwrap_err();
        assert_eq!(error.code(), Some(error_code), "{close_code}");
        assert_eq!(error.close_code(), Some(close_code));
        assert_eq!(error.is_authentication(), close_code != 4403);
        // No new login with a password that may be wrong by now.
        assert_eq!(server.login_count(), 1);
        assert_eq!(client.connection_count(), 1);
        assert!(!client.connected());
    }
}

#[test]
fn close_codes_for_protocol_errors_forbid_a_retry() {
    for (close_code, reason, expected) in
        [(4400, "binary frame", "binary frame"), (1009, "", "64 KiB")]
    {
        let server = FakeServer::new();
        let mut client = connect(&server);
        server.drop_connections(Some(close_code), reason);
        let error = client.status().unwrap_err();
        assert!(
            matches!(&error, Error::Protocol(text) if text.contains(expected)),
            "{error:?}"
        );
        // The same frame would fail again: no second connection.
        assert_eq!(client.connection_count(), 1);
        assert!(!client.connected());
    }
}

#[test]
fn a_close_code_is_found_while_a_request_waits() {
    let server = FakeServer::new();
    let mut client = connect(&server);
    server.ignore_next("status", 1);
    let error = thread::scope(|scope| {
        scope.spawn(|| {
            if wait_until(|| server.count("status") == 1, ms(3000)) {
                server.drop_connections(Some(4410), "password changed");
            }
        });
        client.status().unwrap_err()
    });
    assert_eq!(error.code(), Some("not_authenticated"));
    assert_eq!(server.login_count(), 1);
}

#[test]
fn password_changed_while_listening() {
    let server = FakeServer::new();
    let mut client = connect(&server);
    client.subscribe(&["sms.received"], None).unwrap();
    let error = thread::scope(|scope| {
        scope.spawn(|| {
            thread::sleep(ms(200));
            server.drop_connections(Some(4410), "password changed");
        });
        client.next_event(secs(5)).unwrap_err()
    });
    assert!(error.is_authentication());
    assert!(error.to_string().contains("password changed"));
    assert_eq!(server.login_count(), 1);
    // The iterator ends after the error.
    let mut iterator = client.events(Some(ms(10)));
    assert!(iterator.next().is_none());
}

#[test]
fn request_timeout_reconnects() {
    let server = FakeServer::new();
    let mut client = builder(&server)
        .timeout(ms(300))
        .reconnect_attempts(Some(1))
        .connect()
        .unwrap();
    server.ignore_next("status", 1);
    assert_eq!(client.status().unwrap().delete_mode, "hidden");
    assert_eq!(client.connection_count(), 2);
    server.ignore_next("status", 2);
    let error = client.status().unwrap_err();
    assert!(
        matches!(&error, Error::ConnectionInterrupted { message, .. }
        if message.contains("No response")),
        "{error:?}"
    );
    assert_eq!(client.connection_count(), 3);
}

#[test]
fn a_timeout_in_the_middle_of_waiting_loses_nothing() {
    let server = FakeServer::new();
    let mut client = connect(&server);
    client.subscribe(&["sms.received"], None).unwrap();
    // Many short waits, then a large answer and events in between.
    for _ in 0..20 {
        assert_eq!(client.next_event(Some(ms(3))).unwrap(), None);
    }
    for index in 0..50 {
        let body = format!("{index} {}", "x".repeat(1500));
        server
            .state()
            .add_message("+436601234567", &body, "in", None, true, Some(1), None);
    }
    server.receive_sms(ANNA, "before");
    for _ in 0..20 {
        let page = client
            .messages(&all().address("+436601234567"), 50, None)
            .unwrap();
        assert_eq!(page.items.len(), 50);
        let _ = client.next_event(Some(ms(1))).unwrap();
    }
    assert_eq!(client.connection_count(), 1);
    assert_eq!(client.latest_seq(), Some(1));
}

// ----------------------------------------------------------------------
// Epoch and sequence numbers
// ----------------------------------------------------------------------

#[test]
fn login_tells_epoch_and_sequence_number() {
    let server = FakeServer::new();
    server.receive_sms(ANNA, "one");
    server.receive_sms(ANNA, "two");
    let mut client = builder(&server).build().unwrap();
    assert_eq!((client.epoch(), client.latest_seq()), (None, None));
    client.connect().unwrap();
    assert_eq!(client.epoch(), Some(server.state().epoch));
    assert_eq!(client.latest_seq(), Some(2));
    assert!(!client.server_restarted());
    assert_eq!(client.missed_events(), Some(0));
}

#[test]
fn latest_seq_follows_the_events() {
    let server = FakeServer::new();
    let mut client = connect(&server);
    client.subscribe(&["conversations.changed"], None).unwrap();
    server.receive_sms(ANNA, "not subscribed");
    server.emit("conversations.changed", json!({}));
    let event = client
        .wait_for_event(&["conversations.changed"], secs(2))
        .unwrap();
    assert_eq!(event.seq, 2);
    assert_eq!(client.latest_seq(), Some(2));
    // Events read while a request waits for its response count as well.
    server.before_next_response(&[event_frame(7, "Hi")]);
    client.ping().unwrap();
    assert_eq!(client.latest_seq(), Some(7));
}

#[test]
fn counts_the_events_missed_while_disconnected() {
    type Call = (Option<u64>, bool, bool, Vec<String>);
    let server = FakeServer::new();
    let calls: Arc<Mutex<Vec<Call>>> = Arc::default();
    let seen = Arc::clone(&calls);
    let mut client = builder(&server)
        .on_reconnect(move |dock| {
            let subscribed = dock
                .subscription()
                .map(|s| s.events.clone())
                .unwrap_or_default();
            seen.lock().unwrap().push((
                dock.missed_events(),
                dock.server_restarted(),
                dock.connected(),
                subscribed,
            ));
            Ok(())
        })
        .connect()
        .unwrap();
    assert!(calls.lock().unwrap().is_empty());
    client.subscribe(&["sms.received"], None).unwrap();
    server.receive_sms(ANNA, "seen");
    assert_eq!(
        client
            .wait_for_event(&["sms.received"], secs(2))
            .unwrap()
            .seq,
        1
    );
    server.drop_connections(None, "");
    assert!(wait_until(|| server.client_count() == 0, ms(3000)));
    for body in ["lost one", "lost two", "lost three"] {
        server.receive_sms(ANNA, body);
    }
    assert!(client.ping().is_ok());
    assert_eq!(client.missed_events(), Some(3));
    assert!(!client.server_restarted());
    assert_eq!(client.latest_seq(), Some(4));
    // The hook runs once the subscription is in force again.
    assert_eq!(
        *calls.lock().unwrap(),
        [(Some(3), false, true, vec!["sms.received".to_owned()])]
    );
    assert_eq!(server.actions()[2..], ["authenticate", "subscribe", "ping"]);
    server.drop_connections(None, "");
    assert!(client.ping().is_ok());
    assert_eq!(client.missed_events(), Some(0));
    assert_eq!(calls.lock().unwrap().len(), 2);
}

#[test]
fn notices_a_restart_of_the_app() {
    let server = FakeServer::new();
    let mut client = connect(&server);
    client.subscribe(&["sms.received"], None).unwrap();
    for index in 0..5 {
        server.receive_sms(ANNA, &format!("before {index}"));
    }
    assert_eq!(events(&mut client, ms(300)).len(), 5);
    let first = client.epoch();
    server.restart();
    server.receive_sms(ANNA, "lost");
    assert!(client.ping().is_ok());
    assert!(client.server_restarted());
    assert_eq!(client.missed_events(), None);
    assert_eq!(client.epoch(), Some(server.state().epoch));
    assert_ne!(client.epoch(), first);
    assert_eq!(client.latest_seq(), Some(1));
    server.receive_sms(ANNA, "after");
    assert_eq!(
        client
            .wait_for_event(&["sms.received"], secs(2))
            .unwrap()
            .seq,
        2
    );
    server.drop_connections(None, "");
    assert!(client.ping().is_ok());
    assert!(!client.server_restarted());
    assert_eq!(client.missed_events(), Some(0));
}

#[test]
fn hook_reads_what_was_missed() {
    let server = FakeServer::new();
    let seen: Arc<Mutex<Vec<String>>> = Arc::default();
    let newest = Arc::new(Mutex::new(0_i64));
    let handle = {
        let (seen, newest) = (Arc::clone(&seen), Arc::clone(&newest));
        move |message: Message| {
            seen.lock().unwrap().push(message.body);
            let mut newest = newest.lock().unwrap();
            *newest = (*newest).max(message.timestamp);
        }
    };
    let catch_up = {
        let (handle, newest) = (handle.clone(), Arc::clone(&newest));
        move |dock: &mut SmsDockClient| {
            let since = *newest.lock().unwrap() + 1;
            let filter = MessageFilter::new().direction(Direction::In).since(since);
            let missed: Vec<Message> = dock
                .iter_messages(&filter, 50, None)?
                .collect::<smsdock::Result<_>>()?;
            missed.into_iter().rev().for_each(&handle);
            Ok(())
        }
    };
    let mut client = builder(&server).on_reconnect(catch_up).connect().unwrap();
    client.subscribe(&["sms.received"], None).unwrap();
    server.receive_sms(ANNA, "first");
    handle(
        client
            .wait_for_event(&["sms.received"], secs(2))
            .unwrap()
            .message()
            .unwrap(),
    );
    server.drop_connections(None, "");
    assert!(wait_until(|| server.client_count() == 0, ms(3000)));
    thread::sleep(ms(10));
    server.receive_sms(ANNA, "while away 1");
    thread::sleep(ms(10));
    server.receive_sms(BEN, "while away 2");
    let event = thread::scope(|scope| {
        scope.spawn(|| {
            if wait_until(|| server.count("subscribe") == 2, ms(3000)) {
                thread::sleep(ms(50));
                server.receive_sms(ANNA, "live again");
            }
        });
        client.next_event(secs(5)).unwrap()
    });
    handle(event.unwrap().message().unwrap());
    assert_eq!(
        *seen.lock().unwrap(),
        ["first", "while away 1", "while away 2", "live again"]
    );
}

#[test]
fn hook_can_be_set_later_and_may_fail() {
    let server = FakeServer::new();
    let mut client = connect(&server);
    let calls: Arc<Mutex<Vec<u64>>> = Arc::default();
    let seen = Arc::clone(&calls);
    client.set_on_reconnect(move |dock| {
        seen.lock().unwrap().push(dock.connection_count());
        Ok(())
    });
    server.drop_connections(None, "");
    client.ping().unwrap();
    assert_eq!(*calls.lock().unwrap(), [2]);

    client.set_on_reconnect(|_| Err(Error::Protocol("the hook failed".to_owned())));
    server.drop_connections(None, "");
    let error = client.ping().unwrap_err();
    assert_eq!(error, Error::Protocol("the hook failed".to_owned()));
    assert!(!client.connected());
    client.clear_on_reconnect();
    assert!(client.ping().is_ok());
}

#[test]
fn login_without_epoch_is_refused() {
    let server = FakeServer::new();
    let mut client = builder(&server).build().unwrap();
    server.before_next_response(&[json!({
        "id": "rs-1", "ok": true,
        "result": {"protocolVersion": 1,
                   "app": {"name": "SMS Dock", "version": "0.9.0"}},
    })]);
    let error = client.connect().unwrap_err();
    assert!(matches!(&error, Error::Protocol(text) if text.contains("epoch")));
    assert!(!client.connected());
}

// ----------------------------------------------------------------------
// Rate limit
// ----------------------------------------------------------------------

fn rate_limited(server: &FakeServer, action: &str, retry_after: Option<i64>) {
    let extra = match retry_after {
        Some(seconds) => json!({"retryAfter": seconds}),
        None => json!({}),
    };
    server.fail_next(action, "rate_limited", "Too many requests.", extra);
}

#[test]
fn waits_and_repeats_when_rate_limited() {
    let server = FakeServer::new();
    let mut client = connect(&server);
    rate_limited(&server, "status", Some(1));
    rate_limited(&server, "status", Some(1));
    let started = Instant::now();
    assert_eq!(client.status().unwrap().delete_mode, "hidden");
    let waited = started.elapsed();
    assert!(ms(1900) < waited && waited < ms(3500), "{waited:?}");
    assert_eq!(server.count("status"), 3);
    assert_eq!(client.connection_count(), 1);
}

#[test]
fn gives_up_when_rate_limited_again_and_again() {
    let server = FakeServer::new();
    let mut client = connect(&server);
    for _ in 0..3 {
        rate_limited(&server, "status", Some(1));
    }
    let started = Instant::now();
    let error = client.status().unwrap_err();
    assert_eq!(error.code(), Some("rate_limited"));
    assert_eq!(error.retry_after(), Some(1));
    assert!(started.elapsed() > ms(1900));
    assert_eq!(server.count("status"), 3);
    assert!(client.ping().is_ok());
}

#[test]
fn limit_of_messages_per_minute_is_reported_at_once() {
    let server = FakeServer::new();
    let mut client = connect(&server);
    // The limit of 20 SMS parts per minute names no seconds to wait.
    rate_limited(&server, "messages.send", None);
    let started = Instant::now();
    let error = client.send(BEN, "text", None, None).unwrap_err();
    assert_eq!(error.code(), Some("rate_limited"));
    assert_eq!(error.retry_after(), None);
    assert!(started.elapsed() < ms(900));
    assert_eq!(server.count("messages.send"), 1);
    assert!(server.sent().is_empty());
}

#[test]
fn rate_limit_without_a_repeat() {
    let server = FakeServer::new();
    let started = Instant::now();
    // No retries wanted.
    let mut client = builder(&server).rate_limit_retries(0).connect().unwrap();
    rate_limited(&server, "status", Some(1));
    assert_eq!(code(client.status()), "rate_limited");
    // The wait would be longer than the timeout.
    let mut client = connect(&server);
    rate_limited(&server, "status", Some(6));
    assert_eq!(code(client.status()), "rate_limited");
    // The request must not happen twice.
    rate_limited(&server, "status", Some(1));
    assert_eq!(
        code(client.request_with("status", Value::Null, false, None)),
        "rate_limited"
    );
    // Pipelined requests are not repeated either.
    rate_limited(&server, "status", Some(1));
    let id = client.send_request("status", Value::Null).unwrap();
    assert_eq!(code(client.wait_for_response(&id, None)), "rate_limited");
    assert!(started.elapsed() < ms(900));
    assert_eq!(server.count("status"), 4);
}

#[test]
fn send_is_not_repeated_when_rate_limited() {
    let server = FakeServer::new();
    let mut client = connect(&server);
    rate_limited(&server, "messages.send", Some(1));
    let result = client.send(BEN, "Only once", None, None).unwrap();
    let requests = server.state().requests_of("messages.send");
    assert_eq!(requests.len(), 2);
    assert!(requests.iter().all(|r| r["requestId"] == result.request_id));
    assert_eq!(server.sent().len(), 1);
}

#[test]
fn stays_within_the_limit_of_the_server() {
    let server = FakeServer::start(Options {
        requests_per_second: 5,
        ..Options::default()
    });
    let mut client = connect(&server);
    let started = Instant::now();
    for _ in 0..9 {
        assert!(client.ping().unwrap().time > 0);
    }
    // The login and 4 requests fit into the first second, 5 into the next.
    assert!(server.state().refused_count >= 1);
    assert!(started.elapsed() >= ms(900));
    assert_eq!(server.count("ping"), 9);
    assert_eq!(client.connection_count(), 1);
}
