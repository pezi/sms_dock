// --------------------------------------------------------------------------
// Filename: client.dart
//
// Description: Reconnecting client for the SMS Dock WebSocket API: login,
//   requests, paging, events, reconnect and rate limit.
// --------------------------------------------------------------------------

/// Reconnecting client for the SMS Dock WebSocket API.
///
/// The protocol is described in `API.md` at the top of the repository.
///
/// All methods that talk to the phone return futures. Several of them may
/// be awaited at once on one client: every request carries its own id and
/// the responses are matched by that id, in whatever order they arrive.
/// Timestamps of messages are milliseconds since the Unix epoch.
library;

import 'dart:async';
import 'dart:convert';
import 'dart:io';
import 'dart:math';

import 'errors.dart';
import 'fingerprint.dart';
import 'models.dart';
import 'output.dart';

/// Protocol version this client speaks.
const int apiProtocolVersion = 1;

/// Largest frame the server accepts from a client.
const int maxFrameBytes = 64 * 1024;

/// Largest value of `limit`.
const int maxPageSize = 200;

/// Environment variable that holds the password.
const String passwordEnv = 'SMSDOCK_PASSWORD';

/// Environment variable that holds the certificate fingerprint.
const String fingerprintEnv = 'SMSDOCK_FINGERPRINT';

/// All event names of protocol version 1.
const List<String> eventNames = [
  'sms.received',
  'sms.queued',
  'sms.sent',
  'sms.delivered',
  'sms.failed',
  'sms.deleted',
  'conversations.changed',
];

/// Called with the client after every reconnect.
typedef ReconnectHook = FutureOr<void> Function(SmsDockClient client);

/// Waits before a reconnect or a repeat. The tests replace it.
Future<void> Function(Duration pause) debugSleep = _sleep;

/// Largest request frame the client sends. The tests change it.
int debugMaxFrameBytes = maxFrameBytes;

Future<void> _sleep(Duration pause) => Future<void>.delayed(pause);

final RegExp _requestIdPattern = RegExp(r'^[A-Za-z0-9._-]{1,64}$');
final RegExp _groupRequestIdPattern = RegExp(r'^[A-Za-z0-9._-]{1,61}$');

/// Recipients of one group message at most.
const int maxGroupRecipients = 20;
const List<String> _directions = ['in', 'out', 'all'];
const List<String> _outcomeEvents = ['sms.sent', 'sms.delivered', 'sms.failed'];
const int _maxBackoffExponent = 16;
const Duration _firstPoll = Duration(milliseconds: 100);
const Duration _pingInterval = Duration(seconds: 20);
const Duration _closeTimeout = Duration(seconds: 5);
const int _maxLostRequests = 1000;
final Object _hookKey = Object();
final Random _random = Random.secure();

/// One authenticated connection to SMS Dock.
///
/// Results are the JSON objects exactly as the server sent them, seen
/// through the typed views of `models.dart`.
///
/// ```dart
/// final dock = SmsDockClient(
///   'wss://192.168.1.50:9443/ws',
///   fingerprint: 'AB:CD:…',
/// );
/// try {
///   stdout.writeln(await dock.status());
/// } finally {
///   await dock.close();
/// }
/// ```
///
/// Two requests awaited at once simply work. Events are handed to one
/// reader each: run one [events] loop per client.
class SmsDockClient {
  /// Creates a client; nothing is sent before the first request.
  ///
  /// [url] is the `wss://` address the app shows, for example
  /// `wss://192.168.1.50:9443/ws`. Any other scheme is refused.
  ///
  /// [password] is the password set in the app. Without it the
  /// environment variable `SMSDOCK_PASSWORD` is read.
  ///
  /// [fingerprint] is the SHA-256 fingerprint of the certificate as shown
  /// on the Server screen of the app: hex, with or without colons, in any
  /// case. Without it the environment variable `SMSDOCK_FINGERPRINT` is
  /// read. The client compares it with the certificate of the server in
  /// the TLS handshake and sends nothing if they differ. [insecure] skips
  /// this comparison and writes a warning to standard error; use it only
  /// for tests.
  ///
  /// [timeout] is the time to wait for a response, [connectTimeout] the
  /// time to open the connection.
  ///
  /// After a lost connection the client reconnects up to
  /// [reconnectAttempts] times (`null` means without limit). It waits
  /// [reconnectDelay] before the first attempt and doubles the delay each
  /// time up to [maxReconnectDelay]. After reconnecting it logs in again
  /// and restores the subscription. Events that happened while the
  /// connection was down are lost; [missedEvents] and [serverRestarted]
  /// tell whether there were any.
  ///
  /// [onReconnect] is called with the client after every reconnect, once
  /// the subscription is restored. It can also be set later.
  ///
  /// When the server answers `rate_limited` and names the seconds to
  /// wait, the client waits and repeats the request, at most
  /// [rateLimitRetries] times.
  ///
  /// At most [eventQueueSize] events are kept while nobody reads them;
  /// after that the oldest are dropped and counted in [droppedEvents].
  ///
  /// [environment] replaces the environment of the process; it exists for
  /// tests.
  ///
  /// Throws [ArgumentError] for a wrong argument.
  SmsDockClient(
    this.url, {
    String? password,
    String? fingerprint,
    this.insecure = false,
    this.timeout = const Duration(seconds: 30),
    this.connectTimeout = const Duration(seconds: 10),
    this.reconnectAttempts = 5,
    this.reconnectDelay = const Duration(seconds: 1),
    this.maxReconnectDelay = const Duration(seconds: 10),
    int eventQueueSize = 1000,
    this.rateLimitRetries = 2,
    this.onReconnect,
    Map<String, String>? environment,
  }) : _eventQueueSize = eventQueueSize {
    final target = _parseUrl(url);
    final variables = environment ?? Platform.environment;
    final secret = password ?? variables[passwordEnv];
    if (secret == null || secret.isEmpty) {
      throw ArgumentError(
        'No password: pass one or set the environment variable $passwordEnv',
      );
    }
    var pinned = fingerprint;
    if (pinned == null) {
      final fromEnvironment = variables[fingerprintEnv];
      if (fromEnvironment != null && fromEnvironment.isNotEmpty) {
        pinned = fromEnvironment;
      }
    }
    if (pinned == null && !insecure) {
      throw ArgumentError(
        'No certificate fingerprint: pass the fingerprint shown on '
        'the Server screen of the app or set the environment '
        'variable $fingerprintEnv',
      );
    }
    if (timeout <= Duration.zero || connectTimeout <= Duration.zero) {
      throw ArgumentError('Timeouts must be positive');
    }
    final attempts = reconnectAttempts;
    if (attempts != null && attempts < 0) {
      throw ArgumentError('reconnectAttempts cannot be negative');
    }
    if (reconnectDelay.isNegative || maxReconnectDelay.isNegative) {
      throw ArgumentError('Reconnect delays cannot be negative');
    }
    if (eventQueueSize < 1) {
      throw ArgumentError('eventQueueSize must be at least 1');
    }
    if (rateLimitRetries < 0) {
      throw ArgumentError('rateLimitRetries cannot be negative');
    }
    _password = secret;
    _target = target;
    if (!insecure && pinned != null) {
      _pin = pinBytes(pinned);
    }
  }

