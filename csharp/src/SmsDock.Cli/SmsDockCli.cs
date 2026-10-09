using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace SmsDock.Cli;

/// <summary>The command line cannot be carried out as given.</summary>
internal sealed class UsageException(string message) : Exception(message);

/// <summary>What the tool sees of the world around it; replaced in tests.</summary>
internal sealed class CliEnvironment
{
    /// <summary>Returns the value of an environment variable, or <c>null</c>.</summary>
    public required Func<string, string?> GetVariable { get; init; }

    /// <summary>Standard input.</summary>
    public required TextReader Input { get; init; }

    /// <summary>Whether standard input is a terminal.</summary>
    public required bool InputIsTerminal { get; init; }

    /// <summary>Standard output.</summary>
    public required TextWriter Out { get; init; }

    /// <summary>Standard error.</summary>
    public required TextWriter Error { get; init; }

    /// <summary>Asks for the password without showing it.</summary>
    public Func<string, string?>? PasswordPrompt { get; init; }

    /// <summary>Width of the terminal in columns.</summary>
    public int Columns { get; init; } = 80;
}

/// <summary>
/// Command-line tool <c>smsdock</c> for the SMS Dock WebSocket API. Exit codes: 0 success, 1
/// the server answered with an error, 2 wrong usage, 3 connection or TLS problem.
/// </summary>
internal sealed partial class SmsDockCli(CliEnvironment environment)
{
    /// <summary>Exit code: success.</summary>
    public const int ExitOk = 0;

    /// <summary>Exit code: the server answered with an error, or the message failed.</summary>
    public const int ExitApiError = 1;

    /// <summary>Exit code: wrong usage.</summary>
    public const int ExitUsage = 2;

    /// <summary>Exit code: connection or TLS problem.</summary>
    public const int ExitConnection = 3;

    /// <summary>Exit code: stopped with Ctrl+C.</summary>
    public const int ExitInterrupted = 130;

    private const int TextWidth = 60;

    private readonly TextWriter _out = environment.Out;
    private readonly TextWriter _error = environment.Error;

    /// <summary>Runs the command line and returns the exit code.</summary>
    public async Task<int> RunAsync(IReadOnlyList<string> args, CancellationToken cancellationToken = default)
    {
        var parser = new ArgumentParser(environment.Columns);
        try
        {
            try
            {
                return await DispatchAsync(parser, args, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _out.Flush();
            }
        }
        catch (ParseException error)
        {
            var program = error.Command is null ? "smsdock" : $"smsdock {error.Command.Name}";
            _error.Write($"{parser.Usage(error.Command)}\n{program}: error: {error.Message}\n");
            return ExitUsage;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return ExitInterrupted;
        }
        catch (IOException)
        {
            // The reader of the output is gone, as in "smsdock listen | head -1".
            return ExitOk;
        }
        catch (ApiException error)
        {
            Complain(error);
            return ExitApiError;
        }
        catch (Exception error) when (error is UsageException or ArgumentException)
        {
            Complain(error);
            return ExitUsage;
        }
        catch (Exception error) when (error is SmsDockException or TimeoutException)
        {
            Complain(error);
            return ExitConnection;
        }
    }

    /// <summary>Returns one event as a line of text.</summary>
    internal static string Describe(JsonObject message)
    {
        var name = message["event"];
        var data = message["data"] as JsonObject ?? [];
        var sms = data["message"] as JsonObject ?? [];
        if (PyText.AsString(name) == "sms.received")
        {
            var sender = PyText.Str(sms["address"]);
            if (PyText.Truthy(sms["contact"]))
            {
                sender = $"{sender} ({PyText.Str(sms["contact"])})";
            }
            return $"{Time(sms["timestamp"], true)}  {sender}: {PyText.Str(sms["body"])}";
        }
        var now = Timestamps.Format(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), true, "");
        if (PyText.AsString(name) == "sms.sent" && sms.Count > 0)
        {
            return $"{now}  sent to {PyText.Str(sms["address"])}: {PyText.Str(sms["body"])}";
        }
        var details = string.Join(
            " ",
            data.Where(pair => pair.Value is not (JsonObject or JsonArray))
                .Select(pair => $"{pair.Key}={PyText.Str(pair.Value)}"));
        return $"{now}  {PyText.Str(name)} {details}".TrimEnd(' ');
    }

