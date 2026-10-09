// --------------------------------------------------------------------------
// Filename: fake_server.dart
//
// Description: Fake SMS Dock server that speaks protocol version 1 of API.md.
//   Used by the tests; also runs on its own.
// --------------------------------------------------------------------------

/// A fake SMS Dock server that speaks protocol version 1 of `API.md`.
///
/// It keeps all data in memory and sends nothing. The tests run it in
/// their own isolate. It can also run on its own to try the examples and
/// the command-line tool without a phone:
///
/// ```bash
/// dart run test/fake_server.dart --port 9443 --incoming-every 15
/// ```
///
/// The real server runs on the phone. This one follows `API.md`; where
/// the document leaves something open, a comment names the assumption.
library;

import 'dart:async';
import 'dart:collection';
import 'dart:convert';
import 'dart:io';
import 'dart:math';

import 'package:crypto/crypto.dart';

typedef JsonObject = Map<String, dynamic>;

const int protocolVersion = 1;
const int maxFrameBytes = 64 * 1024;
const List<String> eventNames = [
  'sms.received',
  'sms.queued',
  'sms.sent',
  'sms.delivered',
  'sms.failed',
  'sms.deleted',
  'conversations.changed',
];

final RegExp _requestId = RegExp(r'^[A-Za-z0-9._-]{1,64}$');
final RegExp _number = RegExp(r'^\+?[0-9]{3,15}$');
final RegExp _ignored = RegExp(r'[\s\-./()]');
final RegExp _digitsOnly = RegExp(r'^[0-9]+$');
final RegExp _fingerprint = RegExp(r'^([0-9A-F]{2}:){31}[0-9A-F]{2}$');
const String _webSocketGuid = '258EAFA5-E914-47DA-95CA-C5AB0DC85B11';
final Random _random = Random();

/// A throwaway self-signed certificate made with `openssl`.
class TlsFiles {
  TlsFiles(this.certificate, this.key, this.fingerprint);

  final String certificate;
  final String key;
  final String fingerprint;

  /// Returns the TLS context of a server that presents it.
  SecurityContext serverContext() => SecurityContext(withTrustedRoots: false)
    ..useCertificateChain(certificate)
    ..usePrivateKey(key);
}

/// Creates a self-signed EC certificate like the one of the phone.
///
/// Returns `null` when the `openssl` command is missing or fails. The
/// fingerprint is the one `openssl` prints, so that the tests compare the
/// client with an independent implementation.
TlsFiles? makeCertificate(Directory directory, [String name = 'server']) {
  final key = '${directory.path}/$name.key';
  final certificate = '${directory.path}/$name.crt';
  final commands = [
    ['ecparam', '-name', 'prime256v1', '-genkey', '-noout', '-out', key],
    [
      'req', '-x509', '-new', '-key', key, '-sha256', '-days', '2', //
      '-subj', '/CN=SMS Dock', '-out', certificate,
    ],
    ['x509', '-in', certificate, '-noout', '-fingerprint', '-sha256'],
  ];
  var shown = '';
  try {
    for (final command in commands) {
      final result = Process.runSync('openssl', command);
      if (result.exitCode != 0) {
        return null;
      }
      shown = '${result.stdout}';
    }
  } on ProcessException {
    return null;
  }
  final fingerprint = shown.trim().split('=').last;
  if (!_fingerprint.hasMatch(fingerprint)) {
    return null;
  }
  return TlsFiles(certificate, key, fingerprint);
}

/// An error response, optionally followed by a close frame.
class Failure implements Exception {
  Failure(
    String code,
    String message, {
    this.closeCode,
    JsonObject extra = const {},
  }) : error = {'code': code, 'message': message, ...extra};

  final JsonObject error;
  final int? closeCode;
}

class _Client {
  _Client(this.socket, this.connection);

  final Socket socket;
  final WebSocket connection;
  bool authenticated = false;
  bool closing = false;
  final Set<String> events = {};
  String? address;
  final Queue<Duration> requestTimes = Queue();
  int refused = 0;
  Timer? timer;
}

/// Removes the characters that are ignored in phone numbers.
///
/// These are spaces, dashes, dots, slashes and brackets.
String normalizeNumber(String number) => number.trim().replaceAll(_ignored, '');

/// Whether an address is a phone number and not a sender name.
bool isNumber(String address) =>
    RegExp(r'^\+?[0-9]+$').hasMatch(normalizeNumber(address));

/// Returns a key that is equal for equal addresses where that is known.
///
/// The international form for numbers written with `+` or `00`, the bare
/// digits for other numbers, lower case for sender names.
String addressKey(String address) {
  if (!isNumber(address)) {
    return address.trim().toLowerCase();
  }
  final digits = normalizeNumber(address);
  if (digits.startsWith('00') && digits.length > 4) {
    return '+${digits.substring(2)}';
  }
  return digits;
}

/// Whether two addresses mean the same peer ("Phone numbers" in API.md).
///
/// `+43 699 1234567`, `0043 699 1234567` and `0699 1234567` are the same.
/// A number without country code matches if its digits without leading
/// zeros, at least 7, end the other number. Sender names are compared as
/// text, ignoring case.
bool samePeer(String first, String second) {
  final a = addressKey(first);
  final b = addressKey(second);
  if (a == b) {
    return true;
  }
  if (!isNumber(first) || !isNumber(second)) {
    return false;
  }
  if (a.startsWith('+') && b.startsWith('+')) {
    return false;
  }
  bool ends(String full, String national) {
    final significant = national.replaceFirst(RegExp('^0+'), '');
    return significant.length >= 7 && full.endsWith(significant);
  }

  if (a.startsWith('+')) {
    return ends(a, b);
  }
  if (b.startsWith('+')) {
    return ends(b, a);
  }
  return ends(a, b) || ends(b, a);
}

