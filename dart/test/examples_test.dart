// --------------------------------------------------------------------------
// Filename: examples_test.dart
//
// Description: Tests that run the examples against the fake server.
// --------------------------------------------------------------------------

/// The examples run on their own against the fake server.
library;

import 'dart:async';
import 'dart:convert';
import 'dart:io';

import 'package:test/test.dart';

import '../example/auto_reply.dart';
import 'fake_server.dart';
import 'helpers.dart';

/// Returns the environment of an example process.
Map<String, String> environment(TlsFiles tls) => {
      'SMSDOCK_PASSWORD': password,
      'SMSDOCK_FINGERPRINT': tls.fingerprint,
    };

/// Runs an example to its end.
Future<ProcessResult> runExample(
  String name,
  List<String> arguments, {
  Map<String, String>? variables,
}) =>
    Process.run(
      Platform.resolvedExecutable,
      ['example/$name', ...arguments],
      environment: variables ?? environment(tlsFiles!),
      stdoutEncoding: utf8,
      stderrEncoding: utf8,
    ).timeout(const Duration(seconds: 60));

/// An example that runs until it is stopped.
class Running {
  Running(this.process) {
    final output = StreamGroup([process.stdout, process.stderr]);
    output.transform(utf8.decoder).transform(const LineSplitter()).listen((
      line,
    ) {
      _lines.add(line);
      _waiter?.complete();
      _waiter = null;
    });
  }

  final Process process;
  final List<String> _lines = [];
  Completer<void>? _waiter;

  /// Reads lines of the output, but does not wait for ever.
  Future<List<String>> readLines(int count, [num timeout = 20]) async {
    final clock = Stopwatch()..start();
    while (_lines.length < count && clock.elapsed < seconds(timeout)) {
      final waiter = _waiter ??= Completer<void>();
      await waiter.future.timeout(
        const Duration(milliseconds: 200),
        onTimeout: () {},
      );
    }
    final lines = _lines.take(count).toList();
    _lines.removeRange(0, lines.length);
    return lines;
  }
}

/// Both output streams of a process as one.
class StreamGroup extends Stream<List<int>> {
  StreamGroup(this.streams);

  final List<Stream<List<int>>> streams;

  @override
  StreamSubscription<List<int>> listen(
    void Function(List<int> event)? onData, {
    Function? onError,
    void Function()? onDone,
    bool? cancelOnError,
  }) {
    final controller = StreamController<List<int>>();
    var open = streams.length;
    for (final stream in streams) {
      stream.listen(
        controller.add,
        onError: controller.addError,
        onDone: () {
          open -= 1;
          if (open == 0) {
            unawaited(controller.close());
          }
        },
      );
    }
    return controller.stream.listen(
      onData,
      onError: onError,
      onDone: onDone,
      cancelOnError: cancelOnError,
    );
  }
}

/// Starts an example that runs until it is stopped.
Future<Running> startExample(String name, FakeSmsDock server) async {
  final process = await Process.start(
      Platform.resolvedExecutable,
      [
        'example/$name',
        server.url,
      ],
      environment: environment(server.tls));
  addTearDown(process.kill);
  return Running(process);
}

Future<bool> subscribed(FakeSmsDock server) =>
    waitUntil(() => countOf(server, 'subscribe') > 0, seconds(20));

