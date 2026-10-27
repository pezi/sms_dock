// --------------------------------------------------------------------------
// Filename: errors.dart
//
// Description: Errors thrown by the client, and how error objects and close
//   codes of the server turn into them.
// --------------------------------------------------------------------------

/// Errors thrown by the SMS Dock client.
///
/// Wrong arguments are reported with [ArgumentError] before anything is
/// sent. A wait that ends without a result throws the `TimeoutException`
/// of `dart:async`.
library;

/// Base class of all errors thrown by this package.
class SmsDockError implements Exception {
  /// Creates an error with the text that [toString] returns.
  SmsDockError(this.description);

  /// The complete text of the error.
  final String description;

  @override
  String toString() => description;
}

/// The server answered a request with an error.
///
/// [code] is one of the error codes of `API.md`, [message] the text of
/// the server. [details] holds the other fields of the error object, for
/// example `{"retryAfter": 30}` or `{"permission": "sendSms"}`.
class ApiError extends SmsDockError {
  /// Creates the error for an error object of the server.
  ApiError(this.code, this.message, [Map<String, dynamic>? details])
    : details = Map.unmodifiable(details ?? const <String, dynamic>{}),
      super('$code: $message');

  /// Error code of `API.md`, for example `invalid_number`.
  final String code;

  /// Text of the server.
  final String message;

  /// The other fields of the error object.
  final Map<String, dynamic> details;

  /// Seconds to wait before the next attempt, if the server said so.
  int? get retryAfter {
    final value = details['retryAfter'];
    return value is num && value.isFinite ? value.toInt() : null;
  }

  /// The missing Android permission of a `permission_missing` error.
  String? get permission {
    final value = details['permission'];
    return value is String ? value : null;
  }
}

/// The login failed or the connection is no longer logged in.
///
/// Codes: `invalid_password`, `locked_out` (see [retryAfter]) and
/// `not_authenticated`. The client never retries after this error,
/// because every wrong password counts towards the lock of the phone.
class AuthenticationError extends ApiError {
  /// Creates the error for an error object of the server.
  AuthenticationError(super.code, super.message, [super.details]);
}

/// The API needs the full licence of the app (`licence_required`).
class LicenceRequiredError extends ApiError {
  /// Creates the error for an error object of the server.
  LicenceRequiredError(super.code, super.message, [super.details]);
}

/// The server sent something that protocol version 1 does not allow.
class ProtocolError extends SmsDockError {
  /// Creates the error with its text.
  ProtocolError(super.description);
}

/// The connection failed and could not be restored.
///
/// [closeCode] and [closeReason] are set when the server closed the
/// WebSocket with a close frame.
class ConnectionInterrupted extends SmsDockError {
  /// Creates the error with its text and the close frame, if any.
  ConnectionInterrupted(
    super.description, {
    this.closeCode,
    this.closeReason,
    this.cause,
  });

  /// Close code of the server, or `null` without a close frame.
  final int? closeCode;

  /// Close reason of the server, or `null`.
  final String? closeReason;

  /// The error of the transport that led to this one, if any.
  final Object? cause;
}

/// The server presented a certificate other than the pinned one.
///
/// [expected] and [actual] are SHA-256 fingerprints as upper-case hex
/// pairs separated by colons. Nothing was sent to the server.
class CertificateMismatchError extends SmsDockError {
  /// Creates the error for the two fingerprints.
  CertificateMismatchError(this.expected, this.actual)
    : super(
        'The certificate of the server does not match the pinned '
        'fingerprint.\n  expected: $expected\n  received: $actual\n'
        'Compare the fingerprint with the Server screen of the app. It '
        'changes when the certificate is regenerated or the app is '
        'reinstalled.',
      );

  /// The pinned fingerprint.
  final String expected;

  /// The fingerprint of the certificate the server presented.
  final String actual;
}

/// A failure of the connection that a new attempt may cure.
///
/// Internal: the client turns it into [ConnectionInterrupted] or, for
/// some close codes, into a final error.
class TransportError implements Exception {
  /// Creates the failure with its text and the close frame, if any.
  TransportError(
    this.message, {
    this.closeCode,
    String? closeReason,
    this.cause,
  }) : closeReason = closeReason == null || closeReason.isEmpty
           ? null
           : closeReason;

  /// What happened.
  final String message;

  /// Close code of the server, or `null` without a close frame.
  final int? closeCode;

  /// Close reason of the server, or `null`.
  final String? closeReason;

  /// The error of `dart:io` behind this failure, if any.
  final Object? cause;

  @override
  String toString() => message;
}

/// Returns the error for an error object of the server.
ApiError apiError(Map<String, dynamic> error) {
  final code = '${error['code'] ?? 'unknown'}';
  final text = error['message'];
  final message = text == null || text == '' || text == false
      ? 'no message'
      : '$text';
  final details = <String, dynamic>{
    for (final entry in error.entries)
      if (entry.key != 'code' && entry.key != 'message') entry.key: entry.value,
  };
  return switch (code) {
    'invalid_password' ||
    'locked_out' ||
    'not_authenticated' => AuthenticationError(code, message, details),
    'licence_required' => LicenceRequiredError(code, message, details),
    _ => ApiError(code, message, details),
  };
}

/// Returns the error to throw for a close code that forbids a retry, or
/// `null` when a new attempt is allowed.
SmsDockError? fatalError(TransportError error) {
  final code = error.closeCode;
  final reason = error.closeReason;
  final details = <String, dynamic>{'closeCode': code};
  return switch (code) {
    4401 => AuthenticationError(
      'not_authenticated',
      reason ?? 'The server closed the connection: not logged in',
      details,
    ),
    4410 => AuthenticationError(
      'not_authenticated',
      reason ?? 'The password was changed in the app',
      details,
    ),
    4403 => LicenceRequiredError(
      'licence_required',
      reason ?? 'The API needs the full licence of the app',
      details,
    ),
    1009 => ProtocolError(
      'The server closed the connection: frame larger than 64 KiB',
    ),
    4400 => ProtocolError(
      'The server closed the connection: protocol error'
      '${reason == null ? '' : ' ($reason)'}',
    ),
    _ => null,
  };
}

/// Returns the [ConnectionInterrupted] for a failure of the connection.
ConnectionInterrupted interrupted(String message, TransportError error) {
  final code = error.closeCode;
  final reason = error.closeReason;
  var cause = error.message;
  if (code != null) {
    cause = 'closed by the server with code $code';
    if (reason != null) {
      cause += ' ($reason)';
    }
  }
  return ConnectionInterrupted(
    '$message: $cause',
    closeCode: code,
    closeReason: reason,
    cause: error.cause ?? error,
  );
}
