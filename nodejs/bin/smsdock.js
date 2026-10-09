#!/usr/bin/env node
/** Command-line tool `smsdock`; see `src/cli.js`. */

import { main } from '../src/cli.js';

// `smsdock listen | head -1`: the reader is gone, that is no error.
process.stdout.on('error', (error) => {
  if (error.code === 'EPIPE') {
    process.exit(0);
  }
  throw error;
});

// No process.exit(): the output is written completely, and a socket or
// timer left behind would show as a process that does not end.
process.exitCode = await main();
