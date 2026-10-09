package app.flutterdev.smsdock;

import java.io.ByteArrayOutputStream;
import java.io.Console;
import java.io.IOException;
import java.io.InputStream;
import java.io.PrintStream;
import java.io.UncheckedIOException;
import java.lang.reflect.Method;
import java.nio.charset.Charset;
import java.nio.charset.StandardCharsets;
import java.time.Duration;
import java.time.Instant;
import java.time.LocalDate;
import java.time.LocalDateTime;
import java.time.OffsetDateTime;
import java.time.ZoneId;
import java.time.format.DateTimeFormatter;
import java.time.format.DateTimeParseException;
import java.util.ArrayList;
import java.util.Arrays;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Locale;
import java.util.Map;
import java.util.TreeSet;
import java.util.function.Function;
import java.util.regex.Pattern;

/**
 * Command-line tool {@code smsdock} for the SMS Dock WebSocket API.
 *
 * <p>Exit codes: 0 success, 1 the server answered with an error, 2 wrong usage, 3 connection
 * or TLS problem.
 */
public final class SmsDockCli {

    /** Exit code: success. */
    public static final int EXIT_OK = 0;

    /** Exit code: the server answered with an error, or the message failed. */
    public static final int EXIT_API_ERROR = 1;

    /** Exit code: wrong usage. */
    public static final int EXIT_USAGE = 2;

    /** Exit code: connection or TLS problem. */
    public static final int EXIT_CONNECTION = 3;

    private static final int TEXT_WIDTH = 60;
    private static final int USAGE_WIDTH = 78;
    private static final Pattern NEGATIVE_NUMBER = Pattern.compile("-\\d+|-\\d*\\.\\d+");
    private static final Pattern DIGITS = Pattern.compile("[0-9]+");
    private static final DateTimeFormatter MINUTES =
        DateTimeFormatter.ofPattern("yyyy-MM-dd HH:mm", Locale.ROOT);
    private static final DateTimeFormatter SECONDS =
        DateTimeFormatter.ofPattern("yyyy-MM-dd HH:mm:ss", Locale.ROOT);
    private static final String DESCRIPTION =
        "Read, send and watch SMS through the SMS Dock app.";
    private static final String EPILOG =
        "The password is read from the environment variable " + SmsDock.PASSWORD_ENV
            + ", never from the command line.";

    /** The command line cannot be carried out as given. */
    static final class UsageError extends RuntimeException {

        UsageError(String message) {
            super(message);
        }
    }

    /** The arguments cannot be read; answered with the usage line. */
    private static final class ParseError extends RuntimeException {

        private final transient Command command;

        ParseError(Command command, String message) {
            super(message);
            this.command = command;
        }
    }

    private enum Kind { FLAG, TEXT, INTEGER, POSITIVE, NUMBER, TIMESTAMP, MANY }

    /** One option of a command. */
    private static final class Option {

        private final String name;
        private final Kind kind;
        private final String metavar;
        private final String help;
        private List<String> choices;
        private boolean required;
        private String group;

        Option(String name, Kind kind, String metavar, String help) {
            this.name = name;
            this.kind = kind;
            this.metavar = metavar;
            this.help = help;
        }

        Option choices(List<String> values) {
            this.choices = values;
            return this;
        }

        Option required() {
            this.required = true;
            return this;
        }

        Option group(String name) {
            this.group = name;
            return this;
        }

        String key() {
            return name.substring(2);
        }

        String invocation() {
            String value = metavar != null ? metavar
                : choices != null ? "{" + String.join(",", choices) + "}"
                : key().toUpperCase(Locale.ROOT).replace('-', '_');
            switch (kind) {
                case FLAG:
                    return name;
                case MANY:
                    return name + " " + value + " [" + value + " ...]";
                default:
                    return name + " " + value;
            }
        }
    }

    /** One command with its positional argument and options. */
    private static final class Command {

        private final String name;
        private final String help;
        private final String description;
        private final String positional;
        private final String positionalDisplay;
        private final String positionalHelp;
        private final List<Option> options = new ArrayList<>();

        Command(String name, String help, String description, String positional,
                String positionalDisplay, String positionalHelp) {
            this.name = name;
            this.help = help;
            this.description = description;
            this.positional = positional;
            this.positionalDisplay = positionalDisplay;
            this.positionalHelp = positionalHelp;
        }

        Command add(Option option) {
            options.add(option);
            return this;
        }

        Option find(String name) {
            for (Option option : options) {
                if (option.name.equals(name)) {
                    return option;
                }
            }
            return null;
        }
    }

    private static final List<Command> COMMANDS = commands();

    private final Function<String, String> environment;
    private final InputStream input;
    private final Charset inputCharset;
    private final PrintStream out;
    private final PrintStream err;
    private final Function<String, String> passwordPrompt;

    /**
     * Creates the tool with its surroundings.
     *
     * @param environment returns the value of an environment variable, or {@code null}
     * @param input standard input
     * @param inputCharset encoding of standard input
     * @param out standard output
     * @param err standard error
     * @param passwordPrompt asks for the password without showing it; {@code null} when
     *     the tool does not run in a terminal
     */
    SmsDockCli(
            Function<String, String> environment,
            InputStream input,
            Charset inputCharset,
            PrintStream out,
            PrintStream err,
            Function<String, String> passwordPrompt) {
        this.environment = environment;
        this.input = input;
        this.inputCharset = inputCharset;
        this.out = out;
        this.err = err;
        this.passwordPrompt = passwordPrompt;
    }

