/** Tests of the programs in `examples/`, run against the fake server. */

import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { describe, test } from 'node:test';
import { fileURLToPath } from 'node:url';

import { ReplyGuard, answerFor } from '../examples/auto_reply.mjs';
import { ANNA, BEN, PASSWORD, needTls, startServer, waitUntil } from './helpers.js';

/** Runs an example; `started` gets the process while it runs. */
function runExample(name, server, args = [], started = null) {
  const script = fileURLToPath(new URL(`../examples/${name}`, import.meta.url));
  const url = server ? [server.url] : [];
  return new Promise((resolve, reject) => {
    const child = spawn(process.execPath, [script, ...url, ...args], {
      env: {
        ...process.env,
        SMSDOCK_PASSWORD: PASSWORD,
        SMSDOCK_FINGERPRINT: server ? server.tls.fingerprint : '',
      },
      stdio: ['ignore', 'pipe', 'pipe'],
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
    child.on('close', (code) => {
      clearTimeout(timer);
      resolve({ code, out, err });
    });
    started?.(child, () => out);
  });
}

describe('reply guard', () => {
  test('answers a number once in the cooldown', () => {
    const guard = new ReplyGuard(600, 20);
    assert.equal(guard.refusal('+43 699 1234567', 0), null);
    assert.equal(guard.refusal('+436991234567', 599), 'answered a short time ago');
    assert.equal(guard.refusal('+436991234567', 600), null);
  });

  test('ignores names and short codes', () => {
    const guard = new ReplyGuard(600, 20);
    for (const sender of ['A1', 'Post', '8810', 'MyBank', '+43 (0) x']) {
      assert.equal(guard.refusal(sender, 0), 'not a phone number', sender);
    }
  });

  test('stops after the answers of an hour', () => {
    const guard = new ReplyGuard(600, 3);
    for (let index = 0; index < 3; index += 1) {
      assert.equal(guard.refusal(`+4369912345${index}0`, index), null);
    }
    assert.equal(guard.refusal('+436991234599', 10), 'too many answers in the last hour');
    assert.equal(guard.refusal('+436991234599', 3601), null);
  });

  test('answers', () => {
    assert.equal(answerFor(' status\n'), 'All systems running.');
    assert.equal(answerFor('Hello'), 'Commands: STATUS');
  });
});

describe('examples', needTls, () => {
  test('usage', async () => {
    const names = ['list_messages.mjs', 'send_sms.mjs', 'listen_incoming.mjs', 'auto_reply.mjs'];
    for (const name of names) {
      const { code, out } = await runExample(name, null);
      assert.equal(code, 2, name);
      assert.match(out, /^Usage: node /);
    }
  });

  test('list_messages', async (t) => {
    const server = await startServer(t);
    const { code, out, err } = await runExample('list_messages.mjs', server);
    assert.equal(code, 0, err);
    const lines = out.trimEnd().split('\n');
    assert.equal(lines.length, 10);
    assert.match(lines[0], /^\d{4}-\d\d-\d\d \d\d:\d\d {2}Anna Example: Anna 24$/);
  });

  test('send_sms', async (t) => {
    const server = await startServer(t);
    let { code, out, err } = await runExample('send_sms.mjs', server, [BEN, 'Hello']);
    assert.equal(code, 0, err);
    assert.match(out, new RegExp(`^1 part\\(s\\) for \\${BEN}\\nSent, message id \\d+\\n$`));
    assert.deepEqual(server.sent.map((m) => m.body), ['Hello']);

    ({ code, out } = await runExample('send_sms.mjs', server, ['abc', 'Hello']));
    assert.equal(code, 1);
    assert.match(out, /^Not sent: invalid_number: /);

    server.failNumbers.add('+43123456');
    ({ code, out } = await runExample('send_sms.mjs', server, ['+43123456', 'Hello']));
    assert.equal(code, 1);
    assert.match(out, /Failed, error code 1\n$/);
  });

  test('examples report a wrong fingerprint', async (t) => {
    const server = await startServer(t);
    const wrong = { url: server.url, tls: { fingerprint: 'AB'.repeat(32) } };
    const { code, err } = await runExample('list_messages.mjs', wrong);
    assert.equal(code, 1);
    assert.match(err, /^list_messages: The certificate of the server does not match/);
  });

  test('listen_incoming reads what it missed', async (t) => {
    const server = await startServer(t);
    const { code, out, err } = await runExample(
      'listen_incoming.mjs',
      server,
      [],
      async (child, output) => {
        await waitUntil(() => server.actions().includes('subscribe'), 10);
        server.receiveSms(ANNA, 'live');
        await waitUntil(() => output().includes('live'), 10);
        server.dropConnections();
        await waitUntil(() => server.clientCount === 0);
        server.receiveSms('A1', 'while away');
        await waitUntil(() => output().includes('while away'), 10);
        server.receiveSms(BEN, 'live again');
        await waitUntil(() => output().includes('live again'), 10);
        child.kill('SIGINT');
      },
    );
    assert.equal(code, 0, err);
    const lines = out.trimEnd().split('\n');
    assert.equal(lines[0], 'Waiting for SMS. Press Ctrl+C to stop.');
    assert.match(lines[1], /^\d\d:\d\d:\d\d {2}Anna Example: live$/);
    assert.match(lines[2], /^\d\d:\d\d:\d\d {2}A1: while away$/);
    assert.match(lines[3], /^\d\d:\d\d:\d\d {2}Ben Example: live again$/);
    assert.equal(lines.length, 4);
  });

  test('auto_reply answers once', async (t) => {
    const server = await startServer(t);
    const { code, out, err } = await runExample(
      'auto_reply.mjs',
      server,
      [],
      async (child, output) => {
        await waitUntil(() => server.actions().includes('subscribe'), 10);
        server.receiveSms(ANNA, 'STATUS');
        server.receiveSms(ANNA, 'STATUS');
        server.receiveSms('A1', 'Your bill');
        server.receiveSms(BEN, 'Hello?', 2);
        await waitUntil(() => output().split('\n').length >= 6, 10);
        child.kill('SIGINT');
      },
    );
    assert.equal(code, 0, err);
    assert.deepEqual(
      server.sent.map((m) => [m.to, m.body, m.sim]),
      [
        [ANNA, 'All systems running.', 1],
        [BEN, 'Commands: STATUS', 2],
      ],
    );
    assert.match(out, new RegExp(`\\${ANNA}: no answer \\(answered a short time ago\\)`));
    assert.match(out, /A1: no answer \(not a phone number\)/);
  });
});
