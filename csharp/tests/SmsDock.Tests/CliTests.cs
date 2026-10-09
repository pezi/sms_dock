using System.Text.Json.Nodes;
using SmsDock.Cli;

namespace SmsDock.Tests;

/// <summary>Tests of the command line <c>smsdock</c>.</summary>
public class CliTests : ServerTestBase
{
    private const string Warning = "smsdock: WARNING:";

    private string[] Arguments(string command, params string[] options) =>
        [command, Server.Url, "--fingerprint", Server.Tls.Fingerprint, "--timeout", "5", .. options];

    /// <summary>Runs the command line; returns exit code, output and errors without the insecure warning.</summary>
    private static async Task<(int Code, string Out, string Err)> RunAsync(
        string[] args,
        Dictionary<string, string>? variables = null,
        string input = "",
        bool terminal = false,
        Func<string, string?>? prompt = null,
        bool keepWarning = false,
        CancellationToken cancellationToken = default)
    {
        variables ??= new Dictionary<string, string> { ["SMSDOCK_PASSWORD"] = Password };
        var output = new StringWriter { NewLine = "\n" };
        var error = new StringWriter { NewLine = "\n" };
        var tool = new SmsDockCli(new CliEnvironment
        {
            GetVariable = name => variables.GetValueOrDefault(name),
            Input = new StringReader(input),
            InputIsTerminal = terminal,
            Out = output,
            Error = error,
            PasswordPrompt = prompt,
        });
        var code = await tool.RunAsync(args, cancellationToken);
        var errors = error.ToString();
        if (!keepWarning)
        {
            errors = string.Concat(
                errors.Split('\n').Where(line => !line.StartsWith(Warning, StringComparison.Ordinal))
                    .Select((line, index) => index == 0 ? line : "\n" + line));
        }
        return (code, output.ToString(), errors);
    }

    // ------------------------------------------------------------------
    // Arguments
    // ------------------------------------------------------------------

    [Fact]
    public void ParserReadsAllFlags()
    {
        var parsed = new ArgumentParser(80).Parse(
        [
            "messages", Url, "--fingerprint", Fingerprint, "--address", Anna, "--direction", "in", "--search",
            "invoice", "--limit", "10", "--unread", "--sim", "2", "--json", "--timeout", "7", "--since",
            "1790672531000", "--until", "2026-09-29T08:00", "--conversation", "179", "--reconnect-attempts", "2",
        ]);
        Assert.Equal("messages", parsed.Command!.Name);
        Assert.Equal(Url, parsed.Text("url"));
        Assert.Equal(Fingerprint, parsed.Text("fingerprint"));
        Assert.Equal(Anna, parsed.Text("address"));
        Assert.Equal("in", parsed.Text("direction"));
        Assert.Equal("invoice", parsed.Text("search"));
        Assert.Equal(10, parsed.Integer("limit"));
        Assert.True(parsed.Flag("unread"));
        Assert.Equal(2, parsed.Integer("sim"));
        Assert.True(parsed.Flag("json"));
        Assert.Equal(7.0, parsed.Number("timeout"));
        Assert.Equal(1790672531000, parsed.Integer("since"));
        Assert.True(parsed.Integer("until") > 1_700_000_000_000);
        Assert.Equal(179, parsed.Integer("conversation"));
        Assert.Equal(2, parsed.Integer("reconnect-attempts"));
        Assert.False(parsed.Flag("insecure"));
    }

    [Fact]
    public void ParserLeavesOutWhatWasNotGiven()
    {
        var parsed = new ArgumentParser(80).Parse(["messages", Url]);
        Assert.Equal(["url"], parsed.Values.Keys);
        var listen = new ArgumentParser(80).Parse(["listen", Url, "--events", "sms.sent", "*", "--count", "3"]);
        Assert.Equal(["sms.sent", "*"], (List<string>)listen.Values["events"]);
        Assert.Equal(3, listen.Integer("count"));
    }

    [Theory]
    [InlineData("2026-09-29", 2026, 9, 29, 0, 0, 0)]
    [InlineData("2026-09-29T08:00", 2026, 9, 29, 8, 0, 0)]
    [InlineData("2026-09-29 08:00:30", 2026, 9, 29, 8, 0, 30)]
    public void TimestampsAreLocalTime(string text, int year, int month, int day, int hour, int minute, int second)
    {
        var local = new DateTime(year, month, day, hour, minute, second, DateTimeKind.Local);
        Assert.Equal(new DateTimeOffset(local).ToUnixTimeMilliseconds(), Timestamps.Parse(text));
    }

