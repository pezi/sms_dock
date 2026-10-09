/**
 * Certificate fingerprints and the pinned TLS connection.
 *
 * The certificate of the phone is self-signed and names no host, so host
 * name and chain verification cannot succeed. The client compares the
 * SHA-256 of the certificate instead, right after the TLS handshake and
 * before anything else is sent.
 */

import { createHash, timingSafeEqual } from 'node:crypto';
import { isIP } from 'node:net';
import tls from 'node:tls';

import {
  CertificateMismatchError,
  ConnectionInterrupted,
  ProtocolError,
  TransportError,
} from './errors.js';

/** Default port of the WebSocket API. */
export const DEFAULT_API_PORT = 9443;

const FINGERPRINT_PATTERN = /^[0-9a-f]{64}$/;

/**
 * Returns a SHA-256 fingerprint as 64 lower-case hex digits.
 *
 * Accepts hex with or without colons, in any case, and the line printed by
 * `openssl x509 -fingerprint` (`sha256 Fingerprint=AB:CD:…`). Throws a
 * `RangeError` for anything else.
 *
 * @param {string} fingerprint
 * @returns {string}
 */
export function normalizeFingerprint(fingerprint) {
  if (typeof fingerprint !== 'string') {
    throw new TypeError('The fingerprint must be a string');
  }
  let text = fingerprint.trim();
  if (text.includes('=')) {
    text = text.slice(text.lastIndexOf('=') + 1);
  }
  text = text.replace(/[\s:]/g, '').toLowerCase();
  if (!FINGERPRINT_PATTERN.test(text)) {
    throw new RangeError(
      'The fingerprint must be the SHA-256 of the certificate: ' +
        '64 hex digits, with or without colons',
    );
  }
  return text;
}

/**
 * Returns a fingerprint the way the app shows it: `AB:CD:…`.
 *
 * @param {string} fingerprint
 * @returns {string}
 */
export function formatFingerprint(fingerprint) {
  const digits = normalizeFingerprint(fingerprint).toUpperCase();
  return digits.match(/../g).join(':');
}

/**
 * Returns the SHA-256 fingerprint of a DER certificate as `AB:CD:…`.
 *
 * @param {Uint8Array} der
 * @returns {string}
 */
export function certificateFingerprint(der) {
  return formatFingerprint(createHash('sha256').update(der).digest('hex'));
}

/**
 * Connects to `host:port` and returns the fingerprint of its certificate.
 *
 * This helper trusts nobody: it only reports what the server presented.
 * Compare the result with the fingerprint on the Server screen of the app
 * before you pin it. Nothing but the TLS handshake is exchanged.
 *
 * @param {string} host
 * @param {number} [port]
 * @param {{timeout?: number}} [options] `timeout` in seconds, default 10.
 * @returns {Promise<string>}
 */
export async function fetchFingerprint(host, port = DEFAULT_API_PORT, options = {}) {
  const { timeout = 10 } = options;
  let connection;
  try {
    connection = await connectTls(host, port, null, timeout);
  } catch (error) {
    if (error instanceof TransportError) {
      throw new ConnectionInterrupted(
        `Cannot connect to ${host}:${port}: ${error.message}`,
        null,
        null,
        { cause: error.cause ?? error },
      );
    }
    throw error;
  }
  connection.socket.destroy();
  if (!connection.der || connection.der.length === 0) {
    throw new ProtocolError('The server presented no certificate');
  }
  return certificateFingerprint(connection.der);
}

/**
 * Throws `CertificateMismatchError` unless the digest of the certificate
 * equals the pinned one. The comparison takes constant time.
 *
 * @param {Uint8Array | null | undefined} der
 * @param {Buffer} pinned SHA-256 digest, 32 bytes.
 */
export function checkPin(der, pinned) {
  const present = Boolean(der) && der.length > 0;
  const actual = createHash('sha256').update(present ? der : Buffer.alloc(0)).digest();
  if (!present || actual.length !== pinned.length || !timingSafeEqual(actual, pinned)) {
    throw new CertificateMismatchError(
      formatFingerprint(pinned.toString('hex')),
      present ? formatFingerprint(actual.toString('hex')) : 'no certificate',
    );
  }
}

/**
 * Returns the DER certificate the peer of a TLS socket presented.
 *
 * @param {import('node:tls').TLSSocket} socket
 * @returns {Buffer | null}
 */
export function peerCertificate(socket) {
  const certificate = socket.getPeerCertificate();
  const der = certificate ? certificate.raw : null;
  return der && der.length > 0 ? der : null;
}

/**
 * Opens a TLS connection for the self-signed certificate of the phone.
 *
 * Host name and chain verification are off, the certificate store of the
 * system plays no part. Instead the fingerprint of the certificate is
 * compared as soon as the TLS handshake is done. The promise resolves only
 * after that comparison; on a mismatch the socket is destroyed and nothing
 * was written to it. With `pinned === null` nothing is compared: that is
 * `insecure` and the fingerprint helper.
 *
 * This is the only place of the package that turns verification off.
 *
 * @param {string} host
 * @param {number} port
 * @param {Buffer | null} pinned SHA-256 digest of the certificate.
 * @param {number} timeout Seconds.
 * @returns {Promise<{socket: import('node:tls').TLSSocket, der: Buffer | null}>}
 */
export function connectTls(host, port, pinned, timeout) {
  return new Promise((resolve, reject) => {
    let settled = false;
    /** @type {import('node:tls').TLSSocket} */
    let socket;
    const fail = (error) => {
      if (settled) {
        return;
      }
      settled = true;
      clearTimeout(timer);
      if (socket) {
        socket.destroy();
      }
      reject(error);
    };
    const timer = setTimeout(() => {
      fail(new TransportError(`timed out after ${timeout} seconds`));
    }, timeout * 1000);
    try {
      socket = tls.connect({
        host,
        port,
        rejectUnauthorized: false,
        checkServerIdentity: () => undefined,
        minVersion: 'TLSv1.2',
        ...(isIP(host) ? {} : { servername: host }),
      });
    } catch (error) {
      fail(new TransportError(error.message, { cause: error }));
      return;
    }
    // Stays for the life of the socket: a late error must not be unhandled.
    socket.on('error', (error) => {
      fail(new TransportError(error.message || String(error.code), { cause: error }));
    });
    socket.once('close', () => {
      fail(new TransportError('The connection was closed during the TLS handshake'));
    });
    socket.once('secureConnect', () => {
      if (settled) {
        return;
      }
      let der;
      try {
        der = peerCertificate(socket);
        if (pinned !== null) {
          checkPin(der, pinned);
        }
      } catch (error) {
        fail(error);
        return;
      }
      settled = true;
      clearTimeout(timer);
      resolve({ socket, der });
    });
  });
}
