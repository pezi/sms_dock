namespace SmsDock.Tests;

/// <summary>Lost connections, close codes, epoch and sequence numbers.</summary>
public class ReconnectTests : ServerTestBase
{
    [Fact]
    public async Task ReconnectsAndLogsInAgain()
    {
        var client = await ConnectAsync();
        await client.PingAsync();
        await Server.DropConnectionsAsync();
        Assert.Equal("hidden", (await client.StatusAsync()).DeleteMode);
        Assert.Equal(2, client.ConnectionCount);
        Assert.Equal(2, Server.LoginCount);
        Assert.Equal(["authenticate", "ping", "authenticate", "status"], Server.Actions);
    }

    [Fact]
    public async Task ReconnectsAfterACloseFrame()
    {
        var client = await ConnectAsync();
        await Server.DropConnectionsAsync(1001, "server is stopping");
        await client.PingAsync();
        await Server.DropConnectionsAsync(4408, "no answer to pings");
        await client.PingAsync();
        await Server.DropConnectionsAsync(4429, "too many requests");
        await client.PingAsync();
        Assert.Equal(4, client.ConnectionCount);
    }

    [Fact]
    public async Task RequestLostBeforeItWasHandled()
    {
        var client = await ConnectAsync();
        Server.DropOn("status", "before");
        Server.DropOn("status", "before");
        Assert.Equal("hidden", (await client.StatusAsync()).DeleteMode);
        Assert.Equal(3, Count(Server, "status"));
        Assert.Equal(3, client.ConnectionCount);
    }

    [Fact]
    public async Task RestoresTheSubscription()
    {
        var client = await ConnectAsync();
        await client.SubscribeAsync(["sms.received", "sms.failed"], Ben);
        await Server.DropConnectionsAsync();
        await client.PingAsync();
        Assert.Equal(["authenticate", "subscribe", "authenticate", "subscribe", "ping"], Server.Actions);
        var restored = Server.Requests[3];
        AssertJson("""["sms.received","sms.failed"]""", restored["events"]);
        Assert.Equal(Ben, (string?)restored["address"]);
        await Server.ReceiveSmsAsync(Anna, "filtered out");
        await Server.ReceiveSmsAsync(Ben, "after the reconnect");
        var received = await client.WaitForEventAsync("sms.received", TimeSpan.FromSeconds(2));
        Assert.Equal("after the reconnect", received.Message!.Body);
        Assert.Equal(0, client.PendingEvents);
    }

    [Fact]
    public async Task DoesNotRestoreAnEmptySubscription()
    {
        var client = await ConnectAsync();
        await client.SubscribeAsync("sms.received");
        await client.UnsubscribeAsync("sms.received");
        await Server.DropConnectionsAsync();
        await client.PingAsync();
        Assert.Equal(1, Count(Server, "subscribe"));
    }

    [Fact]
    public async Task WaitingForEventsReconnects()
    {
        var client = await ConnectAsync();
        await client.SubscribeAsync("sms.received");
        await Server.DropConnectionsAsync();
        var arrive = Task.Run(async () =>
        {
            if (await WaitUntilAsync(() => Count(Server, "subscribe") == 2))
            {
                await Server.ReceiveSmsAsync(Anna, "after the reconnect");
            }
        });
        var events = new List<SmsEvent>();
        await foreach (var incoming in client.EventsAsync(TimeSpan.FromSeconds(5)))
        {
            events.Add(incoming);
            break;
        }
        await arrive;
        Assert.Equal("after the reconnect", events[0].Message!.Body);
        Assert.Equal(2, client.ConnectionCount);
    }

    [Fact]
    public async Task SendIsNotRepeatedAfterALostResponse()
    {
        var client = await ConnectAsync();
        Server.DropOn("messages.send", "after");
        var result = await client.SendAsync(Ben, "Only once");
        // The repeat tells the status of now; the message may be out already.
        Assert.Contains(result.Status, new[] { "queued", "sent" });
        var requests = Server.Requests.Where(request => (string?)request["action"] == "messages.send").ToList();
        Assert.Equal(2, requests.Count);
        Assert.Equal(result.RequestId, (string?)requests[0]["requestId"]);
        Assert.Equal(result.RequestId, (string?)requests[1]["requestId"]);
        Assert.NotEqual((string?)requests[0]["id"], (string?)requests[1]["id"]);
        Assert.Equal(2, client.ConnectionCount);
        Assert.Equal("sent", (await client.WaitForResultAsync(result.RequestId, TimeSpan.FromSeconds(5))).Status);
        // The radio was used once although the request arrived twice.
        Assert.Equal(["Only once"], Server.Sent.Select(message => (string?)message["body"]));
        Assert.Single(Server.Messages, message => (string?)message["body"] == "Only once");
    }

