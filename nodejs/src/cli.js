/**
 * Command-line tool `smsdock` for the SMS Dock WebSocket API.
 *
 * Exit codes: 0 success, 1 the server answered with an error, 2 wrong
 * usage, 3 connection or TLS problem.
 */

import { parseArgs } from 'node:util';

import { EVENT_NAMES, FINGERPRINT_ENV, PASSWORD_ENV, SmsDockClient } from './client.js';
import { ApiError, SmsDockError, TimeoutError } from './errors.js';
import { DEFAULT_API_PORT, fetchFingerprint } from './fingerprint.js';

export const EXIT_OK = 0;
export const EXIT_API_ERROR = 1;
export const EXIT_USAGE = 2;
export const EXIT_CONNECTION = 3;
export const EXIT_INTERRUPTED = 130;

const TEXT_WIDTH = 60;
const HELP_WIDTH = 78;
const USAGE_WIDTH = 78;
const HELP_COLUMN = 24;

/** The command line cannot be carried out as given. */
export class UsageError extends Error {
  /** @param {string} message */
  constructor(message) {
    super(message);
    this.name = 'UsageError';
  }
}

/** Ends the parsing of the command line with a text and an exit code. */
class ParseExit extends Error {
  constructor(code, output, errors) {
    super(errors || output);
    this.code = code;
    this.output = output;
    this.errors = errors;
  }
}

// ----------------------------------------------------------------------
// Command line
// ----------------------------------------------------------------------

const DESCRIPTION = 'Read, send and watch SMS through the SMS Dock app.';
const EPILOG =
  `The password is read from the environment variable ${PASSWORD_ENV}, ` +
  'never from the command line.';

const URL_ARGUMENT = {
  name: 'url',
  help: 'wss:// URL shown by the app, e.g. wss://192.168.1.50:9443/ws',
};

const CONNECTION_OPTIONS = [
  {
    name: 'fingerprint',
    metavar: 'HEX',
    help:
      'SHA-256 fingerprint of the certificate as shown by the app ' +
      `(default: environment variable ${FINGERPRINT_ENV})`,
  },
  { name: 'insecure', flag: true, help: 'do not check the certificate (for tests only)' },
  {
    name: 'password-env',
    metavar: 'NAME',
    fallback: PASSWORD_ENV,
    help: `environment variable with the password (default: ${PASSWORD_ENV})`,
  },
  {
    name: 'timeout',
    metavar: 'SECONDS',
    convert: float,
    fallback: 30,
    help: 'seconds to wait for a response (default: 30)',
  },
  {
    name: 'reconnect-attempts',
    metavar: 'N',
    convert: int,
    fallback: 5,
    help:
      'reconnect attempts after a lost connection; -1 means without limit (default: 5)',
  },
  { name: 'json', flag: true, help: 'print JSON instead of text' },
];

