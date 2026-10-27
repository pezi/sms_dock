/**
 * Reconnecting client for the SMS Dock WebSocket API.
 *
 * The protocol is described in `API.md` at the top of the repository.
 *
 * All methods that talk to the phone return promises. Several of them may
 * be awaited at once on one client: every request carries its own id and
 * the responses are matched by that id, in whatever order they arrive.
 * All times (timeouts, delays) are seconds; timestamps of messages are
 * milliseconds since the Unix epoch.
 */

import { AsyncLocalStorage } from 'node:async_hooks';
import { randomUUID } from 'node:crypto';

import WebSocket from 'ws';

import {
  ApiError,
  ConnectionInterrupted,
  ProtocolError,
  TimeoutError,
  TransportError,
  apiError,
  fatalError,
  interrupted,
} from './errors.js';
import {
  checkPin,
  connectTls,
  normalizeFingerprint,
  peerCertificate,
} from './fingerprint.js';

/** Protocol version this client speaks. */
export const API_PROTOCOL_VERSION = 1;

/** Largest frame the server accepts from a client. */
export const MAX_FRAME_BYTES = 64 * 1024;

/** Largest frame the client accepts. Answers may exceed 64 KiB. */
export const MAX_INCOMING_BYTES = 4 * 1024 * 1024;

/** Largest value of `limit`. */
export const MAX_PAGE_SIZE = 200;

/** Environment variable that holds the password. */
export const PASSWORD_ENV = 'SMSDOCK_PASSWORD';

/** Environment variable that holds the certificate fingerprint. */
export const FINGERPRINT_ENV = 'SMSDOCK_FINGERPRINT';

/** All event names of protocol version 1. */
export const EVENT_NAMES = Object.freeze([
  'sms.received',
  'sms.queued',
  'sms.sent',
  'sms.delivered',
  'sms.failed',
  'sms.deleted',
  'conversations.changed',
]);

const CLIENT_OPTIONS = [
  'password',
  'fingerprint',
  'insecure',
  'timeout',
  'connectTimeout',
  'reconnectAttempts',
  'reconnectDelay',
  'maxReconnectDelay',
  'eventQueueSize',
  'rateLimitRetries',
  'onReconnect',
];
const MESSAGE_FILTERS = [
  'address',
  'conversationId',
  'direction',
  'q',
  'since',
  'until',
  'sim',
  'unread',
];
const REQUEST_ID_PATTERN = /^[A-Za-z0-9._-]{1,64}$/;
const GROUP_REQUEST_ID_PATTERN = /^[A-Za-z0-9._-]{1,61}$/;

/** Recipients of one group message at most. */
export const MAX_GROUP_RECIPIENTS = 20;
const DIRECTIONS = ['in', 'out', 'all'];
const OUTCOME_EVENTS = ['sms.sent', 'sms.delivered', 'sms.failed'];
const MAX_BACKOFF_EXPONENT = 16;
const FIRST_POLL = 0.1;
const PING_INTERVAL = 20;
const CLOSE_TIMEOUT = 5;
const MAX_LOST_REQUESTS = 1000;

/** Marks a timeout that belongs to a connection presumed dead. */
const kConnection = Symbol('smsdock.connection');

/** Returned to a reader of events when `close()` was called meanwhile. */
const CLOSED = Symbol('smsdock.closed');

/** Tells code that runs inside an `onReconnect` hook which open it is in. */
const hookContext = new AsyncLocalStorage();

/**
 * One authenticated connection to SMS Dock.
 *
 * Results are plain objects exactly as the server sent them; their keys
 * are described in `API.md` and typed in `index.d.ts`.
 *
 * ```js
 * const dock = new SmsDockClient('wss://192.168.1.50:9443/ws', {
 *   fingerprint: 'AB:CD:…',
 * });
 * try {
 *   console.log(await dock.status());
 * } finally {
 *   await dock.close();
 * }
 * ```
 *
 * Two requests awaited at once simply work. Events are handed to one
 * reader each: run one `events()` loop per client.
 */
export class SmsDockClient {
  #password;
  /** @type {Buffer | null} */
  #pin = null;
  #target;
  /** The connection in use, or `null`. */
  #connection = null;
  /** The open in progress: `{promise, parent}`, or `null`. */
  #opening = null;
  /** Raised by every `close()`; tells waiting code that it was closed. */
  #generation = 0;
  /** A failure nobody waited for; the next operation reports it. */
  #pendingFailure = null;
  /** Requests of `sendRequest()` whose connection was lost: id → error. */
  #lost = new Map();
  /** Queue of `{serial, event}`. */
  #events = [];
  #eventSerial = 0;
  #eventQueueSize;
  #eventWaiters = new Set();
  #requestNumber = 0;
  #subscription = null;
  #warned = false;
  #epoch = null;
  #latestSeq = null;
  #restarted = false;
  #missed = 0;

  /**
   * @param {string} url The `wss://` address the app shows, for example
   *   `wss://192.168.1.50:9443/ws`. Any other scheme is refused.
   * @param {object} [options]
   * @param {string} [options.password] Password set in the app. Without
   *   it the environment variable `SMSDOCK_PASSWORD` is read.
   * @param {string} [options.fingerprint] SHA-256 fingerprint of the
   *   certificate as shown on the Server screen of the app: hex, with or
   *   without colons, in any case. Without it the environment variable
   *   `SMSDOCK_FINGERPRINT` is read. The client compares it with the
   *   certificate of the server right after the TLS handshake and sends
   *   nothing if they differ.
   * @param {boolean} [options.insecure=false] Skip that comparison and
   *   print a warning to standard error. For tests only.
   * @param {number} [options.timeout=30] Seconds to wait for a response.
   * @param {number} [options.connectTimeout=10] Seconds to open the
   *   connection.
   * @param {number | null} [options.reconnectAttempts=5] Reconnect
   *   attempts after a lost connection; `0` for none, `null` for no limit.
   * @param {number} [options.reconnectDelay=1] Seconds before the first
   *   attempt; doubled for each further one.
   * @param {number} [options.maxReconnectDelay=10] Upper limit of that
   *   delay.
   * @param {number} [options.eventQueueSize=1000] Events kept while nobody
   *   reads them; after that the oldest are dropped and counted in
   *   `droppedEvents`.
   * @param {number} [options.rateLimitRetries=2] How often a request is
   *   repeated after the error `rate_limited`.
   * @param {(client: SmsDockClient) => unknown} [options.onReconnect]
   *   Called with the client after every reconnect, once the subscription
   *   is restored. May be async. It can also be set as a property.
   */
  constructor(url, options = {}) {
    checkOptions(options, CLIENT_OPTIONS);
    const target = parseUrl(url);
    const password = options.password ?? process.env[PASSWORD_ENV];
    if (!password) {
      throw new RangeError(
        'No password: pass one or set the environment variable ' + PASSWORD_ENV,
      );
    }
    if (typeof password !== 'string') {
      throw new TypeError('The password must be a string');
    }
    const insecure = Boolean(options.insecure);
    const fingerprint = options.fingerprint ?? (process.env[FINGERPRINT_ENV] || null);
    if (fingerprint === null && !insecure) {
      throw new RangeError(
        'No certificate fingerprint: pass the fingerprint shown on ' +
          'the Server screen of the app or set the environment ' +
          `variable ${FINGERPRINT_ENV}`,
      );
    }
    const {
      timeout = 30,
      connectTimeout = 10,
      reconnectDelay = 1,
      maxReconnectDelay = 10,
      eventQueueSize = 1000,
      rateLimitRetries = 2,
    } = options;
    let { reconnectAttempts = 5 } = options;
    if (reconnectAttempts === Infinity) {
      reconnectAttempts = null;
    }
    if (!(timeout > 0) || !(connectTimeout > 0)) {
      throw new RangeError('Timeouts must be positive');
    }
    if (reconnectAttempts !== null && !(reconnectAttempts >= 0)) {
      throw new RangeError('reconnectAttempts cannot be negative');
    }
    if (!(reconnectDelay >= 0) || !(maxReconnectDelay >= 0)) {
      throw new RangeError('Reconnect delays cannot be negative');
    }
    if (!(eventQueueSize >= 1)) {
      throw new RangeError('eventQueueSize must be at least 1');
    }
    if (!(rateLimitRetries >= 0)) {
      throw new RangeError('rateLimitRetries cannot be negative');
    }

    /** The URL as given. */
    this.url = url;
    /** Seconds to wait for a response. */
    this.timeout = timeout;
    /** Seconds to open the connection. */
    this.connectTimeout = connectTimeout;
    /** Reconnect attempts after a lost connection; `null` is no limit. */
    this.reconnectAttempts = reconnectAttempts;
    /** Seconds before the first reconnect attempt. */
    this.reconnectDelay = reconnectDelay;
    /** Upper limit of the delay between reconnect attempts. */
    this.maxReconnectDelay = maxReconnectDelay;
    /** Whether the certificate is not checked. */
    this.insecure = insecure;
    /** How often a request is repeated after `rate_limited`. */
    this.rateLimitRetries = rateLimitRetries;
    /**
     * Called with the client after every reconnect, or `null`.
     * @type {((client: SmsDockClient) => unknown) | null}
     */
    this.onReconnect = options.onReconnect ?? null;
    /**
     * `{name, version, protocolVersion}` of the app, known after the login.
     * @type {{name: string, version: string, protocolVersion: number} | null}
     */
    this.serverInfo = null;
    /** Number of events dropped because the event queue was full. */
    this.droppedEvents = 0;
    /** Number of connections opened so far, reconnects included. */
    this.connectionCount = 0;
    /** Largest request frame; the tests change it. @private */
    this._maxFrameBytes = MAX_FRAME_BYTES;

    this.#password = password;
    if (!insecure && fingerprint !== null) {
      this.#pin = Buffer.from(normalizeFingerprint(fingerprint), 'hex');
    }
    this.#target = target;
    this.#eventQueueSize = eventQueueSize;
  }