/// Returns the number of SMS parts of a text (simplified alphabet test).
int countParts(String body) {
  final runes = body.runes.toList();
  final plain = runes.every((rune) => rune < 128);
  final (single, multi) = plain ? (160, 153) : (70, 67);
  if (runes.length <= single) {
    return 1;
  }
  return (runes.length / multi).ceil();
}

/// In-memory SMS Dock server on `wss://127.0.0.1:PORT/ws`.
///
/// Fields such as [messages], [sent] and [requests] may be read by the
/// tests; use the methods to change anything while clients are connected.
class FakeSmsDock {
  FakeSmsDock({
    required this.tls,
    this.password = 'secret123',
    this.host = '127.0.0.1',
    this.port = 0,
    this.authTimeout = const Duration(seconds: 10),
    this.maxClients = 8,
    this.sendDelay = const Duration(milliseconds: 20),
    this.deliveryReports = false,
    this.requestsPerSecond = 20,
    this.maxRefused = 20,
  });

  final TlsFiles tls;
  final String password;
  final String host;
  int port;
  final Duration authTimeout;
  final int maxClients;
  final Duration sendDelay;
  final bool deliveryReports;
  final int requestsPerSecond;
  final int maxRefused;

  /// Identifies one run of the app; [restart] changes it.
  int epoch = DateTime.now().millisecondsSinceEpoch;
  int protocol = protocolVersion;
  bool licenceEnforced = false;
  bool licensed = false;
  String deleteMode = 'hidden';
  int failedLogins = 0;
  int lockCount = 0;
  Duration lockedUntil = Duration.zero;

  final List<JsonObject> sims = [
    {
      'subscriptionId': 1,
      'slot': 0,
      'name': 'Play',
      'number': null,
      'isDefault': true,
    },
    {
      'subscriptionId': 2,
      'slot': 1,
      'name': 'Work',
      'number': '+4369087654321',
      'isDefault': false,
    },
  ];
  final List<JsonObject> contacts = [];
  List<JsonObject> messages = [];
  final Map<String, JsonObject> outbox = {};
  final Set<String> failNumbers = {};

  /// Messages handed to the (imaginary) radio, one entry per SMS.
  final List<JsonObject> sent = [];

  /// Every request object received and not refused, in order.
  final List<JsonObject> requests = [];

  /// The text of every frame received, in order.
  final List<String> frames = [];

  /// Number of frames answered with `rate_limited` by the limiter.
  int refusedCount = 0;
  int connectionCount = 0;
  int loginCount = 0;

  int _nextMessageId = 2800;
  final Map<String, int> _conversationIds = {};
  final Map<String, String> _bodies = {};
  int _sequence = 0;
  final List<_Client> _clients = [];
  final Map<String, List<String>> _drops = {};
  final Map<String, List<Failure>> _failures = {};
  final Map<String, List<JsonObject>> _results = {};
  final Map<String, int> _ignoredActions = {};
  List<Object> _framesBeforeResponse = [];
  int _hold = 0;
  List<JsonObject> _held = [];
  final Stopwatch _clock = Stopwatch()..start();
  HttpServer? _server;
  bool _stopped = false;

  // ------------------------------------------------------------------
  // Life cycle
  // ------------------------------------------------------------------

  /// The address of the API.
  String get url => 'wss://$host:$port/ws';

  /// Number of open connections.
  int get clientCount => _clients.length;

  /// Starts the server.
  Future<FakeSmsDock> start() async {
    final server = await HttpServer.bindSecure(
      host,
      port,
      tls.serverContext(),
    );
    _server = server;
    port = server.port;
    server.listen(
      (request) => unawaited(_accept(request)),
      onError: (Object _) {
        // A client that fails the TLS handshake is not a connection.
      },
    );
    return this;
  }

  /// Stops the server; clients see the close code 1001.
  Future<void> stop() async {
    if (_stopped) {
      return;
    }
    _stopped = true;
    final clients = _clients.toList();
    try {
      await Future.wait([
        for (final client in clients)
          client.connection.close(WebSocketStatus.goingAway),
      ]).timeout(const Duration(seconds: 1));
    } on Object {
      // A client that does not answer is dropped.
    }
    for (final client in clients) {
      client.timer?.cancel();
      client.socket.destroy();
    }
    _clients.clear();
    await _server?.close(force: true);
  }

  // ------------------------------------------------------------------
  // Controls for tests
  // ------------------------------------------------------------------

  /// Adds a contact with one number.
  void addContact(String name, String number, [String label = 'Mobile']) {
    contacts.add({
      'name': name,
      'numbers': [
        {'number': number, 'label': label},
      ],
    });
  }