const COMMANDS = {
  fingerprint: {
    help: 'print the certificate fingerprint of a server',
    description:
      'Print the SHA-256 fingerprint of the certificate the server presents. ' +
      'Compare it with the Server screen of the app before you use it.',
    argument: {
      name: 'host',
      metavar: 'HOST[:PORT]',
      help: `address of the phone; the default port is ${DEFAULT_API_PORT}`,
    },
    connection: false,
    options: [
      {
        name: 'timeout',
        metavar: 'SECONDS',
        convert: float,
        fallback: 10,
        help: 'seconds to wait (default: 10)',
      },
    ],
  },
  status: {
    help: 'show phone, SIMs, permissions and licence',
    options: [],
    run: status,
  },
  conversations: {
    help: 'list conversations, newest first',
    options: [
      { name: 'search', metavar: 'TEXT', help: 'search text, numbers and contact names' },
      { name: 'unread', flag: true, help: 'only conversations with unread messages' },
      { name: 'sim', metavar: 'ID', convert: int, help: 'only this subscription id' },
      {
        name: 'limit',
        metavar: 'N',
        convert: positive,
        fallback: 50,
        help: 'number of conversations (default: 50)',
      },
    ],
    run: conversations,
  },
  messages: {
    help: 'list messages, newest first',
    options: [
      { name: 'address', metavar: 'NUMBER', help: 'only messages from or to this number' },
      { name: 'conversation', metavar: 'ID', convert: int, help: 'only this conversation' },
      {
        name: 'direction',
        choices: ['in', 'out', 'all'],
        fallback: 'all',
        help: 'incoming, outgoing or both',
      },
      { name: 'search', metavar: 'TEXT', help: 'search the text' },
      {
        name: 'since',
        metavar: 'TIME',
        convert: timestamp,
        help: 'not older than this: 2026-09-29, 2026-09-29T08:00 or milliseconds',
      },
      { name: 'until', metavar: 'TIME', convert: timestamp, help: 'not newer than this' },
      { name: 'unread', flag: true, help: 'only unread messages' },
      { name: 'sim', metavar: 'ID', convert: int, help: 'only this subscription id' },
      {
        name: 'limit',
        metavar: 'N',
        convert: positive,
        fallback: 50,
        help: 'number of messages (default: 50)',
      },
    ],
    run: messages,
  },
  send: {
    help: 'send an SMS',
    description: 'Send an SMS. Without --text the text is read from standard input.',
    options: [
      {
        name: 'to',
        metavar: 'NUMBER',
        required: true,
        help: 'phone number of the recipient',
      },
      { name: 'text', metavar: 'TEXT', help: 'text of the message' },
      { name: 'sim', metavar: 'ID', convert: int, help: 'subscription id of the SIM' },
      {
        name: 'request-id',
        metavar: 'ID',
        help: 'your own id; the same id is sent only once',
      },
      { name: 'wait', flag: true, help: 'wait until the message was sent or failed' },
      {
        name: 'wait-timeout',
        metavar: 'SECONDS',
        convert: float,
        fallback: 60,
        help: 'longest time to wait with --wait (default: 60)',
      },
    ],
    run: send,
  },
  delete: {
    help: 'delete a message or a conversation',
    options: [
      { name: 'message', metavar: 'ID', convert: int, help: 'id of the message' },
      { name: 'conversation', metavar: 'ID', convert: int, help: 'id of the conversation' },
    ],
    exclusive: ['message', 'conversation'],
    run: remove,
  },
  'mark-read': {
    help: 'mark a conversation as read',
    description:
      'Mark all messages of a conversation as read. SMS Dock must be the ' +
      'default SMS app of the phone.',
    options: [
      {
        name: 'conversation',
        metavar: 'ID',
        convert: int,
        required: true,
        help: 'id of the conversation',
      },
    ],
    run: markRead,
  },
  contacts: {
    help: 'list the contacts of the phone',
    options: [
      { name: 'search', metavar: 'TEXT', help: 'search names and numbers' },
      {
        name: 'limit',
        metavar: 'N',
        convert: positive,
        fallback: 50,
        help: 'number of contacts (default: 50)',
      },
    ],
    run: contacts,
  },
  listen: {
    help: 'print incoming SMS as they arrive',
    description: 'Print events as they arrive until Ctrl+C.',
    options: [
      { name: 'address', metavar: 'NUMBER', help: 'only SMS events of this number' },
      {
        name: 'events',
        metavar: 'EVENT',
        several: true,
        choices: [...EVENT_NAMES, '*'],
        fallback: ['sms.received'],
        help: 'events to print (default: sms.received); * means all',
      },
      {
        name: 'count',
        metavar: 'N',
        convert: positive,
        help: 'stop after this many events',
      },
    ],
    run: listen,
  },
};

function optionsOf(command) {
  const spec = COMMANDS[command];
  return spec.connection === false ? spec.options : [...CONNECTION_OPTIONS, ...spec.options];
}

function camelCase(name) {
  return name.replace(/-([a-z])/g, (_, letter) => letter.toUpperCase());
}

function usageLine(command) {
  const prefix = 'usage: ';
  const optional = ['[-h]'];
  let positional;
  if (command === null) {
    positional = [`{${Object.keys(COMMANDS).join(',')}} ...`];
  } else {
    const spec = COMMANDS[command];
    const argument = spec.argument ?? URL_ARGUMENT;
    positional = [argument.metavar ?? argument.name];
    const group = spec.exclusive ?? [];
    for (const option of optionsOf(command)) {
      let text = `--${option.name}`;
      if (!option.flag) {
        const metavar = option.metavar ?? `{${option.choices.join(',')}}`;
        text += option.several ? ` ${metavar} [${metavar} ...]` : ` ${metavar}`;
      }
      if (group.includes(option.name)) {
        const last = group.indexOf(option.name) === group.length - 1;
        optional.push(group[0] === option.name ? `(${text} |` : last ? `${text})` : `${text} |`);
      } else {
        optional.push(option.required ? text : `[${text}]`);
      }
    }
  }
  const program = command === null ? 'smsdock' : `smsdock ${command}`;
  const oneLine = [program, ...optional, ...positional].join(' ');
  if (prefix.length + oneLine.length <= USAGE_WIDTH) {
    return prefix + oneLine;
  }
  const indent = ' '.repeat(prefix.length + program.length + 1);
  const lines = [];
  const fill = (parts, first) => {
    let line = [];
    let length = (first ? prefix.length : indent.length) - 1;
    for (const part of parts) {
      if (length + 1 + part.length > USAGE_WIDTH && line.length > 0) {
        lines.push(indent + line.join(' '));
        line = [];
        length = indent.length - 1;
      }
      line.push(part);
      length += part.length + 1;
    }
    lines.push(indent + line.join(' '));
  };
  fill([program, ...optional], true);
  lines[0] = prefix + lines[0].slice(indent.length);
  fill(positional, false);
  return lines.join('\n');
}

