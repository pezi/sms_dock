#!/usr/bin/env node
/**
 * Send one SMS and wait for the result.
 *
 * Usage:
 *
 *     export SMSDOCK_PASSWORD='your password'
 *     export SMSDOCK_FINGERPRINT='AB:CD:…'      # from the Server screen of the app
 *     node send_sms.mjs wss://192.168.1.50:9443/ws +4369912345678 "Hello"
 */

import { ApiError, SmsDockClient, SmsDockError, TimeoutError } from 'smsdock-client';

const USAGE = 'Usage: node send_sms.mjs wss://192.168.1.50:9443/ws +4369912345678 "Hello"';

async function main() {
  if (process.argv.length !== 5) {
    console.log(USAGE);
    return 2;
  }
  const [url, number, text] = process.argv.slice(2);

  // Password and fingerprint come from the environment.
  const dock = new SmsDockClient(url);
  let outcome;
  try {
    let result;
    try {
      // send() resolves as soon as the phone has queued the message. The
      // client adds a random requestId, so a repeat after a lost
      // connection cannot send the message twice. Pass your own
      // requestId if the whole program may run again.
      result = await dock.send(number, text);
    } catch (error) {
      if (!(error instanceof ApiError)) {
        throw error;
      }
      // For example invalid_number, message_too_long, quota_exceeded.
      console.log(`Not sent: ${error.code}: ${error.message}`);
      return 1;
    }
    console.log(`${result.parts} part(s) for ${result.to}`);

    // Wait until the network accepted the message or refused it.
    outcome = await dock.waitForResult(result.requestId, { timeout: 60 });
  } finally {
    await dock.close();
  }

  if (outcome.status === 'failed') {
    console.log(`Failed, error code ${outcome.errorCode}`);
    return 1;
  }
  console.log(`Sent, message id ${outcome.messageId}`);
  return 0;
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
  console.error(`send_sms: ${error.message}`);
  process.exitCode = 1;
}
