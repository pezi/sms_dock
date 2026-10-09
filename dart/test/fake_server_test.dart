// --------------------------------------------------------------------------
// Filename: fake_server_test.dart
//
// Description: Tests of the fake server itself, with a bare WebSocket
//   connection.
// --------------------------------------------------------------------------

/// Tests of the fake server itself, with a bare WebSocket connection.
///
/// They pin down the behaviour of `API.md` that the client never triggers
/// on its own: close codes, limits and errors before the login.
library;

import 'dart:async';
import 'dart:convert';
import 'dart:io';

import 'package:test/test.dart';

import 'fake_server.dart';
import 'helpers.dart';

/// A bare connection that sends and receives JSON objects.
class Wire {
  Wire._(this.socket) {
    socket.listen(
      (Object? frame) {
        _frames.add(frame);
        _wake();
      },
      onError: (Object _) {},
      onDone: () {
        _done = true;
        _wake();
      },
    );
  }

  /// Opens a connection that accepts the self-signed certificate.
  static Future<Wire> open(FakeSmsDock server, [String path = '']) async {
    final http = HttpClient(context: SecurityContext(withTrustedRoots: false))
      ..badCertificateCallback = (certificate, host, port) => true;
    final socket = await WebSocket.connect(
      '${server.url}$path',
      customClient: http,
      compression: CompressionOptions.compressionOff,
    ).timeout(const Duration(seconds: 5));
    final wire = Wire._(socket);
    addTearDown(() {
      socket.close().ignore();
      http.close(force: true);
    });
    return wire;
  }

  final WebSocket socket;
  final List<Object?> _frames = [];
  bool _done = false;
  Completer<void>? _waiter;

  void _wake() {
    _waiter?.complete();
    _waiter = null;
  }

  Future<void> _wait(Duration timeout) {
    final waiter = _waiter ??= Completer<void>();
    return waiter.future.timeout(timeout);
  }

  void send(JsonObject request) => socket.add(jsonEncode(request));

  Future<JsonObject> receive([num timeout = 3]) async {
    while (_frames.isEmpty) {
      if (_done) {
        throw StateError('The connection is closed');
      }
      await _wait(seconds(timeout));
    }
    return jsonDecode(_frames.removeAt(0)! as String) as JsonObject;
  }

  Future<JsonObject> call(JsonObject request) {
    send(request);
    return receive();
  }

  Future<void> login() async {
    expect((await receive())['event'], 'hello');
    final response = await call({
      'id': 0,
      'action': 'authenticate',
      'password': password,
    });
    expect(response['ok'], isTrue);
  }

  /// Reads until the server closes and returns the close code.
  Future<int?> closeCode([num timeout = 3]) async {
    while (!_done) {
      await _wait(seconds(timeout));
    }
    return socket.closeCode;
  }
}

JsonObject errorOf(JsonObject response) => response['error'] as JsonObject;

