/**
 * A fake SMS Dock server that speaks protocol version 1 of `API.md`.
 *
 * It keeps all data in memory and sends nothing. The tests run it inside
 * the test process. It can also run on its own to try the examples and the
 * command-line tool without a phone:
 *
 *     node test/fake_server.js --port 9443 --incoming-every 15
 *
 * The real server runs on the phone. This one follows `API.md`; where the
 * document leaves something open, a comment names the assumption. It is a
 * port of `python/tests/fake_server.py`.
 */

import { execFileSync } from 'node:child_process';
import { randomUUID } from 'node:crypto';
import { mkdtempSync, readFileSync, rmSync } from 'node:fs';
import https from 'node:https';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { pathToFileURL } from 'node:url';
import { parseArgs } from 'node:util';

import { WebSocketServer } from 'ws';

export const PROTOCOL_VERSION = 1;
export const MAX_FRAME_BYTES = 64 * 1024;
export const EVENT_NAMES = [
  'sms.received',
  'sms.queued',
  'sms.sent',
  'sms.delivered',
  'sms.failed',
  'sms.deleted',
  'conversations.changed',
];
const REQUEST_ID = /^[A-Za-z0-9._-]{1,64}$/;
const NUMBER = /^\+?[0-9]{3,15}$/;

/**
 * Creates a self-signed EC certificate like the one of the phone.
 *
 * Returns `null` when the `openssl` command is missing or fails. The
 * fingerprint is the one `openssl` prints, so that the tests compare the
 * client with an independent implementation.
 *
 * @param {string} directory
 * @param {string} [name]
 * @returns {{certificate: string, key: string, fingerprint: string,
 *   certificatePem: Buffer, keyPem: Buffer} | null}
 */
export function makeCertificate(directory, name = 'server') {
  const key = join(directory, `${name}.key`);
  const certificate = join(directory, `${name}.crt`);
  const run = (args) =>
    execFileSync('openssl', args, {
      encoding: 'utf8',
      timeout: 60_000,
      stdio: ['ignore', 'pipe', 'pipe'],
    });
  let shown;
  try {
    run(['ecparam', '-name', 'prime256v1', '-genkey', '-noout', '-out', key]);
    run([
      'req', '-x509', '-new', '-key', key, '-sha256', '-days', '2',
      '-subj', '/CN=SMS Dock', '-out', certificate,
    ]);
    shown = run(['x509', '-in', certificate, '-noout', '-fingerprint', '-sha256']);
  } catch {
    return null;
  }
  const fingerprint = shown.trim().split('=').pop();
  if (!/^([0-9A-F]{2}:){31}[0-9A-F]{2}$/.test(fingerprint)) {
    return null;
  }
  return {
    certificate,
    key,
    fingerprint,
    certificatePem: readFileSync(certificate),
    keyPem: readFileSync(key),
  };
}

/** An error response, optionally followed by a close frame. */
class Failure extends Error {
  constructor(code, message, closeCode = null, extra = {}) {
    super(message);
    this.error = { code, message, ...extra };
    this.closeCode = closeCode;
  }
}

/** Removes the characters that are ignored in phone numbers. */
export function normalizeNumber(number) {
  return number.trim().replace(/[\s\-./()]/g, '');
}

/** Whether an address is a phone number and not a sender name. */
export function isNumber(address) {
  return /^\+?[0-9]+$/.test(normalizeNumber(address));
}

/**
 * Returns a key that is equal for equal addresses where that is known: the
 * international form for numbers written with `+` or `00`, the bare digits
 * for other numbers, lower case for sender names.
 */
export function addressKey(address) {
  if (!isNumber(address)) {
    return address.trim().toLowerCase();
  }
  const digits = normalizeNumber(address);
  if (digits.startsWith('00') && digits.length > 4) {
    return `+${digits.slice(2)}`;
  }
  return digits;
}

/**
 * Whether two addresses mean the same peer ("Phone numbers" in API.md).
 *
 * `+43 699 1234567`, `0043 699 1234567` and `0699 1234567` are the same. A
 * number without country code matches if its digits without leading zeros,
 * at least 7, end the other number. Sender names are compared as text,
 * ignoring case.
 */
export function samePeer(first, second) {
  const a = addressKey(first);
  const b = addressKey(second);
  if (a === b) {
    return true;
  }
  if (!isNumber(first) || !isNumber(second)) {
    return false;
  }
  if (a.startsWith('+') && b.startsWith('+')) {
    return false;
  }
  const ends = (full, national) => {
    const significant = national.replace(/^0+/, '');
    return significant.length >= 7 && full.endsWith(significant);
  };
  if (a.startsWith('+')) {
    return ends(a, b);
  }
  if (b.startsWith('+')) {
    return ends(b, a);
  }
  return ends(a, b) || ends(b, a);
}

/** Returns the number of SMS parts of a text (simplified alphabet test). */
export function countParts(body) {
  const characters = [...body];
  const plain = characters.every((character) => character.codePointAt(0) < 128);
  const [single, multi] = plain ? [160, 153] : [70, 67];
  if (characters.length <= single) {
    return 1;
  }
  return Math.ceil(characters.length / multi);
}

/**
 * In-memory SMS Dock server on `wss://127.0.0.1:PORT/ws`.
 *
 * Properties such as `messages`, `sent` and `requests` may be read by the
 * tests; use the methods to change anything while clients are connected.
 */