    /**
     * Runs the command line and ends the program with its exit code.
     *
     * @param args the arguments
     */
    public static void main(String[] args) {
        Console console = terminal();
        Function<String, String> prompt = console == null ? null : text -> {
            char[] typed = console.readPassword("%s", text);
            return typed == null ? null : new String(typed);
        };
        SmsDockCli tool = new SmsDockCli(
            System::getenv, System.in, inputCharset(), System.out, System.err, prompt);
        int code = tool.run(args);
        System.out.flush();
        System.exit(code);
    }

    /**
     * Runs the command line and returns the exit code.
     *
     * @param args the arguments
     * @return 0, 1, 2 or 3; see the class description
     */
    int run(String... args) {
        try {
            return dispatch(args);
        } catch (ParseError error) {
            err.println(usage(error.command));
            String program = error.command == null ? "smsdock" : "smsdock " + error.command.name;
            err.println(program + ": error: " + error.getMessage());
            return EXIT_USAGE;
        } catch (ApiException error) {
            complain(error);
            return EXIT_API_ERROR;
        } catch (UsageError | IllegalArgumentException error) {
            complain(error);
            return EXIT_USAGE;
        } catch (SmsDockException | SmsDockTimeoutException | UncheckedIOException error) {
            complain(error);
            return EXIT_CONNECTION;
        }
    }

    private void complain(Throwable error) {
        err.println("smsdock: " + error.getMessage());
    }

    private int dispatch(String[] args) {
        if (args.length > 0 && isHelp(args[0])) {
            out.println(rootHelp());
            return EXIT_OK;
        }
        if (args.length == 0) {
            throw new ParseError(null, "the following arguments are required: command");
        }
        if (looksLikeOption(args[0])) {
            throw new ParseError(null, "unrecognized arguments: " + args[0]);
        }
        Command command = null;
        for (Command candidate : COMMANDS) {
            if (candidate.name.equals(args[0])) {
                command = candidate;
            }
        }
        if (command == null) {
            List<String> names = new ArrayList<>();
            COMMANDS.forEach(candidate -> names.add(candidate.name));
            throw new ParseError(null, "argument command: invalid choice: '" + args[0]
                + "' (choose from " + String.join(", ", names) + ")");
        }
        Map<String, Object> values = parse(command, Arrays.copyOfRange(args, 1, args.length));
        if (values == null) {
            out.println(commandHelp(command));
            return EXIT_OK;
        }
        if (command.name.equals("fingerprint")) {
            return fingerprint(values);
        }
        try (SmsDockClient client = client(values)) {
            client.connect();
            switch (command.name) {
                case "status":
                    return status(client, values);
                case "conversations":
                    return conversations(client, values);
                case "messages":
                    return messages(client, values);
                case "send":
                    return send(client, values);
                case "delete":
                    return delete(client, values);
                case "mark-read":
                    return markRead(client, values);
                case "contacts":
                    return contacts(client, values);
                default:
                    return listen(client, values);
            }
        }
    }

    // ------------------------------------------------------------------
    // Arguments
    // ------------------------------------------------------------------

