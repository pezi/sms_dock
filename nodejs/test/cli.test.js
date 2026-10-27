/** Tests of the command line `smsdock`, run in a process of its own. */

import assert from 'node:assert/strict';
import { describe, test } from 'node:test';

import {
  describe as describeEvent,
  hostAndPort,
  parseCommandLine,
  UsageError,
} from '../src/cli.js';
import {
  ANNA,
  BEN,
  FINGERPRINT,
  PASSWORD,
  URL,
  cliArguments,
  needTls,
  otherTlsFiles,
  runCli,
  startServer,
  tlsFiles,
  waitUntil,
} from './helpers.js';

const WARNING = 'smsdock: WARNING:';

// ----------------------------------------------------------------------
// Arguments
// ----------------------------------------------------------------------

describe('arguments', () => {
  test('parser reads all flags', () => {
    const args = parseCommandLine([
      'messages', URL, '--fingerprint', FINGERPRINT, '--address', ANNA,
      '--direction', 'in', '--search', 'invoice', '--limit', '10',
      '--unread', '--sim', '2', '--json', '--timeout', '7',
      '--since', '1790672531000', '--until', '2026-09-29T08:00',
      '--conversation', '179', '--reconnect-attempts', '2',
    ]);
    assert.equal(args.command, 'messages');
    assert.equal(args.url, URL);
    assert.equal(args.fingerprint, FINGERPRINT);
    assert.equal(args.address, ANNA);
    assert.equal(args.direction, 'in');
    assert.equal(args.search, 'invoice');
    assert.equal(args.limit, 10);
    assert.equal(args.unread, true);
    assert.equal(args.sim, 2);
    assert.equal(args.json, true);
    assert.equal(args.timeout, 7);
    assert.equal(args.since, 1790672531000);
    assert.equal(args.until, new Date(2026, 8, 29, 8, 0).getTime());
    assert.equal(args.conversation, 179);
    assert.equal(args.reconnectAttempts, 2);
    assert.equal(args.insecure, false);
  });

  test('parser defaults', () => {
    const args = parseCommandLine(['messages', URL], {});
    assert.equal(args.direction, 'all');
    assert.equal(args.limit, 50);
    assert.equal(args.timeout, 30);
    assert.equal(args.reconnectAttempts, 5);
    assert.ok(!args.json && !args.unread && !args.insecure);
    assert.equal(args.fingerprint, null);
    assert.equal(args.passwordEnv, 'SMSDOCK_PASSWORD');
    const listen = parseCommandLine(['listen', URL], {});
    assert.deepEqual(listen.events, ['sms.received']);
    assert.equal(listen.address, null);
    assert.equal(listen.count, null);
    const send = parseCommandLine(['send', URL, '--to', ANNA], {});
    assert.ok(send.text === null && send.wait === false && send.waitTimeout === 60);
    assert.equal(parseCommandLine(['fingerprint', 'phone.local'], {}).timeout, 10);
  });

  test('forms of options', () => {
    const env = { SMSDOCK_FINGERPRINT: FINGERPRINT };
    assert.equal(parseCommandLine(['status', URL], env).fingerprint, FINGERPRINT);
    // The options may stand before the URL, and -1 is a value.
    const args = parseCommandLine(
      ['listen', '--reconnect-attempts', '-1', '--count=3', URL, '--events', 'sms.sent', '*'],
      env,
    );
    assert.equal(args.reconnectAttempts, -1);
    assert.equal(args.count, 3);
    assert.equal(args.url, URL);
    assert.deepEqual(args.events, ['sms.sent', '*']);
    const dates = parseCommandLine(
      ['messages', URL, '--since', '2026-09-29', '--until', '2026-09-29T08:00:30+02:00'],
      env,
    );
    assert.equal(dates.since, new Date(2026, 8, 29).getTime());
    assert.equal(dates.until, Date.UTC(2026, 8, 29, 6, 0, 30));
  });

  for (const argv of [
    [],
    ['unknown'],
    ['status'],
    ['status', URL, '--password', 'secret123'],
    ['status', URL, 'extra'],
    ['status', URL, '--timeout'],
    ['status', URL, '--timeout', 'soon'],
    ['messages', URL, '--direction', 'sideways'],
    ['messages', URL, '--limit', '0'],
    ['messages', URL, '--limit', 'many'],
    ['messages', URL, '--since', 'yesterday'],
    ['messages', URL, '--since', '2026-13-01'],
    ['messages', URL, '--dir', 'in'],
    ['send', URL, '--text', 'no recipient'],
    ['delete', URL],
    ['delete', URL, '--message', '1', '--conversation', '2'],
    ['mark-read', URL],
    ['mark-read', URL, '--conversation', 'first'],
    ['listen', URL, '--events', 'sms.unknown'],
    ['fingerprint'],
  ]) {
    test(`usage error: smsdock ${argv.join(' ')}`, async () => {
      const { code, out, err } = await runCli(argv);
      assert.equal(code, 2);
      assert.equal(out, '');
      assert.match(err, /^usage: smsdock/);
      assert.match(err, /\nsmsdock[a-z -]*: error: /);
    });
  }

  test('help', async () => {
    let { code, out, err } = await runCli(['--help']);
    assert.equal(code, 0);
    assert.equal(err, '');
    assert.match(out, /^usage: smsdock/);
    for (const command of [
      'fingerprint', 'status', 'conversations', 'messages', 'send', 'delete',
      'mark-read', 'contacts', 'listen',
    ]) {
      assert.ok(out.includes(command), command);
    }
    assert.ok(out.includes('SMSDOCK_PASSWORD'));
    ({ code, out } = await runCli(['send', '--help']));
    assert.equal(code, 0);
    assert.ok(out.includes('--request-id ID'));
    assert.ok(out.includes('--fingerprint HEX'));
    assert.ok(!out.includes('--password '));
    assert.ok(out.split('\n').every((line) => line.length <= 80));
  });

  test('the password is no argument', async () => {
    const { code, err } = await runCli([
      'status', URL, '--fingerprint', FINGERPRINT, '--password', PASSWORD,
    ]);
    assert.equal(code, 2);
    assert.match(err, /unrecognized arguments: --password/);
  });

  test('refuses other URLs', async () => {
    for (const url of ['ws://192.168.1.50:9443/ws', 'https://192.168.1.50:8443/', '192.168.1.50']) {
      const { code, out, err } = await runCli(['status', url, '--fingerprint', FINGERPRINT]);
      assert.equal(code, 2);
      assert.match(err, /wss:\/\//);
      assert.equal(out, '');
    }
  });

  test('needs a fingerprint, and a well-formed one', async () => {
    let { code, err } = await runCli(['status', URL]);
    assert.equal(code, 2);
    assert.ok(err.includes('--fingerprint') && err.includes('SMSDOCK_FINGERPRINT'));
    ({ code, err } = await runCli(['status', URL, '--fingerprint', 'AB:CD']));
    assert.equal(code, 2);
    assert.match(err, /SHA-256/);
    ({ code, err } = await runCli([
      'status', URL, '--fingerprint', FINGERPRINT, '--reconnect-attempts', '-2',
    ]));
    assert.equal(code, 2);
    assert.match(err, /--reconnect-attempts/);
  });

  test('needs a password', async () => {
    const { code, err } = await runCli(['status', URL, '--fingerprint', FINGERPRINT], {
      env: { SMSDOCK_PASSWORD: undefined },
    });
    assert.equal(code, 2);
    assert.equal(err, 'smsdock: No password. Set the environment variable SMSDOCK_PASSWORD.\n');
  });

  test('host and port', () => {
    for (const [text, expected] of [
      ['192.168.1.50', ['192.168.1.50', 9443]],
      ['192.168.1.50:9555', ['192.168.1.50', 9555]],
      ['phone.local:9443', ['phone.local', 9443]],
      ['wss://192.168.1.50:9555/ws', ['192.168.1.50', 9555]],
      ['wss://192.168.1.50/ws', ['192.168.1.50', 9443]],
      ['wss://192.168.1.50:443/ws', ['192.168.1.50', 443]],
      ['[fe80::1]:9555', ['fe80::1', 9555]],
      ['fe80::1', ['fe80::1', 9443]],
    ]) {
      assert.deepEqual(hostAndPort(text), expected, text);
    }
    for (const text of ['', ':9443', '192.168.1.50:port', 'host:99999']) {
      assert.throws(() => hostAndPort(text), UsageError, text);
    }
  });

  test('describes events on one line', () => {
    const line = describeEvent({
      event: 'sms.failed',
      seq: 3,
      data: { requestId: 'r1', id: null, to: BEN, errorCode: 1, message: { a: 1 }, list: [] },
    });
    assert.match(line, /^\d{4}-\d\d-\d\d \d\d:\d\d:\d\d {2}sms\.failed /);
    assert.ok(line.endsWith(`sms.failed requestId=r1 id=None to=${BEN} errorCode=1`));
    assert.match(describeEvent({ event: 'conversations.changed', data: {} }), /changed$/);
  });
});

// ----------------------------------------------------------------------
// Commands
// ----------------------------------------------------------------------

describe('commands', needTls, () => {
  test('fingerprint', async (t) => {
    const server = await startServer(t);
    let { code, out, err } = await runCli(['fingerprint', `127.0.0.1:${server.port}`]);
    assert.equal(code, 0);
    assert.equal(out, `${tlsFiles.fingerprint}\n`);
    assert.match(err, /^Compare this fingerprint/);
    ({ code, out } = await runCli(['fingerprint', server.url]));
    assert.deepEqual([code, out], [0, `${tlsFiles.fingerprint}\n`]);
    assert.deepEqual(server.requests, []);
    assert.equal(server.httpRequestCount, 0);

    await server.stop();
    ({ code, out, err } = await runCli([
      'fingerprint', `127.0.0.1:${server.port}`, '--timeout', '2',
    ]));
    assert.equal(code, 3);
    assert.equal(out, '');
    assert.match(err, /^smsdock: /);
  });

  test('status', async (t) => {
    const server = await startServer(t);
    const args = cliArguments(server);
    let { code, out, err } = await runCli(args('status'));
    assert.equal(code, 0);
    assert.equal(err, '');
    const lines = out.split('\n');
    assert.deepEqual(lines.slice(0, 6), [
      'App:             SMS Dock 1.0.0 (protocol 1)',
      'Default SMS app: no',
      'Delete mode:     hidden',
      'Licence:         not enforced',
      'Permissions:     all granted',
      '',
    ]);
    assert.deepEqual(lines.slice(6), [
      'SIM  SLOT  NAME  NUMBER          DEFAULT',
      '1    0     Play                  yes',
      `2    1     Work  ${BEN}  no`,
      '',
    ]);
    assert.ok(!(out + err).includes(PASSWORD));

    ({ code, out } = await runCli(args('status', '--json')));
    assert.equal(code, 0);
    const status = JSON.parse(out);
    assert.equal(status.app.protocolVersion, 1);
    assert.equal(status.sims.length, 2);
    assert.match(out, /^\{\n {2}"app": \{\n {4}"name": "SMS Dock",/);
  });

  test('conversations', async (t) => {
    const server = await startServer(t);
    const args = cliArguments(server);
    let { code, out } = await runCli(args('conversations'));
    assert.equal(code, 0);
    const lines = out.trimEnd().split('\n');
    assert.deepEqual(lines[0].split(/\s+/), [
      'ID', 'TIME', 'NUMBER', 'NAME', 'MSGS', 'UNREAD', 'DIR', 'TEXT',
    ]);
    assert.equal(lines.length, 4);
    assert.ok(lines[1].includes(ANNA) && lines[1].includes('Anna Example'));
    assert.ok(lines[1].endsWith('Anna 24'));
    assert.match(
      lines[1],
      /^170 {2}\d{4}-\d\d-\d\d \d\d:\d\d {2}\+4369912345678 {2}Anna Example {2}25 /,
    );
    assert.ok(lines[3].includes('A1'));

    ({ code, out } = await runCli(
      args('conversations', '--json', '--unread', '--search', 'bill', '--limit', '5'),
    ));
    assert.equal(code, 0);
    assert.deepEqual(JSON.parse(out).map((c) => c.address), ['A1']);
    ({ out } = await runCli(args('conversations', '--sim', '2', '--json')));
    assert.deepEqual(JSON.parse(out).map((c) => c.address), [BEN]);
  });

  test('messages', async (t) => {
    const server = await startServer(t);
    const args = cliArguments(server);
    let { code, out } = await runCli(args('messages', '--direction', 'in', '--limit', '10'));
    assert.equal(code, 0);
    const lines = out.trimEnd().split('\n');
    assert.deepEqual(lines[0].split(/\s+/), [
      'ID', 'TIME', 'DIR', 'NUMBER', 'NAME', 'STATUS', 'TEXT',
    ]);
    assert.equal(lines.length, 11);
    assert.ok(lines.slice(1).every((line) => line.includes(' in ')));
    assert.ok(lines[1].endsWith('received  Anna 24'));
    assert.ok(lines[10].endsWith('received  Anna 6'));

    ({ code, out } = await runCli(
      args('messages', '--json', '--address', BEN, '--search', 'invoice',
        '--direction', 'out', '--sim', '2'),
    ));
    assert.equal(code, 0);
    assert.deepEqual(JSON.parse(out).map((m) => m.body), ['Your invoice is attached']);
    const request = server.requests.filter((r) => r.action === 'messages.list').pop();
    assert.deepEqual(
      [request.address, request.q, request.direction, request.sim],
      [BEN, 'invoice', 'out', 2],
    );
    ({ out } = await runCli(args('messages', '--json', '--unread')));
    assert.equal(JSON.parse(out).length, 4);
    ({ out } = await runCli(args('messages', '--json', '--since', '1790672553000')));
    assert.deepEqual(JSON.parse(out).map((m) => m.body), ['Anna 24', 'Anna 23', 'Anna 22']);

    ({ code, out } = await runCli(args('messages', '--search', 'no such text')));
    assert.deepEqual([code, out], [0, 'Nothing found.\n']);
    ({ code, out } = await runCli(args('messages', '--search', 'no such text', '--json')));
    assert.deepEqual([code, out], [0, '[]\n']);
  });

  test('messages: more than one page, long text', async (t) => {
    const server = await startServer(t);
    const args = cliArguments(server);
    for (let index = 0; index < 230; index += 1) {
      server.addMessage(ANNA, `More ${index}`);
    }
    let { code, out } = await runCli(args('messages', '--json', '--limit', '220'));
    assert.equal(code, 0);
    assert.equal(JSON.parse(out).length, 220);
    const limits = server.requests
      .filter((r) => r.action === 'messages.list')
      .map((r) => r.limit);
    assert.deepEqual(limits, [200, 20]);

    server.addMessage(ANNA, `First line\nsecond line ${'x'.repeat(100)}`, {
      timestamp: Date.now() + 1000,
    });
    ({ code, out } = await runCli(args('messages', '--limit', '1')));
    const line = out.split('\n')[1];
    assert.ok(line.includes('First line second line xxx'));
    assert.ok(line.endsWith('x…'));
    assert.equal([...line.slice(line.indexOf('First'))].length, 60);
    ({ out } = await runCli(args('messages', '--limit', '1', '--json')));
    assert.ok(JSON.parse(out)[0].body.startsWith('First line\nsecond line'));
  });

  test('send', async (t) => {
    const server = await startServer(t);
    const args = cliArguments(server);
    let { code, out } = await runCli(args('send', '--to', BEN, '--text', 'All systems running.'));
    assert.equal(code, 0);
    const requestId = server.sent[0].requestId;
    assert.equal(out, `Queued: ${BEN}, 1 part(s), requestId ${requestId}\n`);
    assert.equal(server.sent[0].body, 'All systems running.');

    // Without --text the text comes from standard input.
    ({ code, out } = await runCli(
      args('send', '--to', BEN, '--sim', '2', '--request-id', 'cron-1', '--json'),
      { input: 'Line one\nLine two\n' },
    ));
    assert.equal(code, 0);
    assert.deepEqual(JSON.parse(out), {
      requestId: 'cron-1', to: BEN, parts: 1, status: 'queued',
    });
    assert.deepEqual(server.sent[1], {
      requestId: 'cron-1', to: BEN, body: 'Line one\nLine two', sim: 2,
    });

    let err;
    ({ code, err } = await runCli(args('send', '--to', BEN), { input: '' }));
    assert.equal(code, 2);
    assert.equal(err, 'smsdock: The text is empty.\n');
    assert.equal(server.sent.length, 2);
  });

  test('send-group', async (t) => {
    const server = await startServer(t);
    const args = cliArguments(server);
    let { code, out } = await runCli(
      args(
        'send-group', '--to', BEN, '--to', ANNA, '--text', 'Hello all',
        '--request-id', 'news-1', '--sim', '2',
      ),
    );
    assert.equal(code, 0);
    assert.equal(
      out,
      'Group news-1: 2 recipient(s), 1 part(s) each\n' +
        `  Queued: ${BEN}, requestId news-1.1\n` +
        `  Queued: ${ANNA}, requestId news-1.2\n`,
    );
    assert.deepEqual(
      server.sent.map((m) => [m.to, m.body, m.sim]),
      [[BEN, 'Hello all', 2], [ANNA, 'Hello all', 2]],
    );

    ({ code, out } = await runCli(
      args('send-group', '--to', BEN, '--request-id', 'news-2', '--json'),
      { input: 'From stdin\n' },
    ));
    assert.equal(code, 0);
    assert.deepEqual(JSON.parse(out), {
      requestId: 'news-2', to: [BEN], parts: 1, status: 'queued',
      messages: [{ requestId: 'news-2.1', to: BEN, parts: 1, status: 'queued' }],
    });

    server.failNumbers.add(ANNA);
    let err;
    ({ code, out, err } = await runCli(
      args('send-group', '--to', BEN, '--to', ANNA, '--text', 'Hello', '--request-id', 'news-3', '--wait'),
    ));
    assert.equal(code, 1);
    assert.equal(
      out,
      'Group news-3: 2 recipient(s), 1 part(s) each\n' +
        `  Sent: ${BEN}, requestId news-3.1\n` +
        `  Failed: ${ANNA}, error code 1, requestId news-3.2\n`,
    );
    assert.equal(err, 'smsdock: 1 of 2 message(s) failed\n');
  });

  test('send and wait', async (t) => {
    const server = await startServer(t);
    const args = cliArguments(server);
    let { code, out, err } = await runCli(args('send', '--to', BEN, '--text', 'Hello', '--wait'));
    assert.equal(code, 0);
    assert.match(out, new RegExp(`^Sent: \\${BEN}, 1 part\\(s\\), requestId [0-9a-f]{32}\\n$`));

    ({ code, out } = await runCli(
      args('send', '--to', BEN, '--text', 'Hello', '--wait', '--json'),
    ));
    assert.equal(code, 0);
    const entry = JSON.parse(out);
    assert.ok(entry.status === 'sent' && entry.partsSent === 1);

    server.failNumbers.add('+43123456');
    ({ code, out, err } = await runCli(
      args('send', '--to', '+43123456', '--text', 'Hello', '--wait', '--request-id', 'f-1'),
    ));
    assert.deepEqual([code, out], [1, '']);
    assert.equal(err, 'smsdock: Sending to +43123456 failed (error code 1, requestId f-1)\n');
    ({ code, out } = await runCli(
      args('send', '--to', '+43123456', '--text', 'Hello', '--wait', '--json'),
    ));
    assert.equal(code, 1);
    assert.equal(JSON.parse(out).status, 'failed');
  });

  test('send again tells the old outcome', async (t) => {
    const server = await startServer(t);
    const args = cliArguments(server);
    server.failNumbers.add('+43123456');
    const command = args('send', '--to', '+43123456', '--text', 'Hello', '--request-id', 'job-1');
    let { code, out, err } = await runCli(command);
    assert.deepEqual([code, out], [0, 'Queued: +43123456, 1 part(s), requestId job-1\n']);
    assert.ok(await waitUntil(() => server.outbox.get('job-1').status === 'failed'));
    server.failNumbers.clear();
    // The repeat sends nothing and reports the failure of the first attempt.
    ({ code, out, err } = await runCli(command));
    assert.deepEqual([code, out], [1, '']);
    assert.equal(err, 'smsdock: Sending to +43123456 failed (requestId job-1)\n');
    assert.equal(server.sent.length, 1);

    const ok = args('send', '--to', BEN, '--text', 'Hello', '--request-id', 'job-2', '--wait');
    assert.equal((await runCli(ok)).code, 0);
    ({ code, out } = await runCli(ok.slice(0, -1)));
    assert.deepEqual([code, out], [0, `Sent: ${BEN}, 1 part(s), requestId job-2\n`]);
    assert.equal(server.sent.length, 2);
  });

  test('send and wait times out', async (t) => {
    const server = await startServer(t, { sendDelay: 30 });
    const { code, err } = await runCli(
      cliArguments(server)(
        'send', '--to', BEN, '--text', 'Hello', '--wait', '--wait-timeout', '0.3',
      ),
    );
    assert.equal(code, 3);
    assert.match(err, /^smsdock: No result for the message/);
  });

  test('delete', async (t) => {
    const server = await startServer(t);
    const args = cliArguments(server);
    const newest = [...server.messages].sort((a, b) => b.timestamp - a.timestamp)[0];
    let { code, out } = await runCli(args('delete', '--message', String(newest.id)));
    assert.equal(code, 0);
    assert.equal(out, 'Deleted 1 message(s): hidden in SMS Dock, still on the phone\n');
    assert.ok(!server.messages.includes(newest));

    server.deleteMode = 'provider';
    const conversation = server.messages[0].conversationId;
    ({ code, out } = await runCli(args('delete', '--conversation', String(conversation))));
    assert.equal(code, 0);
    assert.equal(out, 'Deleted 24 message(s): removed from the phone\n');
    ({ out } = await runCli(
      args('delete', '--conversation', String(server.messages[0].conversationId), '--json'),
    ));
    assert.equal(out, '{\n  "deleted": 3,\n  "mode": "provider"\n}\n');
  });

  test('mark-read', async (t) => {
    const server = await startServer(t);
    const args = cliArguments(server);
    const conversation = String(server.messages[0].conversationId);
    let { code, out, err } = await runCli(args('mark-read', '--conversation', conversation));
    assert.deepEqual([code, out], [1, '']);
    assert.match(err, /^smsdock: not_default_app: /);

    server.deleteMode = 'provider';
    ({ code, out } = await runCli(args('mark-read', '--conversation', conversation)));
    assert.deepEqual([code, out], [0, `Marked conversation ${conversation} as read\n`]);
    assert.ok(
      server.messages
        .filter((m) => String(m.conversationId) === conversation)
        .every((m) => m.read),
    );
    ({ code, out } = await runCli(args('mark-read', '--conversation', conversation, '--json')));
    assert.deepEqual([code, JSON.parse(out)], [0, { ok: true }]);
  });

  test('contacts', async (t) => {
    const server = await startServer(t);
    const args = cliArguments(server);
    let { code, out } = await runCli(args('contacts'));
    assert.equal(code, 0);
    assert.equal(
      out,
      'NAME          NUMBER          LABEL\n' +
        `Anna Example  ${ANNA}  Mobile\n` +
        `Ben Example   ${BEN}  Work\n`,
    );
    ({ code, out } = await runCli(args('contacts', '--search', 'ben', '--json')));
    assert.deepEqual(JSON.parse(out), [
      { name: 'Ben Example', numbers: [{ number: BEN, label: 'Work' }] },
    ]);
  });
});

// ----------------------------------------------------------------------
// listen
// ----------------------------------------------------------------------

describe('listen', needTls, () => {
  /** Lets something happen as soon as a client has subscribed. */
  function afterSubscribe(server, action, requests = 2) {
    return waitUntil(() => server.requests.length >= requests, 10).then((ready) => {
      if (ready) {
        action();
      }
    });
  }

  test('prints incoming sms', async (t) => {
    const server = await startServer(t);
    afterSubscribe(server, () => {
      server.receiveSms(ANNA, 'STATUS');
      server.receiveSms('A1', 'Your bill');
    });
    const { code, out, err } = await runCli(cliArguments(server)('listen', '--count', '2'));
    assert.equal(code, 0);
    const lines = out.trimEnd().split('\n');
    assert.equal(lines.length, 2);
    assert.match(
      lines[0],
      /^\d{4}-\d\d-\d\d \d\d:\d\d:\d\d {2}\+4369912345678 \(Anna Example\): STATUS$/,
    );
    assert.ok(lines[1].endsWith('  A1: Your bill'));
    assert.equal(err, 'Listening for sms.received. Press Ctrl+C to stop.\n');
    const request = server.requests.find((r) => r.action === 'subscribe');
    assert.deepEqual(request.events, ['sms.received']);
    assert.ok(!('address' in request));
  });

  test('json with address', async (t) => {
    const server = await startServer(t);
    afterSubscribe(server, () => {
      server.receiveSms(ANNA, 'ignored');
      server.receiveSms(BEN, 'wanted');
    });
    const { code, out, err } = await runCli(
      cliArguments(server)('listen', '--json', '--address', BEN, '--count', '1'),
    );
    assert.equal(code, 0);
    assert.equal(err, '');
    assert.equal(out.trimEnd().split('\n').length, 1);
    const event = JSON.parse(out);
    assert.equal(event.event, 'sms.received');
    assert.equal(event.data.message.body, 'wanted');
    assert.match(out, /^\{"event": "sms\.received", "seq": 2, "data": \{"message": \{"id": \d+, /);
  });

  test('other events', async (t) => {
    const server = await startServer(t);
    afterSubscribe(server, () => {
      server.emit('sms.delivered', { requestId: 'r1', id: 7, conversationId: 171, to: BEN });
    });
    const { code, out, err } = await runCli(
      cliArguments(server)('listen', '--events', 'sms.delivered', 'sms.failed', '--count', '1'),
    );
    assert.equal(code, 0);
    assert.ok(
      out.trimEnd().endsWith(`sms.delivered requestId=r1 id=7 conversationId=171 to=${BEN}`),
    );
    assert.equal(err, 'Listening for sms.delivered, sms.failed. Press Ctrl+C to stop.\n');
  });

  test('reports a reconnect', async (t) => {
    const server = await startServer(t);
    (async () => {
      if (!(await waitUntil(() => server.requests.length >= 2, 10))) {
        return;
      }
      server.dropConnections();
      await waitUntil(() => server.clientCount === 0);
      server.receiveSms(ANNA, 'lost');
      server.receiveSms(ANNA, 'lost too');
      if (await waitUntil(() => server.requests.length >= 4, 10)) {
        server.restart();
      }
      if (await waitUntil(() => server.requests.length >= 6, 10)) {
        server.receiveSms(ANNA, 'received');
      }
    })();
    const { code, out, err } = await runCli(
      cliArguments(server)('listen', '--count', '1', '--reconnect-attempts', '-1'),
      { env: { SMSDOCK_PASSWORD: PASSWORD } },
    );
    assert.equal(code, 0);
    assert.ok(out.trimEnd().endsWith(': received'));
    assert.deepEqual(err.split('\n').slice(1), [
      'smsdock: reconnected; 2 event(s) of the phone were not received',
      'smsdock: reconnected; the app was restarted, events may be missing',
      '',
    ]);
  });

  test('stops with Ctrl+C', async (t) => {
    const server = await startServer(t);
    const { code, out } = await runCli(cliArguments(server)('listen'), {
      started: (child) => afterSubscribe(server, () => child.kill('SIGINT')),
    });
    assert.equal(code, 130);
    assert.equal(out, '');
    assert.ok(await waitUntil(() => server.clientCount === 0));
  });
});

// ----------------------------------------------------------------------
// Exit codes
// ----------------------------------------------------------------------

describe('exit codes', needTls, () => {
  test('1 for api errors', async (t) => {
    const server = await startServer(t);
    const args = cliArguments(server);
    let { code, out, err } = await runCli(args('delete', '--message', '1'));
    assert.deepEqual([code, out], [1, '']);
    assert.match(err, /^smsdock: not_found: /);

    ({ code, err } = await runCli(args('send', '--to', 'abc', '--text', 'x')));
    assert.equal(code, 1);
    assert.match(err, /invalid_number/);

    server.failNext('messages.send', 'quota_exceeded', 'Daily limit reached.');
    ({ code, err } = await runCli(args('send', '--to', BEN, '--text', 'x')));
    assert.equal(code, 1);
    assert.equal(err, 'smsdock: quota_exceeded: Daily limit reached.\n');
  });

  test('1 for a wrong password, which is not shown and not repeated', async (t) => {
    const server = await startServer(t);
    const { code, out, err } = await runCli(cliArguments(server)('status'), {
      env: { SMSDOCK_PASSWORD: 'wrong-password' },
    });
    assert.deepEqual([code, out], [1, '']);
    assert.equal(err, 'smsdock: invalid_password: Wrong password.\n');
    assert.equal(server.loginCount, 1);
  });

  test('another variable for the password', async (t) => {
    const server = await startServer(t);
    const { code } = await runCli(
      cliArguments(server)('status', '--password-env', 'OFFICE_PHONE'),
      { env: { SMSDOCK_PASSWORD: 'wrong-password', OFFICE_PHONE: PASSWORD } },
    );
    assert.equal(code, 0);
  });

  test('1 without licence', async (t) => {
    const server = await startServer(t);
    server.licenceEnforced = true;
    const { code, err } = await runCli(cliArguments(server)('status'));
    assert.equal(code, 1);
    assert.match(err, /^smsdock: licence_required: /);
  });

  test('3 when nothing listens', async (t) => {
    const server = await startServer(t);
    await server.stop();
    const { code, out, err } = await runCli([
      'status', `wss://127.0.0.1:${server.port}/ws`,
      '--fingerprint', FINGERPRINT, '--reconnect-attempts', '0',
    ]);
    assert.deepEqual([code, out], [3, '']);
    assert.match(err, /^smsdock: Cannot connect to wss:/);
  });

  test('3 for another certificate', async (t) => {
    const server = await startServer(t);
    const { code, out, err } = await runCli([
      'status', server.url, '--fingerprint', otherTlsFiles.fingerprint,
    ]);
    assert.deepEqual([code, out], [3, '']);
    assert.ok(err.includes(tlsFiles.fingerprint));
    assert.ok(err.includes(otherTlsFiles.fingerprint));
    assert.deepEqual(server.requests, []);
    assert.equal(server.httpRequestCount, 0);
  });

  test('fingerprint from the environment', async (t) => {
    const server = await startServer(t);
    const { code } = await runCli(['status', server.url], {
      env: { SMSDOCK_FINGERPRINT: tlsFiles.fingerprint.toLowerCase() },
    });
    assert.equal(code, 0);
  });

  test('insecure warns', async (t) => {
    const server = await startServer(t);
    const { code, out, err } = await runCli(['status', server.url, '--insecure', '--timeout', '5']);
    assert.equal(code, 0);
    assert.ok(err.startsWith(WARNING));
    assert.match(err, /not checked/);
    assert.match(out, /^App:/);
  });
});
