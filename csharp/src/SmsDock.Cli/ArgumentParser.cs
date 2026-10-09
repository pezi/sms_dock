using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;

namespace SmsDock.Cli;

/// <summary>How the value of an option is read.</summary>
internal enum OptionKind
{
    Flag,
    Text,
    Integer,
    Positive,
    Number,
    Timestamp,
    Many,
}

/// <summary>One option of a command.</summary>
internal sealed class OptionSpec(string name, OptionKind kind, string? metavar, string help)
{
    public string Name { get; } = name;

    public OptionKind Kind { get; } = kind;

    public string Help { get; } = help;

    public IReadOnlyList<string>? Choices { get; init; }

    public bool Required { get; init; }

    /// <summary>Name of the group of options of which exactly one must be given.</summary>
    public string? Group { get; init; }

    public string Key => Name[2..];

    /// <summary>The option with its value as shown in the usage line and the help.</summary>
    public string Invocation
    {
        get
        {
            var value = metavar
                ?? (Choices is null ? Key.ToUpperInvariant().Replace('-', '_') : "{" + string.Join(",", Choices) + "}");
            return Kind switch
            {
                OptionKind.Flag => Name,
                OptionKind.Many => $"{Name} {value} [{value} ...]",
                _ => $"{Name} {value}",
            };
        }
    }
}

/// <summary>One command with its positional argument and its options.</summary>
internal sealed class CommandSpec(
    string name, string help, string? description, string positional, string positionalDisplay, string positionalHelp)
{
    public string Name { get; } = name;

    public string Help { get; } = help;

    public string? Description { get; } = description;

    public string Positional { get; } = positional;

    public string PositionalDisplay { get; } = positionalDisplay;

    public string PositionalHelp { get; } = positionalHelp;

    public List<OptionSpec> Options { get; } = [];

    public CommandSpec Add(OptionSpec option)
    {
        Options.Add(option);
        return this;
    }

    public OptionSpec? Find(string name) => Options.Find(option => option.Name == name);
}

/// <summary>The arguments cannot be read; answered with the usage line.</summary>
internal sealed class ParseException(CommandSpec? command, string message) : Exception(message)
{
    public CommandSpec? Command { get; } = command;
}

/// <summary>The result of reading the arguments.</summary>
internal sealed class ParsedArguments
{
    /// <summary>The help text to print, when help was asked for.</summary>
    public string? HelpText { get; init; }

    public CommandSpec? Command { get; init; }

    public Dictionary<string, object> Values { get; init; } = [];

    public bool Flag(string key) => Values.ContainsKey(key);

    public string? Text(string key) => Values.TryGetValue(key, out var value) ? (string)value : null;

    public long? Integer(string key) => Values.TryGetValue(key, out var value) ? (long)value : null;

    public double? Number(string key) => Values.TryGetValue(key, out var value) ? (double)value : null;
}

/// <summary>
/// Reads the command line the way the argument parser of the Python tool does, with the same
/// usage lines, help texts and error messages.
/// </summary>
internal sealed partial class ArgumentParser
{
    private const string Program = "smsdock";
    private const string Description = "Read, send and watch SMS through the SMS Dock app.";
    private const string HelpInvocation = "-h, --help";
    private const string HelpHelp = "show this help message and exit";

    private static readonly string s_epilog =
        $"The password is read from the environment variable {SmsDockApi.PasswordEnv}, "
        + "never from the command line.";

    private readonly int _width;

    /// <summary>Creates the parser.</summary>
    /// <param name="columns">Width of the terminal; texts are wrapped two columns before.</param>
    public ArgumentParser(int columns)
    {
        _width = columns - 2;
    }

    /// <summary>The commands of the tool.</summary>
    public static IReadOnlyList<CommandSpec> Commands { get; } = BuildCommands();

