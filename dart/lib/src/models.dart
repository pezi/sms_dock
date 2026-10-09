// --------------------------------------------------------------------------
// Filename: models.dart
//
// Description: Typed views (extension types) of the JSON objects of the SMS
//   Dock API.
// --------------------------------------------------------------------------

/// Typed views of the JSON objects of the SMS Dock API.
///
/// The client returns the JSON objects exactly as the server sent them.
/// The types in this library are extension types on `Map<String, dynamic>`:
/// they add getters for the known keys and cost nothing at run time. Every
/// object is still a map, so `message['body']` and `jsonEncode(message)`
/// work as well. All timestamps are milliseconds since the Unix epoch.
/// Fields that do not apply are `null`; they are never left out.
library;

/// A JSON object as `dart:convert` decodes it.
typedef JsonObject = Map<String, dynamic>;

/// One SMS.
///
/// [id] is `null` only inside an `sms.received` event whose stored copy
/// was not found in time. [sentTimestamp] is set for incoming messages
/// only. [requestId] is set for messages sent through SMS Dock.
extension type Message(JsonObject json) implements JsonObject {
  /// Id on the phone.
  int? get id => json['id'] as int?;

  /// Id of the conversation.
  int? get conversationId => json['conversationId'] as int?;

  /// Peer number or sender name as stored.
  String get address => json['address'] as String;

  /// Name from the contacts of the phone.
  String? get contact => json['contact'] as String?;

  /// `"in"` or `"out"`.
  String get direction => json['direction'] as String;

  /// Text of the message.
  String get body => json['body'] as String;

  /// Time of the message.
  int get timestamp => json['timestamp'] as int;

  /// Incoming only: time from the network of the sender.
  int? get sentTimestamp => json['sentTimestamp'] as int?;

  /// Whether the message was read.
  bool get read => json['read'] as bool;

  /// One of `received`, `queued`, `sending`, `sent`, `delivered` and
  /// `failed`.
  String get status => json['status'] as String;

  /// Subscription id of the SIM.
  int? get sim => json['sim'] as int?;

  /// The `requestId` of a message sent through SMS Dock.
  String? get requestId => json['requestId'] as String?;
}

/// All messages exchanged with one phone number.
///
/// [snippet] is the newest message cut to 160 characters; [timestamp],
/// [direction] and [sim] describe that newest message.
extension type Conversation(JsonObject json) implements JsonObject {
  /// Id of the conversation.
  int get id => json['id'] as int;

  /// Peer number or sender name.
  String get address => json['address'] as String;

  /// Name from the contacts of the phone.
  String? get contact => json['contact'] as String?;

  /// The newest message, cut to 160 characters.
  String get snippet => json['snippet'] as String;

  /// Time of the newest message.
  int get timestamp => json['timestamp'] as int;

  /// Direction of the newest message: `"in"` or `"out"`.
  String get direction => json['direction'] as String;

  /// Number of messages.
  int get messageCount => json['messageCount'] as int;

  /// Number of unread messages.
  int get unreadCount => json['unreadCount'] as int;

  /// Subscription id of the newest message.
  int? get sim => json['sim'] as int?;
}

/// One phone number of a contact.
extension type ContactNumber(JsonObject json) implements JsonObject {
  /// The phone number.
  String get number => json['number'] as String;

  /// Label such as `Mobile` or `Work`.
  String? get label => json['label'] as String?;
}

/// One contact of the phone that has at least one number.
extension type Contact(JsonObject json) implements JsonObject {
  /// Name of the contact.
  String get name => json['name'] as String;

  /// The numbers of the contact.
  List<ContactNumber> get numbers =>
      (json['numbers'] as List<dynamic>).cast<ContactNumber>();
}

/// One SIM. [subscriptionId] is the value used as `sim` elsewhere.
extension type Sim(JsonObject json) implements JsonObject {
  /// Subscription id.
  int get subscriptionId => json['subscriptionId'] as int;

  /// Slot of the SIM, starting at 0.
  int get slot => json['slot'] as int;

  /// Name of the SIM.
  String get name => json['name'] as String;

  /// Phone number of the SIM, if the phone knows it.
  String? get number => json['number'] as String?;

  /// Whether this is the SMS SIM of the phone.
  bool get isDefault => json['isDefault'] as bool;
}

/// Name and version of the app and the protocol version it speaks.
extension type AppInfo(JsonObject json) implements JsonObject {
  /// Name of the app.
  String get name => json['name'] as String;

  /// Version of the app.
  String get version => json['version'] as String;

  /// Protocol version the app speaks.
  int get protocolVersion => json['protocolVersion'] as int;
}

/// Licence state. [remaining] is `null` when sending is unlimited.
extension type Licence(JsonObject json) implements JsonObject {
  /// Whether the licence is checked at all.
  bool get enforced => json['enforced'] as bool;

  /// Whether the full licence is present.
  bool get licensed => json['licensed'] as bool;

  /// Daily sending limit of the free version.
  int get dailyLimit => json['dailyLimit'] as int;