  /// Stores a message without sending an event.
  JsonObject addMessage(
    String address,
    String body, {
    String direction = 'in',
    int? timestamp,
    bool read = true,
    int? sim = 1,
    String? requestId,
  }) {
    final time = timestamp ?? DateTime.now().millisecondsSinceEpoch;
    final incoming = direction == 'in';
    _nextMessageId += 1;
    final message = <String, dynamic>{
      'id': _nextMessageId,
      'conversationId': _conversationId(address),
      'address': address,
      'contact': _contactName(address),
      'direction': direction,
      'body': body,
      'timestamp': time,
      'sentTimestamp': incoming ? time - 2000 : null,
      'read': read,
      'status': incoming ? 'received' : 'sent',
      'sim': sim,
      'requestId': requestId,
    };
    messages.add(message);
    return message;
  }

  /// Lets an SMS arrive: stores it and sends `sms.received`.
  JsonObject receiveSms(String address, String body, {int sim = 1}) {
    final message = addMessage(address, body, read: false, sim: sim);
    emit('sms.received', {'message': message});
    return message;
  }

  /// Sends an event to the subscribed clients.
  void emit(String name, JsonObject data) {
    _sequence += 1;
    final event = {'event': name, 'seq': _sequence, 'data': data};
    for (final client in _clients.toList()) {
      if (client.authenticated && _wants(client, name, data)) {
        _send(client, event);
      }
    }
  }

  /// Behaves like a restart of the app.
  ///
  /// All connections are lost, the sequence numbers begin at 1 again and
  /// [epoch] changes. Messages and the outbox are kept.
  void restart() {
    dropConnections();
    _sequence = 0;
    epoch = max(epoch + 1, DateTime.now().millisecondsSinceEpoch);
  }

  /// Closes all connections: abruptly, or with a close code.
  void dropConnections([int? code, String reason = '']) {
    for (final client in _clients.toList()) {
      if (code == null) {
        _abort(client);
      } else {
        _close(client, code, reason);
      }
    }
  }

  /// Drops the connection at the next request with this action.
  ///
  /// `when: 'before'` drops before the request is carried out,
  /// `when: 'after'` after it was carried out but before the response is
  /// sent. Calling it again queues one more drop.
  void dropOn(String action, [String when = 'after']) {
    if (when != 'before' && when != 'after') {
      throw ArgumentError("when must be 'before' or 'after'");
    }
    _drops.putIfAbsent(action, () => []).add(when);
  }

  /// Answers the next request with this action with an error.
  void failNext(
    String action,
    String code, {
    String message = 'Injected failure.',
    int? closeCode,
    JsonObject extra = const {},
  }) {
    _failures
        .putIfAbsent(action, () => [])
        .add(Failure(code, message, closeCode: closeCode, extra: extra));
  }

  /// Answers the next request with this action with this result.
  void answerNext(String action, JsonObject result) {
    _results.putIfAbsent(action, () => []).add(result);
  }

  /// Does not answer the next requests with this action at all.
  void ignoreNext(String action, [int times = 1]) {
    _ignoredActions[action] = (_ignoredActions[action] ?? 0) + times;
  }

  /// Sends these frames to the client right before the next response.
  ///
  /// A frame is a JSON object or the text of a frame.
  void beforeNextResponse(List<Object> frames) {
    _framesBeforeResponse.addAll(frames);
  }

  /// Holds the next [count] responses and sends them in reverse order.
  void reverseNextResponses([int count = 2]) {
    _hold = count;
  }

  /// Locks logins as after too many wrong passwords.
  void lockLogins(Duration time) {
    lockedUntil = _clock.elapsed + time;
  }

  /// Fills the server with a few contacts and conversations.
  void seedDemoData() {
    final now = DateTime.now().millisecondsSinceEpoch;
    addContact('Anna Example', '+4369912345678');
    addContact('Ben Example', '+4369087654321', 'Work');
    const minute = 60000;
    const rows = [
      ('+4369912345678', 'in', 'Are you coming on Friday?', 300),
      ('+4369912345678', 'out', 'Yes, at eight.', 290),
      ('+4369912345678', 'in', 'Great, see you then.', 280),
      ('+4369087654321', 'out', 'The server is up again.', 200),
      ('+4369087654321', 'in', 'Thanks!', 190),
      ('A1', 'in', 'Your invoice is ready.', 120),
      ('+436641234567', 'in', 'STATUS', 5),
    ];
    for (final (address, direction, body, age) in rows) {
      addMessage(
        address,
        body,
        direction: direction,
        timestamp: now - age * minute,
        read: age > 100,
      );
    }
  }

  // ------------------------------------------------------------------
  // Connections
  // ------------------------------------------------------------------

  Future<void> _accept(HttpRequest request) async {
    final response = request.response;
    final key = request.headers.value('sec-websocket-key');
    if (request.uri.path != '/ws') {
      response.statusCode = HttpStatus.notFound;
      response.write('Not found\n');
      await response.close();
      return;
    }
    if (!WebSocketTransformer.isUpgradeRequest(request) || key == null) {
      response.statusCode = HttpStatus.badRequest;
      await response.close();
      return;
    }
    // The upgrade is done by hand: the socket is needed to drop a
    // connection without a close frame.
    final accept = base64.encode(
      sha1.convert(utf8.encode('$key$_webSocketGuid')).bytes,
    );
    response
      ..statusCode = HttpStatus.switchingProtocols
      ..headers.set(HttpHeaders.upgradeHeader, 'websocket')
      ..headers.set(HttpHeaders.connectionHeader, 'Upgrade')
      ..headers.set('Sec-WebSocket-Accept', accept)
      ..headers.contentLength = 0;
    final socket = await response.detachSocket();
    final connection = WebSocket.fromUpgradedSocket(socket, serverSide: true);
    _handle(_Client(socket, connection));
  }