export class FakeSmsDock {
  /**
   * @param {object} options
   * @param {NonNullable<ReturnType<typeof makeCertificate>>} options.tls
   * @param {string} [options.password]
   * @param {string} [options.host]
   * @param {number} [options.port]
   * @param {number} [options.authTimeout] Seconds.
   * @param {number} [options.maxClients]
   * @param {number} [options.sendDelay] Seconds.
   * @param {boolean} [options.deliveryReports]
   * @param {number} [options.requestsPerSecond]
   * @param {number} [options.maxRefused]
   */
  constructor(options) {
    this.password = options.password ?? 'secret123';
    this.tls = options.tls;
    this.host = options.host ?? '127.0.0.1';
    this.port = options.port ?? 0;
    this.authTimeout = options.authTimeout ?? 10;
    this.maxClients = options.maxClients ?? 8;
    this.sendDelay = options.sendDelay ?? 0.02;
    this.deliveryReports = options.deliveryReports ?? false;
    this.requestsPerSecond = options.requestsPerSecond ?? 20;
    this.maxRefused = options.maxRefused ?? 20;

    /** Identifies one run of the app; `restart()` changes it. */
    this.epoch = Date.now();
    this.protocolVersion = PROTOCOL_VERSION;
    this.licenceEnforced = false;
    this.licensed = false;
    this.deleteMode = 'hidden';
    this.failedLogins = 0;
    this.lockCount = 0;
    this.lockedUntil = 0;

    this.sims = [
      { subscriptionId: 1, slot: 0, name: 'Play', number: null, isDefault: true },
      {
        subscriptionId: 2, slot: 1, name: 'Work', number: '+4369087654321',
        isDefault: false,
      },
    ];
    this.contacts = [];
    this.messages = [];
    /** requestId → outbox entry */
    this.outbox = new Map();
    this.failNumbers = new Set();
    /** Numbers the phone refuses at once (`send_refused`). */
    this.refuseNumbers = new Set();
    /** Messages handed to the (imaginary) radio, one entry per SMS. */
    this.sent = [];
    /** Every request object received and not refused, in order. */
    this.requests = [];
    /** Number of frames answered with `rate_limited` by the limiter. */
    this.refusedCount = 0;
    /** WebSocket connections accepted so far. */
    this.connectionCount = 0;
    this.loginCount = 0;
    /** TCP connections accepted so far. */
    this.tcpConnectionCount = 0;
    /** HTTP requests (upgrades included) received so far. */
    this.httpRequestCount = 0;

    this._nextMessageId = 2800;
    this._conversationIds = new Map();
    this._bodies = new Map();
    this._sequence = 0;
    this._clients = [];
    this._drops = new Map();
    this._failures = new Map();
    this._results = new Map();
    this._ignored = new Map();
    this._framesBeforeResponse = [];
    this._hold = 0;
    this._held = [];
    this._timers = new Set();
    this._sockets = new Set();
    this._server = null;
    this._wss = null;
    this._pinger = null;
  }

  // ------------------------------------------------------------------
  // Life cycle
  // ------------------------------------------------------------------

  /** The address of the API. */
  get url() {
    return `wss://${this.host}:${this.port}/ws`;
  }

  /** Number of open connections. */
  get clientCount() {
    return this._clients.length;
  }

  /** Starts the server; resolves with the server when it listens. */
  async start() {
    const server = https.createServer({
      cert: this.tls.certificatePem,
      key: this.tls.keyPem,
    });
    const wss = new WebSocketServer({ noServer: true, maxPayload: MAX_FRAME_BYTES });
    server.on('connection', (socket) => {
      this.tcpConnectionCount += 1;
      this._sockets.add(socket);
      socket.on('close', () => this._sockets.delete(socket));
    });
    server.on('tlsClientError', () => {});
    server.on('request', (request, response) => {
      this.httpRequestCount += 1;
      response.writeHead(404, { 'Content-Type': 'text/plain' });
      response.end('Not found\n');
    });
    server.on('upgrade', (request, socket, head) => {
      this.httpRequestCount += 1;
      if (request.url.split('?', 1)[0] !== '/ws') {
        socket.end('HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\n\r\n');
        return;
      }
      wss.handleUpgrade(request, socket, head, (ws) => this._handle(ws));
    });
    await new Promise((resolve, reject) => {
      server.once('error', reject);
      server.listen(this.port, this.host, resolve);
    });
    this.port = server.address().port;
    this._server = server;
    this._wss = wss;
    this._pinger = setInterval(() => {
      for (const client of this._clients) {
        client.ws.ping();
      }
    }, 20_000);
    this._pinger.unref();
    return this;
  }

  /** Stops the server; clients see the close code 1001. */
  async stop() {
    const server = this._server;
    if (server === null) {
      return;
    }
    this._server = null;
    clearInterval(this._pinger);
    for (const timer of this._timers) {
      clearTimeout(timer);
    }
    this._timers.clear();
    for (const client of [...this._clients]) {
      client.ws.close(1001, 'server is stopping');
    }
    const closed = new Promise((resolve) => server.close(resolve));
    // Give the close frames a moment, then end what is left.
    await new Promise((resolve) => setTimeout(resolve, 20));
    for (const socket of this._sockets) {
      socket.destroy();
    }
    this._wss.close();
    await closed;
  }

