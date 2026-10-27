// --------------------------------------------------------------------------
// Filename: send_group_sms.dart
//
// Description: Example: sends one text to several numbers, one SMS each,
//   and waits for every result.
// --------------------------------------------------------------------------

/// Sends one text to several numbers, one SMS each, and waits for the
/// results.
///
/// Usage:
///
///     export SMSDOCK_PASSWORD='your password'
///     export SMSDOCK_FINGERPRINT='AB:CD:…'   # from the Server screen
///     dart run example/send_group_sms.dart wss://192.168.1.50:9443/ws \
///       "Hello all" +4369912345678 +436641112223
library;

import 'dart:async';
import 'dart:io';

import 'package:smsdock/smsdock.dart';

const String usage = '''
Send one text to several numbers, one SMS each, and wait for the results.

Usage:

    export SMSDOCK_PASSWORD='your password'
    export SMSDOCK_FINGERPRINT='AB:CD:…'      # from the Server screen
    dart run example/send_group_sms.dart wss://192.168.1.50:9443/ws \\
      "Hello all" +4369912345678 +436641112223
''';

Future<int> run(List<String> arguments) async {
  if (arguments.length < 3) {
    stdout.writeln(usage);
    return 2;
  }
  final [url, text, ...numbers] = arguments;

  // Password and fingerprint come from the environment.
  final dock = SmsDockClient(url);
  var failed = 0;
  try {
    final GroupSendResult result;
    try {
      // sendGroup() returns as soon as the phone has queued one SMS per
      // recipient. The phone checks the whole group first: one bad number
      // refuses all of them, nothing is sent then. The client adds a
      // random requestId; the members get requestId.1, requestId.2, …
      result = await dock.sendGroup(numbers, text);
    } on ApiError catch (error) {
      // For example invalid_number, message_too_long, quota_exceeded.
      stdout.writeln('Not sent: ${error.code}: ${error.message}');
      return 1;
    }
    stdout.writeln(
      '${result.to.length} recipient(s), ${result.parts} part(s) each',
    );

    // Wait for every member; a member the phone refused at once is
    // already failed and needs no waiting.
    for (final member in result.messages) {
      if (member.status == 'failed') {
        stdout.writeln('${member.to}: refused by the phone');
        failed += 1;
        continue;
      }
      final outcome = await dock.waitForResult(
        member.requestId,
        timeout: const Duration(seconds: 60),
      );
      if (outcome.status == 'failed') {
        stdout.writeln('${member.to}: failed, error code ${outcome.errorCode}');
        failed += 1;
      } else {
        stdout.writeln(
          '${member.to}: ${outcome.status}, message id ${outcome.messageId}',
        );
      }
    }
  } finally {
    await dock.close();
  }
  return failed > 0 ? 1 : 0;
}

Future<void> main(List<String> arguments) async {
  try {
    exitCode = await run(arguments);
  } on SmsDockError catch (error) {
    stderr.writeln('send_group_sms: $error');
    exitCode = 1;
  } on TimeoutException catch (error) {
    stderr.writeln('send_group_sms: ${error.message}');
    exitCode = 1;
  } on ArgumentError catch (error) {
    stderr.writeln('send_group_sms: ${error.message}');
    exitCode = 1;
  }
}
