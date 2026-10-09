// --------------------------------------------------------------------------
// Filename: cli_test.dart
//
// Description: Tests of the command line smsdock.
// --------------------------------------------------------------------------

/// Tests of the command line `smsdock`.
library;

import 'dart:async';
import 'dart:convert';
import 'dart:io';

import 'package:smsdock/src/cli.dart';
import 'package:test/test.dart';

import 'fake_server.dart';
import 'helpers.dart';

const String warning = 'smsdock: WARNING:';

/// Runs the command line; returns exit code, output and errors.
///
/// The password is set and no fingerprint, unless [environment] says
/// otherwise.
Future<(int, String, String)> run(
  List<String> argv, {
  Map<String, String>? environment,
  String input = '',
  bool terminal = false,
  String? typed,
  Stream<void>? interrupts,
}) async {
  final out = StringBuffer();
  final err = StringBuffer();
  final code = await runCli(
    argv,
    io: CliIo(
      out: out,
      err: err,
      environment: environment ?? {'SMSDOCK_PASSWORD': password},
      inputIsTerminal: () => terminal,
      readInput: () async => input,
      readPassword: (prompt) async => typed,
      interrupts: () => interrupts ?? const Stream.empty(),
    ),
  );
  return (code, '$out', '$err');
}

/// Adds URL and fingerprint of a server to a command.
List<String> Function(String, [List<String>]) arguments(FakeSmsDock server) =>
    (command, [options = const []]) => [
          command,
          server.url,
          '--fingerprint',
          server.tls.fingerprint,
          '--timeout',
          '5',
          ...options,
        ];

/// Lets messages arrive as soon as a client has subscribed.
Future<void> incomingLater(
  FakeSmsDock server,
  List<(String, String)> messages,
) async {
  if (await waitUntil(() => countOf(server, 'subscribe') > 0, seconds(5))) {
    for (final (address, body) in messages) {
      server.receiveSms(address, body);
    }
  }
}

Object? decode(String text) => jsonDecode(text);

const String statusHelp = '''
usage: smsdock status [-h] [--fingerprint HEX] [--insecure]
                      [--password-env NAME] [--timeout SECONDS]
                      [--reconnect-attempts N] [--json]
                      url

positional arguments:
  url                   wss:// URL shown by the app, e.g.
                        wss://192.168.1.50:9443/ws

options:
  -h, --help            show this help message and exit
  --fingerprint HEX     SHA-256 fingerprint of the certificate as shown by the
                        app (default: environment variable
                        SMSDOCK_FINGERPRINT)
  --insecure            do not check the certificate (for tests only)
  --password-env NAME   environment variable with the password (default:
                        SMSDOCK_PASSWORD)
  --timeout SECONDS     seconds to wait for a response (default: 30)
  --reconnect-attempts N
                        reconnect attempts after a lost connection; -1 means
                        without limit (default: 5)
  --json                print JSON instead of text
''';

const String mainUsage = 'usage: smsdock [-h]\n'
    '               {fingerprint,status,conversations,messages,send,delete,'
    'mark-read,contacts,listen} ...\n';

const String deleteUsage = '''
usage: smsdock delete [-h] [--fingerprint HEX] [--insecure]
                      [--password-env NAME] [--timeout SECONDS]
                      [--reconnect-attempts N] [--json] (--message ID |
                      --conversation ID)
                      url
''';