  // ------------------------------------------------------------------
  // Controls for tests
  // ------------------------------------------------------------------

  /** Adds a contact with one number. */
  addContact(name, number, label = 'Mobile') {
    this.contacts.push({ name, numbers: [{ number, label }] });
  }

  /** Stores a message without sending an event. */
  addMessage(address, body, options = {}) {
    const {
      direction = 'in',
      timestamp = Date.now(),
      read = true,
      sim = 1,
      requestId = null,
    } = options;
    const incoming = direction === 'in';
    this._nextMessageId += 1;
    const message = {
      id: this._nextMessageId,
      conversationId: this._conversationId(address),
      address,
      contact: this._contactName(address),
      direction,
      body,
      timestamp,
      sentTimestamp: incoming ? timestamp - 2000 : null,
      read,
      status: incoming ? 'received' : 'sent',
      sim,
      requestId,
    };
    this.messages.push(message);
    return message;
  }

  /** Lets an SMS arrive: stores it and sends `sms.received`. */
  receiveSms(address, body, sim = 1) {
    const message = this.addMessage(address, body, { read: false, sim });
    this.emit('sms.received', { message });
    return message;
  }

  /** Sends an event to the subscribed clients. */
  emit(name, data) {
    this._sequence += 1;
    const event = { event: name, seq: this._sequence, data };
    for (const client of [...this._clients]) {
      if (client.authenticated && wants(client, name, data)) {
        this._send(client, event);
      }
    }
  }

  /**
   * Behaves like a restart of the app: all connections are lost, the
   * sequence numbers begin at 1 again and `epoch` changes. Messages and
   * the outbox are kept.
   */
  restart() {
    this.dropConnections();
    this._sequence = 0;
    this.epoch = Math.max(this.epoch + 1, Date.now());
  }

  /** Closes all connections: abruptly, or with a close code. */
  dropConnections(code = null, reason = '') {
    for (const client of [...this._clients]) {
      if (code === null) {
        this._abort(client);
      } else {
        client.ws.close(code, reason);
      }
    }
  }

  /**
   * Drops the connection at the next request with this action.
   *
   * `'before'` drops before the request is carried out, `'after'` after it
   * was carried out but before the response is sent. Calling it again
   * queues one more drop.
   */
  dropOn(action, when = 'after') {
    if (when !== 'before' && when !== 'after') {
      throw new RangeError("when must be 'before' or 'after'");
    }
    push(this._drops, action, when);
  }

  /** Answers the next request with this action with an error. */
  failNext(action, code, message = 'Injected failure.', extra = {}, closeCode = null) {
    push(this._failures, action, new Failure(code, message, closeCode, extra));
  }

  /** Answers the next request with this action with this result. */
  answerNext(action, result) {
    push(this._results, action, result);
  }

  /** Does not answer the next requests with this action at all. */
  ignoreNext(action, times = 1) {
    this._ignored.set(action, (this._ignored.get(action) ?? 0) + times);
  }

  /** Sends these frames to the client right before the next response. */
  beforeNextResponse(...frames) {
    this._framesBeforeResponse.push(...frames);
  }

  /** Holds the next `count` responses and sends them in reverse order. */
  reverseNextResponses(count = 2) {
    this._hold = count;
  }

  /** Locks logins as after too many wrong passwords. */
  lockLogins(seconds) {
    this.lockedUntil = now() + seconds;
  }

  /** Returns the actions received so far, in order. */
  actions() {
    return this.requests.map((request) => request.action);
  }

  /** Fills the server with a few contacts and conversations. */
  seedDemoData() {
    const time = Date.now();
    this.addContact('Anna Example', '+4369912345678');
    this.addContact('Ben Example', '+4369087654321', 'Work');
    const minute = 60_000;
    const rows = [
      ['+4369912345678', 'in', 'Are you coming on Friday?', 300],
      ['+4369912345678', 'out', 'Yes, at eight.', 290],
      ['+4369912345678', 'in', 'Great, see you then.', 280],
      ['+4369087654321', 'out', 'The server is up again.', 200],
      ['+4369087654321', 'in', 'Thanks!', 190],
      ['A1', 'in', 'Your invoice is ready.', 120],
      ['+436641234567', 'in', 'STATUS', 5],
    ];
    for (const [address, direction, body, age] of rows) {
      this.addMessage(address, body, {
        direction,
        timestamp: time - age * minute,
        read: age > 100,
      });
    }
  }

  // ------------------------------------------------------------------
  // Connections
  // ------------------------------------------------------------------

  _handle(ws) {
    if (this._clients.length >= this.maxClients) {
      ws.close(4409, 'too many clients');
      return;
    }
    const client = {
      ws,
      authenticated: false,
      events: new Set(),
      address: null,
      requestTimes: [],
      refused: 0,
    };
    this._clients.push(client);
    this.connectionCount += 1;
    const timer = setTimeout(() => {
      if (!client.authenticated) {
        ws.close(4401, 'not authenticated in time');
      }
    }, this.authTimeout * 1000);
    ws.on('error', () => {});
    ws.on('close', () => {
      clearTimeout(timer);
      const index = this._clients.indexOf(client);
      if (index >= 0) {
        this._clients.splice(index, 1);
      }
    });
    ws.on('message', (data, isBinary) => {
      if (isBinary) {
        ws.close(4400, 'binary frame');
        return;
      }
      this._onFrame(client, data.toString('utf8'));
    });
    this._send(client, {
      event: 'hello',
      protocolVersion: this.protocolVersion,
      authenticationRequired: true,
    });
  }

