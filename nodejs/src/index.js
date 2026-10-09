/** Node.js client for the SMS Dock WebSocket API. */

export {
  API_PROTOCOL_VERSION,
  EVENT_NAMES,
  FINGERPRINT_ENV,
  MAX_FRAME_BYTES,
  MAX_PAGE_SIZE,
  PASSWORD_ENV,
  SmsDockClient,
} from './client.js';
export {
  ApiError,
  AuthenticationError,
  CertificateMismatchError,
  ConnectionInterrupted,
  LicenceRequiredError,
  ProtocolError,
  SmsDockError,
  TimeoutError,
} from './errors.js';
export {
  DEFAULT_API_PORT,
  certificateFingerprint,
  fetchFingerprint,
  formatFingerprint,
  normalizeFingerprint,
} from './fingerprint.js';

/** Version of this package. */
export const VERSION = '0.1.0';