void main() {
  prepareTls();

  // --------------------------------------------------------------------
  // Arguments
  // --------------------------------------------------------------------

  group('arguments', () {
    test('parser reads all flags', () {
      final args = parseCommandLine([
        'messages', url, '--fingerprint', fingerprint, '--address', anna, //
        '--direction', 'in', '--search', 'invoice', '--limit', '10', //
        '--unread', '--sim', '2', '--json', '--timeout', '7', //
        '--since', '1790672531000', '--until', '2026-09-29T08:00', //
        '--conversation', '179', '--reconnect-attempts', '2',
      ], environment: {});
      expect(args.command, 'messages');
      expect(args['url'], url);
      expect(args['fingerprint'], fingerprint);
      expect(args['address'], anna);
      expect(args['direction'], 'in');
      expect(args['search'], 'invoice');
      expect(args['limit'], 10);
      expect(args['unread'], isTrue);
      expect(args['sim'], 2);
      expect(args['json'], isTrue);
      expect(args['timeout'], 7.0);
      expect(args.seconds('timeout'), seconds(7));
      expect(args['since'], 1790672531000);
      expect(args['until'], greaterThan(1700000000000));
      expect(args['conversation'], 179);
      expect(args['reconnect-attempts'], 2);
      expect(args['insecure'], isFalse);
    });

    test('parser defaults', () {
      final args = parseCommandLine(['messages', url], environment: {});
      expect(args['direction'], 'all');
      expect(args['limit'], 50);
      expect(args['timeout'], 30.0);
      expect(args['reconnect-attempts'], 5);
      expect(args['json'], isFalse);
      expect(args['unread'], isFalse);
      expect(args['insecure'], isFalse);
      expect(args['fingerprint'], isNull);
      expect(args['password-env'], 'SMSDOCK_PASSWORD');
      final listen = parseCommandLine(['listen', url], environment: {});
      expect(listen['events'], ['sms.received']);
      expect(listen['address'], isNull);
      expect(listen['count'], isNull);
      final send = parseCommandLine([
        'send',
        url,
        '--to',
        anna,
      ], environment: {});
      expect(send['text'], isNull);
      expect(send['wait'], isFalse);
      expect(send['wait-timeout'], 60.0);
    });

    test('parser reads values as argparse does', () {
      Arguments parse(List<String> argv) =>
          parseCommandLine(argv, environment: {});
      expect(
        parse(['status', url, '--reconnect-attempts', '-1'])[
            'reconnect-attempts'],
        -1,
      );
      expect(parse(['status', '--timeout=2.5', url])['timeout'], 2.5);
      expect(
        parse(['listen', url, '--events', 'sms.sent', '*', '--json'])['events'],
        ['sms.sent', '*'],
      );
      expect(
          parse(['send', url, '--to', anna, '--text', 'a b'])['text'], 'a b');
      expect(parse(['messages', url, '--limit', ' 7 '])['limit'], 7);
      expect(parse(['status', '--', url])['url'], url);
      final local = DateTime(2026, 9, 29, 8).millisecondsSinceEpoch;
      expect(parse(['messages', url, '--since', '2026-09-29T08:00'])['since'],
          local);
      expect(
        parse(['messages', url, '--since', '2026-09-29'])['since'],
        DateTime(2026, 9, 29).millisecondsSinceEpoch,
      );
      expect(
        parse(['messages', url, '--since', '2026-09-29T08:00:00Z'])['since'],
        DateTime.utc(2026, 9, 29, 8).millisecondsSinceEpoch,
      );
      expect(
        parse(['messages', url, '--since', '2026-09-29T08:00+02:00'])['since'],
        DateTime.utc(2026, 9, 29, 6).millisecondsSinceEpoch,
      );
    });

    test('fingerprint from the environment', () {
      final args = parseCommandLine([
        'status',
        url,
      ], environment: {
        'SMSDOCK_FINGERPRINT': fingerprint
      });
      expect(args['fingerprint'], fingerprint);
    });

    for (final argv in [
      <String>[],
      ['unknown'],
      ['status'],
      ['status', url, '--password', 'secret123'],
      ['messages', url, '--direction', 'sideways'],
      ['messages', url, '--limit', '0'],
      ['messages', url, '--limit', 'many'],
      ['messages', url, '--since', 'yesterday'],
      ['messages', url, '--since', '2026-02-30'],
      ['send', url, '--text', 'no recipient'],
      ['delete', url],
      ['delete', url, '--message', '1', '--conversation', '2'],
      ['mark-read', url],
      ['mark-read', url, '--conversation', 'first'],
      ['listen', url, '--events', 'sms.unknown'],
      ['listen', url, '--events'],
      ['status', url, '--timeout', 'soon'],
      ['status', url, '--json=1'],
      ['fingerprint'],
    ]) {
      test('usage error: ${argv.join(' ')}', () async {
        final (code, out, err) = await run(argv);
        expect(code, 2);
        expect(out, isEmpty);
        expect(err, startsWith('usage:'));
      });
    }

    test('usage errors are worded like those of Python', () async {
      var (_, _, err) = await run([]);
      expect(
        err,
        '${mainUsage}smsdock: error: the following arguments are required: '
        'command\n',
      );
      (_, _, err) = await run(['unknown']);
      expect(
        err,
        '${mainUsage}smsdock: error: argument command: invalid choice: '
        "'unknown' (choose from 'fingerprint', 'status', 'conversations', "
        "'messages', 'send', 'delete', 'mark-read', 'contacts', 'listen')\n",
      );
      (_, _, err) = await run(['status', url, 'extra', '--more']);
      expect(
        err,
        '${mainUsage}smsdock: error: unrecognized arguments: extra --more\n',
      );
      (_, _, err) = await run(['delete', url]);
      expect(
        err,
        '${deleteUsage}smsdock delete: error: one of the arguments '
        '--message --conversation is required\n',
      );
      (_, _, err) = await run([
        'delete', url, '--message', '1', '--conversation', '2', //
      ]);
      expect(
        err,
        '${deleteUsage}smsdock delete: error: argument --conversation: '
        'not allowed with argument --message\n',
      );
      (_, _, err) = await run(['delete', url, '--message', 'x']);
      expect(
        err,
        '${deleteUsage}smsdock delete: error: argument --message: '
        "invalid int value: 'x'\n",
      );
      (_, _, err) = await run(['delete', '--message', '1']);
      expect(
        err,
        '${deleteUsage}smsdock delete: error: the following arguments are '
        'required: url\n',
      );
    });

    test('help', () async {
      var (code, out, err) = await run(['--help']);
      expect(code, 0);
      expect(err, isEmpty);
      expect(out, startsWith(mainUsage));
      for (final command in [
        'fingerprint', 'status', 'conversations', 'messages', 'send', //
        'delete', 'mark-read', 'contacts', 'listen',
      ]) {
        expect(out, contains('    $command '));
      }
      expect(out, contains('SMSDOCK_PASSWORD'));
      (code, out, _) = await run(['send', '--help']);
      expect(code, 0);
      expect(out, isNot(contains('--password ')));
      expect(out, contains('--wait-timeout SECONDS'));
      (code, out, _) = await run(['status', '-h']);
      expect((code, out), (0, statusHelp));
      for (final line in out.split('\n')) {
        expect(line.length, lessThanOrEqualTo(78));
      }
    });

    test('the password is no argument', () async {
      final (code, _, err) = await run([
        'status', url, '--fingerprint', fingerprint, '--password', password, //
      ]);
      expect(code, 2);
      expect(err, contains('unrecognized arguments'));
    });

    for (final address in [
      'ws://192.168.1.50:9443/ws',
      'https://192.168.1.50:8443/',
      '192.168.1.50',
    ]) {
      test('refuses the URL $address', () async {
        final (code, out, err) = await run([
          'status',
          address,
          '--fingerprint',
          fingerprint,
        ]);
        expect(code, 2);
        expect(err, contains('wss://'));
        expect(out, isEmpty);
      });
    }

    test('needs a fingerprint', () async {
      final (code, _, err) = await run(['status', url]);
      expect(code, 2);
      expect(
        err,
        'smsdock: No certificate fingerprint. Pass --fingerprint or set '
        "SMSDOCK_FINGERPRINT. 'smsdock fingerprint HOST' prints the "
        'fingerprint of a server.\n',
      );
    });

    test('refuses a malformed fingerprint', () async {
      final (code, _, err) = await run([
        'status',
        url,
        '--fingerprint',
        'AB:CD',
      ]);
      expect(code, 2);
      expect(
        err,
        'smsdock: The fingerprint must be the SHA-256 of the certificate: '
        '64 hex digits, with or without colons\n',
      );
    });

    test('needs a password', () async {
      final (code, _, err) = await run([
        'status',
        url,
        '--fingerprint',
        fingerprint,
      ], environment: {});
      expect(code, 2);
      expect(
        err,
        'smsdock: No password. Set the environment variable '
        'SMSDOCK_PASSWORD.\n',
      );
    });

    test('an empty answer on the terminal is no password', () async {
      final (code, _, err) = await run(
        ['status', url, '--fingerprint', fingerprint],
        environment: {},
        terminal: true,
        typed: '',
      );
      expect(code, 2);
      expect(err, contains('No password'));
    });

    test('refuses impossible numbers', () async {
      var (code, _, err) = await run([
        'status', url, '--insecure', '--reconnect-attempts', '-2', //
      ]);
      expect(code, 2);
      expect(err, 'smsdock: --reconnect-attempts cannot be less than -1\n');
      (code, _, err) = await run([
        'status', url, '--insecure', '--timeout', '0', //
      ]);
      expect(code, 2);
      expect(err, 'smsdock: Timeouts must be positive\n');
    });

    const hosts = {
      '192.168.1.50': ('192.168.1.50', 9443),
      '192.168.1.50:9555': ('192.168.1.50', 9555),
      'phone.local:9443': ('phone.local', 9443),
      'wss://192.168.1.50:9555/ws': ('192.168.1.50', 9555),
      'wss://192.168.1.50/ws': ('192.168.1.50', 9443),
      '[fe80::1]:9555': ('fe80::1', 9555),
      'fe80::1': ('fe80::1', 9443),
    };
    test('host and port', () {
      for (final MapEntry(key: text, value: expected) in hosts.entries) {
        expect(hostAndPort(text), expected, reason: text);
      }
      for (final text in ['', ':9443', '192.168.1.50:port', 'host:99999']) {
        expect(
          () => hostAndPort(text),
          throwsA(isA<UsageError>()),
          reason: text,
        );
      }
    });

    test('an event as a line of text', () {
      final now = DateTime(2026, 9, 29, 12, 55, 30);
      expect(
        describe({
          'event': 'sms.received',
          'seq': 1,
          'data': {
            'message': {
              'address': anna,
              'contact': 'Anna Example',
              'body': 'STATUS',
              'timestamp': now.millisecondsSinceEpoch,
            },
          },
        }),
        '2026-09-29 12:55:30  $anna (Anna Example): STATUS',
      );
      expect(
        describe({
          'event': 'sms.failed',
          'data': {
            'requestId': 'r1',
            'id': null,
            'to': ben,
            'errorCode': 1,
            'ok': false,
            'nested': {'a': 1},
          },
        }, now: now),
        '2026-09-29 12:55:30  sms.failed requestId=r1 id=None to=$ben '
        'errorCode=1 ok=False',
      );
      expect(
        describe({
          'event': 'sms.sent',
          'data': {
            'requestId': null,
            'message': {'address': ben, 'body': 'Hi'},
          },
        }, now: now),
        '2026-09-29 12:55:30  sent to $ben: Hi',
      );
      expect(
        describe({'event': 'conversations.changed', 'data': {}}, now: now),
        '2026-09-29 12:55:30  conversations.changed',
      );
      expect(
        spacedJson({
          'a': [1, 2.5, null, true],
          'b': {'c': 'Grüße "x"\n'},
        }),
        r'{"a": [1, 2.5, null, true], "b": {"c": "Grüße \"x\"\n"}}',
      );
    });
  });

  // --------------------------------------------------------------------
  // Commands
  // --------------------------------------------------------------------

  group('commands', skip: needTls, () {
    test('fingerprint', () async {
      final server = await startServer();
      var (code, out, err) = await run([
        'fingerprint',
        '127.0.0.1:${server.port}',
      ]);
      expect(code, 0);
      expect(out, '${server.tls.fingerprint}\n');
      expect(
        err,
        'Compare this fingerprint with the Server screen of the app. '
        'Use it only if both are the same.\n',
      );
      (code, out, _) = await run(['fingerprint', server.url]);
      expect((code, out), (0, '${server.tls.fingerprint}\n'));
      expect(server.requests, isEmpty);
      expect(server.connectionCount, 0);
    });

    test('fingerprint of nothing', () async {
      final server = await startServer();
      await server.stop();
      final (code, out, err) = await run([
        'fingerprint',
        '127.0.0.1:${server.port}',
        '--timeout',
        '2',
      ]);
      expect(code, 3);
      expect(out, isEmpty);
      expect(err, startsWith('smsdock: '));
    });

    test('status', () async {
      final server = await startServer();
      final (code, out, err) = await run(arguments(server)('status'));
      expect(code, 0);
      expect(
        out,
        'App:             SMS Dock 1.0.0 (protocol 1)\n'
        'Default SMS app: no\n'
        'Delete mode:     hidden\n'
        'Licence:         not enforced\n'
        'Permissions:     all granted\n'
        '\n'
        'SIM  SLOT  NAME  NUMBER          DEFAULT\n'
        '1    0     Play                  yes\n'
        '2    1     Work  +4369087654321  no\n',
      );
      expect(err, isEmpty);
    });

    test('status of the free version', () async {
      final server = await startServer();
      server
        ..licenceEnforced = true
        ..licensed = true;
      var (_, out, _) = await run(arguments(server)('status'));
      expect(out, contains('Licence:         full licence\n'));
      server.answerNext('status', {
        'app': {'name': 'SMS Dock', 'version': '1.0.0', 'protocolVersion': 1},
        'sims': <Object>[],
        'permissions': {'readSms': true, 'sendSms': false, 'contacts': false},
        'defaultSmsApp': true,
        'deleteMode': 'provider',
        'licence': {
          'enforced': true,
          'licensed': false,
          'dailyLimit': 10,
          'sentToday': 3,
        },
      });
      (_, out, _) = await run(arguments(server)('status'));
      expect(
        out,
        'App:             SMS Dock 1.0.0 (protocol 1)\n'
        'Default SMS app: yes\n'
        'Delete mode:     provider\n'
        'Licence:         free version, 3 of 10 SMS sent today\n'
        'Permissions:     missing: contacts, sendSms\n',
      );
    });

    test('status json', () async {
      final server = await startServer();
      final (code, out, _) = await run(arguments(server)('status', ['--json']));
      expect(code, 0);
      final status = decode(out)! as JsonObject;
      expect((status['app'] as JsonObject)['protocolVersion'], 1);
      expect(status['sims'], hasLength(2));
      expect(
        out,
        startsWith(
          '{\n'
          '  "app": {\n'
          '    "name": "SMS Dock",\n'
          '    "version": "1.0.0",\n'
          '    "protocolVersion": 1\n'
          '  },\n'
          '  "sims": [\n'
          '    {\n'
          '      "subscriptionId": 1,\n',
        ),
      );
      expect(out, endsWith('  "popup": true\n}\n'));
    });

    test('conversations', () async {
      final server = await startServer();
      final build = arguments(server);
      var (code, out, _) = await run(build('conversations'));
      expect(code, 0);
      final lines = const LineSplitter().convert(out);
      expect(lines[0].split(RegExp(r'\s+')), [
        'ID', 'TIME', 'NUMBER', 'NAME', 'MSGS', 'UNREAD', 'DIR', 'TEXT', //
      ]);
      expect(lines, hasLength(4));
      expect(lines[1], contains(anna));
      expect(lines[1], contains('Anna Example'));
      expect(lines[1], endsWith('Anna 24'));
      expect(lines[3], contains('A1'));
      // The columns are as wide as their longest cell.
      expect(
          lines[3], matches(RegExp(r'^172  \d{4}-\d\d-\d\d \d\d:\d\d  A1  ')));
      expect(lines[3],
          endsWith('            1     1       in   Your bill is ready'));

      (code, out, _) = await run(
        build('conversations', [
          '--json', '--unread', '--search', 'bill', '--limit', '5', //
        ]),
      );
      expect(code, 0);
      expect(addressesOf(out), ['A1']);
      (_, out, _) = await run(build('conversations', ['--sim', '2', '--json']));
      expect(addressesOf(out), [ben]);
    });

    test('last ten incoming messages', () async {
      final server = await startServer();
      final (code, out, _) = await run(
        arguments(server)('messages', ['--direction', 'in', '--limit', '10']),
      );
      expect(code, 0);
      final lines = const LineSplitter().convert(out);
      expect(lines[0].split(RegExp(r'\s+')), [
        'ID', 'TIME', 'DIR', 'NUMBER', 'NAME', 'STATUS', 'TEXT', //
      ]);
      expect(lines, hasLength(11));
      expect(lines.skip(1).every((line) => line.contains(' in ')), isTrue);
      expect(lines[1], endsWith('received  Anna 24'));
      expect(lines[10], endsWith('received  Anna 6'));
    });

    test('messages json and filters', () async {
      final server = await startServer();
      final build = arguments(server);
      var (code, out, _) = await run(
        build('messages', [
          '--json', '--address', ben, '--search', 'invoice', //
          '--direction', 'out', '--sim', '2',
        ]),
      );
      expect(code, 0);
      expect(bodiesOf(out), ['Your invoice is attached']);
      final request = lastRequest(server, 'messages.list');
      expect(request['address'], ben);
      expect(request['q'], 'invoice');
      expect(request['direction'], 'out');
      expect(request['sim'], 2);

      (_, out, _) = await run(build('messages', ['--json', '--unread']));
      expect(decode(out), hasLength(4));
      (_, out, _) = await run(
        build('messages', ['--json', '--since', '1790672553000']),
      );
      expect(bodiesOf(out), ['Anna 24', 'Anna 23', 'Anna 22']);
      (_, out, _) = await run(
        build('messages', ['--json', '--conversation', '171', '--limit', '1']),
      );
      expect(
        out,
        '[\n'
        '  {\n'
        '    "id": 2828,\n'
        '    "conversationId": 171,\n'
        '    "address": "+4369087654321",\n'
        '    "contact": "Ben Example",\n'
        '    "direction": "in",\n'
        '    "body": "See you",\n'
        '    "timestamp": 1790672528000,\n'
        '    "sentTimestamp": 1790672526000,\n'
        '    "read": true,\n'
        '    "status": "received",\n'
        '    "sim": 2,\n'
        '    "requestId": null\n'
        '  }\n'
        ']\n',
      );
    });

    test('messages limit over one page', () async {
      final server = await startServer();
      for (var index = 0; index < 230; index += 1) {
        server.addMessage(anna, 'More $index');
      }
      final (code, out, _) = await run(
        arguments(server)('messages', ['--json', '--limit', '220']),
      );
      expect(code, 0);
      expect(decode(out), hasLength(220));
      expect(
        [for (final r in requestsOf(server, 'messages.list')) r['limit']],
        [200, 20],
      );
    });

    test('nothing found', () async {
      final server = await startServer();
      final build = arguments(server);
      var (code, out, _) = await run(
        build('messages', ['--search', 'no such text']),
      );
      expect((code, out), (0, 'Nothing found.\n'));
      (code, out, _) = await run(
        build('messages', ['--search', 'no such text', '--json']),
      );
      expect((code, out), (0, '[]\n'));
    });

    test('long text is cut in tables', () async {
      final server = await startServer();
      server.addMessage(anna, 'First line\nsecond  line ${'ü' * 100}');
      final build = arguments(server);
      var (code, out, _) = await run(build('messages', ['--limit', '1']));
      expect(code, 0);
      final line = const LineSplitter().convert(out)[1];
      expect(
        line,
        endsWith('  First line second line ${'ü' * 36}…'),
      );
      (code, out, _) = await run(build('messages', ['--limit', '1', '--json']));
      expect(bodiesOf(out).single, startsWith('First line\nsecond  line'));
    });

    test('send', () async {
      final server = await startServer();
      final (code, out, _) = await run(
        arguments(server)(
            'send', ['--to', ben, '--text', 'All systems running.']),
      );
      expect(code, 0);
      final requestId = server.sent[0]['requestId'];
      expect(out, 'Queued: $ben, 1 part(s), requestId $requestId\n');
      expect(server.sent[0]['body'], 'All systems running.');
    });

    test('send from standard input', () async {
      final server = await startServer();
      final (code, out, _) = await run(
        arguments(server)('send', [
          '--to', ben, '--sim', '2', '--request-id', 'cron-1', '--json', //
        ]),
        input: 'Line one\nLine two\n',
      );
      expect(code, 0);
      expect(
        out,
        '{\n'
        '  "requestId": "cron-1",\n'
        '  "to": "$ben",\n'
        '  "parts": 1,\n'
        '  "status": "queued"\n'
        '}\n',
      );
      expect(server.sent, [
        {
          'requestId': 'cron-1',
          'to': ben,
          'body': 'Line one\nLine two',
          'sim': 2,
        },
      ]);
    });

    test('send needs a text', () async {
      final server = await startServer();
      final build = arguments(server);
      var (code, _, err) = await run(build('send', ['--to', ben]));
      expect(code, 2);
      expect(err, 'smsdock: The text is empty.\n');
      (code, _, err) = await run(build('send', ['--to', ben]), terminal: true);
      expect(code, 2);
      expect(
        err,
        'smsdock: No text. Pass --text or write it to standard input.\n',
      );
      expect(server.sent, isEmpty);
    });

    test('send and wait', () async {
      final server = await startServer();
      final build = arguments(server);
      var (code, out, _) = await run(
        build('send', ['--to', ben, '--text', 'Hello', '--wait']),
      );
      expect(code, 0);
      expect(out, startsWith('Sent: $ben, 1 part(s), requestId '));

      (code, out, _) = await run(
        build('send', ['--to', ben, '--text', 'Hello', '--wait', '--json']),
      );
      expect(code, 0);
      final entry = decode(out)! as JsonObject;
      expect(entry['status'], 'sent');
      expect(entry['partsSent'], 1);
      expect(entry.keys.toList(), [
        'requestId', 'to', 'parts', 'partsSent', 'status', 'errorCode', //
        'messageId', 'sim', 'source', 'createdAt',
      ]);
    });

    test('send and wait failed', () async {
      final server = await startServer();
      server.failNumbers.add('+43123456');
      final build = arguments(server);
      var (code, out, err) = await run(
        build('send', ['--to', '+43123456', '--text', 'Hello', '--wait']),
      );
      expect(code, 1);
      expect(out, isEmpty);
      final requestId = server.sent[0]['requestId'];
      expect(
        err,
        'smsdock: Sending to +43123456 failed (error code 1, '
        'requestId $requestId)\n',
      );

      (code, out, _) = await run(
        build('send', [
          '--to', '+43123456', '--text', 'Hello', '--wait', '--json', //
        ]),
      );
      expect(code, 1);
      expect((decode(out)! as JsonObject)['status'], 'failed');
    });

    test('send again tells the old outcome', () async {
      final server = await startServer();
      server.failNumbers.add('+43123456');
      final build = arguments(server);
      final command = build('send', [
        '--to', '+43123456', '--text', 'Hello', '--request-id', 'job-1', //
      ]);
      var (code, out, err) = await run(command);
      expect((
        code,
        out
      ), (
        0,
        'Queued: +43123456, 1 part(s), requestId job-1\n',
      ));
      expect(
        await waitUntil(() => server.outbox['job-1']!['status'] == 'failed'),
        isTrue,
      );
      server.failNumbers.clear();
      // The repeat sends nothing and reports the failure of the first
      // attempt.
      (code, out, err) = await run(command);
      expect((code, out), (1, ''));
      expect(err, 'smsdock: Sending to +43123456 failed (requestId job-1)\n');
      expect(server.sent, hasLength(1));

      final ok = build('send', [
        '--to', ben, '--text', 'Hello', '--request-id', 'job-2', //
      ]);
      (code, _, _) = await run([...ok, '--wait']);
      expect(code, 0);
      (code, out, _) = await run(ok);
      expect((code, out), (0, 'Sent: $ben, 1 part(s), requestId job-2\n'));
      expect(server.sent, hasLength(2));
    });

    test('send and wait times out', () async {
      final server = await startServer(sendDelay: seconds(30));
      final (code, _, err) = await run([
        'send', server.url, '--fingerprint', server.tls.fingerprint, //
        '--to', ben, '--text', 'Hello', '--wait', '--wait-timeout', '0.3',
      ]);
      expect(code, 3);
      final requestId = server.sent[0]['requestId'];
      expect(
        err,
        "smsdock: No result for the message '$requestId' in time; "
        "its status is 'queued'\n",
      );
    });

    test('delete', () async {
      final server = await startServer();
      final build = arguments(server);
      final newest = server.messages.reduce(
        (a, b) => (a['timestamp'] as int) > (b['timestamp'] as int) ? a : b,
      );
      var (code, out, _) = await run(
        build('delete', ['--message', '${newest['id']}']),
      );
      expect(code, 0);
      expect(
        out,
        'Deleted 1 message(s): hidden in SMS Dock, still on the phone\n',
      );
      expect(server.messages, isNot(contains(newest)));

      server.deleteMode = 'provider';
      var conversation = server.messages[0]['conversationId'];
      (code, out, _) = await run(
        build('delete', ['--conversation', '$conversation']),
      );
      expect(code, 0);
      expect(out, 'Deleted 24 message(s): removed from the phone\n');
      conversation = server.messages[0]['conversationId'];
      (code, out, _) = await run(
        build('delete', ['--conversation', '$conversation', '--json']),
      );
      expect(out, '{\n  "deleted": 3,\n  "mode": "provider"\n}\n');
    });

    test('mark read', () async {
      final server = await startServer();
      final build = arguments(server);
      final conversation = '${server.messages[0]['conversationId']}';
      var (code, out, err) = await run(
        build('mark-read', ['--conversation', conversation]),
      );
      expect((code, out), (1, ''));
      expect(
        err,
        'smsdock: not_default_app: Only the default SMS app can mark '
        'messages as read.\n',
      );

      server.deleteMode = 'provider';
      (code, out, _) = await run(
        build('mark-read', ['--conversation', conversation]),
      );
      expect((code, out), (0, 'Marked conversation $conversation as read\n'));
      expect(
        server.messages
            .where((m) => '${m['conversationId']}' == conversation)
            .every((m) => m['read'] == true),
        isTrue,
      );
      (code, out, _) = await run(
        build('mark-read', ['--conversation', conversation, '--json']),
      );
      expect((code, out), (0, '{\n  "ok": true\n}\n'));
    });

    test('contacts', () async {
      final server = await startServer();
      final build = arguments(server);
      var (code, out, _) = await run(build('contacts'));
      expect(code, 0);
      expect(
        out,
        'NAME          NUMBER          LABEL\n'
        'Anna Example  $anna  Mobile\n'
        'Ben Example   $ben  Work\n',
      );
      (code, out, _) = await run(
        build('contacts', ['--search', 'ben', '--json']),
      );
      expect(decode(out), [
        {
          'name': 'Ben Example',
          'numbers': [
            {'number': ben, 'label': 'Work'},
          ],
        },
      ]);
    });

    test('listen', () async {
      final server = await startServer();
      unawaited(
        incomingLater(server, [(anna, 'STATUS'), ('A1', 'Your bill')]),
      );
      final (code, out, err) = await run(
        arguments(server)('listen', ['--count', '2']),
      );
      expect(code, 0);
      final lines = const LineSplitter().convert(out);
      expect(lines, hasLength(2));
      expect(
        lines[0],
        matches(
          RegExp(
            r'^\d{4}-\d\d-\d\d \d\d:\d\d:\d\d  \+4369912345678 '
            r'\(Anna Example\): STATUS$',
          ),
        ),
      );
      expect(lines[1], endsWith('  A1: Your bill'));
      expect(err, 'Listening for sms.received. Press Ctrl+C to stop.\n');
      final request = lastRequest(server, 'subscribe');
      expect(request['events'], ['sms.received']);
      expect(request.containsKey('address'), isFalse);
    });

    test('listen json with address', () async {
      final server = await startServer();
      unawaited(incomingLater(server, [(anna, 'ignored'), (ben, 'wanted')]));
      final (code, out, err) = await run(
        arguments(server)('listen', [
          '--json', '--address', ben, '--count', '1', //
        ]),
      );
      expect(code, 0);
      expect(err, isEmpty);
      final event = decode(out)! as JsonObject;
      expect(event['event'], 'sms.received');
      final message = (event['data'] as JsonObject)['message'] as JsonObject;
      expect(message['body'], 'wanted');
      expect(const LineSplitter().convert(out), hasLength(1));
      // One line, written as Python writes JSON.
      expect(
        out,
        startsWith(
          '{"event": "sms.received", "seq": 2, "data": {"message": {"id": ',
        ),
      );
      expect(out, endsWith('"sim": 1, "requestId": null}}}\n'));
    });

    test('listen to other events', () async {
      final server = await startServer();
      unawaited(
        waitUntil(() => server.requests.length >= 2, seconds(5)).then((_) {
          server.emit('sms.delivered', {
            'requestId': 'r1',
            'id': 7,
            'conversationId': 171,
            'to': ben,
          });
        }),
      );
      final (code, out, err) = await run(
        arguments(server)('listen', [
          '--events', 'sms.delivered', 'sms.failed', '--count', '1', //
        ]),
      );
      expect(code, 0);
      expect(
        out,
        endsWith(
            '  sms.delivered requestId=r1 id=7 conversationId=171 to=$ben\n'),
      );
      expect(
        err,
        'Listening for sms.delivered, sms.failed. Press Ctrl+C to stop.\n',
      );
    });

    test('listen reports a reconnect', () async {
      final server = await startServer();
      Future<void> later() async {
        if (!await waitUntil(() => server.requests.length >= 2, seconds(5))) {
          return;
        }
        server.dropConnections();
        await waitUntil(() => server.clientCount == 0);
        server
          ..receiveSms(anna, 'lost')
          ..receiveSms(anna, 'lost too');
        if (await waitUntil(() => server.requests.length >= 4, seconds(5))) {
          server.restart();
        }
        if (await waitUntil(() => server.requests.length >= 6, seconds(5))) {
          server.receiveSms(anna, 'received');
        }
      }

      unawaited(later());
      final (code, out, err) = await run(
        arguments(server)('listen', ['--count', '1']),
      );
      expect(code, 0);
      expect(out, endsWith(': received\n'));
      final lines = const LineSplitter().convert(err);
      expect(
        lines[1],
        'smsdock: reconnected; 2 event(s) of the phone were not received',
      );
      expect(
        lines[2],
        'smsdock: reconnected; the app was restarted, events may be missing',
      );
    });

    test('listen stops with Ctrl+C', () async {
      final server = await startServer();
      final interrupts = StreamController<void>();
      addTearDown(interrupts.close);
      unawaited(
        waitUntil(
          () => countOf(server, 'subscribe') > 0,
        ).then((_) => interrupts.add(null)),
      );
      final (code, out, _) = await run(
        arguments(server)('listen'),
        interrupts: interrupts.stream,
      );
      expect(code, 130);
      expect(out, isEmpty);
      expect(await waitUntil(() => server.clientCount == 0), isTrue);
    });
  });

  // --------------------------------------------------------------------
  // Exit codes
  // --------------------------------------------------------------------

  group('exit codes', skip: needTls, () {
    test('asks for the password on a terminal', () async {
      final server = await startServer();
      final prompts = <String>[];
      final out = StringBuffer();
      final code = await runCli(
        arguments(server)('status', ['--json']),
        io: CliIo(
          out: out,
          err: StringBuffer(),
          environment: {},
          inputIsTerminal: () => true,
          readPassword: (prompt) async {
            prompts.add(prompt);
            return password;
          },
          interrupts: () => const Stream.empty(),
        ),
      );
      expect(code, 0);
      expect(prompts, ['SMS Dock password: ']);
      expect((decode('$out')! as JsonObject)['deleteMode'], 'hidden');
    });

    test('other password variable', () async {
      final server = await startServer();
      final (code, _, _) = await run(
        arguments(server)('status', ['--password-env', 'OFFICE_PHONE']),
        environment: {
          'SMSDOCK_PASSWORD': 'wrong-password',
          'OFFICE_PHONE': password,
        },
      );
      expect(code, 0);
    });

    test('exit code 1 for API errors', () async {
      final server = await startServer();
      final build = arguments(server);
      var (code, out, err) = await run(build('delete', ['--message', '1']));
      expect((code, out), (1, ''));
      expect(err, 'smsdock: not_found: No message with the id 1.\n');

      (code, _, err) = await run(
        build('send', ['--to', 'abc', '--text', 'x']),
      );
      expect(code, 1);
      expect(err, contains('invalid_number'));

      server.failNext(
        'messages.send',
        'quota_exceeded',
        message: 'Daily limit reached.',
      );
      (code, _, err) = await run(build('send', ['--to', ben, '--text', 'x']));
      expect(code, 1);
      expect(err, 'smsdock: quota_exceeded: Daily limit reached.\n');
    });

    test('exit code 1 for a wrong password', () async {
      final server = await startServer();
      final (code, out, err) = await run(
        arguments(server)('status'),
        environment: {'SMSDOCK_PASSWORD': 'wrong-password'},
      );
      expect((code, out), (1, ''));
      expect(err, 'smsdock: invalid_password: Wrong password.\n');
      expect(server.loginCount, 1);
    });

    test('exit code 1 without licence', () async {
      final server = await startServer();
      server.licenceEnforced = true;
      final (code, _, err) = await run(arguments(server)('status'));
      expect(code, 1);
      expect(err, contains('licence_required'));
    });

    test('exit code 3 when nothing listens', () async {
      final server = await startServer();
      await server.stop();
      final address = 'wss://127.0.0.1:${server.port}/ws';
      final (code, out, err) = await run([
        'status', address, '--fingerprint', fingerprint, //
        '--reconnect-attempts', '0',
      ]);
      expect((code, out), (3, ''));
      expect(err, startsWith('smsdock: Cannot connect to $address: '));
    });

    test('exit code 3 for another certificate', () async {
      final server = await startServer();
      final other = otherTlsFiles!.fingerprint;
      final (code, out, err) = await run([
        'status',
        server.url,
        '--fingerprint',
        other,
      ]);
      expect((code, out), (3, ''));
      expect(
        err,
        'smsdock: The certificate of the server does not match the pinned '
        'fingerprint.\n'
        '  expected: $other\n'
        '  received: ${server.tls.fingerprint}\n'
        'Compare the fingerprint with the Server screen of the app. It '
        'changes when the certificate is regenerated or the app is '
        'reinstalled.\n',
      );
      expect(server.requests, isEmpty);
    });

    test('fingerprint from the environment is used', () async {
      final server = await startServer();
      final (code, _, _) = await run(
        ['status', server.url],
        environment: {
          'SMSDOCK_PASSWORD': password,
          'SMSDOCK_FINGERPRINT': server.tls.fingerprint.toLowerCase(),
        },
      );
      expect(code, 0);
    });

    test('insecure warns', () async {
      final server = await startServer();
      final (code, out, err) = await run([
        'status', server.url, '--insecure', '--timeout', '5', //
      ]);
      expect(code, 0);
      expect(
        err,
        '$warning the certificate of the server is not checked (insecure). '
        'Anyone on the network can read the password and the messages.\n',
      );
      expect(out, contains('App:'));
    });

    test('the program can be run', () async {
      final server = await startServer();
      final dart = Platform.resolvedExecutable;
      final help = await Process.run(dart, ['bin/smsdock.dart', '--help']);
      expect(help.exitCode, 0);
      expect(help.stdout, startsWith('usage: smsdock'));
      final status = await Process.run(
        dart,
        ['bin/smsdock.dart', 'contacts', server.url, '--search', 'anna'],
        environment: {
          'SMSDOCK_PASSWORD': password,
          'SMSDOCK_FINGERPRINT': server.tls.fingerprint,
        },
      );
      expect(status.stderr, isEmpty);
      expect(
        status.stdout,
        'NAME          NUMBER          LABEL\n'
        'Anna Example  $anna  Mobile\n',
      );
      expect(status.exitCode, 0);
      final failed = await Process.run(dart, ['bin/smsdock.dart', 'status']);
      expect(failed.exitCode, 2);
      expect(failed.stdout, isEmpty);
      expect(failed.stderr, contains('required: url'));
    }, timeout: const Timeout(Duration(minutes: 2)));
  });
}

List<Object?> addressesOf(String json) => [
      for (final item in decode(json)! as List<dynamic>)
        (item as JsonObject)['address'],
    ];

List<String> bodiesOf(String json) => [
      for (final item in decode(json)! as List<dynamic>)
        (item as JsonObject)['body'] as String,
    ];
