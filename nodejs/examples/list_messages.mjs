#!/usr/bin/env node
/**
 * Print the last 10 incoming SMS.
 *
 * Usage:
 *
 *     export SMSDOCK_PASSWORD='your password'
 *     export SMSDOCK_FINGERPRINT='AB:CD:…'      # from the Server screen of the app
 *     node list_messages.mjs wss://192.168.1.50:9443/ws
 */

import { SmsDockClient, SmsDockError } from 'smsdock-client';

const USAGE = 'Usage: node list_messages.mjs wss://192.168.1.50:9443/ws';

/** Timestamps are milliseconds since the epoch; show local time. */
function localTime(milliseconds) {
  const moment = new Date(milliseconds);
  const two = (value) => String(value).padStart(2, '0');
  return (
    `${moment.getFullYear()}-${two(moment.getMonth() + 1)}-${two(moment.getDate())} ` +
    `${two(moment.getHours())}:${two(moment.getMinutes())}`
  );
}

async function main() {
  if (process.argv.length !== 3) {
    console.log(USAGE);
    return 2;
  }

  // The password comes from SMSDOCK_PASSWORD, the fingerprint from
  // SMSDOCK_FINGERPRINT. It pins the self-signed certificate of the phone.
  const dock = new SmsDockClient(process.argv[2]);
  let page;
  try {
    page = await dock.messages({ direction: 'in', limit: 10 });
  } finally {
    await dock.close();
  }

  for (const message of page.items) {
    const sender = message.contact || message.address;
    console.log(`${localTime(message.timestamp)}  ${sender}: ${message.body}`);
  }

  // page.nextCursor leads to the next 10; see iterMessages() for a loop
  // that follows it.
  return 0;
}

try {
  process.exitCode = await main();
} catch (error) {
  if (!(error instanceof SmsDockError || error instanceof RangeError)) {
    throw error;
  }
  console.error(`list_messages: ${error.message}`);
  process.exitCode = 1;
}
