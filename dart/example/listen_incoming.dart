// --------------------------------------------------------------------------
// Filename: listen_incoming.dart
//
// Description: Example: calls a function for every incoming SMS, also for
//   those that arrived while the connection was down.
// --------------------------------------------------------------------------

/// Hook for incoming SMS: calls a function for every SMS that arrives.
///
/// Usage:
///
///     export SMSDOCK_PASSWORD='your password'
///     export SMSDOCK_FINGERPRINT='AB:CD:…'   # from the Server screen
///     dart run example/listen_incoming.dart wss://192.168.1.50:9443/ws
///
/// Stop with Ctrl+C.
library;

import 'dart:io';

import 'package:smsdock/smsdock.dart';

const String usage = '''
Hook for incoming SMS: call a function for every SMS that arrives.

Usage:

    export SMSDOCK_PASSWORD='your password'
    export SMSDOCK_FINGERPRINT='AB:CD:…'      # from the Server screen
    dart run example/listen_incoming.dart wss://192.168.1.50:9443/ws

Stop with Ctrl+C.
''';

/// Ids of the messages the hook has seen, and the time of the newest one.
final Set<int> seen = {};
int newest = 0;

String two(int value) => '$value'.padLeft(2, '0');

/// Your hook. [message] is a Message object of API.md.
void onSms(Message message) {
  // Timestamps are milliseconds since the epoch; show local time.
  final received = DateTime.fromMillisecondsSinceEpoch(message.timestamp);
  final sender = message.contact ?? message.address;
  stdout.writeln(
    '${two(received.hour)}:${two(received.minute)}:${two(received.second)}  '
    '$sender: ${message.body}',
  );
}

/// Calls the hook once per message.
void handle(Message message) {
  final id = message.id;
  if (id != null && !seen.add(id)) {
    return;
  }
  if (message.timestamp > newest) {
    newest = message.timestamp;
  }
  onSms(message);
}

/// Reads what arrived while the connection was down.
///
/// The phone does not send events again. The client calls this function
/// after every reconnect, when the subscription is in force again.
Future<void> catchUp(SmsDockClient dock) async {
  final missed = dock.missedEvents;
  if (dock.serverRestarted || (missed != null && missed > 0)) {
    // Messages of the same millisecond come again; handle() knows them.
    final messages = await dock
        .iterMessages(direction: 'in', since: newest)
        .toList();
    messages.reversed.forEach(handle); // oldest first
  }
}

Future<int> run(List<String> arguments) async {
  if (arguments.length != 1) {
    stdout.writeln(usage);
    return 2;
  }

  // Password and fingerprint come from the environment.
  final dock = SmsDockClient(
    arguments[0],
    // Keep trying when the phone is out of reach for a while.
    reconnectAttempts: null,
    onReconnect: catchUp,
  );
  // close() ends the stream of events below.
  final interrupt = ProcessSignal.sigint.watch().listen((_) => dock.close());
  try {
    // Only messages from now on: remember the time of the phone.
    newest = (await dock.ping()).time;
    // A new connection gets no events. Ask for the ones you need.
    await dock.subscribe(['sms.received']);
    stdout.writeln('Waiting for SMS. Press Ctrl+C to stop.');

    // events() yields until close(). After a lost connection it
    // reconnects, logs in and subscribes again, then calls catchUp().
    await for (final event in dock.events()) {
      final message = event.message;
      if (message != null) {
        handle(message);
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
    stderr.writeln('listen_incoming: $error');
    exitCode = 1;
  } on ArgumentError catch (error) {
    stderr.writeln('listen_incoming: ${error.message}');
    exitCode = 1;
  }
}