    [Fact]
    public void TimestampForms()
    {
        Assert.Equal(1790672531000, Timestamps.Parse("1790672531000"));
        Assert.Equal(1790668800000, Timestamps.Parse("2026-09-29T08:00:00Z"));
        Assert.Equal(1790668800000, Timestamps.Parse("2026-09-29T10:00+02:00"));
        Assert.Equal(1790668800500, Timestamps.Parse("2026-09-29T08:00:00.500+00:00"));
        Assert.Null(Timestamps.Parse("yesterday"));
        Assert.Null(Timestamps.Parse("2026-13-01"));
        Assert.Null(Timestamps.Parse(""));
    }

    [Theory]
    [InlineData()]
    [InlineData("unknown")]
    [InlineData("status")]
    [InlineData("status", Url, "--password", "secret123")]
    [InlineData("messages", Url, "--direction", "sideways")]
    [InlineData("messages", Url, "--limit", "0")]
    [InlineData("messages", Url, "--limit", "many")]
    [InlineData("messages", Url, "--since", "yesterday")]
    [InlineData("send", Url, "--text", "no recipient")]
    [InlineData("delete", Url)]
    [InlineData("delete", Url, "--message", "1", "--conversation", "2")]
    [InlineData("mark-read", Url)]
    [InlineData("mark-read", Url, "--conversation", "first")]
    [InlineData("listen", Url, "--events", "sms.unknown")]
    [InlineData("fingerprint")]
    public async Task UsageErrors(params string[] args)
    {
        var (code, output, errors) = await RunAsync(args);
        Assert.Equal(2, code);
        Assert.Equal("", output);
        Assert.StartsWith("usage: smsdock", errors);
        Assert.Contains(": error: ", errors);
    }

    [Fact]
    public async Task UsageErrorsAreThoseOfThePythonTool()
    {
        var (_, _, errors) = await RunAsync(["delete", Url, "--message", "1", "--conversation", "2"]);
        Assert.Equal(
            "usage: smsdock delete [-h] [--fingerprint HEX] [--insecure]\n"
            + "                      [--password-env NAME] [--timeout SECONDS]\n"
            + "                      [--reconnect-attempts N] [--json] (--message ID |\n"
            + "                      --conversation ID)\n"
            + "                      url\n"
            + "smsdock delete: error: argument --conversation: not allowed with argument --message\n",
            errors);
        (_, _, errors) = await RunAsync(["unknown"]);
        Assert.Equal(
            "usage: smsdock [-h]\n"
            + "               {fingerprint,status,conversations,messages,send,delete,mark-read,contacts,listen} ...\n"
            + "smsdock: error: argument command: invalid choice: 'unknown' (choose from 'fingerprint', 'status', "
            + "'conversations', 'messages', 'send', 'delete', 'mark-read', 'contacts', 'listen')\n",
            errors);
    }

    [Fact]
    public async Task Help()
    {
        var (code, output, _) = await RunAsync(["--help"]);
        Assert.Equal(0, code);
        foreach (var command in ArgumentParser.Commands)
        {
            Assert.Contains(command.Name, output);
        }
        Assert.Contains("SMSDOCK_PASSWORD", output);
        Assert.StartsWith("usage: smsdock [-h]\n", output);
        (code, output, _) = await RunAsync(["send", "--help"]);
        Assert.Equal(0, code);
        Assert.DoesNotContain("--password ", output);
        Assert.Contains(
            "  --wait-timeout SECONDS\n                        longest time to wait with --wait (default: 60)\n",
            output);
    }

    [Fact]
    public async Task ThePasswordIsNoArgument()
    {
        var (code, _, errors) = await RunAsync(
            ["status", Url, "--fingerprint", Fingerprint, "--password", Password]);
        Assert.Equal(2, code);
        Assert.Contains("unrecognized arguments: --password secret123", errors);
    }

    [Theory]
    [InlineData("ws://192.168.1.50:9443/ws")]
    [InlineData("https://192.168.1.50:8443/")]
    [InlineData("192.168.1.50")]
    public async Task RefusesOtherUrls(string url)
    {
        var (code, output, errors) = await RunAsync(["status", url, "--fingerprint", Fingerprint]);
        Assert.Equal(2, code);
        Assert.Equal("smsdock: SMS Dock needs a wss:// URL, for example wss://192.168.1.50:9443/ws\n", errors);
        Assert.Equal("", output);
    }

