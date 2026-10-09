//! The JSON objects of the SMS Dock API as Rust types.
//!
//! All timestamps are milliseconds since the Unix epoch. Fields that do not
//! apply are `None`. Fields that this version of the crate does not know
//! are kept in `extra`, so that nothing the server sent gets lost.

use serde::de::DeserializeOwned;
use serde::{Deserialize, Serialize};
use serde_json::{Map, Value};

/// Fields of an object that this crate has no name for.
pub type Extra = Map<String, Value>;

/// One SMS.
///
/// `id` is `None` only inside an `sms.received` event whose stored copy was
/// not found in time. `direction` is `"in"` or `"out"`. `status` is one of
/// `received`, `queued`, `sending`, `sent`, `delivered` and `failed`.
/// `sent_timestamp` is set for incoming messages only. `request_id` is set
/// for messages sent through SMS Dock.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Message {
    /// Id on the phone.
    pub id: Option<i64>,
    /// Id of the conversation.
    pub conversation_id: Option<i64>,
    /// Peer number or sender name as stored.
    pub address: String,
    /// Name from the contacts of the phone.
    pub contact: Option<String>,
    /// `"in"` or `"out"`.
    pub direction: String,
    /// Text of the message.
    pub body: String,
    /// Time of the message.
    pub timestamp: i64,
    /// Incoming only: time from the network of the sender.
    pub sent_timestamp: Option<i64>,
    /// Whether the message was read.
    pub read: bool,
    /// State of the message.
    pub status: String,
    /// Subscription id of the SIM.
    pub sim: Option<i64>,
    /// Request id, for messages sent through SMS Dock.
    pub request_id: Option<String>,
    /// Fields unknown to this crate.
    #[serde(flatten)]
    pub extra: Extra,
}

/// All messages exchanged with one phone number.
///
/// `snippet` is the newest message cut to 160 characters; `timestamp`,
/// `direction` and `sim` describe that newest message.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Conversation {
    /// Id of the conversation.
    pub id: i64,
    /// Peer number or sender name.
    pub address: String,
    /// Name from the contacts of the phone.
    pub contact: Option<String>,
    /// Newest message, cut to 160 characters.
    pub snippet: String,
    /// Time of the newest message.
    pub timestamp: i64,
    /// Direction of the newest message: `"in"` or `"out"`.
    pub direction: String,
    /// Number of messages.
    pub message_count: i64,
    /// Number of unread messages.
    pub unread_count: i64,
    /// Subscription id of the SIM of the newest message.
    pub sim: Option<i64>,
    /// Fields unknown to this crate.
    #[serde(flatten)]
    pub extra: Extra,
}

/// One phone number of a contact.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ContactNumber {
    /// The number.
    pub number: String,
    /// Label such as `Mobile` or `Work`.
    pub label: Option<String>,
    /// Fields unknown to this crate.
    #[serde(flatten)]
    pub extra: Extra,
}

/// One contact of the phone that has at least one number.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Contact {
    /// Name of the contact.
    pub name: String,
    /// Its phone numbers.
    #[serde(default)]
    pub numbers: Vec<ContactNumber>,
    /// Fields unknown to this crate.
    #[serde(flatten)]
    pub extra: Extra,
}

/// One SIM. `subscription_id` is the value used as `sim` elsewhere.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Sim {
    /// Subscription id.
    pub subscription_id: i64,
    /// Slot of the SIM.
    pub slot: i64,
    /// Name of the SIM, usually the network.
    pub name: String,
    /// Own number, if the phone knows it.
    pub number: Option<String>,
    /// Whether this is the SMS SIM of the phone.
    pub is_default: bool,
    /// Fields unknown to this crate.
    #[serde(flatten)]
    pub extra: Extra,
}

/// Name and version of the app and the protocol version it speaks.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct AppInfo {
    /// Name of the app.
    pub name: String,
    /// Version of the app.
    pub version: String,
    /// Protocol version of the API.
    pub protocol_version: i64,
    /// Fields unknown to this crate.
    #[serde(flatten)]
    pub extra: Extra,
}

/// Licence state. `remaining` is `None` when sending is unlimited.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Licence {
    /// Whether the licence is checked at all.
    pub enforced: bool,
    /// Whether the app has the full licence.
    pub licensed: bool,
    /// Daily sending limit of the free version.
    pub daily_limit: i64,
    /// SMS sent today.
    pub sent_today: i64,
    /// SMS left today; `None` when sending is unlimited.
    pub remaining: Option<i64>,
    /// Fields unknown to this crate.
    #[serde(flatten)]
    pub extra: Extra,
}