  void _handle(_Client client) {
    final connection = client.connection;
    if (_stopped) {
      client.socket.destroy();
      return;
    }
    if (_clients.length >= maxClients) {
      connection.listen((_) {}, onError: (Object _) {});
      _close(client, 4409, 'too many clients');
      return;
    }
    _clients.add(client);
    connectionCount += 1;
    connection.pingInterval = const Duration(seconds: 20);
    client.timer = Timer(authTimeout, () {
      if (!client.authenticated) {
        _close(client, 4401, 'not authenticated in time');
      }
    });
    connection.listen(
      (Object? frame) {
        if (client.closing) {
          return;
        }
        if (frame is! String) {
          _close(client, 4400, 'binary frame');
          return;
        }
        if (utf8.encode(frame).length > maxFrameBytes) {
          _close(client, WebSocketStatus.messageTooBig, 'frame too large');
          return;
        }
        _onFrame(client, frame);
      },
      onError: (Object _) {},
      onDone: () => _forget(client),
    );
    _send(client, {
      'event': 'hello',
      'protocolVersion': protocol,
      'authenticationRequired': true,
    });
  }

  void _forget(_Client client) {
    client.timer?.cancel();
    client.connection.pingInterval = null;
    _clients.remove(client);
  }

  /// Ends a connection without a close frame.
  void _abort(_Client client) {
    _forget(client);
    client.socket.destroy();
  }

  void _close(_Client client, int code, String reason) {
    if (client.closing) {
      return;
    }
    client.closing = true;
    // The close frame must reach the client: the socket is destroyed only
    // when the client does not answer it.
    final patience = Timer(const Duration(seconds: 2), client.socket.destroy);
    unawaited(
      client.connection
          .close(code, reason)
          .catchError((Object _) => null)
          .whenComplete(() {
        patience.cancel();
        _forget(client);
      }),
    );
  }

  void _send(_Client client, Object frame) {
    if (client.closing || client.connection.readyState != WebSocket.open) {
      return;
    }
    client.connection.add(frame is String ? frame : jsonEncode(frame));
  }

  void _onFrame(_Client client, String frame) {
    frames.add(frame);
    Object? request;
    try {
      request = jsonDecode(frame);
    } on FormatException {
      request = null;
    }
    if (!_withinRate(client)) {
      // Every frame counts, the login and unreadable ones as well.
      Object? identifier = request is JsonObject ? request['id'] : null;
      if (identifier is! String && identifier is! num) {
        identifier = null;
      }
      refusedCount += 1;
      _send(client, {
        'id': identifier,
        'ok': false,
        'error': {
          'code': 'rate_limited',
          'message': 'Too many requests.',
          'retryAfter': 1,
        },
      });
      client.refused += 1;
      if (client.refused > maxRefused) {
        _close(client, 4429, 'Too many requests.');
      }
      return;
    }
    client.refused = 0;
    if (request is! JsonObject) {
      _respond(client, {
        'id': null,
        'ok': false,
        'error': {
          'code': 'invalid_json',
          'message': 'The frame is not a JSON object.',
        },
      });
      return;
    }
    requests.add(request);
    final identifier = request['id'];
    final action = request['action'];
    final drops = action is String ? _drops[action] : null;
    if (drops != null && drops.isNotEmpty && drops.first == 'before') {
      drops.removeAt(0);
      _abort(client);
      return;
    }

    int? closeCode;
    JsonObject response;
    try {
      final result = _dispatch(client, request);
      if (result == null) {
        return;
      }
      response = {'id': identifier, 'ok': true, 'result': result};
    } on Failure catch (failure) {
      response = {'id': identifier, 'ok': false, 'error': failure.error};
      closeCode = failure.closeCode;
    }

    if (drops != null && drops.isNotEmpty && drops.first == 'after') {
      drops.removeAt(0);
      _abort(client);
      return;
    }
    _respond(client, response);
    if (closeCode != null) {
      final error = response['error'] as JsonObject;
      _close(client, closeCode, error['code'] as String);
    }
  }

  /// Counts a frame; at most [requestsPerSecond] fit in a second.
  bool _withinRate(_Client client) {
    final now = _clock.elapsed;
    final times = client.requestTimes;
    while (
        times.isNotEmpty && now - times.first >= const Duration(seconds: 1)) {
      times.removeFirst();
    }
    if (times.length >= requestsPerSecond) {
      return false;
    }
    times.add(now);
    return true;
  }

  void _respond(_Client client, JsonObject response) {
    final frames = _framesBeforeResponse;
    _framesBeforeResponse = [];
    for (final frame in frames) {
      _send(client, frame);
    }
    if (_hold > 0) {
      _held.add(response);
      if (_held.length < _hold) {
        return;
      }
      final held = _held;
      _held = [];
      _hold = 0;
      for (final item in held.reversed) {
        _send(client, item);
      }
      return;
    }
    _send(client, response);
  }

  // ------------------------------------------------------------------
  // Actions
  // ------------------------------------------------------------------

