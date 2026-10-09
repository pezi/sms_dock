// --------------------------------------------------------------------------
// Filename: cli.dart
//
// Description: Command-line tool smsdock: arguments, commands, text and JSON
//   output, exit codes. Output is that of the Python tool.
// --------------------------------------------------------------------------

/// Command-line tool `smsdock` for the SMS Dock WebSocket API.
///
/// Exit codes: 0 success, 1 the server answered with an error, 2 wrong
/// usage, 3 connection or TLS problem.
///
/// Commands, options, output and exit codes are those of the Python tool
/// in `../python`.
library;

import 'dart:async';
import 'dart:convert';
import 'dart:io';

import 'client.dart';
import 'errors.dart';
import 'fingerprint.dart';
import 'models.dart';
import 'output.dart';

/// Exit code: success.
const int exitOk = 0;

/// Exit code: the server answered with an error, or the message failed.
const int exitApiError = 1;

/// Exit code: wrong usage.
const int exitUsage = 2;

/// Exit code: connection or TLS problem.
const int exitConnection = 3;

/// Exit code: stopped with Ctrl+C.
const int exitInterrupted = 130;

const int _textWidth = 60;
const int _helpWidth = 78;
const int _maxHelpPosition = 24;

/// The command line cannot be carried out as given.
class UsageError implements Exception {
  /// Creates the error with its text.
  UsageError(this.message);

  /// What is wrong.
  final String message;

  @override
  String toString() => message;
}

/// Ends the parsing of the command line with a text and an exit code.
class ParseExit implements Exception {
  /// Creates the result of `--help` or of a wrong command line.
  ParseExit(this.code, {this.output = '', this.errors = ''});

  /// The exit code.
  final int code;

  /// Text for standard output.
  final String output;

  /// Text for standard error.
  final String errors;

  @override
  String toString() => errors.isEmpty ? output : errors;
}

/// What the command line reads from and writes to.
///
/// The defaults are the terminal and the environment of the process; the
/// tests replace them.
class CliIo {
  /// Creates the surroundings of one run of the command line.
  CliIo({
    StringSink? out,
    StringSink? err,
    Map<String, String>? environment,
    bool Function()? inputIsTerminal,
    Future<String> Function()? readInput,
    Future<String?> Function(String prompt)? readPassword,
    Stream<void> Function()? interrupts,
  })  : out = out ?? stdout,
        err = err ?? stderr,
        environment = environment ?? Platform.environment,
        inputIsTerminal = inputIsTerminal ?? _inputIsTerminal,
        readInput = readInput ?? _readStandardInput,
        readPassword = readPassword ?? _promptHidden,
        interrupts = interrupts ?? ProcessSignal.sigint.watch;

  /// Standard output.
  final StringSink out;

  /// Standard error.
  final StringSink err;

  /// The environment variables.
  final Map<String, String> environment;

  /// Whether standard input is a terminal.
  final bool Function() inputIsTerminal;

  /// Reads standard input to its end.
  final Future<String> Function() readInput;

  /// Asks on the terminal and reads a line without showing it.
  final Future<String?> Function(String prompt) readPassword;

  /// Emits when the user presses Ctrl+C.
  final Stream<void> Function() interrupts;
}

// ----------------------------------------------------------------------
// Command line
// ----------------------------------------------------------------------

typedef _Convert = Object Function(String text);
typedef _Run = Future<int> Function(SmsDockClient, Arguments, CliIo);

class _Option {
  const _Option(
    this.name,
    this.help, {
    this.metavar,
    this.flag = false,
    this.several = false,
    this.choices,
    this.convert,
    this.fallback,
    this.required = false,
  });

  final String name;
  final String help;
  final String? metavar;
  final bool flag;
  final bool several;
  final List<String>? choices;
  final _Convert? convert;
  final Object? fallback;
  final bool required;

  /// The option with its value as the help shows it.
  String get invocation {
    if (flag) {
      return '--$name';
    }
    final value = metavar ?? '{${choices!.join(',')}}';
    return several ? '--$name $value [$value ...]' : '--$name $value';
  }
}

class _Command {
  const _Command(
    this.name,
    this.help, {
    this.description,
    this.argumentName = 'url',
    this.argumentMetavar,
    this.argumentHelp =
        'wss:// URL shown by the app, e.g. wss://192.168.1.50:9443/ws',
    this.connection = true,
    this.options = const [],
    this.exclusive = const [],
    this.run,
  });

  final String name;
  final String help;
  final String? description;
  final String argumentName;
  final String? argumentMetavar;
  final String argumentHelp;
  final bool connection;
  final List<_Option> options;
  final List<String> exclusive;
  final _Run? run;

  String get argument => argumentMetavar ?? argumentName;

  List<_Option> get allOptions =>
      connection ? [..._connectionOptions, ...options] : options;
}

const String _description =
    'Read, send and watch SMS through the SMS Dock app.';
const String _epilog =
    'The password is read from the environment variable $passwordEnv, '
    'never from the command line.';

const List<_Option> _connectionOptions = [
  _Option(
    'fingerprint',
    'SHA-256 fingerprint of the certificate as shown by the app '
        '(default: environment variable $fingerprintEnv)',
    metavar: 'HEX',
  ),
  _Option(
    'insecure',
    'do not check the certificate (for tests only)',
    flag: true,
  ),
  _Option(
    'password-env',
    'environment variable with the password (default: $passwordEnv)',
    metavar: 'NAME',
    fallback: passwordEnv,
  ),
  _Option(
    'timeout',
    'seconds to wait for a response (default: 30)',
    metavar: 'SECONDS',
    convert: _float,
    fallback: 30.0,
  ),
  _Option(
    'reconnect-attempts',
    'reconnect attempts after a lost connection; -1 means without limit '
        '(default: 5)',
    metavar: 'N',
    convert: _int,
    fallback: 5,
  ),
  _Option('json', 'print JSON instead of text', flag: true),
];