  /// The URL as given.
  final String url;

  /// Whether the certificate is not checked.
  final bool insecure;

  /// Time to wait for a response.
  final Duration timeout;

  /// Time to open the connection.
  final Duration connectTimeout;

  /// Reconnect attempts after a lost connection; `null` is no limit.
  final int? reconnectAttempts;

  /// Time before the first reconnect attempt.
  final Duration reconnectDelay;

  /// Upper limit of the delay between reconnect attempts.
  final Duration maxReconnectDelay;

  /// How often a request is repeated after `rate_limited`.
  final int rateLimitRetries;

  /// Called with the client after every reconnect, or `null`.
  ///
  /// It may return a future; the client waits for it before it repeats
  /// the interrupted request or hands out the next event.
  ReconnectHook? onReconnect;

  /// Name and version of the app, known after the login.
  AppInfo? get serverInfo => _serverInfo;

  /// Number of events dropped because the event queue was full.
  int get droppedEvents => _droppedEvents;

  /// Number of connections opened so far, reconnects included.
  int get connectionCount => _connectionCount;

  late final String _password;
  late final ({Uri uri, String host, int port}) _target;
  List<int>? _pin;
  final int _eventQueueSize;
  final Stopwatch _clock = Stopwatch()..start();

  AppInfo? _serverInfo;
  int _droppedEvents = 0;
  int _connectionCount = 0;

  /// The connection in use, or `null`.
  _Connection? _connection;

  /// The open in progress, or `null`.
  _Opening? _opening;

  /// Raised by every [close]; tells waiting code that it was closed.
  int _generation = 0;

  /// A failure nobody waited for; the next operation reports it.
  Object? _pendingFailure;

  /// Requests of [sendRequest] whose connection was lost.
  final Map<String, TransportError> _lost = {};
  final List<_Queued> _events = [];
  int _eventSerial = 0;
  final Set<Completer<_Signal>> _eventWaiters = {};
  int _requestNumber = 0;
  ({List<String> events, String? address})? _subscription;
  bool _warned = false;
  int? _epoch;
  int? _latestSeq;
  bool _restarted = false;
  int? _missed = 0;

  /// Returns a text without the password.
  @override
  String toString() =>
      '<SmsDockClient $url ${connected ? 'connected' : 'closed'}>';

  // ------------------------------------------------------------------
  // Connection
  // ------------------------------------------------------------------

  /// Whether a connection is open and logged in.
  bool get connected {
    final connection = _connection;
    return connection != null &&
        connection.ready &&
        !connection.down &&
        connection.socket.readyState == WebSocket.open;
  }

  /// The subscription in force, or `null` without one.
  Subscription? get subscription {
    final current = _subscription;
    if (current == null) {
      return null;
    }
    return Subscription({
      'events': [...current.events],
      'address': current.address,
    });
  }

  /// Number of events that wait in the queue.
  int get pendingEvents => _events.length;

  /// The `epoch` of the last login; `null` before the first.
  ///
  /// It identifies one run of the app. Sequence numbers begin at 1 again
  /// when it changes.
  int? get epoch => _epoch;

  /// Sequence number of the newest event the client knows of.
  ///
  /// This is the `latestSeq` of the last login or the `seq` of an event
  /// received since, whichever is higher. The phone counts all events, so
  /// a client that subscribed to some of them sees gaps.
  int? get latestSeq => _latestSeq;

  /// Whether the app was restarted before the last reconnect.
  ///
  /// `true` when the `epoch` of the current connection differs from the
  /// one of the connection before. Events were lost then, and their
  /// number is not known.
  bool get serverRestarted => _restarted;

  /// Number of events of the phone during the last interruption.
  ///
  /// Counted over all events, subscribed or not. `0` after the first
  /// connect, `null` when the app was restarted and the number is not
  /// known.
  int? get missedEvents => _missed;

  /// Opens the connection, checks the certificate and logs in.
  ///
  /// Makes one attempt. Throws [CertificateMismatchError],
  /// [AuthenticationError], [LicenceRequiredError], [ProtocolError] or
  /// [ConnectionInterrupted]. Every request connects by itself when
  /// needed, so calling this method is optional.
  Future<void> connect() async {
    await close();
    try {
      await _ensureOpen();
    } on TransportError catch (error) {
      _abandon(error);
      throw fatalError(error) ?? interrupted('Cannot connect to $url', error);
    }
  }

  /// Closes the connection. Queued events are kept.
  ///
  /// Requests that still wait for a response fail with
  /// [ConnectionInterrupted]; a running [events] stream ends. The future
  /// completes when the socket is closed.
  Future<void> close() async {
    _generation += 1;
    _wake(_Signal.closed);
    final connection = _connection;
    if (connection != null) {
      _down(connection, _closedError(), remember: false);
    }
    _pendingFailure = null;
    _lost.clear();
    if (connection != null) {
      await _dispose(connection);
    }
  }

  // ------------------------------------------------------------------
  // Requests
  // ------------------------------------------------------------------

  /// Sends one request and returns the `result` of its response.
  ///
  /// [fields] are the fields of the action as named in `API.md`. With
  /// [retry] the request is sent again after a lost connection; pass
  /// `retry: false` for anything that must not happen twice. The same
  /// holds for the error `rate_limited`: the client waits the seconds the
  /// server names and repeats the request. Throws [ApiError] when the
  /// server answers with an error and [ConnectionInterrupted] when the
  /// connection could not be restored.
  Future<JsonObject> request(
    String action, {
    Map<String, Object?> fields = const {},
    bool retry = true,
    Duration? timeout,
  }) async {
    _checkRequest(action, fields);
    final limit = _orDefault(timeout);
    final generation = _generation;
    var attempt = 0;
    while (true) {
      try {
        await _ensureOpen();
        return await _requestPatiently(
          null,
          action,
          fields,
          limit,
          retry ? rateLimitRetries : 0,
        );
      } on TransportError catch (error) {
        final what = "performing '$action'";
        if (!retry) {
          _abandon(error);
          throw fatalError(error) ??
              interrupted('Connection interrupted while $what', error);
        }
        await _recover(what, error, attempt, generation);
        attempt += 1;
      }
    }
  }

  /// Sends a request without waiting and returns its id.
  ///
  /// Fetch the response with [waitForResponse]. Unlike [request], these
  /// two methods do not reconnect and do not repeat a request that was
  /// answered with `rate_limited`. Use them to keep control of each
  /// frame; for requests that just run side by side, `Future.wait` over
  /// [request] is enough.
  Future<String> sendRequest(
    String action, {
    Map<String, Object?> fields = const {},
  }) async {
    _checkRequest(action, fields);
    try {
      await _ensureOpen();
      return _sendFrame(_current(), action, fields);
    } on TransportError catch (error) {
      _abandon(error);
      throw fatalError(error) ??
          interrupted("Connection interrupted while sending '$action'", error);
    }
  }