  /** Returns a text without the password. */
  toString() {
    return `<SmsDockClient ${this.url} ${this.connected ? 'connected' : 'closed'}>`;
  }

  /** What `console.log` shows: no password, no queued messages. */
  [Symbol.for('nodejs.util.inspect.custom')]() {
    return this.toString();
  }

  // ------------------------------------------------------------------
  // Connection
  // ------------------------------------------------------------------

  /** Whether a connection is open and logged in. */
  get connected() {
    const connection = this.#connection;
    return connection !== null && connection.ready && !connection.down;
  }

  /**
   * The subscription in force, or `null` without one.
   * @returns {{events: string[], address: string | null} | null}
   */
  get subscription() {
    if (this.#subscription === null) {
      return null;
    }
    return {
      events: [...this.#subscription.events],
      address: this.#subscription.address,
    };
  }

  /** Number of events that wait in the queue. */
  get pendingEvents() {
    return this.#events.length;
  }

  /**
   * The `epoch` of the last login; `null` before the first.
   *
   * It identifies one run of the app. Sequence numbers begin at 1 again
   * when it changes.
   * @returns {number | null}
   */
  get epoch() {
    return this.#epoch;
  }

  /**
   * Sequence number of the newest event the client knows of.
   *
   * This is the `latestSeq` of the last login or the `seq` of an event
   * received since, whichever is higher. The phone counts all events, so a
   * client that subscribed to some of them sees gaps.
   * @returns {number | null}
   */
  get latestSeq() {
    return this.#latestSeq;
  }

  /**
   * Whether the app was restarted before the last reconnect.
   *
   * `true` when the `epoch` of the current connection differs from the one
   * of the connection before. Events were lost then, and their number is
   * not known.
   */
  get serverRestarted() {
    return this.#restarted;
  }

  /**
   * Number of events of the phone during the last interruption.
   *
   * Counted over all events, subscribed or not. `0` after the first
   * connect, `null` when the app was restarted and the number is not known.
   * @returns {number | null}
   */
  get missedEvents() {
    return this.#missed;
  }

  /**
   * Opens the connection, checks the certificate and logs in.
   *
   * Makes one attempt. Rejects with `CertificateMismatchError`,
   * `AuthenticationError`, `LicenceRequiredError`, `ProtocolError` or
   * `ConnectionInterrupted`. Every request connects by itself when needed,
   * so calling this method is optional.
   *
   * @returns {Promise<this>} The client, for
   *   `await using dock = await new SmsDockClient(url).connect()`.
   */
  async connect() {
    await this.close();
    try {
      await this.#ensureOpen();
    } catch (error) {
      if (!isTransport(error)) {
        throw error;
      }
      this.#abandon(error);
      throw fatalError(error) ?? interrupted(`Cannot connect to ${this.url}`, error);
    }
    return this;
  }

  /**
   * Closes the connection. Queued events are kept.
   *
   * Requests that still wait for a response are rejected with
   * `ConnectionInterrupted`; a running `events()` loop ends. The promise
   * resolves when the socket is closed.
   *
   * @returns {Promise<void>}
   */
  async close() {
    this.#generation += 1;
    for (const waiter of [...this.#eventWaiters]) {
      waiter.resolve(CLOSED);
    }
    const connection = this.#connection;
    if (connection !== null) {
      this.#down(connection, closedError(), false);
    }
    this.#pendingFailure = null;
    this.#lost.clear();
    if (connection !== null) {
      await this.#dispose(connection);
    }
  }

  // ------------------------------------------------------------------
  // Requests
  // ------------------------------------------------------------------

  /**
   * Sends one request and returns the `result` of its response.
   *
   * `fields` are the fields of the action as named in `API.md`. With
   * `retry` the request is sent again after a lost connection; pass
   * `retry: false` for anything that must not happen twice. The same holds
   * for the error `rate_limited`: the client waits the seconds the server
   * names and repeats the request. Rejects with `ApiError` when the server
   * answers with an error and `ConnectionInterrupted` when the connection
   * could not be restored.
   *
   * @param {string} action
   * @param {Record<string, unknown>} [fields]
   * @param {{retry?: boolean, timeout?: number}} [options]
   * @returns {Promise<any>}
   */
  async request(action, fields = {}, options = {}) {
    checkRequest(action, fields);
    checkOptions(options, ['retry', 'timeout']);
    const { retry = true, timeout } = options;
    const limit = timeout || this.timeout;
    const generation = this.#generation;
    let attempt = 0;
    for (;;) {
      try {
        await this.#ensureOpen();
        return await this.#requestPatiently(
          null,
          action,
          fields,
          limit,
          retry ? this.rateLimitRetries : 0,
        );
      } catch (error) {
        if (!isTransport(error)) {
          throw error;
        }
        const what = `performing '${action}'`;
        if (!retry) {
          this.#abandon(error);
          throw (
            fatalError(error) ?? interrupted(`Connection interrupted while ${what}`, error)
          );
        }
        await this.#recover(what, error, attempt, generation);
        attempt += 1;
      }
    }
  }

  /**
   * Sends a request without waiting and returns its id.
   *
   * Fetch the response with `waitForResponse()`. Unlike `request()`, these
   * two methods do not reconnect and do not repeat a request that was
   * answered with `rate_limited`. Use them to keep control of each frame;
   * for requests that just run side by side, `Promise.all()` over
   * `request()` is enough.
   *
   * @param {string} action
   * @param {Record<string, unknown>} [fields]
   * @returns {Promise<string>}
   */
  async sendRequest(action, fields = {}) {
    checkRequest(action, fields);
    try {
      await this.#ensureOpen();
      return this.#sendFrame(this.#current(), action, fields);
    } catch (error) {
      if (!isTransport(error)) {
        throw error;
      }
      this.#abandon(error);
      throw (
        fatalError(error) ??
        interrupted(`Connection interrupted while sending '${action}'`, error)
      );
    }
  }

  /**
   * Returns the result of a request sent with `sendRequest()`.
   *
   * Rejects with `ApiError` when the response is an error, `TimeoutError`
   * when none arrives in time and `ConnectionInterrupted` when the
   * connection was lost.
   *
   * @param {string} requestId
   * @param {{timeout?: number}} [options]
   * @returns {Promise<any>}
   */
  async waitForResponse(requestId, options = {}) {
    checkOptions(options, ['timeout']);
    const key = String(requestId);
    const label = `Connection interrupted while waiting for '${key}'`;
    const lost = this.#lost.get(key);
    if (lost !== undefined) {
      this.#lost.delete(key);
      throw fatalError(lost) ?? interrupted(label, lost);
    }
    const connection = this.#connection;
    const entry = connection === null ? undefined : connection.outstanding.get(key);
    if (entry === undefined || entry.waiter !== null) {
      throw new RangeError(`No request with the id '${key}' is open`);
    }
    try {
      return await this.#awaitResponse(
        connection,
        key,
        `request ${key}`,
        options.timeout || this.timeout,
      );
    } catch (error) {
      if (error instanceof TransportError) {
        throw fatalError(error) ?? interrupted(label, error);
      }
      throw error;
    }
  }