const List<_Command> _commands = [
  _Command(
    'fingerprint',
    'print the certificate fingerprint of a server',
    description: 'Print the SHA-256 fingerprint of the certificate the server '
        'presents. Compare it with the Server screen of the app before '
        'you use it.',
    argumentName: 'host',
    argumentMetavar: 'HOST[:PORT]',
    argumentHelp: 'address of the phone; the default port is $defaultApiPort',
    connection: false,
    options: [
      _Option(
        'timeout',
        'seconds to wait (default: 10)',
        metavar: 'SECONDS',
        convert: _float,
        fallback: 10.0,
      ),
    ],
  ),
  _Command('status', 'show phone, SIMs, permissions and licence', run: _status),
  _Command(
    'conversations',
    'list conversations, newest first',
    options: [
      _Option(
        'search',
        'search text, numbers and contact names',
        metavar: 'TEXT',
      ),
      _Option('unread', 'only conversations with unread messages', flag: true),
      _Option(
        'sim',
        'only this subscription id',
        metavar: 'ID',
        convert: _int,
      ),
      _Option(
        'limit',
        'number of conversations (default: 50)',
        metavar: 'N',
        convert: _positive,
        fallback: 50,
      ),
    ],
    run: _conversations,
  ),
  _Command(
    'messages',
    'list messages, newest first',
    options: [
      _Option(
        'address',
        'only messages from or to this number',
        metavar: 'NUMBER',
      ),
      _Option(
        'conversation',
        'only this conversation',
        metavar: 'ID',
        convert: _int,
      ),
      _Option(
        'direction',
        'incoming, outgoing or both',
        choices: ['in', 'out', 'all'],
        fallback: 'all',
      ),
      _Option('search', 'search the text', metavar: 'TEXT'),
      _Option(
        'since',
        'not older than this: 2026-09-29, 2026-09-29T08:00 or milliseconds',
        metavar: 'TIME',
        convert: _timestamp,
      ),
      _Option(
        'until',
        'not newer than this',
        metavar: 'TIME',
        convert: _timestamp,
      ),
      _Option('unread', 'only unread messages', flag: true),
      _Option(
        'sim',
        'only this subscription id',
        metavar: 'ID',
        convert: _int,
      ),
      _Option(
        'limit',
        'number of messages (default: 50)',
        metavar: 'N',
        convert: _positive,
        fallback: 50,
      ),
    ],
    run: _messages,
  ),
  _Command(
    'send',
    'send an SMS',
    description:
        'Send an SMS. Without --text the text is read from standard input.',
    options: [
      _Option(
        'to',
        'phone number of the recipient',
        metavar: 'NUMBER',
        required: true,
      ),
      _Option('text', 'text of the message', metavar: 'TEXT'),
      _Option(
        'sim',
        'subscription id of the SIM',
        metavar: 'ID',
        convert: _int,
      ),
      _Option(
        'request-id',
        'your own id; the same id is sent only once',
        metavar: 'ID',
      ),
      _Option('wait', 'wait until the message was sent or failed', flag: true),
      _Option(
        'wait-timeout',
        'longest time to wait with --wait (default: 60)',
        metavar: 'SECONDS',
        convert: _float,
        fallback: 60.0,
      ),
    ],
    run: _send,
  ),
  _Command(
    'delete',
    'delete a message or a conversation',
    options: [
      _Option('message', 'id of the message', metavar: 'ID', convert: _int),
      _Option(
        'conversation',
        'id of the conversation',
        metavar: 'ID',
        convert: _int,
      ),
    ],
    exclusive: ['message', 'conversation'],
    run: _delete,
  ),
  _Command(
    'mark-read',
    'mark a conversation as read',
    description:
        'Mark all messages of a conversation as read. SMS Dock must be '
        'the default SMS app of the phone.',
    options: [
      _Option(
        'conversation',
        'id of the conversation',
        metavar: 'ID',
        convert: _int,
        required: true,
      ),
    ],
    run: _markRead,
  ),
  _Command(
    'contacts',
    'list the contacts of the phone',
    options: [
      _Option('search', 'search names and numbers', metavar: 'TEXT'),
      _Option(
        'limit',
        'number of contacts (default: 50)',
        metavar: 'N',
        convert: _positive,
        fallback: 50,
      ),
    ],
    run: _contacts,
  ),
  _Command(
    'listen',
    'print incoming SMS as they arrive',
    description: 'Print events as they arrive until Ctrl+C.',
    options: [
      _Option(
        'address',
        'only SMS events of this number',
        metavar: 'NUMBER',
      ),
      _Option(
        'events',
        'events to print (default: sms.received); * means all',
        metavar: 'EVENT',
        several: true,
        choices: [...eventNames, '*'],
        fallback: ['sms.received'],
      ),
      _Option(
        'count',
        'stop after this many events',
        metavar: 'N',
        convert: _positive,
      ),
    ],
    run: _listen,
  ),
];

/// The values of a command line.
///
/// The keys are the names of the options as typed, without the dashes,
/// for example `reconnect-attempts`; `command` and `url` (or `host`) are
/// there as well. A flag is `true` or `false`; an option that was not
/// given has its default value or `null`.
extension type Arguments(Map<String, Object?> values) {
  /// The command, for example `messages`.
  String get command => values['command']! as String;

  /// The value of an option, or `null`.
  Object? operator [](String name) => values[name];

  /// Whether a flag was given.
  bool flag(String name) => values[name] == true;

  /// A text value, or `null`.
  String? text(String name) => values[name] as String?;