  /// Returns the result of a request sent with [sendRequest].
  ///
  /// Throws [ApiError] when the response is an error, [TimeoutException]
  /// when none arrives in time and [ConnectionInterrupted] when the
  /// connection was lost.
  Future<JsonObject> waitForResponse(
    String requestId, {
    Duration? timeout,
  }) async {
    final label = "Connection interrupted while waiting for '$requestId'";
    final lost = _lost.remove(requestId);
    if (lost != null) {
      throw fatalError(lost) ?? interrupted(label, lost);
    }
    final connection = _connection;
    final entry = connection?.outstanding[requestId];
    if (connection == null || entry == null || entry.waiter != null) {
      throw ArgumentError("No request with the id '$requestId' is open");
    }
    final limit = _orDefault(timeout);
    try {
      return await _awaitResponse(
        connection,
        requestId,
        'request $requestId',
        limit,
      );
    } on _ResponseTimeout catch (error) {
      throw TimeoutException(error.message, limit);
    } on TransportError catch (error) {
      throw fatalError(error) ?? interrupted(label, error);
    }
  }

  /// Returns phone, SIMs, permissions, licence and limits.
  Future<Status> status() async => Status(await request('status'));

  /// Checks the connection. Returns the time of the phone.
  Future<PingResult> ping() async => PingResult(await request('ping'));

  /// Returns one page of conversations, newest first.
  ///
  /// [q] searches text, numbers and contact names. [unread] keeps only
  /// conversations with unread messages. [sim] is a subscription id.
  /// [cursor] is the `nextCursor` of the previous page.
  Future<Page<Conversation>> conversations({
    String? q,
    bool unread = false,
    int? sim,
    int limit = 50,
    String? cursor,
  }) async {
    final fields = {
      ..._conversationFilter(q, unread, sim),
      ..._paging(limit, cursor),
    };
    return Page(await request('conversations.list', fields: fields));
  }

  /// Yields conversations, newest first, page after page.
  ///
  /// The filters are those of [conversations]. [pageSize] is the `limit`
  /// of each request. [maxItems] ends the stream after that many items;
  /// without it all of them are yielded.
  Stream<Conversation> iterConversations({
    String? q,
    bool unread = false,
    int? sim,
    int pageSize = 50,
    int? maxItems,
  }) {
    final filters = _conversationFilter(q, unread, sim);
    return _iterate(
      'conversations.list',
      filters,
      pageSize,
      maxItems,
    ).map(Conversation.new);
  }

  /// Returns one page of messages, newest first.
  ///
  /// [address] keeps messages from or to one number, [conversationId]
  /// those of one conversation. [direction] is `'in'`, `'out'` or
  /// `'all'`. [q] searches the text. [since] and [until] limit the time,
  /// both inclusive; they are milliseconds since the epoch (`int`) or
  /// [DateTime] objects and are sent as the fields `from` and `to`. [sim]
  /// is a subscription id. [limit] is 1 to 200. [cursor] is the
  /// `nextCursor` of the previous page.
  Future<Page<Message>> messages({
    String? address,
    int? conversationId,
    String direction = 'all',
    String? q,
    Object? since,
    Object? until,
    int? sim,
    bool unread = false,
    int limit = 50,
    String? cursor,
  }) async {
    final fields = {
      ..._messageFilter(
        address,
        conversationId,
        direction,
        q,
        since,
        until,
        sim,
        unread,
      ),
      ..._paging(limit, cursor),
    };
    return Page(await request('messages.list', fields: fields));
  }

  /// Yields messages, newest first, following `nextCursor`.
  ///
  /// The filters are those of [messages]. [pageSize] is the `limit` of
  /// each request. [maxItems] ends the stream after that many messages;
  /// without it all of them are yielded. Wrong arguments throw at once;
  /// the first request is sent when the stream is listened to.
  Stream<Message> iterMessages({
    String? address,
    int? conversationId,
    String direction = 'all',
    String? q,
    Object? since,
    Object? until,
    int? sim,
    bool unread = false,
    int pageSize = 50,
    int? maxItems,
  }) {
    final filters = _messageFilter(
      address,
      conversationId,
      direction,
      q,
      since,
      until,
      sim,
      unread,
    );
    return _iterate(
      'messages.list',
      filters,
      pageSize,
      maxItems,
    ).map(Message.new);
  }

  /// Returns one message. Throws `not_found` if it does not exist.
  Future<Message> getMessage(int messageId) async =>
      Message(await request('messages.get', fields: {'messageId': messageId}));

  /// Sends an SMS and returns the [SendResult].
  ///
  /// [to] is the phone number, [body] the text (at most 10 SMS parts),
  /// [sim] a subscription id (default: the SMS SIM of the phone). The
  /// message is on its way when the future completes; use
  /// [waitForResult] for the outcome.
  ///
  /// [requestId] makes the request safe to repeat: the phone sends a
  /// message with the same `requestId` only once and remembers the id for
  /// 30 days. A repeat returns the result of the first request with the
  /// current `status`, which may be `sent` already. A repeat never sends
  /// again, also not after the first attempt failed: the status is
  /// `failed` then, and a new attempt needs a new `requestId`. A repeat
  /// with another recipient or text fails with `invalid_request`.
  ///
  /// Without [requestId] the client creates a random one. If the
  /// connection drops before the response arrives, the request is
  /// repeated with the same id.
  ///
  /// In the result `to` is the number as it is sent, without spaces and
  /// other ignored characters.
  Future<SendResult> send(
    String to,
    String body, {
    int? sim,
    String? requestId,
  }) async {
    if (to.trim().isEmpty) {
      throw ArgumentError("'to' must be a phone number");
    }
    if (body.isEmpty) {
      throw ArgumentError("'body' must be a text");
    }
    if (requestId != null && !_requestIdPattern.hasMatch(requestId)) {
      throw ArgumentError(
        'requestId must be 1 to 64 characters of A-Z a-z 0-9 . _ -',
      );
    }
    final fields = <String, Object?>{
      'to': to,
      'body': body,
      'requestId': requestId ?? _randomId(),
      if (sim != null) 'sim': sim,
    };
    return SendResult(await request('messages.send', fields: fields));
  }