  /**
   * Returns phone, SIMs, permissions, licence and limits.
   * @returns {Promise<import('../index.d.ts').Status>}
   */
  status() {
    return this.request('status');
  }

  /**
   * Checks the connection. Returns `{time: ms}` of the phone.
   * @returns {Promise<{time: number}>}
   */
  ping() {
    return this.request('ping');
  }

  /**
   * Returns one page of conversations, newest first.
   *
   * `q` searches text, numbers and contact names. `unread` keeps only
   * conversations with unread messages. `sim` is a subscription id.
   * `cursor` is the `nextCursor` of the previous page.
   *
   * @param {{q?: string, unread?: boolean, sim?: number, limit?: number,
   *   cursor?: string}} [options]
   * @returns {Promise<import('../index.d.ts').Page<import('../index.d.ts').Conversation>>}
   */
  async conversations(options = {}) {
    checkOptions(options, ['q', 'unread', 'sim', 'limit', 'cursor']);
    const { q, unread, sim, limit = 50, cursor } = options;
    return this.request('conversations.list', {
      ...conversationFilter(q, unread, sim),
      ...paging(limit, cursor),
    });
  }

  /**
   * Yields conversations, newest first, page after page.
   *
   * @param {{q?: string, unread?: boolean, sim?: number, pageSize?: number,
   *   maxItems?: number}} [options]
   * @returns {AsyncGenerator<import('../index.d.ts').Conversation, void, void>}
   */
  iterConversations(options = {}) {
    checkOptions(options, ['q', 'unread', 'sim', 'pageSize', 'maxItems']);
    const { q, unread, sim, pageSize = 50, maxItems } = options;
    const filters = conversationFilter(q, unread, sim);
    return this.#iterate('conversations.list', filters, pageSize, maxItems);
  }

  /**
   * Returns one page of messages, newest first.
   *
   * `address` keeps messages from or to one number, `conversationId`
   * those of one conversation. `direction` is `'in'`, `'out'` or `'all'`.
   * `q` searches the text. `since` and `until` limit the time, both
   * inclusive; they are milliseconds since the epoch or `Date` objects and
   * are sent as the fields `from` and `to`. `sim` is a subscription id.
   * `limit` is 1 to 200. `cursor` is the `nextCursor` of the previous page.
   *
   * The result is `{items: [Message, …], nextCursor: string or null}`.
   *
   * @param {import('../index.d.ts').MessageFilter & {limit?: number,
   *   cursor?: string}} [options]
   * @returns {Promise<import('../index.d.ts').Page<import('../index.d.ts').Message>>}
   */
  async messages(options = {}) {
    checkOptions(options, [...MESSAGE_FILTERS, 'limit', 'cursor']);
    const { limit = 50, cursor } = options;
    return this.request('messages.list', {
      ...messageFilter(options),
      ...paging(limit, cursor),
    });
  }

  /**
   * Yields messages, newest first, following `nextCursor`.
   *
   * The filters are those of `messages()`. `pageSize` is the `limit` of
   * each request. `maxItems` ends the iteration after that many messages;
   * without it all of them are yielded.
   *
   * @param {import('../index.d.ts').MessageFilter & {pageSize?: number,
   *   maxItems?: number}} [options]
   * @returns {AsyncGenerator<import('../index.d.ts').Message, void, void>}
   */
  iterMessages(options = {}) {
    checkOptions(options, [...MESSAGE_FILTERS, 'pageSize', 'maxItems']);
    const { pageSize = 50, maxItems } = options;
    return this.#iterate('messages.list', messageFilter(options), pageSize, maxItems);
  }

  /**
   * Returns one message. Rejects with `not_found` if it does not exist.
   *
   * @param {number} messageId
   * @returns {Promise<import('../index.d.ts').Message>}
   */
  async getMessage(messageId) {
    return this.request('messages.get', { messageId: integer(messageId, 'messageId') });
  }

  /**
   * Sends an SMS and returns the SendResult.
   *
   * `to` is the phone number, `body` the text (at most 10 SMS parts),
   * `sim` a subscription id (default: the SMS SIM of the phone). The
   * message is on its way when the promise resolves; use `waitForResult()`
   * for the outcome.
   *
   * `requestId` makes the request safe to repeat: the phone sends a
   * message with the same `requestId` only once and remembers the id for
   * 30 days. A repeat returns the result of the first request with the
   * current `status`, which may be `sent` already. A repeat never sends
   * again, also not after the first attempt failed: the status is `failed`
   * then, and a new attempt needs a new `requestId`. A repeat with another
   * recipient or text fails with `invalid_request`.
   *
   * Without `requestId` the client creates a random one. If the connection
   * drops before the response arrives, the request is repeated with the
   * same id.
   *
   * In the result `to` is the number as it is sent, without spaces and
   * other ignored characters.
   *
   * @param {string} to
   * @param {string} body
   * @param {{sim?: number, requestId?: string}} [options]
   * @returns {Promise<import('../index.d.ts').SendResult>}
   */
  async send(to, body, options = {}) {
    checkOptions(options, ['sim', 'requestId']);
    if (typeof to !== 'string' || !to.trim()) {
      throw new RangeError("'to' must be a phone number");
    }
    if (typeof body !== 'string' || !body) {
      throw new RangeError("'body' must be a text");
    }
    let { requestId } = options;
    if (requestId === undefined || requestId === null) {
      requestId = randomUUID().replaceAll('-', '');
    } else if (typeof requestId !== 'string' || !REQUEST_ID_PATTERN.test(requestId)) {
      throw new RangeError('requestId must be 1 to 64 characters of A-Z a-z 0-9 . _ -');
    }
    const fields = { to, body, requestId };
    if (options.sim !== undefined && options.sim !== null) {
      fields.sim = integer(options.sim, 'sim');
    }
    return this.request('messages.send', fields);
  }