/// The Android permissions of the app.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Permissions {
    /// Read SMS.
    pub read_sms: bool,
    /// Send SMS.
    pub send_sms: bool,
    /// Receive SMS.
    pub receive_sms: bool,
    /// Read contacts.
    pub contacts: bool,
    /// Read the state of the phone (SIMs).
    pub phone_state: bool,
    /// Show notifications.
    pub notifications: bool,
    /// Permissions unknown to this crate.
    #[serde(flatten)]
    pub extra: Extra,
}

impl Permissions {
    /// Names of the permissions that are not granted, sorted.
    pub fn missing(&self) -> Vec<String> {
        let known = [
            ("readSms", self.read_sms),
            ("sendSms", self.send_sms),
            ("receiveSms", self.receive_sms),
            ("contacts", self.contacts),
            ("phoneState", self.phone_state),
            ("notifications", self.notifications),
        ];
        let mut names: Vec<String> = known
            .iter()
            .filter(|(_, granted)| !granted)
            .map(|(name, _)| (*name).to_owned())
            .collect();
        for (name, granted) in &self.extra {
            if matches!(granted, Value::Bool(false) | Value::Null) {
                names.push(name.clone());
            }
        }
        names.sort();
        names
    }
}

/// Result of `status`. `delete_mode` is `"hidden"` or `"provider"`.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Status {
    /// The app.
    pub app: AppInfo,
    /// The SIMs of the phone.
    #[serde(default)]
    pub sims: Vec<Sim>,
    /// The Android permissions.
    pub permissions: Permissions,
    /// Whether SMS Dock is the default SMS app of the phone.
    pub default_sms_app: bool,
    /// What a delete does: `"hidden"` or `"provider"`.
    pub delete_mode: String,
    /// Licence state.
    pub licence: Licence,
    /// Whether the app shows a popup for incoming SMS.
    pub popup: bool,
    /// Fields unknown to this crate.
    #[serde(flatten)]
    pub extra: Extra,
}

/// Result of `messages.send`.
///
/// `to` is the number as it is sent, without the ignored characters.
/// `status` is `queued` for a new message; a repeated request returns the
/// current status, `failed` included.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct SendResult {
    /// Request id of the message.
    pub request_id: String,
    /// Number as it is sent.
    pub to: String,
    /// Number of SMS parts.
    pub parts: i64,
    /// State of the message.
    pub status: String,
    /// Fields unknown to this crate.
    #[serde(flatten)]
    pub extra: Extra,
}

/// State of a message sent through SMS Dock.
///
/// `status` is `queued` (handed to the phone), `sending` (some parts are
/// left), `sent`, `delivered` or `failed`. `source` tells who sent: `web`,
/// `api` or `app`.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct OutboxEntry {
    /// Request id of the message.
    pub request_id: String,
    /// Number as it is sent.
    pub to: String,
    /// Number of SMS parts.
    pub parts: i64,
    /// Parts the network accepted so far.
    pub parts_sent: i64,
    /// State of the message.
    pub status: String,
    /// Result code of Android after a failure.
    pub error_code: Option<i64>,
    /// Id of the stored message, once the phone has stored it.
    pub message_id: Option<i64>,
    /// Subscription id of the SIM.
    pub sim: Option<i64>,
    /// Who sent: `web`, `api` or `app`.
    pub source: String,
    /// Time the message was accepted.
    pub created_at: i64,
    /// Fields unknown to this crate.
    #[serde(flatten)]
    pub extra: Extra,
}

/// Result of a delete. `mode` is `"hidden"` or `"provider"`.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct DeleteResult {
    /// Number of deleted messages.
    pub deleted: i64,
    /// `"hidden"`: gone from SMS Dock, still on the phone. `"provider"`:
    /// removed from the phone.
    pub mode: String,
    /// Fields unknown to this crate.
    #[serde(flatten)]
    pub extra: Extra,
}

/// The subscription in force after `subscribe` or `unsubscribe`.
///
/// `events` holds event names; `*` is replaced by the names it stands for.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Subscription {
    /// Names of the subscribed events.
    pub events: Vec<String>,
    /// The number the `sms.*` events are limited to.
    pub address: Option<String>,
    /// Fields unknown to this crate.
    #[serde(flatten)]
    pub extra: Extra,
}

/// Result of `ping`: the time of the phone.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct PingResult {
    /// Time of the phone.
    pub time: i64,
    /// Fields unknown to this crate.
    #[serde(flatten)]
    pub extra: Extra,
}

/// Result of `conversations.markRead`: `{"ok": true}`.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct MarkReadResult {
    /// Always `true`.
    pub ok: bool,
    /// Fields unknown to this crate.
    #[serde(flatten)]
    pub extra: Extra,
}