  JsonObject? _dispatch(_Client client, JsonObject request) {
    final action = request['action'];
    if (action is! String) {
      throw Failure('invalid_request', "The field 'action' is missing.");
    }
    final identifier = request['id'];
    if (identifier != null && identifier is! String && identifier is! num) {
      throw Failure('invalid_request', "'id' must be a string or a number.");
    }
    final ignored = _ignoredActions[action] ?? 0;
    if (ignored > 0) {
      _ignoredActions[action] = ignored - 1;
      return null;
    }
    if (action == 'authenticate') {
      return _authenticate(client, request);
    }
    if (!client.authenticated) {
      throw Failure('not_authenticated', 'Authenticate first.');
    }
    final failures = _failures[action];
    if (failures != null && failures.isNotEmpty) {
      throw failures.removeAt(0);
    }
    final results = _results[action];
    if (results != null && results.isNotEmpty) {
      return results.removeAt(0);
    }
    return switch (action) {
      'status' => _status(),
      'ping' => {'time': DateTime.now().millisecondsSinceEpoch},
      'conversations.list' => _conversationsList(request),
      'messages.list' => _messagesList(request),
      'messages.get' => _findMessage(request),
      'messages.status' => _messagesStatus(request),
      'messages.delete' => _messagesDelete(request),
      'conversations.delete' => _conversationsDelete(request),
      'conversations.markRead' => _conversationsMarkRead(request),
      'contacts.list' => _contactsList(request),
      'messages.send' => _messagesSend(request),
      'subscribe' => _subscribe(client, request),
      'unsubscribe' => _unsubscribe(client, request),
      _ => throw Failure('unknown_action', "Unknown action '$action'."),
    };
  }

  JsonObject _authenticate(_Client client, JsonObject request) {
    loginCount += 1;
    // Assumption, as in the app: the licence is looked at before the
    // password, so a login without licence never counts as wrong.
    if (licenceEnforced && !licensed) {
      throw Failure(
        'licence_required',
        'The API needs the full licence of SMS Dock.',
        closeCode: 4403,
      );
    }
    final remaining = lockedUntil - _clock.elapsed;
    if (remaining > Duration.zero) {
      final seconds = (remaining.inMicroseconds / 1000000).ceil();
      throw Failure(
        'locked_out',
        'Too many attempts. Try again in $seconds seconds.',
        closeCode: 4401,
        extra: {'retryAfter': seconds},
      );
    }
    if (request['password'] != password) {
      failedLogins += 1;
      if (failedLogins >= 5) {
        final seconds = min(30 * pow(2, lockCount).toInt(), 900);
        lockCount += 1;
        failedLogins = 0;
        lockedUntil = _clock.elapsed + Duration(seconds: seconds);
      }
      throw Failure('invalid_password', 'Wrong password.', closeCode: 4401);
    }
    failedLogins = 0;
    lockCount = 0;
    client.authenticated = true;
    return {
      'protocolVersion': protocol,
      'app': {
        'name': 'SMS Dock',
        'version': '1.0.0',
        'protocolVersion': protocol
      },
      'latestSeq': _sequence,
      'epoch': epoch,
    };
  }

  JsonObject _status() => {
        'app': {
          'name': 'SMS Dock',
          'version': '1.0.0',
          'protocolVersion': protocol
        },
        'sims': sims,
        'permissions': {
          'readSms': true,
          'sendSms': true,
          'receiveSms': true,
          'contacts': true,
          'phoneState': true,
          'notifications': true,
        },
        'defaultSmsApp': deleteMode == 'provider',
        'deleteMode': deleteMode,
        'licence': {
          'enforced': licenceEnforced,
          'licensed': licensed,
          'dailyLimit': 10,
          'sentToday': sent.length,
          'remaining': null,
        },
        'popup': true,
      };

  JsonObject _conversationsList(JsonObject request) {
    final query = _optional<String>(request, 'q');
    final unread = _optional<bool>(request, 'unread') ?? false;
    final sim = _optional<int>(request, 'sim');
    var items = _conversations();
    if (query != null && query.isNotEmpty) {
      final needle = query.toLowerCase();
      final matching = {
        for (final message in messages)
          if ((message['body'] as String).toLowerCase().contains(needle))
            message['conversationId'],
      };
      items = [
        for (final item in items)
          if (matching.contains(item['id']) ||
              (item['address'] as String).toLowerCase().contains(needle) ||
              ((item['contact'] as String?) ?? '').toLowerCase().contains(
                    needle,
                  ))
            item,
      ];
    }
    if (unread) {
      items = [
        for (final item in items)
          if ((item['unreadCount'] as int) > 0) item,
      ];
    }
    if (sim != null) {
      items = [
        for (final item in items)
          if (item['sim'] == sim) item,
      ];
    }
    return _page(items, request);
  }

  JsonObject _messagesList(JsonObject request) {
    final address = _optional<String>(request, 'address');
    final conversation = _optional<int>(request, 'conversationId');
    final direction = _optional<String>(request, 'direction') ?? 'all';
    final query = _optional<String>(request, 'q');
    final start = _optional<int>(request, 'from');
    final end = _optional<int>(request, 'to');
    final sim = _optional<int>(request, 'sim');
    final unread = _optional<bool>(request, 'unread') ?? false;
    if (!const ['in', 'out', 'all'].contains(direction)) {
      throw Failure(
        'invalid_request',
        "'direction' must be 'in', 'out' or 'all'.",
      );
    }
    final needle = query?.toLowerCase() ?? '';
    final items = [
      for (final m in messages.toList()..sort((a, b) => -_byTime(a, b)))
        // conversationId wins if address is given too.
        if ((conversation != null
                ? m['conversationId'] == conversation
                : address == null ||
                    samePeer(m['address'] as String, address)) &&
            (direction == 'all' || m['direction'] == direction) &&
            (needle.isEmpty ||
                (m['body'] as String).toLowerCase().contains(needle)) &&
            (start == null || (m['timestamp'] as int) >= start) &&
            (end == null || (m['timestamp'] as int) <= end) &&
            (sim == null || m['sim'] == sim) &&
            (!unread || m['read'] == false))
          m,
    ];
    return _page(items, request);
  }