    [Fact]
    public async Task NeedsAFingerprint()
    {
        var (code, _, errors) = await RunAsync(["status", Url]);
        Assert.Equal(2, code);
        Assert.Contains("--fingerprint", errors);
        Assert.Contains("SMSDOCK_FINGERPRINT", errors);
    }

    [Fact]
    public async Task RefusesAMalformedFingerprint()
    {
        var (code, _, errors) = await RunAsync(["status", Url, "--fingerprint", "AB:CD"]);
        Assert.Equal(2, code);
        Assert.Contains("SHA-256", errors);
    }

    [Fact]
    public async Task NeedsAPassword()
    {
        var (code, _, errors) = await RunAsync(["status", Url, "--fingerprint", Fingerprint], variables: []);
        Assert.Equal(2, code);
        Assert.Equal("smsdock: No password. Set the environment variable SMSDOCK_PASSWORD.\n", errors);
    }

    [Fact]
    public async Task AsksForThePasswordOnATerminal()
    {
        string? asked = null;
        var (code, output, _) = await RunAsync(
            Arguments("status", "--json"),
            variables: [],
            terminal: true,
            prompt: text =>
            {
                asked = text;
                return Password;
            });
        Assert.Equal(0, code);
        Assert.Equal("SMS Dock password: ", asked);
        Assert.Equal("hidden", (string?)JsonNode.Parse(output)!["deleteMode"]);
    }

    [Fact]
    public async Task OtherPasswordVariable()
    {
        var variables = new Dictionary<string, string>
        {
            ["SMSDOCK_PASSWORD"] = "wrong-password", ["OFFICE_PHONE"] = Password,
        };
        var (code, _, _) = await RunAsync(Arguments("status", "--password-env", "OFFICE_PHONE"), variables);
        Assert.Equal(0, code);
    }

    [Theory]
    [InlineData("192.168.1.50", "192.168.1.50", 9443)]
    [InlineData("192.168.1.50:9555", "192.168.1.50", 9555)]
    [InlineData("phone.local:9443", "phone.local", 9443)]
    [InlineData("wss://192.168.1.50:9555/ws", "192.168.1.50", 9555)]
    [InlineData("wss://192.168.1.50/ws", "192.168.1.50", 9443)]
    [InlineData("[fe80::1]:9555", "fe80::1", 9555)]
    [InlineData("fe80::1", "fe80::1", 9443)]
    public void HostAndPort(string text, string host, int port) =>
        Assert.Equal((host, port), SmsDockCli.HostAndPort(text));

    [Theory]
    [InlineData("")]
    [InlineData(":9443")]
    [InlineData("192.168.1.50:port")]
    public void HostAndPortErrors(string text) =>
        Assert.Throws<UsageException>(() => SmsDockCli.HostAndPort(text));

    // ------------------------------------------------------------------
    // Commands
    // ------------------------------------------------------------------

    [Fact]
    public async Task FingerprintCommand()
    {
        var (code, output, errors) = await RunAsync(["fingerprint", $"127.0.0.1:{Server.Port}"]);
        Assert.Equal(0, code);
        Assert.Equal(Server.Tls.Fingerprint + "\n", output);
        Assert.Equal(
            "Compare this fingerprint with the Server screen of the app. Use it only if both are the same.\n",
            errors);
        (code, output, _) = await RunAsync(["fingerprint", Server.Url]);
        Assert.Equal((0, Server.Tls.Fingerprint + "\n"), (code, output));
        Assert.Empty(Server.Requests);
    }

    [Fact]
    public async Task FingerprintOfNothing()
    {
        var server = StartServer();
        await server.StopAsync();
        var (code, output, errors) = await RunAsync(["fingerprint", $"127.0.0.1:{server.Port}", "--timeout", "2"]);
        Assert.Equal(3, code);
        Assert.Equal("", output);
        Assert.StartsWith("smsdock: ", errors);
    }

    [Fact]
    public async Task Status()
    {
        var (code, output, errors) = await RunAsync(Arguments("status"));
        Assert.Equal(0, code);
        Assert.Equal(
            "App:             SMS Dock 1.0.0 (protocol 1)\n"
            + "Default SMS app: no\n"
            + "Delete mode:     hidden\n"
            + "Licence:         not enforced\n"
            + "Permissions:     all granted\n"
            + "\n"
            + "SIM  SLOT  NAME  NUMBER          DEFAULT\n"
            + "1    0     Play                  yes\n"
            + "2    1     Work  +4369087654321  no\n",
            output);
        Assert.DoesNotContain(Password, output + errors);
    }

