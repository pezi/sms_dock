/** Tests of `SmsDockClient` against the fake server. */

import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { describe, test } from 'node:test';
import tls from 'node:tls';
import { inspect } from 'node:util';

import {
  ApiError,
  AuthenticationError,
  CertificateMismatchError,
  ConnectionInterrupted,
  EVENT_NAMES,
  LicenceRequiredError,
  ProtocolError,
  SmsDockClient,
  SmsDockError,
  TimeoutError,
  certificateFingerprint,
  fetchFingerprint,
  formatFingerprint,
  normalizeFingerprint,
} from '../src/index.js';
import { FakeSmsDock } from './fake_server.js';
import {
  ANNA,
  BASE_TIME,
  BEN,
  FINGERPRINT,
  PASSWORD,
  URL,
  collect,
  eventFrame,
  fill,
  lastRequest,
  makeClient,
  needTls,
  otherTlsFiles,
  pair,
  sleep,
  startServer,
  tlsFiles,
  waitUntil,
} from './helpers.js';

const range = (start, stop, step = 1) => {
  const values = [];
  for (let value = start; step > 0 ? value < stop : value > stop; value += step) {
    values.push(value);
  }
  return values;
};

/** Replaces the pauses of a client by a list of their lengths. */
function recordSleeps(client) {
  const pauses = [];
  client._sleep = async (seconds) => {
    pauses.push(seconds);
  };
  return pauses;
}

const count = (server, action) =>
  server.actions().filter((name) => name === action).length;

// ----------------------------------------------------------------------
// Construction
// ----------------------------------------------------------------------

