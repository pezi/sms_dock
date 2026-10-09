using System.Diagnostics;
using AutoReply;

namespace SmsDock.Tests;

/// <summary>The examples run on their own against the fake server.</summary>
public class ExamplesTests : ServerTestBase
{
    private static string ExamplePath(string name)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        // .../tests/SmsDock.Tests/bin/<Configuration>/net10.0/
        var configuration = directory.Parent!.Name;
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "SmsDock.sln")))
        {
            directory = directory.Parent;
        }
        var path = Path.Combine(directory!.FullName, "examples", name, "bin", configuration, "net8.0", name + ".dll");
        Assert.True(File.Exists(path), $"{path} is missing; build the solution first");
        return path;
    }

    private Process Start(string name, bool withFingerprint, params string[] arguments)
    {
        var info = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        info.ArgumentList.Add(ExamplePath(name));
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }
        info.Environment["SMSDOCK_PASSWORD"] = Password;
        info.Environment.Remove("SMSDOCK_FINGERPRINT");
        if (withFingerprint)
        {
            info.Environment["SMSDOCK_FINGERPRINT"] = Server.Tls.Fingerprint;
        }
        return Process.Start(info)!;
    }

    /// <summary>Runs an example to its end.</summary>
    private async Task<(int Code, string Out, string Err)> RunAsync(
        string name, bool withFingerprint, params string[] arguments)
    {
        using var process = Start(name, withFingerprint, arguments);
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await process.WaitForExitAsync(limit.Token);
        return (process.ExitCode, await output, await errors);
    }

    /// <summary>Reads lines of the output, but does not wait for ever.</summary>
    private static async Task<List<string>> ReadLinesAsync(Process process, int count, double timeout = 20)
    {
        var lines = new List<string>();
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(timeout));
        try
        {
            while (lines.Count < count && await process.StandardOutput.ReadLineAsync(limit.Token) is { } line)
            {
                lines.Add(line);
            }
        }
        catch (OperationCanceledException)
        {
            // The caller sees too few lines.
        }
        return lines;
    }

    private static void Stop(Process process)
    {
        process.Kill();
        process.WaitForExit();
        process.Dispose();
    }

    [Fact]
    public async Task ListMessages()
    {
        var (code, output, errors) = await RunAsync("ListMessages", true, Server.Url);
        Assert.True(code == 0, errors);
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(10, lines.Length);
        Assert.EndsWith("Anna Example: Anna 24", lines[0]);
        Assert.EndsWith("Anna Example: Anna 6", lines[9]);
    }

    [Fact]
    public async Task SendSms()
    {
        var (code, output, errors) = await RunAsync("SendSms", true, Server.Url, Ben, "Hello");
        Assert.True(code == 0, errors);
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal($"1 part(s) for {Ben}", lines[0]);
        Assert.StartsWith("Sent, message id ", lines[1]);
        var sent = Assert.Single(Server.Sent);
        Assert.Equal((Ben, "Hello"), ((string?)sent["to"], (string?)sent["body"]));
    }

    [Fact]
    public async Task SendSmsRefused()
    {
        var (code, output, _) = await RunAsync("SendSms", true, Server.Url, "abc", "Hello");
        Assert.Equal(1, code);
        Assert.StartsWith("Not sent: invalid_number", output);
    }

    [Fact]
    public async Task ExamplesReportAMissingFingerprint()
    {
        var (code, _, errors) = await RunAsync("ListMessages", false, Server.Url);
        Assert.Equal(1, code);
        Assert.Contains("SMSDOCK_FINGERPRINT", errors);
        Assert.DoesNotContain("Unhandled", errors);
    }

    [Theory]
    [InlineData("ListMessages")]
    [InlineData("SendSms")]
    [InlineData("ListenIncoming")]
    [InlineData("AutoReply")]
    public async Task ExamplesExplainTheirUsage(string name)
    {
        var (code, output, _) = await RunAsync(name, true);
        Assert.Equal(2, code);
        Assert.Contains("Usage:", output);
    }

    [Fact]
    public async Task ListenIncoming()
    {
        var process = Start("ListenIncoming", true, Server.Url);
        List<string> lines;
        try
        {
            Assert.True(await WaitUntilAsync(() => Count(Server, "subscribe") > 0, 20));
            await Server.ReceiveSmsAsync(Anna, "STATUS");
            await Server.ReceiveSmsAsync("A1", "Your bill");
            lines = await ReadLinesAsync(process, 3);
        }
        finally
        {
            Stop(process);
        }
        Assert.StartsWith("Waiting for SMS", lines[0]);
        Assert.EndsWith("Anna Example: STATUS", lines[1]);
        Assert.EndsWith("A1: Your bill", lines[2]);
    }

    [Fact]
    public async Task ListenIncomingReadsWhatItMissed()
    {
        var process = Start("ListenIncoming", true, Server.Url);
        List<string> lines;
        try
        {
            Assert.True(await WaitUntilAsync(() => Count(Server, "subscribe") > 0, 20));
            await Server.ReceiveSmsAsync(Anna, "first");
            Assert.Equal(2, (await ReadLinesAsync(process, 2)).Count);
            await Server.DropConnectionsAsync();
            Assert.True(await WaitUntilAsync(() => Server.ClientCount == 0));
            await Server.ReceiveSmsAsync(Ben, "while away");
            await Server.RestartAsync();
            await Server.ReceiveSmsAsync(Anna, "after the restart");
            lines = await ReadLinesAsync(process, 2);
            Assert.True(await WaitUntilAsync(() => Server.ClientCount == 1, 20));
            await Server.ReceiveSmsAsync(Anna, "live again");
            lines.AddRange(await ReadLinesAsync(process, 1));
        }
        finally
        {
            Stop(process);
        }
        // Old messages of the phone are not shown, new ones only once.
        Assert.Equal(
            ["while away", "after the restart", "live again"], lines.Select(line => line.Split(": ", 2)[1]));
    }

    [Fact]
    public async Task AutoReplyAnswers()
    {
        var process = Start("AutoReply", true, Server.Url);
        List<string> lines;
        try
        {
            Assert.True(await WaitUntilAsync(() => Count(Server, "subscribe") > 0, 20));
            await Server.ReceiveSmsAsync(Anna, "status");
            await Server.ReceiveSmsAsync(Anna, "STATUS");
            await Server.ReceiveSmsAsync("A1", "Your bill");
            await Server.ReceiveSmsAsync("8810", "Your code is 1234");
            await Server.ReceiveSmsAsync(Ben, "Hello?", 2);
            lines = await ReadLinesAsync(process, 6);
        }
        finally
        {
            Stop(process);
        }
        Assert.Equal(6, lines.Count);
        Assert.StartsWith($"{Anna}: answered, requestId ", lines[1]);
        Assert.Equal($"{Anna}: no answer (answered a short time ago)", lines[2]);
        Assert.Equal("A1: no answer (not a phone number)", lines[3]);
        Assert.Equal("8810: no answer (not a phone number)", lines[4]);
        Assert.StartsWith($"{Ben}: answered, requestId ", lines[5]);
        Assert.Equal(
            [(Anna, "All systems running.", 1L), (Ben, "Commands: STATUS", 2L)],
            Server.Sent.Select(message => ((string)message["to"]!, (string)message["body"]!, (long)message["sim"]!)));
    }

    [Fact]
    public void ReplyGuardRules()
    {
        var guard = new ReplyGuard(600, 3);
        Assert.Null(guard.Refusal("+4369912345678", 0));
        Assert.NotNull(guard.Refusal("+4369912345678", 599));
        Assert.NotNull(guard.Refusal("+43 699 123-456.78", 599));
        Assert.Null(guard.Refusal("+4369912345678", 600));
        foreach (var sender in new[] { "A1", "Post", "8810", "12345", "", "+43 abc" })
        {
            Assert.Equal("not a phone number", guard.Refusal(sender, 700));
        }
        Assert.Null(guard.Refusal("+4369087654321", 700));
        // Three answers in the last hour: the fourth sender has to wait.
        Assert.Equal("too many answers in the last hour", guard.Refusal("+436601111111", 800));
        Assert.Null(guard.Refusal("+436601111111", 3600));
    }

    [Fact]
    public void Answers()
    {
        Assert.Equal("All systems running.", ReplyGuard.AnswerFor(" status\n"));
        Assert.Equal("Commands: STATUS", ReplyGuard.AnswerFor("Hello"));
    }
}