void main() {
  prepareTls();

  group('examples', skip: needTls, () {
    test('list_messages', () async {
      final server = await startServer();
      final result = await runExample('list_messages.dart', [server.url]);
      expect(result.exitCode, 0, reason: '${result.stderr}');
      final lines = const LineSplitter().convert(result.stdout as String);
      expect(lines, hasLength(10));
      expect(
        lines[0],
        matches(RegExp(r'^\d{4}-\d\d-\d\d \d\d:\d\d  Anna Example: Anna 24$')),
      );
      expect(lines[9], endsWith('Anna Example: Anna 6'));
    });

    test('send_sms', () async {
      final server = await startServer();
      final result = await runExample('send_sms.dart', [
        server.url,
        ben,
        'Hello',
      ]);
      expect(result.exitCode, 0, reason: '${result.stderr}');
      final lines = const LineSplitter().convert(result.stdout as String);
      expect(lines[0], '1 part(s) for $ben');
      expect(lines[1], startsWith('Sent, message id '));
      expect(
        [for (final message in server.sent) (message['to'], message['body'])],
        [(ben, 'Hello')],
      );
    });

    test('send_sms refused', () async {
      final server = await startServer();
      final result = await runExample('send_sms.dart', [
        server.url,
        'abc',
        'Hello',
      ]);
      expect(result.exitCode, 1);
      expect(result.stdout, startsWith('Not sent: invalid_number'));
    });

    test('examples report a missing fingerprint', () async {
      final server = await startServer();
      final result = await runExample(
        'list_messages.dart',
        [server.url],
        variables: {'SMSDOCK_PASSWORD': password, 'SMSDOCK_FINGERPRINT': ''},
      );
      expect(result.exitCode, 1);
      expect(result.stderr, startsWith('list_messages: '));
      expect(result.stderr, contains('SMSDOCK_FINGERPRINT'));
      expect(result.stderr, isNot(contains('#0')));
      expect(result.stdout, isEmpty);
    });

    for (final name in [
      'list_messages.dart',
      'send_sms.dart',
      'listen_incoming.dart',
      'auto_reply.dart',
    ]) {
      test('$name explains its usage', () async {
        final result = await runExample(name, []);
        expect(result.exitCode, 2);
        expect(result.stdout, contains('Usage:'));
        expect(result.stdout, contains('example/$name'));
      });
    }

    test('listen_incoming', () async {
      final server = await startServer();
      final example = await startExample('listen_incoming.dart', server);
      expect(await subscribed(server), isTrue);
      server
        ..receiveSms(anna, 'STATUS')
        ..receiveSms('A1', 'Your bill');
      final lines = await example.readLines(3);
      expect(lines, hasLength(3));
      expect(lines[0], startsWith('Waiting for SMS'));
      expect(
        lines[1],
        matches(RegExp(r'^\d\d:\d\d:\d\d  Anna Example: STATUS$')),
      );
      expect(lines[2], endsWith('A1: Your bill'));
    });

    test('listen_incoming reads what it missed', () async {
      final server = await startServer();
      final example = await startExample('listen_incoming.dart', server);
      expect(await subscribed(server), isTrue);
      server.receiveSms(anna, 'first');
      expect(await example.readLines(2), hasLength(2));
      server.dropConnections();
      expect(await waitUntil(() => server.clientCount == 0), isTrue);
      server
        ..receiveSms(ben, 'while away')
        ..restart()
        ..receiveSms(anna, 'after the restart');
      final lines = await example.readLines(2);
      expect(
        await waitUntil(() => server.clientCount == 1, seconds(20)),
        isTrue,
      );
      server.receiveSms(anna, 'live again');
      lines.addAll(await example.readLines(1));
      // Old messages of the phone are not shown, new ones only once.
      expect(
        [for (final line in lines) line.split(': ').last],
        ['while away', 'after the restart', 'live again'],
      );
    });

    test('listen_incoming stops with Ctrl+C', () async {
      final server = await startServer();
      final example = await startExample('listen_incoming.dart', server);
      expect(await subscribed(server), isTrue);
      example.process.kill(ProcessSignal.sigint);
      final code = await example.process.exitCode.timeout(seconds(20));
      expect(code, 0);
    }, testOn: '!windows');

    test('auto_reply', () async {
      final server = await startServer();
      final example = await startExample('auto_reply.dart', server);
      expect(await subscribed(server), isTrue);
      server
        ..receiveSms(anna, 'status')
        ..receiveSms(anna, 'STATUS')
        ..receiveSms('A1', 'Your bill')
        ..receiveSms('8810', 'Your code is 1234')
        ..receiveSms(ben, 'Hello?', sim: 2);
      final lines = await example.readLines(6);
      expect(lines, hasLength(6));
      expect(lines[1], startsWith('$anna: answered, requestId '));
      expect(lines[2], '$anna: no answer (answered a short time ago)');
      expect(lines[3], 'A1: no answer (not a phone number)');
      expect(lines[4], '8810: no answer (not a phone number)');
      expect(lines[5], startsWith('$ben: answered, requestId '));
      expect(
        [for (final m in server.sent) (m['to'], m['body'], m['sim'])],
        [(anna, 'All systems running.', 1), (ben, 'Commands: STATUS', 2)],
      );
    });
  });

  group('auto_reply', () {
    test('reply guard', () {
      final guard = ReplyGuard(cooldown: seconds(600), repliesPerHour: 3);
      String? refusal(String address, int now) =>
          guard.refusal(address, now: seconds(now));
      expect(refusal('+4369912345678', 0), isNull);
      expect(refusal('+4369912345678', 599), isNotNull);
      expect(refusal('+43 699 123-456.78', 599), isNotNull);
      expect(refusal('+4369912345678', 600), isNull);
      for (final sender in ['A1', 'Post', '8810', '12345', '', '+43 abc']) {
        expect(refusal(sender, 700), 'not a phone number');
      }
      expect(refusal('+4369087654321', 700), isNull);
      // Three answers in the last hour: the fourth sender has to wait.
      expect(
        refusal('+436601111111', 800),
        'too many answers in the last hour',
      );
      expect(refusal('+436601111111', 3600), isNull);
    });

    test('answers', () {
      expect(answerFor(' status\n'), 'All systems running.');
      expect(answerFor('Hello'), 'Commands: STATUS');
    });
  });
}