    private static List<Command> commands() {
        List<Command> commands = new ArrayList<>();
        commands.add(new Command(
            "fingerprint", "print the certificate fingerprint of a server",
            "Print the SHA-256 fingerprint of the certificate the server presents. Compare "
                + "it with the Server screen of the app before you use it.",
            "host", "HOST[:PORT]",
            "address of the phone; the default port is " + SmsDock.DEFAULT_API_PORT)
            .add(new Option("--timeout", Kind.NUMBER, "SECONDS",
                "seconds to wait (default: 10)")));
        commands.add(connected("status", "show phone, SIMs, permissions and licence", null));
        commands.add(connected("conversations", "list conversations, newest first", null)
            .add(new Option("--search", Kind.TEXT, "TEXT",
                "search text, numbers and contact names"))
            .add(new Option("--unread", Kind.FLAG, null,
                "only conversations with unread messages"))
            .add(new Option("--sim", Kind.INTEGER, "ID", "only this subscription id"))
            .add(new Option("--limit", Kind.POSITIVE, "N",
                "number of conversations (default: 50)")));
        commands.add(connected("messages", "list messages, newest first", null)
            .add(new Option("--address", Kind.TEXT, "NUMBER",
                "only messages from or to this number"))
            .add(new Option("--conversation", Kind.INTEGER, "ID", "only this conversation"))
            .add(new Option("--direction", Kind.TEXT, null, "incoming, outgoing or both")
                .choices(List.of("in", "out", "all")))
            .add(new Option("--search", Kind.TEXT, "TEXT", "search the text"))
            .add(new Option("--since", Kind.TIMESTAMP, "TIME",
                "not older than this: 2026-09-29, 2026-09-29T08:00 or milliseconds"))
            .add(new Option("--until", Kind.TIMESTAMP, "TIME", "not newer than this"))
            .add(new Option("--unread", Kind.FLAG, null, "only unread messages"))
            .add(new Option("--sim", Kind.INTEGER, "ID", "only this subscription id"))
            .add(new Option("--limit", Kind.POSITIVE, "N",
                "number of messages (default: 50)")));
        commands.add(connected("send", "send an SMS",
            "Send an SMS. Without --text the text is read from standard input.")
            .add(new Option("--to", Kind.TEXT, "NUMBER", "phone number of the recipient")
                .required())
            .add(new Option("--text", Kind.TEXT, null, "text of the message"))
            .add(new Option("--sim", Kind.INTEGER, "ID", "subscription id of the SIM"))
            .add(new Option("--request-id", Kind.TEXT, "ID",
                "your own id; the same id is sent only once"))
            .add(new Option("--wait", Kind.FLAG, null,
                "wait until the message was sent or failed"))
            .add(new Option("--wait-timeout", Kind.NUMBER, "SECONDS",
                "longest time to wait with --wait (default: 60)")));
        commands.add(connected("delete", "delete a message or a conversation", null)
            .add(new Option("--message", Kind.INTEGER, "ID", "id of the message")
                .group("target"))
            .add(new Option("--conversation", Kind.INTEGER, "ID", "id of the conversation")
                .group("target")));
        commands.add(connected("mark-read", "mark a conversation as read",
            "Mark all messages of a conversation as read. SMS Dock must be the default SMS "
                + "app of the phone.")
            .add(new Option("--conversation", Kind.INTEGER, "ID", "id of the conversation")
                .required()));
        commands.add(connected("contacts", "list the contacts of the phone", null)
            .add(new Option("--search", Kind.TEXT, "TEXT", "search names and numbers"))
            .add(new Option("--limit", Kind.POSITIVE, "N",
                "number of contacts (default: 50)")));
        List<String> eventChoices = new ArrayList<>(SmsDock.EVENT_NAMES);
        eventChoices.add("*");
        commands.add(connected("listen", "print incoming SMS as they arrive",
            "Print events as they arrive until Ctrl+C.")
            .add(new Option("--address", Kind.TEXT, "NUMBER",
                "only SMS events of this number"))
            .add(new Option("--events", Kind.MANY, "EVENT",
                "events to print (default: sms.received); * means all")
                .choices(eventChoices))
            .add(new Option("--count", Kind.POSITIVE, "N", "stop after this many events")));
        return commands;
    }

    private static Command connected(String name, String help, String description) {
        return new Command(name, help, description, "url", "url",
            "wss:// URL shown by the app, e.g. wss://192.168.1.50:9443/ws")
            .add(new Option("--fingerprint", Kind.TEXT, "HEX",
                "SHA-256 fingerprint of the certificate as shown by the app (default: "
                    + "environment variable " + SmsDock.FINGERPRINT_ENV + ")"))
            .add(new Option("--insecure", Kind.FLAG, null,
                "do not check the certificate (for tests only)"))
            .add(new Option("--password-env", Kind.TEXT, "NAME",
                "environment variable with the password (default: "
                    + SmsDock.PASSWORD_ENV + ")"))
            .add(new Option("--timeout", Kind.NUMBER, "SECONDS",
                "seconds to wait for a response (default: 30)"))
            .add(new Option("--reconnect-attempts", Kind.INTEGER, "N",
                "reconnect attempts after a lost connection; -1 means without limit "
                    + "(default: 5)"))
            .add(new Option("--json", Kind.FLAG, null, "print JSON instead of text"));
    }

    /** Reads the arguments of a command; returns {@code null} when help was asked for. */
    private static Map<String, Object> parse(Command command, String[] args) {
        Map<String, Object> values = new LinkedHashMap<>();
        List<String> positionals = new ArrayList<>();
        List<String> extras = new ArrayList<>();
        Map<String, Option> groups = new LinkedHashMap<>();
        int index = 0;
        while (index < args.length) {
            String argument = args[index++];
            if (argument.equals("--")) {
                while (index < args.length) {
                    positionals.add(args[index++]);
                }
                break;
            }
            if (isHelp(argument)) {
                return null;
            }
            if (!looksLikeOption(argument)) {
                positionals.add(argument);
                continue;
            }
            String name = argument;
            String inline = null;
            int equals = argument.indexOf('=');
            if (argument.startsWith("--") && equals > 0) {
                name = argument.substring(0, equals);
                inline = argument.substring(equals + 1);
            }
            Option option = command.find(name);
            if (option == null) {
                extras.add(argument);
                continue;
            }
            if (option.group != null) {
                Option other = groups.putIfAbsent(option.group, option);
                if (other != null && other != option) {
                    throw new ParseError(command, "argument " + option.name
                        + ": not allowed with argument " + other.name);
                }
            }
            if (option.kind == Kind.FLAG) {
                if (inline != null) {
                    throw new ParseError(command, "argument " + option.name
                        + ": ignored explicit argument '" + inline + "'");
                }
                values.put(option.key(), Boolean.TRUE);
            } else if (option.kind == Kind.MANY) {
                List<Object> items = new ArrayList<>();
                if (inline != null) {
                    items.add(convert(command, option, inline));
                }
                while (inline == null && index < args.length
                        && !looksLikeOption(args[index])) {
                    items.add(convert(command, option, args[index++]));
                }
                if (items.isEmpty()) {
                    throw new ParseError(command,
                        "argument " + option.name + ": expected at least one argument");
                }
                values.put(option.key(), items);
            } else {
                String text = inline;
                if (text == null) {
                    if (index >= args.length || looksLikeOption(args[index])) {
                        throw new ParseError(command,
                            "argument " + option.name + ": expected one argument");
                    }
                    text = args[index++];
                }
                values.put(option.key(), convert(command, option, text));
            }
        }
        List<String> missing = new ArrayList<>();
        if (positionals.isEmpty()) {
            missing.add(command.positionalDisplay);
        } else {
            values.put(command.positional, positionals.get(0));
            extras.addAll(positionals.subList(1, positionals.size()));
        }
        for (Option option : command.options) {
            if (option.required && !values.containsKey(option.key())) {
                missing.add(option.name);
            }
        }
        if (!missing.isEmpty()) {
            throw new ParseError(command,
                "the following arguments are required: " + String.join(", ", missing));
        }
        List<String> grouped = new ArrayList<>();
        for (Option option : command.options) {
            if (option.group != null) {
                grouped.add(option.name);
            }
        }
        if (!grouped.isEmpty() && groups.isEmpty()) {
            throw new ParseError(command,
                "one of the arguments " + String.join(" ", grouped) + " is required");
        }
        if (!extras.isEmpty()) {
            throw new ParseError(null, "unrecognized arguments: " + String.join(" ", extras));
        }
        return values;
    }