  _later(seconds, action) {
    const timer = setTimeout(() => {
      this._timers.delete(timer);
      action();
    }, seconds * 1000);
    this._timers.add(timer);
  }

  _abort(client) {
    client.ws.terminate();
  }

  _send(client, frame) {
    const text = typeof frame === 'string' ? frame : JSON.stringify(frame);
    if (client.ws.readyState === 1) {
      client.ws.send(text);
    }
  }

  _onFrame(client, frame) {
    let request = null;
    try {
      request = JSON.parse(frame);
    } catch {
      request = null;
    }
    const isObject =
      typeof request === 'object' && request !== null && !Array.isArray(request);
    if (!this._withinRate(client)) {
      // Every frame counts, the login and unreadable ones as well.
      let identifier = isObject ? request.id : null;
      if (typeof identifier !== 'string' && typeof identifier !== 'number') {
        identifier = null;
      }
      this.refusedCount += 1;
      this._send(client, {
        id: identifier,
        ok: false,
        error: { code: 'rate_limited', message: 'Too many requests.', retryAfter: 1 },
      });
      client.refused += 1;
      if (client.refused > this.maxRefused) {
        client.ws.close(4429, 'Too many requests.');
      }
      return;
    }
    client.refused = 0;
    if (!isObject) {
      this._respond(client, {
        id: null,
        ok: false,
        error: { code: 'invalid_json', message: 'The frame is not a JSON object.' },
      });
      return;
    }
    this.requests.push(request);
    const identifier = request.id ?? null;
    const action = request.action;
    const drops = typeof action === 'string' ? this._drops.get(action) : undefined;
    if (drops && drops[0] === 'before') {
      drops.shift();
      this._abort(client);
      return;
    }

    let closeCode = null;
    let response;
    try {
      const result = this._dispatch(client, request);
      if (result === null) {
        return;
      }
      response = { id: identifier, ok: true, result };
    } catch (error) {
      if (!(error instanceof Failure)) {
        throw error;
      }
      response = { id: identifier, ok: false, error: error.error };
      closeCode = error.closeCode;
    }

    if (drops && drops[0] === 'after') {
      drops.shift();
      this._abort(client);
      return;
    }
    this._respond(client, response);
    if (closeCode !== null) {
      client.ws.close(closeCode, response.error.code);
    }
  }

  /** Counts a frame; at most `requestsPerSecond` fit in a second. */
  _withinRate(client) {
    const time = now();
    const times = client.requestTimes;
    while (times.length > 0 && time - times[0] >= 1.0) {
      times.shift();
    }
    if (times.length >= this.requestsPerSecond) {
      return false;
    }
    times.push(time);
    return true;
  }

  _respond(client, response) {
    const frames = this._framesBeforeResponse;
    this._framesBeforeResponse = [];
    for (const frame of frames) {
      this._send(client, frame);
    }
    if (this._hold > 0) {
      this._held.push(response);
      if (this._held.length < this._hold) {
        return;
      }
      const held = this._held;
      this._held = [];
      this._hold = 0;
      for (const item of held.reverse()) {
        this._send(client, item);
      }
      return;
    }
    this._send(client, response);
  }

  // ------------------------------------------------------------------
  // Actions
  // ------------------------------------------------------------------

  _dispatch(client, request) {
    const action = request.action;
    if (typeof action !== 'string') {
      throw new Failure('invalid_request', "The field 'action' is missing.");
    }
    const identifier = request.id ?? null;
    if (
      identifier !== null &&
      typeof identifier !== 'string' &&
      typeof identifier !== 'number'
    ) {
      throw new Failure('invalid_request', "'id' must be a string or a number.");
    }
    if ((this._ignored.get(action) ?? 0) > 0) {
      this._ignored.set(action, this._ignored.get(action) - 1);
      return null;
    }
    if (action === 'authenticate') {
      return this._authenticate(client, request);
    }
    if (!client.authenticated) {
      throw new Failure('not_authenticated', 'Authenticate first.');
    }
    const failures = this._failures.get(action);
    if (failures && failures.length > 0) {
      throw failures.shift();
    }
    const results = this._results.get(action);
    if (results && results.length > 0) {
      return results.shift();
    }
    switch (action) {
      case 'status':
        return this._status();
      case 'ping':
        return { time: Date.now() };
      case 'conversations.list':
        return this._conversationsList(request);
      case 'messages.list':
        return this._messagesList(request);
      case 'messages.get':
        return this._findMessage(request);
      case 'messages.status':
        return this._messagesStatus(request);
      case 'messages.delete':
        return this._messagesDelete(request);
      case 'conversations.delete':
        return this._conversationsDelete(request);
      case 'conversations.markRead':
        return this._conversationsMarkRead(request);
      case 'contacts.list':
        return this._contactsList(request);
      case 'messages.send':
        return this._messagesSend(request);
      case 'messages.sendGroup':
        return this._messagesSendGroup(request);
      case 'subscribe':
        return this._subscribe(client, request);
      case 'unsubscribe':
        return this._unsubscribe(client, request);
      default:
        throw new Failure('unknown_action', `Unknown action '${action}'.`);
    }
  }