    [Fact]
    public async Task StatusJson()
    {
        var (code, output, _) = await RunAsync(Arguments("status", "--json"));
        Assert.Equal(0, code);
        var status = JsonNode.Parse(output)!;
        Assert.Equal(1, (int?)status["app"]!["protocolVersion"]);
        Assert.Equal(2, status["sims"]!.AsArray().Count);
        Assert.StartsWith(
            "{\n  \"app\": {\n    \"name\": \"SMS Dock\",\n    \"version\": \"1.0.0\",\n    \"protocolVersion\": 1\n  },\n"
            + "  \"sims\": [\n    {\n      \"subscriptionId\": 1,\n      \"slot\": 0,\n      \"name\": \"Play\",\n"
            + "      \"number\": null,\n",
            output);
        Assert.EndsWith("  \"popup\": true\n}\n", output);
    }

    [Fact]
    public async Task Conversations()
    {
        var (code, output, _) = await RunAsync(Arguments("conversations"));
        Assert.Equal(0, code);
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(
            ["ID", "TIME", "NUMBER", "NAME", "MSGS", "UNREAD", "DIR", "TEXT"],
            lines[0].Split(' ', StringSplitOptions.RemoveEmptyEntries));
        Assert.Equal(4, lines.Length);
        Assert.Contains(Anna, lines[1]);
        Assert.Contains("Anna Example", lines[1]);
        Assert.EndsWith("Anna 24", lines[1]);
        Assert.Contains("A1", lines[3]);

        (code, output, _) = await RunAsync(
            Arguments("conversations", "--json", "--unread", "--search", "bill", "--limit", "5"));
        Assert.Equal(0, code);
        Assert.Equal(["A1"], JsonNode.Parse(output)!.AsArray().Select(item => (string?)item!["address"]));
        (_, output, _) = await RunAsync(Arguments("conversations", "--sim", "2", "--json"));
        Assert.Equal([Ben], JsonNode.Parse(output)!.AsArray().Select(item => (string?)item!["address"]));
    }

