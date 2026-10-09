// --------------------------------------------------------------------------
// Filename: send_sms.dart
//
// Description: Example: sends one SMS and waits for the result.
// --------------------------------------------------------------------------

/// Sends one SMS and waits for the result.
///
/// Usage:
///
///     export SMSDOCK_PASSWORD='your password'
///     export SMSDOCK_FINGERPRINT='AB:CD:…'   # from the Server screen
///     dart run example/send_sms.dart wss://192.168.1.50:9443/ws \
///       +4369912345678 "Hello"
library;

import 'dart:async';
import 'dart:io';

import 'package:smsdock/smsdock.dart';

const String usage = '''
Send one SMS and wait for the result.

Usage:

    export SMSDOCK_PASSWORD='your password'
    export SMSDOCK_FINGERPRINT='AB:CD:…'      # from the Server screen
    dart run example/send_sms.dart wss://192.168.1.50:9443/ws \\
      +4369912345678 "Hello"
''';

Future<int> run(List<String> arguments) async {
  if (arguments.length != 3) {
    stdout.writeln(usage);
    return 2;
  }
  final [url, number, text] = arguments;

  // Password and fingerprint come from the environment.
  final dock = SmsDockClient(url);
  final OutboxEntry outcome;
  try {
    final SendResult result;
    try {
      // send() returns as soon as the phone has queued the message. The
      // client adds a random requestId, so a repeat after a lost
      // connection cannot send the message twice. Pass your own
      // requestId: if the whole program may run again.
      result = await dock.send(number, text);
    } on ApiError catch (error) {
      // For example invalid_number, message_too_long, quota_exceeded.
      stdout.writeln('Not sent: ${error.code}: ${error.message}');
      return 1;
    }
    stdout.writeln('${result.parts} part(s) for ${result.to}');

    // Wait until the network accepted the message or refused it.
    outcome = await dock.waitForResult(
      result.requestId,
      timeout: const Duration(seconds: 60),
    );
  } finally {
    await dock.close();
  }

  if (outcome.status == 'failed') {
    stdout.writeln('Failed, error code ${outcome.errorCode}');
    return 1;
  }
  stdout.writeln('Sent, message id ${outcome.messageId}');
  return 0;
}

Future<void> main(List<String> arguments) async {
  try {
    exitCode = await run(arguments);
  } on SmsDockError catch (error) {
    stderr.writeln('send_sms: $error');
    exitCode = 1;
  } on TimeoutException catch (error) {
    stderr.writeln('send_sms: ${error.message}');
    exitCode = 1;
  } on ArgumentError catch (error) {
    stderr.writeln('send_sms: ${error.message}');
    exitCode = 1;
  }
}