  JsonObject _findMessage(JsonObject request) {
    final identifier = _required<int>(request, 'messageId');
    for (final message in messages) {
      if (message['id'] == identifier) {
        return message;
      }
    }
    throw Failure('not_found', 'No message with the id $identifier.');
  }

  JsonObject _messagesStatus(JsonObject request) {
    final requestId = _required<String>(request, 'requestId');
    final entry = outbox[requestId];
    if (entry == null) {
      throw Failure('not_found', 'No message with the requestId $requestId.');
    }
    return {...entry};
  }

  JsonObject _messagesDelete(JsonObject request) {
    final message = _findMessage(request);
    messages.remove(message);
    _later(
      () => emit('sms.deleted', {
        'id': message['id'],
        'conversationId': message['conversationId'],
        'address': message['address'],
        'mode': deleteMode,
      }),
    );
    return {'deleted': 1, 'mode': deleteMode};
  }

  JsonObject _conversationsDelete(JsonObject request) {
    final identifier = _required<int>(request, 'conversationId');
    final doomed = [
      for (final m in messages)
        if (m['conversationId'] == identifier) m,
    ];
    if (doomed.isEmpty) {
      throw Failure('not_found', 'This conversation does not exist.');
    }
    final newest = doomed.reduce((a, b) => _byTime(a, b) >= 0 ? a : b);
    messages = [
      for (final m in messages)
        if (m['conversationId'] != identifier) m,
    ];
    _later(
      () => emit('sms.deleted', {
        'id': null,
        'conversationId': identifier,
        'address': newest['address'],
        'mode': deleteMode,
      }),
    );
    return {'deleted': doomed.length, 'mode': deleteMode};
  }

  JsonObject _conversationsMarkRead(JsonObject request) {
    // API.md names no event for this action and no order of the two
    // errors. Assumption, as in the app: no event; the default SMS app
    // is checked first.
    final identifier = _required<int>(request, 'conversationId');
    if (deleteMode != 'provider') {
      throw Failure(
        'not_default_app',
        'Only the default SMS app can mark messages as read.',
      );
    }
    final found = [
      for (final m in messages)
        if (m['conversationId'] == identifier) m,
    ];
    if (found.isEmpty) {
      throw Failure('not_found', 'This conversation does not exist.');
    }
    for (final message in found) {
      message['read'] = true;
    }
    return {'ok': true};
  }

  JsonObject _contactsList(JsonObject request) {
    final query = _optional<String>(request, 'q');
    String name(JsonObject contact) =>
        (contact['name'] as String).toLowerCase();
    var items = _stableSorted(contacts, (a, b) => name(a).compareTo(name(b)));
    if (query != null && query.isNotEmpty) {
      final needle = query.toLowerCase();
      items = [
        for (final contact in items)
          if (name(contact).contains(needle) ||
              (contact['numbers'] as List<dynamic>).any(
                (n) => ((n as JsonObject)['number'] as String).contains(needle),
              ))
            contact,
      ];
    }
    return _page(items, request, byIndex: true);
  }

  JsonObject _messagesSend(JsonObject request) {
    final to = _required<String>(request, 'to');
    final body = _required<String>(request, 'body');
    var sim = _optional<int>(request, 'sim');
    var requestId = _optional<String>(request, 'requestId');
    if (requestId != null && !_requestId.hasMatch(requestId)) {
      throw Failure(
        'invalid_request',
        "'requestId' must be 1 to 64 characters of A-Z a-z 0-9 . _ -.",
      );
    }
    final number = normalizeNumber(to);
    if (!_number.hasMatch(number)) {
      throw Failure('invalid_number', "'to' is not a phone number.");
    }
    if (body.isEmpty) {
      throw Failure('invalid_request', "'body' is empty.");
    }
    final parts = countParts(body);
    if (parts > 10) {
      throw Failure(
        'message_too_long',
        'The message needs $parts SMS parts; at most 10 are allowed.',
      );
    }
    final known = requestId == null ? null : outbox[requestId];
    if (known != null) {
      // A repeat: never sent twice, answered with the current status.
      if (_bodies[known['requestId']] != body ||
          !samePeer(known['to'] as String, number)) {
        throw Failure(
          'invalid_request',
          "'requestId' was already used for another message.",
        );
      }
      return {
        'requestId': known['requestId'],
        'to': known['to'],
        'parts': known['parts'],
        'status': known['status'],
      };
    }
    if (sim == null) {
      sim = sims.firstWhere((s) => s['isDefault'] == true)['subscriptionId']
          as int;
    } else if (sims.every((s) => s['subscriptionId'] != sim)) {
      throw Failure('sim_unavailable', 'No SIM with the subscription id $sim.');
    }
    requestId ??= [
      for (var index = 0; index < 16; index += 1)
        _random.nextInt(256).toRadixString(16).padLeft(2, '0'),
    ].join();
    final entry = <String, dynamic>{
      'requestId': requestId,
      'to': number,
      'parts': parts,
      'partsSent': 0,
      'status': 'queued',
      'errorCode': null,
      'messageId': null,
      'sim': sim,
      'source': 'api',
      'createdAt': DateTime.now().millisecondsSinceEpoch,
    };
    outbox[requestId] = entry;
    _bodies[requestId] = body;
    sent.add({'requestId': requestId, 'to': number, 'body': body, 'sim': sim});
    emit('sms.queued', {
      'requestId': requestId,
      'to': number,
      'parts': parts,
      'sim': sim,
      'source': 'api',
    });
    unawaited(_transmit(entry, body));
    return {
      'requestId': requestId,
      'to': number,
      'parts': parts,
      'status': 'queued',
    };
  }