  _authenticate(client, request) {
    this.loginCount += 1;
    // Assumption, as in the app: the licence is looked at before the
    // password, so a login without licence never counts as wrong.
    if (this.licenceEnforced && !this.licensed) {
      throw new Failure(
        'licence_required',
        'The API needs the full licence of SMS Dock.',
        4403,
      );
    }
    const remaining = this.lockedUntil - now();
    if (remaining > 0) {
      const seconds = Math.ceil(remaining);
      throw new Failure(
        'locked_out',
        `Too many attempts. Try again in ${seconds} seconds.`,
        4401,
        { retryAfter: seconds },
      );
    }
    if (request.password !== this.password) {
      this.failedLogins += 1;
      if (this.failedLogins >= 5) {
        const seconds = Math.min(30 * 2 ** this.lockCount, 900);
        this.lockCount += 1;
        this.failedLogins = 0;
        this.lockedUntil = now() + seconds;
      }
      throw new Failure('invalid_password', 'Wrong password.', 4401);
    }
    this.failedLogins = 0;
    this.lockCount = 0;
    client.authenticated = true;
    return {
      protocolVersion: this.protocolVersion,
      app: this._app(),
      latestSeq: this._sequence,
      epoch: this.epoch,
    };
  }

  _app() {
    return { name: 'SMS Dock', version: '1.0.0', protocolVersion: this.protocolVersion };
  }

  _status() {
    return {
      app: this._app(),
      sims: this.sims,
      permissions: {
        readSms: true,
        sendSms: true,
        receiveSms: true,
        contacts: true,
        phoneState: true,
        notifications: true,
      },
      defaultSmsApp: this.deleteMode === 'provider',
      deleteMode: this.deleteMode,
      licence: {
        enforced: this.licenceEnforced,
        licensed: this.licensed,
        dailyLimit: 10,
        sentToday: this.sent.length,
        remaining: null,
      },
      popup: true,
    };
  }

  _conversationsList(request) {
    const query = optional(request, 'q', 'string');
    const unread = optional(request, 'unread', 'boolean') ?? false;
    const sim = optional(request, 'sim', 'integer');
    let items = this._conversations();
    if (query) {
      const needle = query.toLowerCase();
      const matching = new Set(
        this.messages
          .filter((message) => message.body.toLowerCase().includes(needle))
          .map((message) => message.conversationId),
      );
      items = items.filter(
        (item) =>
          matching.has(item.id) ||
          item.address.toLowerCase().includes(needle) ||
          (item.contact ?? '').toLowerCase().includes(needle),
      );
    }
    if (unread) {
      items = items.filter((item) => item.unreadCount > 0);
    }
    if (sim !== null) {
      items = items.filter((item) => item.sim === sim);
    }
    return page(items, request);
  }

  _messagesList(request) {
    const address = optional(request, 'address', 'string');
    const conversation = optional(request, 'conversationId', 'integer');
    const direction = optional(request, 'direction', 'string') ?? 'all';
    const query = optional(request, 'q', 'string');
    const start = optional(request, 'from', 'integer');
    const end = optional(request, 'to', 'integer');
    const sim = optional(request, 'sim', 'integer');
    const unread = optional(request, 'unread', 'boolean') ?? false;
    if (!['in', 'out', 'all'].includes(direction)) {
      throw new Failure('invalid_request', "'direction' must be 'in', 'out' or 'all'.");
    }
    let items = [...this.messages].sort(newestFirst);
    if (conversation !== null) {
      // conversationId wins if address is given too.
      items = items.filter((m) => m.conversationId === conversation);
    } else if (address !== null) {
      items = items.filter((m) => samePeer(m.address, address));
    }
    if (direction !== 'all') {
      items = items.filter((m) => m.direction === direction);
    }
    if (query) {
      items = items.filter((m) => m.body.toLowerCase().includes(query.toLowerCase()));
    }
    if (start !== null) {
      items = items.filter((m) => m.timestamp >= start);
    }
    if (end !== null) {
      items = items.filter((m) => m.timestamp <= end);
    }
    if (sim !== null) {
      items = items.filter((m) => m.sim === sim);
    }
    if (unread) {
      items = items.filter((m) => !m.read);
    }
    return page(items, request);
  }

  _findMessage(request) {
    const identifier = required(request, 'messageId', 'integer');
    const message = this.messages.find((m) => m.id === identifier);
    if (message === undefined) {
      throw new Failure('not_found', `No message with the id ${identifier}.`);
    }
    return message;
  }

  _messagesStatus(request) {
    const requestId = required(request, 'requestId', 'string');
    const entry = this.outbox.get(requestId);
    if (entry === undefined) {
      throw new Failure('not_found', `No message with the requestId ${requestId}.`);
    }
    return { ...entry };
  }

  _messagesDelete(request) {
    const message = this._findMessage(request);
    this.messages.splice(this.messages.indexOf(message), 1);
    const mode = this.deleteMode;
    this._later(0, () => {
      this.emit('sms.deleted', {
        id: message.id,
        conversationId: message.conversationId,
        address: message.address,
        mode,
      });
    });
    return { deleted: 1, mode };
  }