  /**
   * Sends one text to several numbers, one SMS each, and returns the
   * GroupSendResult.
   *
   * `to` holds 1 to 20 phone numbers; repeated numbers are sent once.
   * Every recipient gets a separate SMS and does not see the others. The
   * result holds one SendResult per recipient in `messages`; their request
   * ids are `<requestId>.1`, `<requestId>.2` and so on, and
   * `waitForResult()` and `messageStatus()` take them like those of
   * single messages.
   *
   * The phone checks the whole group before it sends anything: a number
   * that is not a phone number (`invalid_number`), a text that is too
   * long, fewer messages left in the daily limit of the free version than
   * the group has recipients (`quota_exceeded`) or more SMS parts than the
   * 20 of one minute, parts times recipients (`invalid_request`), refuse
   * the whole group. A refusal by the phone for one recipient does not
   * stop the others; that member has the status `failed` in `messages`.
   *
   * `requestId` has at most 61 characters here, so that the member suffix
   * fits, and works as for `send()`: the same id with the same text and
   * numbers is not sent again but answered with the stored members and
   * their current status.
   *
   * @param {string[]} to
   * @param {string} body
   * @param {{sim?: number, requestId?: string}} [options]
   * @returns {Promise<import('../index.d.ts').GroupSendResult>}
   */
  async sendGroup(to, body, options = {}) {
    checkOptions(options, ['sim', 'requestId']);
    const numbers = Array.isArray(to) && to.every((n) => typeof n === 'string' && n.trim());
    if (!numbers || to.length === 0) {
      throw new RangeError("'to' must be a list of phone numbers");
    }
    if (to.length > MAX_GROUP_RECIPIENTS) {
      throw new RangeError(`'to' holds more than ${MAX_GROUP_RECIPIENTS} numbers`);
    }
    if (typeof body !== 'string' || !body) {
      throw new RangeError("'body' must be a text");
    }
    let { requestId } = options;
    if (requestId === undefined || requestId === null) {
      requestId = randomUUID().replaceAll('-', '');
    } else if (typeof requestId !== 'string' || !GROUP_REQUEST_ID_PATTERN.test(requestId)) {
      throw new RangeError(
        'requestId of a group must be 1 to 61 characters of A-Z a-z 0-9 . _ -',
      );
    }
    const fields = { to: [...to], body, requestId };
    if (options.sim !== undefined && options.sim !== null) {
      fields.sim = integer(options.sim, 'sim');
    }
    return this.request('messages.sendGroup', fields);
  }

  /**
   * Returns the outbox entry of a message sent through SMS Dock.
   *
   * @param {string} requestId
   * @returns {Promise<import('../index.d.ts').OutboxEntry>}
   */
  messageStatus(requestId) {
    return this.request('messages.status', { requestId });
  }