    /// <summary>Splits <c>HOST</c>, <c>HOST:PORT</c>, <c>[IPv6]:PORT</c> or a <c>wss://</c> URL.</summary>
    /// <exception cref="UsageException">The text is none of these.</exception>
    internal static (string Host, int Port) HostAndPort(string text)
    {
        var wrong = new UsageException($"'{text}' is not HOST or HOST:PORT");
        var value = text.Trim();
        if (!value.Contains("://", StringComparison.Ordinal))
        {
            if (value.Count(character => character == ':') > 1 && !value.StartsWith('['))
            {
                value = $"[{value}]";
            }
            value = $"//{value}";
        }
        else
        {
            var scheme = value[..value.IndexOf(':')];
            if (!SchemePattern().IsMatch(scheme))
            {
                throw wrong;
            }
            value = value[(scheme.Length + 1)..];
        }
        if (!value.StartsWith("//", StringComparison.Ordinal))
        {
            throw wrong;
        }
        var end = value.IndexOfAny(['/', '?', '#'], 2);
        var netloc = end < 0 ? value[2..] : value[2..end];
        var hostInfo = netloc[(netloc.LastIndexOf('@') + 1)..];
        string host;
        string port;
        var open = hostInfo.IndexOf('[');
        if (netloc.Contains('[') != netloc.Contains(']'))
        {
            throw wrong;
        }
        if (open >= 0)
        {
            var bracketed = hostInfo[(open + 1)..];
            var close = bracketed.IndexOf(']');
            if (open > 0 || close < 0)
            {
                throw wrong;
            }
            host = bracketed[..close];
            port = bracketed[(close + 1)..];
            if (port.Length > 0 && !port.StartsWith(':'))
            {
                throw wrong;
            }
            port = port.Length > 0 ? port[1..] : port;
            if (!IPAddress.TryParse(host, out var address) || address.AddressFamily != AddressFamily.InterNetworkV6)
            {
                throw wrong;
            }
        }
        else
        {
            var colon = hostInfo.IndexOf(':');
            host = colon < 0 ? hostInfo : hostInfo[..colon];
            port = colon < 0 ? "" : hostInfo[(colon + 1)..];
        }
        var number = 0;
        if (port.Length > 0
            && (!port.All(char.IsAsciiDigit) || port.TrimStart('0').Length > 5
                || (number = int.Parse(port, System.Globalization.CultureInfo.InvariantCulture)) > 65535))
        {
            throw wrong;
        }
        if (host.Length == 0)
        {
            throw wrong;
        }
        return (host.ToLowerInvariant(), number == 0 ? SmsDockApi.DefaultPort : number);
    }

    private static TimeSpan Seconds(double seconds)
    {
        if (double.IsNaN(seconds) || seconds <= 0)
        {
            return seconds < 0 ? TimeSpan.FromTicks(-1) : TimeSpan.Zero;
        }
        if (seconds >= Timing.Longest.TotalSeconds)
        {
            return Timing.Longest;
        }
        return TimeSpan.FromTicks(Math.Max(1, (long)(seconds * TimeSpan.TicksPerSecond)));
    }

    private static string Time(JsonNode? milliseconds, bool seconds = false) =>
        PyText.AsDouble(milliseconds) is { } value
            ? Timestamps.Format(value, seconds, PyText.Str(milliseconds))
            : "";

    private static string OneLine(JsonNode? text)
    {
        var value = PyText.Truthy(text) ? PyText.Str(text) : "";
        var words = new List<string>();
        var word = new StringBuilder();
        foreach (var character in value)
        {
            if (PyText.IsSpace(character))
            {
                if (word.Length > 0)
                {
                    words.Add(word.ToString());
                    word.Clear();
                }
            }
            else
            {
                word.Append(character);
            }
        }
        if (word.Length > 0)
        {
            words.Add(word.ToString());
        }
        var line = string.Join(" ", words);
        if (Length(line) > TextWidth)
        {
            line = Cut(line, TextWidth - 1) + "…";
        }
        return line;
    }