  _conversationsDelete(request) {
    const identifier = required(request, 'conversationId', 'integer');
    const doomed = this.messages.filter((m) => m.conversationId === identifier);
    if (doomed.length === 0) {
      throw new Failure('not_found', 'This conversation does not exist.');
    }
    const newest = [...doomed].sort(newestFirst)[0];
    this.messages = this.messages.filter((m) => !doomed.includes(m));
    const mode = this.deleteMode;
    this._later(0, () => {
      this.emit('sms.deleted', {
        id: null,
        conversationId: identifier,
        address: newest.address,
        mode,
      });
    });
    return { deleted: doomed.length, mode };
  }

  _conversationsMarkRead(request) {
    // API.md names no order of the two errors. Assumption, as in the app:
    // the default SMS app is checked first.
    const identifier = required(request, 'conversationId', 'integer');
    if (this.deleteMode !== 'provider') {
      throw new Failure(
        'not_default_app',
        'Only the default SMS app can mark messages as read.',
      );
    }
    const found = this.messages.filter((m) => m.conversationId === identifier);
    if (found.length === 0) {
      throw new Failure('not_found', 'This conversation does not exist.');
    }
    for (const message of found) {
      message.read = true;
    }
    return { ok: true };
  }

  _contactsList(request) {
    const query = optional(request, 'q', 'string');
    let items = [...this.contacts].sort((a, b) =>
      a.name.toLowerCase() < b.name.toLowerCase() ? -1 : 1,
    );
    if (query) {
      const needle = query.toLowerCase();
      items = items.filter(
        (contact) =>
          contact.name.toLowerCase().includes(needle) ||
          contact.numbers.some((n) => n.number.includes(needle)),
      );
    }
    return page(items, request, true);
  }

  _messagesSend(request) {
    const to = required(request, 'to', 'string');
    const body = required(request, 'body', 'string');
    let sim = optional(request, 'sim', 'integer');
    let requestId = optional(request, 'requestId', 'string');
    if (requestId !== null && !REQUEST_ID.test(requestId)) {
      throw new Failure(
        'invalid_request',
        "'requestId' must be 1 to 64 characters of A-Z a-z 0-9 . _ -.",
      );
    }
    const number = normalizeNumber(to);
    if (!NUMBER.test(number)) {
      throw new Failure('invalid_number', "'to' is not a phone number.");
    }
    if (!body) {
      throw new Failure('invalid_request', "'body' is empty.");
    }
    const parts = countParts(body);
    if (parts > 10) {
      throw new Failure(
        'message_too_long',
        `The message needs ${parts} SMS parts; at most 10 are allowed.`,
      );
    }
    const known = requestId === null ? undefined : this.outbox.get(requestId);
    if (known !== undefined) {
      // A repeat: never sent twice, answered with the current status.
      if (this._bodies.get(known.requestId) !== body || !samePeer(known.to, number)) {
        throw new Failure(
          'invalid_request',
          "'requestId' was already used for another message.",
        );
      }
      return {
        requestId: known.requestId,
        to: known.to,
        parts: known.parts,
        status: known.status,
      };
    }
    sim = this._subscription(sim);
    if (requestId === null) {
      requestId = randomUUID().replaceAll('-', '');
    }
    return sendResult(this._accept(requestId, number, body, parts, sim));
  }

  /** Returns the SIM to send with; the default one without `sim`. */
  _subscription(sim) {
    if (sim === null) {
      return this.sims.find((s) => s.isDefault).subscriptionId;
    }
    if (this.sims.every((s) => s.subscriptionId !== sim)) {
      throw new Failure('sim_unavailable', `No SIM with the subscription id ${sim}.`);
    }
    return sim;
  }

  /** Queues one message that passed the checks and starts sending it. */
  _accept(requestId, number, body, parts, sim, group = null) {
    const entry = {
      requestId,
      to: number,
      parts,
      partsSent: 0,
      status: 'queued',
      errorCode: null,
      messageId: null,
      sim,
      source: 'api',
      createdAt: Date.now(),
    };
    this.outbox.set(requestId, entry);
    this._bodies.set(requestId, body);
    const sent = { requestId, to: number, body, sim };
    if (group !== null) {
      sent.group = group;
    }
    this.sent.push(sent);
    this.emit('sms.queued', { requestId, to: number, parts, sim, source: 'api' });
    if (this.refuseNumbers.has(number)) {
      // The phone did not even take the message.
      entry.status = 'failed';
      this.emit('sms.failed', {
        requestId,
        id: null,
        conversationId: null,
        to: number,
        errorCode: null,
        reason: 'send_refused',
      });
      return entry;
    }
    this._transmit(entry, body);
    return entry;
  }