    [Fact]
    public async Task WaitForResultSurvivesAReconnect()
    {
        var server = StartServer(new FakeOptions { SendDelay = 0.3 });
        var client = await ConnectAsync(server);
        await client.SubscribeAsync(["sms.sent", "sms.failed"]);
        var requestId = (await client.SendAsync(Ben, "text")).RequestId;
        await server.DropConnectionsAsync();
        var entry = await client.WaitForResultAsync(
            requestId, TimeSpan.FromSeconds(5), pollInterval: TimeSpan.FromMilliseconds(100));
        Assert.Equal("sent", entry.Status);
        Assert.Single(server.Sent);
    }

    [Fact]
    public async Task GivesUpAfterTheLastAttempt()
    {
        var server = StartServer();
        var client = await ConnectAsync(server, options =>
        {
            options.ReconnectAttempts = 3;
            options.ConnectTimeout = TimeSpan.FromSeconds(2);
        });
        await server.StopAsync();
        var error = await Assert.ThrowsAsync<ConnectionInterruptedException>(() => client.StatusAsync());
        Assert.Contains("'status'", error.Message);
        Assert.NotNull(error.InnerException);
        Assert.False(client.Connected);
    }

    [Fact]
    public async Task BackoffIsBounded()
    {
        var server = StartServer();
        var client = await ConnectAsync(
            server,
            options =>
            {
                options.ReconnectAttempts = 6;
                options.ReconnectDelay = TimeSpan.FromSeconds(1);
                options.MaxReconnectDelay = TimeSpan.FromSeconds(10);
                options.ConnectTimeout = TimeSpan.FromSeconds(2);
            },
            recordPauses: true);
        await server.StopAsync();
        await Assert.ThrowsAsync<ConnectionInterruptedException>(() => client.StatusAsync());
        Assert.Equal([1.0, 2.0, 4.0, 8.0, 10.0, 10.0], Pauses);
    }

    [Fact]
    public async Task NoRetryOnRequest()
    {
        var client = await ConnectAsync();
        Server.DropOn("status", "before");
        await Assert.ThrowsAsync<ConnectionInterruptedException>(() => client.RequestAsync("status", retry: false));
        Assert.Equal(1, client.ConnectionCount);
        await client.StatusAsync();
        Assert.Equal(2, client.ConnectionCount);
    }

    [Fact]
    public async Task ZeroReconnectAttempts()
    {
        var client = await ConnectAsync(configure: options => options.ReconnectAttempts = 0);
        Server.DropOn("status", "before");
        await Assert.ThrowsAsync<ConnectionInterruptedException>(() => client.StatusAsync());
        Assert.Equal(1, client.ConnectionCount);
    }

    [Fact]
    public async Task PipelinedRequestsDoNotReconnect()
    {
        var client = await ConnectAsync();
        Server.DropOn("status", "after");
        var requestId = await client.SendRequestAsync("status");
        await Assert.ThrowsAsync<ConnectionInterruptedException>(
            () => client.WaitForResponseAsync(requestId, TimeSpan.FromSeconds(2)));
        Assert.Equal(1, client.ConnectionCount);
    }

    [Theory]
    [InlineData(4410, typeof(AuthenticationException), "not_authenticated")]
    [InlineData(4401, typeof(AuthenticationException), "not_authenticated")]
    [InlineData(4403, typeof(LicenceRequiredException), "licence_required")]
    public async Task CloseCodesThatForbidARetry(int code, Type kind, string errorCode)
    {
        var client = await ConnectAsync();
        await Server.DropConnectionsAsync(code);
        var error = (ApiException)await Assert.ThrowsAsync(kind, () => client.StatusAsync());
        Assert.Equal(errorCode, error.Code);
        Assert.Equal(code, (int?)error.Details["closeCode"]);
        // No new login with a password that may be wrong by now.
        Assert.Equal(1, Server.LoginCount);
        Assert.Equal(1, client.ConnectionCount);
    }