  /// A whole number, or `null`.
  int? integer(String name) => values[name] as int?;

  /// A number of seconds as a [Duration].
  Duration seconds(String name) => _duration(values[name]! as double);
}

_Command? _find(String name) {
  for (final command in _commands) {
    if (command.name == name) {
      return command;
    }
  }
  return null;
}

String _usageLine(_Command? command) {
  const prefix = 'usage: ';
  final optional = ['[-h]'];
  final List<String> positional;
  if (command == null) {
    positional = ['{${_commands.map((c) => c.name).join(',')}} ...'];
  } else {
    positional = [command.argument];
    final group = command.exclusive;
    for (final option in command.allOptions) {
      final text = option.invocation;
      if (group.contains(option.name)) {
        final first = group.first == option.name;
        final last = group.last == option.name;
        optional.add(
          first
              ? '($text |'
              : last
                  ? '$text)'
                  : '$text |',
        );
      } else {
        optional.add(option.required ? text : '[$text]');
      }
    }
  }
  final program = command == null ? 'smsdock' : 'smsdock ${command.name}';
  final oneLine = [program, ...optional, ...positional].join(' ');
  if (prefix.length + oneLine.length <= _helpWidth) {
    return prefix + oneLine;
  }
  final indent = ' ' * (prefix.length + program.length + 1);
  final lines = <String>[];
  void fill(List<String> parts, {required bool first}) {
    var line = <String>[];
    var length = (first ? prefix.length : indent.length) - 1;
    for (final part in parts) {
      if (length + 1 + part.length > _helpWidth && line.isNotEmpty) {
        lines.add(indent + line.join(' '));
        line = [];
        length = indent.length - 1;
      }
      line.add(part);
      length += part.length + 1;
    }
    lines.add(indent + line.join(' '));
  }

  fill([program, ...optional], first: true);
  lines[0] = prefix + lines[0].substring(indent.length);
  fill(positional, first: false);
  return lines.join('\n');
}

List<String> _wrap(String text, int width) {
  final lines = <String>[];
  var line = '';
  for (final word in text.split(' ')) {
    if (line.isNotEmpty && line.length + 1 + word.length > width) {
      lines.add(line);
      line = word;
    } else {
      line = line.isEmpty ? word : '$line $word';
    }
  }
  lines.add(line);
  return lines;
}

String _helpEntry(String left, String help, int position, {int indent = 2}) {
  final padding = ' ' * position;
  final body = _wrap(help, _helpWidth - position);
  final head = '${' ' * indent}$left';
  if (head.length > position - 2) {
    return [head, ...body.map((line) => padding + line)].join('\n');
  }
  return [
    head.padRight(position) + body.first,
    ...body.skip(1).map((line) => padding + line),
  ].join('\n');
}

const String _helpOption = '-h, --help';
const String _helpText = 'show this help message and exit';

String _mainHelp() {
  final choices = '{${_commands.map((c) => c.name).join(',')}}';
  final lines = [
    _usageLine(null),
    '',
    _description,
    '',
    'positional arguments:',
    '  $choices',
    for (final command in _commands)
      _helpEntry(command.name, command.help, _maxHelpPosition, indent: 4),
    '',
    'options:',
    _helpEntry(_helpOption, _helpText, _maxHelpPosition),
    '',
    ..._wrap(_epilog, _helpWidth),
    '',
  ];
  return lines.join('\n');
}

String _commandHelp(_Command command) {
  final options = command.allOptions;
  final longest = [
    command.argument,
    _helpOption,
    for (final option in options) option.invocation,
  ].map((text) => text.length + 2).reduce((a, b) => a > b ? a : b);
  final position =
      longest + 2 < _maxHelpPosition ? longest + 2 : _maxHelpPosition;
  final description = command.description;
  final lines = [
    _usageLine(command),
    '',
    if (description != null) ...[..._wrap(description, _helpWidth), ''],
    'positional arguments:',
    _helpEntry(command.argument, command.argumentHelp, position),
    '',
    'options:',
    _helpEntry(_helpOption, _helpText, position),
    for (final option in options)
      _helpEntry(option.invocation, option.help, position),
    '',
  ];
  return lines.join('\n');
}

ParseExit _usageError(_Command? command, String message) {
  final program = command == null ? 'smsdock' : 'smsdock ${command.name}';
  return ParseExit(
    exitUsage,
    errors: '${_usageLine(command)}\n$program: error: $message\n',
  );
}

String _quoted(Iterable<String> choices) =>
    choices.map((choice) => "'$choice'").join(', ');

final RegExp _negativeNumber = RegExp(
  r'^-(?:\d+(?:_\d+)*(?:\.\d+(?:_\d+)*)?|\.\d+(?:_\d+)*)$',
);

/// Whether an argument is written like an option and not like a value.
bool _looksLikeOption(String argument) =>
    argument.length > 1 &&
    argument.startsWith('-') &&
    !_negativeNumber.hasMatch(argument) &&
    !argument.contains(' ');

/// Reads the command line.
///
/// Returns the values of the options, among them `command` and `url` (or
/// `host`). Throws [ParseExit] for `--help` and for wrong usage.
Arguments parseCommandLine(
  List<String> argv, {
  Map<String, String>? environment,
}) {
  final unknown = <String>[];
  var index = 0;
  _Command? command;
  for (; index < argv.length; index += 1) {
    final argument = argv[index];
    if (argument == '-h' || argument == '--help') {
      throw ParseExit(exitOk, output: _mainHelp());
    }
    if (_looksLikeOption(argument)) {
      unknown.add(argument);
      continue;
    }
    command = _find(argument);
    if (command == null) {
      throw _usageError(
        null,
        "argument command: invalid choice: '$argument' "
        '(choose from ${_quoted(_commands.map((c) => c.name))})',
      );
    }
    break;
  }
  if (command == null) {
    throw _usageError(null, 'the following arguments are required: command');
  }
  final values = _parseCommand(command, argv.sublist(index + 1), unknown);
  if (command.connection && values['fingerprint'] == null) {
    values['fingerprint'] =
        (environment ?? Platform.environment)[fingerprintEnv];
  }
  return Arguments(values);
}