    /// <summary>Reads the arguments.</summary>
    /// <exception cref="ParseException">The arguments cannot be read.</exception>
    public ParsedArguments Parse(IReadOnlyList<string> args)
    {
        var extras = new List<string>();
        var index = 0;
        string? name = null;
        while (index < args.Count && name is null)
        {
            var argument = args[index++];
            if (argument == "--")
            {
                continue;
            }
            switch (Classify(argument, null, out var explicitValue))
            {
                case ArgumentClass.Help:
                    CheckHelp(null, explicitValue);
                    return new ParsedArguments { HelpText = RootHelp() };
                case ArgumentClass.Unknown:
                    extras.Add(argument);
                    break;
                default:
                    name = argument;
                    break;
            }
        }
        if (name is null)
        {
            throw new ParseException(null, "the following arguments are required: command");
        }
        var command = Commands.FirstOrDefault(candidate => candidate.Name == name)
            ?? throw new ParseException(
                null,
                $"argument command: invalid choice: {PyText.Repr(name)} (choose from "
                + string.Join(", ", Commands.Select(candidate => PyText.Repr(candidate.Name))) + ")");
        var parsed = ParseCommand(command, args, index, extras);
        if (parsed.HelpText is null && extras.Count > 0)
        {
            throw new ParseException(null, "unrecognized arguments: " + string.Join(" ", extras));
        }
        return parsed;
    }

    /// <summary>Returns the usage line of a command, or of the tool for <c>null</c>.</summary>
    public string Usage(CommandSpec? command)
    {
        var program = Program;
        var optionals = new List<string> { "[-h]" };
        var positionals = new List<string>();
        if (command is null)
        {
            positionals.Add("{" + string.Join(",", Commands.Select(candidate => candidate.Name)) + "} ...");
        }
        else
        {
            program = $"{Program} {command.Name}";
            positionals.Add(command.PositionalDisplay);
            var groups = new HashSet<string>();
            foreach (var option in command.Options)
            {
                if (option.Group is null)
                {
                    optionals.Add(option.Required ? option.Invocation : $"[{option.Invocation}]");
                }
                else if (groups.Add(option.Group))
                {
                    var members = command.Options.Where(other => other.Group == option.Group)
                        .Select(other => other.Invocation).ToList();
                    members[0] = "(" + members[0];
                    for (var member = 0; member < members.Count - 1; member++)
                    {
                        members[member] += " |";
                    }
                    members[^1] += ")";
                    optionals.AddRange(members);
                }
            }
        }
        const string Prefix = "usage: ";
        var textWidth = _width;
        var single = string.Join(" ", new[] { program }.Concat(optionals).Concat(positionals));
        if (Prefix.Length + single.Length <= textWidth)
        {
            return Prefix + single;
        }
        List<string> lines;
        if (Prefix.Length + program.Length <= 0.75 * textWidth)
        {
            var indent = new string(' ', Prefix.Length + program.Length + 1);
            lines = UsageLines(new[] { program }.Concat(optionals).ToList(), indent, Prefix, textWidth);
            lines.AddRange(UsageLines(positionals, indent, null, textWidth));
        }
        else
        {
            var indent = new string(' ', Prefix.Length);
            lines = UsageLines(optionals.Concat(positionals).ToList(), indent, null, textWidth);
            if (lines.Count > 1)
            {
                lines = UsageLines(optionals, indent, null, textWidth);
                lines.AddRange(UsageLines(positionals, indent, null, textWidth));
            }
            lines.Insert(0, program);
        }
        return Prefix + string.Join("\n", lines);
    }