  /// Sends one text to several numbers, one SMS each, and returns the
  /// [GroupSendResult].
  ///
  /// [to] holds 1 to 20 phone numbers; repeated numbers are sent once.
  /// Every recipient gets a separate SMS and does not see the others. The
  /// result holds one [SendResult] per recipient; their request ids are
  /// `<requestId>.1`, `<requestId>.2` and so on, and [waitForResult] and
  /// [messageStatus] take them like those of single messages.
  ///
  /// The phone checks the whole group before it sends anything: a number
  /// that is not a phone number (`invalid_number`), a text that is too
  /// long, fewer messages left in the daily limit of the free version than
  /// the group has recipients (`quota_exceeded`) or more SMS parts than
  /// the 20 of one minute, parts times recipients (`invalid_request`),
  /// refuse the whole group. A refusal by the phone for one recipient does
  /// not stop the others; that member has the status `failed` in
  /// `messages`.
  ///
  /// [requestId] has at most 61 characters here, so that the member suffix
  /// fits, and works as for [send]: the same id with the same text and
  /// numbers is not sent again but answered with the stored members and
  /// their current status.
  Future<GroupSendResult> sendGroup(
    List<String> to,
    String body, {
    int? sim,
    String? requestId,
  }) async {
    if (to.isEmpty || to.any((number) => number.trim().isEmpty)) {
      throw ArgumentError("'to' must be a list of phone numbers");
    }
    if (to.length > maxGroupRecipients) {
      throw ArgumentError("'to' holds more than $maxGroupRecipients numbers");
    }
    if (body.isEmpty) {
      throw ArgumentError("'body' must be a text");
    }
    if (requestId != null && !_groupRequestIdPattern.hasMatch(requestId)) {
      throw ArgumentError(
        'requestId of a group must be 1 to 61 characters of '
        'A-Z a-z 0-9 . _ -',
      );
    }
    final fields = <String, Object?>{
      'to': List<String>.of(to),
      'body': body,
      'requestId': requestId ?? _randomId(),
      if (sim != null) 'sim': sim,
    };
    return GroupSendResult(await request('messages.sendGroup', fields: fields));
  }

  /// Returns the outbox entry of a message sent through SMS Dock.
  Future<OutboxEntry> messageStatus(String requestId) async => OutboxEntry(
    await request('messages.status', fields: {'requestId': requestId}),
  );

  /// Waits until a sent message succeeded or failed.
  ///
  /// Returns the outbox entry once its `status` is `sent`, `delivered` or
  /// `failed`. With [delivered] the status `sent` is not enough: the
  /// method waits for the delivery report, which some networks never
  /// send. Check `status` and `errorCode` of the result; a failed message
  /// throws nothing.
  ///
  /// The method asks the server with `messages.status`: soon after the
  /// start, then less often, at last every [pollInterval]. If the
  /// connection is subscribed to `sms.sent`, `sms.delivered` and
  /// `sms.failed` it asks as soon as such an event arrives. The events
  /// stay in the queue. Throws [TimeoutException] after [timeout]
  /// (default: the timeout of the client).
  Future<OutboxEntry> waitForResult(
    String requestId, {
    Duration? timeout,
    bool delivered = false,
    Duration pollInterval = const Duration(seconds: 1),
  }) async {
    if (pollInterval <= Duration.zero) {
      throw ArgumentError('pollInterval must be positive');
    }
    final last = delivered
        ? const ['delivered', 'failed']
        : const ['sent', 'delivered', 'failed'];
    final limit = _orDefault(timeout);
    final deadline = _clock.elapsed + limit;
    var pause = _firstPoll < pollInterval ? _firstPoll : pollInterval;
    bool outcome(JsonObject event) {
      final data = event['data'];
      return _outcomeEvents.contains(event['event']) &&
          data is Map &&
          data['requestId'] == requestId;
    }

    while (true) {
      // Only events that arrive after this request can tell something new.
      final since = _eventSerial;
      final entry = await messageStatus(requestId);
      if (last.contains(entry['status'])) {
        return entry;
      }
      final remaining = deadline - _clock.elapsed;
      if (remaining <= Duration.zero) {
        throw TimeoutException(
          "No result for the message '$requestId' in time; "
          'its status is ${_repr(entry['status'])}',
          limit,
        );
      }
      final found = await _nextEvent(
        _clock.elapsed + (pause < remaining ? pause : remaining),
        'waiting for the result',
        outcome,
        take: false,
        since: since,
      );
      if (found.signal == _Signal.closed) {
        throw _closedError();
      }
      pause = pause * 2;
      if (pause > pollInterval) {
        pause = pollInterval;
      }
    }
  }

  /// Deletes one message.
  ///
  /// With mode `hidden` the message disappears from SMS Dock but stays on
  /// the phone. If the connection drops after the server deleted the
  /// message, the repeated request fails with `not_found`.
  Future<DeleteResult> deleteMessage(int messageId) async => DeleteResult(
    await request('messages.delete', fields: {'messageId': messageId}),
  );

  /// Deletes a conversation; `deleted` is the number of its messages.
  Future<DeleteResult> deleteConversation(int conversationId) async =>
      DeleteResult(
        await request(
          'conversations.delete',
          fields: {'conversationId': conversationId},
        ),
      );

  /// Marks all messages of a conversation as read.
  ///
  /// Android lets only the default SMS app change messages; otherwise the
  /// error is `not_default_app`.
  Future<MarkReadResult> markRead(int conversationId) async => MarkReadResult(
    await request(
      'conversations.markRead',
      fields: {'conversationId': conversationId},
    ),
  );

  /// Returns one page of contacts, sorted by name.
  ///
  /// [q] searches names and numbers.
  Future<Page<Contact>> contacts({
    String? q,
    int limit = 50,
    String? cursor,
  }) async {
    final fields = {if (q != null) 'q': q, ..._paging(limit, cursor)};
    return Page(await request('contacts.list', fields: fields));
  }

  /// Yields contacts, sorted by name, page after page.
  Stream<Contact> iterContacts({String? q, int pageSize = 50, int? maxItems}) {
    final filters = <String, Object?>{if (q != null) 'q': q};
    return _iterate(
      'contacts.list',
      filters,
      pageSize,
      maxItems,
    ).map(Contact.new);
  }

  // ------------------------------------------------------------------
  // Events
  // ------------------------------------------------------------------

  /// Asks for events and returns the subscription now in force.
  ///
  /// [events] are event names, or `['*']` for all; the names are added to
  /// the subscription. [address] limits the `sms.*` events to one number
  /// and replaces the limit set before; without it the limit is removed.
  /// Numbers are compared as the phone does: `+43 699 1234567` and
  /// `0699 1234567` are the same.
  ///
  /// In the result `*` is replaced by the names it stands for. The client
  /// remembers the subscription and restores it after a reconnect.
  Future<Subscription> subscribe(
    Iterable<String> events, {
    String? address,
  }) async {
    final fields = <String, Object?>{
      'events': _eventNames(events),
      if (address != null) 'address': address,
    };
    final result = await request('subscribe', fields: fields);
    _rememberSubscription(result);
    return Subscription(result);
  }

  /// Stops events and returns the subscription now in force.
  ///
  /// [events] are event names, or `['*']` for all. When no name is left,
  /// the limit to one number is removed too.
  Future<Subscription> unsubscribe(Iterable<String> events) async {
    final result = await request(
      'unsubscribe',
      fields: {'events': _eventNames(events)},
    );
    _rememberSubscription(result);
    return Subscription(result);
  }