function wrap(text, width, indent = '') {
  const lines = [];
  let line = '';
  for (const word of text.split(' ')) {
    if (line && (line + ' ' + word).length > width) {
      lines.push(line);
      line = indent + word;
    } else {
      line = line ? `${line} ${word}` : word;
    }
  }
  lines.push(line);
  return lines.join('\n');
}

function helpEntry(left, help) {
  const indent = ' '.repeat(HELP_COLUMN);
  const body = wrap(help, HELP_WIDTH - HELP_COLUMN).split('\n');
  const head = `  ${left}`;
  if (head.length > HELP_COLUMN - 2) {
    return [head, ...body.map((line) => indent + line)].join('\n');
  }
  return [head.padEnd(HELP_COLUMN) + body[0], ...body.slice(1).map((l) => indent + l)].join(
    '\n',
  );
}

function mainHelp() {
  const lines = [usageLine(null), '', DESCRIPTION, '', 'commands:'];
  for (const [name, spec] of Object.entries(COMMANDS)) {
    lines.push(helpEntry(name, spec.help));
  }
  lines.push('', 'options:', helpEntry('-h, --help', 'show this help message and exit'));
  lines.push('', wrap(EPILOG, HELP_WIDTH), '');
  return lines.join('\n');
}

function commandHelp(command) {
  const spec = COMMANDS[command];
  const argument = spec.argument ?? URL_ARGUMENT;
  const lines = [usageLine(command), ''];
  if (spec.description) {
    lines.push(wrap(spec.description, HELP_WIDTH), '');
  }
  lines.push('positional arguments:', helpEntry(argument.metavar ?? argument.name, argument.help));
  lines.push('', 'options:', helpEntry('-h, --help', 'show this help message and exit'));
  for (const option of optionsOf(command)) {
    let left = `--${option.name}`;
    if (!option.flag) {
      const metavar = option.metavar ?? `{${option.choices.join(',')}}`;
      left += option.several ? ` ${metavar} [${metavar} ...]` : ` ${metavar}`;
    }
    lines.push(helpEntry(left, option.help));
  }
  lines.push('');
  return lines.join('\n');
}

function usageError(command, message) {
  const program = command === null ? 'smsdock' : `smsdock ${command}`;
  return new ParseExit(
    EXIT_USAGE,
    '',
    `${usageLine(command)}\n${program}: error: ${message}\n`,
  );
}

/**
 * `--reconnect-attempts -1`: the option parser takes `-1` for an option.
 * Join such a pair to `--reconnect-attempts=-1`.
 */
function joinNegativeNumbers(argv, options) {
  const takesValue = new Set(
    options.filter((option) => !option.flag).map((option) => `--${option.name}`),
  );
  const joined = [];
  for (let index = 0; index < argv.length; index += 1) {
    const next = argv[index + 1];
    if (takesValue.has(argv[index]) && next !== undefined && /^-\d/.test(next)) {
      joined.push(`${argv[index]}=${next}`);
      index += 1;
    } else {
      joined.push(argv[index]);
    }
  }
  return joined;
}

function quoted(choices) {
  return choices.map((choice) => `'${choice}'`).join(', ');
}

/** Returns the arguments that the command does not know, as they were typed. */
function unrecognized(args, config, several) {
  const { tokens } = parseArgs({
    args,
    options: config,
    allowPositionals: true,
    strict: false,
    tokens: true,
  });
  const unknown = [];
  let collecting = false;
  let positionals = 0;
  for (const token of tokens) {
    if (token.kind === 'option') {
      collecting = several.includes(token.name);
      if (!Object.hasOwn(config, token.name)) {
        unknown.push(token.rawName + (token.inlineValue ? `=${token.value}` : ''));
      }
    } else if (token.kind === 'positional' && !collecting) {
      positionals += 1;
      if (positionals > 1) {
        unknown.push(token.value);
      }
    }
  }
  return unknown;
}

function parseProblem(command, error, args, config, several) {
  const option = /'(-{1,2}[^'\s=<]+)/.exec(error.message)?.[1];
  switch (error.code) {
    case 'ERR_PARSE_ARGS_UNKNOWN_OPTION': {
      const unknown = unrecognized(args, config, several);
      const listed = unknown.length > 0 ? unknown.join(' ') : (option ?? error.message);
      return usageError(null, `unrecognized arguments: ${listed}`);
    }
    case 'ERR_PARSE_ARGS_INVALID_OPTION_VALUE':
      if (option && /argument missing|ambiguous/.test(error.message)) {
        return usageError(command, `argument ${option}: expected one argument`);
      }
      if (option && /does not take an argument/.test(error.message)) {
        return usageError(command, `argument ${option}: takes no value`);
      }
      return usageError(command, error.message);
    default:
      return usageError(command, error.message);
  }
}