void main() {
  prepareTls();

  group('fake server', skip: needTls, () {
    test('hello comes first', () async {
      final wire = await Wire.open(await startServer());
      expect(await wire.receive(), {
        'event': 'hello',
        'protocolVersion': 1,
        'authenticationRequired': true,
      });
    });

    test('login result', () async {
      final server = await startServer();
      final wire = await Wire.open(server);
      await wire.receive();
      server.receiveSms(anna, 'one event so far');
      final response = await wire.call({
        'id': '1',
        'action': 'authenticate',
        'password': password,
      });
      expect(response, {
        'id': '1',
        'ok': true,
        'result': {
          'protocolVersion': 1,
          'app': {'name': 'SMS Dock', 'version': '1.0.0', 'protocolVersion': 1},
          'latestSeq': 1,
          'epoch': server.epoch,
        },
      });
    });

    test('actions need a login', () async {
      final wire = await Wire.open(await startServer());
      await wire.receive();
      final response = await wire.call({'id': 5, 'action': 'messages.list'});
      expect(response['id'], 5);
      expect(response['ok'], isFalse);
      expect(errorOf(response)['code'], 'not_authenticated');
    });

    test('wrong password closes with 4401', () async {
      final wire = await Wire.open(await startServer());
      await wire.receive();
      final response = await wire.call({
        'id': '1',
        'action': 'authenticate',
        'password': 'wrong',
      });
      expect(errorOf(response)['code'], 'invalid_password');
      expect(await wire.closeCode(), 4401);
    });

    test('lock after five wrong passwords', () async {
      final server = await startServer();
      for (var attempt = 0; attempt < 5; attempt += 1) {
        final wire = await Wire.open(server);
        await wire.receive();
        final response = await wire.call({
          'id': '1',
          'action': 'authenticate',
          'password': 'wrong',
        });
        expect(errorOf(response)['code'], 'invalid_password');
      }
      final wire = await Wire.open(server);
      await wire.receive();
      final response = await wire.call({
        'id': '1',
        'action': 'authenticate',
        'password': password,
      });
      expect(errorOf(response)['code'], 'locked_out');
      expect(errorOf(response)['retryAfter'], inInclusiveRange(1, 30));
      expect(await wire.closeCode(), 4401);
    });

    test('licence required closes with 4403', () async {
      final server = await startServer();
      server.licenceEnforced = true;
      var wire = await Wire.open(server);
      await wire.receive();
      var response = await wire.call({
        'id': '1',
        'action': 'authenticate',
        'password': password,
      });
      expect(errorOf(response)['code'], 'licence_required');
      expect(await wire.closeCode(), 4403);
      // The licence is looked at first: a wrong password is not counted.
      wire = await Wire.open(server);
      await wire.receive();
      response = await wire.call({
        'id': '1',
        'action': 'authenticate',
        'password': 'wrong',
      });
      expect(errorOf(response)['code'], 'licence_required');
      expect(server.failedLogins, 0);
      server.licensed = true;
      await (await Wire.open(server)).login();
    });

    test('login timeout closes with 4401', () async {
      final server = await startServer(authTimeout: seconds(0.2));
      final wire = await Wire.open(server);
      expect((await wire.receive())['event'], 'hello');
      expect(await wire.closeCode(), 4401);
    });

    test('no timeout after the login', () async {
      final server = await startServer(authTimeout: seconds(0.2));
      final wire = await Wire.open(server);
      await wire.login();
      await expectLater(wire.receive(0.5), throwsA(isA<TimeoutException>()));
      expect((await wire.call({'id': 1, 'action': 'ping'}))['ok'], isTrue);
    });

    test('large frame closes with 1009', () async {
      final server = await startServer();
      final wire = await Wire.open(server);
      await wire.login();
      wire.send({
        'id': 1,
        'action': 'messages.send',
        'to': anna,
        'body': 'x' * (64 * 1024),
      });
      expect(await wire.closeCode(), 1009);
      expect(server.sent, isEmpty);
    });

    test('binary frame closes with 4400', () async {
      final wire = await Wire.open(await startServer());
      await wire.login();
      wire.socket.add([0, 1]);
      expect(await wire.closeCode(), 4400);
    });

    test('ninth client closes with 4409', () async {
      final server = await startServer();
      for (var index = 0; index < 8; index += 1) {
        await (await Wire.open(server)).login();
      }
      expect(await (await Wire.open(server)).closeCode(), 4409);
      expect(server.clientCount, 8);
    });

    test('other paths do not exist', () async {
      final server = await startServer();
      await expectLater(
        Wire.open(server, '/other'),
        throwsA(isA<WebSocketException>()),
      );
      expect(server.connectionCount, 0);
    });

    for (final frame in ['not JSON', '[1]', '"text"', '12']) {
      test('invalid JSON: $frame', () async {
        final wire = await Wire.open(await startServer());
        await wire.login();
        wire.socket.add(frame);
        final response = await wire.receive();
        expect(response['ok'], isFalse);
        expect(errorOf(response)['code'], 'invalid_json');
        expect((await wire.call({'id': 1, 'action': 'ping'}))['ok'], isTrue);
      });
    }

    test('errors', () async {
      final wire = await Wire.open(
        await startServer(requestsPerSecond: 1000),
      );
      await wire.login();
      final cases = <(JsonObject, String)>[
        ({'action': 'unknown'}, 'unknown_action'),
        ({}, 'invalid_request'),
        ({'action': 'messages.list', 'limit': 0}, 'invalid_request'),
        ({'action': 'messages.list', 'limit': 201}, 'invalid_request'),
        ({'action': 'messages.list', 'limit': '10'}, 'invalid_request'),
        ({'action': 'messages.list', 'direction': 'up'}, 'invalid_request'),
        ({'action': 'messages.list', 'cursor': 'nonsense'}, 'invalid_request'),
        ({'action': 'messages.get'}, 'invalid_request'),
        ({'action': 'messages.get', 'messageId': '2813'}, 'invalid_request'),
        ({'action': 'messages.get', 'messageId': 1}, 'not_found'),
        ({'action': 'messages.delete'}, 'invalid_request'),
        ({'action': 'conversations.delete'}, 'invalid_request'),
        ({'action': 'conversations.delete', 'conversationId': 1}, 'not_found'),
        ({'action': 'conversations.markRead'}, 'invalid_request'),
        (
          {'action': 'conversations.markRead', 'conversationId': 170},
          'not_default_app',
        ),
        ({'action': 'contacts.list', 'cursor': 'x'}, 'invalid_request'),
        ({'action': 'messages.send', 'to': anna}, 'invalid_request'),
        (
          {
            'action': 'messages.send',
            'to': anna,
            'body': 'x',
            'requestId': 'a b'
          },
          'invalid_request',
        ),
        ({'action': 'messages.status', 'requestId': 'none'}, 'not_found'),
        ({'action': 'subscribe', 'events': 'sms.received'}, 'invalid_request'),
        ({'action': 'subscribe'}, 'invalid_request'),
      ];
      for (final (request, code) in cases) {
        final response = await wire.call(request);
        expect(response['ok'], isFalse, reason: '$request');
        expect(errorOf(response)['code'], code, reason: '$request');
        expect(errorOf(response)['message'], isA<String>());
      }
    });

    test('request without id', () async {
      final wire = await Wire.open(await startServer());
      await wire.login();
      var response = await wire.call({'action': 'ping'});
      expect(response['id'], isNull);
      expect(response['ok'], isTrue);
      // The field "id" names the request only, never a message.
      response = await wire.call({'id': 2801, 'action': 'messages.get'});
      expect(response['id'], 2801);
      expect(errorOf(response)['code'], 'invalid_request');
    });

    test('sequence numbers count all events', () async {
      final server = await startServer();
      final wire = await Wire.open(server);
      await wire.login();
      server.receiveSms(anna, 'not subscribed yet');
      final response = await wire.call({
        'id': 1,
        'action': 'subscribe',
        'events': ['sms.received'],
      });
      expect(response['ok'], isTrue);
      server
        ..emit('conversations.changed', {})
        ..receiveSms(anna, 'third event');
      final event = await wire.receive();
      expect(event['event'], 'sms.received');
      expect(event['seq'], 3);
    });

    test('stopping closes with 1001', () async {
      final server = await startServer();
      final wire = await Wire.open(server);
      await wire.login();
      await server.stop();
      expect(await wire.closeCode(), 1001);
    });

    test('restart changes the epoch', () async {
      final server = await startServer();
      var wire = await Wire.open(server);
      await wire.login();
      server.receiveSms(anna, 'before');
      final first = server.epoch;
      server.restart();
      await expectLater(wire.receive(), throwsA(isA<StateError>()));
      wire = await Wire.open(server);
      await wire.receive();
      final response = await wire.call({
        'id': 1,
        'action': 'authenticate',
        'password': password,
      });
      final result = response['result'] as JsonObject;
      expect(result['epoch'], server.epoch);
      expect(result['epoch'], isNot(first));
      expect(result['latestSeq'], 0);
      final subscribed = await wire.call({
        'id': 2,
        'action': 'subscribe',
        'events': ['*'],
      });
      expect(subscribed['ok'], isTrue);
      server.receiveSms(anna, 'after');
      expect((await wire.receive())['seq'], 1);
    });

    test('rate limit', () async {
      final server = await startServer(requestsPerSecond: 5);
      final wire = await Wire.open(server);
      await wire.login();
      for (var number = 0; number < 8; number += 1) {
        wire.send({'id': number, 'action': 'ping'});
      }
      final responses = [
        for (var number = 0; number < 8; number += 1) await wire.receive(),
      ];
      expect([for (final r in responses) r['id']], [0, 1, 2, 3, 4, 5, 6, 7]);
      // The login was the first request of this second.
      expect(
        [for (final r in responses) r['ok']],
        [true, true, true, true, false, false, false, false],
      );
      expect(responses[4]['error'], {
        'code': 'rate_limited',
        'message': 'Too many requests.',
        'retryAfter': 1,
      });
      expect(server.refusedCount, 4);
      expect(countOf(server, 'ping'), 4);
      // Frames that cannot be read count as well and get the id null.
      wire.socket.add('not JSON');
      final response = await wire.receive();
      expect(response['id'], isNull);
      expect(errorOf(response)['code'], 'rate_limited');
      await Future<void>.delayed(seconds(1.1));
      expect((await wire.call({'id': 9, 'action': 'ping'}))['ok'], isTrue);
    });

    test('flooding closes with 4429', () async {
      final server = await startServer(requestsPerSecond: 3, maxRefused: 5);
      final wire = await Wire.open(server);
      await wire.login();
      for (var number = 0; number < 2 + 6; number += 1) {
        wire.send({'id': number, 'action': 'ping'});
      }
      expect(await wire.closeCode(), 4429);
      // 5 refused requests in a row are tolerated, the 6th is one too
      // many.
      expect(server.refusedCount, 6);
    });

    test('subscription lists names instead of the star', () async {
      final wire = await Wire.open(await startServer());
      await wire.login();
      var response = await wire.call({
        'id': 1,
        'action': 'subscribe',
        'events': ['*'],
        'address': '0699 1234567',
      });
      expect(response['result'], {
        'events': eventNames,
        'address': '0699 1234567',
      });
      response = await wire.call({
        'id': 2,
        'action': 'subscribe',
        'events': ['sms.sent'],
        'address': null,
      });
      expect(response['result'], {'events': eventNames, 'address': null});
      response = await wire.call({
        'id': 3,
        'action': 'unsubscribe',
        'events': ['*'],
      });
      expect(response['result'], {'events': <String>[], 'address': null});
    });

    test('seeded data', () async {
      final server = FakeSmsDock(tls: tlsFiles!)..seedDemoData();
      expect(server.messages, hasLength(7));
      expect(server.contacts, hasLength(2));
      expect(server.messages.last['address'], '+436641234567');
      expect(server.messages.first['contact'], 'Anna Example');
    });
  });

  group('helpers', () {
    const cases = [
      ('+43 699 1234567', '0043 699 1234567', true),
      ('+43 699 1234567', '0699 1234567', true),
      ('0043 699 1234567', '0699 1234567', true),
      ('0699 1234567', '+43 699 1234567', true),
      ('0699/12 34-567', '(0699) 12.34.567', true),
      ('0699 1234567', '699 1234567', true),
      ('1234567', '+43 699 1234567', true),
      ('234567', '+43 699 1234567', false),
      ('0699 1234567', '0699 1234568', false),
      ('+43 699 1234567', '+49 699 1234567', false),
      ('+43 699 1234567', '+43 699 12345678', false),
      ('8810', '8810', true),
      ('8810', '08810', false),
      ('8810', '+43 699 1238810', false),
      ('MyBank', 'mybank', true),
      (' MyBank ', 'MYBANK', true),
      ('MyBank', 'MyBank2', false),
      ('A1', '+431', false),
    ];
    for (final (first, second, same) in cases) {
      test('samePeer("$first", "$second") is $same', () {
        expect(samePeer(first, second), same);
        expect(samePeer(second, first), same);
      });
    }

    test('numbers and parts', () {
      expect(normalizeNumber('+43 (699) 123-456.78'), '+4369912345678');
      expect(normalizeNumber(' 0699/123 456 78 '), '069912345678');
      expect(countParts('x' * 160), 1);
      expect(countParts('x' * 161), 2);
      expect(countParts('x' * 1530), 10);
      expect(countParts('ü' * 70), 1);
      expect(countParts('ü' * 71), 2);
      expect(countParts('ü' * 670), 10);
      expect(countParts('ü' * 671), 11);
    });
  });
}
