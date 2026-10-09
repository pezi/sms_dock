// --------------------------------------------------------------------------
// Filename: smsdock.dart
//
// Description: Public library of the package. Exports the client, the errors,
//   the typed views of the JSON objects and the fingerprint helpers.
// --------------------------------------------------------------------------

/// Dart client for the SMS Dock WebSocket API, protocol version 1.
///
/// ```dart
/// import 'dart:io';
///
/// import 'package:smsdock/smsdock.dart';
///
/// Future<void> main() async {
///   // Password and fingerprint come from SMSDOCK_PASSWORD and
///   // SMSDOCK_FINGERPRINT.
///   final dock = SmsDockClient('wss://192.168.1.50:9443/ws');
///   try {
///     final page = await dock.messages(direction: 'in', limit: 10);
///     for (final message in page.items) {
///       stdout.writeln('${message.address}: ${message.body}');
///     }
///   } finally {
///     await dock.close();
///   }
/// }
/// ```
library;

export 'src/client.dart'
    show
        ReconnectHook,
        SmsDockClient,
        apiProtocolVersion,
        eventNames,
        fingerprintEnv,
        maxFrameBytes,
        maxPageSize,
        passwordEnv;
export 'src/errors.dart'
    show
        ApiError,
        AuthenticationError,
        CertificateMismatchError,
        ConnectionInterrupted,
        LicenceRequiredError,
        ProtocolError,
        SmsDockError;
export 'src/fingerprint.dart'
    show
        certificateFingerprint,
        defaultApiPort,
        fetchFingerprint,
        formatFingerprint,
        normalizeFingerprint;
export 'src/models.dart';