Map<String, Object?> _parseCommand(
  _Command command,
  List<String> rest,
  List<String> unknown,
) {
  final options = {
    for (final option in command.allOptions) '--${option.name}': option,
  };
  final seen = <String, Object?>{};
  String? positional;
  var literal = false;

  Object convert(_Option option, String text) {
    final choices = option.choices;
    if (choices != null && !choices.contains(text)) {
      throw _usageError(
        command,
        "argument --${option.name}: invalid choice: '$text' "
        '(choose from ${_quoted(choices)})',
      );
    }
    final function = option.convert;
    if (function == null) {
      return text;
    }
    try {
      return function(text);
    } on UsageError catch (error) {
      throw _usageError(command, 'argument --${option.name}: ${error.message}');
    }
  }

  var index = 0;
  while (index < rest.length) {
    final argument = rest[index];
    index += 1;
    if (!literal && argument == '--') {
      literal = true;
      continue;
    }
    if (literal || !_looksLikeOption(argument)) {
      if (positional == null) {
        positional = argument;
      } else {
        unknown.add(argument);
      }
      continue;
    }
    if (argument == '-h' || argument == '--help') {
      throw ParseExit(exitOk, output: _commandHelp(command));
    }
    final equals = argument.indexOf('=');
    final name = equals < 0 ? argument : argument.substring(0, equals);
    final explicit = equals < 0 ? null : argument.substring(equals + 1);
    final option = options[name];
    if (option == null) {
      unknown.add(argument);
      continue;
    }
    final Object value;
    if (option.flag) {
      if (explicit != null) {
        throw _usageError(
          command,
          "argument $name: ignored explicit argument '$explicit'",
        );
      }
      value = true;
    } else {
      final texts = <String>[];
      if (explicit != null) {
        texts.add(explicit);
      } else {
        while (index < rest.length &&
            rest[index] != '--' &&
            !_looksLikeOption(rest[index]) &&
            (option.several || texts.isEmpty)) {
          texts.add(rest[index]);
          index += 1;
        }
      }
      if (texts.isEmpty) {
        throw _usageError(
          command,
          option.several
              ? 'argument $name: expected at least one argument'
              : 'argument $name: expected one argument',
        );
      }
      value = option.several
          ? [for (final text in texts) convert(option, text)]
          : convert(option, texts.single);
    }
    if (command.exclusive.contains(option.name)) {
      for (final other in command.exclusive) {
        if (other != option.name && seen.containsKey(other)) {
          throw _usageError(
            command,
            'argument $name: not allowed with argument --$other',
          );
        }
      }
    }
    seen[option.name] = value;
  }

  final missing = [
    if (positional == null) command.argument,
    for (final option in command.allOptions)
      if (option.required && !seen.containsKey(option.name)) '--${option.name}',
  ];
  if (missing.isNotEmpty) {
    throw _usageError(
      command,
      'the following arguments are required: ${missing.join(', ')}',
    );
  }
  if (command.exclusive.isNotEmpty &&
      !command.exclusive.any(seen.containsKey)) {
    final names = command.exclusive.map((name) => '--$name').join(' ');
    throw _usageError(command, 'one of the arguments $names is required');
  }
  if (unknown.isNotEmpty) {
    throw _usageError(null, 'unrecognized arguments: ${unknown.join(' ')}');
  }
  return {
    'command': command.name,
    command.argumentName: positional,
    for (final option in command.allOptions)
      option.name: seen.containsKey(option.name)
          ? seen[option.name]
          : option.flag
              ? false
              : option.fallback,
  };
}

/// Runs the command line and returns the exit code.
///
/// [argv] are the arguments without the name of the program.
Future<int> runCli(List<String> argv, {CliIo? io}) {
  final surroundings = io ?? CliIo();
  return runZoned(
    () => _main(argv, surroundings),
    zoneValues: {errorSinkKey: surroundings.err},
  );
}

Future<int> _main(List<String> argv, CliIo io) async {
  final Arguments args;
  try {
    args = parseCommandLine(argv, environment: io.environment);
  } on ParseExit catch (exit) {
    io.out.write(exit.output);
    io.err.write(exit.errors);
    return exit.code;
  }
  void complain(Object? message) => io.err.writeln('smsdock: $message');
  SmsDockClient? client;
  StreamSubscription<void>? watch;
  try {
    if (args.command == 'fingerprint') {
      return await _fingerprint(args, io);
    }
    final dock = client = await _client(args, io);
    final interrupted = Completer<int>();
    watch = io.interrupts().listen((_) {
      if (!interrupted.isCompleted) {
        interrupted.complete(exitInterrupted);
      }
    });
    final run = _find(args.command)!.run!;
    return await Future.any([
      interrupted.future,
      dock.connect().then((_) => run(dock, args, io)),
    ]);
  } on ApiError catch (error) {
    complain(error);
    return exitApiError;
  } on UsageError catch (error) {
    complain(error.message);
    return exitUsage;
  } on ArgumentError catch (error) {
    complain(error.message);
    return exitUsage;
  } on SmsDockError catch (error) {
    complain(error);
    return exitConnection;
  } on TimeoutException catch (error) {
    complain(error.message ?? 'Timed out');
    return exitConnection;
  } on SocketException catch (error) {
    complain(describeSocketError(error));
    return exitConnection;
  } on IOException catch (error) {
    complain(error);
    return exitConnection;
  } finally {
    await watch?.cancel();
    await client?.close();
  }
}