/**
 * Reads the command line.
 *
 * Returns the values with camel-case names, among them `command` and `url`
 * (or `host`). Throws `ParseExit` for `--help` and for wrong usage.
 *
 * @param {string[]} argv
 * @param {NodeJS.ProcessEnv} [env]
 * @returns {Record<string, any>}
 */
export function parseCommandLine(argv, env = process.env) {
  const [command, ...rest] = argv;
  if (command === undefined) {
    throw usageError(null, 'the following arguments are required: command');
  }
  if (command === '-h' || command === '--help') {
    throw new ParseExit(EXIT_OK, mainHelp(), '');
  }
  if (!Object.hasOwn(COMMANDS, command)) {
    const message = command.startsWith('-')
      ? `unrecognized arguments: ${command}`
      : `argument command: invalid choice: '${command}' ` +
        `(choose from ${quoted(Object.keys(COMMANDS))})`;
    throw usageError(null, message);
  }
  const spec = COMMANDS[command];
  const options = optionsOf(command);
  const config = { help: { type: 'boolean', short: 'h' } };
  for (const option of options) {
    config[option.name] = option.flag
      ? { type: 'boolean' }
      : { type: 'string', multiple: Boolean(option.several) };
  }
  const several = options.filter((option) => option.several).map((option) => option.name);
  const given = joinNegativeNumbers(rest, options);
  let tokens;
  try {
    ({ tokens } = parseArgs({
      args: given,
      options: config,
      allowPositionals: true,
      strict: true,
      tokens: true,
    }));
  } catch (error) {
    throw parseProblem(command, error, given, config, several);
  }
  if (tokens.some((token) => token.kind === 'option' && token.name === 'help')) {
    throw new ParseExit(EXIT_OK, commandHelp(command), '');
  }

  const byName = new Map(options.map((option) => [option.name, option]));
  const raw = new Map();
  const positionals = [];
  let collecting = null;
  for (const token of tokens) {
    if (token.kind === 'option') {
      const option = byName.get(token.name);
      if (option.several) {
        // `--events a b c`: the values that follow belong to the option.
        collecting = raw.get(option.name) ?? [];
        collecting.push(token.value);
        raw.set(option.name, collecting);
      } else {
        collecting = null;
        const other = spec.exclusive?.find(
          (name) => name !== option.name && raw.has(name),
        );
        if (other !== undefined && spec.exclusive.includes(option.name)) {
          throw usageError(
            command,
            `argument --${option.name}: not allowed with argument --${other}`,
          );
        }
        raw.set(option.name, option.flag ? true : token.value);
      }
    } else if (token.kind === 'positional') {
      if (collecting !== null) {
        collecting.push(token.value);
      } else {
        positionals.push(token.value);
      }
    } else {
      collecting = null;
    }
  }

  const argument = spec.argument ?? URL_ARGUMENT;
  const args = { command };
  for (const option of options) {
    const key = camelCase(option.name);
    if (!raw.has(option.name)) {
      args[key] = option.flag ? false : (option.fallback ?? null);
      continue;
    }
    const value = raw.get(option.name);
    const check = (text) => {
      if (option.choices && !option.choices.includes(text)) {
        throw usageError(
          command,
          `argument --${option.name}: invalid choice: '${text}' ` +
            `(choose from ${quoted(option.choices)})`,
        );
      }
      if (!option.convert) {
        return text;
      }
      try {
        return option.convert(text);
      } catch (error) {
        if (!(error instanceof UsageError)) {
          throw error;
        }
        throw usageError(command, `argument --${option.name}: ${error.message}`);
      }
    };
    args[key] = option.flag ? true : option.several ? value.map(check) : check(value);
  }
  if (command !== 'fingerprint' && args.fingerprint === null) {
    args.fingerprint = env[FINGERPRINT_ENV] ?? null;
  }

  const missing = [];
  if (positionals.length === 0) {
    missing.push(argument.metavar ?? argument.name);
  }
  for (const option of options) {
    if (option.required && !raw.has(option.name)) {
      missing.push(`--${option.name}`);
    }
  }
  if (missing.length > 0) {
    throw usageError(
      command,
      `the following arguments are required: ${missing.join(', ')}`,
    );
  }
  if (spec.exclusive && !spec.exclusive.some((name) => raw.has(name))) {
    const names = spec.exclusive.map((name) => `--${name}`).join(' ');
    throw usageError(command, `one of the arguments ${names} is required`);
  }
  if (positionals.length > 1) {
    throw usageError(null, `unrecognized arguments: ${positionals.slice(1).join(' ')}`);
  }
  args[argument.name] = positionals[0];
  return args;
}