  Future<void> _transmit(JsonObject entry, String body) async {
    final number = entry['to'] as String;
    if ((entry['parts'] as int) > 1 && !failNumbers.contains(number)) {
      // Some parts are sent, some are left.
      await Future<void>.delayed(sendDelay ~/ 2);
      entry['status'] = 'sending';
      entry['partsSent'] = 1;
      await Future<void>.delayed(sendDelay ~/ 2);
    } else {
      await Future<void>.delayed(sendDelay);
    }
    if (_stopped) {
      return;
    }
    if (failNumbers.contains(number)) {
      // The phone has not stored the message: no id, no conversation.
      entry['status'] = 'failed';
      entry['errorCode'] = 1;
      emit('sms.failed', {
        'requestId': entry['requestId'],
        'id': null,
        'conversationId': null,
        'to': number,
        'errorCode': 1,
        'reason': 'generic_failure',
      });
      return;
    }
    final message = addMessage(
      number,
      body,
      direction: 'out',
      sim: entry['sim'] as int?,
      requestId: entry['requestId'] as String,
    );
    entry['status'] = 'sent';
    entry['partsSent'] = entry['parts'];
    entry['messageId'] = message['id'];
    emit('sms.sent', {
      'requestId': entry['requestId'],
      'conversationId': message['conversationId'],
      'to': number,
      'message': message,
    });
    if (deliveryReports) {
      await Future<void>.delayed(sendDelay);
      if (_stopped) {
        return;
      }
      entry['status'] = 'delivered';
      message['status'] = 'delivered';
      emit('sms.delivered', {
        'requestId': entry['requestId'],
        'id': message['id'],
        'conversationId': message['conversationId'],
        'to': number,
      });
    }
  }

  JsonObject _subscribe(_Client client, JsonObject request) {
    // subscribe adds the names; its address replaces the filter, and
    // without address (or with null) the filter is removed.
    client.events.addAll(_names(request));
    client.address = _optional<String>(request, 'address');
    return _subscription(client);
  }

  JsonObject _unsubscribe(_Client client, JsonObject request) {
    client.events.removeAll(_names(request));
    if (client.events.isEmpty) {
      client.address = null;
    }
    return _subscription(client);
  }

  // ------------------------------------------------------------------
  // Data
  // ------------------------------------------------------------------

  /// Runs after the response of the request at hand was sent.
  void _later(void Function() action) {
    scheduleMicrotask(() {
      if (!_stopped) {
        action();
      }
    });
  }

  int _conversationId(String address) {
    for (final entry in _conversationIds.entries) {
      if (samePeer(entry.key, address)) {
        return entry.value;
      }
    }
    return _conversationIds[address] = 170 + _conversationIds.length;
  }

  String? _contactName(String address) {
    for (final contact in contacts) {
      for (final number in contact['numbers'] as List<dynamic>) {
        if (samePeer((number as JsonObject)['number'] as String, address)) {
          return contact['name'] as String;
        }
      }
    }
    return null;
  }

  List<JsonObject> _conversations() {
    final grouped = <int, List<JsonObject>>{};
    for (final message in messages) {
      grouped
          .putIfAbsent(message['conversationId'] as int, () => [])
          .add(message);
    }
    final items = [
      for (final entry in grouped.entries)
        () {
          final newest = entry.value.reduce(
            (a, b) => _byTime(a, b) >= 0 ? a : b,
          );
          final snippet = (newest['body'] as String).runes.take(160);
          return <String, dynamic>{
            'id': entry.key,
            'address': newest['address'],
            'contact': newest['contact'],
            'snippet': String.fromCharCodes(snippet),
            'timestamp': newest['timestamp'],
            'direction': newest['direction'],
            'messageCount': entry.value.length,
            'unreadCount': entry.value.where((m) => m['read'] == false).length,
            'sim': newest['sim'],
          };
        }(),
    ];
    return items..sort((a, b) => -_byTime(a, b));
  }
}

/// Orders messages and conversations by time, then by id.
int _byTime(JsonObject a, JsonObject b) {
  final order = (a['timestamp'] as int).compareTo(b['timestamp'] as int);
  return order != 0 ? order : (a['id'] as int).compareTo(b['id'] as int);
}

List<T> _stableSorted<T>(List<T> items, int Function(T a, T b) compare) {
  final indexed = items.indexed.toList()
    ..sort((a, b) {
      final order = compare(a.$2, b.$2);
      return order != 0 ? order : a.$1.compareTo(b.$1);
    });
  return [for (final (_, item) in indexed) item];
}