Future<SmsDockClient> _client(Arguments args, CliIo io) async {
  final fingerprint = args.text('fingerprint');
  final insecure = args.flag('insecure');
  if (!insecure && (fingerprint == null || fingerprint.isEmpty)) {
    throw UsageError(
      'No certificate fingerprint. Pass --fingerprint or set '
      "$fingerprintEnv. 'smsdock fingerprint HOST' prints the "
      'fingerprint of a server.',
    );
  }
  final attempts = args.integer('reconnect-attempts')!;
  if (attempts < -1) {
    throw UsageError('--reconnect-attempts cannot be less than -1');
  }
  final password = await _password(args.text('password-env')!, io);
  return SmsDockClient(
    args.text('url')!,
    password: password,
    fingerprint: fingerprint,
    insecure: insecure,
    timeout: args.seconds('timeout'),
    reconnectAttempts: attempts == -1 ? null : attempts,
    environment: io.environment,
  );
}

Future<String> _password(String variable, CliIo io) async {
  final fromEnvironment = io.environment[variable];
  if (fromEnvironment != null && fromEnvironment.isNotEmpty) {
    return fromEnvironment;
  }
  if (io.inputIsTerminal()) {
    final typed = await io.readPassword('SMS Dock password: ');
    if (typed != null && typed.isNotEmpty) {
      return typed;
    }
  }
  throw UsageError('No password. Set the environment variable $variable.');
}

bool? _terminal;

/// Whether standard input is a terminal that a person types into.
///
/// `dart:io` takes every character device for a terminal, also
/// `/dev/null`. Only a real terminal has an echo mode. The answer is kept:
/// the prompt for the password closes standard input when it is done.
bool _inputIsTerminal() => _terminal ??= _hasEchoMode();

bool _hasEchoMode() {
  if (!stdin.hasTerminal) {
    return false;
  }
  try {
    stdin.echoMode;
    return true;
  } on StdinException {
    return false;
  }
}

/// Asks on the terminal and reads a line without showing it.
Future<String?> _promptHidden(String prompt) {
  stderr.write(prompt);
  final echo = stdin.echoMode;
  final typed = Completer<String?>();
  late final StreamSubscription<String> lines;
  // Ctrl+C must not leave a terminal that shows nothing.
  final watch = ProcessSignal.sigint.watch().listen((_) {
    stdin.echoMode = echo;
    exit(exitInterrupted);
  });
  void finish(String? line) {
    if (typed.isCompleted) {
      return;
    }
    // First the echo: the end of the subscription closes standard input.
    stdin.echoMode = echo;
    stderr.writeln();
    typed.complete(line);
    unawaited(lines.cancel());
    unawaited(watch.cancel());
  }

  stdin.echoMode = false;
  lines = stdin.transform(utf8.decoder).transform(const LineSplitter()).listen(
        finish,
        onError: (Object _) => finish(null),
        onDone: () => finish(null),
      );
  return typed.future;
}

Future<String> _readStandardInput() async {
  final text = await utf8.decoder.bind(stdin).join();
  // Line ends as Python reads them.
  return text.replaceAll('\r\n', '\n').replaceAll('\r', '\n');
}

// ----------------------------------------------------------------------
// Commands
// ----------------------------------------------------------------------

Future<int> _fingerprint(Arguments args, CliIo io) async {
  final (host, port) = hostAndPort(args.text('host')!);
  final fingerprint = await fetchFingerprint(
    host,
    port: port,
    timeout: args.seconds('timeout'),
  );
  io.out.writeln(fingerprint);
  io.err.writeln(
    'Compare this fingerprint with the Server screen of the app. '
    'Use it only if both are the same.',
  );
  return exitOk;
}

Future<int> _status(SmsDockClient client, Arguments args, CliIo io) async {
  final status = await client.status();
  if (args.flag('json')) {
    _printJson(io, status);
    return exitOk;
  }
  final app = _object(status['app']);
  final licence = _object(status['licence']);
  final permissions = _object(status['permissions']);
  final missing = [
    for (final entry in permissions.entries)
      if (!_truthy(entry.value)) entry.key,
  ]..sort();
  final String licenceText;
  if (_truthy(licence['licensed'])) {
    licenceText = 'full licence';
  } else if (!_truthy(licence['enforced'])) {
    licenceText = 'not enforced';
  } else {
    licenceText = 'free version, ${_show(licence['sentToday'])} of '
        '${_show(licence['dailyLimit'])} SMS sent today';
  }
  final lines = [
    (
      'App',
      '${_show(app['name'])} ${_show(app['version'])} '
          '(protocol ${_show(app['protocolVersion'])})',
    ),
    ('Default SMS app', _yesNo(status['defaultSmsApp'])),
    ('Delete mode', _show(status['deleteMode'])),
    ('Licence', licenceText),
    (
      'Permissions',
      missing.isEmpty ? 'all granted' : 'missing: ${missing.join(', ')}',
    ),
  ];
  for (final (label, value) in lines) {
    io.out.writeln('${'$label:'.padRight(17)}$value');
  }
  final sims = status['sims'];
  if (sims is List && sims.isNotEmpty) {
    io.out.writeln();
    _printTable(io, const [
      'SIM',
      'SLOT',
      'NAME',
      'NUMBER',
      'DEFAULT'
    ], [
      for (final sim in sims.map(_object))
        [
          sim['subscriptionId'],
          sim['slot'],
          sim['name'],
          sim['number'],
          _yesNo(sim['isDefault']),
        ],
    ]);
  }
  return exitOk;
}