/**
 * Runs the command line and returns the exit code.
 *
 * @param {string[]} [argv] Arguments without `node` and the script.
 * @returns {Promise<number>}
 */
export async function main(argv = process.argv.slice(2)) {
  let args;
  try {
    args = parseCommandLine(argv);
  } catch (error) {
    if (error instanceof ParseExit) {
      process.stdout.write(error.output);
      process.stderr.write(error.errors);
      return error.code;
    }
    throw error;
  }
  /** @type {SmsDockClient | null} */
  let client = null;
  const onInterrupt = () => {
    const stop = () => process.exit(EXIT_INTERRUPTED);
    if (client === null) {
      stop();
    } else {
      client.close().then(stop, stop);
    }
  };
  process.on('SIGINT', onInterrupt);
  try {
    if (args.command === 'fingerprint') {
      return await fingerprint(args);
    }
    client = await makeClient(args);
    try {
      await client.connect();
      return await COMMANDS[args.command].run(client, args);
    } finally {
      await client.close();
    }
  } catch (error) {
    if (error instanceof ApiError) {
      complain(`${error.code}: ${error.message}`);
      return EXIT_API_ERROR;
    }
    if (error instanceof UsageError || error instanceof RangeError) {
      complain(error.message);
      return EXIT_USAGE;
    }
    if (
      error instanceof SmsDockError ||
      error instanceof TimeoutError ||
      isSystemError(error)
    ) {
      complain(error.message);
      return EXIT_CONNECTION;
    }
    throw error;
  } finally {
    process.off('SIGINT', onInterrupt);
  }
}

function isSystemError(error) {
  return error instanceof Error && typeof error.syscall === 'string';
}

function complain(message) {
  process.stderr.write(`smsdock: ${message}\n`);
}

function print(text = '') {
  process.stdout.write(`${text}\n`);
}

async function makeClient(args) {
  if (!args.insecure && !args.fingerprint) {
    throw new UsageError(
      'No certificate fingerprint. Pass --fingerprint or set ' +
        `${FINGERPRINT_ENV}. 'smsdock fingerprint HOST' prints the ` +
        'fingerprint of a server.',
    );
  }
  if (args.reconnectAttempts < -1) {
    throw new UsageError('--reconnect-attempts cannot be less than -1');
  }
  const password = await readPassword(args.passwordEnv);
  return new SmsDockClient(args.url, {
    password,
    fingerprint: args.fingerprint ?? undefined,
    insecure: args.insecure,
    timeout: args.timeout,
    reconnectAttempts: args.reconnectAttempts === -1 ? null : args.reconnectAttempts,
  });
}

async function readPassword(variable) {
  const fromEnvironment = process.env[variable];
  if (fromEnvironment) {
    return fromEnvironment;
  }
  if (process.stdin.isTTY) {
    const typed = await promptHidden('SMS Dock password: ');
    if (typed) {
      return typed;
    }
  }
  throw new UsageError(`No password. Set the environment variable ${variable}.`);
}

/** Asks on the terminal and reads a line without showing it. */
function promptHidden(prompt) {
  return new Promise((resolve) => {
    const stdin = process.stdin;
    let typed = '';
    const finish = () => {
      stdin.off('data', onData);
      stdin.setRawMode(false);
      stdin.pause();
      process.stderr.write('\n');
      resolve(typed);
    };
    const onData = (chunk) => {
      for (const character of chunk.toString('utf8')) {
        if (character === '\r' || character === '\n' || character === '\u0004') {
          finish();
          return;
        }
        if (character === '\u0003') {
          stdin.setRawMode(false);
          process.stderr.write('\n');
          process.exit(EXIT_INTERRUPTED);
        }
        if (character === '\u007f' || character === '\b') {
          typed = [...typed].slice(0, -1).join('');
        } else {
          typed += character;
        }
      }
    };
    process.stderr.write(prompt);
    stdin.setRawMode(true);
    stdin.resume();
    stdin.on('data', onData);
  });
}

async function readStandardInput() {
  const chunks = [];
  for await (const chunk of process.stdin) {
    chunks.push(chunk);
  }
  return Buffer.concat(chunks).toString('utf8');
}

// ----------------------------------------------------------------------
// Commands
// ----------------------------------------------------------------------

async function fingerprint(args) {
  const [host, port] = hostAndPort(args.host);
  print(await fetchFingerprint(host, port, { timeout: args.timeout }));
  process.stderr.write(
    'Compare this fingerprint with the Server screen of the app. ' +
      'Use it only if both are the same.\n',
  );
  return EXIT_OK;
}

