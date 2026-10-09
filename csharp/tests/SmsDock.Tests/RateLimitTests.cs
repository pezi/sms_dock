using System.Diagnostics;
using System.Text.Json.Nodes;

namespace SmsDock.Tests;

/// <summary>The error <c>rate_limited</c>.</summary>
public class RateLimitTests : ServerTestBase
{
    private static JsonObject RetryAfter(int seconds) => new() { ["retryAfter"] = seconds };

    [Fact]
    public async Task WaitsAndRepeatsWhenRateLimited()
    {
        var client = await ConnectAsync(recordPauses: true);
        Server.FailNext("status", "rate_limited", "Too many requests.", extra: RetryAfter(1));
        Assert.Equal("hidden", (await client.StatusAsync()).DeleteMode);
        Assert.Equal([1.0], Pauses);
        Server.FailNext("status", "rate_limited", extra: RetryAfter(1));
        Server.FailNext("status", "rate_limited", extra: RetryAfter(3));
        Assert.Equal("hidden", (await client.StatusAsync()).DeleteMode);
        Assert.Equal([1.0, 1.0, 3.0], Pauses);
        Assert.Equal(5, Count(Server, "status"));
        Assert.Equal(1, client.ConnectionCount);
    }

    [Fact]
    public async Task GivesUpWhenRateLimitedAgainAndAgain()
    {
        var client = await ConnectAsync(recordPauses: true);
        for (var index = 0; index < 3; index++)
        {
            Server.FailNext("status", "rate_limited", extra: RetryAfter(1));
        }
        var error = await Assert.ThrowsAsync<ApiException>(() => client.StatusAsync());
        Assert.Equal("rate_limited", error.Code);
        Assert.Equal(1, error.RetryAfter);
        Assert.Equal([1.0, 1.0], Pauses);
        Assert.Equal(3, Count(Server, "status"));
        await client.PingAsync();
    }

    [Fact]
    public async Task LimitOfMessagesPerMinuteIsReportedAtOnce()
    {
        var client = await ConnectAsync(recordPauses: true);
        // The limit of 20 SMS parts per minute names no seconds to wait.
        Server.FailNext("messages.send", "rate_limited", "Too many messages per minute. Try again shortly.");
        var error = await Assert.ThrowsAsync<ApiException>(() => client.SendAsync(Ben, "text"));
        Assert.Equal("rate_limited", error.Code);
        Assert.Null(error.RetryAfter);
        Assert.Empty(Pauses);
        Assert.Empty(Server.Sent);
    }

    [Theory]
    [InlineData(0, 1, true)]
    [InlineData(2, 6, true)]
    [InlineData(2, 1, false)]
    public async Task RateLimitWithoutARepeat(int retries, int retryAfter, bool retry)
    {
        var client = await ConnectAsync(configure: options => options.RateLimitRetries = retries, recordPauses: true);
        Server.FailNext("status", "rate_limited", extra: RetryAfter(retryAfter));
        var error = await Assert.ThrowsAsync<ApiException>(() => client.RequestAsync("status", retry: retry));
        Assert.Equal("rate_limited", error.Code);
        Assert.Empty(Pauses);
    }

    [Fact]
    public async Task SendIsNotRepeatedWhenRateLimited()
    {
        var client = await ConnectAsync(recordPauses: true);
        Server.FailNext("messages.send", "rate_limited", extra: RetryAfter(1));
        var result = await client.SendAsync(Ben, "Only once");
        Assert.Equal([1.0], Pauses);
        var requests = Server.Requests.Where(request => (string?)request["action"] == "messages.send");
        Assert.Equal([result.RequestId, result.RequestId], requests.Select(request => (string?)request["requestId"]));
        Assert.Equal(["Only once"], Server.Sent.Select(message => (string?)message["body"]));
    }

    [Fact]
    public async Task StaysWithinTheLimitOfTheServer()
    {
        var server = StartServer(new FakeOptions { RequestsPerSecond = 5 });
        var client = await ConnectAsync(server);
        var watch = Stopwatch.StartNew();
        for (var index = 0; index < 9; index++)
        {
            Assert.True((await client.PingAsync()).Time > 0);
        }
        // The login and 4 requests fit into the first second, 5 into the next.
        Assert.True(server.RefusedCount >= 1);
        Assert.True(watch.Elapsed.TotalSeconds >= 0.9);
        Assert.Equal(9, Count(server, "ping"));
        Assert.Equal(1, client.ConnectionCount);
    }
}