int _pageSize(int limit) => limit < maxPageSize ? limit : maxPageSize;

Future<int> _conversations(
  SmsDockClient client,
  Arguments args,
  CliIo io,
) async {
  final limit = args.integer('limit')!;
  final items = await client
      .iterConversations(
        q: args.text('search'),
        unread: args.flag('unread'),
        sim: args.integer('sim'),
        pageSize: _pageSize(limit),
        maxItems: limit,
      )
      .toList();
  if (args.flag('json')) {
    _printJson(io, items);
    return exitOk;
  }
  _printTable(
    io,
    const ['ID', 'TIME', 'NUMBER', 'NAME', 'MSGS', 'UNREAD', 'DIR', 'TEXT'],
    [
      for (final item in items)
        [
          item['id'],
          _time(item['timestamp']),
          item['address'],
          item['contact'],
          item['messageCount'],
          item['unreadCount'],
          item['direction'],
          _oneLine(item['snippet']),
        ],
    ],
  );
  return exitOk;
}

Future<int> _messages(SmsDockClient client, Arguments args, CliIo io) async {
  final limit = args.integer('limit')!;
  final items = await client
      .iterMessages(
        address: args.text('address'),
        conversationId: args.integer('conversation'),
        direction: args.text('direction')!,
        q: args.text('search'),
        since: args.integer('since'),
        until: args.integer('until'),
        sim: args.integer('sim'),
        unread: args.flag('unread'),
        pageSize: _pageSize(limit),
        maxItems: limit,
      )
      .toList();
  if (args.flag('json')) {
    _printJson(io, items);
    return exitOk;
  }
  _printTable(
    io,
    const ['ID', 'TIME', 'DIR', 'NUMBER', 'NAME', 'STATUS', 'TEXT'],
    [
      for (final item in items)
        [
          item['id'],
          _time(item['timestamp']),
          item['direction'],
          item['address'],
          item['contact'],
          item['status'],
          _oneLine(item['body']),
        ],
    ],
  );
  return exitOk;
}

Future<int> _send(SmsDockClient client, Arguments args, CliIo io) async {
  var text = args.text('text');
  if (text == null) {
    if (io.inputIsTerminal()) {
      throw UsageError('No text. Pass --text or write it to standard input.');
    }
    text = await io.readInput();
    if (text.endsWith('\n')) {
      text = text.substring(0, text.length - 1);
    }
  }
  if (text.isEmpty) {
    throw UsageError('The text is empty.');
  }
  JsonObject result = await client.send(
    args.text('to')!,
    text,
    sim: args.integer('sim'),
    requestId: args.text('request-id'),
  );
  if (args.flag('wait')) {
    result = await client.waitForResult(
      result['requestId'] as String,
      timeout: args.seconds('wait-timeout'),
    );
  }
  // Also without --wait: a repeated request id tells the old outcome.
  final failed = result['status'] == 'failed';
  if (args.flag('json')) {
    _printJson(io, result);
  } else if (failed) {
    final code = result['errorCode'];
    final cause = code == null ? '' : 'error code ${_show(code)}, ';
    io.err.writeln(
      'smsdock: Sending to ${_show(result['to'])} failed '
      '(${cause}requestId ${_show(result['requestId'])})',
    );
  } else {
    io.out.writeln(
      '${_capitalize(_show(result['status']))}: ${_show(result['to'])}, '
      '${_show(result['parts'])} part(s), '
      'requestId ${_show(result['requestId'])}',
    );
  }
  return failed ? exitApiError : exitOk;
}

Future<int> _delete(SmsDockClient client, Arguments args, CliIo io) async {
  final message = args.integer('message');
  final result = message != null
      ? await client.deleteMessage(message)
      : await client.deleteConversation(args.integer('conversation')!);
  if (args.flag('json')) {
    _printJson(io, result);
    return exitOk;
  }
  final where = result['mode'] == 'provider'
      ? 'removed from the phone'
      : 'hidden in SMS Dock, still on the phone';
  io.out.writeln('Deleted ${_show(result['deleted'])} message(s): $where');
  return exitOk;
}

Future<int> _markRead(SmsDockClient client, Arguments args, CliIo io) async {
  final conversation = args.integer('conversation')!;
  final result = await client.markRead(conversation);
  if (args.flag('json')) {
    _printJson(io, result);
  } else {
    io.out.writeln('Marked conversation $conversation as read');
  }
  return exitOk;
}

Future<int> _contacts(SmsDockClient client, Arguments args, CliIo io) async {
  final limit = args.integer('limit')!;
  final items = await client
      .iterContacts(
        q: args.text('search'),
        pageSize: _pageSize(limit),
        maxItems: limit,
      )
      .toList();
  if (args.flag('json')) {
    _printJson(io, items);
    return exitOk;
  }
  final rows = <List<Object?>>[];
  for (final contact in items) {
    final numbers = contact['numbers'];
    final listed = numbers is List && numbers.isNotEmpty
        ? numbers
        : const [<String, dynamic>{}];
    for (final number in listed.map(_object)) {
      rows.add([contact['name'], number['number'], number['label']]);
    }
  }
  _printTable(io, const ['NAME', 'NUMBER', 'LABEL'], rows);
  return exitOk;
}

Future<int> _listen(SmsDockClient client, Arguments args, CliIo io) async {
  final events = (args['events']! as List).cast<String>();
  final json = args.flag('json');
  final limit = args.integer('count');
  client.onReconnect = (dock) => _reportReconnect(dock, io);
  await client.subscribe(events, address: args.text('address'));
  if (!json) {
    io.err.writeln(
      'Listening for ${events.join(', ')}. Press Ctrl+C to stop.',
    );
  }
  var count = 0;
  await for (final event in client.events()) {
    count += 1;
    io.out.writeln(json ? spacedJson(event) : describe(event));
    if (limit != null && count >= limit) {
      break;
    }
  }
  return exitOk;
}