    private static string YesNo(JsonNode? value) => PyText.Truthy(value) ? "yes" : "no";

    private static string Capitalize(string value)
    {
        if (value.Length == 0)
        {
            return value;
        }
        var first = char.IsHighSurrogate(value[0]) && value.Length > 1 ? 2 : 1;
        return value[..first].ToUpperInvariant() + value[first..].ToLowerInvariant();
    }

    /// <summary>The length of a text as Python counts it: in code points.</summary>
    private static int Length(string value) => value.Length - value.Count(char.IsLowSurrogate);

    private static string Cut(string value, int codePoints)
    {
        var index = 0;
        for (var count = 0; count < codePoints && index < value.Length; count++)
        {
            index += char.IsHighSurrogate(value[index]) && index + 1 < value.Length ? 2 : 1;
        }
        return value[..index];
    }

    private static JsonObject Map(JsonNode? value) => value as JsonObject ?? [];

    private void Complain(Exception error) => _error.Write($"smsdock: {error.Message}\n");

    private void PrintLine(string line) => _out.Write(line + "\n");

    private void PrintJson(JsonNode? value) => PrintLine(PyText.Pretty(value));

    private async Task<int> DispatchAsync(
        ArgumentParser parser, IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        var parsed = parser.Parse(args);
        if (parsed.HelpText is not null)
        {
            _out.Write(parsed.HelpText);
            return ExitOk;
        }
        var command = parsed.Command!.Name;
        if (command == "fingerprint")
        {
            return await FingerprintAsync(parsed, cancellationToken).ConfigureAwait(false);
        }
        var client = CreateClient(parsed);
        await using (client.ConfigureAwait(false))
        {
            await client.ConnectAsync(cancellationToken).ConfigureAwait(false);
            return command switch
            {
                "status" => await StatusAsync(client, parsed, cancellationToken).ConfigureAwait(false),
                "conversations" => await ConversationsAsync(client, parsed, cancellationToken).ConfigureAwait(false),
                "messages" => await MessagesAsync(client, parsed, cancellationToken).ConfigureAwait(false),
                "send" => await SendAsync(client, parsed, cancellationToken).ConfigureAwait(false),
                "delete" => await DeleteAsync(client, parsed, cancellationToken).ConfigureAwait(false),
                "mark-read" => await MarkReadAsync(client, parsed, cancellationToken).ConfigureAwait(false),
                "contacts" => await ContactsAsync(client, parsed, cancellationToken).ConfigureAwait(false),
                _ => await ListenAsync(client, parsed, cancellationToken).ConfigureAwait(false),
            };
        }
    }

    private SmsDockClient CreateClient(ParsedArguments args)
    {
        var insecure = args.Flag("insecure");
        var fingerprint = args.Text("fingerprint") ?? environment.GetVariable(SmsDockApi.FingerprintEnv);
        if (!insecure && string.IsNullOrEmpty(fingerprint))
        {
            throw new UsageException(
                $"No certificate fingerprint. Pass --fingerprint or set {SmsDockApi.FingerprintEnv}. "
                + "'smsdock fingerprint HOST' prints the fingerprint of a server.");
        }
        var attempts = args.Integer("reconnect-attempts") ?? 5;
        if (attempts < -1)
        {
            throw new UsageException("--reconnect-attempts cannot be less than -1");
        }
        var password = Password(args.Text("password-env") ?? SmsDockApi.PasswordEnv);
        return new SmsDockClient(
            args.Text("url")!,
            password,
            new SmsDockClientOptions
            {
                Fingerprint = fingerprint,
                Insecure = insecure,
                Timeout = Seconds(args.Number("timeout") ?? 30.0),
                ReconnectAttempts = attempts == -1 ? null : (int)Math.Min(attempts, int.MaxValue),
                Environment = environment.GetVariable,
                Error = _error,
            });
    }

    private string Password(string variable)
    {
        var password = environment.GetVariable(variable);
        if (!string.IsNullOrEmpty(password))
        {
            return password;
        }
        if (environment.InputIsTerminal && environment.PasswordPrompt is { } prompt)
        {
            password = prompt("SMS Dock password: ");
            if (!string.IsNullOrEmpty(password))
            {
                return password;
            }
        }
        throw new UsageException($"No password. Set the environment variable {variable}.");
    }

