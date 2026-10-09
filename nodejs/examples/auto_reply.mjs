#!/usr/bin/env node
/**
 * Hook that answers incoming SMS.
 *
 * An SMS with the text STATUS gets the answer "All systems running.";
 * everything else gets a short help text.
 *
 * An answering machine can end up in a loop, for example with another
 * answering machine or with a service that answers every SMS. Three guards
 * prevent that:
 *
 * 1. A number gets at most one answer in COOLDOWN_MINUTES.
 * 2. Senders that are not ordinary phone numbers get no answer: names such
 *    as "A1" or "Post" and short codes such as 8810.
 * 3. At most MAX_REPLIES_PER_HOUR answers are sent in total.
 *
 * Usage:
 *
 *     export SMSDOCK_PASSWORD='your password'
 *     export SMSDOCK_FINGERPRINT='AB:CD:…'      # from the Server screen of the app
 *     node auto_reply.mjs wss://192.168.1.50:9443/ws
 *
 * Stop with Ctrl+C. Every answer is an SMS that your tariff may charge.
 */

import { pathToFileURL } from 'node:url';

import { ApiError, SmsDockClient, SmsDockError } from 'smsdock-client';

const USAGE = 'Usage: node auto_reply.mjs wss://192.168.1.50:9443/ws';

export const COOLDOWN_MINUTES = 10;
export const MAX_REPLIES_PER_HOUR = 20;

// A phone number: optional +, then 7 to 15 digits. Shorter ones are short
// codes; anything with letters is a sender name that cannot be answered.
const PHONE_NUMBER = /^\+?[0-9]{7,15}$/;

/** Decides whether a sender may get an answer now. */
export class ReplyGuard {
  #lastReply = new Map();
  #recent = [];

  /**
   * @param {number} cooldownSeconds
   * @param {number} repliesPerHour
   */
  constructor(cooldownSeconds, repliesPerHour) {
    this.cooldownSeconds = cooldownSeconds;
    this.repliesPerHour = repliesPerHour;
  }

  /**
   * Returns why this sender gets no answer, or `null` if it gets one.
   *
   * @param {string} address
   * @param {number} [now] Seconds of a clock that never goes back.
   * @returns {string | null}
   */
  refusal(address, now = performance.now() / 1000) {
    const number = address.replace(/[\s\-.()]/g, '');
    if (!PHONE_NUMBER.test(number)) {
      return 'not a phone number';
    }
    const last = this.#lastReply.get(number);
    if (last !== undefined && now - last < this.cooldownSeconds) {
      return 'answered a short time ago';
    }
    while (this.#recent.length > 0 && now - this.#recent[0] >= 3600) {
      this.#recent.shift();
    }
    if (this.#recent.length >= this.repliesPerHour) {
      return 'too many answers in the last hour';
    }
    // Count the answer before it is sent: a failure must not open the
    // door for a loop.
    this.#lastReply.set(number, now);
    this.#recent.push(now);
    return null;
  }
}

/** Returns the answer to a message text. */
export function answerFor(text) {
  if (text.trim().toUpperCase() === 'STATUS') {
    return 'All systems running.';
  }
  return 'Commands: STATUS';
}

async function main() {
  if (process.argv.length !== 3) {
    console.log(USAGE);
    return 2;
  }

  const guard = new ReplyGuard(COOLDOWN_MINUTES * 60, MAX_REPLIES_PER_HOUR);
  const dock = new SmsDockClient(process.argv[2], { reconnectAttempts: null });
  // Ctrl+C: close() ends the loop below.
  process.once('SIGINT', () => dock.close());
  try {
    await dock.subscribe('sms.received');
    console.log('Answering SMS. Press Ctrl+C to stop.');

    // SMS that arrive while the connection is down get no answer: the
    // phone does not send events again, and a late answer helps nobody.
    // listen_incoming.mjs shows how to read such messages.
    for await (const event of dock.events()) {
      const message = event.data.message;
      const sender = message.address;
      const refusal = guard.refusal(sender);
      if (refusal !== null) {
        console.log(`${sender}: no answer (${refusal})`);
        continue;
      }
      try {
        // Answer on the SIM that received the message.
        const result = await dock.send(sender, answerFor(message.body), {
          sim: message.sim,
        });
        console.log(`${sender}: answered, requestId ${result.requestId}`);
      } catch (error) {
        if (!(error instanceof ApiError)) {
          throw error;
        }
        console.log(`${sender}: not answered, ${error.code}: ${error.message}`);
      }
    }
  } finally {
    await dock.close();
  }
  return 0;
}

// Run only as a program, so that the guard can be imported and tested.
if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  try {
    process.exitCode = await main();
  } catch (error) {
    if (!(error instanceof SmsDockError || error instanceof RangeError)) {
      throw error;
    }
    console.error(`auto_reply: ${error.message}`);
    process.exitCode = 1;
  }
}