    [Fact]
    public async Task PasswordChangedWhileListening()
    {
        var client = await ConnectAsync();
        await client.SubscribeAsync("sms.received");
        var drop = Task.Run(async () =>
        {
            await Task.Delay(200);
            await Server.DropConnectionsAsync(4410, "password changed");
        });
        var error = await Assert.ThrowsAsync<AuthenticationException>(
            () => ListAsync(client.EventsAsync(TimeSpan.FromSeconds(5))));
        await drop;
        Assert.Contains("password changed", error.Message);
        Assert.Equal(1, Server.LoginCount);
    }

    [Fact]
    public async Task ProtocolErrorCloseCode()
    {
        var client = await ConnectAsync();
        await Server.DropConnectionsAsync(4400, "binary frame");
        var error = await Assert.ThrowsAsync<ProtocolException>(() => client.StatusAsync());
        Assert.Contains("binary frame", error.Message);
        Assert.Equal(1, client.ConnectionCount);
    }

    [Fact]
    public async Task RequestTimeoutReconnects()
    {
        var client = await ConnectAsync(configure: options =>
        {
            options.Timeout = TimeSpan.FromMilliseconds(300);
            options.ReconnectAttempts = 1;
        });
        Server.IgnoreNext("status");
        Assert.Equal("hidden", (await client.StatusAsync()).DeleteMode);
        Assert.Equal(2, client.ConnectionCount);
        Server.IgnoreNext("status", 2);
        var error = await Assert.ThrowsAsync<ConnectionInterruptedException>(() => client.StatusAsync());
        Assert.Contains("No response", error.Message);
        Assert.Equal(3, client.ConnectionCount);
    }

    [Fact]
    public async Task LoginTellsEpochAndSequenceNumber()
    {
        await Server.ReceiveSmsAsync(Anna, "one");
        await Server.ReceiveSmsAsync(Anna, "two");
        var client = NewClient(Server);
        Assert.Null(client.Epoch);
        Assert.Null(client.LatestSeq);
        await client.ConnectAsync();
        Assert.Equal(Server.Epoch, client.Epoch);
        Assert.Equal(2, client.LatestSeq);
        Assert.False(client.ServerRestarted);
        Assert.Equal(0, client.MissedEvents);
    }

    [Fact]
    public async Task LatestSeqFollowsTheEvents()
    {
        var client = await ConnectAsync();
        await client.SubscribeAsync("conversations.changed");
        await Server.ReceiveSmsAsync(Anna, "not subscribed");
        await Server.EmitAsync("conversations.changed", []);
        Assert.Equal(2, (await client.WaitForEventAsync("conversations.changed", TimeSpan.FromSeconds(2))).Seq);
        Assert.Equal(2, client.LatestSeq);
        // Events read while a request waits for its response count as well.
        Server.BeforeNextResponse(EventFrame(7));
        await client.PingAsync();
        Assert.Equal(7, client.LatestSeq);
    }

    [Fact]
    public async Task CountsTheEventsMissedWhileDisconnected()
    {
        var calls = new List<(long? Missed, bool Restarted, bool Connected, string? Subscription)>();
        var client = await ConnectAsync(configure: options => options.OnReconnect = (dock, _) =>
        {
            calls.Add((dock.MissedEvents, dock.ServerRestarted, dock.Connected, dock.Subscription?.ToString()));
            return Task.CompletedTask;
        });
        Assert.Empty(calls);
        await client.SubscribeAsync("sms.received");
        await Server.ReceiveSmsAsync(Anna, "seen");
        Assert.Equal(1, (await client.WaitForEventAsync("sms.received", TimeSpan.FromSeconds(2))).Seq);
        await Server.DropConnectionsAsync();
        Assert.True(await WaitUntilAsync(() => Server.ClientCount == 0));
        foreach (var body in new[] { "lost one", "lost two", "lost three" })
        {
            await Server.ReceiveSmsAsync(Anna, body);
        }
        await client.PingAsync();
        Assert.Equal(3, client.MissedEvents);
        Assert.False(client.ServerRestarted);
        Assert.Equal(4, client.LatestSeq);
        // The hook runs once the subscription is in force again.
        Assert.Equal([(3, false, true, """{"events":["sms.received"],"address":null}""")], calls);
        await Server.DropConnectionsAsync();
        await client.PingAsync();
        Assert.Equal(0, client.MissedEvents);
        Assert.Equal(2, calls.Count);
    }