/// Tells on standard error what a lost connection may have cost.
void _reportReconnect(SmsDockClient client, CliIo io) {
  final missed = client.missedEvents;
  final String lost;
  if (client.serverRestarted) {
    lost = 'the app was restarted, events may be missing';
  } else if (missed != null && missed != 0) {
    lost = '$missed event(s) of the phone were not received';
  } else {
    lost = 'no event was missed';
  }
  io.err.writeln('smsdock: reconnected; $lost');
}

// ----------------------------------------------------------------------
// Text
// ----------------------------------------------------------------------

/// Returns one event as a line of text.
///
/// [now] is the time written in front of events that carry none.
String describe(Map<String, dynamic> event, {DateTime? now}) {
  final name = event['event'];
  final data = _object(event['data']);
  final message = _object(data['message']);
  if (name == 'sms.received') {
    var sender = _show(message['address']);
    if (_truthy(message['contact'])) {
      sender = '$sender (${_show(message['contact'])})';
    }
    return '${_time(message['timestamp'], seconds: true)}  '
        '$sender: ${_show(message['body'])}';
  }
  final moment = _time(
    (now ?? DateTime.now()).millisecondsSinceEpoch,
    seconds: true,
  );
  if (name == 'sms.sent' && message.isNotEmpty) {
    return '$moment  sent to ${_show(message['address'])}: '
        '${_show(message['body'])}';
  }
  final details = [
    for (final entry in data.entries)
      if (entry.value is! Map && entry.value is! List)
        '${entry.key}=${_show(entry.value)}',
  ].join(' ');
  return _stripRight('$moment  ${_show(name)} $details');
}

final RegExp _schemePrefix = RegExp(r'^[A-Za-z][A-Za-z0-9+.-]*://');
final RegExp _hostPattern = RegExp(
  r'^(?:\[([^\]]+)\]|([^:\[\]]+))(?::([0-9]*))?$',
);

/// Splits `HOST`, `HOST:PORT`, `[IPv6]:PORT` or a `wss://` URL.
///
/// Throws [UsageError] for anything else.
(String, int) hostAndPort(String text) {
  final problem = UsageError("'$text' is not HOST or HOST:PORT");
  var value = text.trim();
  final scheme = _schemePrefix.firstMatch(value);
  if (scheme != null) {
    value = value.substring(scheme.end);
  } else if (':'.allMatches(value).length > 1 && !value.startsWith('[')) {
    value = '[$value]';
  }
  value = value.split(RegExp('[/?#]')).first;
  value = value.substring(value.lastIndexOf('@') + 1);
  final match = _hostPattern.firstMatch(value);
  if (match == null) {
    throw problem;
  }
  final host = match.group(1) ?? match.group(2) ?? '';
  final digits = match.group(3) ?? '';
  final port = digits.isEmpty ? 0 : int.tryParse(digits);
  if (host.isEmpty || port == null || port > 65535) {
    throw problem;
  }
  return (host, port == 0 ? defaultApiPort : port);
}

final RegExp _integerPattern = RegExp(r'^\s*[+-]?\d+\s*$');

Object _int(String text) {
  final value =
      _integerPattern.hasMatch(text) ? int.tryParse(text.trim()) : null;
  if (value == null) {
    throw UsageError("invalid int value: '$text'");
  }
  return value;
}

final RegExp _infinity = RegExp(r'^[+-]?inf(inity)?$', caseSensitive: false);

Object _float(String text) {
  final trimmed = text.trim();
  if (_infinity.hasMatch(trimmed)) {
    return trimmed.startsWith('-') ? double.negativeInfinity : double.infinity;
  }
  final value = double.tryParse(trimmed);
  if (value == null || !value.isFinite) {
    throw UsageError("invalid float value: '$text'");
  }
  return value;
}

Object _positive(String text) {
  final value =
      _integerPattern.hasMatch(text) ? int.tryParse(text.trim()) ?? 0 : 0;
  if (value < 1) {
    throw UsageError('must be a number from 1');
  }
  return value;
}

/// A number of seconds as a [Duration]; "without limit" is a century.
Duration _duration(double seconds) {
  const century = Duration(days: 36525);
  if (seconds == double.infinity || seconds > century.inSeconds) {
    return century;
  }
  if (seconds == double.negativeInfinity || seconds < -century.inSeconds) {
    return -century;
  }
  final micros = (seconds * Duration.microsecondsPerSecond).round();
  if (micros == 0 && seconds != 0) {
    return Duration(microseconds: seconds > 0 ? 1 : -1);
  }
  return Duration(microseconds: micros);
}

final RegExp _digits = RegExp(r'^\d+$');
final RegExp _isoTime = RegExp(
  r'^(\d{4})-(\d{2})-(\d{2})'
  r'(?:[T ](\d{2})(?::(\d{2})(?::(\d{2})(?:[.,](\d{1,6}))?)?)?)?'
  r'(Z|[+-]\d{2}:?\d{2})?$',
);

