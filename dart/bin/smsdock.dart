// --------------------------------------------------------------------------
// Filename: smsdock.dart
//
// Description: Entry point of the command-line tool smsdock.
// --------------------------------------------------------------------------

/// Command-line tool `smsdock` for the SMS Dock WebSocket API.
///
/// ```bash
/// dart run smsdock status wss://192.168.1.50:9443/ws
/// ```
library;

import 'dart:async';
import 'dart:io';

import 'package:smsdock/src/cli.dart';

void main(List<String> arguments) {
  runZonedGuarded(
    () async {
      final code = await runCli(arguments);
      await stdout.flush();
      await stderr.flush();
      exit(code);
    },
    (error, stack) {
      if (error is FileSystemException && error.path == '') {
        // The reader of the output is gone, as in `smsdock listen … | head`.
        exit(exitOk);
      }
      stderr.writeln('Unhandled exception:\n$error\n$stack');
      exit(255);
    },
  );
}