    private static List<CommandSpec> BuildCommands()
    {
        var events = SmsDockApi.EventNames.Append("*").ToList();
        return
        [
            new CommandSpec(
                "fingerprint", "print the certificate fingerprint of a server",
                "Print the SHA-256 fingerprint of the certificate the server presents. Compare it with the "
                + "Server screen of the app before you use it.",
                "host", "HOST[:PORT]", $"address of the phone; the default port is {SmsDockApi.DefaultPort}")
                .Add(new OptionSpec("--timeout", OptionKind.Number, "SECONDS", "seconds to wait (default: 10)")),
            Connected("status", "show phone, SIMs, permissions and licence", null),
            Connected("conversations", "list conversations, newest first", null)
                .Add(new OptionSpec("--search", OptionKind.Text, "TEXT", "search text, numbers and contact names"))
                .Add(new OptionSpec("--unread", OptionKind.Flag, null, "only conversations with unread messages"))
                .Add(new OptionSpec("--sim", OptionKind.Integer, "ID", "only this subscription id"))
                .Add(new OptionSpec("--limit", OptionKind.Positive, "N", "number of conversations (default: 50)")),
            Connected("messages", "list messages, newest first", null)
                .Add(new OptionSpec("--address", OptionKind.Text, "NUMBER", "only messages from or to this number"))
                .Add(new OptionSpec("--conversation", OptionKind.Integer, "ID", "only this conversation"))
                .Add(new OptionSpec("--direction", OptionKind.Text, null, "incoming, outgoing or both")
                {
                    Choices = ["in", "out", "all"],
                })
                .Add(new OptionSpec("--search", OptionKind.Text, "TEXT", "search the text"))
                .Add(new OptionSpec(
                    "--since", OptionKind.Timestamp, "TIME",
                    "not older than this: 2026-09-29, 2026-09-29T08:00 or milliseconds"))
                .Add(new OptionSpec("--until", OptionKind.Timestamp, "TIME", "not newer than this"))
                .Add(new OptionSpec("--unread", OptionKind.Flag, null, "only unread messages"))
                .Add(new OptionSpec("--sim", OptionKind.Integer, "ID", "only this subscription id"))
                .Add(new OptionSpec("--limit", OptionKind.Positive, "N", "number of messages (default: 50)")),
            Connected(
                "send", "send an SMS", "Send an SMS. Without --text the text is read from standard input.")
                .Add(new OptionSpec("--to", OptionKind.Text, "NUMBER", "phone number of the recipient")
                {
                    Required = true,
                })
                .Add(new OptionSpec("--text", OptionKind.Text, null, "text of the message"))
                .Add(new OptionSpec("--sim", OptionKind.Integer, "ID", "subscription id of the SIM"))
                .Add(new OptionSpec(
                    "--request-id", OptionKind.Text, "ID", "your own id; the same id is sent only once"))
                .Add(new OptionSpec("--wait", OptionKind.Flag, null, "wait until the message was sent or failed"))
                .Add(new OptionSpec(
                    "--wait-timeout", OptionKind.Number, "SECONDS",
                    "longest time to wait with --wait (default: 60)")),
            Connected("delete", "delete a message or a conversation", null)
                .Add(new OptionSpec("--message", OptionKind.Integer, "ID", "id of the message") { Group = "target" })
                .Add(new OptionSpec("--conversation", OptionKind.Integer, "ID", "id of the conversation")
                {
                    Group = "target",
                }),
            Connected(
                "mark-read", "mark a conversation as read",
                "Mark all messages of a conversation as read. SMS Dock must be the default SMS app of the "
                + "phone.")
                .Add(new OptionSpec("--conversation", OptionKind.Integer, "ID", "id of the conversation")
                {
                    Required = true,
                }),
            Connected("contacts", "list the contacts of the phone", null)
                .Add(new OptionSpec("--search", OptionKind.Text, "TEXT", "search names and numbers"))
                .Add(new OptionSpec("--limit", OptionKind.Positive, "N", "number of contacts (default: 50)")),
            Connected(
                "listen", "print incoming SMS as they arrive", "Print events as they arrive until Ctrl+C.")
                .Add(new OptionSpec("--address", OptionKind.Text, "NUMBER", "only SMS events of this number"))
                .Add(new OptionSpec(
                    "--events", OptionKind.Many, "EVENT", "events to print (default: sms.received); * means all")
                {
                    Choices = events,
                })
                .Add(new OptionSpec("--count", OptionKind.Positive, "N", "stop after this many events")),
        ];
    }

    private static CommandSpec Connected(string name, string help, string? description) =>
        new CommandSpec(
            name, help, description, "url", "url", "wss:// URL shown by the app, e.g. wss://192.168.1.50:9443/ws")
            .Add(new OptionSpec(
                "--fingerprint", OptionKind.Text, "HEX",
                "SHA-256 fingerprint of the certificate as shown by the app (default: environment variable "
                + $"{SmsDockApi.FingerprintEnv})"))
            .Add(new OptionSpec("--insecure", OptionKind.Flag, null, "do not check the certificate (for tests only)"))
            .Add(new OptionSpec(
                "--password-env", OptionKind.Text, "NAME",
                $"environment variable with the password (default: {SmsDockApi.PasswordEnv})"))
            .Add(new OptionSpec(
                "--timeout", OptionKind.Number, "SECONDS", "seconds to wait for a response (default: 30)"))
            .Add(new OptionSpec(
                "--reconnect-attempts", OptionKind.Integer, "N",
                "reconnect attempts after a lost connection; -1 means without limit (default: 5)"))
            .Add(new OptionSpec("--json", OptionKind.Flag, null, "print JSON instead of text"));

