#!/usr/bin/env node
/**
 * Hook for incoming SMS: call a function for every SMS that arrives.
 *
 * Usage:
 *
 *     export SMSDOCK_PASSWORD='your password'
 *     export SMSDOCK_FINGERPRINT='AB:CD:…'      # from the Server screen of the app
 *     node listen_incoming.mjs wss://192.168.1.50:9443/ws
 *
 * Stop with Ctrl+C.
 */

import { SmsDockClient, SmsDockError } from 'smsdock-client';

const USAGE = 'Usage: node listen_incoming.mjs wss://192.168.1.50:9443/ws';

// Ids of the messages the hook has seen, and the time of the newest one.
const seen = new Set();
let newest = 0;

/** Your hook. `message` is a Message object of API.md. */
function onSms(message) {
  // Timestamps are milliseconds since the epoch; show local time.
  const received = new Date(message.timestamp).toLocaleTimeString('en-GB');
  const sender = message.contact || message.address;
  console.log(`${received}  ${sender}: ${message.body}`);
}

/** Calls the hook once per message. */
function handle(message) {
  if (seen.has(message.id)) {
    return;
  }
  if (message.id !== null) {
    seen.add(message.id);
  }
  newest = Math.max(newest, message.timestamp);
  onSms(message);
}

/**
 * Reads what arrived while the connection was down.
 *
 * The phone does not send events again. The client calls this function
 * after every reconnect, when the subscription is in force again, and
 * waits for it before it hands out the next event.
 */
async function catchUp(dock) {
  if (dock.serverRestarted || dock.missedEvents) {
    // Messages of the same millisecond come again; handle() knows them.
    const missed = [];
    for await (const message of dock.iterMessages({ direction: 'in', since: newest })) {
      missed.push(message);
    }
    for (const message of missed.reverse()) {
      handle(message); // oldest first
    }
  }
}

async function main() {
  if (process.argv.length !== 3) {
    console.log(USAGE);
    return 2;
  }

  // Password and fingerprint come from the environment.
  const dock = new SmsDockClient(process.argv[2], {
    // Keep trying when the phone is out of reach for a while.
    reconnectAttempts: null,
    onReconnect: catchUp,
  });
  // Ctrl+C: close() ends the loop below.
  process.once('SIGINT', () => dock.close());
  try {
    // Only messages from now on: remember the time of the phone.
    newest = (await dock.ping()).time;
    // A new connection gets no events. Ask for the ones you need.
    await dock.subscribe('sms.received');
    console.log('Waiting for SMS. Press Ctrl+C to stop.');

    // events() yields forever. After a lost connection it reconnects,
    // logs in and subscribes again, then calls catchUp().
    for await (const event of dock.events()) {
      handle(event.data.message);
    }
  } finally {
    await dock.close();
  }
  return 0;
}

try {
  process.exitCode = await main();
} catch (error) {
  if (!(error instanceof SmsDockError || error instanceof RangeError)) {
    throw error;
  }
  console.error(`listen_incoming: ${error.message}`);
  process.exitCode = 1;
}