    private async Task<int> FingerprintAsync(ParsedArguments args, CancellationToken cancellationToken)
    {
        var (host, port) = HostAndPort(args.Text("host")!);
        var fingerprint = await CertificateFingerprint.FetchAsync(
            host, port, Seconds(args.Number("timeout") ?? 10.0), cancellationToken).ConfigureAwait(false);
        PrintLine(fingerprint);
        _out.Flush();
        _error.Write(
            "Compare this fingerprint with the Server screen of the app. Use it only if both are the same.\n");
        return ExitOk;
    }

    private async Task<int> StatusAsync(
        SmsDockClient client, ParsedArguments args, CancellationToken cancellationToken)
    {
        var status = (await client.StatusAsync(cancellationToken).ConfigureAwait(false)).Json;
        if (args.Flag("json"))
        {
            PrintJson(status);
            return ExitOk;
        }
        var app = Map(status["app"]);
        var licence = Map(status["licence"]);
        var missing = Map(status["permissions"]).Where(pair => !PyText.Truthy(pair.Value))
            .Select(pair => pair.Key).Order(StringComparer.Ordinal).ToList();
        string licenceText;
        if (PyText.Truthy(licence["licensed"]))
        {
            licenceText = "full licence";
        }
        else if (!PyText.Truthy(licence["enforced"]))
        {
            licenceText = "not enforced";
        }
        else
        {
            licenceText = $"free version, {PyText.Str(licence["sentToday"])} of "
                + $"{PyText.Str(licence["dailyLimit"])} SMS sent today";
        }
        (string Label, string Value)[] lines =
        [
            ("App", $"{PyText.Str(app["name"])} {PyText.Str(app["version"])} "
                + $"(protocol {PyText.Str(app["protocolVersion"])})"),
            ("Default SMS app", YesNo(status["defaultSmsApp"])),
            ("Delete mode", PyText.Str(status["deleteMode"])),
            ("Licence", licenceText),
            ("Permissions", missing.Count == 0 ? "all granted" : "missing: " + string.Join(", ", missing)),
        ];
        foreach (var (label, value) in lines)
        {
            PrintLine((label + ":").PadRight(17) + value);
        }
        if (status["sims"] is JsonArray { Count: > 0 } sims)
        {
            PrintLine("");
            PrintTable(
                ["SIM", "SLOT", "NAME", "NUMBER", "DEFAULT"],
                sims.Select(Map).Select(sim => new[]
                {
                    Cell(sim["subscriptionId"]), Cell(sim["slot"]), Cell(sim["name"]), Cell(sim["number"]),
                    YesNo(sim["isDefault"]),
                }));
        }
        return ExitOk;
    }

    private async Task<int> ConversationsAsync(
        SmsDockClient client, ParsedArguments args, CancellationToken cancellationToken)
    {
        var limit = args.Integer("limit") ?? 50;
        var filter = new ConversationFilter
        {
            Query = args.Text("search"),
            Unread = args.Flag("unread"),
            Sim = args.Integer("sim"),
        };
        var items = new List<JsonObject>();
        await foreach (var item in client.EnumerateConversationsAsync(
            filter, (int)Math.Min(limit, 200), (int)Math.Min(limit, int.MaxValue), cancellationToken)
            .ConfigureAwait(false))
        {
            items.Add(item.Json);
        }
        if (args.Flag("json"))
        {
            PrintJson(new JsonArray([.. items]));
            return ExitOk;
        }
        PrintTable(
            ["ID", "TIME", "NUMBER", "NAME", "MSGS", "UNREAD", "DIR", "TEXT"],
            items.Select(item => new[]
            {
                Cell(item["id"]), Time(item["timestamp"]), Cell(item["address"]), Cell(item["contact"]),
                Cell(item["messageCount"]), Cell(item["unreadCount"]), Cell(item["direction"]),
                OneLine(item["snippet"]),
            }));
        return ExitOk;
    }