    private static Object convert(Command command, Option option, String text) {
        String prefix = "argument " + option.name + ": ";
        if (option.choices != null && !option.choices.contains(text)) {
            throw new ParseError(command, prefix + "invalid choice: '" + text
                + "' (choose from " + String.join(", ", option.choices) + ")");
        }
        switch (option.kind) {
            case INTEGER:
                try {
                    return Long.valueOf(text.strip());
                } catch (NumberFormatException error) {
                    throw new ParseError(command, prefix + "invalid int value: '" + text + "'");
                }
            case POSITIVE:
                long number;
                try {
                    number = Long.parseLong(text.strip());
                } catch (NumberFormatException error) {
                    number = 0;
                }
                if (number < 1) {
                    throw new ParseError(command, prefix + "must be a number from 1");
                }
                return number;
            case NUMBER:
                try {
                    double value = Double.parseDouble(text.strip());
                    if (!Double.isNaN(value) && !Double.isInfinite(value)) {
                        return value;
                    }
                } catch (NumberFormatException error) {
                    // Reported below.
                }
                throw new ParseError(command, prefix + "invalid float value: '" + text + "'");
            case TIMESTAMP:
                Long moment = timestamp(text);
                if (moment == null) {
                    throw new ParseError(command, prefix
                        + "must be a date such as 2026-09-29 or 2026-09-29T08:00, "
                        + "or milliseconds since the epoch");
                }
                return moment;
            default:
                return text;
        }
    }

    private static boolean isHelp(String argument) {
        return argument.equals("-h") || argument.equals("--help");
    }

    private static boolean looksLikeOption(String argument) {
        return argument.length() > 1
            && argument.startsWith("-")
            && !NEGATIVE_NUMBER.matcher(argument).matches()
            && argument.indexOf(' ') < 0;
    }

    /** Reads milliseconds since the epoch or an ISO 8601 date (local time). */
    static Long timestamp(String text) {
        if (DIGITS.matcher(text).matches()) {
            try {
                return Long.valueOf(text);
            } catch (NumberFormatException error) {
                return null;
            }
        }
        String value = text.strip().replaceFirst("^(\\d{4}-\\d{2}-\\d{2}) ", "$1T");
        ZoneId zone = ZoneId.systemDefault();
        try {
            return LocalDate.parse(value).atStartOfDay(zone).toInstant().toEpochMilli();
        } catch (DateTimeParseException error) {
            // Not a plain date.
        }
        try {
            return LocalDateTime.parse(value).atZone(zone).toInstant().toEpochMilli();
        } catch (DateTimeParseException error) {
            // Not a local date and time.
        }
        try {
            return OffsetDateTime.parse(value).toInstant().toEpochMilli();
        } catch (DateTimeParseException error) {
            return null;
        }
    }

    /** Returns the usage line, wrapped to the width of a terminal like argparse does. */
    private static String usage(Command command) {
        String program = "smsdock";
        List<String> optional = new ArrayList<>(List.of("[-h]"));
        String positional = "{" + commandNames() + "} ...";
        if (command != null) {
            program = "smsdock " + command.name;
            positional = command.positionalDisplay;
            List<String> group = new ArrayList<>();
            for (Option option : command.options) {
                if (option.group != null) {
                    group.add(option.invocation());
                } else if (option.required) {
                    optional.add(option.invocation());
                } else {
                    optional.add("[" + option.invocation() + "]");
                }
            }
            if (!group.isEmpty()) {
                optional.add("(" + String.join(" | ", group) + ")");
            }
        }
        String prefix = "usage: " + program;
        String single = prefix + " " + String.join(" ", optional) + " " + positional;
        if (single.length() <= USAGE_WIDTH) {
            return single;
        }
        String indent = " ".repeat(prefix.length() + 1);
        StringBuilder text = new StringBuilder(prefix);
        int length = prefix.length();
        for (String part : optional) {
            if (length + 1 + part.length() > USAGE_WIDTH) {
                text.append('\n').append(indent.substring(1));
                length = indent.length() - 1;
            }
            text.append(' ').append(part);
            length += part.length() + 1;
        }
        return text.append('\n').append(indent).append(positional).toString();
    }

