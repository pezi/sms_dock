/**
 * Errors thrown by the SMS Dock client.
 *
 * Wrong arguments are reported with the built-in `TypeError` and
 * `RangeError` before anything is sent.
 */

/** Base class of all errors thrown by this package. */
export class SmsDockError extends Error {
  /**
   * @param {string} message
   * @param {{cause?: unknown}} [options]
   */
  constructor(message, options) {
    super(message, options);
    this.name = new.target.name;
  }
}

/**
 * The server answered a request with an error.
 *
 * `code` is one of the error codes of `API.md`, `message` the text of the
 * server. `details` holds the other fields of the error object, for example
 * `{retryAfter: 30}` or `{permission: 'sendSms'}`.
 */
export class ApiError extends SmsDockError {
  /**
   * @param {string} code
   * @param {string} message
   * @param {Record<string, unknown>} [details]
   */
  constructor(code, message, details) {
    super(message);
    /** @type {string} */
    this.code = code;
    /** @type {Record<string, unknown>} */
    this.details = { ...(details ?? {}) };
  }

  /** Seconds to wait before the next attempt, if the server said so. */
  get retryAfter() {
    const value = this.details.retryAfter;
    if (typeof value !== 'number' || !Number.isFinite(value)) {
      return null;
    }
    return Math.trunc(value);
  }

  /** The missing Android permission of a `permission_missing` error. */
  get permission() {
    const value = this.details.permission;
    return typeof value === 'string' ? value : null;
  }

  /** Returns `Name: code: message`. */
  toString() {
    return `${this.name}: ${this.code}: ${this.message}`;
  }
}

/**
 * The login failed or the connection is no longer logged in.
 *
 * Codes: `invalid_password`, `locked_out` (see `retryAfter`) and
 * `not_authenticated`. The client never retries after this error, because
 * every wrong password counts towards the lock of the phone.
 */
export class AuthenticationError extends ApiError {}

/** The API needs the full licence of the app (`licence_required`). */
export class LicenceRequiredError extends ApiError {}

/** The server sent something that protocol version 1 does not allow. */
export class ProtocolError extends SmsDockError {}

/**
 * The connection failed and could not be restored.
 *
 * `closeCode` and `closeReason` are set when the server closed the
 * WebSocket with a close frame.
 */
export class ConnectionInterrupted extends SmsDockError {
  /**
   * @param {string} message
   * @param {number | null} [closeCode]
   * @param {string | null} [closeReason]
   * @param {{cause?: unknown}} [options]
   */
  constructor(message, closeCode = null, closeReason = null, options) {
    super(message, options);
    /** @type {number | null} */
    this.closeCode = closeCode;
    /** @type {string | null} */
    this.closeReason = closeReason;
  }
}

/**
 * The server presented a certificate other than the pinned one.
 *
 * `expected` and `actual` are SHA-256 fingerprints as upper-case hex pairs
 * separated by colons. Nothing was sent to the server.
 */
export class CertificateMismatchError extends SmsDockError {
  /**
   * @param {string} expected
   * @param {string} actual
   */
  constructor(expected, actual) {
    super(
      'The certificate of the server does not match the pinned ' +
        `fingerprint.\n  expected: ${expected}\n  received: ${actual}\n` +
        'Compare the fingerprint with the Server screen of the app. It ' +
        'changes when the certificate is regenerated or the app is ' +
        'reinstalled.',
    );
    /** @type {string} */
    this.expected = expected;
    /** @type {string} */
    this.actual = actual;
  }
}

/**
 * The time to wait is over.
 *
 * Thrown by `waitForEvent()`, `waitForResult()` and `waitForResponse()`.
 * It is not an `SmsDockError`: the connection is still usable.
 */
export class TimeoutError extends Error {
  /** @param {string} message */
  constructor(message) {
    super(message);
    this.name = 'TimeoutError';
    /** @type {string} */
    this.code = 'ETIMEDOUT';
  }
}

/**
 * A failure of the connection that a new attempt may cure. Internal: the
 * client turns it into `ConnectionInterrupted` or one of the errors for a
 * close code that forbids a retry.
 */
export class TransportError extends Error {
  /**
   * @param {string} message
   * @param {{closeCode?: number | null, closeReason?: string | null,
   *   cause?: unknown}} [options]
   */
  constructor(message, options = {}) {
    super(message, options.cause === undefined ? undefined : { cause: options.cause });
    this.name = 'TransportError';
    /** @type {number | null} */
    this.closeCode = options.closeCode ?? null;
    /** @type {string | null} */
    this.closeReason = options.closeReason || null;
  }
}

/**
 * Returns the error for an error object of the server.
 *
 * @param {Record<string, unknown>} error
 * @returns {ApiError}
 */
export function apiError(error) {
  const code = String(error.code ?? 'unknown');
  const message = String(error.message || 'no message');
  const details = {};
  for (const [key, value] of Object.entries(error)) {
    if (key !== 'code' && key !== 'message') {
      details[key] = value;
    }
  }
  if (['invalid_password', 'locked_out', 'not_authenticated'].includes(code)) {
    return new AuthenticationError(code, message, details);
  }
  if (code === 'licence_required') {
    return new LicenceRequiredError(code, message, details);
  }
  return new ApiError(code, message, details);
}

/**
 * Returns the error to throw for a close code that forbids a retry, or
 * `null` when a new attempt is allowed.
 *
 * @param {unknown} error
 * @returns {SmsDockError | null}
 */
export function fatalError(error) {
  if (!(error instanceof TransportError) || error.closeCode === null) {
    return null;
  }
  const code = error.closeCode;
  const reason = error.closeReason;
  const details = { closeCode: code };
  switch (code) {
    case 4401:
      return new AuthenticationError(
        'not_authenticated',
        reason || 'The server closed the connection: not logged in',
        details,
      );
    case 4410:
      return new AuthenticationError(
        'not_authenticated',
        reason || 'The password was changed in the app',
        details,
      );
    case 4403:
      return new LicenceRequiredError(
        'licence_required',
        reason || 'The API needs the full licence of the app',
        details,
      );
    case 1009:
      return new ProtocolError(
        'The server closed the connection: frame larger than 64 KiB',
      );
    case 4400:
      return new ProtocolError(
        'The server closed the connection: protocol error' +
          (reason ? ` (${reason})` : ''),
      );
    default:
      return null;
  }
}

/**
 * Returns the `ConnectionInterrupted` for a failure of the connection.
 *
 * @param {string} message
 * @param {Error} error
 * @returns {ConnectionInterrupted}
 */
export function interrupted(message, error) {
  const code = error instanceof TransportError ? error.closeCode : null;
  const reason = error instanceof TransportError ? error.closeReason : null;
  let cause;
  if (code !== null) {
    cause = `closed by the server with code ${code}`;
    if (reason) {
      cause += ` (${reason})`;
    }
  } else {
    cause = error.message || error.name;
  }
  return new ConnectionInterrupted(`${message}: ${cause}`, code, reason, {
    cause: error.cause ?? error,
  });
}
