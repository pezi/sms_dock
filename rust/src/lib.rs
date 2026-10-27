//! Client for the WebSocket API of the SMS Dock app, protocol version 1.
//!
//! SMS Dock turns an Android phone into an SMS hub. With this crate a
//! program can list conversations, messages and contacts, send SMS and
//! wait for the result, delete messages and react to incoming ones. The
//! protocol is described in `API.md` at the top of the repository.
//!
//! The client pins the certificate of the phone, reconnects after a lost
//! connection, tells what it missed meanwhile and never sends a message
//! twice.
//!
//! ```no_run
//! use std::time::Duration;
//! use smsdock::{Direction, MessageFilter, SmsDockClient};
//!
//! fn main() -> smsdock::Result<()> {
//!     // Password and fingerprint come from SMSDOCK_PASSWORD and
//!     // SMSDOCK_FINGERPRINT.
//!     let mut dock = SmsDockClient::builder("wss://192.168.1.50:9443/ws").connect()?;
//!
//!     // The last 10 incoming SMS
//!     let incoming = MessageFilter::new().direction(Direction::In);
//!     for message in dock.messages(&incoming, 10, None)?.items {
//!         println!("{} {} {}", message.timestamp, message.address, message.body);
//!     }
//!
//!     // Send and wait for the result
//!     let result = dock.send("+4369912345678", "All systems running.", None, None)?;
//!     let outcome = dock.wait_for_result(&result.request_id, Some(Duration::from_secs(60)))?;
//!     println!("{}", outcome.status); // "sent" or "failed"
//!     Ok(())
//! }
//! ```

#![forbid(unsafe_code)]
#![warn(missing_docs)]

mod client;
mod error;
pub mod models;
mod tls;

pub use client::{
    ClientBuilder, ConversationFilter, Direction, Events, Items, MessageFilter, ReconnectHook,
    SmsDockClient, WaitOptions,
};
pub use error::{Error, Result};
pub use models::{
    AppInfo, Contact, ContactNumber, Conversation, DeleteResult, Event, GroupSendResult, Licence,
    MarkReadResult, Message, OutboxEntry, Page, Permissions, PingResult, SendResult, Sim, Status,
    Subscription,
};
pub use tls::{
    certificate_fingerprint, fetch_fingerprint, format_fingerprint, normalize_fingerprint,
};

/// Protocol version this client speaks.
pub const API_PROTOCOL_VERSION: i64 = 1;

/// Default port of the WebSocket API.
pub const DEFAULT_API_PORT: u16 = 9443;

/// Largest frame the server accepts from a client, in bytes.
pub const MAX_FRAME_BYTES: usize = 64 * 1024;

/// Largest frame the client accepts, in bytes. Answers may exceed 64 KiB.
pub const MAX_INCOMING_BYTES: usize = 4 * 1024 * 1024;

/// Largest value of `limit`.
pub const MAX_PAGE_SIZE: u32 = 200;

/// Recipients of one group message at most.
pub const MAX_GROUP_RECIPIENTS: usize = 20;

/// Environment variable that holds the password.
pub const PASSWORD_ENV: &str = "SMSDOCK_PASSWORD";

/// Environment variable that holds the certificate fingerprint.
pub const FINGERPRINT_ENV: &str = "SMSDOCK_FINGERPRINT";

/// All event names of protocol version 1.
pub const EVENT_NAMES: [&str; 7] = [
    "sms.received",
    "sms.queued",
    "sms.sent",
    "sms.delivered",
    "sms.failed",
    "sms.deleted",
    "conversations.changed",
];