/// Reads milliseconds since the epoch or an ISO 8601 date (local time).
Object _timestamp(String text) {
  if (_digits.hasMatch(text)) {
    final value = int.tryParse(text);
    if (value != null) {
      return value;
    }
  }
  final problem = UsageError(
    'must be a date such as 2026-09-29 or 2026-09-29T08:00, '
    'or milliseconds since the epoch',
  );
  final match = _isoTime.firstMatch(text);
  if (match == null) {
    throw problem;
  }
  int part(int group) => int.parse(match.group(group) ?? '0');
  final year = part(1);
  final month = part(2);
  final day = part(3);
  final hour = part(4);
  final minute = part(5);
  final second = part(6);
  final fraction = (match.group(7) ?? '').padRight(6, '0');
  final micros = int.parse(fraction);
  final date = DateTime.utc(year, month, day);
  final valid = year >= 1 &&
      date.month == month &&
      date.day == day &&
      hour < 24 &&
      minute < 60 &&
      second < 60;
  if (!valid) {
    throw problem;
  }
  final zone = match.group(8);
  if (zone == null) {
    return DateTime(
          year,
          month,
          day,
          hour,
          minute,
          second,
          0,
          micros,
        ).microsecondsSinceEpoch ~/
        1000;
  }
  var offset = Duration.zero;
  if (zone != 'Z') {
    final digits = zone.replaceAll(':', '');
    offset = Duration(
      hours: int.parse(digits.substring(1, 3)),
      minutes: int.parse(digits.substring(3, 5)),
    );
    if (digits.startsWith('-')) {
      offset = -offset;
    }
  }
  final utc = DateTime.utc(year, month, day, hour, minute, second, 0, micros);
  return utc.subtract(offset).microsecondsSinceEpoch ~/ 1000;
}

String _time(Object? milliseconds, {bool seconds = false}) {
  if (milliseconds is! num) {
    return '';
  }
  final DateTime moment;
  try {
    moment = DateTime.fromMicrosecondsSinceEpoch((milliseconds * 1000).round());
  } on Object {
    return _show(milliseconds);
  }
  if (moment.year < 1 || moment.year > 9999) {
    return _show(milliseconds);
  }
  String two(int value) => '$value'.padLeft(2, '0');
  final date = '${'${moment.year}'.padLeft(4, '0')}-${two(moment.month)}-'
      '${two(moment.day)}';
  final time = '${two(moment.hour)}:${two(moment.minute)}';
  return seconds ? '$date $time:${two(moment.second)}' : '$date $time';
}

/// The white space of Python's `str.split()` and `str.rstrip()`.
bool _isSpace(int rune) =>
    (rune >= 0x09 && rune <= 0x0d) ||
    (rune >= 0x1c && rune <= 0x20) ||
    rune == 0x85 ||
    rune == 0xa0 ||
    rune == 0x1680 ||
    (rune >= 0x2000 && rune <= 0x200a) ||
    rune == 0x2028 ||
    rune == 0x2029 ||
    rune == 0x202f ||
    rune == 0x205f ||
    rune == 0x3000;

String _stripRight(String text) {
  final runes = text.runes.toList();
  var end = runes.length;
  while (end > 0 && _isSpace(runes[end - 1])) {
    end -= 1;
  }
  return String.fromCharCodes(runes, 0, end);
}

String _oneLine(Object? text) {
  final words = <List<int>>[];
  var word = <int>[];
  for (final rune in (_truthy(text) ? _show(text) : '').runes) {
    if (_isSpace(rune)) {
      if (word.isNotEmpty) {
        words.add(word);
        word = [];
      }
    } else {
      word.add(rune);
    }
  }
  if (word.isNotEmpty) {
    words.add(word);
  }
  final line = <int>[];
  for (final (index, item) in words.indexed) {
    if (index > 0) {
      line.add(0x20);
    }
    line.addAll(item);
  }
  if (line.length > _textWidth) {
    return '${String.fromCharCodes(line, 0, _textWidth - 1)}…';
  }
  return String.fromCharCodes(line);
}

/// Whether Python would take the value for true.
bool _truthy(Object? value) => switch (value) {
      null || false => false,
      final num number => number != 0,
      final String text => text.isNotEmpty,
      final List<dynamic> list => list.isNotEmpty,
      final Map<dynamic, dynamic> map => map.isNotEmpty,
      _ => true,
    };

String _yesNo(Object? value) => _truthy(value) ? 'yes' : 'no';

/// A value inside a line of text, written the way the Python tool does.
String _show(Object? value) => switch (value) {
      null => 'None',
      true => 'True',
      false => 'False',
      final String text => text,
      final num number => '$number',
      _ => spacedJson(value),
    };

String _capitalize(String text) => text.isEmpty
    ? text
    : text.substring(0, 1).toUpperCase() + text.substring(1).toLowerCase();

Map<String, dynamic> _object(Object? value) =>
    value is Map<String, dynamic> ? value : const {};

void _printJson(CliIo io, Object? value) {
  io.out.writeln(const JsonEncoder.withIndent('  ').convert(value));
}

/// JSON on one line with a space after `,` and `:`.
String spacedJson(Object? value) => switch (value) {
      final List<dynamic> list => '[${list.map(spacedJson).join(', ')}]',
      final Map<dynamic, dynamic> map => '{${[
          for (final entry in map.entries)
            '${jsonEncode('${entry.key}')}: '
                '${spacedJson(entry.value)}'
        ].join(', ')}}',
      _ => jsonEncode(value),
    };

void _printTable(CliIo io, List<String> headers, List<List<Object?>> rows) {
  final table = [
    for (final row in rows)
      [for (final cell in row) cell == null ? '' : _show(cell)],
  ];
  if (table.isEmpty) {
    io.out.writeln('Nothing found.');
    return;
  }
  int length(String text) => text.runes.length;
  final widths = [
    for (final (index, header) in headers.indexed)
      [
        length(header),
        for (final row in table) length(row[index]),
      ].reduce((a, b) => a > b ? a : b),
  ];
  for (final row in [headers, ...table]) {
    final cells = [
      for (final (index, cell) in row.indexed)
        cell + ' ' * (widths[index] - length(cell)),
    ];
    io.out.writeln(_stripRight(cells.join('  ')));
  }
}