  /// Yields events as they arrive.
  ///
  /// Events that were queued while nobody read them come first. [timeout]
  /// is the longest time to wait for the next event; the stream ends when
  /// it passes. Without it the stream waits without limit. Subscribe
  /// first, a new connection gets no events.
  ///
  /// While the listener is paused, as it is inside the body of an
  /// `await for` loop, events stay in the queue of the client.
  ///
  /// After a lost connection the stream reconnects and goes on. It emits
  /// [ConnectionInterrupted] when that fails and ends quietly when
  /// [close] is called.
  Stream<SmsEvent> events({Duration? timeout}) {
    late final StreamController<SmsEvent> controller;
    var cancelled = false;
    Completer<void>? resumed;

    Future<void> pump() async {
      try {
        while (!cancelled) {
          if (controller.isPaused) {
            final waiting = resumed = Completer<void>();
            await waiting.future;
            continue;
          }
          final next = await _nextEvent(
            timeout == null ? null : _clock.elapsed + timeout,
            'waiting for events',
            (_) => true,
            cancelled: () => cancelled,
          );
          final queued = next.queued;
          if (queued == null) {
            break;
          }
          if (cancelled) {
            // Nobody listens any more: the event stays in the queue.
            _events.insert(0, queued);
            break;
          }
          controller.add(SmsEvent(queued.event));
        }
      } catch (error, stack) {
        if (!cancelled) {
          controller.addError(error, stack);
        }
      }
      if (!cancelled) {
        unawaited(controller.close());
      }
    }

    controller = StreamController<SmsEvent>(
      sync: true,
      onListen: () => unawaited(pump()),
      onResume: () {
        resumed?.complete();
        resumed = null;
      },
      onCancel: () {
        cancelled = true;
        resumed?.complete();
        resumed = null;
        _wake(_Signal.event);
      },
    );
    return controller.stream;
  }

  /// Returns the next event with one of these names.
  ///
  /// [names] are event names; `*` stands for all. [predicate] is called
  /// with each candidate and must return `true` for the wanted one. Other
  /// events stay in the queue. Throws [TimeoutException] after [timeout]
  /// (default: the timeout of the client).
  Future<SmsEvent> waitForEvent(
    Iterable<String> names, {
    Duration? timeout,
    bool Function(SmsEvent event)? predicate,
  }) async {
    final wantedNames = _eventNames(names).toSet();
    bool wanted(JsonObject event) {
      if (!wantedNames.contains('*') && !wantedNames.contains(event['event'])) {
        return false;
      }
      return predicate == null || predicate(SmsEvent(event));
    }

    final label = (wantedNames.toList()..sort()).join(', ');
    final limit = _orDefault(timeout);
    final next = await _nextEvent(
      _clock.elapsed + limit,
      'waiting for $label',
      wanted,
    );
    final queued = next.queued;
    if (queued != null) {
      return SmsEvent(queued.event);
    }
    if (next.signal == _Signal.closed) {
      throw _closedError();
    }
    throw TimeoutException('No event $label in time', limit);
  }

  // ------------------------------------------------------------------
  // Internals: opening a connection
  // ------------------------------------------------------------------

  Duration _orDefault(Duration? limit) =>
      limit == null || limit == Duration.zero ? timeout : limit;

  /// Makes sure a logged-in connection exists.
  ///
  /// All callers share one attempt. Code that runs inside the
  /// [onReconnect] hook belongs to the open in progress and must not wait
  /// for it.
  Future<void> _ensureOpen() async {
    while (true) {
      final opening = _opening;
      if (opening != null && !_insideHookOf(opening)) {
        await opening.future;
        continue;
      }
      if (connected) {
        return;
      }
      final closing = _connection;
      if (closing != null && closing.ready && !closing.down) {
        // The close frame of the server is being handled. Its code tells
        // whether a new attempt is allowed.
        await closing.ended.future.timeout(_closeTimeout, onTimeout: () {});
        if (!closing.down) {
          _down(
            closing,
            TransportError('The connection was lost'),
            remember: true,
          );
          closing.terminate();
        }
        continue;
      }
      final failure = _pendingFailure;
      if (failure != null) {
        _pendingFailure = null;
        throw failure;
      }
      final started = _Opening(opening);
      final future = _open(started).whenComplete(() {
        _opening = started.parent;
      });
      // The callers handle the error; this keeps it from being unhandled.
      future.ignore();
      started.future = future;
      _opening = started;
      await future;
      return;
    }
  }

  /// Connects, pins the certificate, logs in, restores the subscription.
  Future<void> _open(_Opening opening) async {
    if (insecure && !_warned) {
      _warned = true;
      errorSink.writeln(
        'smsdock: WARNING: the certificate of the server is not '
        'checked (insecure). Anyone on the network can read the '
        'password and the messages.',
      );
    }
    final generation = _generation;
    final connection = await _dial();
    if (generation != _generation) {
      connection.down = true;
      connection.terminate();
      throw _closedError();
    }
    _connection = connection;
    _connectionCount += 1;
    final reconnected = _epoch != null;
    try {
      await _handshake(connection);
      connection.ready = true;
      final hook = onReconnect;
      if (reconnected && hook != null) {
        final context = _HookContext(
          opening,
          Zone.current[_hookKey] as _HookContext?,
        );
        await runZoned(
          () async => await hook(this),
          zoneValues: {_hookKey: context},
        );
      }
    } on TransportError catch (error) {
      _down(connection, error, remember: false);
      connection.terminate();
      rethrow;
    } catch (_) {
      _down(
        connection,
        TransportError('The connection was closed'),
        remember: false,
      );
      await _dispose(connection);
      rethrow;
    }
  }

  /// Opens the pinned TLS connection and the WebSocket on top of it.
  ///
  /// The HTTP client gets its connection from [connectPinned], which
  /// compares the certificate before it hands the socket out. The upgrade
  /// request is therefore never written to a server with another
  /// certificate.
  Future<_Connection> _dial() async {
    final pin = _pin;
    final target = _target;
    SecureSocket? transport;
    Object? refusal;
    final http = HttpClient(context: SecurityContext(withTrustedRoots: false))
      ..connectionTimeout = connectTimeout
      ..findProxy = ((_) => 'DIRECT')
      // Not used with a connection factory; refuses if it ever is.
      ..badCertificateCallback = ((certificate, host, port) =>
          pin != null && matchesPin(certificate.der, pin));
    http.connectionFactory = (uri, proxyHost, proxyPort) async {
      final socket = connectPinned(
        target.host,
        target.port,
        pin,
        connectTimeout,
      ).then((socket) => transport = socket);
      return ConnectionTask.fromSocket<Socket>(
        socket.catchError((Object error) {
          refusal = error;
          throw error;
        }),
        () {},
      );
    };
    final WebSocket socket;
    try {
      socket =
          await WebSocket.connect(
            '${target.uri}',
            customClient: http,
            compression: CompressionOptions.compressionOff,
          ).timeout(
            connectTimeout,
            onTimeout: () =>
                throw TimeoutException('timed out', connectTimeout),
          );
    } catch (error) {
      http.close(force: true);
      transport?.destroy();
      final cause = refusal ?? error;
      if (cause is SmsDockError || cause is Error) {
        throw cause;
      }
      throw TransportError(_describe(cause), cause: cause);
    }
    final connection = _Connection(socket, http, transport);
    if (transport == null) {
      // The WebSocket does not run on the connection that was checked.
      connection.terminate();
      throw ProtocolError(
        'The WebSocket does not use the connection whose certificate '
        'was checked; refusing to go on',
      );
    }
    socket.pingInterval = _pingInterval;
    connection.subscription = socket.listen(
      (Object? data) => _onMessage(connection, data),
      onError: (Object error) {
        connection.streamError ??= error;
      },
      onDone: () => _onDone(connection),
    );
    return connection;
  }

