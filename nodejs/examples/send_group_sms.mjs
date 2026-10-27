#!/usr/bin/env node
/**
 * Send one text to several numbers, one SMS each, and wait for the results.
 *
 * Usage:
 *
 *     export SMSDOCK_PASSWORD='your password'
 *     export SMSDOCK_FINGERPRINT='AB:CD:…'      # from the Server screen of the app
 *     node send_group_sms.mjs wss://192.168.1.50:9443/ws "Hello all" \
 *         +4369912345678 +436641112223
 */

import { ApiError, SmsDockClient, SmsDockError, TimeoutError } from 'smsdock-client';

const USAGE =
  'Usage: node send_group_sms.mjs wss://192.168.1.50:9443/ws "Hello all" ' +
  '+4369912345678 +436641112223';

async function main() {
  if (process.argv.length < 6) {
    console.log(USAGE);
    return 2;
  }
  const [url, text, ...numbers] = process.argv.slice(2);

  // Password and fingerprint come from the environment.
  const dock = new SmsDockClient(url);
  let failed = 0;
  try {
    let result;
    try {
      // sendGroup() resolves as soon as the phone has queued one SMS per
      // recipient. The phone checks the whole group first: one bad number
      // refuses all of them, nothing is sent then. The client adds a
      // random requestId; the members get the ids requestId.1,
      // requestId.2 and so on.
      result = await dock.sendGroup(numbers, text);
    } catch (error) {
      if (!(error instanceof ApiError)) {
        throw error;
      }
      // For example invalid_number, message_too_long, quota_exceeded.
      console.log(`Not sent: ${error.code}: ${error.message}`);
      return 1;
    }
    console.log(`${result.to.length} recipient(s), ${result.parts} part(s) each`);

    // Wait for every member; a member the phone refused at once is
    // already failed and needs no waiting.
    for (const member of result.messages) {
      if (member.status === 'failed') {
        console.log(`${member.to}: refused by the phone`);
        failed += 1;
        continue;
      }
      const outcome = await dock.waitForResult(member.requestId, { timeout: 60 });
      if (outcome.status === 'failed') {
        console.log(`${member.to}: failed, error code ${outcome.errorCode}`);
        failed += 1;
      } else {
        console.log(`${member.to}: ${outcome.status}, message id ${outcome.messageId}`);
      }
    }
  } finally {
    await dock.close();
  }
  return failed > 0 ? 1 : 0;
}

try {
  process.exitCode = await main();
} catch (error) {
  const known =
    error instanceof SmsDockError ||
    error instanceof TimeoutError ||
    error instanceof RangeError;
  if (!known) {
    throw error;
  }
  console.error(`send_group_sms: ${error.message}`);
  process.exitCode = 1;
}
