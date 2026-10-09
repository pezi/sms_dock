/**
 * Helpers of the tests: a fake SMS Dock server and clients connected to it.
 *
 * The tests talk to the fake server over real TLS with a throwaway
 * certificate made by the `openssl` command. Without `openssl` they are
 * skipped.
 */

import { spawn } from 'node:child_process';
import { mkdtempSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';

import { SmsDockClient } from '../src/index.js';
import { FakeSmsDock, makeCertificate } from './fake_server.js';

export const PASSWORD = 'secret123';
export const BASE_TIME = 1790672531000;
export const ANNA = '+4369912345678';
export const BEN = '+4369087654321';
export const FINGERPRINT = Array(32).fill('AB').join(':');
export const URL = 'wss://192.168.1.50:9443/ws';
export const CLI = fileURLToPath(new globalThis.URL('../bin/smsdock.js', import.meta.url));

// No test sees the password or fingerprint of the developer.
delete process.env.SMSDOCK_PASSWORD;
delete process.env.SMSDOCK_FINGERPRINT;

const directory = mkdtempSync(join(tmpdir(), 'smsdock-test-'));
process.on('exit', () => rmSync(directory, { recursive: true, force: true }));

/** Certificate of the fake server, or `null` without openssl. */
export const tlsFiles = makeCertificate(directory, 'server');

/** A second certificate that the client does not pin. */
export const otherTlsFiles = makeCertificate(directory, 'other');

/** Pass as the options of `describe` or `test`: skips without openssl. */
export const needTls =
  tlsFiles === null || otherTlsFiles === null
    ? { skip: 'needs the openssl command to create a throwaway certificate' }
    : {};

/** Stores 29 messages in three conversations. */
export function fill(server) {
  server.addContact('Anna Example', ANNA);
  server.addContact('Ben Example', BEN, 'Work');
  for (let index = 0; index < 25; index += 1) {
    server.addMessage(ANNA, `Anna ${index}`, {
      direction: index % 2 === 0 ? 'in' : 'out',
      timestamp: BASE_TIME + index * 1000,
      read: index < 22,
    });
  }
  server.addMessage(BEN, 'Your invoice is attached', {
    direction: 'out',
    timestamp: BASE_TIME - 5000,
    sim: 2,
  });
  server.addMessage(BEN, 'Thanks for the invoice', { timestamp: BASE_TIME - 4000, sim: 2 });
  server.addMessage(BEN, 'See you', { timestamp: BASE_TIME - 3000, sim: 2 });
  server.addMessage('A1', 'Your bill is ready', { timestamp: BASE_TIME - 9000, read: false });
}

/**
 * Starts a fake server with the data of `fill()`; it stops with the test.
 *
 * @param {import('node:test').TestContext} t
 * @param {object} [options]
 * @returns {Promise<FakeSmsDock>}
 */
export async function startServer(t, options = {}) {
  const server = new FakeSmsDock({ tls: tlsFiles, password: PASSWORD, ...options });
  fill(server);
  t.after(() => server.stop());
  return server.start();
}

/**
 * Makes a client for a server; it is closed with the test.
 *
 * @param {import('node:test').TestContext} t
 * @param {FakeSmsDock} server
 * @param {object} [options] Options of the client, and `connect: false`.
 * @returns {Promise<SmsDockClient>}
 */
export async function makeClient(t, server, options = {}) {
  const { connect = true, ...rest } = options;
  const client = new SmsDockClient(server.url, {
    password: PASSWORD,
    fingerprint: server.tls.fingerprint,
    reconnectDelay: 0.01,
    timeout: 5,
    ...rest,
  });
  t.after(() => client.close());
  if (connect) {
    await client.connect();
  }
  return client;
}

/** A server and a client that is connected and logged in. */
export async function pair(t, serverOptions = {}, clientOptions = {}) {
  const server = await startServer(t, serverOptions);
  const client = await makeClient(t, server, clientOptions);
  return { server, client };
}

export function sleep(seconds) {
  return new Promise((resolve) => {
    setTimeout(resolve, seconds * 1000);
  });
}

/** Polls until the condition holds; returns whether it did. */
export async function waitUntil(condition, timeout = 3) {
  const deadline = performance.now() + timeout * 1000;
  while (performance.now() < deadline) {
    if (condition()) {
      return true;
    }
    await sleep(0.01);
  }
  return Boolean(condition());
}

/** Reads an async iterator to its end. */
export async function collect(iterator) {
  const items = [];
  for await (const item of iterator) {
    items.push(item);
  }
  return items;
}

/** Returns the newest request with this action. */
export function lastRequest(server, action) {
  return server.requests.filter((request) => request.action === action).pop();
}

/** Returns an `sms.received` event as the server would send it. */
export function eventFrame(sequence, body = 'Hi') {
  return {
    event: 'sms.received',
    seq: sequence,
    data: {
      message: {
        id: 9000 + sequence,
        conversationId: 1,
        address: ANNA,
        contact: null,
        direction: 'in',
        body,
        timestamp: BASE_TIME,
        sentTimestamp: BASE_TIME,
        read: false,
        status: 'received',
        sim: 1,
        requestId: null,
      },
    },
  };
}

/**
 * Runs the command line in a process of its own.
 *
 * @param {string[]} args
 * @param {{env?: Record<string, string | undefined>, input?: string,
 *   started?: (child: import('node:child_process').ChildProcess) => void}}
 *   [options]
 * @returns {Promise<{code: number | null, signal: string | null, out: string,
 *   err: string}>}
 */
export function runCli(args, options = {}) {
  const env = { ...process.env, SMSDOCK_PASSWORD: PASSWORD, ...options.env };
  for (const [name, value] of Object.entries(env)) {
    if (value === undefined) {
      delete env[name];
    }
  }
  return new Promise((resolve, reject) => {
    const child = spawn(process.execPath, [CLI, ...args], {
      env,
      stdio: ['pipe', 'pipe', 'pipe'],
    });
    const timer = setTimeout(() => child.kill('SIGKILL'), 30_000);
    let out = '';
    let err = '';
    child.stdout.setEncoding('utf8').on('data', (chunk) => {
      out += chunk;
    });
    child.stderr.setEncoding('utf8').on('data', (chunk) => {
      err += chunk;
    });
    child.on('error', reject);
    child.on('close', (code, signal) => {
      clearTimeout(timer);
      resolve({ code, signal, out, err });
    });
    child.stdin.on('error', () => {});
    child.stdin.end(options.input ?? '');
    options.started?.(child);
  });
}

/** Returns a function that adds URL and fingerprint to a command. */
export function cliArguments(server) {
  return (command, ...options) => [
    command,
    server.url,
    '--fingerprint',
    server.tls.fingerprint,
    '--timeout',
    '5',
    ...options,
  ];
}
