// --------------------------------------------------------------------------
// Filename: list_messages.dart
//
// Description: Example: prints the last 10 incoming SMS.
// --------------------------------------------------------------------------

/// Prints the last 10 incoming SMS.
///
/// Usage:
///
///     export SMSDOCK_PASSWORD='your password'
///     export SMSDOCK_FINGERPRINT='AB:CD:…'   # from the Server screen
///     dart run example/list_messages.dart wss://192.168.1.50:9443/ws
library;

import 'dart:io';

import 'package:smsdock/smsdock.dart';

const String usage = '''
Print the last 10 incoming SMS.

Usage:

    export SMSDOCK_PASSWORD='your password'
    export SMSDOCK_FINGERPRINT='AB:CD:…'      # from the Server screen
    dart run example/list_messages.dart wss://192.168.1.50:9443/ws
''';

String two(int value) => '$value'.padLeft(2, '0');

Future<int> run(List<String> arguments) async {
  if (arguments.length != 1) {
    stdout.writeln(usage);
    return 2;
  }

  // The password comes from SMSDOCK_PASSWORD, the fingerprint from
  // SMSDOCK_FINGERPRINT. It pins the self-signed certificate of the phone.
  final dock = SmsDockClient(arguments[0]);
  final Page<Message> page;
  try {
    page = await dock.messages(direction: 'in', limit: 10);
  } finally {
    await dock.close();
  }

  for (final message in page.items) {
    // Timestamps are milliseconds since the epoch; show local time.
    final received = DateTime.fromMillisecondsSinceEpoch(message.timestamp);
    final sender = message.contact ?? message.address;
    stdout.writeln(
      '${received.year}-${two(received.month)}-${two(received.day)} '
      '${two(received.hour)}:${two(received.minute)}  '
      '$sender: ${message.body}',
    );
  }

  // page.nextCursor leads to the next 10; see iterMessages() for a stream
  // that follows it.
  return 0;
}

Future<void> main(List<String> arguments) async {
  try {
    exitCode = await run(arguments);
  } on SmsDockError catch (error) {
    stderr.writeln('list_messages: $error');
    exitCode = 1;
  } on ArgumentError catch (error) {
    stderr.writeln('list_messages: ${error.message}');
    exitCode = 1;
  }
}