  Future<void> _handshake(_Connection connection) async {
    final hello = await connection.hello.future.timeout(
      timeout,
      onTimeout: () => throw _ResponseTimeout(
        'The server sent no hello in time',
        connection,
      ),
    );
    if (hello['event'] != 'hello') {
      // Not the frame itself: it could hold the text of a message.
      throw ProtocolError(
        "Expected 'hello', received a frame with the keys ${_keysOf(hello)}",
      );
    }
    if (hello['protocolVersion'] != apiProtocolVersion) {
      throw ProtocolError(
        'The server speaks protocol version '
        '${_repr(hello['protocolVersion'])}, this client version '
        '$apiProtocolVersion',
      );
    }
    final result = await _requestPatiently(
      connection,
      'authenticate',
      {'password': _password},
      timeout,
      rateLimitRetries,
    );
    if (result['protocolVersion'] != apiProtocolVersion) {
      throw ProtocolError(
        'The login answered with protocol version '
        '${_repr(result['protocolVersion'])}',
      );
    }
    final app = result['app'];
    _serverInfo = app is JsonObject ? AppInfo(app) : null;
    _noteLogin(result['epoch'], result['latestSeq']);
    final subscription = _subscription;
    if (subscription != null && subscription.events.isNotEmpty) {
      final address = subscription.address;
      await _requestPatiently(
        connection,
        'subscribe',
        {
          'events': [...subscription.events],
          if (address != null) 'address': address,
        },
        timeout,
        rateLimitRetries,
      );
    }
  }

  /// Compares epoch and sequence number with the connection before.
  void _noteLogin(Object? epoch, Object? latest) {
    if (epoch is! int || latest is! int) {
      throw ProtocolError("The login answered without 'epoch' and 'latestSeq'");
    }
    if (_epoch == null) {
      _restarted = false;
      _missed = 0;
    } else if (epoch != _epoch) {
      _restarted = true;
      _missed = null;
    } else {
      _restarted = false;
      _missed = max(0, latest - (_latestSeq ?? 0));
    }
    _epoch = epoch;
    _latestSeq = latest;
  }

  // ------------------------------------------------------------------
  // Internals: closing and failing
  // ------------------------------------------------------------------

  void _onDone(_Connection connection) {
    if (!connection.ended.isCompleted) {
      connection.ended.complete();
    }
    final code = connection.socket.closeCode;
    final streamError = connection.streamError;
    final TransportError error;
    if (streamError != null) {
      error = TransportError(_describe(streamError), cause: streamError);
    } else if (code != null &&
        code != WebSocketStatus.noStatusReceived &&
        code != WebSocketStatus.abnormalClosure) {
      error = TransportError(
        'closed by the server with code $code',
        closeCode: code,
        closeReason: connection.socket.closeReason,
      );
    } else {
      error = TransportError('The connection was lost');
    }
    _down(connection, error, remember: true);
    connection.http.close(force: true);
  }

  /// Notes that a connection is gone and tells everybody who waits on it.
  ///
  /// With [remember], a failure that nobody waited for is kept for the
  /// next operation, which then decides whether to reconnect.
  void _down(_Connection connection, Object error, {required bool remember}) {
    if (connection.down) {
      return;
    }
    connection.down = true;
    final current = identical(_connection, connection);
    if (current) {
      _connection = null;
    }
    var told = false;
    if (!connection.hello.isCompleted) {
      connection.hello.completeError(error);
      told = true;
    }
    for (final entry in connection.outstanding.entries) {
      final waiter = entry.value.waiter;
      if (waiter != null) {
        entry.value.timer?.cancel();
        waiter.completeError(error);
        told = true;
      } else if (error is TransportError) {
        _lost[entry.key] = error;
        if (_lost.length > _maxLostRequests) {
          _lost.remove(_lost.keys.first);
        }
      }
    }
    connection.outstanding.clear();
    if (current) {
      for (final waiter in _eventWaiters.toList()) {
        _eventWaiters.remove(waiter);
        waiter.completeError(error);
        told = true;
      }
      if (remember && !told && connection.ready) {
        _pendingFailure = error;
      }
    }
  }

  /// Drops the connection a timeout belongs to: it is presumed dead.
  void _abandon(TransportError error) {
    if (error is! _ResponseTimeout || error.connection.down) {
      return;
    }
    _down(
      error.connection,
      TransportError(
        'The connection was closed after a request got no response',
      ),
      remember: false,
    );
    error.connection.terminate();
  }

  /// Closes the WebSocket politely; completes when the socket is gone.
  Future<void> _dispose(_Connection connection) async {
    final limit = timeout < _closeTimeout ? timeout : _closeTimeout;
    try {
      unawaited(
        connection.socket.close(WebSocketStatus.normalClosure).catchError((
          Object _,
        ) {
          // The connection is gone either way.
        }),
      );
      // The answer of the server ends the stream.
      await connection.ended.future.timeout(limit);
    } on Object {
      // The connection is gone either way.
    }
    connection.terminate();
  }

  /// Waits before the next attempt after a failure of the connection.
  ///
  /// Throws instead when the failure is final, no attempt is left or the
  /// client was closed meanwhile.
  Future<void> _recover(
    String what,
    TransportError error,
    int attempt,
    int generation,
  ) async {
    _abandon(error);
    if (generation != _generation) {
      throw _closedError();
    }
    final fatal = fatalError(error);
    if (fatal != null) {
      throw fatal;
    }
    final attempts = reconnectAttempts;
    if (attempts != null && attempt >= attempts) {
      throw interrupted('Connection interrupted while $what', error);
    }
    final exponent = min(attempt, _maxBackoffExponent);
    final delay = reconnectDelay * pow(2, exponent);
    await debugSleep(delay < maxReconnectDelay ? delay : maxReconnectDelay);
    if (generation != _generation) {
      throw _closedError();
    }
  }

  // ------------------------------------------------------------------
  // Internals: frames
  // ------------------------------------------------------------------

  _Connection _current() {
    final connection = _connection;
    if (connection == null || !connection.ready || connection.down) {
      throw TransportError('No connection to SMS Dock');
    }
    return connection;
  }

  /// Sends a request; waits and repeats it while it is rate limited.
  ///
  /// Only when the server names the seconds to wait. The limit of
  /// messages per minute comes without them and is reported at once.
  /// Without [connection] the connection of the moment is used for every
  /// attempt.
  Future<JsonObject> _requestPatiently(
    _Connection? connection,
    String action,
    Map<String, Object?> fields,
    Duration limit,
    int retries,
  ) async {
    var refused = 0;
    while (true) {
      try {
        final target = connection ?? _current();
        final key = _sendFrame(target, action, fields);
        return await _awaitResponse(target, key, "'$action'", limit);
      } on ApiError catch (error) {
        final wait = error.retryAfter;
        if (error.code != 'rate_limited' ||
            wait == null ||
            Duration(seconds: wait) > limit ||
            refused >= retries) {
          rethrow;
        }
        refused += 1;
        await debugSleep(Duration(seconds: max(0, wait)));
      }
    }
  }