async function status(client, args) {
  const result = await client.status();
  if (args.json) {
    printJson(result);
    return EXIT_OK;
  }
  const app = result.app ?? {};
  const licence = result.licence ?? {};
  const permissions = result.permissions ?? {};
  const missing = Object.keys(permissions)
    .filter((name) => !permissions[name])
    .sort();
  let licenceText;
  if (licence.licensed) {
    licenceText = 'full licence';
  } else if (!licence.enforced) {
    licenceText = 'not enforced';
  } else {
    licenceText =
      `free version, ${show(licence.sentToday)} of ` +
      `${show(licence.dailyLimit)} SMS sent today`;
  }
  const lines = [
    [
      'App',
      `${show(app.name)} ${show(app.version)} (protocol ${show(app.protocolVersion)})`,
    ],
    ['Default SMS app', yesNo(result.defaultSmsApp)],
    ['Delete mode', show(result.deleteMode)],
    ['Licence', licenceText],
    ['Permissions', missing.length === 0 ? 'all granted' : `missing: ${missing.join(', ')}`],
  ];
  for (const [label, value] of lines) {
    print(`${`${label}:`.padEnd(17)}${value}`);
  }
  const sims = result.sims ?? [];
  if (sims.length > 0) {
    print();
    printTable(
      ['SIM', 'SLOT', 'NAME', 'NUMBER', 'DEFAULT'],
      sims.map((sim) => [
        sim.subscriptionId,
        sim.slot,
        sim.name,
        sim.number,
        yesNo(sim.isDefault),
      ]),
    );
  }
  return EXIT_OK;
}

async function collect(iterator) {
  const items = [];
  for await (const item of iterator) {
    items.push(item);
  }
  return items;
}

async function conversations(client, args) {
  const items = await collect(
    client.iterConversations({
      q: args.search,
      unread: args.unread,
      sim: args.sim,
      pageSize: Math.min(args.limit, 200),
      maxItems: args.limit,
    }),
  );
  if (args.json) {
    printJson(items);
    return EXIT_OK;
  }
  printTable(
    ['ID', 'TIME', 'NUMBER', 'NAME', 'MSGS', 'UNREAD', 'DIR', 'TEXT'],
    items.map((item) => [
      item.id,
      formatTime(item.timestamp),
      item.address,
      item.contact,
      item.messageCount,
      item.unreadCount,
      item.direction,
      oneLine(item.snippet),
    ]),
  );
  return EXIT_OK;
}

async function messages(client, args) {
  const items = await collect(
    client.iterMessages({
      address: args.address,
      conversationId: args.conversation,
      direction: args.direction,
      q: args.search,
      since: args.since,
      until: args.until,
      sim: args.sim,
      unread: args.unread,
      pageSize: Math.min(args.limit, 200),
      maxItems: args.limit,
    }),
  );
  if (args.json) {
    printJson(items);
    return EXIT_OK;
  }
  printTable(
    ['ID', 'TIME', 'DIR', 'NUMBER', 'NAME', 'STATUS', 'TEXT'],
    items.map((item) => [
      item.id,
      formatTime(item.timestamp),
      item.direction,
      item.address,
      item.contact,
      item.status,
      oneLine(item.body),
    ]),
  );
  return EXIT_OK;
}

async function send(client, args) {
  let text = args.text;
  if (text === null) {
    if (process.stdin.isTTY) {
      throw new UsageError('No text. Pass --text or write it to standard input.');
    }
    text = await readStandardInput();
    if (text.endsWith('\n')) {
      text = text.slice(0, -1);
    }
  }
  if (!text) {
    throw new UsageError('The text is empty.');
  }
  let result = await client.send(args.to, text, {
    sim: args.sim,
    requestId: args.requestId,
  });
  if (args.wait) {
    result = await client.waitForResult(result.requestId, { timeout: args.waitTimeout });
  }
  // Also without --wait: a repeated request id tells the old outcome.
  const failed = result.status === 'failed';
  if (args.json) {
    printJson(result);
  } else if (failed) {
    const code = result.errorCode;
    const cause = code === null || code === undefined ? '' : `error code ${code}, `;
    complain(
      `Sending to ${show(result.to)} failed (${cause}requestId ${show(result.requestId)})`,
    );
  } else {
    print(
      `${capitalize(show(result.status))}: ${show(result.to)}, ` +
        `${show(result.parts)} part(s), requestId ${show(result.requestId)}`,
    );
  }
  return failed ? EXIT_API_ERROR : EXIT_OK;
}