    private async Task<int> MessagesAsync(
        SmsDockClient client, ParsedArguments args, CancellationToken cancellationToken)
    {
        var limit = args.Integer("limit") ?? 50;
        var filter = new MessageFilter
        {
            Address = args.Text("address"),
            ConversationId = args.Integer("conversation"),
            Direction = args.Text("direction") ?? "all",
            Query = args.Text("search"),
            Since = args.Integer("since"),
            Until = args.Integer("until"),
            Sim = args.Integer("sim"),
            Unread = args.Flag("unread"),
        };
        var items = new List<JsonObject>();
        await foreach (var item in client.EnumerateMessagesAsync(
            filter, (int)Math.Min(limit, 200), (int)Math.Min(limit, int.MaxValue), cancellationToken)
            .ConfigureAwait(false))
        {
            items.Add(item.Json);
        }
        if (args.Flag("json"))
        {
            PrintJson(new JsonArray([.. items]));
            return ExitOk;
        }
        PrintTable(
            ["ID", "TIME", "DIR", "NUMBER", "NAME", "STATUS", "TEXT"],
            items.Select(item => new[]
            {
                Cell(item["id"]), Time(item["timestamp"]), Cell(item["direction"]), Cell(item["address"]),
                Cell(item["contact"]), Cell(item["status"]), OneLine(item["body"]),
            }));
        return ExitOk;
    }

    private async Task<int> SendAsync(
        SmsDockClient client, ParsedArguments args, CancellationToken cancellationToken)
    {
        var text = args.Text("text");
        if (text is null)
        {
            if (environment.InputIsTerminal)
            {
                throw new UsageException("No text. Pass --text or write it to standard input.");
            }
            text = (await environment.Input.ReadToEndAsync(cancellationToken).ConfigureAwait(false))
                .Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
            if (text.EndsWith('\n'))
            {
                text = text[..^1];
            }
        }
        if (text.Length == 0)
        {
            throw new UsageException("The text is empty.");
        }
        JsonObject result = (await client.SendAsync(
            args.Text("to")!, text, args.Integer("sim"), args.Text("request-id"), cancellationToken)
            .ConfigureAwait(false)).Json;
        if (args.Flag("wait"))
        {
            result = (await client.WaitForResultAsync(
                PyText.Str(result["requestId"]),
                Seconds(args.Number("wait-timeout") ?? 60.0),
                cancellationToken: cancellationToken).ConfigureAwait(false)).Json;
        }
        // Also without --wait: a repeated request id tells the old outcome.
        var failed = PyText.AsString(result["status"]) == "failed";
        if (args.Flag("json"))
        {
            PrintJson(result);
        }
        else if (failed)
        {
            var cause = result["errorCode"] is null ? "" : $"error code {PyText.Str(result["errorCode"])}, ";
            _error.Write(
                $"smsdock: Sending to {PyText.Str(result["to"])} failed "
                + $"({cause}requestId {PyText.Str(result["requestId"])})\n");
        }
        else
        {
            PrintLine(
                $"{Capitalize(PyText.Str(result["status"]))}: {PyText.Str(result["to"])}, "
                + $"{PyText.Str(result["parts"])} part(s), requestId {PyText.Str(result["requestId"])}");
        }
        return failed ? ExitApiError : ExitOk;
    }

    private async Task<int> DeleteAsync(
        SmsDockClient client, ParsedArguments args, CancellationToken cancellationToken)
    {
        var result = args.Integer("message") is { } message
            ? (await client.DeleteMessageAsync(message, cancellationToken).ConfigureAwait(false)).Json
            : (await client.DeleteConversationAsync(args.Integer("conversation") ?? 0, cancellationToken)
                .ConfigureAwait(false)).Json;
        if (args.Flag("json"))
        {
            PrintJson(result);
            return ExitOk;
        }
        var where = PyText.AsString(result["mode"]) == "provider"
            ? "removed from the phone"
            : "hidden in SMS Dock, still on the phone";
        PrintLine($"Deleted {PyText.Str(result["deleted"])} message(s): {where}");
        return ExitOk;
    }

    private async Task<int> MarkReadAsync(
        SmsDockClient client, ParsedArguments args, CancellationToken cancellationToken)
    {
        var conversation = args.Integer("conversation") ?? 0;
        var result = await client.MarkReadAsync(conversation, cancellationToken).ConfigureAwait(false);
        if (args.Flag("json"))
        {
            PrintJson(result.Json);
        }
        else
        {
            PrintLine($"Marked conversation {conversation} as read");
        }
        return ExitOk;
    }

