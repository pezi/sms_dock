// --------------------------------------------------------------------------
// Filename: helpers.dart
//
// Description: Helpers of the tests: certificates, a filled fake server and
//   clients connected to it.
// --------------------------------------------------------------------------

/// Helpers of the tests: a fake SMS Dock server and clients connected to
/// it.
///
/// The tests talk to the fake server over real TLS with a throwaway
/// certificate made by the `openssl` command. Without `openssl` (or with
/// `SMSDOCK_TEST_NO_TLS=1`) the tests that need a server are skipped.
library;

import 'dart:async';
import 'dart:io';

import 'package:smsdock/smsdock.dart';
import 'package:smsdock/src/client.dart' show debugMaxFrameBytes, debugSleep;
import 'package:test/test.dart';

import 'fake_server.dart' hide JsonObject, eventNames, maxFrameBytes;

const String password = 'secret123';
const int baseTime = 1790672531000;
const String anna = '+4369912345678';
const String ben = '+4369087654321';
const String url = 'wss://192.168.1.50:9443/ws';
final String fingerprint = List.filled(32, 'AB').join(':');

/// Certificate of the fake server, or `null` without openssl.
TlsFiles? tlsFiles;

/// A second certificate that the client does not pin.
TlsFiles? otherTlsFiles;

/// The reason to skip the tests that need a server, or `null`.
String? get needTls => tlsFiles == null || otherTlsFiles == null
    ? 'needs the openssl command to create a throwaway certificate'
    : null;

/// Creates the certificates. Call it first in `main` of a test file.
void prepareTls() {
  final directory = Directory.systemTemp.createTempSync('smsdock-test-');
  // SMSDOCK_TEST_NO_TLS=1 behaves like a computer without openssl.
  if (!Platform.environment.containsKey('SMSDOCK_TEST_NO_TLS')) {
    tlsFiles = makeCertificate(directory, 'server');
    otherTlsFiles = makeCertificate(directory, 'other');
  }
  tearDownAll(() => directory.deleteSync(recursive: true));
  final sleep = debugSleep;
  tearDown(() {
    debugSleep = sleep;
    debugMaxFrameBytes = maxFrameBytes;
  });
}

Duration seconds(num value) =>
    Duration(microseconds: (value * Duration.microsecondsPerSecond).round());

/// Stores 29 messages in three conversations.
void fill(FakeSmsDock server) {
  server
    ..addContact('Anna Example', anna)
    ..addContact('Ben Example', ben, 'Work');
  for (var index = 0; index < 25; index += 1) {
    server.addMessage(
      anna,
      'Anna $index',
      direction: index.isEven ? 'in' : 'out',
      timestamp: baseTime + index * 1000,
      read: index < 22,
    );
  }
  server
    ..addMessage(
      ben,
      'Your invoice is attached',
      direction: 'out',
      timestamp: baseTime - 5000,
      sim: 2,
    )
    ..addMessage(
      ben,
      'Thanks for the invoice',
      timestamp: baseTime - 4000,
      sim: 2,
    )
    ..addMessage(ben, 'See you', timestamp: baseTime - 3000, sim: 2)
    ..addMessage(
      'A1',
      'Your bill is ready',
      timestamp: baseTime - 9000,
      read: false,
    );
}

/// Starts a fake server with the data of [fill]; it stops with the test.
Future<FakeSmsDock> startServer({
  TlsFiles? tls,
  int port = 0,
  Duration authTimeout = const Duration(seconds: 10),
  int maxClients = 8,
  Duration sendDelay = const Duration(milliseconds: 20),
  bool deliveryReports = false,
  int requestsPerSecond = 20,
  int maxRefused = 20,
}) async {
  final server = FakeSmsDock(
    tls: tls ?? tlsFiles!,
    password: password,
    port: port,
    authTimeout: authTimeout,
    maxClients: maxClients,
    sendDelay: sendDelay,
    deliveryReports: deliveryReports,
    requestsPerSecond: requestsPerSecond,
    maxRefused: maxRefused,
  );
  fill(server);
  addTearDown(server.stop);
  return server.start();
}

/// Makes a client for a server; it is closed with the test.
SmsDockClient makeClient(
  FakeSmsDock server, {
  String secret = password,
  String? pin,
  bool insecure = false,
  Duration timeout = const Duration(seconds: 5),
  Duration connectTimeout = const Duration(seconds: 10),
  int? reconnectAttempts = 5,
  Duration reconnectDelay = const Duration(milliseconds: 10),
  Duration maxReconnectDelay = const Duration(seconds: 10),
  int eventQueueSize = 1000,
  int rateLimitRetries = 2,
  ReconnectHook? onReconnect,
}) {
  final client = SmsDockClient(
    server.url,
    password: secret,
    fingerprint: insecure ? null : pin ?? server.tls.fingerprint,
    insecure: insecure,
    timeout: timeout,
    connectTimeout: connectTimeout,
    reconnectAttempts: reconnectAttempts,
    reconnectDelay: reconnectDelay,
    maxReconnectDelay: maxReconnectDelay,
    eventQueueSize: eventQueueSize,
    rateLimitRetries: rateLimitRetries,
    onReconnect: onReconnect,
    environment: const {},
  );
  addTearDown(client.close);
  return client;
}

/// Polls until the condition holds; returns whether it did.
Future<bool> waitUntil(
  bool Function() condition, [
  Duration timeout = const Duration(seconds: 3),
]) async {
  final clock = Stopwatch()..start();
  while (clock.elapsed < timeout) {
    if (condition()) {
      return true;
    }
    await Future<void>.delayed(const Duration(milliseconds: 10));
  }
  return condition();
}

/// Returns the actions the server received, in order.
List<Object?> actions(FakeSmsDock server) => [
      for (final request in server.requests) request['action'],
    ];

/// How often the server received an action.
int countOf(FakeSmsDock server, String action) =>
    actions(server).where((name) => name == action).length;

/// Returns the requests with this action, in order.
List<JsonObject> requestsOf(FakeSmsDock server, String action) => [
      for (final request in server.requests)
        if (request['action'] == action) request,
    ];

/// Returns the newest request with this action.
JsonObject lastRequest(FakeSmsDock server, String action) =>
    requestsOf(server, action).last;

/// Returns an `sms.received` event as the server would send it.
JsonObject eventFrame(int sequence, [String body = 'Hi']) => {
      'event': 'sms.received',
      'seq': sequence,
      'data': {
        'message': {
          'id': 9000 + sequence,
          'conversationId': 1,
          'address': anna,
          'contact': null,
          'direction': 'in',
          'body': body,
          'timestamp': baseTime,
          'sentTimestamp': baseTime,
          'read': false,
          'status': 'received',
          'sim': 1,
          'requestId': null,
        },
      },
    };

/// Awaits a future that must fail with an error of the type [T].
Future<T> failure<T extends Object>(Future<Object?> future) async {
  try {
    await future;
  } on T catch (error) {
    return error;
  }
  fail('Expected an error of the type $T');
}

/// Replaces the pauses of the client by a list of their lengths.
List<Duration> recordPauses() {
  final pauses = <Duration>[];
  debugSleep = (pause) async => pauses.add(pause);
  return pauses;
}