  /// SMS sent today.
  int get sentToday => json['sentToday'] as int;

  /// SMS left for today, or `null` without a limit.
  int? get remaining => json['remaining'] as int?;
}

/// Result of `status`.
extension type Status(JsonObject json) implements JsonObject {
  /// Name and version of the app.
  AppInfo get app => AppInfo(json['app'] as JsonObject);

  /// The SIMs of the phone.
  List<Sim> get sims => (json['sims'] as List<dynamic>).cast<Sim>();

  /// Maps `readSms`, `sendSms`, `receiveSms`, `contacts`, `phoneState`
  /// and `notifications` to whether the permission is granted.
  Map<String, bool> get permissions =>
      (json['permissions'] as JsonObject).cast<String, bool>();

  /// Whether SMS Dock is the default SMS app of the phone.
  bool get defaultSmsApp => json['defaultSmsApp'] as bool;

  /// `"hidden"` or `"provider"`.
  String get deleteMode => json['deleteMode'] as String;

  /// Licence state.
  Licence get licence => Licence(json['licence'] as JsonObject);

  /// Whether the app shows a popup for incoming SMS.
  bool get popup => json['popup'] as bool;
}

/// Result of `messages.send`.
///
/// [to] is the number as it is sent, without the ignored characters.
/// [status] is `queued` for a new message; a repeated request returns the
/// current status, `failed` included.
extension type SendResult(JsonObject json) implements JsonObject {
  /// The id that names this message.
  String get requestId => json['requestId'] as String;

  /// The number as it is sent.
  String get to => json['to'] as String;

  /// Number of SMS parts.
  int get parts => json['parts'] as int;

  /// Status of the message.
  String get status => json['status'] as String;
}

/// State of a message sent through SMS Dock.
///
/// [status] is `queued` (handed to the phone), `sending` (some parts are
/// left), `sent`, `delivered` or `failed`. [source] tells who sent: `web`,
/// `api` or `app`.
extension type OutboxEntry(JsonObject json) implements JsonObject {
  /// The id that names this message.
  String get requestId => json['requestId'] as String;

  /// The number as it is sent.
  String get to => json['to'] as String;

  /// Number of SMS parts.
  int get parts => json['parts'] as int;

  /// Number of parts the network accepted so far.
  int get partsSent => json['partsSent'] as int;

  /// Status of the message.
  String get status => json['status'] as String;

  /// Result code of Android for a failed message.
  int? get errorCode => json['errorCode'] as int?;

  /// Id of the stored message, once the phone has stored it.
  int? get messageId => json['messageId'] as int?;

  /// Subscription id of the SIM.
  int? get sim => json['sim'] as int?;

  /// Who sent: `web`, `api` or `app`.
  String get source => json['source'] as String;

  /// Time the request was accepted.
  int get createdAt => json['createdAt'] as int;
}

/// Result of a delete. [mode] is `"hidden"` or `"provider"`.
extension type DeleteResult(JsonObject json) implements JsonObject {
  /// Number of messages deleted.
  int get deleted => json['deleted'] as int;

  /// `"hidden"` or `"provider"`.
  String get mode => json['mode'] as String;
}

/// The subscription in force after `subscribe` or `unsubscribe`.
///
/// [events] holds event names; `*` is replaced by the names it stands for.
extension type Subscription(JsonObject json) implements JsonObject {
  /// The subscribed event names.
  List<String> get events => (json['events'] as List<dynamic>).cast<String>();

  /// The number the `sms.*` events are limited to, or `null`.
  String? get address => json['address'] as String?;
}

/// Result of `ping`: the time of the phone.
extension type PingResult(JsonObject json) implements JsonObject {
  /// Time of the phone.
  int get time => json['time'] as int;
}

/// Result of `conversations.markRead`: `{"ok": true}`.
extension type MarkReadResult(JsonObject json) implements JsonObject {
  /// Always `true`.
  bool get ok => json['ok'] as bool;
}

/// One page of items. [nextCursor] is `null` on the last page.
extension type Page<T extends JsonObject>(JsonObject json)
    implements JsonObject {
  /// The items of this page.
  List<T> get items => (json['items'] as List<dynamic>).cast<T>();

  /// Pass it as `cursor` to get the next page; `null` on the last page.
  String? get nextCursor => json['nextCursor'] as String?;
}

/// An event: `{"event": name, "seq": number, "data": {...}}`.
///
/// [seq] counts all events of the phone and restarts at 1 when the app is
/// restarted. The keys of [data] are listed under "Events" in `API.md`.
extension type SmsEvent(JsonObject json) implements JsonObject {
  /// Name of the event, for example `sms.received`.
  String get name => json['event'] as String;

  /// Sequence number of the event.
  int get seq => json['seq'] as int;

  /// The fields of the event.
  JsonObject get data => (json['data'] as JsonObject?) ?? const {};

  /// The message of an `sms.received` or `sms.sent` event, else `null`.
  Message? get message {
    final value = data['message'];
    return value is JsonObject ? Message(value) : null;
  }
}