  String _sendFrame(
    _Connection connection,
    String action,
    Map<String, Object?> fields,
  ) {
    if (connection.down || connection.socket.readyState != WebSocket.open) {
      throw TransportError('No connection to SMS Dock');
    }
    _requestNumber += 1;
    final id = 'dart-$_requestNumber';
    final frame = jsonEncode({'id': id, 'action': action, ...fields});
    if (utf8.encode(frame).length > debugMaxFrameBytes) {
      throw ArgumentError(
        'The request is larger than ${maxFrameBytes ~/ 1024} KiB',
      );
    }
    connection.outstanding[id] = _Pending();
    connection.socket.add(frame);
    return id;
  }

  Future<JsonObject> _awaitResponse(
    _Connection connection,
    String key,
    String label,
    Duration limit,
  ) async {
    final entry = connection.outstanding[key];
    if (entry == null) {
      throw TransportError('No connection to SMS Dock');
    }
    var response = entry.response;
    if (response == null) {
      final waiter = entry.waiter = Completer<JsonObject>();
      entry.timer = Timer(limit, () {
        entry.waiter = null;
        waiter.completeError(
          _ResponseTimeout('No response to $label in time', connection),
        );
      });
      response = await waiter.future;
    }
    connection.outstanding.remove(key);
    return _resultOf(response, label);
  }

  void _onMessage(_Connection connection, Object? data) {
    if (connection.down) {
      return;
    }
    final JsonObject message;
    try {
      message = _parseFrame(data);
    } on ProtocolError catch (error) {
      _down(connection, error, remember: true);
      connection.terminate();
      return;
    }
    final isEvent = message.containsKey('event');
    final sequence = message['seq'];
    final latest = _latestSeq;
    if (isEvent && sequence is int && (latest == null || sequence > latest)) {
      _latestSeq = sequence;
    }
    if (!connection.helloSeen) {
      connection.helloSeen = true;
      connection.hello.complete(message);
    } else if (isEvent) {
      _queueEvent(message);
    } else {
      _fileResponse(connection, message);
    }
  }

  /// Hands a response to the request that waits for it.
  void _fileResponse(_Connection connection, JsonObject response) {
    final identifier = response['id'];
    var key = identifier?.toString();
    if (key == null && connection.outstanding.length == 1) {
      // The server could not read the id of the only open request.
      key = connection.outstanding.keys.first;
    }
    final entry = key == null ? null : connection.outstanding[key];
    if (entry == null || entry.response != null) {
      // It answers a request nobody waits for any more.
      return;
    }
    entry.response = response;
    final waiter = entry.waiter;
    if (waiter != null) {
      entry.timer?.cancel();
      entry.waiter = null;
      waiter.complete(response);
    }
  }

  // ------------------------------------------------------------------
  // Internals: events
  // ------------------------------------------------------------------

  void _queueEvent(JsonObject event) {
    if (_events.length >= _eventQueueSize) {
      _events.removeAt(0);
      _droppedEvents += 1;
    }
    _eventSerial += 1;
    _events.add(_Queued(_eventSerial, event));
    _wake(_Signal.event);
  }

  void _wake(_Signal signal) {
    for (final waiter in _eventWaiters.toList()) {
      _eventWaiters.remove(waiter);
      waiter.complete(signal);
    }
  }

  /// Completes with what ended the wait; fails when the connection does.
  Future<_Signal> _signal(Duration? wait) async {
    final waiter = Completer<_Signal>();
    _eventWaiters.add(waiter);
    final timer = wait == null
        ? null
        : Timer(wait, () {
            if (_eventWaiters.remove(waiter)) {
              waiter.complete(_Signal.timeout);
            }
          });
    try {
      return await waiter.future;
    } finally {
      timer?.cancel();
    }
  }

  /// Returns the first event that [accept] wants, from the queue or as
  /// soon as it arrives. Reconnects when needed.
  ///
  /// With [take] the event leaves the queue. [since] skips events that
  /// were queued before. [cancelled] ends the wait without an event.
  Future<_Next> _nextEvent(
    Duration? deadline,
    String what,
    bool Function(JsonObject event) accept, {
    bool take = true,
    int since = 0,
    bool Function()? cancelled,
  }) async {
    final generation = _generation;
    var seen = since;
    _Queued? scan() {
      for (var index = 0; index < _events.length; index += 1) {
        final queued = _events[index];
        if (queued.serial > seen) {
          seen = queued.serial;
          if (accept(queued.event)) {
            if (take) {
              _events.removeAt(index);
            }
            return queued;
          }
        }
      }
      return null;
    }

    var attempt = 0;
    while (true) {
      if (cancelled != null && cancelled()) {
        return const _Next(_Signal.closed);
      }
      final found = scan();
      if (found != null) {
        return _Next(_Signal.event, found);
      }
      try {
        await _ensureOpen();
        if (_events.isNotEmpty && _events.last.serial > seen) {
          continue;
        }
        Duration? wait;
        if (deadline != null) {
          wait = deadline - _clock.elapsed;
          if (wait <= Duration.zero) {
            return const _Next(_Signal.timeout);
          }
        }
        final signal = await _signal(wait);
        if (signal != _Signal.event) {
          return _Next(signal);
        }
        attempt = 0;
      } on TransportError catch (error) {
        if (generation != _generation) {
          return const _Next(_Signal.closed);
        }
        await _recover(what, error, attempt, generation);
        attempt += 1;
      } catch (_) {
        if (generation != _generation) {
          return const _Next(_Signal.closed);
        }
        rethrow;
      }
    }
  }

  void _rememberSubscription(JsonObject result) {
    final events = result['events'];
    if (events is! List) {
      throw ProtocolError(
        'Malformed subscription: a result with the keys ${_keysOf(result)}',
      );
    }
    final address = result['address'];
    _subscription = (
      events: [for (final name in events) '$name'],
      address: address is String ? address : null,
    );
  }

  // ------------------------------------------------------------------
  // Internals: paging
  // ------------------------------------------------------------------

  Stream<JsonObject> _iterate(
    String action,
    Map<String, Object?> filters,
    int pageSize,
    int? maxItems,
  ) {
    if (maxItems != null && maxItems < 0) {
      throw ArgumentError('maxItems cannot be negative');
    }
    _paging(pageSize, null);
    return _pages(action, filters, pageSize, maxItems);
  }

  Stream<JsonObject> _pages(
    String action,
    Map<String, Object?> filters,
    int pageSize,
    int? maxItems,
  ) async* {
    String? cursor;
    var count = 0;
    while (maxItems == null || count < maxItems) {
      final limit = maxItems == null
          ? pageSize
          : min(pageSize, maxItems - count);
      final page = await request(
        action,
        fields: {...filters, ..._paging(limit, cursor)},
      );
      final items = page['items'];
      if (items is! List) {
        throw ProtocolError('$action returned no list of items');
      }
      for (final item in items) {
        if (item is! JsonObject) {
          throw ProtocolError('$action returned an item that is no object');
        }
        yield item;
        count += 1;
        if (maxItems != null && count >= maxItems) {
          return;
        }
      }
      final next = page['nextCursor'];
      if (next == null) {
        return;
      }
      if (next is! String || next == cursor) {
        throw ProtocolError('$action returned the cursor ${_repr(next)}');
      }
      cursor = next;
    }
  }
}