    private static String commandNames() {
        List<String> names = new ArrayList<>();
        COMMANDS.forEach(command -> names.add(command.name));
        return String.join(",", names);
    }

    private static String rootHelp() {
        StringBuilder text = new StringBuilder(usage(null)).append("\n\n")
            .append(DESCRIPTION).append("\n\npositional arguments:\n  {")
            .append(commandNames()).append("}\n");
        for (Command command : COMMANDS) {
            text.append(helpLine("  " + command.name, command.help));
        }
        text.append("\noptions:\n")
            .append(helpLine("-h, --help", "show this help message and exit"))
            .append('\n').append(EPILOG);
        return text.toString();
    }

    private static String commandHelp(Command command) {
        StringBuilder text = new StringBuilder(usage(command)).append("\n\n");
        if (command.description != null) {
            text.append(command.description).append("\n\n");
        }
        text.append("positional arguments:\n")
            .append(helpLine(command.positionalDisplay, command.positionalHelp))
            .append("\noptions:\n")
            .append(helpLine("-h, --help", "show this help message and exit"));
        for (Option option : command.options) {
            text.append(helpLine(option.invocation(), option.help));
        }
        return text.toString().stripTrailing();
    }

    private static String helpLine(String invocation, String help) {
        String start = "  " + invocation;
        if (start.length() > 22) {
            return start + "\n" + " ".repeat(24) + help + "\n";
        }
        return start + " ".repeat(24 - start.length()) + help + "\n";
    }

    // ------------------------------------------------------------------
    // Commands
    // ------------------------------------------------------------------

    private SmsDockClient client(Map<String, Object> args) {
        boolean insecure = flag(args, "insecure");
        String fingerprint = (String) args.get("fingerprint");
        if (fingerprint == null) {
            fingerprint = environment.apply(SmsDock.FINGERPRINT_ENV);
        }
        if (!insecure && (fingerprint == null || fingerprint.isEmpty())) {
            throw new UsageError(
                "No certificate fingerprint. Pass --fingerprint or set "
                    + SmsDock.FINGERPRINT_ENV + ". 'smsdock fingerprint HOST' prints the "
                    + "fingerprint of a server.");
        }
        long attempts = (Long) args.getOrDefault("reconnect-attempts", 5L);
        if (attempts < -1) {
            throw new UsageError("--reconnect-attempts cannot be less than -1");
        }
        String variable = (String) args.getOrDefault("password-env", SmsDock.PASSWORD_ENV);
        SmsDockClient.Builder builder = SmsDockClient.builder((String) args.get("url"))
            .environment(environment)
            .password(password(variable))
            .fingerprint(fingerprint)
            .insecure(insecure)
            .timeout(seconds((Double) args.getOrDefault("timeout", 30.0)));
        if (attempts == -1) {
            builder.reconnectWithoutLimit();
        } else {
            builder.reconnectAttempts((int) Math.min(attempts, Integer.MAX_VALUE));
        }
        return builder.build();
    }

    private String password(String variable) {
        String password = environment.apply(variable);
        if (password != null && !password.isEmpty()) {
            return password;
        }
        if (passwordPrompt != null) {
            password = passwordPrompt.apply("SMS Dock password: ");
            if (password != null && !password.isEmpty()) {
                return password;
            }
        }
        throw new UsageError("No password. Set the environment variable " + variable + ".");
    }

    private int fingerprint(Map<String, Object> args) {
        Object[] address = hostAndPort((String) args.get("host"));
        Duration timeout = seconds((Double) args.getOrDefault("timeout", 10.0));
        out.println(SmsDock.fetchFingerprint((String) address[0], (Integer) address[1], timeout));
        err.println("Compare this fingerprint with the Server screen of the app. "
            + "Use it only if both are the same.");
        return EXIT_OK;
    }

    private int status(SmsDockClient client, Map<String, Object> args) {
        Map<String, Object> status = client.status();
        if (flag(args, "json")) {
            out.println(Json.pretty(status));
            return EXIT_OK;
        }
        Map<?, ?> app = map(status.get("app"));
        Map<?, ?> licence = map(status.get("licence"));
        TreeSet<String> missing = new TreeSet<>();
        map(status.get("permissions")).forEach((name, granted) -> {
            if (!truthy(granted)) {
                missing.add(String.valueOf(name));
            }
        });
        String licenceText;
        if (truthy(licence.get("licensed"))) {
            licenceText = "full licence";
        } else if (!truthy(licence.get("enforced"))) {
            licenceText = "not enforced";
        } else {
            licenceText = "free version, " + text(licence.get("sentToday")) + " of "
                + text(licence.get("dailyLimit")) + " SMS sent today";
        }
        String[][] lines = {
            {"App", text(app.get("name")) + " " + text(app.get("version")) + " (protocol "
                + text(app.get("protocolVersion")) + ")"},
            {"Default SMS app", yesNo(status.get("defaultSmsApp"))},
            {"Delete mode", text(status.get("deleteMode"))},
            {"Licence", licenceText},
            {"Permissions", missing.isEmpty() ? "all granted"
                : "missing: " + String.join(", ", missing)},
        };
        for (String[] line : lines) {
            out.println(String.format("%-17s%s", line[0] + ":", line[1]));
        }
        Object sims = status.get("sims");
        if (sims instanceof List && !((List<?>) sims).isEmpty()) {
            out.println();
            List<Object[]> rows = new ArrayList<>();
            for (Object entry : (List<?>) sims) {
                Map<?, ?> sim = map(entry);
                rows.add(new Object[] {sim.get("subscriptionId"), sim.get("slot"),
                    sim.get("name"), sim.get("number"), yesNo(sim.get("isDefault"))});
            }
            printTable(new String[] {"SIM", "SLOT", "NAME", "NUMBER", "DEFAULT"}, rows);
        }
        return EXIT_OK;
    }