    [Fact]
    public async Task LastTenIncomingMessages()
    {
        var (code, output, _) = await RunAsync(Arguments("messages", "--direction", "in", "--limit", "10"));
        Assert.Equal(0, code);
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(11, lines.Length);
        var time = TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeMilliseconds(BaseTime + 24000), TimeZoneInfo.Local);
        var id = (long)Server.Messages.Single(message => (string?)message["body"] == "Anna 24")["id"]!;
        Assert.Equal("ID    TIME              DIR  NUMBER          NAME          STATUS    TEXT", lines[0]);
        Assert.Equal(
            $"{id}  {time:yyyy-MM-dd HH:mm}  in   {Anna}  Anna Example  received  Anna 24", lines[1]);
        Assert.All(lines.Skip(1), line => Assert.Contains(" in ", line));
        Assert.EndsWith("received  Anna 6", lines[10]);
    }

    [Fact]
    public async Task MessagesJsonAndFilters()
    {
        var (code, output, _) = await RunAsync(Arguments(
            "messages", "--json", "--address", Ben, "--search", "invoice", "--direction", "out", "--sim", "2"));
        Assert.Equal(0, code);
        Assert.Equal(
            ["Your invoice is attached"], JsonNode.Parse(output)!.AsArray().Select(item => (string?)item!["body"]));
        var request = LastRequest(Server, "messages.list");
        Assert.Equal(Ben, (string?)request["address"]);
        Assert.Equal("invoice", (string?)request["q"]);
        Assert.Equal("out", (string?)request["direction"]);
        Assert.Equal(2, (int?)request["sim"]);

        (_, output, _) = await RunAsync(Arguments("messages", "--json", "--unread"));
        Assert.Equal(4, JsonNode.Parse(output)!.AsArray().Count);
        (_, output, _) = await RunAsync(Arguments("messages", "--json", "--since", "1790672553000"));
        Assert.Equal(
            ["Anna 24", "Anna 23", "Anna 22"], JsonNode.Parse(output)!.AsArray().Select(item => (string?)item!["body"]));
    }

    [Fact]
    public async Task MessagesLimitOverOnePage()
    {
        for (var index = 0; index < 230; index++)
        {
            Server.AddMessage(Anna, $"More {index}");
        }
        var (code, output, _) = await RunAsync(Arguments("messages", "--json", "--limit", "220"));
        Assert.Equal(0, code);
        Assert.Equal(220, JsonNode.Parse(output)!.AsArray().Count);
        Assert.Equal(
            [200, 20],
            Server.Requests.Where(request => (string?)request["action"] == "messages.list")
                .Select(request => (int)request["limit"]!));
    }

    [Fact]
    public async Task NothingFound()
    {
        var (code, output, _) = await RunAsync(Arguments("messages", "--search", "no such text"));
        Assert.Equal((0, "Nothing found.\n"), (code, output));
        (code, output, _) = await RunAsync(Arguments("messages", "--search", "no such text", "--json"));
        Assert.Equal((0, "[]\n"), (code, output));
    }

    [Fact]
    public async Task LongTextIsCutInTables()
    {
        Server.AddMessage(Anna, "First line\nsecond line " + new string('x', 100));
        var (code, output, _) = await RunAsync(Arguments("messages", "--limit", "1"));
        Assert.Equal(0, code);
        var line = output.Split('\n')[1];
        Assert.EndsWith("  First line second line " + new string('x', 36) + "…", line);
        (_, output, _) = await RunAsync(Arguments("messages", "--limit", "1", "--json"));
        Assert.StartsWith("First line\nsecond line", (string?)JsonNode.Parse(output)![0]!["body"]);
    }

    [Fact]
    public async Task Send()
    {
        var (code, output, _) = await RunAsync(Arguments("send", "--to", Ben, "--text", "All systems running."));
        Assert.Equal(0, code);
        var requestId = (string?)Server.Sent[0]["requestId"];
        Assert.Equal($"Queued: {Ben}, 1 part(s), requestId {requestId}\n", output);
        Assert.Equal("All systems running.", (string?)Server.Sent[0]["body"]);
    }

    [Fact]
    public async Task SendFromStandardInput()
    {
        var (code, output, _) = await RunAsync(
            Arguments("send", "--to", Ben, "--sim", "2", "--request-id", "cron-1", "--json"),
            input: "Line one\nLine two\n");
        Assert.Equal(0, code);
        Assert.Equal(
            "{\n  \"requestId\": \"cron-1\",\n  \"to\": \"+4369087654321\",\n  \"parts\": 1,\n  \"status\": \"queued\"\n}\n",
            output);
        AssertJson(
            $$"""[{"requestId":"cron-1","to":"{{Ben}}","body":"Line one\nLine two","sim":2}]""",
            new JsonArray([.. Server.Sent]));
    }

    [Fact]
    public async Task SendNeedsAText()
    {
        var (code, _, errors) = await RunAsync(Arguments("send", "--to", Ben));
        Assert.Equal(2, code);
        Assert.Equal("smsdock: The text is empty.\n", errors);
        (code, _, errors) = await RunAsync(Arguments("send", "--to", Ben), terminal: true);
        Assert.Equal(2, code);
        Assert.Equal("smsdock: No text. Pass --text or write it to standard input.\n", errors);
        Assert.Empty(Server.Sent);
    }

    [Fact]
    public async Task SendAndWait()
    {
        var (code, output, _) = await RunAsync(Arguments("send", "--to", Ben, "--text", "Hello", "--wait"));
        Assert.Equal(0, code);
        Assert.StartsWith($"Sent: {Ben}, 1 part(s), requestId ", output);

        (code, output, _) = await RunAsync(Arguments("send", "--to", Ben, "--text", "Hello", "--wait", "--json"));
        Assert.Equal(0, code);
        var entry = JsonNode.Parse(output)!;
        Assert.Equal("sent", (string?)entry["status"]);
        Assert.Equal(1, (int?)entry["partsSent"]);
    }

    [Fact]
    public async Task SendAndWaitFailed()
    {
        Server.FailNumber("+43123456");
        var (code, output, errors) = await RunAsync(
            Arguments("send", "--to", "+43123456", "--text", "Hello", "--wait"));
        Assert.Equal(1, code);
        Assert.Equal("", output);
        var requestId = (string?)Server.Sent[0]["requestId"];
        Assert.Equal($"smsdock: Sending to +43123456 failed (error code 1, requestId {requestId})\n", errors);

        (code, output, _) = await RunAsync(
            Arguments("send", "--to", "+43123456", "--text", "Hello", "--wait", "--json"));
        Assert.Equal(1, code);
        Assert.Equal("failed", (string?)JsonNode.Parse(output)!["status"]);
    }

    [Fact]
    public async Task SendAgainTellsTheOldOutcome()
    {
        Server.FailNumber("+43123456");
        var command = Arguments("send", "--to", "+43123456", "--text", "Hello", "--request-id", "job-1");
        var (code, output, errors) = await RunAsync(command);
        Assert.Equal((0, "Queued: +43123456, 1 part(s), requestId job-1\n"), (code, output));
        Assert.True(await WaitUntilAsync(() => (string?)Server.Outbox("job-1")!["status"] == "failed"));
        Server.ClearFailNumbers();
        // The repeat sends nothing and reports the failure of the first attempt.
        (code, output, errors) = await RunAsync(command);
        Assert.Equal((1, ""), (code, output));
        Assert.Equal("smsdock: Sending to +43123456 failed (requestId job-1)\n", errors);
        Assert.Single(Server.Sent);

        var ok = Arguments("send", "--to", Ben, "--text", "Hello", "--request-id", "job-2", "--wait");
        Assert.Equal(0, (await RunAsync(ok)).Code);
        (code, output, _) = await RunAsync(ok[..^1]);
        Assert.Equal((0, $"Sent: {Ben}, 1 part(s), requestId job-2\n"), (code, output));
        Assert.Equal(2, Server.Sent.Count);
    }

    [Fact]
    public async Task SendAndWaitTimesOut()
    {
        var server = StartServer(new FakeOptions { SendDelay = 30 });
        var (code, _, errors) = await RunAsync(
        [
            "send", server.Url, "--fingerprint", server.Tls.Fingerprint, "--to", Ben, "--text", "Hello", "--wait",
            "--wait-timeout", "0.3",
        ]);
        Assert.Equal(3, code);
        Assert.StartsWith("smsdock: No result for the message '", errors);
        Assert.EndsWith("' in time; its status is 'queued'\n", errors);
    }

    [Fact]
    public async Task Delete()
    {
        var newest = Server.Messages.MaxBy(message => (long)message["timestamp"]!)!;
        var (code, output, _) = await RunAsync(Arguments("delete", "--message", newest["id"]!.ToString()));
        Assert.Equal(0, code);
        Assert.Equal("Deleted 1 message(s): hidden in SMS Dock, still on the phone\n", output);
        Assert.DoesNotContain(Server.Messages, message => (long)message["id"]! == (long)newest["id"]!);

        Server.DeleteMode = "provider";
        var conversation = Server.Messages[0]["conversationId"]!.ToString();
        (code, output, _) = await RunAsync(Arguments("delete", "--conversation", conversation));
        Assert.Equal(0, code);
        Assert.Equal("Deleted 24 message(s): removed from the phone\n", output);
        conversation = Server.Messages[0]["conversationId"]!.ToString();
        (_, output, _) = await RunAsync(Arguments("delete", "--conversation", conversation, "--json"));
        Assert.Equal("{\n  \"deleted\": 3,\n  \"mode\": \"provider\"\n}\n", output);
    }

    [Fact]
    public async Task MarkRead()
    {
        var conversation = Server.Messages[0]["conversationId"]!.ToString();
        var (code, output, errors) = await RunAsync(Arguments("mark-read", "--conversation", conversation));
        Assert.Equal((1, ""), (code, output));
        Assert.StartsWith("smsdock: not_default_app: ", errors);

        Server.DeleteMode = "provider";
        (code, output, _) = await RunAsync(Arguments("mark-read", "--conversation", conversation));
        Assert.Equal((0, $"Marked conversation {conversation} as read\n"), (code, output));
        Assert.All(
            Server.Messages.Where(message => message["conversationId"]!.ToString() == conversation),
            message => Assert.True((bool)message["read"]!));
        (code, output, _) = await RunAsync(Arguments("mark-read", "--conversation", conversation, "--json"));
        Assert.Equal((0, "{\n  \"ok\": true\n}\n"), (code, output));
    }

    [Fact]
    public async Task Contacts()
    {
        var (code, output, _) = await RunAsync(Arguments("contacts"));
        Assert.Equal(0, code);
        Assert.Equal(
            "NAME          NUMBER          LABEL\n"
            + $"Anna Example  {Anna}  Mobile\n"
            + $"Ben Example   {Ben}  Work\n",
            output);
        (_, output, _) = await RunAsync(Arguments("contacts", "--search", "ben", "--json"));
        AssertJson(
            $$"""[{"name":"Ben Example","numbers":[{"number":"{{Ben}}","label":"Work"}]}]""", JsonNode.Parse(output));
    }

    /// <summary>Lets messages arrive as soon as a client has subscribed.</summary>
    private Task IncomingLater(params (string Address, string Body)[] messages) => Task.Run(async () =>
    {
        if (await WaitUntilAsync(() => Count(Server, "subscribe") > 0, 5))
        {
            foreach (var (address, body) in messages)
            {
                await Server.ReceiveSmsAsync(address, body);
            }
        }
    });

    [Fact]
    public async Task Listen()
    {
        var arrive = IncomingLater((Anna, "STATUS"), ("A1", "Your bill"));
        var (code, output, errors) = await RunAsync(Arguments("listen", "--count", "2"));
        await arrive;
        Assert.Equal(0, code);
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
        Assert.Matches(@"^\d{4}-\d\d-\d\d \d\d:\d\d:\d\d  \+4369912345678 \(Anna Example\): STATUS$", lines[0]);
        Assert.EndsWith("  A1: Your bill", lines[1]);
        Assert.Equal("Listening for sms.received. Press Ctrl+C to stop.\n", errors);
        var request = LastRequest(Server, "subscribe");
        AssertJson("""["sms.received"]""", request["events"]);
        Assert.False(request.ContainsKey("address"));
    }

    [Fact]
    public async Task ListenJsonWithAddress()
    {
        var arrive = IncomingLater((Anna, "ignored"), (Ben, "wanted"));
        var (code, output, errors) = await RunAsync(
            Arguments("listen", "--json", "--address", Ben, "--count", "1"));
        await arrive;
        Assert.Equal(0, code);
        Assert.Equal("", errors);
        var received = JsonNode.Parse(output)!;
        Assert.Equal("sms.received", (string?)received["event"]);
        Assert.Equal("wanted", (string?)received["data"]!["message"]!["body"]);
        Assert.Single(output.Split('\n', StringSplitOptions.RemoveEmptyEntries));
        Assert.StartsWith("{\"event\": \"sms.received\", \"seq\": 2, \"data\": {\"message\": {\"id\": ", output);
    }

    [Fact]
    public async Task ListenToOtherEvents()
    {
        var later = Task.Run(async () =>
        {
            if (await WaitUntilAsync(() => Server.Requests.Count >= 2, 5))
            {
                await Server.EmitAsync("sms.delivered", new JsonObject
                {
                    ["requestId"] = "r1", ["id"] = 7, ["conversationId"] = null, ["to"] = Ben, ["ok"] = true,
                });
            }
        });
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var (code, output, errors) = await RunAsync(
            Arguments("listen", "--events", "sms.delivered", "sms.failed", "--count", "1"),
            cancellationToken: limit.Token);
        await later;
        Assert.Equal("Listening for sms.delivered, sms.failed. Press Ctrl+C to stop.\n", errors);
        Assert.Equal(0, code);
        // Values are written as the Python tool writes them: None, True.
        Assert.EndsWith($"  sms.delivered requestId=r1 id=7 conversationId=None to={Ben} ok=True\n", output);
    }

    [Fact]
    public async Task ListenReportsAReconnect()
    {
        var later = Task.Run(async () =>
        {
            if (!await WaitUntilAsync(() => Server.Requests.Count >= 2, 5))
            {
                return;
            }
            await Server.DropConnectionsAsync();
            await WaitUntilAsync(() => Server.ClientCount == 0);
            await Server.ReceiveSmsAsync(Anna, "lost");
            await Server.ReceiveSmsAsync(Anna, "lost too");
            if (await WaitUntilAsync(() => Server.Requests.Count >= 4, 5))
            {
                await Server.RestartAsync();
            }
            if (await WaitUntilAsync(() => Server.Requests.Count >= 6, 5))
            {
                await Server.ReceiveSmsAsync(Anna, "received");
            }
        });
        var (code, output, errors) = await RunAsync(Arguments("listen", "--count", "1"));
        await later;
        Assert.Equal(0, code);
        Assert.EndsWith(": received\n", output);
        Assert.Equal(
            "Listening for sms.received. Press Ctrl+C to stop.\n"
            + "smsdock: reconnected; 2 event(s) of the phone were not received\n"
            + "smsdock: reconnected; the app was restarted, events may be missing\n",
            errors);
    }

    [Fact]
    public async Task ListenStopsWithCtrlC()
    {
        using var interrupt = new CancellationTokenSource();
        var pressed = Task.Run(async () =>
        {
            await WaitUntilAsync(() => Count(Server, "subscribe") > 0, 5);
            interrupt.Cancel();
        });
        var (code, _, _) = await RunAsync(Arguments("listen"), cancellationToken: interrupt.Token);
        await pressed;
        Assert.Equal(130, code);
        Assert.True(await WaitUntilAsync(() => Server.ClientCount == 0));
    }

    // ------------------------------------------------------------------
    // Exit codes
    // ------------------------------------------------------------------

    [Fact]
    public async Task ExitCode1ForApiErrors()
    {
        var (code, output, errors) = await RunAsync(Arguments("delete", "--message", "1"));
        Assert.Equal((1, ""), (code, output));
        Assert.Equal("smsdock: not_found: No message with the id 1.\n", errors);

        (code, _, errors) = await RunAsync(Arguments("send", "--to", "abc", "--text", "x"));
        Assert.Equal(1, code);
        Assert.Contains("invalid_number", errors);

        Server.FailNext("messages.send", "quota_exceeded", "Daily limit reached.");
        (code, _, errors) = await RunAsync(Arguments("send", "--to", Ben, "--text", "x"));
        Assert.Equal(1, code);
        Assert.Equal("smsdock: quota_exceeded: Daily limit reached.\n", errors);
    }

    [Fact]
    public async Task ExitCode1ForAWrongPassword()
    {
        var (code, output, errors) = await RunAsync(
            Arguments("status"), new Dictionary<string, string> { ["SMSDOCK_PASSWORD"] = "wrong-password" });
        Assert.Equal((1, ""), (code, output));
        Assert.Equal("smsdock: invalid_password: Wrong password.\n", errors);
        Assert.Equal(1, Server.LoginCount);
    }

    [Fact]
    public async Task ExitCode1WithoutLicence()
    {
        Server.LicenceEnforced = true;
        var (code, _, errors) = await RunAsync(Arguments("status"));
        Assert.Equal(1, code);
        Assert.Contains("licence_required", errors);
    }

    [Fact]
    public async Task ExitCode3WhenNothingListens()
    {
        var server = StartServer();
        await server.StopAsync();
        var (code, output, errors) = await RunAsync(
        [
            "status", $"wss://127.0.0.1:{server.Port}/ws", "--fingerprint", Fingerprint, "--reconnect-attempts", "0",
        ]);
        Assert.Equal((3, ""), (code, output));
        Assert.StartsWith($"smsdock: Cannot connect to wss://127.0.0.1:{server.Port}/ws: ", errors);
    }

    [Fact]
    public async Task ExitCode3ForAnotherCertificate()
    {
        var other = TestCertificate.Other.Fingerprint;
        var (code, output, errors) = await RunAsync(["status", Server.Url, "--fingerprint", other]);
        Assert.Equal((3, ""), (code, output));
        Assert.Equal(
            "smsdock: The certificate of the server does not match the pinned fingerprint.\n"
            + $"  expected: {other}\n  received: {Server.Tls.Fingerprint}\n"
            + "Compare the fingerprint with the Server screen of the app. It changes when the certificate is "
            + "regenerated or the app is reinstalled.\n",
            errors);
        Assert.Empty(Server.Requests);
    }

    [Fact]
    public async Task FingerprintFromTheEnvironmentIsUsed()
    {
        var variables = new Dictionary<string, string>
        {
            ["SMSDOCK_PASSWORD"] = Password, ["SMSDOCK_FINGERPRINT"] = Server.Tls.Fingerprint.ToLowerInvariant(),
        };
        Assert.Equal(0, (await RunAsync(["status", Server.Url], variables)).Code);
    }

    [Fact]
    public async Task InsecureWarns()
    {
        var (code, output, errors) = await RunAsync(
            ["status", Server.Url, "--insecure", "--timeout", "5"], keepWarning: true);
        Assert.Equal(0, code);
        Assert.Equal(
            "smsdock: WARNING: the certificate of the server is not checked (insecure). Anyone on the network "
            + "can read the password and the messages.\n",
            errors);
        Assert.Contains("App:", output);
    }
}