    private static void CheckHelp(CommandSpec? command, string? explicitValue)
    {
        if (explicitValue is not null)
        {
            throw new ParseException(
                command, $"argument -h/--help: ignored explicit argument {PyText.Repr(explicitValue)}");
        }
    }

    /// <summary>Tells what an argument is: a known option, an unknown one, or a value.</summary>
    private static ArgumentClass Classify(string argument, CommandSpec? command, out string? explicitValue)
    {
        explicitValue = null;
        if (argument.Length == 0 || argument[0] != '-')
        {
            return ArgumentClass.Value;
        }
        if (argument is "-h" or "--help")
        {
            return ArgumentClass.Help;
        }
        if (command?.Find(argument) is not null)
        {
            return ArgumentClass.Option;
        }
        if (argument.Length == 1)
        {
            return ArgumentClass.Value;
        }
        var equals = argument.IndexOf('=');
        if (equals >= 0)
        {
            var name = argument[..equals];
            if (name is "-h" or "--help")
            {
                explicitValue = argument[(equals + 1)..];
                return ArgumentClass.Help;
            }
            if (command?.Find(name) is not null)
            {
                explicitValue = argument[(equals + 1)..];
                return ArgumentClass.Option;
            }
        }
        if (argument[1] != '-' && argument[1] == 'h')
        {
            // "-hx": short options glued together; help comes first and ends the run.
            var rest = argument[2..];
            explicitValue = rest.StartsWith('-') ? rest : null;
            return ArgumentClass.Help;
        }
        if (NegativeNumber().IsMatch(argument) || argument.Contains(' '))
        {
            return ArgumentClass.Value;
        }
        return ArgumentClass.Unknown;
    }

    private static object Convert(CommandSpec command, OptionSpec option, string text)
    {
        var prefix = $"argument {option.Name}: ";
        object value = text;
        switch (option.Kind)
        {
            case OptionKind.Integer:
                value = PyInt(text) is { } integer && integer >= long.MinValue && integer <= long.MaxValue
                    ? (long)integer
                    : throw new ParseException(command, $"{prefix}invalid int value: {PyText.Repr(text)}");
                break;
            case OptionKind.Positive:
                var number = PyInt(text) ?? BigInteger.Zero;
                if (number < 1)
                {
                    throw new ParseException(command, prefix + "must be a number from 1");
                }
                value = number > long.MaxValue ? long.MaxValue : (long)number;
                break;
            case OptionKind.Number:
                value = PyFloat(text)
                    ?? throw new ParseException(command, $"{prefix}invalid float value: {PyText.Repr(text)}");
                break;
            case OptionKind.Timestamp:
                value = Timestamps.Parse(text)
                    ?? throw new ParseException(
                        command,
                        prefix + "must be a date such as 2026-09-29 or 2026-09-29T08:00, "
                        + "or milliseconds since the epoch");
                break;
        }
        if (option.Choices is not null && !option.Choices.Contains(text))
        {
            throw new ParseException(
                command,
                $"{prefix}invalid choice: {PyText.Repr(text)} (choose from "
                + string.Join(", ", option.Choices.Select(PyText.Repr)) + ")");
        }
        return value;
    }

    /// <summary>Reads an integer as Python's <c>int()</c> does; <c>null</c> when it is none.</summary>
    private static BigInteger? PyInt(string text)
    {
        var trimmed = text.Trim().TrimStart('\u001c', '\u001d', '\u001e', '\u001f')
            .TrimEnd('\u001c', '\u001d', '\u001e', '\u001f');
        if (!IntegerText().IsMatch(trimmed))
        {
            return null;
        }
        return BigInteger.Parse(
            trimmed.Replace("_", "", StringComparison.Ordinal),
            NumberStyles.AllowLeadingSign,
            CultureInfo.InvariantCulture);
    }