async function remove(client, args) {
  const result =
    args.message !== null
      ? await client.deleteMessage(args.message)
      : await client.deleteConversation(args.conversation);
  if (args.json) {
    printJson(result);
    return EXIT_OK;
  }
  const where =
    result.mode === 'provider'
      ? 'removed from the phone'
      : 'hidden in SMS Dock, still on the phone';
  print(`Deleted ${show(result.deleted)} message(s): ${where}`);
  return EXIT_OK;
}

async function markRead(client, args) {
  const result = await client.markRead(args.conversation);
  if (args.json) {
    printJson(result);
  } else {
    print(`Marked conversation ${args.conversation} as read`);
  }
  return EXIT_OK;
}

async function contacts(client, args) {
  const items = await collect(
    client.iterContacts({
      q: args.search,
      pageSize: Math.min(args.limit, 200),
      maxItems: args.limit,
    }),
  );
  if (args.json) {
    printJson(items);
    return EXIT_OK;
  }
  const rows = [];
  for (const contact of items) {
    const numbers = contact.numbers?.length ? contact.numbers : [{}];
    for (const number of numbers) {
      rows.push([contact.name, number.number, number.label]);
    }
  }
  printTable(['NAME', 'NUMBER', 'LABEL'], rows);
  return EXIT_OK;
}

async function listen(client, args) {
  client.onReconnect = reportReconnect;
  await client.subscribe(args.events, { address: args.address });
  if (!args.json) {
    process.stderr.write(
      `Listening for ${args.events.join(', ')}. Press Ctrl+C to stop.\n`,
    );
  }
  let count = 0;
  for await (const event of client.events()) {
    count += 1;
    print(args.json ? spacedJson(event) : describe(event));
    if (args.count !== null && count >= args.count) {
      break;
    }
  }
  return EXIT_OK;
}

/** Tells on standard error what a lost connection may have cost. */
function reportReconnect(client) {
  let lost;
  if (client.serverRestarted) {
    lost = 'the app was restarted, events may be missing';
  } else if (client.missedEvents) {
    lost = `${client.missedEvents} event(s) of the phone were not received`;
  } else {
    lost = 'no event was missed';
  }
  complain(`reconnected; ${lost}`);
}

// ----------------------------------------------------------------------
// Text
// ----------------------------------------------------------------------

/**
 * Returns one event as a line of text.
 *
 * @param {Record<string, any>} event
 * @returns {string}
 */
export function describe(event) {
  const name = event.event;
  const data = event.data ?? {};
  const message = data.message ?? {};
  if (name === 'sms.received') {
    let sender = show(message.address);
    if (message.contact) {
      sender = `${sender} (${message.contact})`;
    }
    return `${formatTime(message.timestamp, true)}  ${sender}: ${show(message.body)}`;
  }
  const now = formatTime(Date.now(), true);
  if (name === 'sms.sent' && Object.keys(message).length > 0) {
    return `${now}  sent to ${show(message.address)}: ${show(message.body)}`;
  }
  const details = Object.entries(data)
    .filter(([, value]) => typeof value !== 'object' || value === null)
    .map(([key, value]) => `${key}=${show(value)}`)
    .join(' ');
  return `${now}  ${show(name)} ${details}`.trimEnd();
}

/**
 * Splits `HOST`, `HOST:PORT`, `[IPv6]:PORT` or a `wss://` URL.
 *
 * @param {string} text
 * @returns {[string, number]}
 */
