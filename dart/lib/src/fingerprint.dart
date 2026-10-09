// --------------------------------------------------------------------------
// Filename: fingerprint.dart
//
// Description: Certificate pinning: fingerprint forms, the constant-time
//   comparison and the pinned TLS connection.
// --------------------------------------------------------------------------

/// Certificate pinning by the SHA-256 fingerprint of the certificate.
///
/// The certificate of the phone is self-signed and names no host, so host
/// name and chain verification cannot succeed. The fingerprint of the
/// certificate is compared instead, before anything is sent.
library;

import 'dart:async';
import 'dart:io';

import 'package:crypto/crypto.dart';

import 'errors.dart';

/// Default port of the WebSocket API.
const int defaultApiPort = 9443;

final RegExp _fingerprintPattern = RegExp(r'^[0-9a-f]{64}$');
final RegExp _separators = RegExp(r'[\s:]');

/// Returns a SHA-256 fingerprint as 64 lower-case hex digits.
///
/// Accepts hex with or without colons, in any case, and the line printed
/// by `openssl x509 -fingerprint` (`sha256 Fingerprint=AB:CD:…`). Throws
/// [ArgumentError] for anything else.
String normalizeFingerprint(String fingerprint) {
  var text = fingerprint.trim();
  final equals = text.lastIndexOf('=');
  if (equals >= 0) {
    text = text.substring(equals + 1);
  }
  text = text.replaceAll(_separators, '').toLowerCase();
  if (!_fingerprintPattern.hasMatch(text)) {
    throw ArgumentError(
      'The fingerprint must be the SHA-256 of the certificate: '
      '64 hex digits, with or without colons',
    );
  }
  return text;
}

/// Returns a fingerprint the way the app shows it: `AB:CD:…`.
String formatFingerprint(String fingerprint) {
  final digits = normalizeFingerprint(fingerprint).toUpperCase();
  return [
    for (var index = 0; index < digits.length; index += 2)
      digits.substring(index, index + 2),
  ].join(':');
}

/// Returns the SHA-256 fingerprint of a DER certificate as `AB:CD:…`.
String certificateFingerprint(List<int> der) =>
    formatFingerprint(_hex(sha256.convert(der).bytes));

/// Connects to [host] and returns the fingerprint of its certificate.
///
/// This helper trusts nobody: it only reports what the server presented.
/// Compare the result with the fingerprint on the Server screen of the app
/// before you pin it. Nothing but the TLS handshake is exchanged.
///
/// Throws [ProtocolError] when the server presents no certificate, and
/// the exceptions of `dart:io` when the connection fails.
Future<String> fetchFingerprint(
  String host, {
  int port = defaultApiPort,
  Duration timeout = const Duration(seconds: 10),
}) async {
  final socket = await connectPinned(host, port, null, timeout);
  try {
    final der = socket.peerCertificate?.der;
    if (der == null || der.isEmpty) {
      throw ProtocolError('The server presented no certificate');
    }
    return certificateFingerprint(der);
  } finally {
    socket.destroy();
  }
}

/// Returns the bytes of a fingerprint for [checkPin].
List<int> pinBytes(String fingerprint) {
  final digits = normalizeFingerprint(fingerprint);
  return [
    for (var index = 0; index < digits.length; index += 2)
      int.parse(digits.substring(index, index + 2), radix: 16),
  ];
}

/// Whether [der] is the certificate with the SHA-256 digest [pin].
///
/// The digests are compared in constant time.
bool matchesPin(List<int>? der, List<int> pin) {
  if (der == null || der.isEmpty) {
    return false;
  }
  final actual = sha256.convert(der).bytes;
  if (actual.length != pin.length) {
    return false;
  }
  var difference = 0;
  for (var index = 0; index < actual.length; index += 1) {
    difference |= actual[index] ^ pin[index];
  }
  return difference == 0;
}

/// Throws [CertificateMismatchError] unless [der] has the digest [pin].
void checkPin(List<int>? der, List<int> pin) {
  if (matchesPin(der, pin)) {
    return;
  }
  throw CertificateMismatchError(
    formatFingerprint(_hex(pin)),
    der == null || der.isEmpty ? 'no certificate' : certificateFingerprint(der),
  );
}

/// Opens a TLS connection to the self-signed certificate of the phone.
///
/// The context trusts no certificate authority, so the platform asks the
/// callback about every certificate, also about one that a public
/// authority signed. With [pin] the callback accepts only the certificate
/// with this SHA-256 digest; the handshake fails otherwise and
/// [CertificateMismatchError] is thrown. The certificate of the finished
/// handshake is compared once more. Without [pin] every certificate is
/// accepted and nothing is compared.
///
/// Nothing is written to the socket.
Future<SecureSocket> connectPinned(
  String host,
  int port,
  List<int>? pin,
  Duration timeout,
) async {
  List<int>? presented;
  var abandoned = false;
  final SecureSocket socket;
  try {
    final task = await SecureSocket.startConnect(
      host,
      port,
      context: SecurityContext(withTrustedRoots: false),
      onBadCertificate: (certificate) {
        presented = certificate.der;
        return pin == null || matchesPin(presented, pin);
      },
    );
    // A socket that arrives after the time is over is not used.
    task.socket.then((late) {
      if (abandoned) {
        late.destroy();
      }
    }).ignore();
    socket = await task.socket.timeout(
      timeout,
      onTimeout: () {
        abandoned = true;
        task.cancel();
        throw TimeoutException('timed out', timeout);
      },
    );
  } on HandshakeException {
    final seen = presented;
    if (pin != null && seen != null && !matchesPin(seen, pin)) {
      checkPin(seen, pin);
    }
    rethrow;
  }
  if (pin != null) {
    try {
      // Once more, on the connection that is going to carry the password.
      checkPin(socket.peerCertificate?.der, pin);
    } catch (_) {
      socket.destroy();
      rethrow;
    }
  }
  return socket;
}

String _hex(List<int> bytes) =>
    bytes.map((byte) => byte.toRadixString(16).padLeft(2, '0')).join();
