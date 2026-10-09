// --------------------------------------------------------------------------
// Filename: auto_reply.dart
//
// Description: Example: answers incoming SMS, with guards against reply
//   loops.
// --------------------------------------------------------------------------

/// Hook that answers incoming SMS.
///
/// An SMS with the text STATUS gets the answer "All systems running.";
/// everything else gets a short help text.
///
/// An answering machine can end up in a loop, for example with another
/// answering machine or with a service that answers every SMS. Three
/// guards prevent that:
///
/// 1. A number gets at most one answer in [cooldownMinutes].
/// 2. Senders that are not ordinary phone numbers get no answer: names
///    such as "A1" or "Post" and short codes such as 8810.
/// 3. At most [maxRepliesPerHour] answers are sent in total.
///
/// Usage:
///
///     export SMSDOCK_PASSWORD='your password'
///     export SMSDOCK_FINGERPRINT='AB:CD:…'   # from the Server screen
///     dart run example/auto_reply.dart wss://192.168.1.50:9443/ws
///
/// Stop with Ctrl+C. Every answer is an SMS that your tariff may charge.
library;

import 'dart:collection';
import 'dart:io';

import 'package:smsdock/smsdock.dart';

const String usage = '''
Hook that answers incoming SMS.

An SMS with the text STATUS gets the answer "All systems running.";
everything else gets a short help text.

An answering machine can end up in a loop, for example with another
answering machine or with a service that answers every SMS. Three guards
prevent that:

1. A number gets at most one answer in 10 minutes.
2. Senders that are not ordinary phone numbers get no answer: names such
   as "A1" or "Post" and short codes such as 8810.
3. At most 20 answers are sent per hour in total.

Usage:

    export SMSDOCK_PASSWORD='your password'
    export SMSDOCK_FINGERPRINT='AB:CD:…'      # from the Server screen
    dart run example/auto_reply.dart wss://192.168.1.50:9443/ws

Stop with Ctrl+C. Every answer is an SMS that your tariff may charge.
''';

const int cooldownMinutes = 10;
const int maxRepliesPerHour = 20;

/// A phone number: optional +, then 7 to 15 digits. Shorter ones are
/// short codes; anything with letters is a sender name that cannot be
/// answered.
final RegExp phoneNumber = RegExp(r'^\+?[0-9]{7,15}$');
final RegExp _ignored = RegExp(r'[\s\-.()]');

/// Decides whether a sender may get an answer now.
class ReplyGuard {
  ReplyGuard({required this.cooldown, required this.repliesPerHour});

  final Duration cooldown;
  final int repliesPerHour;
  final Map<String, Duration> _lastReply = {};
  final Queue<Duration> _recent = Queue();
  final Stopwatch _clock = Stopwatch()..start();

  /// Returns why this sender gets no answer, or `null` if it gets one.
  ///
  /// [now] is the time on a clock that never goes back; the tests set it.
  String? refusal(String address, {Duration? now}) {
    final time = now ?? _clock.elapsed;
    final number = address.replaceAll(_ignored, '');
    if (!phoneNumber.hasMatch(number)) {
      return 'not a phone number';
    }
    final last = _lastReply[number];
    if (last != null && time - last < cooldown) {
      return 'answered a short time ago';
    }
    while (_recent.isNotEmpty &&
        time - _recent.first >= const Duration(hours: 1)) {
      _recent.removeFirst();
    }
    if (_recent.length >= repliesPerHour) {
      return 'too many answers in the last hour';
    }
    // Count the answer before it is sent: a failure must not open the
    // door for a loop.
    _lastReply[number] = time;
    _recent.add(time);
    return null;
  }
}

/// Returns the answer to a message text.
String answerFor(String text) => text.trim().toUpperCase() == 'STATUS'
    ? 'All systems running.'
    : 'Commands: STATUS';

Future<int> run(List<String> arguments) async {
  if (arguments.length != 1) {
    stdout.writeln(usage);
    return 2;
  }

  final guard = ReplyGuard(
    cooldown: const Duration(minutes: cooldownMinutes),
    repliesPerHour: maxRepliesPerHour,
  );
  final dock = SmsDockClient(arguments[0], reconnectAttempts: null);
  // close() ends the stream of events below.
  final interrupt = ProcessSignal.sigint.watch().listen((_) => dock.close());
  try {
    await dock.subscribe(['sms.received']);
    stdout.writeln('Answering SMS. Press Ctrl+C to stop.');

    // SMS that arrive while the connection is down get no answer: the
    // phone does not send events again, and a late answer helps nobody.
    // listen_incoming.dart shows how to read such messages.
    await for (final event in dock.events()) {
      final message = event.message;
      if (message == null) {
        continue;
      }
      final sender = message.address;
      final refusal = guard.refusal(sender);
      if (refusal != null) {
        stdout.writeln('$sender: no answer ($refusal)');
        continue;
      }
      try {
        // Answer on the SIM that received the message.
        final result = await dock.send(
          sender,
          answerFor(message.body),
          sim: message.sim,
        );
        stdout.writeln('$sender: answered, requestId ${result.requestId}');
      } on ApiError catch (error) {
        stdout.writeln(
          '$sender: not answered, ${error.code}: ${error.message}',
        );
      }
    }
  } finally {
    await interrupt.cancel();
    await dock.close();
  }
  return 0;
}

Future<void> main(List<String> arguments) async {
  try {
    exitCode = await run(arguments);
  } on SmsDockError catch (error) {
    stderr.writeln('auto_reply: $error');
    exitCode = 1;
  } on ArgumentError catch (error) {
    stderr.writeln('auto_reply: ${error.message}');
    exitCode = 1;
  }
}