export function hostAndPort(text) {
  const problem = new UsageError(`'${text}' is not HOST or HOST:PORT`);
  let value = text.trim();
  const scheme = /^[A-Za-z][A-Za-z0-9+.-]*:\/\//.exec(value);
  if (scheme) {
    value = value.slice(scheme[0].length);
  } else if ((value.match(/:/g) ?? []).length > 1 && !value.startsWith('[')) {
    value = `[${value}]`;
  }
  value = value.split(/[/?#]/, 1)[0];
  value = value.slice(value.lastIndexOf('@') + 1);
  const match = /^(?:\[([^\]]+)\]|([^:[\]]+))(?::([0-9]*))?$/.exec(value);
  if (!match) {
    throw problem;
  }
  const host = match[1] ?? match[2];
  const port = match[3] ? Number(match[3]) : DEFAULT_API_PORT;
  if (!host || port > 65535) {
    throw problem;
  }
  return [host, port || DEFAULT_API_PORT];
}

function int(text) {
  if (!/^\s*[+-]?\d+\s*$/.test(text)) {
    throw new UsageError(`invalid int value: '${text}'`);
  }
  return Number(text);
}

function float(text) {
  const value = text.trim() === '' ? NaN : Number(text);
  if (!Number.isFinite(value)) {
    throw new UsageError(`invalid float value: '${text}'`);
  }
  return value;
}

function positive(text) {
  const value = /^\s*[+-]?\d+\s*$/.test(text) ? Number(text) : 0;
  if (value < 1) {
    throw new UsageError('must be a number from 1');
  }
  return value;
}

const ISO_TIME = new RegExp(
  '^(\\d{4})-(\\d{2})-(\\d{2})' +
    '(?:[T ](\\d{2}):(\\d{2})(?::(\\d{2})(?:[.,](\\d{1,6}))?)?)?' +
    '(Z|[+-]\\d{2}:?\\d{2})?$',
);

/** Reads milliseconds since the epoch or an ISO 8601 date (local time). */
function timestamp(text) {
  if (/^\d+$/.test(text)) {
    return Number(text);
  }
  const problem = new UsageError(
    'must be a date such as 2026-09-29 or 2026-09-29T08:00, ' +
      'or milliseconds since the epoch',
  );
  const match = ISO_TIME.exec(text);
  if (!match) {
    throw problem;
  }
  const [year, month, day, hour, minute, second] = match
    .slice(1, 7)
    .map((part) => Number(part ?? 0));
  const milliseconds = Math.trunc(Number(`0.${match[7] ?? '0'}`) * 1000);
  const fields = [year, month - 1, day, hour, minute, second, milliseconds];
  const zone = match[8];
  if (zone === undefined) {
    const local = new Date(...fields);
    const same =
      local.getFullYear() === year &&
      local.getMonth() === month - 1 &&
      local.getDate() === day &&
      local.getHours() === hour &&
      local.getMinutes() === minute;
    if (!same) {
      throw problem;
    }
    return local.getTime();
  }
  const utc = new Date(Date.UTC(...fields));
  if (utc.getUTCMonth() !== month - 1 || utc.getUTCDate() !== day || hour > 23) {
    throw problem;
  }
  let offset = 0;
  if (zone !== 'Z') {
    const digits = zone.replace(':', '');
    offset = Number(digits.slice(1, 3)) * 60 + Number(digits.slice(3, 5));
    if (digits[0] === '-') {
      offset = -offset;
    }
  }
  return utc.getTime() - offset * 60_000;
}

function formatTime(milliseconds, seconds = false) {
  if (typeof milliseconds !== 'number') {
    return '';
  }
  const moment = new Date(milliseconds);
  if (Number.isNaN(moment.getTime())) {
    return String(milliseconds);
  }
  const two = (value) => String(value).padStart(2, '0');
  const date =
    `${String(moment.getFullYear()).padStart(4, '0')}-` +
    `${two(moment.getMonth() + 1)}-${two(moment.getDate())}`;
  const time = `${two(moment.getHours())}:${two(moment.getMinutes())}`;
  return seconds ? `${date} ${time}:${two(moment.getSeconds())}` : `${date} ${time}`;
}

function oneLine(text) {
  const words = String(text ?? '').split(/\s+/).filter(Boolean);
  const characters = [...words.join(' ')];
  if (characters.length > TEXT_WIDTH) {
    return `${characters.slice(0, TEXT_WIDTH - 1).join('')}…`;
  }
  return characters.join('');
}

function yesNo(value) {
  return value ? 'yes' : 'no';
}

/** A value inside a line of text; written the way the Python tool writes it. */
function show(value) {
  if (value === undefined || value === null) return 'None';
  if (typeof value === 'boolean') return value ? 'True' : 'False';
  return String(value);
}

function capitalize(text) {
  return text.charAt(0).toUpperCase() + text.slice(1).toLowerCase();
}

function printJson(value) {
  print(JSON.stringify(value, null, 2));
}

/** JSON on one line with a space after `,` and `:`. */
function spacedJson(value) {
  if (Array.isArray(value)) {
    return `[${value.map(spacedJson).join(', ')}]`;
  }
  if (typeof value === 'object' && value !== null) {
    const fields = Object.entries(value)
      .filter(([, item]) => item !== undefined)
      .map(([key, item]) => `${JSON.stringify(key)}: ${spacedJson(item)}`);
    return `{${fields.join(', ')}}`;
  }
  return JSON.stringify(value);
}

function length(text) {
  return [...text].length;
}

function printTable(headers, rows) {
  const table = rows.map((row) =>
    row.map((cell) => (cell === null || cell === undefined ? '' : String(cell))),
  );
  if (table.length === 0) {
    print('Nothing found.');
    return;
  }
  const widths = headers.map((header, index) =>
    Math.max(length(header), ...table.map((row) => length(row[index]))),
  );
  for (const row of [headers, ...table]) {
    const cells = row.map(
      (cell, index) => cell + ' '.repeat(widths[index] - length(cell)),
    );
    print(cells.join('  ').trimEnd());
  }
}