    private async Task<int> ContactsAsync(
        SmsDockClient client, ParsedArguments args, CancellationToken cancellationToken)
    {
        var limit = args.Integer("limit") ?? 50;
        var items = new List<JsonObject>();
        await foreach (var item in client.EnumerateContactsAsync(
            args.Text("search"), (int)Math.Min(limit, 200), (int)Math.Min(limit, int.MaxValue), cancellationToken)
            .ConfigureAwait(false))
        {
            items.Add(item.Json);
        }
        if (args.Flag("json"))
        {
            PrintJson(new JsonArray([.. items]));
            return ExitOk;
        }
        var rows = new List<string[]>();
        foreach (var contact in items)
        {
            var numbers = contact["numbers"] is JsonArray { Count: > 0 } list
                ? list.Select(Map)
                : [new JsonObject()];
            foreach (var number in numbers)
            {
                rows.Add([Cell(contact["name"]), Cell(number["number"]), Cell(number["label"])]);
            }
        }
        PrintTable(["NAME", "NUMBER", "LABEL"], rows);
        return ExitOk;
    }

    private async Task<int> ListenAsync(
        SmsDockClient client, ParsedArguments args, CancellationToken cancellationToken)
    {
        var names = args.Values.TryGetValue("events", out var chosen)
            ? (List<string>)chosen
            : ["sms.received"];
        var json = args.Flag("json");
        var limit = args.Integer("count");
        client.OnReconnect = ReportReconnectAsync;
        await client.SubscribeAsync(names, args.Text("address"), cancellationToken).ConfigureAwait(false);
        if (!json)
        {
            _error.Write($"Listening for {string.Join(", ", names)}. Press Ctrl+C to stop.\n");
        }
        long count = 0;
        await foreach (var message in client.EventsAsync(cancellationToken: cancellationToken)
            .ConfigureAwait(false))
        {
            PrintLine(json ? PyText.Line(message.Json) : Describe(message.Json));
            _out.Flush();
            count++;
            if (limit is not null && count >= limit)
            {
                break;
            }
        }
        return ExitOk;
    }

    /// <summary>Tells on standard error what a lost connection may have cost.</summary>
    private Task ReportReconnectAsync(SmsDockClient client, CancellationToken cancellationToken)
    {
        string lost;
        if (client.ServerRestarted)
        {
            lost = "the app was restarted, events may be missing";
        }
        else if (client.MissedEvents is { } missed and not 0)
        {
            lost = $"{missed} event(s) of the phone were not received";
        }
        else
        {
            lost = "no event was missed";
        }
        _error.Write($"smsdock: reconnected; {lost}\n");
        _error.Flush();
        return Task.CompletedTask;
    }

    private static string Cell(JsonNode? value) => value is null ? "" : PyText.Str(value);

    private void PrintTable(IReadOnlyList<string> headers, IEnumerable<string[]> rows)
    {
        var table = rows.ToList();
        if (table.Count == 0)
        {
            PrintLine("Nothing found.");
            return;
        }
        var widths = headers.Select(
            (header, index) => Math.Max(Length(header), table.Max(row => Length(row[index])))).ToArray();
        foreach (var row in table.Prepend([.. headers]))
        {
            var line = new StringBuilder();
            for (var index = 0; index < widths.Length; index++)
            {
                if (index > 0)
                {
                    line.Append("  ");
                }
                line.Append(row[index]).Append(' ', widths[index] - Length(row[index]));
            }
            PrintLine(TrimEnd(line.ToString()));
        }
    }

    /// <summary>Removes trailing whitespace as Python's <c>str.rstrip()</c> does.</summary>
    private static string TrimEnd(string line)
    {
        var end = line.Length;
        while (end > 0 && PyText.IsSpace(line[end - 1]))
        {
            end--;
        }
        return line[..end];
    }

    [GeneratedRegex(@"\A[A-Za-z][A-Za-z0-9+.\-]*\z")]
    private static partial Regex SchemePattern();
}