T _required<T>(JsonObject request, String name) {
  final value = _optional<T>(request, name);
  if (value == null) {
    throw Failure('invalid_request', "The field '$name' is missing.");
  }
  return value;
}

T? _optional<T>(JsonObject request, String name) {
  final value = request[name];
  if (value == null) {
    return null;
  }
  if (value is! T) {
    throw Failure('invalid_request', "The field '$name' is malformed.");
  }
  return value;
}

/// Returns the event names of a request, with `*` replaced.
List<String> _names(JsonObject request) {
  final names = request['events'];
  if (names is! List ||
      names.isEmpty ||
      !names.every((name) => name is String)) {
    throw Failure('invalid_request', "'events' is missing.");
  }
  if (names.contains('*')) {
    return eventNames;
  }
  for (final name in names) {
    if (!eventNames.contains(name)) {
      throw Failure('invalid_request', "Unknown event '$name'.");
    }
  }
  return names.cast<String>();
}

JsonObject _subscription(_Client client) => {
      'events': [
        for (final name in eventNames)
          if (client.events.contains(name)) name,
      ],
      'address': client.address,
    };

/// Whether a client subscribed to an event and its filter lets it pass.
///
/// The filter applies to `sms.*` events that name a peer. Events whose
/// number is not known are always delivered.
bool _wants(_Client client, String name, JsonObject data) {
  if (!client.events.contains(name)) {
    return false;
  }
  final filter = client.address;
  if (filter == null || !name.startsWith('sms.')) {
    return true;
  }
  final message = data['message'];
  Object? peer = message is JsonObject ? message['address'] : null;
  if (peer == null || peer == '') {
    peer = data['to'];
  }
  if (peer == null || peer == '') {
    peer = data['address'];
  }
  if (peer is! String) {
    return true;
  }
  return samePeer(peer, filter);
}

JsonObject _page(
  List<JsonObject> items,
  JsonObject request, {
  bool byIndex = false,
}) {
  final limit = request.containsKey('limit') ? request['limit'] : 50;
  if (limit is! int || limit < 1 || limit > 200) {
    throw Failure('invalid_request', "'limit' must be between 1 and 200.");
  }
  final cursor = _optional<String>(request, 'cursor');
  var start = 0;
  if (cursor != null && byIndex) {
    if (!_digitsOnly.hasMatch(cursor)) {
      throw Failure('invalid_request', "'cursor' is invalid.");
    }
    start = int.parse(cursor);
  } else if (cursor != null) {
    final keys = [
      for (final item in items) '${item['timestamp']}:${item['id']}',
    ];
    final found = keys.indexOf(cursor);
    if (found < 0) {
      throw Failure('invalid_request', "'cursor' is invalid.");
    }
    start = found + 1;
  }
  final chunk = items.skip(start).take(limit).toList();
  String? nextCursor;
  if (chunk.isNotEmpty && start + limit < items.length) {
    nextCursor = byIndex
        ? '${start + chunk.length}'
        : '${chunk.last['timestamp']}:${chunk.last['id']}';
  }
  return {'items': chunk, 'nextCursor': nextCursor};
}

/// Runs the fake server until Ctrl+C.
Future<void> main(List<String> arguments) async {
  final options = <String, String>{};
  for (var index = 0; index < arguments.length; index += 1) {
    final name = arguments[index];
    const known = ['--host', '--port', '--password', '--incoming-every'];
    if (name == '-h' || name == '--help' || !known.contains(name)) {
      stdout.writeln(
        'usage: fake_server.dart [--host HOST] [--port PORT] '
        '[--password PASSWORD] [--incoming-every SECONDS]\n\n'
        'Fake SMS Dock server for trying the client without a phone.',
      );
      exitCode = name.startsWith('-h') || name == '--help' ? 0 : 2;
      return;
    }
    index += 1;
    options[name] = index < arguments.length ? arguments[index] : '';
  }
  final password = options['--password'] ?? 'secret123';
  final every = double.tryParse(options['--incoming-every'] ?? '');
  final directory = Directory.systemTemp.createTempSync('smsdock-fake-');
  try {
    final tls = makeCertificate(directory);
    if (tls == null) {
      stdout.writeln('The openssl command is needed to create a certificate.');
      exitCode = 1;
      return;
    }
    final server = FakeSmsDock(
      password: password,
      tls: tls,
      host: options['--host'] ?? '127.0.0.1',
      port: int.tryParse(options['--port'] ?? '') ?? 9443,
      sendDelay: const Duration(milliseconds: 500),
      deliveryReports: true,
    )..seedDemoData();
    await server.start();
    stdout
      ..writeln('URL:         ${server.url}')
      ..writeln('Fingerprint: ${tls.fingerprint}')
      ..writeln('Password:    $password')
      ..writeln('Press Ctrl+C to stop.');
    var number = 0;
    final timer = every == null || every <= 0
        ? null
        : Timer.periodic(Duration(milliseconds: (every * 1000).round()), (_) {
            number += 1;
            server.receiveSms('+436641234567', 'Test message $number');
          });
    await ProcessSignal.sigint.watch().first;
    timer?.cancel();
    stdout.writeln();
    await server.stop();
  } finally {
    directory.deleteSync(recursive: true);
  }
}