/// One page of a list. `next_cursor` is `None` on the last page.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Page<T> {
    /// The items of this page.
    pub items: Vec<T>,
    /// Pass it as `cursor` to get the next page.
    pub next_cursor: Option<String>,
}

/// An event: `{"event": name, "seq": number, "data": {...}}`.
///
/// `seq` counts all events of the phone and restarts at 1 when the app is
/// restarted. The keys of `data` are listed under "Events" in `API.md`.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Event {
    /// Name of the event, for example `sms.received`.
    pub event: String,
    /// Sequence number.
    #[serde(default)]
    pub seq: i64,
    /// Data of the event as the server sent it.
    #[serde(default)]
    pub data: Value,
    /// Fields unknown to this crate.
    #[serde(flatten)]
    pub extra: Extra,
}

impl Event {
    /// The message of an `sms.received` or `sms.sent` event.
    ///
    /// `None` when the event carries no message (`sms.sent` of a message
    /// the phone has not stored) or another kind of data.
    pub fn message(&self) -> Option<Message> {
        self.field("message")
    }

    /// The `requestId` of an `sms.queued`, `sms.sent`, `sms.delivered` or
    /// `sms.failed` event; `None` if another app sent the message.
    pub fn request_id(&self) -> Option<&str> {
        self.data.get("requestId")?.as_str()
    }

    /// Read one field of `data` as a Rust type.
    pub fn field<T: DeserializeOwned>(&self, name: &str) -> Option<T> {
        let value = self.data.get(name)?;
        if value.is_null() {
            return None;
        }
        serde_json::from_value(value.clone()).ok()
    }
}

#[cfg(test)]
mod tests {
    use serde_json::json;

    use super::*;

    #[test]
    fn message_round_trip_keeps_unknown_fields() {
        let sent = json!({
            "id": 2815, "conversationId": 179, "address": "+4369912345678",
            "contact": null, "direction": "in", "body": "STATUS",
            "timestamp": 1790672531000_i64, "sentTimestamp": 1790672529000_i64,
            "read": false, "status": "received", "sim": 1, "requestId": null,
            "future": {"x": 1},
        });
        let message: Message = serde_json::from_value(sent.clone()).unwrap();
        assert_eq!(message.id, Some(2815));
        assert_eq!(message.contact, None);
        assert_eq!(message.sent_timestamp, Some(1790672529000));
        assert_eq!(message.extra["future"], json!({"x": 1}));
        assert_eq!(serde_json::to_value(&message).unwrap(), sent);
    }

    #[test]
    fn status_of_the_document() {
        let status: Status = serde_json::from_value(json!({
            "app": {"name": "SMS Dock", "version": "1.0.0", "protocolVersion": 1},
            "sims": [{"subscriptionId": 1, "slot": 0, "name": "Play",
                      "number": null, "isDefault": true}],
            "permissions": {"readSms": true, "sendSms": false, "receiveSms": true,
                            "contacts": false, "phoneState": true,
                            "notifications": true},
            "defaultSmsApp": false,
            "deleteMode": "hidden",
            "licence": {"enforced": false, "licensed": false, "dailyLimit": 10,
                        "sentToday": 3, "remaining": null},
            "popup": true,
        }))
        .unwrap();
        assert_eq!(status.sims[0].subscription_id, 1);
        assert_eq!(status.licence.remaining, None);
        assert_eq!(status.permissions.missing(), ["contacts", "sendSms"]);
    }

    #[test]
    fn event_helpers() {
        let event: Event = serde_json::from_value(json!({
            "event": "sms.sent", "seq": 7,
            "data": {"requestId": "r1", "conversationId": 1, "to": "+43123",
                     "message": null},
        }))
        .unwrap();
        assert_eq!(event.request_id(), Some("r1"));
        assert_eq!(event.message(), None);
        assert_eq!(event.field::<i64>("conversationId"), Some(1));
        let bare: Event =
            serde_json::from_value(json!({"event": "conversations.changed"})).unwrap();
        assert_eq!((bare.seq, bare.data.clone()), (0, Value::Null));
    }

    #[test]
    fn page_of_contacts() {
        let page: Page<Contact> = serde_json::from_value(json!({
            "items": [{"name": "Anna Example",
                       "numbers": [{"number": "+4369912345678", "label": "Mobile"}]}],
            "nextCursor": null,
        }))
        .unwrap();
        assert_eq!(page.items[0].numbers[0].label.as_deref(), Some("Mobile"));
        assert_eq!(page.next_cursor, None);
    }
}