    private int conversations(SmsDockClient client, Map<String, Object> args) {
        ConversationFilter filter = new ConversationFilter()
            .query((String) args.get("search"))
            .unread(flag(args, "unread"));
        if (args.get("sim") != null) {
            filter.sim((Long) args.get("sim"));
        }
        long limit = (Long) args.getOrDefault("limit", 50L);
        List<Map<String, Object>> items = collect(
            client.iterConversations(filter, (int) Math.min(limit, 200), count(limit)));
        if (flag(args, "json")) {
            out.println(Json.pretty(items));
            return EXIT_OK;
        }
        List<Object[]> rows = new ArrayList<>();
        for (Map<String, Object> item : items) {
            rows.add(new Object[] {item.get("id"), time(item.get("timestamp"), false),
                item.get("address"), item.get("contact"), item.get("messageCount"),
                item.get("unreadCount"), item.get("direction"), oneLine(item.get("snippet"))});
        }
        printTable(new String[] {
            "ID", "TIME", "NUMBER", "NAME", "MSGS", "UNREAD", "DIR", "TEXT"}, rows);
        return EXIT_OK;
    }

    private int messages(SmsDockClient client, Map<String, Object> args) {
        MessageFilter filter = new MessageFilter()
            .address((String) args.get("address"))
            .direction((String) args.getOrDefault("direction", "all"))
            .query((String) args.get("search"))
            .unread(flag(args, "unread"));
        if (args.get("conversation") != null) {
            filter.conversationId((Long) args.get("conversation"));
        }
        if (args.get("since") != null) {
            filter.since((Long) args.get("since"));
        }
        if (args.get("until") != null) {
            filter.until((Long) args.get("until"));
        }
        if (args.get("sim") != null) {
            filter.sim((Long) args.get("sim"));
        }
        long limit = (Long) args.getOrDefault("limit", 50L);
        List<Map<String, Object>> items = collect(
            client.iterMessages(filter, (int) Math.min(limit, 200), count(limit)));
        if (flag(args, "json")) {
            out.println(Json.pretty(items));
            return EXIT_OK;
        }
        List<Object[]> rows = new ArrayList<>();
        for (Map<String, Object> item : items) {
            rows.add(new Object[] {item.get("id"), time(item.get("timestamp"), false),
                item.get("direction"), item.get("address"), item.get("contact"),
                item.get("status"), oneLine(item.get("body"))});
        }
        printTable(new String[] {
            "ID", "TIME", "DIR", "NUMBER", "NAME", "STATUS", "TEXT"}, rows);
        return EXIT_OK;
    }

    private int send(SmsDockClient client, Map<String, Object> args) {
        String text = (String) args.get("text");
        if (text == null) {
            if (passwordPrompt != null) {
                throw new UsageError("No text. Pass --text or write it to standard input.");
            }
            text = readInput();
            if (text.endsWith("\n")) {
                text = text.substring(0, text.length() - 1);
            }
        }
        if (text.isEmpty()) {
            throw new UsageError("The text is empty.");
        }
        Map<String, Object> result = client.send(
            (String) args.get("to"), text, (Long) args.get("sim"),
            (String) args.get("request-id"));
        if (flag(args, "wait")) {
            result = client.waitForResult(
                (String) result.get("requestId"),
                seconds((Double) args.getOrDefault("wait-timeout", 60.0)));
        }
        // Also without --wait: a repeated request id tells the old outcome.
        boolean failed = "failed".equals(result.get("status"));
        if (flag(args, "json")) {
            out.println(Json.pretty(result));
        } else if (failed) {
            Object code = result.get("errorCode");
            String cause = code == null ? "" : "error code " + text(code) + ", ";
            err.println("smsdock: Sending to " + text(result.get("to")) + " failed (" + cause
                + "requestId " + text(result.get("requestId")) + ")");
        } else {
            out.println(capitalize(text(result.get("status"))) + ": " + text(result.get("to"))
                + ", " + text(result.get("parts")) + " part(s), requestId "
                + text(result.get("requestId")));
        }
        return failed ? EXIT_API_ERROR : EXIT_OK;
    }

    private int delete(SmsDockClient client, Map<String, Object> args) {
        Map<String, Object> result = args.get("message") != null
            ? client.deleteMessage((Long) args.get("message"))
            : client.deleteConversation((Long) args.get("conversation"));
        if (flag(args, "json")) {
            out.println(Json.pretty(result));
            return EXIT_OK;
        }
        String where = "provider".equals(result.get("mode"))
            ? "removed from the phone" : "hidden in SMS Dock, still on the phone";
        out.println("Deleted " + text(result.get("deleted")) + " message(s): " + where);
        return EXIT_OK;
    }