    /// <summary>Reads a number as Python's <c>float()</c> does; <c>null</c> when it is none.</summary>
    private static double? PyFloat(string text)
    {
        var trimmed = text.Trim();
        var special = SpecialFloat().Match(trimmed);
        if (special.Success)
        {
            var negative = special.Groups[1].Value == "-";
            if (special.Groups[2].Value.StartsWith("n", StringComparison.OrdinalIgnoreCase))
            {
                return double.NaN;
            }
            return negative ? double.NegativeInfinity : double.PositiveInfinity;
        }
        if (!FloatText().IsMatch(trimmed))
        {
            return null;
        }
        return double.Parse(
            trimmed.Replace("_", "", StringComparison.Ordinal), NumberStyles.Float, CultureInfo.InvariantCulture);
    }

    private static List<string> UsageLines(IReadOnlyList<string> parts, string indent, string? prefix, int textWidth)
    {
        var lines = new List<string>();
        var line = new List<string>();
        var length = (prefix?.Length ?? indent.Length) - 1;
        foreach (var part in parts)
        {
            if (length + 1 + part.Length > textWidth && line.Count > 0)
            {
                lines.Add(indent + string.Join(" ", line));
                line.Clear();
                length = indent.Length - 1;
            }
            line.Add(part);
            length += part.Length + 1;
        }
        if (line.Count > 0)
        {
            lines.Add(indent + string.Join(" ", line));
        }
        if (prefix is not null && lines.Count > 0)
        {
            lines[0] = lines[0][indent.Length..];
        }
        return lines;
    }

    private ParsedArguments ParseCommand(
        CommandSpec command, IReadOnlyList<string> args, int index, List<string> extras)
    {
        var values = new Dictionary<string, object>();
        var seenGroups = new Dictionary<string, OptionSpec>();
        var hasPositional = false;
        var onlyValues = false;

        void TakeValue(string argument)
        {
            if (hasPositional)
            {
                extras.Add(argument);
                return;
            }
            hasPositional = true;
            values[command.Positional] = argument;
        }

        bool IsValue(int position) =>
            position < args.Count && args[position] != "--"
            && Classify(args[position], command, out _) == ArgumentClass.Value;

        while (index < args.Count)
        {
            var argument = args[index++];
            if (onlyValues)
            {
                TakeValue(argument);
                continue;
            }
            if (argument == "--")
            {
                onlyValues = true;
                continue;
            }
            var kind = Classify(argument, command, out var explicitValue);
            if (kind == ArgumentClass.Help)
            {
                CheckHelp(command, explicitValue);
                return new ParsedArguments { HelpText = CommandHelp(command) };
            }
            if (kind == ArgumentClass.Value)
            {
                TakeValue(argument);
                continue;
            }
            if (kind == ArgumentClass.Unknown)
            {
                extras.Add(argument);
                continue;
            }
            var name = explicitValue is null ? argument : argument[..argument.IndexOf('=')];
            var option = command.Find(name)!;
            if (option.Group is not null)
            {
                if (seenGroups.TryGetValue(option.Group, out var other) && other != option)
                {
                    throw new ParseException(
                        command, $"argument {option.Name}: not allowed with argument {other.Name}");
                }
                seenGroups.TryAdd(option.Group, option);
            }
            if (option.Kind == OptionKind.Flag)
            {
                if (explicitValue is not null)
                {
                    throw new ParseException(
                        command,
                        $"argument {option.Name}: ignored explicit argument {PyText.Repr(explicitValue)}");
                }
                values[option.Key] = true;
            }
            else if (option.Kind == OptionKind.Many)
            {
                var items = new List<string>();
                if (explicitValue is not null)
                {
                    items.Add(explicitValue);
                }
                else
                {
                    while (IsValue(index))
                    {
                        items.Add(args[index++]);
                    }
                }
                if (items.Count == 0)
                {
                    throw new ParseException(command, $"argument {option.Name}: expected at least one argument");
                }
                foreach (var item in items)
                {
                    Convert(command, option, item);
                }
                values[option.Key] = items;
            }
            else
            {
                var text = explicitValue;
                if (text is null)
                {
                    if (!IsValue(index))
                    {
                        throw new ParseException(command, $"argument {option.Name}: expected one argument");
                    }
                    text = args[index++];
                }
                values[option.Key] = Convert(command, option, text);
            }
        }
        var missing = new List<string>();
        if (!hasPositional)
        {
            missing.Add(command.PositionalDisplay);
        }
        missing.AddRange(
            command.Options.Where(option => option.Required && !values.ContainsKey(option.Key))
                .Select(option => option.Name));
        if (missing.Count > 0)
        {
            throw new ParseException(
                command, "the following arguments are required: " + string.Join(", ", missing));
        }
        var grouped = command.Options.Where(option => option.Group is not null).ToList();
        if (grouped.Count > 0 && seenGroups.Count == 0)
        {
            throw new ParseException(
                command,
                "one of the arguments " + string.Join(" ", grouped.Select(option => option.Name)) + " is required");
        }
        return new ParsedArguments { Command = command, Values = values };
    }