  _messagesSendGroup(request) {
    const to = request.to;
    if (to === null || to === undefined || (typeof to === 'string' && to === '')) {
      throw new Failure('invalid_request', "The field 'to' is missing.");
    }
    const list = typeof to === 'string' ? [to] : to;
    if (!Array.isArray(list) || list.some((item) => typeof item !== 'string')) {
      throw new Failure('invalid_request', "'to' must contain strings.");
    }
    const body = required(request, 'body', 'string');
    let sim = optional(request, 'sim', 'integer');
    let requestId = optional(request, 'requestId', 'string');
    if (list.length === 0) {
      throw new Failure('invalid_request', "'to' is missing.");
    }
    const numbers = [];
    for (const item of list) {
      const number = normalizeNumber(item);
      if (!NUMBER.test(number)) {
        throw new Failure(
          'invalid_number',
          `'to' contains '${item}', which is not a phone number.`,
        );
      }
      if (!numbers.some((known) => samePeer(known, number))) {
        numbers.push(number);
      }
    }
    if (numbers.length > 20) {
      throw new Failure('invalid_request', 'A group message goes to at most 20 recipients.');
    }
    if (!body) {
      throw new Failure('invalid_request', "'body' is empty.");
    }
    const parts = countParts(body);
    if (parts > 10) {
      throw new Failure(
        'message_too_long',
        `The message needs ${parts} SMS parts; at most 10 are allowed.`,
      );
    }
    if (requestId !== null && (!REQUEST_ID.test(requestId) || requestId.length > 61)) {
      throw new Failure(
        'invalid_request',
        "'requestId' of a group must be 1 to 61 characters of A-Z a-z 0-9 . _ -.",
      );
    }
    if (requestId === null) {
      requestId = randomUUID().replaceAll('-', '');
    }
    const known = this._knownGroup(requestId);
    if (known.length > 0) {
      const same =
        known.length === numbers.length &&
        known.every((member) => this._bodies.get(member.requestId) === body) &&
        numbers.every((number) => known.some((member) => samePeer(member.to, number)));
      if (!same) {
        throw new Failure(
          'invalid_request',
          "'requestId' was already used for another message.",
        );
      }
      return groupResult(requestId, known);
    }
    const total = parts * numbers.length;
    if (total > 20) {
      throw new Failure(
        'invalid_request',
        `This group needs ${total} SMS parts; at most 20 can be sent per minute. ` +
          'Send to fewer recipients or shorten the text.',
      );
    }
    sim = this._subscription(sim);
    const members = numbers.map((number, index) =>
      this._accept(`${requestId}.${index + 1}`, number, body, parts, sim, requestId),
    );
    return groupResult(requestId, members);
  }

  /** Returns the members of a group sent before, oldest first. */
  _knownGroup(requestId) {
    const members = [];
    for (let index = 1; this.outbox.has(`${requestId}.${index}`); index += 1) {
      members.push(this.outbox.get(`${requestId}.${index}`));
    }
    return members;
  }

  _transmit(entry, body) {
    const number = entry.to;
    const finish = () => {
      if (this.failNumbers.has(number)) {
        // The phone has not stored the message: no id, no conversation.
        entry.status = 'failed';
        entry.errorCode = 1;
        this.emit('sms.failed', {
          requestId: entry.requestId,
          id: null,
          conversationId: null,
          to: number,
          errorCode: 1,
          reason: 'generic_failure',
        });
        return;
      }
      const message = this.addMessage(number, body, {
        direction: 'out',
        sim: entry.sim,
        requestId: entry.requestId,
      });
      entry.status = 'sent';
      entry.partsSent = entry.parts;
      entry.messageId = message.id;
      this.emit('sms.sent', {
        requestId: entry.requestId,
        conversationId: message.conversationId,
        to: number,
        message,
      });
      if (this.deliveryReports) {
        this._later(this.sendDelay, () => {
          entry.status = 'delivered';
          message.status = 'delivered';
          this.emit('sms.delivered', {
            requestId: entry.requestId,
            id: message.id,
            conversationId: message.conversationId,
            to: number,
          });
        });
      }
    };
    if (entry.parts > 1 && !this.failNumbers.has(number)) {
      // Some parts are sent, some are left.
      this._later(this.sendDelay / 2, () => {
        entry.status = 'sending';
        entry.partsSent = 1;
        this._later(this.sendDelay / 2, finish);
      });
    } else {
      this._later(this.sendDelay, finish);
    }
  }

  _subscribe(client, request) {
    // subscribe adds the names; its address replaces the filter, and
    // without address (or with null) the filter is removed.
    for (const name of names(request)) {
      client.events.add(name);
    }
    client.address = optional(request, 'address', 'string');
    return subscription(client);
  }

  _unsubscribe(client, request) {
    for (const name of names(request)) {
      client.events.delete(name);
    }
    if (client.events.size === 0) {
      client.address = null;
    }
    return subscription(client);
  }

  // ------------------------------------------------------------------
  // Data
  // ------------------------------------------------------------------

  _conversationId(address) {
    for (const [known, identifier] of this._conversationIds) {
      if (samePeer(known, address)) {
        return identifier;
      }
    }
    const identifier = 170 + this._conversationIds.size;
    this._conversationIds.set(address, identifier);
    return identifier;
  }

  _contactName(address) {
    for (const contact of this.contacts) {
      if (contact.numbers.some((number) => samePeer(number.number, address))) {
        return contact.name;
      }
    }
    return null;
  }

  _conversations() {
    const grouped = new Map();
    for (const message of this.messages) {
      const list = grouped.get(message.conversationId) ?? [];
      list.push(message);
      grouped.set(message.conversationId, list);
    }
    const items = [];
    for (const [identifier, messages] of grouped) {
      const newest = [...messages].sort(newestFirst)[0];
      items.push({
        id: identifier,
        address: newest.address,
        contact: newest.contact,
        snippet: newest.body.slice(0, 160),
        timestamp: newest.timestamp,
        direction: newest.direction,
        messageCount: messages.length,
        unreadCount: messages.filter((m) => !m.read).length,
        sim: newest.sim,
      });
    }
    return items.sort(newestFirst);
  }
}

function now() {
  return performance.now() / 1000;
}

function push(map, key, value) {
  const list = map.get(key) ?? [];
  list.push(value);
  map.set(key, list);
}