    private int markRead(SmsDockClient client, Map<String, Object> args) {
        long conversation = (Long) args.get("conversation");
        Map<String, Object> result = client.markRead(conversation);
        if (flag(args, "json")) {
            out.println(Json.pretty(result));
        } else {
            out.println("Marked conversation " + conversation + " as read");
        }
        return EXIT_OK;
    }

    private int contacts(SmsDockClient client, Map<String, Object> args) {
        long limit = (Long) args.getOrDefault("limit", 50L);
        List<Map<String, Object>> items = collect(client.iterContacts(
            (String) args.get("search"), (int) Math.min(limit, 200), count(limit)));
        if (flag(args, "json")) {
            out.println(Json.pretty(items));
            return EXIT_OK;
        }
        List<Object[]> rows = new ArrayList<>();
        for (Map<String, Object> contact : items) {
            Object numbers = contact.get("numbers");
            List<?> list = numbers instanceof List && !((List<?>) numbers).isEmpty()
                ? (List<?>) numbers : List.of(Map.of());
            for (Object entry : list) {
                Map<?, ?> number = map(entry);
                rows.add(new Object[] {
                    contact.get("name"), number.get("number"), number.get("label")});
            }
        }
        printTable(new String[] {"NAME", "NUMBER", "LABEL"}, rows);
        return EXIT_OK;
    }

    private int listen(SmsDockClient client, Map<String, Object> args) {
        List<String> names = new ArrayList<>();
        Object chosen = args.get("events");
        if (chosen instanceof List) {
            ((List<?>) chosen).forEach(name -> names.add(String.valueOf(name)));
        } else {
            names.add("sms.received");
        }
        boolean json = flag(args, "json");
        Long limit = (Long) args.get("count");
        client.setOnReconnect(this::reportReconnect);
        client.subscribe(names, (String) args.get("address"));
        if (!json) {
            err.println("Listening for " + String.join(", ", names)
                + ". Press Ctrl+C to stop.");
        }
        long count = 0;
        for (Map<String, Object> event : client.events()) {
            out.println(json ? Json.line(event) : describe(event));
            out.flush();
            count++;
            // checkError: the reader of a pipe is gone.
            if ((limit != null && count >= limit) || out.checkError()) {
                break;
            }
        }
        return EXIT_OK;
    }

    /** Tells on standard error what a lost connection may have cost. */
    private void reportReconnect(SmsDockClient client) {
        String lost;
        Long missed = client.getMissedEvents();
        if (client.isServerRestarted()) {
            lost = "the app was restarted, events may be missing";
        } else if (missed != null && missed != 0) {
            lost = missed + " event(s) of the phone were not received";
        } else {
            lost = "no event was missed";
        }
        err.println("smsdock: reconnected; " + lost);
        err.flush();
    }

    /** Returns one event as a line of text. */
    static String describe(Map<String, Object> event) {
        Object name = event.get("event");
        Map<?, ?> data = map(event.get("data"));
        Map<?, ?> message = map(data.get("message"));
        if ("sms.received".equals(name)) {
            String sender = text(message.get("address"));
            if (truthy(message.get("contact"))) {
                sender = sender + " (" + text(message.get("contact")) + ")";
            }
            return time(message.get("timestamp"), true) + "  " + sender + ": "
                + text(message.get("body"));
        }
        String now = time(System.currentTimeMillis(), true);
        if ("sms.sent".equals(name) && !message.isEmpty()) {
            return now + "  sent to " + text(message.get("address")) + ": "
                + text(message.get("body"));
        }
        List<String> details = new ArrayList<>();
        data.forEach((key, value) -> {
            if (!(value instanceof Map) && !(value instanceof List)) {
                details.add(key + "=" + text(value));
            }
        });
        return (now + "  " + text(name) + " " + String.join(" ", details)).stripTrailing();
    }

    /**
     * Splits {@code HOST}, {@code HOST:PORT}, {@code [IPv6]:PORT} or a {@code wss://} URL.
     *
     * @return the host and the port
     */
    static Object[] hostAndPort(String text) {
        UsageError wrong = new UsageError("'" + text + "' is not HOST or HOST:PORT");
        String value = text.strip();
        boolean url = value.contains("://");
        if (url) {
            value = value.substring(value.indexOf("://") + 3);
        }
        value = value.split("[/?#]", 2)[0];
        value = value.substring(value.lastIndexOf('@') + 1);
        String host;
        String port;
        if (value.startsWith("[")) {
            int end = value.indexOf(']');
            if (end < 0) {
                throw wrong;
            }
            host = value.substring(1, end);
            String rest = value.substring(end + 1);
            if (!rest.isEmpty() && !rest.startsWith(":")) {
                throw wrong;
            }
            port = rest.isEmpty() ? "" : rest.substring(1);
        } else if (!url && value.chars().filter(character -> character == ':').count() > 1) {
            host = value;
            port = "";
        } else if (value.contains(":")) {
            host = value.substring(0, value.lastIndexOf(':'));
            port = value.substring(value.lastIndexOf(':') + 1);
        } else {
            host = value;
            port = "";
        }
        int number = SmsDock.DEFAULT_API_PORT;
        if (!port.isEmpty()) {
            if (!DIGITS.matcher(port).matches() || port.length() > 5
                    || Integer.parseInt(port) > 65535) {
                throw wrong;
            }
            number = Integer.parseInt(port) == 0 ? number : Integer.parseInt(port);
        }
        if (host.isEmpty()) {
            throw wrong;
        }
        return new Object[] {host.toLowerCase(Locale.ROOT), number};
    }