    private string RootHelp()
    {
        var choices = "{" + string.Join(",", Commands.Select(command => command.Name)) + "}";
        var longest = Math.Max(
            Math.Max(HelpInvocation.Length, choices.Length) + 2, Commands.Max(command => command.Name.Length) + 4);
        var position = HelpPosition(longest);
        var text = new StringBuilder(Usage(null)).Append("\n\n")
            .Append(TextWrap.Fill(Description, Math.Max(_width, 11))).Append("\n\n")
            .Append("positional arguments:\n")
            .Append(ActionLines(choices, null, 2, position));
        foreach (var command in Commands)
        {
            text.Append(ActionLines(command.Name, command.Help, 4, position));
        }
        return text.Append("\noptions:\n")
            .Append(ActionLines(HelpInvocation, HelpHelp, 2, position))
            .Append('\n').Append(TextWrap.Fill(s_epilog, Math.Max(_width, 11))).Append('\n')
            .ToString();
    }

    private string CommandHelp(CommandSpec command)
    {
        var longest = new[] { command.PositionalDisplay, HelpInvocation }
            .Concat(command.Options.Select(option => option.Invocation)).Max(text => text.Length) + 2;
        var position = HelpPosition(longest);
        var text = new StringBuilder(Usage(command)).Append("\n\n");
        if (command.Description is not null)
        {
            text.Append(TextWrap.Fill(command.Description, Math.Max(_width, 11))).Append("\n\n");
        }
        text.Append("positional arguments:\n")
            .Append(ActionLines(command.PositionalDisplay, command.PositionalHelp, 2, position))
            .Append("\noptions:\n")
            .Append(ActionLines(HelpInvocation, HelpHelp, 2, position));
        foreach (var option in command.Options)
        {
            text.Append(ActionLines(option.Invocation, option.Help, 2, position));
        }
        return text.ToString();
    }

    private int HelpPosition(int longestInvocation) =>
        Math.Min(longestInvocation + 2, Math.Min(24, Math.Max(_width - 20, 4)));

    private string ActionLines(string invocation, string? help, int indent, int position)
    {
        var helpWidth = Math.Max(_width - position, 11);
        var actionWidth = position - indent - 2;
        var text = new StringBuilder().Append(' ', indent).Append(invocation);
        if (help is null)
        {
            return text.Append('\n').ToString();
        }
        int first;
        if (invocation.Length <= actionWidth)
        {
            text.Append(' ', actionWidth - invocation.Length + 2);
            first = 0;
        }
        else
        {
            text.Append('\n');
            first = position;
        }
        var lines = TextWrap.Wrap(help, helpWidth);
        for (var line = 0; line < lines.Count; line++)
        {
            text.Append(' ', line == 0 ? first : position).Append(lines[line]).Append('\n');
        }
        return text.ToString();
    }

    [GeneratedRegex(@"\A-\.?\d")]
    private static partial Regex NegativeNumber();

    [GeneratedRegex(@"\A[+-]?[0-9]+(_[0-9]+)*\z")]
    private static partial Regex IntegerText();

    [GeneratedRegex(@"\A([+-]?)(inf|infinity|nan)\z", RegexOptions.IgnoreCase)]
    private static partial Regex SpecialFloat();

    [GeneratedRegex(
        @"\A[+-]?([0-9]+(_[0-9]+)*(\.([0-9]+(_[0-9]+)*)?)?|\.[0-9]+(_[0-9]+)*)([eE][+-]?[0-9]+(_[0-9]+)*)?\z")]
    private static partial Regex FloatText();

    private enum ArgumentClass
    {
        Value,
        Option,
        Help,
        Unknown,
    }
}