function newestFirst(a, b) {
  return b.timestamp - a.timestamp || b.id - a.id;
}

const STATUS_ORDER = ['queued', 'sending', 'sent', 'delivered'];

function sendResult(entry) {
  return { requestId: entry.requestId, to: entry.to, parts: entry.parts, status: entry.status };
}

/** Returns the GroupSendResult of `members` as the app builds it. */
function groupResult(requestId, members) {
  const alive = members.filter((member) => member.status !== 'failed');
  const status =
    alive.length === 0
      ? 'failed'
      : alive
          .map((member) => member.status)
          .sort((a, b) => STATUS_ORDER.indexOf(a) - STATUS_ORDER.indexOf(b))[0];
  return {
    requestId,
    to: members.map((member) => member.to),
    parts: members[0].parts,
    status,
    messages: members.map(sendResult),
  };
}

function required(request, name, kind) {
  const value = optional(request, name, kind);
  if (value === null) {
    throw new Failure('invalid_request', `The field '${name}' is missing.`);
  }
  return value;
}

function optional(request, name, kind) {
  const value = request[name];
  if (value === null || value === undefined) {
    return null;
  }
  const fits = kind === 'integer' ? Number.isInteger(value) : typeof value === kind;
  if (!fits) {
    throw new Failure('invalid_request', `The field '${name}' is malformed.`);
  }
  return value;
}

/** Returns the event names of a request, with `*` replaced. */
function names(request) {
  const list = request.events;
  if (
    !Array.isArray(list) ||
    list.length === 0 ||
    !list.every((name) => typeof name === 'string')
  ) {
    throw new Failure('invalid_request', "'events' is missing.");
  }
  if (list.includes('*')) {
    return [...EVENT_NAMES];
  }
  for (const name of list) {
    if (!EVENT_NAMES.includes(name)) {
      throw new Failure('invalid_request', `Unknown event '${name}'.`);
    }
  }
  return list;
}

function subscription(client) {
  return {
    events: EVENT_NAMES.filter((name) => client.events.has(name)),
    address: client.address,
  };
}

/**
 * Whether a client subscribed to an event and its filter lets it pass.
 *
 * The filter applies to `sms.*` events that name a peer. Events whose
 * number is not known are always delivered.
 */
function wants(client, name, data) {
  if (!client.events.has(name)) {
    return false;
  }
  if (client.address === null || !name.startsWith('sms.')) {
    return true;
  }
  const message = data.message;
  let peer = message && typeof message === 'object' ? message.address : null;
  peer = peer || data.to || data.address;
  if (typeof peer !== 'string') {
    return true;
  }
  return samePeer(peer, client.address);
}

function page(items, request, byIndex = false) {
  const limit = request.limit ?? 50;
  if (!Number.isInteger(limit) || limit < 1 || limit > 200) {
    throw new Failure('invalid_request', "'limit' must be between 1 and 200.");
  }
  const cursor = optional(request, 'cursor', 'string');
  let start = 0;
  if (cursor !== null && byIndex) {
    if (!/^[0-9]+$/.test(cursor)) {
      throw new Failure('invalid_request', "'cursor' is invalid.");
    }
    start = Number(cursor);
  } else if (cursor !== null) {
    const keys = items.map((item) => `${item.timestamp}:${item.id}`);
    if (!keys.includes(cursor)) {
      throw new Failure('invalid_request', "'cursor' is invalid.");
    }
    start = keys.indexOf(cursor) + 1;
  }
  const chunk = items.slice(start, start + limit);
  let nextCursor = null;
  if (chunk.length > 0 && start + limit < items.length) {
    const last = chunk[chunk.length - 1];
    nextCursor = byIndex ? String(start + chunk.length) : `${last.timestamp}:${last.id}`;
  }
  return { items: chunk, nextCursor };
}

/** Runs the fake server until Ctrl+C. */
async function main() {
  const { values } = parseArgs({
    options: {
      host: { type: 'string', default: '127.0.0.1' },
      port: { type: 'string', default: '9443' },
      password: { type: 'string', default: 'secret123' },
      'incoming-every': { type: 'string' },
    },
  });
  const directory = mkdtempSync(join(tmpdir(), 'smsdock-fake-'));
  const tls = makeCertificate(directory);
  rmSync(directory, { recursive: true, force: true });
  if (tls === null) {
    console.log('The openssl command is needed to create a certificate.');
    return 1;
  }
  const server = new FakeSmsDock({
    password: values.password,
    tls,
    host: values.host,
    port: Number(values.port),
    sendDelay: 0.5,
    deliveryReports: true,
  });
  server.seedDemoData();
  await server.start();
  console.log(`URL:         ${server.url}`);
  console.log(`Fingerprint: ${tls.fingerprint}`);
  console.log(`Password:    ${values.password}`);
  console.log('Press Ctrl+C to stop.');
  const every = Number(values['incoming-every'] ?? 0);
  let number = 0;
  const timer =
    every > 0
      ? setInterval(() => {
          number += 1;
          server.receiveSms('+436641234567', `Test message ${number}`);
        }, every * 1000)
      : null;
  await new Promise((resolve) => {
    process.once('SIGINT', resolve);
    process.once('SIGTERM', resolve);
  });
  clearInterval(timer);
  console.log();
  await server.stop();
  return 0;
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  process.exitCode = await main();
}
