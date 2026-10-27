// --------------------------------------------------------------------------
// Filename: client_test.dart
//
// Description: Tests of SmsDockClient against the fake server.
// --------------------------------------------------------------------------

/// Tests of `SmsDockClient` against the fake server.
library;

import 'dart:async';
import 'dart:convert';
import 'dart:io';

import 'package:crypto/crypto.dart';
import 'package:smsdock/smsdock.dart';
import 'package:smsdock/src/client.dart' show debugMaxFrameBytes;
import 'package:smsdock/src/output.dart';
import 'package:test/test.dart';

import 'fake_server.dart' hide JsonObject, eventNames, maxFrameBytes;
import 'helpers.dart';

/// A server and a client that is connected and logged in.
Future<(FakeSmsDock, SmsDockClient)> pair({
  Duration sendDelay = const Duration(milliseconds: 20),
  bool deliveryReports = false,
  int eventQueueSize = 1000,
  ReconnectHook? onReconnect,
}) async {
  final server = await startServer(
    sendDelay: sendDelay,
    deliveryReports: deliveryReports,
  );
  final client = makeClient(
    server,
    eventQueueSize: eventQueueSize,
    onReconnect: onReconnect,
  );
  await client.connect();
  return (server, client);
}

List<Object?> bodies(Iterable<Map<String, dynamic>> messages) => [
  for (final message in messages) message['body'],
];

List<Object?> addresses(Iterable<Map<String, dynamic>> items) => [
  for (final item in items) item['address'],
];

List<Object?> eventBodies(Iterable<SmsEvent> events) => [
  for (final event in events) event.message?.body,
];

Future<List<SmsEvent>> drain(SmsDockClient client, num time) =>
    client.events(timeout: seconds(time)).toList();

Matcher throwsArgument([Object? message]) => throwsA(
  isA<ArgumentError>().having(
    (error) => '${error.message}',
    'message',
    message ?? anything,
  ),
);