    // ------------------------------------------------------------------
    // Output
    // ------------------------------------------------------------------

    private void printTable(String[] headers, List<Object[]> rows) {
        if (rows.isEmpty()) {
            out.println("Nothing found.");
            return;
        }
        List<String[]> table = new ArrayList<>();
        table.add(headers);
        for (Object[] row : rows) {
            String[] cells = new String[row.length];
            for (int index = 0; index < row.length; index++) {
                cells[index] = row[index] == null ? "" : text(row[index]);
            }
            table.add(cells);
        }
        int[] widths = new int[headers.length];
        for (String[] row : table) {
            for (int index = 0; index < widths.length; index++) {
                widths[index] = Math.max(widths[index], length(row[index]));
            }
        }
        for (String[] row : table) {
            StringBuilder line = new StringBuilder();
            for (int index = 0; index < widths.length; index++) {
                if (index > 0) {
                    line.append("  ");
                }
                line.append(row[index]);
                line.append(" ".repeat(widths[index] - length(row[index])));
            }
            out.println(line.toString().stripTrailing());
        }
    }

    private String readInput() {
        try {
            ByteArrayOutputStream bytes = new ByteArrayOutputStream();
            input.transferTo(bytes);
            return new String(bytes.toByteArray(), inputCharset);
        } catch (IOException error) {
            throw new UncheckedIOException("Cannot read standard input: " + error.getMessage(),
                error);
        }
    }

    private static List<Map<String, Object>> collect(Iterable<Map<String, Object>> items) {
        List<Map<String, Object>> list = new ArrayList<>();
        items.forEach(list::add);
        return list;
    }

    private static int count(long limit) {
        return (int) Math.min(limit, Integer.MAX_VALUE);
    }

    private static boolean flag(Map<String, Object> args, String name) {
        return Boolean.TRUE.equals(args.get(name));
    }

    private static Duration seconds(double seconds) {
        return Duration.ofNanos((long) Math.min(seconds * 1e9, Long.MAX_VALUE / 4.0));
    }

    private static Map<?, ?> map(Object value) {
        return value instanceof Map ? (Map<?, ?>) value : Map.of();
    }

    /** Whether a JSON value counts as set: not null, false, 0 or empty. */
    private static boolean truthy(Object value) {
        if (value == null) {
            return false;
        }
        if (value instanceof Boolean) {
            return (Boolean) value;
        }
        if (value instanceof Number) {
            return ((Number) value).doubleValue() != 0;
        }
        if (value instanceof CharSequence) {
            return ((CharSequence) value).length() > 0;
        }
        if (value instanceof List) {
            return !((List<?>) value).isEmpty();
        }
        if (value instanceof Map) {
            return !((Map<?, ?>) value).isEmpty();
        }
        return true;
    }

    /** Text of a JSON value, written as the Python tool does: None, True, False. */
    private static String text(Object value) {
        if (value == null) {
            return "None";
        }
        if (value instanceof Boolean) {
            return (Boolean) value ? "True" : "False";
        }
        return String.valueOf(value);
    }

    private static String yesNo(Object value) {
        return truthy(value) ? "yes" : "no";
    }

    private static String capitalize(String value) {
        if (value.isEmpty()) {
            return value;
        }
        int first = value.offsetByCodePoints(0, 1);
        return value.substring(0, first).toUpperCase(Locale.ROOT)
            + value.substring(first).toLowerCase(Locale.ROOT);
    }

    private static int length(String value) {
        return value.codePointCount(0, value.length());
    }

    private static String time(Object milliseconds, boolean seconds) {
        if (!(milliseconds instanceof Number)) {
            return "";
        }
        try {
            long value = milliseconds instanceof Double
                ? Math.round((Double) milliseconds) : ((Number) milliseconds).longValue();
            return Instant.ofEpochMilli(value).atZone(ZoneId.systemDefault())
                .format(seconds ? SECONDS : MINUTES);
        } catch (RuntimeException error) {
            return String.valueOf(milliseconds);
        }
    }

    private static String oneLine(Object text) {
        String value = text == null ? "" : String.valueOf(text);
        String line = String.join(" ", Arrays.stream(value.strip().split("(?U)\\s+"))
            .filter(word -> !word.isEmpty()).toArray(String[]::new));
        if (length(line) > TEXT_WIDTH) {
            line = line.substring(0, line.offsetByCodePoints(0, TEXT_WIDTH - 1)) + "…";
        }
        return line;
    }

    /** The console when the tool runs in a terminal, else {@code null}. */
    private static Console terminal() {
        Console console = System.console();
        if (console == null) {
            return null;
        }
        try {
            Method isTerminal = Console.class.getMethod("isTerminal");
            if (!Boolean.TRUE.equals(isTerminal.invoke(console))) {
                return null;
            }
        } catch (ReflectiveOperationException error) {
            // Before Java 22 a console exists only in a terminal.
        }
        return console;
    }

    private static Charset inputCharset() {
        for (String property : new String[] {"stdin.encoding", "native.encoding"}) {
            String name = System.getProperty(property);
            try {
                if (name != null && !name.isEmpty()) {
                    return Charset.forName(name);
                }
            } catch (RuntimeException error) {
                // Try the next one.
            }
        }
        return StandardCharsets.UTF_8;
    }
}