describe('construction', () => {
  const options = { password: PASSWORD, fingerprint: FINGERPRINT };

  test('refuses everything but wss', () => {
    for (const url of [
      'ws://192.168.1.50:9443/ws',
      'http://192.168.1.50:9443/ws',
      'https://192.168.1.50:9443/ws',
      '192.168.1.50:9443',
      'wss:///ws',
      '',
      undefined,
    ]) {
      assert.throws(() => new SmsDockClient(url, options), RangeError, String(url));
      assert.throws(() => new SmsDockClient(url, options), /wss:\/\//);
    }
    assert.ok(new SmsDockClient('WSS://phone.local/ws', options));
  });

  test('reads the password from the environment', (t) => {
    t.after(() => delete process.env.SMSDOCK_PASSWORD);
    assert.throws(
      () => new SmsDockClient(URL, { fingerprint: FINGERPRINT }),
      (error) => error instanceof RangeError && /SMSDOCK_PASSWORD/.test(error.message),
    );
    process.env.SMSDOCK_PASSWORD = 'from-the-environment';
    assert.ok(new SmsDockClient(URL, { fingerprint: FINGERPRINT }));
  });

  test('needs a fingerprint unless insecure', () => {
    assert.throws(
      () => new SmsDockClient(URL, { password: PASSWORD }),
      (error) => error instanceof RangeError && /SMSDOCK_FINGERPRINT/.test(error.message),
    );
    assert.equal(new SmsDockClient(URL, { password: PASSWORD, insecure: true }).insecure, true);
  });

  test('reads the fingerprint from the environment', (t) => {
    t.after(() => delete process.env.SMSDOCK_FINGERPRINT);
    process.env.SMSDOCK_FINGERPRINT = FINGERPRINT.toLowerCase();
    assert.ok(new SmsDockClient(URL, { password: PASSWORD }));
    process.env.SMSDOCK_FINGERPRINT = 'AB:CD';
    assert.throws(() => new SmsDockClient(URL, { password: PASSWORD }), /SHA-256/);
  });

  test('refuses malformed fingerprints', () => {
    const wrong = ['', 'AB:CD', 'zz'.repeat(32), 'AB'.repeat(31), 'AB'.repeat(33)];
    for (const fingerprint of wrong) {
      assert.throws(
        () => new SmsDockClient(URL, { password: PASSWORD, fingerprint }),
        (error) => error instanceof RangeError && /SHA-256/.test(error.message),
        fingerprint,
      );
    }
  });

  test('refuses impossible options', () => {
    for (const extra of [
      { timeout: 0 },
      { connectTimeout: -1 },
      { reconnectAttempts: -1 },
      { reconnectDelay: -1 },
      { eventQueueSize: 0 },
      { rateLimitRetries: -1 },
    ]) {
      assert.throws(() => new SmsDockClient(URL, { ...options, ...extra }), RangeError);
    }
    // A misspelt option is not ignored.
    assert.throws(() => new SmsDockClient(URL, { ...options, time_out: 3 }), TypeError);
  });

  test('has the defaults of the Python client', () => {
    const client = new SmsDockClient(URL, options);
    assert.deepEqual(
      [
        client.timeout,
        client.connectTimeout,
        client.reconnectAttempts,
        client.reconnectDelay,
        client.maxReconnectDelay,
        client.rateLimitRetries,
        client.insecure,
        client.onReconnect,
      ],
      [30, 10, 5, 1, 10, 2, false, null],
    );
    const endless = new SmsDockClient(URL, { ...options, reconnectAttempts: null });
    assert.equal(endless.reconnectAttempts, null);
    assert.deepEqual(
      [client.connected, client.serverInfo, client.subscription, client.epoch, client.latestSeq],
      [false, null, null, null, null],
    );
    assert.deepEqual(
      [client.serverRestarted, client.missedEvents, client.pendingEvents, client.droppedEvents],
      [false, 0, 0, 0],
    );
  });

  test('fingerprint forms', () => {
    const digits = '0123456789abcdef'.repeat(4);
    const colons = digits.match(/../g).join(':');
    assert.equal(normalizeFingerprint(digits.toUpperCase()), digits);
    assert.equal(normalizeFingerprint(colons), digits);
    assert.equal(normalizeFingerprint(` ${colons.toUpperCase()}\n`), digits);
    assert.equal(normalizeFingerprint(`sha256 Fingerprint=${colons.toUpperCase()}`), digits);
    assert.equal(formatFingerprint(digits), colons.toUpperCase());
    assert.equal(
      certificateFingerprint(Buffer.from('certificate')),
      formatFingerprint(createHash('sha256').update('certificate').digest('hex')),
    );
    assert.throws(() => normalizeFingerprint('AB:CD'), RangeError);
  });

  test('does not show the password', () => {
    const client = new SmsDockClient(URL, { password: 'very-secret', fingerprint: FINGERPRINT });
    for (const text of [String(client), inspect(client), inspect(client, { depth: 9 })]) {
      assert.ok(!text.includes('very-secret'), text);
      assert.ok(text.includes(URL));
    }
    assert.ok(!JSON.stringify(client).includes('very-secret'));
    assert.ok(!Object.values(client).includes('very-secret'));
  });

  test('all errors share a base class', () => {
    for (const kind of [
      ApiError,
      AuthenticationError,
      LicenceRequiredError,
      ProtocolError,
      ConnectionInterrupted,
      CertificateMismatchError,
    ]) {
      assert.ok(kind.prototype instanceof SmsDockError, kind.name);
    }
    assert.ok(AuthenticationError.prototype instanceof ApiError);
    assert.ok(LicenceRequiredError.prototype instanceof ApiError);
    assert.ok(!(TimeoutError.prototype instanceof SmsDockError));
    const error = new ApiError('locked_out', 'Too many attempts.', { retryAfter: 30.0 });
    assert.equal(error.name, 'ApiError');
    assert.equal(error.message, 'Too many attempts.');
    assert.equal(error.retryAfter, 30);
    assert.equal(error.permission, null);
    assert.equal(String(error), 'ApiError: locked_out: Too many attempts.');
  });
});

// ----------------------------------------------------------------------
// Handshake
// ----------------------------------------------------------------------

describe('handshake', needTls, () => {
  test('logs in', async (t) => {
    const { server, client } = await pair(t);
    assert.equal(client.connected, true);
    assert.deepEqual(client.serverInfo, { name: 'SMS Dock', version: '1.0.0', protocolVersion: 1 });
    assert.equal(client.connectionCount, 1);
    assert.deepEqual(server.actions(), ['authenticate']);
    assert.equal(server.requests[0].password, PASSWORD);
    assert.equal(server.loginCount, 1);
  });

  test('connects by itself and closes', async (t) => {
    const server = await startServer(t);
    const client = await makeClient(t, server, { connect: false });
    assert.ok((await client.ping()).time > 0);
    assert.equal(server.clientCount, 1);
    await client.close();
    assert.equal(client.connected, false);
    assert.ok(await waitUntil(() => server.clientCount === 0));
  });

  test('connect() returns the client and asyncDispose closes it', async (t) => {
    const server = await startServer(t);
    const client = await makeClient(t, server, { connect: false });
    assert.equal(await client.connect(), client);
    assert.equal(typeof client[Symbol.asyncDispose], 'function');
    await client[Symbol.asyncDispose]();
    assert.equal(client.connected, false);
  });

  test('wrong password', async (t) => {
    const server = await startServer(t);
    const client = await makeClient(t, server, { connect: false, password: 'wrong-password' });
    await assert.rejects(client.connect(), (error) => {
      assert.ok(error instanceof AuthenticationError);
      assert.ok(error instanceof ApiError);
      assert.equal(error.code, 'invalid_password');
      assert.ok(!inspect(error).includes('wrong-password'));
      return true;
    });
    assert.equal(client.connected, false);
    // A wrong password counts towards the lock: it must not be repeated.
    assert.equal(server.loginCount, 1);
  });

  test('wrong password is not retried by requests', async (t) => {
    const server = await startServer(t);
    const client = await makeClient(t, server, { connect: false, password: 'wrong-password' });
    await assert.rejects(client.status(), AuthenticationError);
    assert.equal(server.loginCount, 1);
    assert.equal(client.connectionCount, 1);
  });

  test('locked out', async (t) => {
    const server = await startServer(t);
    server.lockLogins(30);
    const client = await makeClient(t, server, { connect: false });
    await assert.rejects(client.connect(), (error) => {
      assert.ok(error instanceof AuthenticationError);
      assert.equal(error.code, 'locked_out');
      assert.ok(error.retryAfter >= 1 && error.retryAfter <= 30);
      assert.deepEqual(error.details, { retryAfter: error.retryAfter });
      return true;
    });
    assert.equal(server.loginCount, 1);
  });

  test('licence required', async (t) => {
    const server = await startServer(t);
    server.licenceEnforced = true;
    const client = await makeClient(t, server, { connect: false });
    await assert.rejects(client.connect(), (error) => {
      assert.ok(error instanceof LicenceRequiredError);
      assert.ok(error instanceof ApiError);
      assert.equal(error.code, 'licence_required');
      return true;
    });
    assert.equal(server.loginCount, 1);
  });

  test('refuses other protocol versions', async (t) => {
    const server = await startServer(t);
    server.protocolVersion = 2;
    const client = await makeClient(t, server, { connect: false });
    await assert.rejects(client.connect(), (error) => {
      return error instanceof ProtocolError && /version 2/.test(error.message);
    });
    // The password is not sent to a server of another protocol version.
    assert.deepEqual(server.requests, []);
  });

  test('close code 4401 while logging in', async (t) => {
    const server = await startServer(t, { authTimeout: 0.2 });
    server.ignoreNext('authenticate');
    const client = await makeClient(t, server, { connect: false });
    await assert.rejects(client.connect(), (error) => {
      assert.ok(error instanceof AuthenticationError);
      assert.equal(error.code, 'not_authenticated');
      assert.equal(error.details.closeCode, 4401);
      return true;
    });
    assert.equal(client.connectionCount, 1);
  });

  test('too many clients', async (t) => {
    const server = await startServer(t, { maxClients: 1 });
    await makeClient(t, server);
    const second = await makeClient(t, server, { connect: false });
    await assert.rejects(second.connect(), (error) => {
      assert.ok(error instanceof ConnectionInterrupted);
      assert.equal(error.closeCode, 4409);
      assert.equal(error.closeReason, 'too many clients');
      return true;
    });
  });

  test('nothing listens', async (t) => {
    const server = await startServer(t);
    const client = await makeClient(t, server, { connect: false, connectTimeout: 2 });
    await server.stop();
    await assert.rejects(client.connect(), (error) => {
      assert.ok(error instanceof ConnectionInterrupted);
      assert.match(error.message, /Cannot connect/);
      assert.equal(error.closeCode, null);
      return true;
    });
  });

  test('login without epoch is refused', async (t) => {
    const server = await startServer(t);
    const client = await makeClient(t, server, { connect: false });
    server.beforeNextResponse({
      id: 'js-1',
      ok: true,
      result: { protocolVersion: 1, app: { name: 'SMS Dock', version: '0.9.0' } },
    });
    await assert.rejects(client.connect(), (error) => {
      return error instanceof ProtocolError && /epoch/.test(error.message);
    });
    assert.equal(client.connected, false);
  });
});

// ----------------------------------------------------------------------
// Certificate pinning over real TLS
// ----------------------------------------------------------------------

describe('certificate', needTls, () => {
  /** A TLS server that records every byte a client sends after the handshake. */
  async function rawTlsServer(t, files) {
    const record = { data: Buffer.alloc(0), closed: 0, connections: 0 };
    const server = tls.createServer(
      { cert: files.certificatePem, key: files.keyPem },
      (socket) => {
        socket.on('data', (chunk) => {
          record.data = Buffer.concat([record.data, chunk]);
        });
        socket.on('error', () => {});
        socket.on('end', () => socket.end());
      },
    );
    server.on('connection', (socket) => {
      record.connections += 1;
      socket.on('close', () => {
        record.closed += 1;
      });
    });
    server.on('tlsClientError', () => {});
    await new Promise((resolve) => server.listen(0, '127.0.0.1', resolve));
    t.after(() => server.close());
    record.url = `wss://127.0.0.1:${server.address().port}/ws`;
    return record;
  }

  test('pins the certificate, in every form of the fingerprint', async (t) => {
    const server = await startServer(t);
    const fingerprint = tlsFiles.fingerprint;
    for (const form of [
      fingerprint,
      fingerprint.toLowerCase(),
      fingerprint.replaceAll(':', ''),
      fingerprint.replaceAll(':', '').toLowerCase(),
      `sha256 Fingerprint=${fingerprint}\n`,
    ]) {
      const client = await makeClient(t, server, { fingerprint: form });
      assert.ok((await client.ping()).time > 0);
      await client.close();
    }
  });

  test('certificate mismatch', async (t) => {
    const server = await startServer(t);
    const client = await makeClient(t, server, {
      connect: false,
      fingerprint: otherTlsFiles.fingerprint,
    });
    await assert.rejects(client.connect(), (error) => {
      assert.ok(error instanceof CertificateMismatchError);
      assert.equal(error.expected, otherTlsFiles.fingerprint);
      assert.equal(error.actual, tlsFiles.fingerprint);
      assert.ok(error.message.includes(otherTlsFiles.fingerprint));
      assert.ok(error.message.includes(tlsFiles.fingerprint));
      return true;
    });
    assert.equal(client.connected, false);
    assert.equal(client.connectionCount, 0);
    await sleep(0.1);
    // The client came as far as the TLS handshake; nothing else reached
    // the server: no WebSocket request, no password.
    assert.equal(server.tcpConnectionCount, 1);
    assert.equal(server.httpRequestCount, 0);
    assert.equal(server.connectionCount, 0);
    assert.deepEqual(server.requests, []);
  });

  test('not one byte is sent to a server with another certificate', async (t) => {
    const record = await rawTlsServer(t, otherTlsFiles);
    const client = new SmsDockClient(record.url, {
      password: PASSWORD,
      fingerprint: tlsFiles.fingerprint,
      reconnectAttempts: 0,
    });
    await assert.rejects(client.connect(), CertificateMismatchError);
    assert.ok(await waitUntil(() => record.closed === 1));
    assert.equal(record.connections, 1);
    assert.equal(record.data.length, 0);

    // The same server with the right pin: now the WebSocket request arrives.
    const pinned = new SmsDockClient(record.url, {
      password: PASSWORD,
      fingerprint: otherTlsFiles.fingerprint,
      connectTimeout: 0.5,
    });
    await assert.rejects(pinned.connect(), ConnectionInterrupted);
    assert.match(record.data.toString('latin1'), /^GET \/ws HTTP\/1\.1\r\n/);
    assert.ok(!record.data.toString('latin1').includes(PASSWORD));
  });

  test('certificate mismatch is not retried', async (t) => {
    const server = await startServer(t);
    const client = await makeClient(t, server, {
      connect: false,
      fingerprint: otherTlsFiles.fingerprint,
    });
    await assert.rejects(client.status(), CertificateMismatchError);
    assert.equal(client.connectionCount, 0);
    assert.equal(server.tcpConnectionCount, 1);
  });

  test('new certificate after reconnect', async (t) => {
    const first = await startServer(t);
    const client = await makeClient(t, first);
    assert.ok(await client.ping());
    await first.stop();
    const second = await startServer(t, { tls: otherTlsFiles, port: first.port });
    await assert.rejects(client.status(), CertificateMismatchError);
    assert.deepEqual(second.requests, []);
    assert.equal(second.httpRequestCount, 0);
  });

  test('fetchFingerprint', async (t) => {
    const server = await startServer(t);
    assert.equal(await fetchFingerprint('127.0.0.1', server.port), tlsFiles.fingerprint);
    await sleep(0.05);
    assert.equal(server.httpRequestCount, 0);
    await server.stop();
    await assert.rejects(
      fetchFingerprint('127.0.0.1', server.port, { timeout: 2 }),
      ConnectionInterrupted,
    );
  });

  test('insecure skips the check and warns once', async (t) => {
    const server = await startServer(t);
    const written = [];
    t.mock.method(process.stderr, 'write', (chunk) => {
      written.push(String(chunk));
      return true;
    });
    const client = await makeClient(t, server, {
      fingerprint: otherTlsFiles.fingerprint,
      insecure: true,
    });
    assert.ok((await client.ping()).time > 0);
    server.dropConnections();
    assert.ok((await client.ping()).time > 0);
    t.mock.restoreAll();
    const warnings = written.filter((text) => text.includes('WARNING'));
    assert.equal(warnings.length, 1);
    assert.match(warnings[0], /^smsdock: WARNING: .*not checked/);
    assert.ok(!written.join('').includes(PASSWORD));
  });
});

// ----------------------------------------------------------------------
// Requests
// ----------------------------------------------------------------------

describe('requests', needTls, () => {
  test('status and ping', async (t) => {
    const { client } = await pair(t);
    const status = await client.status();
    assert.deepEqual(status.app, { name: 'SMS Dock', version: '1.0.0', protocolVersion: 1 });
    assert.equal(status.deleteMode, 'hidden');
    assert.equal(status.sims[0].subscriptionId, 1);
    assert.equal(status.permissions.sendSms, true);
    assert.equal(status.licence.remaining, null);
    assert.ok(Math.abs((await client.ping()).time - Date.now()) < 60_000);
  });

  test('conversations', async (t) => {
    const { server, client } = await pair(t);
    const page = await client.conversations();
    assert.deepEqual(page.items.map((item) => item.address), [ANNA, BEN, 'A1']);
    assert.equal(page.nextCursor, null);
    const anna = page.items[0];
    assert.equal(anna.contact, 'Anna Example');
    assert.equal(anna.messageCount, 25);
    assert.equal(anna.unreadCount, 3);
    assert.equal(anna.snippet, 'Anna 24');
    assert.deepEqual(lastRequest(server, 'conversations.list'), {
      id: 'js-2',
      action: 'conversations.list',
      limit: 50,
    });
  });

  test('conversations filters and paging', async (t) => {
    const { server, client } = await pair(t);
    const addresses = async (options) =>
      (await client.conversations(options)).items.map((item) => item.address);
    assert.deepEqual(await addresses({ q: 'invoice' }), [BEN]);
    assert.deepEqual(await addresses({ q: 'anna' }), [ANNA]);
    assert.deepEqual(await addresses({ unread: true }), [ANNA, 'A1']);
    assert.deepEqual(await addresses({ sim: 2 }), [BEN]);
    const request = lastRequest(server, 'conversations.list');
    assert.ok(request.sim === 2 && !('unread' in request) && !('q' in request));

    const first = await client.conversations({ limit: 2 });
    assert.equal(first.items.length, 2);
    assert.notEqual(first.nextCursor, null);
    const second = await client.conversations({ limit: 2, cursor: first.nextCursor });
    assert.deepEqual(second.items.map((item) => item.address), ['A1']);
    assert.equal(second.nextCursor, null);
    const all = await collect(client.iterConversations({ pageSize: 1 }));
    assert.deepEqual(all.map((item) => item.address), [ANNA, BEN, 'A1']);
  });

  test('last ten incoming messages', async (t) => {
    const { server, client } = await pair(t);
    const page = await client.messages({ direction: 'in', limit: 10 });
    assert.deepEqual(
      page.items.map((m) => m.body),
      range(24, 4, -2).map((index) => `Anna ${index}`),
    );
    assert.ok(page.items.every((m) => m.direction === 'in'));
    assert.notEqual(page.nextCursor, null);
    assert.deepEqual(lastRequest(server, 'messages.list'), {
      id: 'js-2',
      action: 'messages.list',
      direction: 'in',
      limit: 10,
    });
  });

  test('messages of a number, written in many ways', async (t) => {
    const { client } = await pair(t);
    for (const [address, expected] of [
      [BEN, 3],
      ['+43 690 876-54321', 3],
      ['0043 690 87654321', 3],
      ['0690 / 876 54 321', 3],
      ['87654321', 3],
      ['654321', 0],
      ['+49 690 87654321', 0],
      ['a1', 1],
      ['A1 Telekom', 0],
    ]) {
      assert.equal((await client.messages({ address })).items.length, expected, address);
    }
  });

  test('messages filters', async (t) => {
    const { server, client } = await pair(t);
    const ben = (await client.conversations({ q: 'invoice' })).items[0].id;
    // The conversation wins over the address.
    const both = await client.messages({ conversationId: ben, address: ANNA });
    assert.deepEqual([...new Set(both.items.map((m) => m.address))], [BEN]);
    const sent = lastRequest(server, 'messages.list');
    assert.deepEqual([sent.conversationId, sent.address], [ben, ANNA]);

    assert.equal((await client.messages({ direction: 'out', limit: 200 })).items.length, 13);
    assert.deepEqual(
      (await client.messages({ q: 'INVOICE' })).items.map((m) => m.body),
      ['Thanks for the invoice', 'Your invoice is attached'],
    );
    assert.equal((await client.messages({ sim: 2 })).items.length, 3);
    assert.deepEqual(
      (await client.messages({ unread: true })).items.map((m) => m.body),
      ['Anna 24', 'Anna 23', 'Anna 22', 'Your bill is ready'],
    );
    const combined = await client.messages({
      address: ANNA,
      direction: 'in',
      q: 'Anna 1',
      limit: 3,
    });
    assert.deepEqual(combined.items.map((m) => m.body), ['Anna 18', 'Anna 16', 'Anna 14']);
    const request = lastRequest(server, 'messages.list');
    delete request.id;
    assert.deepEqual(request, {
      action: 'messages.list',
      address: ANNA,
      direction: 'in',
      q: 'Anna 1',
      limit: 3,
    });
  });

  test('messages time range', async (t) => {
    const { server, client } = await pair(t);
    let page = await client.messages({ since: BASE_TIME + 3000, until: BASE_TIME + 5000 });
    assert.deepEqual(page.items.map((m) => m.body), ['Anna 5', 'Anna 4', 'Anna 3']);
    const request = lastRequest(server, 'messages.list');
    assert.equal(request.from, BASE_TIME + 3000);
    assert.equal(request.to, BASE_TIME + 5000);

    page = await client.messages({ since: new Date(BASE_TIME + 23_000) });
    assert.deepEqual(page.items.map((m) => m.body), ['Anna 24', 'Anna 23']);
    assert.equal(lastRequest(server, 'messages.list').from, BASE_TIME + 23_000);
  });

  test('messages paging', async (t) => {
    const { client } = await pair(t);
    const first = await client.messages({ address: ANNA, limit: 10 });
    const second = await client.messages({ address: ANNA, limit: 10, cursor: first.nextCursor });
    const third = await client.messages({ address: ANNA, limit: 10, cursor: second.nextCursor });
    const bodies = [first, second, third].flatMap((page) => page.items.map((m) => m.body));
    assert.deepEqual(bodies, range(24, -1, -1).map((index) => `Anna ${index}`));
    assert.equal(third.nextCursor, null);
  });

  test('iterMessages follows the cursor', async (t) => {
    const { server, client } = await pair(t);
    const messages = await collect(client.iterMessages({ address: ANNA, pageSize: 10 }));
    assert.deepEqual(
      messages.map((m) => m.body),
      range(24, -1, -1).map((index) => `Anna ${index}`),
    );
    const requests = server.requests.filter((r) => r.action === 'messages.list');
    assert.deepEqual(
      requests.map((r) => r.cursor),
      [
        undefined,
        `${BASE_TIME + 15000}:${messages[9].id}`,
        `${BASE_TIME + 5000}:${messages[19].id}`,
      ],
    );
    assert.ok(requests.every((r) => r.limit === 10));
  });

  test('iterMessages is lazy and bounded', async (t) => {
    const { server, client } = await pair(t);
    const iterator = client.iterMessages({ pageSize: 10, maxItems: 12 });
    await sleep(0.05);
    assert.equal(count(server, 'messages.list'), 0);
    assert.equal((await collect(iterator)).length, 12);
    const requests = server.requests.filter((r) => r.action === 'messages.list');
    assert.deepEqual(requests.map((r) => r.limit), [10, 2]);
    assert.deepEqual(await collect(client.iterMessages({ maxItems: 0 })), []);
    assert.equal((await collect(client.iterMessages({ pageSize: 200 }))).length, 29);
  });

  test('iterMessages refuses a cursor that does not move', async (t) => {
    const { server, client } = await pair(t);
    const page = { items: [{ id: 1 }], nextCursor: 'again' };
    server.answerNext('messages.list', page);
    server.answerNext('messages.list', page);
    await assert.rejects(collect(client.iterMessages()), (error) => {
      return error instanceof ProtocolError && /cursor/.test(error.message);
    });
    assert.equal(count(server, 'messages.list'), 2);
  });

  test('iterMessages refuses a page without items', async (t) => {
    const { server, client } = await pair(t);
    server.answerNext('messages.list', { nextCursor: null });
    await assert.rejects(collect(client.iterMessages()), (error) => {
      return error instanceof ProtocolError && /items/.test(error.message);
    });
  });

  test('pages may be shorter than the limit', async (t) => {
    const { server, client } = await pair(t);
    server.answerNext('messages.list', { items: [{ id: 1 }], nextCursor: '1:1' });
    server.answerNext('messages.list', { items: [], nextCursor: '1:0' });
    server.answerNext('messages.list', { items: [{ id: 2 }], nextCursor: null });
    assert.deepEqual(await collect(client.iterMessages({ pageSize: 50 })), [{ id: 1 }, { id: 2 }]);
  });

  test('wrong arguments are refused before anything is sent', async (t) => {
    const { server, client } = await pair(t);
    const wrong = (error) => error instanceof RangeError || error instanceof TypeError;
    for (const options of [
      { direction: 'sideways' },
      { limit: 0 },
      { limit: 201 },
      { limit: true },
      { limit: 1.5 },
      { sim: '1' },
      { conversationId: '7' },
      { since: 'yesterday' },
      { conversation_id: 7 },
    ]) {
      await assert.rejects(client.messages(options), wrong, JSON.stringify(options));
    }
    // The iterators check their arguments at once, not at the first item.
    assert.throws(() => client.iterMessages({ pageSize: 500 }), RangeError);
    assert.throws(() => client.iterMessages({ maxItems: -1 }), RangeError);
    assert.throws(() => client.iterMessages({ direction: 'sideways' }), RangeError);
    assert.throws(() => client.iterContacts({ pageSize: 0 }), RangeError);
    await assert.rejects(client.getMessage('12'), TypeError);
    await assert.rejects(client.deleteMessage('12'), TypeError);
    await assert.rejects(client.deleteConversation(1.5), TypeError);
    await assert.rejects(client.markRead('179'), TypeError);
    await assert.rejects(client.subscribe([]), RangeError);
    await assert.rejects(client.waitForResult('r', { pollInterval: 0 }), RangeError);
    await assert.rejects(client.request('messages.get', { id: 1 }), /id/);
    await assert.rejects(client.sendRequest('ping', { id: 'mine' }), RangeError);
    assert.deepEqual(server.actions(), ['authenticate']);
  });

  test('getMessage', async (t) => {
    const { server, client } = await pair(t);
    const newest = (await client.messages({ limit: 1 })).items[0];
    assert.deepEqual(await client.getMessage(newest.id), newest);
    assert.deepEqual(Object.keys(newest).sort(), [
      'address', 'body', 'contact', 'conversationId', 'direction', 'id', 'read',
      'requestId', 'sentTimestamp', 'sim', 'status', 'timestamp',
    ]);
    assert.deepEqual(lastRequest(server, 'messages.get'), {
      id: 'js-3',
      action: 'messages.get',
      messageId: newest.id,
    });
    await assert.rejects(client.getMessage(1), (error) => {
      assert.ok(error instanceof ApiError && !(error instanceof AuthenticationError));
      assert.equal(error.code, 'not_found');
      return true;
    });
    assert.ok(await client.ping());
  });

  test('send', async (t) => {
    const { server, client } = await pair(t);
    const result = await client.send('+43 690 876-54321', 'All systems running.');
    assert.equal(result.to, BEN);
    assert.equal(result.parts, 1);
    assert.equal(result.status, 'queued');
    const requestId = result.requestId;
    assert.match(requestId, /^[0-9a-f]{32}$/);
    assert.deepEqual(lastRequest(server, 'messages.send'), {
      id: 'js-2',
      action: 'messages.send',
      to: '+43 690 876-54321',
      body: 'All systems running.',
      requestId,
    });
    assert.deepEqual(server.sent, [
      { requestId, to: BEN, body: 'All systems running.', sim: 1 },
    ]);
    const second = await client.send(BEN, 'All systems running.');
    assert.notEqual(second.requestId, requestId);
    assert.equal(server.sent.length, 2);
  });

  test('send with own requestId and sim', async (t) => {
    const { server, client } = await pair(t);
    const options = { sim: 2, requestId: 'order-42.confirm_1' };
    const result = await client.send(BEN, 'Grüße', options);
    assert.equal(result.requestId, 'order-42.confirm_1');
    assert.equal(server.sent[0].sim, 2);
    assert.equal(server.sent[0].body, 'Grüße');
    assert.equal(result.status, 'queued');
    const outcome = await client.waitForResult('order-42.confirm_1', { timeout: 5 });
    assert.equal(outcome.status, 'sent');
    // A repeat is not sent again; it tells the status of today.
    assert.deepEqual(await client.send(BEN, 'Grüße', options), { ...result, status: 'sent' });
    assert.equal(server.sent.length, 1);
    // The number may be written differently.
    const again = await client.send('0690/876.54.321', 'Grüße', options);
    assert.equal(again.to, BEN);
    // One requestId stands for one message.
    for (const [to, body] of [[BEN, 'Another text'], [ANNA, 'Grüße']]) {
      await assert.rejects(client.send(to, body, options), (error) => {
        return error instanceof ApiError && error.code === 'invalid_request';
      });
    }
    assert.equal(server.sent.length, 1);
  });

  test('repeat after a failure does not send again', async (t) => {
    const { server, client } = await pair(t);
    server.failNumbers.add('+43123456789');
    const first = await client.send('+43123456789', 'Hello', { requestId: 'r-1' });
    assert.equal(first.status, 'queued');
    assert.equal((await client.waitForResult('r-1', { timeout: 5 })).status, 'failed');
    server.failNumbers.clear();
    const again = await client.send('+43123456789', 'Hello', { requestId: 'r-1' });
    assert.deepEqual(again, { ...first, status: 'failed' });
    assert.equal(server.sent.length, 1);
    // A new attempt needs a new request id.
    const retry = await client.send('+43123456789', 'Hello', { requestId: 'r-2' });
    assert.equal((await client.waitForResult(retry.requestId, { timeout: 5 })).status, 'sent');
    assert.equal(server.sent.length, 2);
  });

  test('send refuses empty values and malformed request ids', async (t) => {
    const { server, client } = await pair(t);
    const empty = [['', 'text'], ['   ', 'text'], [BEN, ''], [null, 'text'], [BEN, null]];
    for (const [to, body] of empty) {
      await assert.rejects(client.send(to, body), RangeError);
    }
    for (const requestId of ['', 'a b', 'ä', 'x'.repeat(65), 'a/b']) {
      await assert.rejects(client.send(BEN, 'text', { requestId }), (error) => {
        return error instanceof RangeError && /requestId/.test(error.message);
      });
    }
    await assert.rejects(client.send(BEN, 'text', { sim: '2' }), TypeError);
    await assert.rejects(client.send(BEN, 'text', { request_id: 'r' }), TypeError);
    assert.deepEqual(server.sent, []);
    assert.deepEqual(server.actions(), ['authenticate']);
  });

  test('send errors', async (t) => {
    const { server, client } = await pair(t);
    for (const [to, body, sim, code] of [
      ['not a number', 'text', undefined, 'invalid_number'],
      [BEN, 'x'.repeat(1531), undefined, 'message_too_long'],
      [BEN, 'ü'.repeat(671), undefined, 'message_too_long'],
      [BEN, 'text', 7, 'sim_unavailable'],
    ]) {
      await assert.rejects(client.send(to, body, { sim }), (error) => {
        return error instanceof ApiError && error.code === code;
      });
    }
    assert.deepEqual(server.sent, []);
    assert.equal((await client.send(BEN, 'x'.repeat(1530))).parts, 10);
    assert.equal((await client.send(BEN, 'ü'.repeat(670))).parts, 10);
  });

  test('error details', async (t) => {
    const { server, client } = await pair(t);
    server.failNext('messages.send', 'permission_missing',
      'The permission to send SMS is missing.', { permission: 'sendSms' });
    await assert.rejects(client.send(BEN, 'text'), (error) => {
      assert.equal(error.code, 'permission_missing');
      assert.equal(error.message, 'The permission to send SMS is missing.');
      assert.equal(error.permission, 'sendSms');
      assert.equal(error.retryAfter, null);
      assert.deepEqual(error.details, { permission: 'sendSms' });
      return true;
    });
    server.failNext('messages.send', 'quota_exceeded');
    await assert.rejects(client.send(BEN, 'text'), (error) => error.code === 'quota_exceeded');
    assert.ok(await client.status());
  });

  test('messageStatus', async (t) => {
    const { server, client } = await pair(t);
    const { requestId } = await client.send(BEN, 'x'.repeat(200));
    assert.ok(await waitUntil(() => server.outbox.get(requestId).status === 'sent'));
    const entry = await client.messageStatus(requestId);
    assert.equal(entry.status, 'sent');
    assert.ok(entry.parts === 2 && entry.partsSent === 2);
    assert.equal(entry.source, 'api');
    assert.equal(entry.errorCode, null);
    assert.equal((await client.getMessage(entry.messageId)).requestId, requestId);
    await assert.rejects(client.messageStatus('unknown'), (error) => error.code === 'not_found');
  });

  test('waitForResult by polling', async (t) => {
    const { server, client } = await pair(t, { sendDelay: 0.2 });
    const { requestId } = await client.send(BEN, 'text');
    const entry = await client.waitForResult(requestId, { timeout: 5, pollInterval: 0.05 });
    assert.equal(entry.status, 'sent');
    assert.equal(entry.requestId, requestId);
    assert.equal(client.subscription, null);
    assert.ok(count(server, 'messages.status') >= 2);
  });

  test('waitForResult wakes up on the event', async (t) => {
    const { client } = await pair(t, { sendDelay: 1.0 });
    await client.subscribe(['sms.queued', 'sms.sent', 'sms.failed']);
    const { requestId } = await client.send(BEN, 'text');
    const started = performance.now();
    const entry = await client.waitForResult(requestId, { timeout: 10, pollInterval: 8 });
    // The client asks after 0.1, 0.3, 0.7 and 1.5 seconds. The message is
    // sent after 1 second; only the event can tell that early.
    const elapsed = (performance.now() - started) / 1000;
    assert.ok(elapsed > 0.9 && elapsed < 1.4, String(elapsed));
    assert.equal(entry.status, 'sent');
    // The events are still there for the application.
    const names = (await collect(client.events({ timeout: 0.05 }))).map((e) => e.event);
    assert.deepEqual(names, ['sms.queued', 'sms.sent']);
  });

  test('waitForResult failed and delivered', async (t) => {
    const { server, client } = await pair(t, { sendDelay: 0.1, deliveryReports: true });
    server.failNumbers.add('+43123456');
    await client.subscribe('*');
    const bad = (await client.send('+43123456', 'text')).requestId;
    const failed = await client.waitForResult(bad, { timeout: 5 });
    assert.equal(failed.status, 'failed');
    assert.equal(failed.errorCode, 1);
    const event = await client.waitForEvent('sms.failed', { timeout: 1 });
    assert.deepEqual(event.data, {
      requestId: bad,
      id: null,
      conversationId: null,
      to: '+43123456',
      errorCode: 1,
      reason: 'generic_failure',
    });

    const good = (await client.send(BEN, 'text')).requestId;
    const entry = await client.waitForResult(good, {
      timeout: 5,
      delivered: true,
      pollInterval: 0.05,
    });
    assert.equal(entry.status, 'delivered');
    const delivered = await client.waitForEvent('sms.delivered', { timeout: 1 });
    const message = await client.getMessage(entry.messageId);
    assert.deepEqual(delivered.data, {
      requestId: good,
      id: entry.messageId,
      conversationId: message.conversationId,
      to: BEN,
    });
    assert.equal(message.status, 'delivered');
  });

  test('waitForResult times out', async (t) => {
    const { server, client } = await pair(t, { sendDelay: 30 });
    const { requestId } = await client.send(BEN, 'text');
    await assert.rejects(
      client.waitForResult(requestId, { timeout: 0.3, pollInterval: 0.1 }),
      (error) => {
        assert.ok(error instanceof TimeoutError);
        assert.equal(error.name, 'TimeoutError');
        assert.match(error.message, /queued/);
        return true;
      },
    );
    assert.ok(count(server, 'messages.status') >= 2);
    assert.ok(await client.ping());
  });

  test('deleteMessage and deleteConversation', async (t) => {
    const { server, client } = await pair(t);
    await client.subscribe('sms.deleted');
    const newest = (await client.messages({ limit: 1 })).items[0];
    assert.deepEqual(await client.deleteMessage(newest.id), { deleted: 1, mode: 'hidden' });
    assert.deepEqual(lastRequest(server, 'messages.delete'), {
      id: 'js-4',
      action: 'messages.delete',
      messageId: newest.id,
    });
    assert.equal(server.messages.length, 28);
    await assert.rejects(client.deleteMessage(newest.id), (error) => error.code === 'not_found');
    let event = await client.waitForEvent('sms.deleted', { timeout: 2 });
    assert.deepEqual(event.data, {
      id: newest.id,
      conversationId: newest.conversationId,
      address: newest.address,
      mode: 'hidden',
    });

    server.deleteMode = 'provider';
    assert.deepEqual(await client.deleteConversation(newest.conversationId), {
      deleted: 24,
      mode: 'provider',
    });
    assert.equal(lastRequest(server, 'conversations.delete').conversationId, newest.conversationId);
    event = await client.waitForEvent('sms.deleted', { timeout: 2 });
    assert.deepEqual(event.data, {
      id: null,
      conversationId: newest.conversationId,
      address: ANNA,
      mode: 'provider',
    });
    assert.deepEqual(
      (await client.conversations()).items.map((item) => item.address),
      [BEN, 'A1'],
    );
    await assert.rejects(
      client.deleteConversation(newest.conversationId),
      (error) => error.code === 'not_found',
    );
  });

  test('markRead', async (t) => {
    const { server, client } = await pair(t);
    const anna = (await client.conversations({ q: 'anna' })).items[0];
    assert.equal((await client.status()).defaultSmsApp, false);
    await assert.rejects(client.markRead(anna.id), (error) => error.code === 'not_default_app');
    assert.equal((await client.conversations({ q: 'anna' })).items[0].unreadCount, 3);

    server.deleteMode = 'provider';
    assert.deepEqual(await client.markRead(anna.id), { ok: true });
    const request = lastRequest(server, 'conversations.markRead');
    assert.deepEqual([request.action, request.conversationId], ['conversations.markRead', anna.id]);
    assert.equal((await client.conversations({ q: 'anna' })).items[0].unreadCount, 0);
    assert.deepEqual((await client.messages({ address: ANNA, unread: true })).items, []);
    await assert.rejects(client.markRead(1), (error) => error.code === 'not_found');
  });

  test('contacts', async (t) => {
    const { server, client } = await pair(t);
    server.addContact('Zoe Example', '+43660111222');
    const names = (page) => page.items.map((contact) => contact.name);
    const page = await client.contacts();
    assert.deepEqual(names(page), ['Anna Example', 'Ben Example', 'Zoe Example']);
    assert.deepEqual(page.items[1].numbers, [{ number: BEN, label: 'Work' }]);
    assert.deepEqual(names(await client.contacts({ q: 'zoe' })), ['Zoe Example']);
    assert.deepEqual(names(await client.contacts({ q: '5678' })), ['Anna Example']);
    const first = await client.contacts({ limit: 2 });
    assert.notEqual(first.nextCursor, null);
    const second = await client.contacts({ limit: 2, cursor: first.nextCursor });
    assert.deepEqual(names(second), ['Zoe Example']);
    const some = await collect(client.iterContacts({ pageSize: 1, maxItems: 2 }));
    assert.deepEqual(some.map((c) => c.name), ['Anna Example', 'Ben Example']);
  });

  test('generic request', async (t) => {
    const { client } = await pair(t);
    const page = await client.request('messages.list', { limit: 1, direction: 'out' });
    assert.equal(page.items.length, 1);
    await assert.rejects(client.request('messages.archive'), (error) => {
      return error instanceof ApiError && error.code === 'unknown_action';
    });
  });

  test('frames are compact JSON', async (t) => {
    const received = [];
    const { server, client } = await pair(t);
    const original = server._onFrame.bind(server);
    server._onFrame = (peer, frame) => {
      received.push(frame);
      original(peer, frame);
    };
    await client.send(BEN, 'Grüße', { requestId: 'r1' });
    assert.deepEqual(received, [
      '{"id":"js-2","action":"messages.send","to":"+4369087654321",' +
        '"body":"Grüße","requestId":"r1"}',
    ]);
  });

  test('answers may be larger than 64 KiB', async (t) => {
    const { server, client } = await pair(t);
    for (let index = 0; index < 200; index += 1) {
      server.addMessage('+436601234567', `${index} ${'x'.repeat(1500)}`);
    }
    const page = await client.messages({ address: '+436601234567', limit: 200 });
    assert.equal(page.items.length, 200);
    assert.ok(JSON.stringify(page).length > 300_000);
  });

  test('refuses requests over 64 KiB', async (t) => {
    const { server, client } = await pair(t);
    await assert.rejects(client.send(BEN, 'x'.repeat(70_000)), (error) => {
      return error instanceof RangeError && /64 KiB/.test(error.message);
    });
    assert.deepEqual(server.actions(), ['authenticate']);
    assert.ok(await client.ping());
  });

  test('close code 1009', async (t) => {
    const { client } = await pair(t);
    client._maxFrameBytes = 1024 * 1024;
    await assert.rejects(client.send(BEN, 'x'.repeat(70_000)), (error) => {
      return error instanceof ProtocolError && /64 KiB/.test(error.message);
    });
    // The same frame would fail again: no second connection.
    assert.equal(client.connectionCount, 1);
    assert.equal(client.connected, false);
  });
});

// ----------------------------------------------------------------------
// Several requests at once
// ----------------------------------------------------------------------

describe('several requests at once', needTls, () => {
  test('requests awaited together are matched by id', async (t) => {
    const { server, client } = await pair(t);
    server.reverseNextResponses(3);
    const [page, status, contacts] = await Promise.all([
      client.messages({ limit: 1 }),
      client.status(),
      client.contacts(),
    ]);
    assert.equal(page.items[0].body, 'Anna 24');
    assert.equal(status.deleteMode, 'hidden');
    assert.equal(contacts.items.length, 2);
    const ids = server.requests.slice(1).map((request) => request.id);
    assert.equal(new Set(ids).size, 3);
    assert.equal(client.connectionCount, 1);
  });

  test('requests awaited together share one connection attempt', async (t) => {
    const server = await startServer(t);
    const client = await makeClient(t, server, { connect: false });
    const results = await Promise.all([client.ping(), client.ping(), client.status()]);
    assert.equal(results.length, 3);
    assert.equal(client.connectionCount, 1);
    assert.equal(server.loginCount, 1);
    // And one reconnect when the connection is lost under all of them.
    server.dropOn('ping', 'before');
    await Promise.all([client.ping(), client.status(), client.contacts()]);
    assert.equal(client.connectionCount, 2);
    assert.equal(server.loginCount, 2);
  });

  test('responses in another order', async (t) => {
    const { server, client } = await pair(t);
    server.reverseNextResponses(3);
    const first = await client.sendRequest('messages.list', { limit: 1 });
    const second = await client.sendRequest('status');
    const third = await client.sendRequest('contacts.list');
    assert.equal(new Set([first, second, third]).size, 3);
    assert.ok('deleteMode' in (await client.waitForResponse(second, { timeout: 2 })));
    assert.equal((await client.waitForResponse(first, { timeout: 2 })).items[0].body, 'Anna 24');
    assert.equal((await client.waitForResponse(third, { timeout: 2 })).items.length, 2);
    await assert.rejects(client.waitForResponse(first), RangeError);
    assert.ok(await client.ping());
  });

  test('response error of a pipelined request', async (t) => {
    const { server, client } = await pair(t);
    server.reverseNextResponses(2);
    const missing = await client.sendRequest('messages.get', { messageId: 1 });
    const fine = await client.sendRequest('ping');
    await assert.rejects(client.waitForResponse(missing, { timeout: 2 }), (error) => {
      return error instanceof ApiError && error.code === 'not_found';
    });
    assert.ok((await client.waitForResponse(fine, { timeout: 2 })).time > 0);
  });

  test('waitForResponse times out and can wait again', async (t) => {
    const { server, client } = await pair(t);
    server.reverseNextResponses(2);
    const held = await client.sendRequest('status');
    await assert.rejects(client.waitForResponse(held, { timeout: 0.1 }), TimeoutError);
    assert.equal(client.connected, true);
    const other = await client.sendRequest('ping');
    assert.ok((await client.waitForResponse(other, { timeout: 2 })).time > 0);
    assert.equal((await client.waitForResponse(held, { timeout: 2 })).deleteMode, 'hidden');
  });

  test('pipelined requests do not reconnect', async (t) => {
    const { server, client } = await pair(t);
    server.dropOn('status', 'after');
    const requestId = await client.sendRequest('status');
    await assert.rejects(client.waitForResponse(requestId, { timeout: 2 }), ConnectionInterrupted);
    assert.equal(client.connectionCount, 1);
    // Also when the connection was lost before anybody waited.
    const later = await client.sendRequest('ping');
    assert.equal(client.connectionCount, 2);
    server.dropConnections();
    assert.ok(await waitUntil(() => !client.connected));
    await assert.rejects(client.waitForResponse(later), ConnectionInterrupted);
    await assert.rejects(client.waitForResponse(later), RangeError);
  });

  test('stale responses are ignored', async (t) => {
    const { server, client } = await pair(t);
    server.beforeNextResponse(
      { id: 'js-0', ok: true, result: { stale: true } },
      { id: 77, ok: false, error: { code: 'internal_error', message: 'stale' } },
    );
    assert.equal((await client.status()).deleteMode, 'hidden');
    assert.ok((await client.ping()).time > 0);
  });

  test('response without id answers the open request', async (t) => {
    const { server, client } = await pair(t);
    server.beforeNextResponse({
      id: null,
      ok: false,
      error: { code: 'invalid_json', message: 'Not a JSON object.' },
    });
    await assert.rejects(client.status(), (error) => error.code === 'invalid_json');
    // The late real response is dropped; the next request works.
    assert.ok((await client.ping()).time > 0);
  });

  test('malformed frames', async (t) => {
    const { server, client } = await pair(t);
    for (const [frame, message] of [
      ['this is not JSON', /invalid JSON/],
      ['[1, 2, 3]', /not a JSON object/],
    ]) {
      server.beforeNextResponse(frame);
      await assert.rejects(client.status(), (error) => {
        return error instanceof ProtocolError && message.test(error.message);
      });
    }
    // The client dropped the connections it could not trust any more.
    assert.ok(await client.ping());
    assert.equal(client.connectionCount, 3);
  });

  test('malformed response does not show its content', async (t) => {
    const { server, client } = await pair(t);
    server.beforeNextResponse({ id: 'js-2', ok: false, body: 'Text of an SMS' });
    await assert.rejects(client.status(), (error) => {
      assert.ok(error instanceof ProtocolError);
      assert.match(error.message, /Malformed/);
      assert.ok(!error.message.includes('Text of an SMS'));
      return true;
    });
  });
});

// ----------------------------------------------------------------------
// Events
// ----------------------------------------------------------------------

describe('events', needTls, () => {
  test('subscribe and unsubscribe', async (t) => {
    const { server, client } = await pair(t);
    assert.equal(client.subscription, null);
    assert.deepEqual(await client.subscribe('sms.received'), {
      events: ['sms.received'],
      address: null,
    });
    // subscribe adds names; its address replaces the filter.
    const expected = { events: ['sms.received', 'sms.sent', 'sms.failed'], address: ANNA };
    const added = await client.subscribe(['sms.sent', 'sms.failed'], { address: ANNA });
    assert.deepEqual(added, expected);
    assert.deepEqual(client.subscription, expected);
    assert.deepEqual(await client.unsubscribe(['sms.sent']), {
      events: ['sms.received', 'sms.failed'],
      address: ANNA,
    });
    // Without address the filter is removed.
    assert.deepEqual(await client.subscribe('sms.queued'), {
      events: ['sms.received', 'sms.queued', 'sms.failed'],
      address: null,
    });
    assert.ok(!('address' in lastRequest(server, 'subscribe')));
    // When no name is left, the filter is removed too.
    await client.subscribe('sms.queued', { address: BEN });
    assert.deepEqual(await client.unsubscribe('*'), { events: [], address: null });
    assert.deepEqual(client.subscription, { events: [], address: null });
    // In the answer * is replaced by the names it stands for.
    assert.deepEqual(await client.subscribe('*'), { events: [...EVENT_NAMES], address: null });
    const request = lastRequest(server, 'subscribe');
    assert.deepEqual([request.action, request.events], ['subscribe', ['*']]);
    await assert.rejects(client.subscribe('sms.unknown'), (error) => {
      return error instanceof ApiError && error.code === 'invalid_request';
    });
  });

  test('a new connection gets no events', async (t) => {
    const { server, client } = await pair(t);
    server.receiveSms(ANNA, 'Nobody listens');
    assert.deepEqual(await collect(client.events({ timeout: 0.2 })), []);
  });

  test('incoming sms', async (t) => {
    const { server, client } = await pair(t);
    await client.subscribe('sms.received');
    const stored = server.receiveSms(ANNA, 'STATUS');
    const event = await client.waitForEvent('sms.received', { timeout: 2 });
    assert.equal(event.event, 'sms.received');
    assert.equal(event.seq, 1);
    assert.deepEqual(event.data.message, stored);
    assert.equal(event.data.message.contact, 'Anna Example');
    assert.equal(event.data.message.read, false);
    assert.equal((await client.getMessage(event.data.message.id)).body, 'STATUS');
  });

  test('react to an incoming sms inside the loop', async (t) => {
    const { server, client } = await pair(t);
    await client.subscribe('sms.received');
    server.receiveSms(ANNA, 'STATUS');
    for await (const event of client.events({ timeout: 2 })) {
      await client.send(event.data.message.address, 'All systems running.');
      break;
    }
    assert.deepEqual(
      server.sent.map((m) => [m.to, m.body]),
      [[ANNA, 'All systems running.']],
    );
  });

  test('events between request and response are queued', async (t) => {
    const { server, client } = await pair(t);
    await client.subscribe('sms.received');
    server.beforeNextResponse(eventFrame(1, 'first'), eventFrame(2, 'second'));
    assert.equal((await client.status()).deleteMode, 'hidden');
    assert.equal(client.pendingEvents, 2);
    server.beforeNextResponse(eventFrame(3, 'third'));
    assert.ok((await client.ping()).time > 0);
    assert.equal(client.pendingEvents, 3);
    const events = await collect(client.events({ timeout: 0.1 }));
    assert.deepEqual(events.map((event) => event.seq), [1, 2, 3]);
    assert.deepEqual(
      events.map((event) => event.data.message.body),
      ['first', 'second', 'third'],
    );
    assert.equal(client.pendingEvents, 0);
  });

  test('events end after the timeout', async (t) => {
    const { client } = await pair(t);
    await client.subscribe('*');
    const started = performance.now();
    assert.deepEqual(await collect(client.events({ timeout: 0.2 })), []);
    const elapsed = (performance.now() - started) / 1000;
    assert.ok(elapsed > 0.15 && elapsed < 2);
    assert.equal(client.connected, true);
  });

  test('events arrive in order', async (t) => {
    const { server, client } = await pair(t);
    await client.subscribe('sms.received');
    for (let index = 0; index < 5; index += 1) {
      server.receiveSms(ANNA, `Message ${index}`);
    }
    const events = await collect(client.events({ timeout: 0.3 }));
    assert.deepEqual(
      events.map((e) => e.data.message.body),
      range(0, 5).map((index) => `Message ${index}`),
    );
    assert.deepEqual(events.map((e) => e.seq), [1, 2, 3, 4, 5]);
  });

  test('waitForEvent keeps the others', async (t) => {
    const { server, client } = await pair(t);
    await client.subscribe('*');
    server.receiveSms(ANNA, 'one');
    server.emit('conversations.changed', {});
    server.receiveSms(BEN, 'two');
    const changed = await client.waitForEvent('conversations.changed', { timeout: 2 });
    assert.deepEqual(changed.data, {});
    assert.equal(client.pendingEvents, 2);
    const seen = [];
    const fromBen = await client.waitForEvent('sms.received', {
      timeout: 2,
      predicate: (event) => {
        seen.push(event.data.message.body);
        return event.data.message.address === BEN;
      },
    });
    assert.equal(fromBen.data.message.body, 'two');
    // Every candidate is shown to the predicate once.
    assert.deepEqual(seen, ['one', 'two']);
    assert.equal(client.pendingEvents, 1);
    const one = await client.waitForEvent(['sms.sent', 'sms.received'], { timeout: 2 });
    assert.equal(one.data.message.body, 'one');
    assert.equal(client.pendingEvents, 0);
  });

  test('waitForEvent times out', async (t) => {
    const { server, client } = await pair(t);
    await client.subscribe('*');
    server.emit('conversations.changed', {});
    await assert.rejects(client.waitForEvent('sms.received', { timeout: 0.2 }), (error) => {
      return error instanceof TimeoutError && /sms\.received/.test(error.message);
    });
    assert.equal(client.pendingEvents, 1);
    assert.equal(client.connected, true);
  });

  test('address filter compares like the phone', async (t) => {
    const { server, client } = await pair(t);
    const bodies = async () =>
      (await collect(client.events({ timeout: 0.2 }))).map((e) => e.data.message.body);
    // A number without country code matches every number it ends.
    await client.subscribe('sms.received', { address: '0690 876 54 321' });
    server.receiveSms(ANNA, 'from Anna');
    server.receiveSms(BEN, 'international');
    server.receiveSms('0043 690 87654321', 'with 00');
    server.receiveSms('+49 690 87654321', 'another country');
    server.receiveSms('0690 876 54 322', 'another number');
    assert.deepEqual(await bodies(), ['international', 'with 00', 'another country']);
    await client.subscribe('sms.received', { address: 'mybank' });
    server.receiveSms('MyBank', 'Your code is 1234');
    server.receiveSms('MyBank2', 'other sender');
    assert.deepEqual(await bodies(), ['Your code is 1234']);
  });

  test('full event queue drops the oldest', async (t) => {
    const server = await startServer(t);
    const client = await makeClient(t, server, { eventQueueSize: 2 });
    server.beforeNextResponse(...[1, 2, 3, 4].map((n) => eventFrame(n)));
    await client.ping();
    assert.equal(client.pendingEvents, 2);
    assert.equal(client.droppedEvents, 2);
    const events = await collect(client.events({ timeout: 0.05 }));
    assert.deepEqual(events.map((e) => e.seq), [3, 4]);
    assert.equal(client.latestSeq, 4);
  });

  test('close() ends a waiting events() loop and rejects open requests', async (t) => {
    const { server, client } = await pair(t);
    await client.subscribe('sms.received');
    const loop = collect(client.events());
    server.ignoreNext('status');
    const request = assert.rejects(client.status(), ConnectionInterrupted);
    const waiting = assert.rejects(client.waitForEvent('sms.sent'), ConnectionInterrupted);
    await sleep(0.05);
    await client.close();
    assert.deepEqual(await loop, []);
    await request;
    await waiting;
    assert.equal(client.connectionCount, 1);
    assert.ok(await waitUntil(() => server.clientCount === 0));
    // The client can be used again.
    assert.ok(await client.ping());
    assert.equal(client.connectionCount, 2);
  });
});

// ----------------------------------------------------------------------
// Reconnect
// ----------------------------------------------------------------------

describe('reconnect', needTls, () => {
  test('reconnects and logs in again', async (t) => {
    const { server, client } = await pair(t);
    assert.ok(await client.ping());
    server.dropConnections();
    assert.equal((await client.status()).deleteMode, 'hidden');
    assert.equal(client.connectionCount, 2);
    assert.equal(server.loginCount, 2);
    assert.deepEqual(server.actions(), ['authenticate', 'ping', 'authenticate', 'status']);
  });

  test('reconnects after a close frame', async (t) => {
    const { server, client } = await pair(t);
    for (const [code, reason] of [
      [1001, 'server is stopping'],
      [4408, 'no answer to pings'],
      [4429, 'too many requests'],
      [4409, 'too many clients'],
    ]) {
      server.dropConnections(code, reason);
      assert.ok(await client.ping());
    }
    assert.equal(client.connectionCount, 5);
  });

  test('request lost before it was handled', async (t) => {
    const { server, client } = await pair(t);
    server.dropOn('status', 'before');
    server.dropOn('status', 'before');
    assert.equal((await client.status()).deleteMode, 'hidden');
    assert.equal(count(server, 'status'), 3);
    assert.equal(client.connectionCount, 3);
  });

  test('restores the subscription with its address filter', async (t) => {
    const { server, client } = await pair(t);
    await client.subscribe(['sms.received', 'sms.failed'], { address: BEN });
    server.dropConnections();
    assert.ok(await client.ping());
    assert.deepEqual(server.actions(), [
      'authenticate', 'subscribe', 'authenticate', 'subscribe', 'ping',
    ]);
    const restored = server.requests[3];
    assert.deepEqual(restored.events, ['sms.received', 'sms.failed']);
    assert.equal(restored.address, BEN);
    server.receiveSms(ANNA, 'filtered out');
    server.receiveSms(BEN, 'after the reconnect');
    const event = await client.waitForEvent('sms.received', { timeout: 2 });
    assert.equal(event.data.message.body, 'after the reconnect');
    assert.equal(client.pendingEvents, 0);
  });

  test('does not restore an empty subscription', async (t) => {
    const { server, client } = await pair(t);
    await client.subscribe('sms.received');
    await client.unsubscribe('sms.received');
    server.dropConnections();
    assert.ok(await client.ping());
    assert.equal(count(server, 'subscribe'), 1);
  });

  test('waiting for events reconnects', async (t) => {
    const { server, client } = await pair(t);
    await client.subscribe('sms.received');
    const events = [];
    const loop = (async () => {
      for await (const event of client.events({ timeout: 5 })) {
        events.push(event);
        break;
      }
    })();
    await sleep(0.05);
    server.dropConnections();
    assert.ok(await waitUntil(() => count(server, 'subscribe') === 2));
    server.receiveSms(ANNA, 'after the reconnect');
    await loop;
    assert.equal(events[0].data.message.body, 'after the reconnect');
    assert.equal(client.connectionCount, 2);
  });

  test('send is not repeated after a lost response', async (t) => {
    const { server, client } = await pair(t);
    server.dropOn('messages.send', 'after');
    const result = await client.send(BEN, 'Only once');
    // The repeat tells the status of now; the message may be out already.
    assert.ok(['queued', 'sent'].includes(result.status));
    const requests = server.requests.filter((r) => r.action === 'messages.send');
    assert.equal(requests.length, 2);
    assert.equal(requests[0].requestId, result.requestId);
    assert.equal(requests[1].requestId, result.requestId);
    assert.notEqual(requests[0].id, requests[1].id);
    assert.equal(client.connectionCount, 2);
    assert.equal((await client.waitForResult(result.requestId, { timeout: 5 })).status, 'sent');
    // The radio was used once although the request arrived twice.
    assert.deepEqual(server.sent.map((m) => m.body), ['Only once']);
    assert.equal(server.messages.filter((m) => m.body === 'Only once').length, 1);
  });

  test('request ids outlive a restart of the app', async (t) => {
    const { server, client } = await pair(t);
    await client.send(BEN, 'Hello', { requestId: 'r-1' });
    assert.equal((await client.waitForResult('r-1', { timeout: 5 })).status, 'sent');
    server.restart();
    assert.equal((await client.send(BEN, 'Hello', { requestId: 'r-1' })).status, 'sent');
    assert.equal(server.sent.length, 1);
  });

  test('waitForResult survives a reconnect', async (t) => {
    const { server, client } = await pair(t, { sendDelay: 0.3 });
    await client.subscribe(['sms.sent', 'sms.failed']);
    const { requestId } = await client.send(BEN, 'text');
    server.dropConnections();
    const entry = await client.waitForResult(requestId, { timeout: 5, pollInterval: 0.1 });
    assert.equal(entry.status, 'sent');
    assert.equal(server.sent.length, 1);
  });

  test('gives up after the last attempt', async (t) => {
    const server = await startServer(t);
    const client = await makeClient(t, server, { reconnectAttempts: 3, connectTimeout: 2 });
    await server.stop();
    await assert.rejects(client.status(), (error) => {
      assert.ok(error instanceof ConnectionInterrupted);
      assert.match(error.message, /'status'/);
      assert.ok(error.cause instanceof Error);
      return true;
    });
    assert.equal(client.connected, false);
  });

  test('backoff is 1, 2, 4, 8, 10 seconds', async (t) => {
    const server = await startServer(t);
    const client = await makeClient(t, server, {
      reconnectAttempts: 6,
      reconnectDelay: 1,
      maxReconnectDelay: 10,
      connectTimeout: 2,
    });
    await server.stop();
    const delays = recordSleeps(client);
    await assert.rejects(client.status(), ConnectionInterrupted);
    assert.deepEqual(delays, [1, 2, 4, 8, 10, 10]);
  });

  test('no retry on request', async (t) => {
    const { server, client } = await pair(t);
    server.dropOn('status', 'before');
    await assert.rejects(client.request('status', {}, { retry: false }), ConnectionInterrupted);
    assert.equal(client.connectionCount, 1);
    assert.ok(await client.status());
    assert.equal(client.connectionCount, 2);
  });

  test('zero reconnect attempts', async (t) => {
    const server = await startServer(t);
    const client = await makeClient(t, server, { reconnectAttempts: 0 });
    server.dropOn('status', 'before');
    await assert.rejects(client.status(), ConnectionInterrupted);
    assert.equal(client.connectionCount, 1);
  });

  for (const [code, kind, errorCode] of [
    [4410, AuthenticationError, 'not_authenticated'],
    [4401, AuthenticationError, 'not_authenticated'],
    [4403, LicenceRequiredError, 'licence_required'],
  ]) {
    test(`close code ${code} forbids a retry`, async (t) => {
      const { server, client } = await pair(t);
      // While a request waits for its response ...
      server.ignoreNext('status');
      const request = client.status();
      await waitUntil(() => count(server, 'status') === 1);
      server.dropConnections(code);
      await assert.rejects(request, (error) => {
        assert.ok(error instanceof kind);
        assert.equal(error.code, errorCode);
        assert.equal(error.details.closeCode, code);
        return true;
      });
      // No new login with a password that may be wrong by now.
      assert.equal(server.loginCount, 1);
      assert.equal(client.connectionCount, 1);
    });

    test(`close code ${code} while nobody waits is told to the next request`, async (t) => {
      const { server, client } = await pair(t);
      server.dropConnections(code);
      assert.ok(await waitUntil(() => !client.connected));
      await assert.rejects(client.status(), (error) => {
        return error instanceof kind && error.details.closeCode === code;
      });
      assert.equal(server.loginCount, 1);
      assert.equal(client.connectionCount, 1);
    });
  }

  test('password changed while listening', async (t) => {
    const { server, client } = await pair(t);
    await client.subscribe('sms.received');
    setTimeout(() => server.dropConnections(4410, 'password changed'), 100);
    await assert.rejects(collect(client.events({ timeout: 5 })), (error) => {
      return error instanceof AuthenticationError && /password changed/.test(error.message);
    });
    assert.equal(server.loginCount, 1);
  });

  test('protocol error close code', async (t) => {
    const { server, client } = await pair(t);
    server.dropConnections(4400, 'binary frame');
    await assert.rejects(client.status(), (error) => {
      return error instanceof ProtocolError && /binary frame/.test(error.message);
    });
    assert.equal(client.connectionCount, 1);
  });

  test('request timeout reconnects', async (t) => {
    const server = await startServer(t);
    const client = await makeClient(t, server, { timeout: 0.3, reconnectAttempts: 1 });
    server.ignoreNext('status');
    assert.equal((await client.status()).deleteMode, 'hidden');
    assert.equal(client.connectionCount, 2);
    server.ignoreNext('status', 2);
    await assert.rejects(client.status(), (error) => {
      return error instanceof ConnectionInterrupted && /No response/.test(error.message);
    });
    assert.equal(client.connectionCount, 3);
  });
});

// ----------------------------------------------------------------------
// Epoch and sequence numbers
// ----------------------------------------------------------------------

describe('epoch and sequence numbers', needTls, () => {
  test('login tells epoch and sequence number', async (t) => {
    const server = await startServer(t);
    server.receiveSms(ANNA, 'one');
    server.receiveSms(ANNA, 'two');
    const client = await makeClient(t, server, { connect: false });
    assert.equal(client.epoch, null);
    assert.equal(client.latestSeq, null);
    await client.connect();
    assert.equal(client.epoch, server.epoch);
    assert.equal(client.latestSeq, 2);
    assert.equal(client.serverRestarted, false);
    assert.equal(client.missedEvents, 0);
  });

  test('latestSeq follows the events', async (t) => {
    const { server, client } = await pair(t);
    await client.subscribe('conversations.changed');
    server.receiveSms(ANNA, 'not subscribed');
    server.emit('conversations.changed', {});
    assert.equal((await client.waitForEvent('conversations.changed', { timeout: 2 })).seq, 2);
    assert.equal(client.latestSeq, 2);
    server.beforeNextResponse(eventFrame(7));
    await client.ping();
    assert.equal(client.latestSeq, 7);
  });

  test('counts the events missed while disconnected', async (t) => {
    const calls = [];
    const server = await startServer(t);
    const client = await makeClient(t, server, {
      onReconnect: (c) => {
        calls.push([c.missedEvents, c.serverRestarted, c.connected, c.subscription]);
      },
    });
    assert.deepEqual(calls, []);
    await client.subscribe('sms.received');
    server.receiveSms(ANNA, 'seen');
    assert.equal((await client.waitForEvent('sms.received', { timeout: 2 })).seq, 1);
    server.dropConnections();
    assert.ok(await waitUntil(() => server.clientCount === 0));
    for (const body of ['lost one', 'lost two', 'lost three']) {
      server.receiveSms(ANNA, body);
    }
    assert.ok(await client.ping());
    assert.equal(client.missedEvents, 3);
    assert.equal(client.serverRestarted, false);
    assert.equal(client.latestSeq, 4);
    // The hook runs once the subscription is in force again.
    assert.deepEqual(calls, [[3, false, true, { events: ['sms.received'], address: null }]]);
    server.dropConnections();
    assert.ok(await client.ping());
    assert.equal(client.missedEvents, 0);
    assert.equal(calls.length, 2);
  });

  test('notices a restart of the app', async (t) => {
    const { server, client } = await pair(t);
    await client.subscribe('sms.received');
    for (let index = 0; index < 5; index += 1) {
      server.receiveSms(ANNA, `before ${index}`);
    }
    assert.equal((await collect(client.events({ timeout: 0.3 }))).length, 5);
    const first = client.epoch;
    server.restart();
    server.receiveSms(ANNA, 'lost');
    assert.ok(await client.ping());
    assert.equal(client.serverRestarted, true);
    assert.equal(client.missedEvents, null);
    assert.equal(client.epoch, server.epoch);
    assert.notEqual(client.epoch, first);
    assert.equal(client.latestSeq, 1);
    server.receiveSms(ANNA, 'after');
    assert.equal((await client.waitForEvent('sms.received', { timeout: 2 })).seq, 2);
    server.dropConnections();
    assert.ok(await client.ping());
    assert.equal(client.serverRestarted, false);
    assert.equal(client.missedEvents, 0);
  });

  test('an async hook reads what was missed before the loop goes on', async (t) => {
    const seen = [];
    let newest = 0;
    const handle = (message) => {
      seen.push(message.body);
      newest = Math.max(newest, message.timestamp);
    };
    const server = await startServer(t);
    const client = await makeClient(t, server, {
      onReconnect: async (dock) => {
        // Requests inside the hook use the connection that is being opened.
        const missed = await collect(dock.iterMessages({ direction: 'in', since: newest + 1 }));
        await sleep(0.05);
        for (const message of missed.reverse()) {
          handle(message);
        }
      },
    });
    await client.subscribe('sms.received');
    server.receiveSms(ANNA, 'first');
    handle((await client.waitForEvent('sms.received', { timeout: 2 })).data.message);
    server.dropConnections();
    assert.ok(await waitUntil(() => server.clientCount === 0));
    await sleep(0.005);
    server.receiveSms(ANNA, 'while away 1');
    await sleep(0.005);
    server.receiveSms(BEN, 'while away 2');
    const loop = (async () => {
      for await (const event of client.events({ timeout: 5 })) {
        handle(event.data.message);
        break;
      }
    })();
    assert.ok(await waitUntil(() => count(server, 'messages.list') === 1));
    // A request from outside waits until the hook is done.
    const outside = client.ping().then(() => seen.length);
    server.receiveSms(ANNA, 'live again');
    await loop;
    assert.deepEqual(seen, ['first', 'while away 1', 'while away 2', 'live again']);
    assert.ok((await outside) >= 3);
    assert.equal(client.connectionCount, 2);
  });

  test('hook can be set later and its error is reported', async (t) => {
    const { server, client } = await pair(t);
    const calls = [];
    client.onReconnect = (c) => calls.push(c.connectionCount);
    server.dropConnections();
    await client.ping();
    assert.deepEqual(calls, [2]);
    client.onReconnect = () => {
      throw new Error('hook failed');
    };
    server.dropConnections();
    await assert.rejects(client.ping(), /hook failed/);
    assert.equal(client.connected, false);
  });
});

// ----------------------------------------------------------------------
// Rate limit
// ----------------------------------------------------------------------

describe('rate limit', needTls, () => {
  test('waits and repeats when rate limited', async (t) => {
    const { server, client } = await pair(t);
    const waits = recordSleeps(client);
    server.failNext('status', 'rate_limited', 'Too many requests.', { retryAfter: 1 });
    assert.equal((await client.status()).deleteMode, 'hidden');
    assert.deepEqual(waits, [1]);
    server.failNext('status', 'rate_limited', 'x', { retryAfter: 1 });
    server.failNext('status', 'rate_limited', 'x', { retryAfter: 3 });
    assert.equal((await client.status()).deleteMode, 'hidden');
    assert.deepEqual(waits, [1, 1, 3]);
    assert.equal(count(server, 'status'), 5);
    assert.equal(client.connectionCount, 1);
  });

  test('gives up when rate limited again and again', async (t) => {
    const { server, client } = await pair(t);
    const waits = recordSleeps(client);
    for (let index = 0; index < 3; index += 1) {
      server.failNext('status', 'rate_limited', 'x', { retryAfter: 1 });
    }
    await assert.rejects(client.status(), (error) => {
      assert.ok(error instanceof ApiError);
      assert.equal(error.code, 'rate_limited');
      assert.equal(error.retryAfter, 1);
      return true;
    });
    assert.deepEqual(waits, [1, 1]);
    assert.equal(count(server, 'status'), 3);
    assert.ok(await client.ping());
  });

  test('limit of messages per minute is reported at once', async (t) => {
    const { server, client } = await pair(t);
    const waits = recordSleeps(client);
    // The limit of 20 SMS parts per minute names no seconds to wait.
    server.failNext('messages.send', 'rate_limited', 'Too many messages per minute.');
    await assert.rejects(client.send(BEN, 'text'), (error) => {
      return error.code === 'rate_limited' && error.retryAfter === null;
    });
    assert.deepEqual(waits, []);
    assert.deepEqual(server.sent, []);
  });

  for (const [name, options, retryAfter, retry] of [
    ['rateLimitRetries: 0', { rateLimitRetries: 0 }, 1, true],
    ['retryAfter longer than the timeout', { timeout: 5 }, 6, true],
    ['retry: false', {}, 1, false],
  ]) {
    test(`rate limit without a repeat: ${name}`, async (t) => {
      const server = await startServer(t);
      const client = await makeClient(t, server, options);
      const waits = recordSleeps(client);
      server.failNext('status', 'rate_limited', 'x', { retryAfter });
      await assert.rejects(client.request('status', {}, { retry }), (error) => {
        return error instanceof ApiError && error.code === 'rate_limited';
      });
      assert.deepEqual(waits, []);
    });
  }

  test('send is not repeated when rate limited', async (t) => {
    const { server, client } = await pair(t);
    const waits = recordSleeps(client);
    server.failNext('messages.send', 'rate_limited', 'x', { retryAfter: 1 });
    const result = await client.send(BEN, 'Only once');
    assert.deepEqual(waits, [1]);
    const requests = server.requests.filter((r) => r.action === 'messages.send');
    assert.deepEqual(requests.map((r) => r.requestId), [result.requestId, result.requestId]);
    assert.deepEqual(server.sent.map((m) => m.body), ['Only once']);
  });

  test('stays within the limit of the server', async (t) => {
    const { server, client } = await pair(t, { requestsPerSecond: 5 });
    const started = performance.now();
    for (let index = 0; index < 9; index += 1) {
      assert.ok((await client.ping()).time > 0);
    }
    // The login and 4 requests fit into the first second, 5 into the next.
    assert.ok(server.refusedCount >= 1);
    assert.ok(performance.now() - started >= 900);
    assert.equal(count(server, 'ping'), 9);
    assert.equal(client.connectionCount, 1);
  });
});

// ----------------------------------------------------------------------
// The fake server itself
// ----------------------------------------------------------------------

describe('fake server', needTls, () => {
  test('starts with the data of the tests', async (t) => {
    const server = new FakeSmsDock({ tls: tlsFiles });
    fill(server);
    t.after(() => server.stop());
    await server.start();
    assert.equal(server.messages.length, 29);
    assert.match(server.url, /^wss:\/\/127\.0\.0\.1:\d+\/ws$/);
  });
});