void main() {
  prepareTls();

  // --------------------------------------------------------------------
  // Construction
  // --------------------------------------------------------------------

  group('construction', () {
    for (final address in [
      'ws://192.168.1.50:9443/ws',
      'http://192.168.1.50:9443/ws',
      'https://192.168.1.50:9443/ws',
      '192.168.1.50:9443',
      'wss:///ws',
      '',
    ]) {
      test('refuses everything but wss: "$address"', () {
        expect(
          () => SmsDockClient(
            address,
            password: password,
            fingerprint: fingerprint,
          ),
          throwsArgument(contains('wss://')),
        );
      });
    }

    test('needs a password', () {
      expect(
        () => SmsDockClient(url, fingerprint: fingerprint, environment: {}),
        throwsArgument(contains('SMSDOCK_PASSWORD')),
      );
      expect(
        () => SmsDockClient(
          url,
          password: '',
          fingerprint: fingerprint,
          environment: {},
        ),
        throwsArgument(contains('SMSDOCK_PASSWORD')),
      );
    });

    test('needs a fingerprint unless insecure', () {
      expect(
        () => SmsDockClient(url, password: password, environment: {}),
        throwsArgument(contains('SMSDOCK_FINGERPRINT')),
      );
      final client = SmsDockClient(
        url,
        password: password,
        insecure: true,
        environment: {},
      );
      expect(client.insecure, isTrue);
    });

    test('refuses a malformed fingerprint of the environment', () {
      expect(
        () => SmsDockClient(
          url,
          password: password,
          environment: {'SMSDOCK_FINGERPRINT': 'AB:CD'},
        ),
        throwsArgument(contains('SHA-256')),
      );
    });

    for (final pin in [
      '',
      'AB:CD',
      'zz' * 32,
      'AB' * 31,
      'AB' * 33,
      'AB' * 20,
    ]) {
      test('refuses the malformed fingerprint "$pin"', () {
        expect(
          () => SmsDockClient(url, password: password, fingerprint: pin),
          throwsArgument(contains('SHA-256')),
        );
      });
    }

    test('refuses impossible options', () {
      SmsDockClient make({
        Duration timeout = const Duration(seconds: 30),
        Duration connectTimeout = const Duration(seconds: 10),
        int? reconnectAttempts = 5,
        Duration reconnectDelay = const Duration(seconds: 1),
        int eventQueueSize = 1000,
        int rateLimitRetries = 2,
      }) => SmsDockClient(
        url,
        password: password,
        fingerprint: fingerprint,
        timeout: timeout,
        connectTimeout: connectTimeout,
        reconnectAttempts: reconnectAttempts,
        reconnectDelay: reconnectDelay,
        eventQueueSize: eventQueueSize,
        rateLimitRetries: rateLimitRetries,
      );
      expect(() => make(timeout: Duration.zero), throwsArgument());
      expect(() => make(connectTimeout: seconds(-1)), throwsArgument());
      expect(() => make(reconnectAttempts: -1), throwsArgument());
      expect(() => make(reconnectDelay: seconds(-1)), throwsArgument());
      expect(() => make(eventQueueSize: 0), throwsArgument());
      expect(() => make(rateLimitRetries: -1), throwsArgument());
      expect(make(reconnectAttempts: null).reconnectAttempts, isNull);
    });

    test('fingerprint forms', () {
      final digits = '0123456789abcdef' * 4;
      final colons = [
        for (var i = 0; i < 64; i += 2) digits.substring(i, i + 2),
      ].join(':');
      expect(normalizeFingerprint(digits.toUpperCase()), digits);
      expect(normalizeFingerprint(colons), digits);
      expect(normalizeFingerprint(' ${colons.toUpperCase()}\n'), digits);
      expect(
        normalizeFingerprint('sha256 Fingerprint=${colons.toUpperCase()}'),
        digits,
      );
      expect(formatFingerprint(digits), colons.toUpperCase());
      expect(
        certificateFingerprint(utf8.encode('certificate')),
        formatFingerprint('${sha256.convert(utf8.encode('certificate'))}'),
      );
    });

    test('toString does not show the password', () {
      final client = SmsDockClient(
        url,
        password: 'very-secret',
        fingerprint: fingerprint,
      );
      expect('$client', isNot(contains('very-secret')));
      expect('$client', contains(url));
    });

    test('all errors share a base class', () {
      expect(ApiError('a', 'b'), isA<SmsDockError>());
      expect(AuthenticationError('a', 'b'), isA<ApiError>());
      expect(LicenceRequiredError('a', 'b'), isA<ApiError>());
      expect(ProtocolError('a'), isA<SmsDockError>());
      expect(ConnectionInterrupted('a'), isA<SmsDockError>());
      expect(CertificateMismatchError('a', 'b'), isA<SmsDockError>());
      expect(ApiError('a', 'b'), isA<Exception>());
      expect(
        '${ApiError('not_found', 'No message.')}',
        'not_found: No message.',
      );
    });
  });

  // --------------------------------------------------------------------
  // Handshake
  // --------------------------------------------------------------------

  group('handshake', skip: needTls, () {
    test('logs in', () async {
      final (server, client) = await pair();
      expect(client.connected, isTrue);
      expect(client.serverInfo, {
        'name': 'SMS Dock',
        'version': '1.0.0',
        'protocolVersion': 1,
      });
      expect(client.serverInfo?.version, '1.0.0');
      expect(client.connectionCount, 1);
      expect(actions(server), ['authenticate']);
      expect(server.requests[0]['password'], password);
      expect(server.loginCount, 1);
    });

    test('reads password and fingerprint from the environment', () async {
      final server = await startServer();
      final client = SmsDockClient(
        server.url,
        timeout: seconds(5),
        environment: {
          'SMSDOCK_PASSWORD': password,
          'SMSDOCK_FINGERPRINT': server.tls.fingerprint.toLowerCase(),
        },
      );
      addTearDown(client.close);
      expect((await client.ping()).time, greaterThan(0));
      expect(server.requests[0]['password'], password);
    });

    test('the first request connects; close closes', () async {
      final server = await startServer();
      final client = makeClient(server);
      expect(client.connected, isFalse);
      expect((await client.ping()).time, greaterThan(0));
      expect(server.clientCount, 1);
      await client.close();
      expect(client.connected, isFalse);
      expect(await waitUntil(() => server.clientCount == 0), isTrue);
    });

    test('wrong password', () async {
      final server = await startServer();
      final client = makeClient(server, secret: 'wrong-password');
      final error = await failure<AuthenticationError>(client.connect());
      expect(error.code, 'invalid_password');
      expect(error, isA<ApiError>());
      expect('$error', isNot(contains('wrong-password')));
      expect(client.connected, isFalse);
      // A wrong password counts towards the lock: it must not be repeated.
      expect(server.loginCount, 1);
    });

    test('wrong password is not retried by requests', () async {
      final server = await startServer();
      final client = makeClient(server, secret: 'wrong-password');
      await failure<AuthenticationError>(client.status());
      expect(server.loginCount, 1);
    });

    test('locked out', () async {
      final server = await startServer();
      server.lockLogins(seconds(30));
      final client = makeClient(server);
      final error = await failure<AuthenticationError>(client.connect());
      expect(error.code, 'locked_out');
      expect(error.retryAfter, inInclusiveRange(1, 30));
      expect(error.details, {'retryAfter': error.retryAfter});
      expect(server.loginCount, 1);
    });

    test('licence required', () async {
      final server = await startServer();
      server.licenceEnforced = true;
      final client = makeClient(server);
      final error = await failure<LicenceRequiredError>(client.connect());
      expect(error.code, 'licence_required');
      expect(error, isA<ApiError>());
    });

    test('refuses other protocol versions', () async {
      final server = await startServer();
      server.protocol = 2;
      final client = makeClient(server);
      final error = await failure<ProtocolError>(client.connect());
      expect('$error', contains('version 2'));
      // The password is not sent to a server of another protocol version.
      expect(server.requests, isEmpty);
    });

    test('close code 4401 while logging in', () async {
      final server = await startServer(authTimeout: seconds(0.2));
      server.ignoreNext('authenticate');
      final client = makeClient(server);
      final error = await failure<AuthenticationError>(client.connect());
      expect(error.code, 'not_authenticated');
      expect(error.details['closeCode'], 4401);
      expect(client.connectionCount, 1);
    });

    test('too many clients', () async {
      final server = await startServer(maxClients: 1);
      await makeClient(server).connect();
      final second = makeClient(server);
      final error = await failure<ConnectionInterrupted>(second.connect());
      expect(error.closeCode, 4409);
    });

    test('nothing listens', () async {
      final server = await startServer();
      final client = makeClient(server, connectTimeout: seconds(2));
      await server.stop();
      final error = await failure<ConnectionInterrupted>(client.connect());
      expect('$error', contains('Cannot connect'));
      expect(error.cause, isA<SocketException>());
    });
  });

  // --------------------------------------------------------------------
  // Certificate pinning over real TLS
  // --------------------------------------------------------------------

  group('certificate', skip: needTls, () {
    test('pins the certificate', () async {
      final server = await startServer();
      final pin = server.tls.fingerprint;
      for (final form in [
        pin,
        pin.toLowerCase(),
        pin.replaceAll(':', ''),
        pin.replaceAll(':', '').toLowerCase(),
        'sha256 Fingerprint=$pin',
      ]) {
        final client = makeClient(server, pin: form);
        expect((await client.ping()).time, greaterThan(0));
      }
    });

    test('the fingerprint is the SHA-256 of the DER certificate', () {
      final pem = File(tlsFiles!.certificate).readAsStringSync();
      final der = base64.decode(
        pem.split('\n').where((line) => !line.startsWith('-----')).join(),
      );
      expect(certificateFingerprint(der), tlsFiles!.fingerprint);
    });

    test('certificate mismatch', () async {
      final server = await startServer();
      final other = otherTlsFiles!.fingerprint;
      final client = makeClient(server, pin: other);
      final error = await failure<CertificateMismatchError>(client.connect());
      expect(error.expected, other);
      expect(error.actual, server.tls.fingerprint);
      expect('$error', contains(other));
      expect('$error', contains(server.tls.fingerprint));
      expect(client.connected, isFalse);
      await Future<void>.delayed(seconds(0.1));
      // Nothing reached the server: no WebSocket, no password.
      expect(server.connectionCount, 0);
      expect(server.requests, isEmpty);
      expect(server.frames, isEmpty);
    });

    test('certificate mismatch is not retried', () async {
      final server = await startServer();
      final client = makeClient(server, pin: otherTlsFiles!.fingerprint);
      await failure<CertificateMismatchError>(client.status());
      expect(client.connectionCount, 0);
      expect(server.connectionCount, 0);
    });

    test('new certificate after reconnect', () async {
      final first = await startServer();
      final client = makeClient(first);
      expect((await client.ping()).time, greaterThan(0));
      await first.stop();
      final second = await startServer(tls: otherTlsFiles, port: first.port);
      await failure<CertificateMismatchError>(client.status());
      expect(second.requests, isEmpty);
      expect(second.connectionCount, 0);
    });

    test('fetch fingerprint', () async {
      final server = await startServer();
      expect(
        await fetchFingerprint('127.0.0.1', port: server.port),
        server.tls.fingerprint,
      );
      await Future<void>.delayed(seconds(0.1));
      expect(server.requests, isEmpty);
      expect(server.connectionCount, 0);
    });

    test('insecure skips the check and warns', () async {
      final server = await startServer();
      final errors = StringBuffer();
      await runZoned(() async {
        final client = makeClient(server, insecure: true);
        expect((await client.ping()).time, greaterThan(0));
        server.dropConnections();
        expect((await client.ping()).time, greaterThan(0));
      }, zoneValues: {errorSinkKey: errors});
      expect('WARNING'.allMatches('$errors').length, 1);
      expect('$errors', contains('not checked'));
    });
  });

  // --------------------------------------------------------------------
  // Requests
  // --------------------------------------------------------------------

  group('requests', skip: needTls, () {
    test('status', () async {
      final (_, client) = await pair();
      final status = await client.status();
      expect(status.app, {
        'name': 'SMS Dock',
        'version': '1.0.0',
        'protocolVersion': 1,
      });
      expect(status.deleteMode, 'hidden');
      expect(status.sims[0].subscriptionId, 1);
      expect(status.permissions['sendSms'], isTrue);
      expect(status.licence.remaining, isNull);
      expect(status['deleteMode'], 'hidden');
    });

    test('ping', () async {
      final (_, client) = await pair();
      final now = DateTime.now().millisecondsSinceEpoch;
      expect(((await client.ping()).time - now).abs(), lessThan(60000));
    });

    test('conversations', () async {
      final (server, client) = await pair();
      final page = await client.conversations();
      expect(addresses(page.items), [anna, ben, 'A1']);
      expect(page.nextCursor, isNull);
      final first = page.items[0];
      expect(first.contact, 'Anna Example');
      expect(first.messageCount, 25);
      expect(first.unreadCount, 3);
      expect(first.snippet, 'Anna 24');
      expect(lastRequest(server, 'conversations.list'), {
        'id': 'dart-2',
        'action': 'conversations.list',
        'limit': 50,
      });
    });

    test('conversations filters', () async {
      final (server, client) = await pair();
      expect(addresses((await client.conversations(q: 'invoice')).items), [
        ben,
      ]);
      expect(addresses((await client.conversations(q: 'anna')).items), [anna]);
      final unread = await client.conversations(unread: true);
      expect(addresses(unread.items), [anna, 'A1']);
      expect(addresses((await client.conversations(sim: 2)).items), [ben]);
      final request = lastRequest(server, 'conversations.list');
      expect(request['sim'], 2);
      expect(request.containsKey('unread'), isFalse);
      expect(request.containsKey('q'), isFalse);
    });

    test('conversations paging', () async {
      final (_, client) = await pair();
      final first = await client.conversations(limit: 2);
      expect(first.items, hasLength(2));
      expect(first.nextCursor, isNotNull);
      final second = await client.conversations(
        limit: 2,
        cursor: first.nextCursor,
      );
      expect(addresses(second.items), ['A1']);
      expect(second.nextCursor, isNull);
      final all = await client.iterConversations(pageSize: 1).toList();
      expect(addresses(all), [anna, ben, 'A1']);
    });

    test('last ten incoming messages', () async {
      final (server, client) = await pair();
      final page = await client.messages(direction: 'in', limit: 10);
      expect(bodies(page.items), [
        for (var index = 24; index > 4; index -= 2) 'Anna $index',
      ]);
      expect(page.items.every((m) => m.direction == 'in'), isTrue);
      expect(page.nextCursor, isNotNull);
      expect(lastRequest(server, 'messages.list'), {
        'id': 'dart-2',
        'action': 'messages.list',
        'direction': 'in',
        'limit': 10,
      });
    });

    test('messages of a number', () async {
      final (_, client) = await pair();
      const cases = {
        ben: 3,
        '+43 690 876-54321': 3,
        '0043 690 87654321': 3,
        '0690 / 876 54 321': 3,
        '(0690) 876.54.321': 3,
        '87654321': 3,
        '654321': 0,
        '+49 690 87654321': 0,
        'a1': 1,
        'A1': 1,
        'A1 Telekom': 0,
      };
      for (final MapEntry(key: address, value: count) in cases.entries) {
        final page = await client.messages(address: address);
        expect(page.items, hasLength(count), reason: address);
      }
    });

    test('conversation wins over address', () async {
      final (server, client) = await pair();
      final id = (await client.conversations(q: 'invoice')).items[0].id;
      final page = await client.messages(conversationId: id, address: anna);
      expect(addresses(page.items).toSet(), {ben});
      final request = lastRequest(server, 'messages.list');
      expect(request['conversationId'], id);
      expect(request['address'], anna);
    });

    test('messages filters', () async {
      final (server, client) = await pair();
      expect((await client.messages(address: ben)).items, hasLength(3));
      final id = (await client.conversations(q: 'invoice')).items[0].id;
      final byConversation = await client.messages(conversationId: id);
      expect(addresses(byConversation.items).toSet(), {ben});
      final outgoing = await client.messages(direction: 'out', limit: 200);
      expect(outgoing.items, hasLength(13));
      expect(bodies((await client.messages(q: 'INVOICE')).items), [
        'Thanks for the invoice',
        'Your invoice is attached',
      ]);
      expect((await client.messages(sim: 2)).items, hasLength(3));
      expect(bodies((await client.messages(unread: true)).items), [
        'Anna 24',
        'Anna 23',
        'Anna 22',
        'Your bill is ready',
      ]);
      final combined = await client.messages(
        address: anna,
        direction: 'in',
        q: 'Anna 1',
        limit: 3,
      );
      expect(bodies(combined.items), ['Anna 18', 'Anna 16', 'Anna 14']);
      expect(lastRequest(server, 'messages.list'), {
        'id': 'dart-9',
        'action': 'messages.list',
        'address': anna,
        'direction': 'in',
        'q': 'Anna 1',
        'limit': 3,
      });
    });

    test('messages time range', () async {
      final (server, client) = await pair();
      var page = await client.messages(
        since: baseTime + 3000,
        until: baseTime + 5000,
      );
      expect(bodies(page.items), ['Anna 5', 'Anna 4', 'Anna 3']);
      final request = lastRequest(server, 'messages.list');
      expect(request['from'], baseTime + 3000);
      expect(request['to'], baseTime + 5000);

      final moment = DateTime.fromMillisecondsSinceEpoch(
        baseTime + 23000,
        isUtc: true,
      );
      page = await client.messages(since: moment);
      expect(bodies(page.items), ['Anna 24', 'Anna 23']);
      expect(lastRequest(server, 'messages.list')['from'], baseTime + 23000);
    });

    test('messages paging', () async {
      final (_, client) = await pair();
      final first = await client.messages(address: anna, limit: 10);
      final second = await client.messages(
        address: anna,
        limit: 10,
        cursor: first.nextCursor,
      );
      final third = await client.messages(
        address: anna,
        limit: 10,
        cursor: second.nextCursor,
      );
      expect(bodies([...first.items, ...second.items, ...third.items]), [
        for (var index = 24; index >= 0; index -= 1) 'Anna $index',
      ]);
      expect(third.nextCursor, isNull);
    });

    test('iterMessages follows the cursor', () async {
      final (server, client) = await pair();
      final messages = await client
          .iterMessages(address: anna, pageSize: 10)
          .toList();
      expect(bodies(messages), [
        for (var index = 24; index >= 0; index -= 1) 'Anna $index',
      ]);
      final requests = requestsOf(server, 'messages.list');
      expect(
        [for (final request in requests) request['cursor']],
        [
          null,
          '${baseTime + 15000}:${messages[9].id}',
          '${baseTime + 5000}:${messages[19].id}',
        ],
      );
      expect(requests.every((request) => request['limit'] == 10), isTrue);
    });

    test('iterMessages is lazy and bounded', () async {
      final (server, client) = await pair();
      final stream = client.iterMessages(pageSize: 10, maxItems: 12);
      await Future<void>.delayed(seconds(0.05));
      expect(countOf(server, 'messages.list'), 0);
      expect(await stream.toList(), hasLength(12));
      final requests = requestsOf(server, 'messages.list');
      expect([for (final request in requests) request['limit']], [10, 2]);
      expect(await client.iterMessages(maxItems: 0).toList(), isEmpty);
      expect(await client.iterMessages(pageSize: 200).toList(), hasLength(29));
    });

    test('iterMessages stops asking when the reader stops', () async {
      final (server, client) = await pair();
      final first = await client.iterMessages(pageSize: 5).take(3).toList();
      expect(first, hasLength(3));
      expect(countOf(server, 'messages.list'), 1);
    });

    test('iterMessages refuses a cursor that does not move', () async {
      final (server, client) = await pair();
      final page = {
        'items': [
          {'id': 1},
        ],
        'nextCursor': 'again',
      };
      server
        ..answerNext('messages.list', page)
        ..answerNext('messages.list', page);
      final error = await failure<ProtocolError>(
        client.iterMessages().toList(),
      );
      expect('$error', contains('cursor'));
      expect(countOf(server, 'messages.list'), 2);
    });

    test('iterMessages refuses a page without items', () async {
      final (server, client) = await pair();
      server.answerNext('messages.list', {'nextCursor': null});
      final error = await failure<ProtocolError>(
        client.iterMessages().toList(),
      );
      expect('$error', contains('items'));
    });

    test('pages may be shorter than the limit', () async {
      final (server, client) = await pair();
      server
        ..answerNext('messages.list', {
          'items': [
            {'id': 1},
          ],
          'nextCursor': '1:1',
        })
        ..answerNext('messages.list', {
          'items': <Object>[],
          'nextCursor': '1:0',
        })
        ..answerNext('messages.list', {
          'items': [
            {'id': 2},
          ],
          'nextCursor': null,
        });
      expect(await client.iterMessages(pageSize: 50).toList(), [
        {'id': 1},
        {'id': 2},
      ]);
    });

    test('messages refuses wrong arguments', () async {
      final (server, client) = await pair();
      expect(client.messages(direction: 'sideways'), throwsArgument());
      expect(client.messages(limit: 0), throwsArgument());
      expect(client.messages(limit: 201), throwsArgument());
      expect(client.messages(since: 'yesterday'), throwsArgument());
      expect(client.messages(until: true), throwsArgument());
      await Future<void>.delayed(seconds(0.05));
      expect(actions(server), ['authenticate']);
    });

    test('iterators check their arguments at once', () async {
      final (_, client) = await pair();
      expect(() => client.iterMessages(pageSize: 500), throwsArgument());
      expect(() => client.iterMessages(maxItems: -1), throwsArgument());
      expect(
        () => client.iterMessages(direction: 'sideways'),
        throwsArgument(),
      );
      expect(() => client.iterContacts(pageSize: 0), throwsArgument());
      expect(() => client.iterConversations(maxItems: -1), throwsArgument());
    });

    test('get message', () async {
      final (server, client) = await pair();
      final newest = (await client.messages(limit: 1)).items[0];
      expect(await client.getMessage(newest.id!), newest);
      expect(newest.keys.toSet(), {
        'id',
        'conversationId',
        'address',
        'contact',
        'direction',
        'body',
        'timestamp',
        'sentTimestamp',
        'read',
        'status',
        'sim',
        'requestId',
      });
      expect(lastRequest(server, 'messages.get'), {
        'id': 'dart-3',
        'action': 'messages.get',
        'messageId': newest.id,
      });
    });

    test('get message not found', () async {
      final (_, client) = await pair();
      final error = await failure<ApiError>(client.getMessage(1));
      expect(error.code, 'not_found');
      expect('$error', contains('not_found'));
      expect(error, isNot(isA<AuthenticationError>()));
      expect((await client.ping()).time, greaterThan(0));
    });

    test('send', () async {
      final (server, client) = await pair();
      final result = await client.send(
        '+43 690 876-54321',
        'All systems running.',
      );
      expect(result.to, ben);
      expect(result.parts, 1);
      expect(result.status, 'queued');
      final requestId = result.requestId;
      expect(requestId, matches(RegExp(r'^[0-9a-f]{32}$')));
      expect(lastRequest(server, 'messages.send'), {
        'id': 'dart-2',
        'action': 'messages.send',
        'to': '+43 690 876-54321',
        'body': 'All systems running.',
        'requestId': requestId,
      });
      expect(server.sent, [
        {
          'requestId': requestId,
          'to': ben,
          'body': 'All systems running.',
          'sim': 1,
        },
      ]);
    });

    test('send creates a new request id each time', () async {
      final (server, client) = await pair();
      final first = await client.send(ben, 'one');
      final second = await client.send(ben, 'one');
      expect(first.requestId, isNot(second.requestId));
      expect(server.sent, hasLength(2));
    });

    test('send with own request id and sim', () async {
      final (server, client) = await pair();
      const id = 'order-42.confirm_1';
      final result = await client.send(ben, 'Grüße', sim: 2, requestId: id);
      expect(result.requestId, id);
      expect(server.sent[0]['sim'], 2);
      expect(server.sent[0]['body'], 'Grüße');
      expect(result.status, 'queued');
      final entry = await client.waitForResult(id, timeout: seconds(5));
      expect(entry.status, 'sent');
      // A repeat is not sent again; it tells the status of today.
      final again = await client.send(ben, 'Grüße', sim: 2, requestId: id);
      expect(again, {...result, 'status': 'sent'});
      expect(server.sent, hasLength(1));
    });

    test('repeat with the number written differently', () async {
      final (server, client) = await pair();
      final first = await client.send(
        '+43 690 876 54 321',
        'Hello',
        requestId: 'r-1',
      );
      expect(first.to, ben);
      for (final number in ['0043 (690) 876-54321', '0690/876.54.321', ben]) {
        final again = await client.send(number, 'Hello', requestId: 'r-1');
        expect(again.to, ben);
      }
      expect(server.sent, hasLength(1));
    });

    for (final (to, body) in [(ben, 'Another text'), (anna, 'Hello')]) {
      test('request id of another message: $body', () async {
        final (server, client) = await pair();
        await client.send(ben, 'Hello', requestId: 'r-1');
        final error = await failure<ApiError>(
          client.send(to, body, requestId: 'r-1'),
        );
        expect(error.code, 'invalid_request');
        expect(server.sent, hasLength(1));
      });
    }

    test('repeat after a failure does not send again', () async {
      final (server, client) = await pair();
      const number = '+43123456789';
      server.failNumbers.add(number);
      final first = await client.send(number, 'Hello', requestId: 'r-1');
      expect(first.status, 'queued');
      final entry = await client.waitForResult('r-1', timeout: seconds(5));
      expect(entry.status, 'failed');
      server.failNumbers.clear();
      final again = await client.send(number, 'Hello', requestId: 'r-1');
      expect(again, {...first, 'status': 'failed'});
      expect(server.sent, hasLength(1));
      // A new attempt needs a new request id.
      final retry = await client.send(number, 'Hello', requestId: 'r-2');
      final outcome = await client.waitForResult(
        retry.requestId,
        timeout: seconds(5),
      );
      expect(outcome.status, 'sent');
      expect(server.sent, hasLength(2));
    });

    test('sendGroup', () async {
      final (server, client) = await pair();
      final result = await client.sendGroup(
        ['+43 690 876-54321', anna, ben],
        'Hello all',
        sim: 2,
        requestId: 'news-1',
      );
      expect(result.requestId, 'news-1');
      expect(result.to, [ben, anna]);
      expect(result.parts, 1);
      expect(result.status, 'queued');
      expect(
        [for (final m in result.messages) (m.requestId, m.to, m.status)],
        [('news-1.1', ben, 'queued'), ('news-1.2', anna, 'queued')],
      );
      expect(lastRequest(server, 'messages.sendGroup'), {
        'id': 'dart-2',
        'action': 'messages.sendGroup',
        'to': ['+43 690 876-54321', anna, ben],
        'body': 'Hello all',
        'requestId': 'news-1',
        'sim': 2,
      });
      expect(
        [
          for (final m in server.sent)
            (m['requestId'], m['to'], m['sim'], m['group']),
        ],
        [('news-1.1', ben, 2, 'news-1'), ('news-1.2', anna, 2, 'news-1')],
      );
      final entry = await client.waitForResult('news-1.2', timeout: seconds(5));
      expect(entry.status, 'sent');
      expect((await client.messageStatus('news-1.1')).to, ben);
      final error = await failure<ApiError>(client.messageStatus('news-1'));
      expect(error.code, 'not_found');
    });

    test('sendGroup creates a request id', () async {
      final (server, client) = await pair();
      final result = await client.sendGroup([ben], 'one');
      expect(result.requestId, matches(RegExp(r'^[0-9a-f]{32}$')));
      expect(result.messages.single.requestId, '${result.requestId}.1');
      expect(server.sent, hasLength(1));
    });

    test('sendGroup repeats only once', () async {
      final (server, client) = await pair();
      final first = await client.sendGroup(
        [ben, anna],
        'Same',
        requestId: 'g-2',
      );
      final again = await client.sendGroup(
        ['0690/876.54.321', anna],
        'Same',
        requestId: 'g-2',
      );
      expect(again, first);
      expect(server.sent, hasLength(2));
      final entry = await client.waitForResult('g-2.1', timeout: seconds(5));
      expect(entry.status, 'sent');
      final later = await client.sendGroup(
        [anna, ben],
        'Same',
        requestId: 'g-2',
      );
      expect(later.messages[0].status, anyOf('sent', 'delivered'));
      expect([for (final m in later.messages) m.requestId], ['g-2.1', 'g-2.2']);
      for (final (to, body) in [
        ([ben, anna], 'Other'),
        ([ben], 'Same'),
      ]) {
        final error = await failure<ApiError>(
          client.sendGroup(to, body, requestId: 'g-2'),
        );
        expect(error.code, 'invalid_request');
      }
      expect(server.sent, hasLength(2));
    });

    test('sendGroup refuses the whole group', () async {
      final (server, client) = await pair();
      final cases = [
        ([ben, 'abc'], 'Hello', 'invalid_number'),
        ([ben, anna], 'x' * 1531, 'message_too_long'),
        ([ben, anna, '+43123456789'], 'x' * 1000, 'invalid_request'),
        ([ben], 'Hello', 'sim_unavailable'),
      ];
      for (final (to, body, code) in cases) {
        final error = await failure<ApiError>(
          client.sendGroup(to, body, sim: code == 'sim_unavailable' ? 7 : null),
        );
        expect(error.code, code);
      }
      expect(server.sent, isEmpty);
    });

    test('sendGroup goes on after a refused member', () async {
      final (server, client) = await pair();
      server.refuseNumbers.add(anna);
      final result = await client.sendGroup(
        [ben, anna],
        'Hello',
        requestId: 'g-3',
      );
      expect([for (final m in result.messages) m.status], ['queued', 'failed']);
      expect(result.status, 'queued');
      final entry = await client.waitForResult('g-3.1', timeout: seconds(5));
      expect(entry.status, 'sent');
      expect((await client.messageStatus('g-3.2')).status, 'failed');
      server.refuseNumbers.add(ben);
      final all = await client.sendGroup(
        [ben, anna],
        'Hello',
        requestId: 'g-4',
      );
      expect(all.status, 'failed');
    });

    test('sendGroup refuses bad values', () async {
      final (server, client) = await pair();
      final many = [for (var i = 0; i < 21; i += 1) '+4369900000${i + 10}'];
      expect(client.sendGroup([], 'Hello'), throwsArgument());
      expect(client.sendGroup([ben, ' '], 'Hello'), throwsArgument());
      expect(client.sendGroup([ben], ''), throwsArgument());
      expect(client.sendGroup(many, 'Hello'), throwsArgument());
      for (final id in ['', 'a b', 'x' * 62]) {
        expect(
          client.sendGroup([ben], 'Hello', requestId: id),
          throwsArgument(contains('requestId')),
        );
      }
      await Future<void>.delayed(seconds(0.05));
      expect(server.sent, isEmpty);
    });

    test('request ids outlive a restart of the app', () async {
      final (server, client) = await pair();
      await client.send(ben, 'Hello', requestId: 'r-1');
      final entry = await client.waitForResult('r-1', timeout: seconds(5));
      expect(entry.status, 'sent');
      server.restart();
      final again = await client.send(ben, 'Hello', requestId: 'r-1');
      expect(again.status, 'sent');
      expect(server.sent, hasLength(1));
    });

    test('status of a long message', () async {
      final (server, client) = await pair(sendDelay: seconds(1));
      final result = await client.send(ben, 'x' * 400);
      expect((result.parts, result.status), (3, 'queued'));
      final id = result.requestId;
      expect(
        await waitUntil(() => server.outbox[id]!['status'] == 'sending'),
        isTrue,
      );
      var entry = await client.messageStatus(id);
      expect((entry.status, entry.partsSent), ('sending', 1));
      // Some parts are left: this is not the result yet.
      entry = await client.waitForResult(id, timeout: seconds(5));
      expect((entry.status, entry.partsSent), ('sent', 3));
    });

    test('send refuses empty values', () async {
      final (server, client) = await pair();
      expect(client.send('', 'text'), throwsArgument());
      expect(client.send('   ', 'text'), throwsArgument());
      expect(client.send(ben, ''), throwsArgument());
      await Future<void>.delayed(seconds(0.05));
      expect(server.sent, isEmpty);
    });

    test('send refuses malformed request ids', () async {
      final (server, client) = await pair();
      for (final id in ['', 'a b', 'ä', 'x' * 65, 'a/b']) {
        expect(
          client.send(ben, 'text', requestId: id),
          throwsArgument(contains('requestId')),
        );
      }
      await Future<void>.delayed(seconds(0.05));
      expect(server.sent, isEmpty);
    });

    test('send errors', () async {
      final (server, client) = await pair();
      final cases = [
        ('not a number', 'text', null, 'invalid_number'),
        (ben, 'x' * 1531, null, 'message_too_long'),
        (ben, 'ü' * 671, null, 'message_too_long'),
        (ben, 'text', 7, 'sim_unavailable'),
      ];
      for (final (to, body, sim, code) in cases) {
        final error = await failure<ApiError>(client.send(to, body, sim: sim));
        expect(error.code, code);
      }
      expect(server.sent, isEmpty);
    });

    test('longest message', () async {
      final (_, client) = await pair();
      expect((await client.send(ben, 'x' * 1530)).parts, 10);
      expect((await client.send(ben, 'ü' * 670)).parts, 10);
    });

    test('error details', () async {
      final (server, client) = await pair();
      server.failNext(
        'messages.send',
        'permission_missing',
        message: 'The permission to send SMS is missing.',
        extra: {'permission': 'sendSms'},
      );
      var error = await failure<ApiError>(client.send(ben, 'text'));
      expect(error.code, 'permission_missing');
      expect(error.message, 'The permission to send SMS is missing.');
      expect(error.permission, 'sendSms');
      expect(error.retryAfter, isNull);
      expect(error.details, {'permission': 'sendSms'});

      server.failNext('messages.send', 'quota_exceeded');
      error = await failure<ApiError>(client.send(ben, 'text'));
      expect(error.code, 'quota_exceeded');
      expect((await client.status()).deleteMode, 'hidden');
    });

    test('message status', () async {
      final (server, client) = await pair();
      final id = (await client.send(ben, 'x' * 200)).requestId;
      expect(
        await waitUntil(() => server.outbox[id]!['status'] == 'sent'),
        isTrue,
      );
      final entry = await client.messageStatus(id);
      expect(entry.status, 'sent');
      expect((entry.parts, entry.partsSent), (2, 2));
      expect(entry.source, 'api');
      expect(entry.errorCode, isNull);
      expect((await client.getMessage(entry.messageId!)).requestId, id);
      final error = await failure<ApiError>(client.messageStatus('unknown'));
      expect(error.code, 'not_found');
    });

    test('waitForResult by polling', () async {
      final (server, client) = await pair(sendDelay: seconds(0.2));
      final id = (await client.send(ben, 'text')).requestId;
      final entry = await client.waitForResult(
        id,
        timeout: seconds(5),
        pollInterval: seconds(0.05),
      );
      expect(entry.status, 'sent');
      expect(entry.requestId, id);
      expect(client.subscription, isNull);
      expect(countOf(server, 'messages.status'), greaterThanOrEqualTo(2));
    });

    test('waitForResult wakes up on the event', () async {
      final (_, client) = await pair(sendDelay: seconds(2));
      await client.subscribe(['sms.queued', 'sms.sent', 'sms.failed']);
      final id = (await client.send(ben, 'text')).requestId;
      final clock = Stopwatch()..start();
      final entry = await client.waitForResult(
        id,
        timeout: seconds(10),
        pollInterval: seconds(8),
      );
      // The client asks after 0.1, 0.3, 0.7, 1.5 and 3.1 seconds. The
      // message is sent after 2 seconds; only the event can tell that
      // early.
      expect(clock.elapsed, greaterThan(seconds(1.9)));
      expect(clock.elapsed, lessThan(seconds(2.9)));
      expect(entry.status, 'sent');
      // The events are still there for the application.
      final events = await drain(client, 0.05);
      expect([for (final e in events) e.name], ['sms.queued', 'sms.sent']);
    });

    test('waitForResult failed', () async {
      final (server, client) = await pair();
      server.failNumbers.add('+43123456');
      await client.subscribe(['sms.failed']);
      final id = (await client.send('+43123456', 'text')).requestId;
      final entry = await client.waitForResult(id, timeout: seconds(5));
      expect(entry.status, 'failed');
      expect(entry.errorCode, 1);
      final event = await client.waitForEvent([
        'sms.failed',
      ], timeout: seconds(1));
      expect(event.data['requestId'], id);
      expect(event.data['reason'], 'generic_failure');
    });

    test('waitForResult delivered', () async {
      final (_, client) = await pair(
        sendDelay: seconds(0.1),
        deliveryReports: true,
      );
      await client.subscribe(['*']);
      final id = (await client.send(ben, 'text')).requestId;
      final entry = await client.waitForResult(
        id,
        timeout: seconds(5),
        delivered: true,
        pollInterval: seconds(0.05),
      );
      expect(entry.status, 'delivered');
      final event = await client.waitForEvent([
        'sms.delivered',
      ], timeout: seconds(1));
      final message = await client.getMessage(entry.messageId!);
      expect(event.data, {
        'requestId': id,
        'id': entry.messageId,
        'conversationId': message.conversationId,
        'to': ben,
      });
      expect(message.status, 'delivered');
    });

    test('waitForResult times out', () async {
      final (server, client) = await pair(sendDelay: seconds(30));
      final id = (await client.send(ben, 'text')).requestId;
      final error = await failure<TimeoutException>(
        client.waitForResult(
          id,
          timeout: seconds(0.3),
          pollInterval: seconds(0.1),
        ),
      );
      expect(error.message, contains("'queued'"));
      expect(error.message, startsWith("No result for the message '$id'"));
      expect(countOf(server, 'messages.status'), greaterThanOrEqualTo(2));
      expect(
        client.waitForResult(id, pollInterval: Duration.zero),
        throwsArgument(),
      );
      expect((await client.ping()).time, greaterThan(0));
    });

    test('delete message', () async {
      final (server, client) = await pair();
      final newest = (await client.messages(limit: 1)).items[0];
      expect(await client.deleteMessage(newest.id!), {
        'deleted': 1,
        'mode': 'hidden',
      });
      expect(lastRequest(server, 'messages.delete'), {
        'id': 'dart-3',
        'action': 'messages.delete',
        'messageId': newest.id,
      });
      expect(server.messages, hasLength(28));
      final error = await failure<ApiError>(client.deleteMessage(newest.id!));
      expect(error.code, 'not_found');
    });

    test('delete conversation', () async {
      final (server, client) = await pair();
      server.deleteMode = 'provider';
      final id = (await client.conversations(q: 'anna')).items[0].id;
      final result = await client.deleteConversation(id);
      expect((result.deleted, result.mode), (25, 'provider'));
      expect(lastRequest(server, 'conversations.delete'), {
        'id': 'dart-3',
        'action': 'conversations.delete',
        'conversationId': id,
      });
      expect(addresses((await client.conversations()).items), [ben, 'A1']);
      final error = await failure<ApiError>(client.deleteConversation(id));
      expect(error.code, 'not_found');
    });

    test('delete sends an event', () async {
      final (_, client) = await pair();
      await client.subscribe(['sms.deleted']);
      final newest = (await client.messages(limit: 1)).items[0];
      await client.deleteMessage(newest.id!);
      var event = await client.waitForEvent([
        'sms.deleted',
      ], timeout: seconds(2));
      expect(event.data, {
        'id': newest.id,
        'conversationId': newest.conversationId,
        'address': newest.address,
        'mode': 'hidden',
      });
      await client.deleteConversation(newest.conversationId!);
      event = await client.waitForEvent(['sms.deleted'], timeout: seconds(2));
      expect(event.data, {
        'id': null,
        'conversationId': newest.conversationId,
        'address': anna,
        'mode': 'hidden',
      });
    });

    test('mark read', () async {
      final (server, client) = await pair();
      server.deleteMode = 'provider';
      final first = (await client.conversations(q: 'anna')).items[0];
      expect(first.unreadCount, 3);
      expect(await client.markRead(first.id), {'ok': true});
      expect(lastRequest(server, 'conversations.markRead'), {
        'id': 'dart-3',
        'action': 'conversations.markRead',
        'conversationId': first.id,
      });
      final after = (await client.conversations(q: 'anna')).items[0];
      expect(after.unreadCount, 0);
      final unread = await client.messages(address: anna, unread: true);
      expect(unread.items, isEmpty);
      final error = await failure<ApiError>(client.markRead(1));
      expect(error.code, 'not_found');
    });

    test('mark read needs the default SMS app', () async {
      final (_, client) = await pair();
      expect((await client.status()).defaultSmsApp, isFalse);
      final first = (await client.conversations(q: 'anna')).items[0];
      final error = await failure<ApiError>(client.markRead(first.id));
      expect(error.code, 'not_default_app');
      final after = (await client.conversations(q: 'anna')).items[0];
      expect(after.unreadCount, 3);
    });

    test('contacts', () async {
      final (server, client) = await pair();
      server.addContact('Zoe Example', '+43660111222');
      List<String> names(Iterable<Contact> contacts) => [
        for (final contact in contacts) contact.name,
      ];
      final page = await client.contacts();
      expect(names(page.items), ['Anna Example', 'Ben Example', 'Zoe Example']);
      expect(page.items[1].numbers, [
        {'number': ben, 'label': 'Work'},
      ]);
      expect(page.items[1].numbers[0].label, 'Work');
      expect(names((await client.contacts(q: 'zoe')).items), ['Zoe Example']);
      expect(names((await client.contacts(q: '5678')).items), ['Anna Example']);
      final first = await client.contacts(limit: 2);
      expect(first.nextCursor, isNotNull);
      final second = await client.contacts(limit: 2, cursor: first.nextCursor);
      expect(names(second.items), ['Zoe Example']);
      final some = await client.iterContacts(pageSize: 1, maxItems: 2).toList();
      expect(names(some), ['Anna Example', 'Ben Example']);
    });

    test('generic request', () async {
      final (_, client) = await pair();
      final page = await client.request(
        'messages.list',
        fields: {'limit': 1, 'direction': 'out'},
      );
      expect(page['items'], hasLength(1));
      final error = await failure<ApiError>(client.request('messages.archive'));
      expect(error.code, 'unknown_action');
    });

    test('the request id belongs to the client', () async {
      final (server, client) = await pair();
      expect(
        client.request('messages.get', fields: {'id': 1}),
        throwsArgument(contains('id')),
      );
      expect(
        client.sendRequest('ping', fields: {'id': 'mine'}),
        throwsArgument(contains('id')),
      );
      await Future<void>.delayed(seconds(0.05));
      expect(actions(server), ['authenticate']);
    });

    test('answers may be larger than 64 KiB', () async {
      final (server, client) = await pair();
      for (var index = 0; index < 200; index += 1) {
        server.addMessage('+436601234567', '$index ${'x' * 1500}');
      }
      final page = await client.messages(address: '+436601234567', limit: 200);
      expect(page.items, hasLength(200));
      expect(jsonEncode(page).length, greaterThan(300000));
    });

    test('refuses requests over 64 KiB', () async {
      final (server, client) = await pair();
      expect(client.send(ben, 'x' * 70000), throwsArgument(contains('64 KiB')));
      await Future<void>.delayed(seconds(0.05));
      expect(actions(server), ['authenticate']);
      expect((await client.ping()).time, greaterThan(0));
    });

    test('close code 1009', () async {
      final (_, client) = await pair();
      debugMaxFrameBytes = 1024 * 1024;
      final error = await failure<ProtocolError>(client.send(ben, 'x' * 70000));
      expect('$error', contains('64 KiB'));
      // The same frame would fail again: no second connection.
      expect(client.connectionCount, 1);
      expect(client.connected, isFalse);
    });

    test('frames are compact JSON', () async {
      final (server, client) = await pair();
      await client.send(ben, 'Grüße', requestId: 'r1');
      expect(
        server.frames.last,
        '{"id":"dart-2","action":"messages.send","to":"+4369087654321",'
        '"body":"Grüße","requestId":"r1"}',
      );
    });

    test('requests awaited at once share the connection', () async {
      final server = await startServer();
      final client = makeClient(server);
      final results = await Future.wait([
        client.status(),
        client.ping(),
        client.contacts(),
      ]);
      expect(results[0]['deleteMode'], 'hidden');
      expect(results[2]['items'], hasLength(2));
      expect(client.connectionCount, 1);
      expect(server.loginCount, 1);
    });

    test('close ends the requests that wait', () async {
      final (server, client) = await pair();
      server.ignoreNext('status');
      final pending = failure<ConnectionInterrupted>(client.status());
      await waitUntil(() => countOf(server, 'status') == 1);
      await client.close();
      expect('${await pending}', contains('closed'));
      expect(client.connectionCount, 1);
    });
  });

  // --------------------------------------------------------------------
  // Events
  // --------------------------------------------------------------------

  group('events', skip: needTls, () {
    test('subscribe and unsubscribe', () async {
      final (server, client) = await pair();
      expect(client.subscription, isNull);
      expect(await client.subscribe(['sms.received']), {
        'events': ['sms.received'],
        'address': null,
      });
      // subscribe adds names; its address replaces the filter.
      final both = {
        'events': ['sms.received', 'sms.sent', 'sms.failed'],
        'address': anna,
      };
      expect(
        await client.subscribe(['sms.sent', 'sms.failed'], address: anna),
        both,
      );
      expect(client.subscription, both);
      expect(client.subscription?.address, anna);
      expect(await client.unsubscribe(['sms.sent']), {
        'events': ['sms.received', 'sms.failed'],
        'address': anna,
      });
      // Without address the filter is removed.
      expect(await client.subscribe(['sms.queued']), {
        'events': ['sms.received', 'sms.queued', 'sms.failed'],
        'address': null,
      });
      expect(lastRequest(server, 'subscribe').containsKey('address'), isFalse);
      // When no name is left, the filter is removed too.
      await client.subscribe(['sms.queued'], address: ben);
      const nothing = {'events': <String>[], 'address': null};
      expect(await client.unsubscribe(['*']), nothing);
      expect(client.subscription, nothing);
      // In the answer * is replaced by the names it stands for.
      expect(await client.subscribe(['*']), {
        'events': eventNames,
        'address': null,
      });
      expect(lastRequest(server, 'subscribe'), {
        'id': 'dart-8',
        'action': 'subscribe',
        'events': ['*'],
      });
      final rest = await client.unsubscribe([
        'sms.deleted',
        'conversations.changed',
      ]);
      expect(rest.events, [
        'sms.received',
        'sms.queued',
        'sms.sent',
        'sms.delivered',
        'sms.failed',
      ]);
      expect(client.subscribe([]), throwsArgument());
      final error = await failure<ApiError>(client.subscribe(['sms.unknown']));
      expect(error.code, 'invalid_request');
    });

    test('a new connection gets no events', () async {
      final (server, client) = await pair();
      server.receiveSms(anna, 'Nobody listens');
      expect(await drain(client, 0.2), isEmpty);
    });

    test('incoming SMS', () async {
      final (server, client) = await pair();
      await client.subscribe(['sms.received']);
      final stored = server.receiveSms(anna, 'STATUS');
      final event = await client.waitForEvent([
        'sms.received',
      ], timeout: seconds(2));
      expect(event.name, 'sms.received');
      expect(event.seq, 1);
      final message = event.message!;
      expect(message, stored);
      expect(message.address, anna);
      expect(message.contact, 'Anna Example');
      expect(message.direction, 'in');
      expect(message.read, isFalse);
      expect((await client.getMessage(message.id!)).body, 'STATUS');
    });

    test('react to an incoming SMS', () async {
      final (server, client) = await pair();
      await client.subscribe(['sms.received']);
      server.receiveSms(anna, 'STATUS');
      await for (final event in client.events(timeout: seconds(2))) {
        await client.send(event.message!.address, 'All systems running.');
        break;
      }
      expect(
        [for (final message in server.sent) (message['to'], message['body'])],
        [(anna, 'All systems running.')],
      );
    });

    test('events between request and response are queued', () async {
      final (server, client) = await pair();
      await client.subscribe(['sms.received']);
      server.beforeNextResponse([
        eventFrame(1, 'first'),
        eventFrame(2, 'second'),
      ]);
      expect((await client.status()).deleteMode, 'hidden');
      expect(client.pendingEvents, 2);
      server.beforeNextResponse([eventFrame(3, 'third')]);
      expect((await client.ping()).time, greaterThan(0));
      expect(client.pendingEvents, 3);
      final events = await drain(client, 0.1);
      expect([for (final event in events) event.seq], [1, 2, 3]);
      expect(eventBodies(events), ['first', 'second', 'third']);
      expect(client.pendingEvents, 0);
    });

    test('events end after the timeout', () async {
      final (_, client) = await pair();
      await client.subscribe(['*']);
      final clock = Stopwatch()..start();
      expect(await drain(client, 0.2), isEmpty);
      expect(clock.elapsed, greaterThan(seconds(0.15)));
      expect(clock.elapsed, lessThan(seconds(2)));
      expect(client.connected, isTrue);
    });

    test('events arrive in order', () async {
      final (server, client) = await pair();
      await client.subscribe(['sms.received']);
      for (var index = 0; index < 5; index += 1) {
        server.receiveSms(anna, 'Message $index');
      }
      final events = await drain(client, 0.3);
      expect(eventBodies(events), [
        for (var index = 0; index < 5; index += 1) 'Message $index',
      ]);
      expect([for (final event in events) event.seq], [1, 2, 3, 4, 5]);
    });

    test('events stay in the queue while the reader is busy', () async {
      final (server, client) = await pair();
      await client.subscribe(['sms.received']);
      for (var index = 0; index < 3; index += 1) {
        server.receiveSms(anna, 'Message $index');
      }
      await waitUntil(() => client.pendingEvents == 3);
      final pending = <int>[];
      await for (final _ in client.events(timeout: seconds(0.2))) {
        await client.ping();
        pending.add(client.pendingEvents);
      }
      expect(pending, [2, 1, 0]);
    });

    test('an event that nobody takes stays in the queue', () async {
      final (server, client) = await pair();
      await client.subscribe(['sms.received']);
      server
        ..receiveSms(anna, 'one')
        ..receiveSms(anna, 'two');
      await waitUntil(() => client.pendingEvents == 2);
      final first = await client.events().first;
      expect(first.message?.body, 'one');
      await Future<void>.delayed(seconds(0.05));
      expect(client.pendingEvents, 1);
      expect(eventBodies(await drain(client, 0.1)), ['two']);
    });

    test('close ends the stream of events', () async {
      final (_, client) = await pair();
      await client.subscribe(['sms.received']);
      final events = client.events().toList();
      await Future<void>.delayed(seconds(0.05));
      await client.close();
      expect(await events.timeout(seconds(2)), isEmpty);
    });

    test('waitForEvent keeps the others', () async {
      final (server, client) = await pair();
      await client.subscribe(['*']);
      server
        ..receiveSms(anna, 'one')
        ..emit('conversations.changed', {})
        ..receiveSms(ben, 'two');
      final changed = await client.waitForEvent([
        'conversations.changed',
      ], timeout: seconds(2));
      expect(changed.data, isEmpty);
      await waitUntil(() => client.pendingEvents == 2);
      final fromBen = await client.waitForEvent(
        ['sms.received'],
        timeout: seconds(2),
        predicate: (event) => event.message?.address == ben,
      );
      expect(fromBen.message?.body, 'two');
      expect(client.pendingEvents, 1);
      final last = await client.waitForEvent([
        'sms.sent',
        'sms.received',
      ], timeout: seconds(2));
      expect(last.message?.body, 'one');
      expect(client.pendingEvents, 0);
    });

    test('waitForEvent times out', () async {
      final (server, client) = await pair();
      await client.subscribe(['*']);
      server.emit('conversations.changed', {});
      final error = await failure<TimeoutException>(
        client.waitForEvent(['sms.received'], timeout: seconds(0.2)),
      );
      expect(error.message, 'No event sms.received in time');
      expect(client.pendingEvents, 1);
      expect(client.connected, isTrue);
    });

    test('address filter', () async {
      final (server, client) = await pair();
      await client.subscribe(['sms.received'], address: ben);
      server
        ..receiveSms(anna, 'from Anna')
        ..receiveSms(ben, 'from Ben');
      expect(eventBodies(await drain(client, 0.3)), ['from Ben']);
    });

    test('address filter compares like the phone', () async {
      final (server, client) = await pair();
      // A number without country code matches every number it ends.
      await client.subscribe(['sms.received'], address: '0690 876 54 321');
      server
        ..receiveSms(ben, 'international')
        ..receiveSms('0043 690 87654321', 'with 00')
        ..receiveSms('+49 690 87654321', 'another country')
        ..receiveSms('0690 876 54 322', 'another number');
      expect(eventBodies(await drain(client, 0.3)), [
        'international',
        'with 00',
        'another country',
      ]);
      // Two numbers with country code are the same or they are not.
      await client.subscribe(['sms.received'], address: '0043 690 876 54 321');
      server
        ..receiveSms(ben, 'international')
        ..receiveSms('0690/87654321', 'national')
        ..receiveSms('+49 690 87654321', 'another country');
      expect(eventBodies(await drain(client, 0.3)), [
        'international',
        'national',
      ]);
      await client.subscribe(['sms.received'], address: 'mybank');
      server
        ..receiveSms('MyBank', 'Your code is 1234')
        ..receiveSms('MyBank2', 'other sender');
      expect(eventBodies(await drain(client, 0.3)), ['Your code is 1234']);
    });

    test('address filter on all events that name a peer', () async {
      final (server, client) = await pair(deliveryReports: true);
      server.failNumbers.add('+43123456789');
      await client.subscribe(['*'], address: ben);
      for (final number in [anna, ben, '+43123456789']) {
        final result = await client.send(number, 'text');
        await client.waitForResult(result.requestId, timeout: seconds(5));
      }
      expect(
        await waitUntil(
          () => server.outbox.values.every(
            (entry) => const ['delivered', 'failed'].contains(entry['status']),
          ),
        ),
        isTrue,
      );
      final toBen = (await client.messages(address: ben, limit: 1)).items[0];
      final toAnna = (await client.messages(address: anna, limit: 1)).items[0];
      await client.deleteMessage(toAnna.id!);
      await client.deleteMessage(toBen.id!);
      // Events that name no peer always pass.
      server
        ..emit('conversations.changed', {})
        ..emit('sms.deleted', {
          'id': null,
          'conversationId': 1,
          'address': null,
          'mode': 'hidden',
        });
      final events = await drain(client, 0.3);
      expect(
        [for (final event in events) event.name],
        [
          'sms.queued',
          'sms.sent',
          'sms.delivered',
          'sms.deleted',
          'conversations.changed',
          'sms.deleted',
        ],
      );
      expect(
        [for (final event in events) event.data['to'] ?? event.data['address']],
        [ben, ben, ben, ben, null, null],
      );
    });

    test('events of a sent message', () async {
      final (server, client) = await pair(deliveryReports: true);
      server.failNumbers.add('+43123456789');
      await client.subscribe([
        'sms.queued',
        'sms.sent',
        'sms.delivered',
        'sms.failed',
      ]);
      final good = (await client.send(ben, 'text')).requestId;
      final entry = await client.waitForResult(
        good,
        timeout: seconds(5),
        delivered: true,
      );
      final message = await client.getMessage(entry.messageId!);
      final queued = await client.waitForEvent([
        'sms.queued',
      ], timeout: seconds(1));
      expect(queued.data, {
        'requestId': good,
        'to': ben,
        'parts': 1,
        'sim': 1,
        'source': 'api',
      });
      final sent = await client.waitForEvent(['sms.sent'], timeout: seconds(1));
      expect(sent.data.keys.toSet(), {
        'requestId',
        'conversationId',
        'to',
        'message',
      });
      expect(sent.data['conversationId'], message.conversationId);
      expect(sent.data['to'], ben);
      expect(sent.message?.id, entry.messageId);

      final bad = (await client.send('+43123456789', 'text')).requestId;
      final outcome = await client.waitForResult(bad, timeout: seconds(5));
      expect(outcome.status, 'failed');
      final failed = await client.waitForEvent([
        'sms.failed',
      ], timeout: seconds(1));
      // The phone has not stored the message: no id, no conversation.
      expect(failed.data, {
        'requestId': bad,
        'id': null,
        'conversationId': null,
        'to': '+43123456789',
        'errorCode': 1,
        'reason': 'generic_failure',
      });
    });

    test('full event queue drops the oldest', () async {
      final (server, client) = await pair(eventQueueSize: 2);
      server.beforeNextResponse([
        for (final n in [1, 2, 3, 4]) eventFrame(n),
      ]);
      await client.ping();
      expect(client.pendingEvents, 2);
      expect(client.droppedEvents, 2);
      final events = await drain(client, 0.05);
      expect([for (final event in events) event.seq], [3, 4]);
    });

    test('responses in another order', () async {
      final (server, client) = await pair();
      server.reverseNextResponses(3);
      final first = await client.sendRequest(
        'messages.list',
        fields: {'limit': 1},
      );
      final second = await client.sendRequest('status');
      final third = await client.sendRequest('contacts.list');
      expect({first, second, third}, hasLength(3));
      final status = await client.waitForResponse(second, timeout: seconds(2));
      expect(status.containsKey('deleteMode'), isTrue);
      final page = await client.waitForResponse(first, timeout: seconds(2));
      expect(bodies(Page<Message>(page).items), ['Anna 24']);
      final contacts = await client.waitForResponse(third, timeout: seconds(2));
      expect(contacts['items'], hasLength(2));
      expect(client.waitForResponse(first), throwsArgument());
      expect((await client.ping()).time, greaterThan(0));
    });

    test('response error of a pipelined request', () async {
      final (server, client) = await pair();
      server.reverseNextResponses(2);
      final missing = await client.sendRequest(
        'messages.get',
        fields: {'messageId': 1},
      );
      final fine = await client.sendRequest('ping');
      final error = await failure<ApiError>(
        client.waitForResponse(missing, timeout: seconds(2)),
      );
      expect(error.code, 'not_found');
      final result = await client.waitForResponse(fine, timeout: seconds(2));
      expect(result['time'], greaterThan(0));
    });

    test('waitForResponse times out and can be asked again', () async {
      final (server, client) = await pair();
      server.ignoreNext('status');
      final id = await client.sendRequest('status');
      final error = await failure<TimeoutException>(
        client.waitForResponse(id, timeout: seconds(0.2)),
      );
      expect(error.message, contains('No response'));
      expect(client.connected, isTrue);
      expect((await client.ping()).time, greaterThan(0));
    });

    test('stale responses are ignored', () async {
      final (server, client) = await pair();
      server.beforeNextResponse([
        {
          'id': 'dart-0',
          'ok': true,
          'result': {'stale': true},
        },
        {
          'id': 77,
          'ok': false,
          'error': {'code': 'internal_error', 'message': 'stale'},
        },
      ]);
      expect((await client.status()).deleteMode, 'hidden');
      expect((await client.ping()).time, greaterThan(0));
    });

    test('response without id answers the open request', () async {
      final (server, client) = await pair();
      server.beforeNextResponse([
        {
          'id': null,
          'ok': false,
          'error': {'code': 'invalid_json', 'message': 'Not a JSON object.'},
        },
      ]);
      final error = await failure<ApiError>(client.status());
      expect(error.code, 'invalid_json');
      // The late real response is dropped; the next request works.
      expect((await client.ping()).time, greaterThan(0));
    });

    for (final (frame, message) in [
      ('this is not JSON', 'invalid JSON'),
      ('[1, 2, 3]', 'not a JSON object'),
    ]) {
      test('malformed frame: $frame', () async {
        final (server, client) = await pair();
        server.beforeNextResponse([frame]);
        final error = await failure<ProtocolError>(client.status());
        expect('$error', contains(message));
      });
    }

    test('malformed response', () async {
      final (server, client) = await pair();
      server.beforeNextResponse([
        {'id': 'dart-2', 'ok': false},
      ]);
      final error = await failure<ProtocolError>(client.status());
      expect('$error', contains('Malformed'));
    });
  });

  // --------------------------------------------------------------------
  // Reconnect
  // --------------------------------------------------------------------

  group('reconnect', skip: needTls, () {
    test('reconnects and logs in again', () async {
      final (server, client) = await pair();
      expect((await client.ping()).time, greaterThan(0));
      server.dropConnections();
      expect((await client.status()).deleteMode, 'hidden');
      expect(client.connectionCount, 2);
      expect(server.loginCount, 2);
      expect(actions(server), [
        'authenticate',
        'ping',
        'authenticate',
        'status',
      ]);
    });

    test('reconnects after a close frame', () async {
      final (server, client) = await pair();
      server.dropConnections(1001, 'server is stopping');
      expect((await client.ping()).time, greaterThan(0));
      server.dropConnections(4408, 'no answer to pings');
      expect((await client.ping()).time, greaterThan(0));
      server.dropConnections(4429, 'too many requests');
      expect((await client.ping()).time, greaterThan(0));
      expect(client.connectionCount, 4);
    });

    test('request lost before it was handled', () async {
      final (server, client) = await pair();
      server
        ..dropOn('status', 'before')
        ..dropOn('status', 'before');
      expect((await client.status()).deleteMode, 'hidden');
      expect(countOf(server, 'status'), 3);
      expect(client.connectionCount, 3);
    });

    test('restores the subscription', () async {
      final (server, client) = await pair();
      await client.subscribe(['sms.received', 'sms.failed'], address: ben);
      server.dropConnections();
      expect((await client.ping()).time, greaterThan(0));
      expect(actions(server), [
        'authenticate',
        'subscribe',
        'authenticate',
        'subscribe',
        'ping',
      ]);
      final restored = server.requests[3];
      expect(restored['events'], ['sms.received', 'sms.failed']);
      expect(restored['address'], ben);
      server
        ..receiveSms(anna, 'filtered out')
        ..receiveSms(ben, 'after the reconnect');
      final event = await client.waitForEvent([
        'sms.received',
      ], timeout: seconds(2));
      expect(event.message?.body, 'after the reconnect');
      expect(client.pendingEvents, 0);
    });

    test('does not restore an empty subscription', () async {
      final (server, client) = await pair();
      await client.subscribe(['sms.received']);
      await client.unsubscribe(['sms.received']);
      server.dropConnections();
      expect((await client.ping()).time, greaterThan(0));
      expect(countOf(server, 'subscribe'), 1);
    });

    test('waiting for events reconnects', () async {
      final (server, client) = await pair();
      await client.subscribe(['sms.received']);
      server.dropConnections();
      unawaited(
        waitUntil(() => countOf(server, 'subscribe') == 2).then((ready) {
          if (ready) {
            server.receiveSms(anna, 'after the reconnect');
          }
        }),
      );
      final event = await client.events(timeout: seconds(5)).first;
      expect(event.message?.body, 'after the reconnect');
      expect(client.connectionCount, 2);
    });

    test('send is not repeated after a lost response', () async {
      final (server, client) = await pair();
      server.dropOn('messages.send', 'after');
      final result = await client.send(ben, 'Only once');
      // The repeat tells the status of now; the message may be out
      // already.
      expect(result.status, anyOf('queued', 'sent'));
      final requests = requestsOf(server, 'messages.send');
      expect(requests, hasLength(2));
      expect(requests[0]['requestId'], result.requestId);
      expect(requests[1]['requestId'], result.requestId);
      expect(requests[0]['id'], isNot(requests[1]['id']));
      expect(client.connectionCount, 2);
      final entry = await client.waitForResult(
        result.requestId,
        timeout: seconds(5),
      );
      expect(entry.status, 'sent');
      // The radio was used once although the request arrived twice.
      expect(bodies(server.sent), ['Only once']);
      expect(
        server.messages.where((message) => message['body'] == 'Only once'),
        hasLength(1),
      );
    });

    test('waitForResult survives a reconnect', () async {
      final (server, client) = await pair(sendDelay: seconds(0.3));
      await client.subscribe(['sms.sent', 'sms.failed']);
      final id = (await client.send(ben, 'text')).requestId;
      server.dropConnections();
      final entry = await client.waitForResult(
        id,
        timeout: seconds(5),
        pollInterval: seconds(0.1),
      );
      expect(entry.status, 'sent');
      expect(server.sent, hasLength(1));
    });

    test('gives up after the last attempt', () async {
      final server = await startServer();
      final client = makeClient(
        server,
        reconnectAttempts: 3,
        connectTimeout: seconds(2),
      );
      await client.connect();
      await server.stop();
      final error = await failure<ConnectionInterrupted>(client.status());
      expect('$error', contains("'status'"));
      expect(error.cause, isA<SocketException>());
      expect(client.connected, isFalse);
    });

    test('backoff is bounded', () async {
      final server = await startServer();
      final client = makeClient(
        server,
        reconnectAttempts: 6,
        reconnectDelay: seconds(1),
        maxReconnectDelay: seconds(10),
        connectTimeout: seconds(2),
      );
      await client.connect();
      await server.stop();
      final pauses = recordPauses();
      await failure<ConnectionInterrupted>(client.status());
      expect(pauses, [
        for (final time in [1, 2, 4, 8, 10, 10]) seconds(time),
      ]);
    });

    test('no retry on request', () async {
      final (server, client) = await pair();
      server.dropOn('status', 'before');
      await failure<ConnectionInterrupted>(
        client.request('status', retry: false),
      );
      expect(client.connectionCount, 1);
      expect((await client.status()).deleteMode, 'hidden');
      expect(client.connectionCount, 2);
    });

    test('zero reconnect attempts', () async {
      final server = await startServer();
      final client = makeClient(server, reconnectAttempts: 0);
      await client.connect();
      server.dropOn('status', 'before');
      await failure<ConnectionInterrupted>(client.status());
      expect(client.connectionCount, 1);
    });

    test('pipelined requests do not reconnect', () async {
      final (server, client) = await pair();
      server.dropOn('status', 'after');
      final id = await client.sendRequest('status');
      await failure<ConnectionInterrupted>(
        client.waitForResponse(id, timeout: seconds(2)),
      );
      expect(client.connectionCount, 1);
    });

    test('a pipelined request whose connection was lost before', () async {
      final (server, client) = await pair();
      server.ignoreNext('status');
      final id = await client.sendRequest('status');
      await waitUntil(() => countOf(server, 'status') == 1);
      server.dropConnections();
      await waitUntil(() => !client.connected);
      final error = await failure<ConnectionInterrupted>(
        client.waitForResponse(id, timeout: seconds(2)),
      );
      expect('$error', contains("'$id'"));
      expect(client.connectionCount, 1);
    });

    for (final (code, licence, errorCode) in [
      (4410, false, 'not_authenticated'),
      (4401, false, 'not_authenticated'),
      (4403, true, 'licence_required'),
    ]) {
      test('close code $code forbids a retry', () async {
        final (server, client) = await pair();
        server.dropConnections(code);
        final error = await failure<ApiError>(client.status());
        expect(
          error,
          licence ? isA<LicenceRequiredError>() : isA<AuthenticationError>(),
        );
        expect(error.code, errorCode);
        expect(error.details['closeCode'], code);
        // No new login with a password that may be wrong by now.
        expect(server.loginCount, 1);
        expect(client.connectionCount, 1);
      });
    }

    test('password changed while listening', () async {
      final (server, client) = await pair();
      await client.subscribe(['sms.received']);
      Timer(
        seconds(0.2),
        () => server.dropConnections(4410, 'password changed'),
      );
      final error = await failure<AuthenticationError>(drain(client, 5));
      expect('$error', contains('password changed'));
      expect(server.loginCount, 1);
    });

    test('protocol error close code', () async {
      final (server, client) = await pair();
      server.dropConnections(4400, 'binary frame');
      final error = await failure<ProtocolError>(client.status());
      expect('$error', contains('binary frame'));
      expect(client.connectionCount, 1);
    });

    test('request timeout reconnects', () async {
      final server = await startServer();
      final client = makeClient(
        server,
        timeout: seconds(0.3),
        reconnectAttempts: 1,
      );
      await client.connect();
      server.ignoreNext('status');
      expect((await client.status()).deleteMode, 'hidden');
      expect(client.connectionCount, 2);
      server.ignoreNext('status', 2);
      final error = await failure<ConnectionInterrupted>(client.status());
      expect('$error', contains('No response'));
      expect(client.connectionCount, 3);
    });
  });

  // --------------------------------------------------------------------
  // Epoch and sequence numbers
  // --------------------------------------------------------------------

  group('epoch and sequence numbers', skip: needTls, () {
    test('login tells epoch and sequence number', () async {
      final server = await startServer();
      server
        ..receiveSms(anna, 'one')
        ..receiveSms(anna, 'two');
      final client = makeClient(server);
      expect(client.epoch, isNull);
      expect(client.latestSeq, isNull);
      await client.connect();
      expect(client.epoch, server.epoch);
      expect(client.latestSeq, 2);
      expect(client.serverRestarted, isFalse);
      expect(client.missedEvents, 0);
    });

    test('latestSeq follows the events', () async {
      final (server, client) = await pair();
      await client.subscribe(['conversations.changed']);
      server
        ..receiveSms(anna, 'not subscribed')
        ..emit('conversations.changed', {});
      final event = await client.waitForEvent([
        'conversations.changed',
      ], timeout: seconds(2));
      expect(event.seq, 2);
      expect(client.latestSeq, 2);
      // Events read while a request waits for its response count as well.
      server.beforeNextResponse([eventFrame(7)]);
      await client.ping();
      expect(client.latestSeq, 7);
    });

    test('counts the events missed while disconnected', () async {
      final calls = <(int?, bool, bool)>[];
      final subscriptions = <Subscription?>[];
      final (server, client) = await pair(
        onReconnect: (c) {
          calls.add((c.missedEvents, c.serverRestarted, c.connected));
          subscriptions.add(c.subscription);
        },
      );
      expect(calls, isEmpty);
      await client.subscribe(['sms.received']);
      server.receiveSms(anna, 'seen');
      final seen = await client.waitForEvent([
        'sms.received',
      ], timeout: seconds(2));
      expect(seen.seq, 1);
      server.dropConnections();
      expect(await waitUntil(() => server.clientCount == 0), isTrue);
      for (final body in ['lost one', 'lost two', 'lost three']) {
        server.receiveSms(anna, body);
      }
      expect((await client.ping()).time, greaterThan(0));
      expect(client.missedEvents, 3);
      expect(client.serverRestarted, isFalse);
      expect(client.latestSeq, 4);
      // The hook runs once the subscription is in force again.
      expect(calls, [(3, false, true)]);
      expect(subscriptions, [
        {
          'events': ['sms.received'],
          'address': null,
        },
      ]);
      server.dropConnections();
      expect((await client.ping()).time, greaterThan(0));
      expect(client.missedEvents, 0);
      expect(calls, hasLength(2));
    });

    test('notices a restart of the app', () async {
      final (server, client) = await pair();
      await client.subscribe(['sms.received']);
      for (var index = 0; index < 5; index += 1) {
        server.receiveSms(anna, 'before $index');
      }
      expect(await drain(client, 0.3), hasLength(5));
      final first = client.epoch;
      server
        ..restart()
        ..receiveSms(anna, 'lost');
      expect((await client.ping()).time, greaterThan(0));
      expect(client.serverRestarted, isTrue);
      expect(client.missedEvents, isNull);
      expect(client.epoch, server.epoch);
      expect(client.epoch, isNot(first));
      expect(client.latestSeq, 1);
      server.receiveSms(anna, 'after');
      final event = await client.waitForEvent([
        'sms.received',
      ], timeout: seconds(2));
      expect(event.seq, 2);
      server.dropConnections();
      expect((await client.ping()).time, greaterThan(0));
      expect(client.serverRestarted, isFalse);
      expect(client.missedEvents, 0);
    });

    test('hook reads what was missed', () async {
      final seen = <String>[];
      var newest = 0;
      void handle(Message message) {
        seen.add(message.body);
        if (message.timestamp > newest) {
          newest = message.timestamp;
        }
      }

      Future<void> catchUp(SmsDockClient dock) async {
        final missed = await dock
            .iterMessages(direction: 'in', since: newest + 1)
            .toList();
        missed.reversed.forEach(handle);
      }

      final (server, client) = await pair(onReconnect: catchUp);
      await client.subscribe(['sms.received']);
      server.receiveSms(anna, 'first');
      final first = await client.waitForEvent([
        'sms.received',
      ], timeout: seconds(2));
      handle(first.message!);
      server.dropConnections();
      expect(await waitUntil(() => server.clientCount == 0), isTrue);
      await Future<void>.delayed(seconds(0.01));
      server.receiveSms(anna, 'while away 1');
      await Future<void>.delayed(seconds(0.01));
      server.receiveSms(ben, 'while away 2');
      unawaited(
        waitUntil(() => countOf(server, 'subscribe') == 2).then((_) async {
          await Future<void>.delayed(seconds(0.05));
          server.receiveSms(anna, 'live again');
        }),
      );
      final event = await client.events(timeout: seconds(5)).first;
      handle(event.message!);
      expect(seen, ['first', 'while away 1', 'while away 2', 'live again']);
    });

    test('hook can be set later', () async {
      final (server, client) = await pair();
      final calls = <int>[];
      client.onReconnect = (c) => calls.add(c.connectionCount);
      server.dropConnections();
      await client.ping();
      expect(calls, [2]);
    });

    test('login without epoch is refused', () async {
      final server = await startServer();
      final client = makeClient(server);
      server.beforeNextResponse([
        {
          'id': 'dart-1',
          'ok': true,
          'result': {
            'protocolVersion': 1,
            'app': {'name': 'SMS Dock', 'version': '0.9.0'},
          },
        },
      ]);
      final error = await failure<ProtocolError>(client.connect());
      expect('$error', contains('epoch'));
      expect(client.connected, isFalse);
    });
  });

  // --------------------------------------------------------------------
  // Rate limit
  // --------------------------------------------------------------------

  group('rate limit', skip: needTls, () {
    const limited = 'rate_limited';
    Map<String, dynamic> after(int time) => {'retryAfter': time};

    test('waits and repeats when rate limited', () async {
      final (server, client) = await pair();
      final pauses = recordPauses();
      server.failNext(
        'status',
        limited,
        message: 'Too many requests.',
        extra: after(1),
      );
      expect((await client.status()).deleteMode, 'hidden');
      expect(pauses, [seconds(1)]);
      server
        ..failNext('status', limited, extra: after(1))
        ..failNext('status', limited, extra: after(3));
      expect((await client.status()).deleteMode, 'hidden');
      expect(pauses, [seconds(1), seconds(1), seconds(3)]);
      expect(countOf(server, 'status'), 5);
      expect(client.connectionCount, 1);
    });

    test('gives up when rate limited again and again', () async {
      final (server, client) = await pair();
      final pauses = recordPauses();
      for (var index = 0; index < 3; index += 1) {
        server.failNext('status', limited, extra: after(1));
      }
      final error = await failure<ApiError>(client.status());
      expect(error.code, limited);
      expect(error.retryAfter, 1);
      expect(pauses, [seconds(1), seconds(1)]);
      expect(countOf(server, 'status'), 3);
      expect((await client.ping()).time, greaterThan(0));
    });

    test('limit of messages per minute is reported at once', () async {
      final (server, client) = await pair();
      final pauses = recordPauses();
      // The limit of 20 SMS parts per minute names no seconds to wait.
      server.failNext(
        'messages.send',
        limited,
        message: 'Too many messages per minute. Try again shortly.',
      );
      final error = await failure<ApiError>(client.send(ben, 'text'));
      expect(error.code, limited);
      expect(error.retryAfter, isNull);
      expect(pauses, isEmpty);
      expect(server.sent, isEmpty);
    });

    test('rate limit without a repeat', () async {
      final server = await startServer();
      final pauses = recordPauses();
      Future<void> check(
        SmsDockClient client, {
        int retryAfter = 1,
        bool retry = true,
      }) async {
        server.failNext('status', limited, extra: after(retryAfter));
        final error = await failure<ApiError>(
          client.request('status', retry: retry),
        );
        expect(error.code, limited);
        expect(pauses, isEmpty);
      }

      await check(makeClient(server, rateLimitRetries: 0));
      await check(makeClient(server, timeout: seconds(5)), retryAfter: 6);
      await check(makeClient(server), retry: false);
    });

    test('send is not repeated when rate limited', () async {
      final (server, client) = await pair();
      final pauses = recordPauses();
      server.failNext('messages.send', limited, extra: after(1));
      final result = await client.send(ben, 'Only once');
      expect(pauses, [seconds(1)]);
      final requests = requestsOf(server, 'messages.send');
      expect(
        [for (final request in requests) request['requestId']],
        [result.requestId, result.requestId],
      );
      expect(bodies(server.sent), ['Only once']);
    });

    test('stays within the limit of the server', () async {
      final server = await startServer(requestsPerSecond: 5);
      final client = makeClient(server);
      await client.connect();
      final clock = Stopwatch()..start();
      for (var index = 0; index < 9; index += 1) {
        expect((await client.ping()).time, greaterThan(0));
      }
      // The login and 4 requests fit into the first second, 5 into the
      // next.
      expect(server.refusedCount, greaterThanOrEqualTo(1));
      expect(clock.elapsed, greaterThanOrEqualTo(seconds(0.9)));
      expect(countOf(server, 'ping'), 9);
      expect(client.connectionCount, 1);
    });
  });
}