  /**
   * Waits until a sent message succeeded or failed.
   *
   * Returns the outbox entry once its `status` is `sent`, `delivered` or
   * `failed`. With `delivered: true` the status `sent` is not enough: the
   * method waits for the delivery report, which some networks never send.
   * Check `status` and `errorCode` of the result; a failed message does
   * not reject.
   *
   * The method asks the server with `messages.status`: soon after the
   * start, then less often, at last every `pollInterval` seconds. If the
   * connection is subscribed to `sms.sent`, `sms.delivered` and
   * `sms.failed` it asks as soon as such an event arrives. The events stay
   * in the queue. Rejects with `TimeoutError` after `timeout` seconds
   * (default: the timeout of the client).
   *
   * @param {string} requestId
   * @param {{timeout?: number, delivered?: boolean, pollInterval?: number}}
   *   [options]
   * @returns {Promise<import('../index.d.ts').OutboxEntry>}
   */
  async waitForResult(requestId, options = {}) {
    checkOptions(options, ['timeout', 'delivered', 'pollInterval']);
    const { timeout, delivered = false, pollInterval = 1 } = options;
    if (!(pollInterval > 0)) {
      throw new RangeError('pollInterval must be positive');
    }
    const final = delivered ? ['delivered', 'failed'] : ['sent', 'delivered', 'failed'];
    const deadline = monotonic() + (timeout || this.timeout);
    let pause = Math.min(FIRST_POLL, pollInterval);
    for (;;) {
      // Only events that arrive after this request can tell something new.
      const since = this.#eventSerial;
      const entry = await this.messageStatus(requestId);
      if (final.includes(entry.status)) {
        return entry;
      }
      const remaining = deadline - monotonic();
      if (remaining <= 0) {
        throw new TimeoutError(
          `No result for the message '${requestId}' in time; ` +
            `its status is '${entry.status}'`,
        );
      }
      const outcome = (event) =>
        OUTCOME_EVENTS.includes(event.event) &&
        isObject(event.data) &&
        event.data.requestId === requestId;
      const found = await this.#nextEvent(
        monotonic() + Math.min(pause, remaining),
        'waiting for the result',
        outcome,
        { take: false, since },
      );
      if (found === CLOSED) {
        throw closedError();
      }
      pause = Math.min(pause * 2, pollInterval);
    }
  }

  /**
   * Deletes one message. Returns `{deleted: 1, mode: …}`.
   *
   * With mode `hidden` the message disappears from SMS Dock but stays on
   * the phone. If the connection drops after the server deleted the
   * message, the repeated request fails with `not_found`.
   *
   * @param {number} messageId
   * @returns {Promise<{deleted: number, mode: string}>}
   */
  async deleteMessage(messageId) {
    return this.request('messages.delete', {
      messageId: integer(messageId, 'messageId'),
    });
  }

  /**
   * Deletes a conversation. Returns `{deleted: n, mode: …}`.
   *
   * @param {number} conversationId
   * @returns {Promise<{deleted: number, mode: string}>}
   */
  async deleteConversation(conversationId) {
    return this.request('conversations.delete', {
      conversationId: integer(conversationId, 'conversationId'),
    });
  }

  /**
   * Marks all messages of a conversation as read.
   *
   * Returns `{ok: true}`. Android lets only the default SMS app change
   * messages; otherwise the error is `not_default_app`.
   *
   * @param {number} conversationId
   * @returns {Promise<{ok: boolean}>}
   */
  async markRead(conversationId) {
    return this.request('conversations.markRead', {
      conversationId: integer(conversationId, 'conversationId'),
    });
  }

  /**
   * Returns one page of contacts, sorted by name.
   *
   * `q` searches names and numbers.
   *
   * @param {{q?: string, limit?: number, cursor?: string}} [options]
   * @returns {Promise<import('../index.d.ts').Page<import('../index.d.ts').Contact>>}
   */
  async contacts(options = {}) {
    checkOptions(options, ['q', 'limit', 'cursor']);
    const { q, limit = 50, cursor } = options;
    return this.request('contacts.list', {
      ...(given(q) ? { q } : {}),
      ...paging(limit, cursor),
    });
  }

  /**
   * Yields contacts, sorted by name, page after page.
   *
   * @param {{q?: string, pageSize?: number, maxItems?: number}} [options]
   * @returns {AsyncGenerator<import('../index.d.ts').Contact, void, void>}
   */
  iterContacts(options = {}) {
    checkOptions(options, ['q', 'pageSize', 'maxItems']);
    const { q, pageSize = 50, maxItems } = options;
    return this.#iterate('contacts.list', given(q) ? { q } : {}, pageSize, maxItems);
  }

  // ------------------------------------------------------------------
  // Events
  // ------------------------------------------------------------------

  /**
   * Asks for events and returns the subscription now in force.
   *
   * `events` is one name or several, or `'*'` for all; the names are added
   * to the subscription. `address` limits the `sms.*` events to one number
   * and replaces the limit set before; without it the limit is removed.
   * Numbers are compared as the phone does: `+43 699 1234567` and
   * `0699 1234567` are the same.
   *
   * In the result `*` is replaced by the names it stands for. The client
   * remembers the subscription and restores it after a reconnect.
   *
   * @param {string | Iterable<string>} events
   * @param {{address?: string}} [options]
   * @returns {Promise<{events: string[], address: string | null}>}
   */
  async subscribe(events, options = {}) {
    checkOptions(options, ['address']);
    const fields = { events: eventNames(events) };
    if (given(options.address)) {
      fields.address = options.address;
    }
    const result = await this.request('subscribe', fields);
    this.#rememberSubscription(result);
    return result;
  }

  /**
   * Stops events and returns the subscription now in force.
   *
   * `events` is one name or several, or `'*'` for all. When no name is
   * left, the limit to one number is removed too.
   *
   * @param {string | Iterable<string>} events
   * @returns {Promise<{events: string[], address: string | null}>}
   */
  async unsubscribe(events) {
    const result = await this.request('unsubscribe', { events: eventNames(events) });
    this.#rememberSubscription(result);
    return result;
  }

  /**
   * Yields events as they arrive.
   *
   * Events that were queued while nobody read them come first. `timeout`
   * is the longest time in seconds to wait for the next event; the
   * iteration ends when it passes. Without it the loop waits without
   * limit. Subscribe first, a new connection gets no events.
   *
   * After a lost connection the generator reconnects and goes on. It
   * throws `ConnectionInterrupted` when that fails and ends quietly when
   * `close()` is called.
   *
   * @param {{timeout?: number}} [options]
   * @returns {AsyncGenerator<import('../index.d.ts').SmsDockEvent, void, void>}
   */
  events(options = {}) {
    checkOptions(options, ['timeout']);
    return this.#eventLoop(options.timeout);
  }

  async *#eventLoop(timeout) {
    for (;;) {
      const deadline = given(timeout) ? monotonic() + timeout : null;
      const event = await this.#nextEvent(deadline, 'waiting for events', () => true);
      if (event === null || event === CLOSED) {
        return;
      }
      yield event;
    }
  }

  /**
   * Returns the next event with this name.
   *
   * `name` is one event name or several. `predicate` is called with each
   * candidate and must return `true` for the wanted one. Other events stay
   * in the queue. Rejects with `TimeoutError` after `timeout` seconds
   * (default: the timeout of the client).
   *
   * @param {string | Iterable<string>} name
   * @param {{timeout?: number,
   *   predicate?: (event: import('../index.d.ts').SmsDockEvent) => boolean}}
   *   [options]
   * @returns {Promise<import('../index.d.ts').SmsDockEvent>}
   */
  async waitForEvent(name, options = {}) {
    checkOptions(options, ['timeout', 'predicate']);
    const { timeout, predicate } = options;
    const names = new Set(eventNames(name));
    const wanted = (event) => {
      if (!names.has('*') && !names.has(event.event)) {
        return false;
      }
      return !predicate || Boolean(predicate(event));
    };
    const label = [...names].sort().join(', ');
    const deadline = monotonic() + (timeout || this.timeout);
    const event = await this.#nextEvent(deadline, `waiting for ${label}`, wanted);
    if (event === CLOSED) {
      throw closedError();
    }
    if (event === null) {
      throw new TimeoutError(`No event ${label} in time`);
    }
    return event;
  }

  // ------------------------------------------------------------------
  // Internals: opening a connection
  // ------------------------------------------------------------------

  /**
   * Makes sure a logged-in connection exists.
   *
   * All callers share one attempt. Code that runs inside the `onReconnect`
   * hook belongs to the open in progress and must not wait for it.
   */
  async #ensureOpen() {
    for (;;) {
      const opening = this.#opening;
      if (opening !== null && !insideHookOf(opening)) {
        await opening.promise;
        continue;
      }
      if (this.connected) {
        return;
      }
      const failure = this.#pendingFailure;
      if (failure !== null) {
        this.#pendingFailure = null;
        throw failure;
      }
      const started = { promise: null, parent: opening };
      started.promise = this.#open(started).finally(() => {
        this.#opening = started.parent;
      });
      // The callers handle the rejection; this keeps it from being unhandled.
      started.promise.catch(() => {});
      this.#opening = started;
      await started.promise;
      return;
    }
  }

  /** Connects, pins the certificate, logs in, restores the subscription. */
  async #open(opening) {
    if (this.insecure && !this.#warned) {
      this.#warned = true;
      process.stderr.write(
        'smsdock: WARNING: the certificate of the server is not ' +
          'checked (insecure). Anyone on the network can read the ' +
          'password and the messages.\n',
      );
    }
    const generation = this.#generation;
    const connection = await this.#dial();
    if (generation !== this.#generation) {
      connection.down = true;
      connection.socket.destroy();
      throw closedError();
    }
    this.#connection = connection;
    this.connectionCount += 1;
    const reconnected = this.#epoch !== null;
    try {
      await this.#handshake(connection);
      connection.ready = true;
      this.#startKeepAlive(connection);
      const hook = this.onReconnect;
      if (reconnected && typeof hook === 'function') {
        const context = { opening, parent: hookContext.getStore() };
        await hookContext.run(context, () => hook(this));
      }
    } catch (error) {
      if (isTransport(error)) {
        this.#down(connection, error, false);
        connection.ws.terminate();
      } else {
        this.#down(connection, new TransportError('The connection was closed'), false);
        await this.#dispose(connection);
      }
      throw error;
    }
  }

  /**
   * Opens the pinned TLS connection and the WebSocket on top of it.
   *
   * The TLS socket is made here and its certificate compared before the
   * WebSocket library gets to see it, so the upgrade request is never
   * written to a server with another certificate.
   */
  async #dial() {
    const { host, port } = this.#target;
    const pin = this.#pin;
    const started = monotonic();
    const { socket } = await connectTls(host, port, pin, this.connectTimeout);
    const remaining = Math.max(0.05, this.connectTimeout - (monotonic() - started));
    return new Promise((resolve, reject) => {
      let settled = false;
      const ws = new WebSocket(this.url, {
        createConnection: () => socket,
        maxPayload: MAX_INCOMING_BYTES,
        perMessageDeflate: false,
        followRedirects: false,
      });
      const connection = {
        ws,
        socket,
        ready: false,
        down: false,
        alive: true,
        keepAlive: null,
        refused: null,
        lastError: null,
        helloSeen: false,
        hello: deferred(),
        /** id → `{response, waiter}` */
        outstanding: new Map(),
      };
      ws.on('upgrade', (response) => {
        // Once more, on the connection that is going to carry the password.
        try {
          if (response.socket !== socket) {
            throw new ProtocolError(
              'The WebSocket does not use the connection whose certificate ' +
                'was checked; refusing to go on',
            );
          }
          if (pin !== null) {
            checkPin(peerCertificate(socket), pin);
          }
        } catch (error) {
          connection.refused = error;
          ws.terminate();
        }
      });
      // The timeout of the library does not run on a socket that is
      // connected already.
      const timer = setTimeout(() => {
        connection.refused = new TransportError(
          `The WebSocket handshake timed out after ${this.connectTimeout} seconds`,
        );
        ws.terminate();
      }, remaining * 1000);
      ws.on('open', () => {
        clearTimeout(timer);
        settled = true;
        resolve(connection);
      });
      ws.on('error', (error) => {
        connection.lastError = error;
      });
      ws.on('close', (code, reason) => {
        const error = connection.refused ?? closeError(code, reason, connection.lastError);
        clearTimeout(timer);
        if (!settled) {
          settled = true;
          socket.destroy();
          reject(error);
          return;
        }
        this.#down(connection, error, true);
      });
      ws.on('message', (data, isBinary) => {
        this.#onMessage(connection, data, isBinary);
      });
      ws.on('pong', () => {
        connection.alive = true;
      });
    });
  }

  async #handshake(connection) {
    const hello = await within(connection.hello.promise, this.timeout, () =>
      mark(new TimeoutError('The server sent no hello in time'), connection),
    );
    if (hello.event !== 'hello') {
      // Not the frame itself: it could hold the text of a message.
      throw new ProtocolError(
        `Expected 'hello', received a frame with the keys ${keysOf(hello)}`,
      );
    }
    if (hello.protocolVersion !== API_PROTOCOL_VERSION) {
      throw new ProtocolError(
        'The server speaks protocol version ' +
          `${JSON.stringify(hello.protocolVersion)}, this client version ` +
          `${API_PROTOCOL_VERSION}`,
      );
    }
    const result = await this.#requestPatiently(
      connection,
      'authenticate',
      { password: this.#password },
      this.timeout,
      this.rateLimitRetries,
    );
    if (result.protocolVersion !== API_PROTOCOL_VERSION) {
      throw new ProtocolError(
        'The login answered with protocol version ' +
          `${JSON.stringify(result.protocolVersion)}`,
      );
    }
    this.serverInfo = isObject(result.app) ? result.app : null;
    this.#noteLogin(result.epoch, result.latestSeq);
    const subscription = this.#subscription;
    if (subscription !== null && subscription.events.length > 0) {
      const fields = { events: [...subscription.events] };
      if (subscription.address !== null) {
        fields.address = subscription.address;
      }
      await this.#requestPatiently(
        connection,
        'subscribe',
        fields,
        this.timeout,
        this.rateLimitRetries,
      );
    }
  }

  /** Compares epoch and sequence number with the connection before. */
  #noteLogin(epoch, latest) {
    if (!Number.isInteger(epoch) || !Number.isInteger(latest)) {
      throw new ProtocolError("The login answered without 'epoch' and 'latestSeq'");
    }
    if (this.#epoch === null) {
      this.#restarted = false;
      this.#missed = 0;
    } else if (epoch !== this.#epoch) {
      this.#restarted = true;
      this.#missed = null;
    } else {
      this.#restarted = false;
      this.#missed = Math.max(0, latest - (this.#latestSeq ?? 0));
    }
    this.#epoch = epoch;
    this.#latestSeq = latest;
  }

  /** Pings the server like it pings the client; a dead line is dropped. */
  #startKeepAlive(connection) {
    connection.keepAlive = setInterval(() => {
      if (!connection.alive) {
        this.#down(
          connection,
          new TransportError('The server did not answer the keep-alive ping'),
          true,
        );
        connection.ws.terminate();
        return;
      }
      connection.alive = false;
      try {
        connection.ws.ping();
      } catch {
        // The close event reports it.
      }
    }, PING_INTERVAL * 1000);
    connection.keepAlive.unref();
  }

  // ------------------------------------------------------------------
  // Internals: closing and failing
  // ------------------------------------------------------------------

  /**
   * Notes that a connection is gone and tells everybody who waits on it.
   *
   * With `remember`, a failure that nobody waited for is kept for the next
   * operation, which then decides whether to reconnect.
   */
  #down(connection, error, remember) {
    if (connection.down) {
      return;
    }
    connection.down = true;
    clearInterval(connection.keepAlive);
    const current = this.#connection === connection;
    if (current) {
      this.#connection = null;
    }
    let told = connection.hello.settle(null, error);
    for (const [key, entry] of connection.outstanding) {
      if (entry.waiter !== null) {
        entry.waiter.reject(error);
        told = true;
      } else if (error instanceof TransportError) {
        this.#lost.set(key, error);
        if (this.#lost.size > MAX_LOST_REQUESTS) {
          this.#lost.delete(this.#lost.keys().next().value);
        }
      }
    }
    connection.outstanding.clear();
    if (current) {
      for (const waiter of [...this.#eventWaiters]) {
        waiter.reject(error);
        told = true;
      }
      if (remember && !told && connection.ready) {
        this.#pendingFailure = error;
      }
    }
  }

  /** Drops the connection a timeout belongs to: it is presumed dead. */
  #abandon(error) {
    const connection = error[kConnection];
    if (connection === undefined || connection.down) {
      return;
    }
    this.#down(
      connection,
      new TransportError('The connection was closed after a request got no response'),
      false,
    );
    connection.ws.terminate();
  }

  /** Closes the WebSocket politely; resolves when the socket is gone. */
  #dispose(connection) {
    return new Promise((resolve) => {
      const ws = connection.ws;
      if (ws.readyState === WebSocket.CLOSED) {
        resolve();
        return;
      }
      const timer = setTimeout(
        () => ws.terminate(),
        Math.min(this.timeout, CLOSE_TIMEOUT) * 1000,
      );
      ws.once('close', () => {
        clearTimeout(timer);
        resolve();
      });
      if (ws.readyState === WebSocket.OPEN) {
        ws.close(1000);
      } else if (ws.readyState === WebSocket.CONNECTING) {
        ws.terminate();
      }
    });
  }

  /**
   * Waits before the next attempt after a failure of the connection.
   *
   * Throws instead when the failure is final, no attempt is left or the
   * client was closed meanwhile.
   */
  async #recover(what, error, attempt, generation) {
    this.#abandon(error);
    if (generation !== this.#generation) {
      throw closedError();
    }
    const fatal = fatalError(error);
    if (fatal !== null) {
      throw fatal;
    }
    const attempts = this.reconnectAttempts;
    if (attempts !== null && attempt >= attempts) {
      throw interrupted(`Connection interrupted while ${what}`, error);
    }
    const exponent = Math.min(attempt, MAX_BACKOFF_EXPONENT);
    await this._sleep(
      Math.min(this.reconnectDelay * 2 ** exponent, this.maxReconnectDelay),
    );
    if (generation !== this.#generation) {
      throw closedError();
    }
  }

  /**
   * Waits for some seconds. The tests replace it.
   * @param {number} seconds
   * @returns {Promise<void>}
   * @private
   */
  _sleep(seconds) {
    return new Promise((resolve) => {
      setTimeout(resolve, seconds * 1000);
    });
  }

  // ------------------------------------------------------------------
  // Internals: frames
  // ------------------------------------------------------------------

  #current() {
    const connection = this.#connection;
    if (connection === null || !connection.ready || connection.down) {
      throw new TransportError('No connection to SMS Dock');
    }
    return connection;
  }

  /**
   * Sends a request; waits and repeats it while it is rate limited.
   *
   * Only when the server names the seconds to wait. The limit of messages
   * per minute comes without them and is reported at once. Without
   * `connection` the connection of the moment is used for every attempt.
   */
  async #requestPatiently(connection, action, fields, timeout, retries) {
    let refused = 0;
    for (;;) {
      try {
        const target = connection ?? this.#current();
        const key = this.#sendFrame(target, action, fields);
        return await this.#awaitResponse(target, key, `'${action}'`, timeout);
      } catch (error) {
        if (!(error instanceof ApiError)) {
          throw error;
        }
        const wait = error.retryAfter;
        if (
          error.code !== 'rate_limited' ||
          wait === null ||
          wait > timeout ||
          refused >= retries
        ) {
          throw error;
        }
        refused += 1;
        await this._sleep(Math.max(0, wait));
      }
    }
  }

  #sendFrame(connection, action, fields) {
    if (connection.down || connection.ws.readyState !== WebSocket.OPEN) {
      throw new TransportError('No connection to SMS Dock');
    }
    const id = `js-${this.#requestNumber + 1}`;
    const frame = JSON.stringify({ id, action, ...fields });
    if (Buffer.byteLength(frame, 'utf8') > this._maxFrameBytes) {
      throw new RangeError(`The request is larger than ${MAX_FRAME_BYTES / 1024} KiB`);
    }
    this.#requestNumber += 1;
    connection.outstanding.set(id, { response: null, waiter: null });
    connection.ws.send(frame, () => {
      // A failed write shows up as the close of the connection.
    });
    return id;
  }

  #awaitResponse(connection, key, label, timeout) {
    return new Promise((resolve, reject) => {
      const entry = connection.outstanding.get(key);
      if (entry === undefined) {
        reject(new TransportError('No connection to SMS Dock'));
        return;
      }
      const finish = (response) => {
        connection.outstanding.delete(key);
        try {
          resolve(resultOf(response, label));
        } catch (error) {
          reject(error);
        }
      };
      if (entry.response !== null) {
        finish(entry.response);
        return;
      }
      const timer = setTimeout(() => {
        entry.waiter = null;
        reject(mark(new TimeoutError(`No response to ${label} in time`), connection));
      }, timeout * 1000);
      entry.waiter = {
        resolve: (response) => {
          clearTimeout(timer);
          finish(response);
        },
        reject: (error) => {
          clearTimeout(timer);
          reject(error);
        },
      };
    });
  }

  #onMessage(connection, data, isBinary) {
    if (connection.down) {
      return;
    }
    let message;
    try {
      message = parseFrame(data, isBinary);
    } catch (error) {
      this.#down(connection, error, true);
      connection.ws.terminate();
      return;
    }
    const isEvent = 'event' in message;
    const sequence = message.seq;
    if (
      isEvent &&
      Number.isInteger(sequence) &&
      (this.#latestSeq === null || sequence > this.#latestSeq)
    ) {
      this.#latestSeq = sequence;
    }
    if (!connection.helloSeen) {
      connection.helloSeen = true;
      connection.hello.settle(message, null);
    } else if (isEvent) {
      this.#queueEvent(message);
    } else {
      this.#fileResponse(connection, message);
    }
  }

  /** Hands a response to the request that waits for it. */
  #fileResponse(connection, response) {
    const identifier = response.id;
    let key = identifier === null || identifier === undefined ? null : String(identifier);
    if (key === null && connection.outstanding.size === 1) {
      // The server could not read the id of the only open request.
      key = connection.outstanding.keys().next().value;
    }
    const entry = key === null ? undefined : connection.outstanding.get(key);
    if (entry === undefined || entry.response !== null) {
      // It answers a request nobody waits for any more.
      return;
    }
    entry.response = response;
    if (entry.waiter !== null) {
      entry.waiter.resolve(response);
    }
  }

  // ------------------------------------------------------------------
  // Internals: events
  // ------------------------------------------------------------------

  #queueEvent(event) {
    if (this.#events.length >= this.#eventQueueSize) {
      this.#events.shift();
      this.droppedEvents += 1;
    }
    this.#eventSerial += 1;
    this.#events.push({ serial: this.#eventSerial, event });
    for (const waiter of [...this.#eventWaiters]) {
      waiter.resolve('event');
    }
  }

  /** Resolves with `'event'`, `'timeout'` or `CLOSED`; rejects on a failure. */
  #signal(seconds) {
    return new Promise((resolve, reject) => {
      let timer = null;
      const waiter = {
        resolve: (value) => {
          clearTimeout(timer);
          this.#eventWaiters.delete(waiter);
          resolve(value);
        },
        reject: (error) => {
          clearTimeout(timer);
          this.#eventWaiters.delete(waiter);
          reject(error);
        },
      };
      this.#eventWaiters.add(waiter);
      if (seconds !== null) {
        timer = setTimeout(() => waiter.resolve('timeout'), seconds * 1000);
      }
    });
  }

  /**
   * Returns the first event that `accept` wants, from the queue or as soon
   * as it arrives; `null` when the deadline passed and `CLOSED` when the
   * client was closed. Reconnects when needed.
   *
   * With `take` the event leaves the queue. `since` skips events that
   * were queued before.
   */
  async #nextEvent(deadline, what, accept, options = {}) {
    const { take = true, since = 0 } = options;
    const generation = this.#generation;
    let seen = since;
    const scan = () => {
      for (let index = 0; index < this.#events.length; index += 1) {
        const { serial, event } = this.#events[index];
        if (serial > seen) {
          seen = serial;
          if (accept(event)) {
            if (take) {
              this.#events.splice(index, 1);
            }
            return event;
          }
        }
      }
      return null;
    };
    let attempt = 0;
    let found = scan();
    while (found === null) {
      try {
        await this.#ensureOpen();
        found = scan();
        if (found !== null) {
          break;
        }
        let wait = null;
        if (deadline !== null) {
          wait = deadline - monotonic();
          if (wait <= 0) {
            return null;
          }
        }
        const signal = await this.#signal(wait);
        if (signal === 'timeout') {
          return null;
        }
        if (signal === CLOSED) {
          return CLOSED;
        }
        attempt = 0;
        found = scan();
      } catch (error) {
        if (generation !== this.#generation) {
          return CLOSED;
        }
        if (!isTransport(error)) {
          throw error;
        }
        await this.#recover(what, error, attempt, generation);
        attempt += 1;
      }
    }
    return found;
  }

  #rememberSubscription(result) {
    if (!isObject(result) || !Array.isArray(result.events)) {
      throw new ProtocolError(
        `Malformed subscription: a result with the keys ${keysOf(result)}`,
      );
    }
    this.#subscription = {
      events: result.events.map(String),
      address: typeof result.address === 'string' ? result.address : null,
    };
  }

  // ------------------------------------------------------------------
  // Internals: paging
  // ------------------------------------------------------------------

  #iterate(action, filters, pageSize, maxItems) {
    if (given(maxItems) && !(maxItems >= 0)) {
      throw new RangeError('maxItems cannot be negative');
    }
    paging(pageSize, null);
    return this.#pages(action, filters, pageSize, given(maxItems) ? maxItems : null);
  }

  async *#pages(action, filters, pageSize, maxItems) {
    let cursor = null;
    let count = 0;
    while (maxItems === null || count < maxItems) {
      const limit = maxItems === null ? pageSize : Math.min(pageSize, maxItems - count);
      const page = await this.request(action, { ...filters, ...paging(limit, cursor) });
      if (!Array.isArray(page.items)) {
        throw new ProtocolError(`${action} returned no list of items`);
      }
      for (const item of page.items) {
        yield item;
        count += 1;
        if (maxItems !== null && count >= maxItems) {
          return;
        }
      }
      const next = page.nextCursor;
      if (next === null || next === undefined) {
        return;
      }
      if (typeof next !== 'string' || next === cursor) {
        throw new ProtocolError(`${action} returned the cursor ${JSON.stringify(next)}`);
      }
      cursor = next;
    }
  }
}