    [Fact]
    public async Task NoticesARestartOfTheApp()
    {
        var client = await ConnectAsync();
        await client.SubscribeAsync("sms.received");
        for (var index = 0; index < 5; index++)
        {
            await Server.ReceiveSmsAsync(Anna, $"before {index}");
        }
        Assert.Equal(5, (await ListAsync(client.EventsAsync(TimeSpan.FromMilliseconds(300)))).Count);
        var first = client.Epoch;
        await Server.RestartAsync();
        await Server.ReceiveSmsAsync(Anna, "lost");
        await client.PingAsync();
        Assert.True(client.ServerRestarted);
        Assert.Null(client.MissedEvents);
        Assert.Equal(Server.Epoch, client.Epoch);
        Assert.NotEqual(first, client.Epoch);
        Assert.Equal(1, client.LatestSeq);
        await Server.ReceiveSmsAsync(Anna, "after");
        Assert.Equal(2, (await client.WaitForEventAsync("sms.received", TimeSpan.FromSeconds(2))).Seq);
        await Server.DropConnectionsAsync();
        await client.PingAsync();
        Assert.False(client.ServerRestarted);
        Assert.Equal(0, client.MissedEvents);
    }

    [Fact]
    public async Task HookReadsWhatWasMissed()
    {
        var seen = new List<string>();
        long newest = 0;

        void Handle(Message message)
        {
            seen.Add(message.Body);
            newest = Math.Max(newest, message.Timestamp);
        }

        var client = await ConnectAsync(configure: options => options.OnReconnect = async (dock, token) =>
        {
            var missed = await ListAsync(dock.EnumerateMessagesAsync(
                new MessageFilter { Direction = "in", Since = newest + 1 }, cancellationToken: token));
            missed.Reverse();
            missed.ForEach(Handle);
        });
        await client.SubscribeAsync("sms.received");
        await Server.ReceiveSmsAsync(Anna, "first");
        Handle((await client.WaitForEventAsync("sms.received", TimeSpan.FromSeconds(2))).Message!);
        await Server.DropConnectionsAsync();
        Assert.True(await WaitUntilAsync(() => Server.ClientCount == 0));
        await Task.Delay(10);
        await Server.ReceiveSmsAsync(Anna, "while away 1");
        await Task.Delay(10);
        await Server.ReceiveSmsAsync(Ben, "while away 2");
        var arrive = Task.Run(async () =>
        {
            if (await WaitUntilAsync(() => Count(Server, "subscribe") == 2))
            {
                await Task.Delay(50);
                await Server.ReceiveSmsAsync(Anna, "live again");
            }
        });
        await foreach (var incoming in client.EventsAsync(TimeSpan.FromSeconds(5)))
        {
            Handle(incoming.Message!);
            break;
        }
        await arrive;
        Assert.Equal(["first", "while away 1", "while away 2", "live again"], seen);
    }

    [Fact]
    public async Task HookCanBeSetLater()
    {
        var client = await ConnectAsync();
        var calls = new List<int>();
        client.OnReconnect = (dock, _) =>
        {
            calls.Add(dock.ConnectionCount);
            return Task.CompletedTask;
        };
        await Server.DropConnectionsAsync();
        await client.PingAsync();
        Assert.Equal([2], calls);
    }

    [Fact]
    public async Task AQuietConnectionThatAnswersIsKept()
    {
        var client = await ConnectAsync(configure: options =>
        {
            options.KeepAliveInterval = TimeSpan.FromMilliseconds(100);
            options.KeepAliveTimeout = TimeSpan.FromMilliseconds(300);
        });
        await client.SubscribeAsync("sms.received");
        Assert.Empty(await ListAsync(client.EventsAsync(TimeSpan.FromMilliseconds(900))));
        Assert.Equal(1, client.ConnectionCount);
        Assert.True(client.Connected);
        Assert.True(Count(Server, "ping") >= 3);
        // The answers to these questions are not mistaken for anything else.
        Assert.Equal(0, client.PendingEvents);
        Assert.Equal("hidden", (await client.StatusAsync()).DeleteMode);
    }

    [Fact]
    public async Task AServerThatAnswersNothingIsGivenUp()
    {
        var client = await ConnectAsync(configure: options =>
        {
            options.KeepAliveInterval = TimeSpan.FromMilliseconds(100);
            options.KeepAliveTimeout = TimeSpan.FromMilliseconds(300);
            options.ReconnectAttempts = 0;
        });
        await client.SubscribeAsync("sms.received");
        Server.IgnoreNext("ping", 100);
        var error = await Assert.ThrowsAsync<ConnectionInterruptedException>(
            () => ListAsync(client.EventsAsync(TimeSpan.FromSeconds(5))));
        Assert.Contains("does not answer", error.Message);
        Assert.False(client.Connected);
    }
}