// ----------------------------------------------------------------------
// Helpers
// ----------------------------------------------------------------------

enum _Signal { event, timeout, closed }

/// What a wait for an event ended with.
class _Next {
  const _Next(this.signal, [this.queued]);

  final _Signal signal;
  final _Queued? queued;
}

/// An event in the queue with the number of its arrival.
class _Queued {
  _Queued(this.serial, this.event);

  final int serial;
  final JsonObject event;
}

/// A request that waits for its response.
class _Pending {
  JsonObject? response;
  Completer<JsonObject>? waiter;
  Timer? timer;
}

/// An open in progress; [parent] is the one whose hook started it.
class _Opening {
  _Opening(this.parent);

  final _Opening? parent;
  late final Future<void> future;
}

/// Tells code that runs inside an `onReconnect` hook which open it is in.
class _HookContext {
  _HookContext(this.opening, this.parent);

  final _Opening opening;
  final _HookContext? parent;
}

/// One WebSocket connection and what waits on it.
class _Connection {
  _Connection(this.socket, this.http, this.transport) {
    // A failure before anybody waits for the hello is reported later.
    hello.future.ignore();
  }

  final WebSocket socket;
  final HttpClient http;
  final SecureSocket? transport;
  bool ready = false;
  bool down = false;
  bool helloSeen = false;
  Object? streamError;
  StreamSubscription<dynamic>? subscription;
  final Completer<JsonObject> hello = Completer<JsonObject>();

  /// Completes when the stream of the WebSocket has ended.
  final Completer<void> ended = Completer<void>();
  final Map<String, _Pending> outstanding = {};

  /// Ends the connection at once, without a close frame.
  void terminate() {
    // The ping timer of the WebSocket would keep the process alive.
    socket.pingInterval = null;
    unawaited(subscription?.cancel());
    transport?.destroy();
    http.close(force: true);
  }
}

/// A request got no response in time on [connection].
class _ResponseTimeout extends TransportError {
  _ResponseTimeout(super.message, this.connection);

  final _Connection connection;
}

bool _insideHookOf(_Opening opening) {
  var context = Zone.current[_hookKey] as _HookContext?;
  while (context != null) {
    if (identical(context.opening, opening)) {
      return true;
    }
    context = context.parent;
  }
  return false;
}

ConnectionInterrupted _closedError() =>
    ConnectionInterrupted('The client was closed');

String _keysOf(Object? value) =>
    value is Map ? '[${value.keys.join(', ')}]' : '[]';

/// A value inside an error text, written the way Python writes it.
String _repr(Object? value) => switch (value) {
  null => 'None',
  true => 'True',
  false => 'False',
  final String text => "'$text'",
  final num number => '$number',
  _ => jsonEncode(value),
};

/// The text of a failed connection, written as Python writes it.
String describeSocketError(SocketException error) {
  final cause = error.osError;
  if (cause == null || cause.message.isEmpty) {
    return error.message;
  }
  return '[Errno ${cause.errorCode}] ${cause.message}';
}

String _describe(Object error) {
  if (error is TimeoutException) {
    return error.message ?? 'The connection timed out';
  }
  if (error is SocketException) {
    return describeSocketError(error);
  }
  final text = '$error';
  return text.isEmpty ? '${error.runtimeType}' : text;
}

String _randomId() => [
  for (var index = 0; index < 16; index += 1)
    _random.nextInt(256).toRadixString(16).padLeft(2, '0'),
].join();

({Uri uri, String host, int port}) _parseUrl(String url) {
  final parts = Uri.tryParse(url.trim());
  if (parts == null || !parts.isScheme('wss') || parts.host.isEmpty) {
    throw ArgumentError(
      'SMS Dock needs a wss:// URL, for example wss://192.168.1.50:9443/ws',
    );
  }
  final port = parts.hasPort ? parts.port : 443;
  return (uri: parts.replace(port: port), host: parts.host, port: port);
}

JsonObject _parseFrame(Object? data) {
  if (data is! String) {
    throw ProtocolError('The server sent a binary frame');
  }
  final Object? decoded;
  try {
    decoded = jsonDecode(data);
  } on FormatException {
    throw ProtocolError('The server sent invalid JSON');
  }
  if (decoded is! JsonObject) {
    throw ProtocolError('The frame is not a JSON object');
  }
  return decoded;
}

JsonObject _resultOf(JsonObject response, String label) {
  if (response['ok'] == true) {
    final result = response['result'];
    if (result is! JsonObject) {
      throw ProtocolError('The result of $label is not a JSON object');
    }
    return result;
  }
  final error = response['error'];
  if (response['ok'] != false || error is! JsonObject) {
    // Not the response itself: it could hold the text of a message.
    throw ProtocolError(
      'Malformed response with the keys ${_keysOf(response)}',
    );
  }
  throw apiError(error);
}

void _checkRequest(String action, Map<String, Object?> fields) {
  if (action.isEmpty) {
    throw ArgumentError('action must be the name of an action');
  }
  if (fields.containsKey('id')) {
    throw ArgumentError("'id' is the id of the request; the client sets it");
  }
}

int _millis(Object value, String label) => switch (value) {
  final DateTime moment => moment.millisecondsSinceEpoch,
  final int milliseconds => milliseconds,
  final double milliseconds when milliseconds.isFinite => milliseconds.toInt(),
  _ => throw ArgumentError(
    '$label must be milliseconds since the epoch or a DateTime',
  ),
};

Map<String, Object?> _paging(int limit, String? cursor) {
  if (limit < 1 || limit > maxPageSize) {
    throw ArgumentError('limit must be between 1 and $maxPageSize');
  }
  return {'limit': limit, if (cursor != null) 'cursor': cursor};
}

Map<String, Object?> _conversationFilter(String? q, bool unread, int? sim) => {
  if (q != null) 'q': q,
  if (unread) 'unread': true,
  if (sim != null) 'sim': sim,
};

Map<String, Object?> _messageFilter(
  String? address,
  int? conversationId,
  String direction,
  String? q,
  Object? since,
  Object? until,
  int? sim,
  bool unread,
) {
  if (!_directions.contains(direction)) {
    throw ArgumentError("direction must be 'in', 'out' or 'all'");
  }
  return {
    if (address != null) 'address': address,
    if (conversationId != null) 'conversationId': conversationId,
    if (direction != 'all') 'direction': direction,
    if (q != null) 'q': q,
    if (since != null) 'from': _millis(since, 'since'),
    if (until != null) 'to': _millis(until, 'until'),
    if (sim != null) 'sim': sim,
    if (unread) 'unread': true,
  };
}

List<String> _eventNames(Iterable<String> events) {
  final names = events.toList();
  if (names.isEmpty || names.any((name) => name.isEmpty)) {
    throw ArgumentError('events must be one event name or several');
  }
  return names;
}