if (typeof Symbol.asyncDispose === 'symbol') {
  /** Lets `await using` close the client. */
  SmsDockClient.prototype[Symbol.asyncDispose] = function asyncDispose() {
    return this.close();
  };
}

// ----------------------------------------------------------------------
// Helpers
// ----------------------------------------------------------------------

function monotonic() {
  return performance.now() / 1000;
}

function given(value) {
  return value !== undefined && value !== null;
}

function isObject(value) {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

function keysOf(value) {
  return isObject(value) ? `[${Object.keys(value).join(', ')}]` : '[]';
}

function closedError() {
  return new ConnectionInterrupted('The client was closed');
}

/** Whether a new attempt may cure this error. */
function isTransport(error) {
  return (
    error instanceof TransportError ||
    (error instanceof TimeoutError && error[kConnection] !== undefined)
  );
}

function mark(error, connection) {
  Object.defineProperty(error, kConnection, { value: connection });
  return error;
}

/** Whether the code that calls runs inside the hook of this open. */
function insideHookOf(opening) {
  for (let context = hookContext.getStore(); context; context = context.parent) {
    if (context.opening === opening) {
      return true;
    }
  }
  return false;
}

function deferred() {
  let settled = false;
  let resolvePromise;
  let rejectPromise;
  const promise = new Promise((resolve, reject) => {
    resolvePromise = resolve;
    rejectPromise = reject;
  });
  promise.catch(() => {});
  return {
    promise,
    /** Returns whether this call settled the promise with an error. */
    settle(value, error) {
      if (settled) {
        return false;
      }
      settled = true;
      if (error) {
        rejectPromise(error);
        return true;
      }
      resolvePromise(value);
      return false;
    },
  };
}

function within(promise, seconds, makeError) {
  return new Promise((resolve, reject) => {
    const timer = setTimeout(() => reject(makeError()), seconds * 1000);
    promise.then(
      (value) => {
        clearTimeout(timer);
        resolve(value);
      },
      (error) => {
        clearTimeout(timer);
        reject(error);
      },
    );
  });
}

function parseUrl(url) {
  const problem = new RangeError(
    'SMS Dock needs a wss:// URL, for example wss://192.168.1.50:9443/ws',
  );
  // `new URL('wss:///ws')` would take `ws` for the host.
  if (typeof url !== 'string' || !/^wss:\/\/[^/?#\\]/i.test(url.trim())) {
    throw problem;
  }
  let parts;
  try {
    parts = new URL(url);
  } catch {
    throw problem;
  }
  if (parts.protocol !== 'wss:' || !parts.hostname) {
    throw problem;
  }
  return {
    host: parts.hostname.replace(/^\[|\]$/g, ''),
    port: parts.port ? Number(parts.port) : 443,
  };
}

function closeError(code, reason, lastError) {
  if (code && code !== 1006) {
    const text = reason ? reason.toString('utf8') : '';
    return new TransportError(`closed by the server with code ${code}`, {
      closeCode: code,
      closeReason: text,
    });
  }
  if (lastError) {
    return new TransportError(lastError.message || 'The connection was lost', {
      cause: lastError,
    });
  }
  return new TransportError('The connection was lost');
}

function parseFrame(data, isBinary) {
  if (isBinary) {
    throw new ProtocolError('The server sent a binary frame');
  }
  let decoded;
  try {
    decoded = JSON.parse(data.toString('utf8'));
  } catch {
    throw new ProtocolError('The server sent invalid JSON');
  }
  if (!isObject(decoded)) {
    throw new ProtocolError('The frame is not a JSON object');
  }
  return decoded;
}

function resultOf(response, label) {
  if (response.ok === true) {
    if (!isObject(response.result)) {
      throw new ProtocolError(`The result of ${label} is not a JSON object`);
    }
    return response.result;
  }
  if (response.ok !== false || !isObject(response.error)) {
    // Not the response itself: it could hold the text of a message.
    throw new ProtocolError(`Malformed response with the keys ${keysOf(response)}`);
  }
  throw apiError(response.error);
}

function checkRequest(action, fields) {
  if (typeof action !== 'string' || !action) {
    throw new TypeError('action must be the name of an action');
  }
  if (!isObject(fields)) {
    throw new TypeError('fields must be an object');
  }
  if ('id' in fields) {
    throw new RangeError("'id' is the id of the request; the client sets it");
  }
}

/** Refuses misspelt options such as `conversation_id` instead of ignoring them. */
function checkOptions(options, allowed) {
  if (!isObject(options)) {
    throw new TypeError('options must be an object');
  }
  for (const key of Object.keys(options)) {
    if (!allowed.includes(key)) {
      throw new TypeError(`Unknown option '${key}'; known are ${allowed.join(', ')}`);
    }
  }
}

function integer(value, label) {
  if (!Number.isSafeInteger(value)) {
    throw new TypeError(`${label} must be an integer`);
  }
  return value;
}

function millis(value, label) {
  if (value instanceof Date) {
    const time = value.getTime();
    if (Number.isNaN(time)) {
      throw new RangeError(`${label} is an invalid Date`);
    }
    return time;
  }
  if (typeof value !== 'number' || !Number.isFinite(value)) {
    throw new TypeError(`${label} must be milliseconds since the epoch or a Date`);
  }
  return Math.trunc(value);
}

function paging(limit, cursor) {
  if (!Number.isInteger(limit) || limit < 1 || limit > MAX_PAGE_SIZE) {
    throw new RangeError(`limit must be between 1 and ${MAX_PAGE_SIZE}`);
  }
  const fields = { limit };
  if (given(cursor)) {
    fields.cursor = cursor;
  }
  return fields;
}

function conversationFilter(q, unread, sim) {
  const filters = {};
  if (given(q)) {
    filters.q = q;
  }
  if (unread) {
    filters.unread = true;
  }
  if (given(sim)) {
    filters.sim = integer(sim, 'sim');
  }
  return filters;
}

function messageFilter(options) {
  const { address, conversationId, direction = 'all', q, since, until, sim, unread } =
    options;
  if (!DIRECTIONS.includes(direction)) {
    throw new RangeError("direction must be 'in', 'out' or 'all'");
  }
  const filters = {};
  if (given(address)) {
    filters.address = address;
  }
  if (given(conversationId)) {
    filters.conversationId = integer(conversationId, 'conversationId');
  }
  if (direction !== 'all') {
    filters.direction = direction;
  }
  if (given(q)) {
    filters.q = q;
  }
  if (given(since)) {
    filters.from = millis(since, 'since');
  }
  if (given(until)) {
    filters.to = millis(until, 'until');
  }
  if (given(sim)) {
    filters.sim = integer(sim, 'sim');
  }
  if (unread) {
    filters.unread = true;
  }
  return filters;
}

function eventNames(events) {
  let names;
  if (typeof events === 'string') {
    names = [events];
  } else if (events !== null && events !== undefined && Symbol.iterator in Object(events)) {
    names = [...events];
  } else {
    throw new TypeError('events must be one event name or several');
  }
  if (names.length === 0 || !names.every((name) => typeof name === 'string' && name)) {
    throw new RangeError('events must be one event name or several');
  }
  return names;
}
