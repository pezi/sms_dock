using System.Diagnostics;
using System.Text.Json.Nodes;

namespace SmsDock.Tests;

/// <summary>Subscriptions, events and responses that arrive out of order.</summary>
public class EventsTests : ServerTestBase
{
    private static readonly TimeSpan s_short = TimeSpan.FromMilliseconds(300);

    private static IEnumerable<string?> Bodies(IEnumerable<SmsEvent> events) =>
        events.Select(item => item.Message?.Body);

    [Fact]
    public async Task SubscribeAndUnsubscribe()
    {
        var client = await ConnectAsync();
        Assert.Null(client.Subscription);
        AssertJson(
            """{"events":["sms.received"],"address":null}""", (await client.SubscribeAsync("sms.received")).Json);
        // subscribe adds names; its address replaces the filter.
        var expected = $$"""{"events":["sms.received","sms.sent","sms.failed"],"address":"{{Anna}}"}""";
        AssertJson(expected, (await client.SubscribeAsync(["sms.sent", "sms.failed"], Anna)).Json);
        AssertJson(expected, client.Subscription!.Json);
        Assert.Equal(Anna, client.Subscription.Address);
        AssertJson(
            $$"""{"events":["sms.received","sms.failed"],"address":"{{Anna}}"}""",
            (await client.UnsubscribeAsync(["sms.sent"])).Json);
        // Without address the filter is removed.
        AssertJson(
            """{"events":["sms.received","sms.queued","sms.failed"],"address":null}""",
            (await client.SubscribeAsync("sms.queued")).Json);
        Assert.False(LastRequest(Server, "subscribe").ContainsKey("address"));
        // When no name is left, the filter is removed too.
        await client.SubscribeAsync("sms.queued", Ben);
        AssertJson("""{"events":[],"address":null}""", (await client.UnsubscribeAsync("*")).Json);
        AssertJson("""{"events":[],"address":null}""", client.Subscription.Json);
        // In the answer * is replaced by the names it stands for.
        Assert.Equal(SmsDockApi.EventNames, (await client.SubscribeAsync("*")).Events);
        AssertJson("""{"id":"cs-8","action":"subscribe","events":["*"]}""", LastRequest(Server, "subscribe"));
        Assert.Equal(
            ["sms.received", "sms.queued", "sms.sent", "sms.delivered", "sms.failed"],
            (await client.UnsubscribeAsync(["sms.deleted", "conversations.changed"])).Events);
        await Assert.ThrowsAsync<ArgumentException>(() => client.SubscribeAsync(Array.Empty<string>()));
        var error = await Assert.ThrowsAsync<ApiException>(() => client.SubscribeAsync("sms.unknown"));
        Assert.Equal("invalid_request", error.Code);
    }

    [Fact]
    public async Task ANewConnectionGetsNoEvents()
    {
        var client = await ConnectAsync();
        await Server.ReceiveSmsAsync(Anna, "Nobody listens");
        Assert.Empty(await ListAsync(client.EventsAsync(TimeSpan.FromMilliseconds(200))));
    }

    [Fact]
    public async Task IncomingSms()
    {
        var client = await ConnectAsync();
        await client.SubscribeAsync("sms.received");
        var stored = await Server.ReceiveSmsAsync(Anna, "STATUS");
        var received = await client.WaitForEventAsync("sms.received", TimeSpan.FromSeconds(2));
        Assert.Equal("sms.received", received.Name);
        Assert.Equal(1, received.Seq);
        var message = received.Message!;
        Assert.True(JsonNode.DeepEquals(stored, message.Json));
        Assert.Equal(Anna, message.Address);
        Assert.Equal("Anna Example", message.Contact);
        Assert.Equal("in", message.Direction);
        Assert.False(message.Read);
        Assert.Equal("STATUS", (await client.GetMessageAsync(message.Id!.Value)).Body);
    }

    [Fact]
    public async Task ReactToAnIncomingSms()
    {
        var client = await ConnectAsync();
        await client.SubscribeAsync("sms.received");
        await Server.ReceiveSmsAsync(Anna, "STATUS");
        await foreach (var incoming in client.EventsAsync(TimeSpan.FromSeconds(2)))
        {
            await client.SendAsync(incoming.Message!.Address, "All systems running.");
            break;
        }
        var sent = Assert.Single(Server.Sent);
        Assert.Equal((Anna, "All systems running."), ((string?)sent["to"], (string?)sent["body"]));
    }

    [Fact]
    public async Task EventsBetweenRequestAndResponseAreQueued()
    {
        var client = await ConnectAsync();
        await client.SubscribeAsync("sms.received");
        Server.BeforeNextResponse(EventFrame(1, "first"), EventFrame(2, "second"));
        Assert.Equal("hidden", (await client.StatusAsync()).DeleteMode);
        Assert.Equal(2, client.PendingEvents);
        Server.BeforeNextResponse(EventFrame(3, "third"));
        Assert.True((await client.PingAsync()).Time > 0);
        Assert.Equal(3, client.PendingEvents);
        var events = await ListAsync(client.EventsAsync(TimeSpan.FromMilliseconds(100)));
        Assert.Equal([1L, 2L, 3L], events.Select(item => item.Seq));
        Assert.Equal(["first", "second", "third"], Bodies(events));
        Assert.Equal(0, client.PendingEvents);
    }

    [Fact]
    public async Task EventsEndAfterTheTimeout()
    {
        var client = await ConnectAsync();
        await client.SubscribeAsync("*");
        var watch = Stopwatch.StartNew();
        Assert.Empty(await ListAsync(client.EventsAsync(TimeSpan.FromMilliseconds(200))));
        Assert.InRange(watch.Elapsed.TotalSeconds, 0.15, 2);
        Assert.True(client.Connected);
    }

    [Fact]
    public async Task EventsCanBeCancelled()
    {
        var client = await ConnectAsync();
        await client.SubscribeAsync("*");
        using var cancel = new CancellationTokenSource(200);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => ListAsync(client.EventsAsync(cancellationToken: cancel.Token)));
        Assert.True((await client.PingAsync()).Time > 0);
    }

    [Fact]
    public async Task EventsArriveInOrder()
    {
        var client = await ConnectAsync();
        await client.SubscribeAsync("sms.received");
        for (var index = 0; index < 5; index++)
        {
            await Server.ReceiveSmsAsync(Anna, $"Message {index}");
        }
        var events = await ListAsync(client.EventsAsync(s_short));
        Assert.Equal(Enumerable.Range(0, 5).Select(index => $"Message {index}"), Bodies(events));
        Assert.Equal([1L, 2L, 3L, 4L, 5L], events.Select(item => item.Seq));
    }

    [Fact]
    public async Task WaitForEventKeepsTheOthers()
    {
        var client = await ConnectAsync();
        await client.SubscribeAsync("*");
        await Server.ReceiveSmsAsync(Anna, "one");
        await Server.EmitAsync("conversations.changed", []);
        await Server.ReceiveSmsAsync(Ben, "two");
        var changed = await client.WaitForEventAsync("conversations.changed", TimeSpan.FromSeconds(2));
        Assert.Empty(changed.Data);
        Assert.Equal(1, client.PendingEvents);
        var fromBen = await client.WaitForEventAsync(
            "sms.received", TimeSpan.FromSeconds(2), candidate => candidate.Message!.Address == Ben);
        Assert.Equal("two", fromBen.Message!.Body);
        Assert.Equal(1, client.PendingEvents);
        var one = await client.WaitForEventAsync(["sms.sent", "sms.received"], TimeSpan.FromSeconds(2));
        Assert.Equal("one", one.Message!.Body);
        Assert.Equal(0, client.PendingEvents);
    }

    [Fact]
    public async Task WaitForEventTimesOut()
    {
        var client = await ConnectAsync();
        await client.SubscribeAsync("*");
        await Server.EmitAsync("conversations.changed", []);
        var error = await Assert.ThrowsAsync<TimeoutException>(
            () => client.WaitForEventAsync("sms.received", TimeSpan.FromMilliseconds(200)));
        Assert.Contains("sms.received", error.Message);
        Assert.Equal(1, client.PendingEvents);
        Assert.True(client.Connected);
    }

    [Fact]
    public async Task AddressFilter()
    {
        var client = await ConnectAsync();
        await client.SubscribeAsync("sms.received", Ben);
        await Server.ReceiveSmsAsync(Anna, "from Anna");
        await Server.ReceiveSmsAsync(Ben, "from Ben");
        Assert.Equal(["from Ben"], Bodies(await ListAsync(client.EventsAsync(s_short))));
    }

    [Fact]
    public async Task AddressFilterComparesLikeThePhone()
    {
        var client = await ConnectAsync();
        // A number without country code matches every number it ends.
        await client.SubscribeAsync("sms.received", "0690 876 54 321");
        await Server.ReceiveSmsAsync(Ben, "international");
        await Server.ReceiveSmsAsync("0043 690 87654321", "with 00");
        await Server.ReceiveSmsAsync("+49 690 87654321", "another country");
        await Server.ReceiveSmsAsync("0690 876 54 322", "another number");
        Assert.Equal(
            ["international", "with 00", "another country"], Bodies(await ListAsync(client.EventsAsync(s_short))));
        // Two numbers with country code are the same or they are not.
        await client.SubscribeAsync("sms.received", "0043 690 876 54 321");
        await Server.ReceiveSmsAsync(Ben, "international");
        await Server.ReceiveSmsAsync("0690/87654321", "national");
        await Server.ReceiveSmsAsync("+49 690 87654321", "another country");
        Assert.Equal(["international", "national"], Bodies(await ListAsync(client.EventsAsync(s_short))));
        await client.SubscribeAsync("sms.received", "mybank");
        await Server.ReceiveSmsAsync("MyBank", "Your code is 1234");
        await Server.ReceiveSmsAsync("MyBank2", "other sender");
        Assert.Equal(["Your code is 1234"], Bodies(await ListAsync(client.EventsAsync(s_short))));
    }

    [Fact]
    public async Task AddressFilterOnAllEventsThatNameAPeer()
    {
        var server = StartServer(new FakeOptions { DeliveryReports = true });
        server.FailNumber("+43123456789");
        var client = await ConnectAsync(server);
        await client.SubscribeAsync("*", Ben);
        foreach (var number in new[] { Anna, Ben, "+43123456789" })
        {
            var sent = await client.SendAsync(number, "text");
            await client.WaitForResultAsync(sent.RequestId, TimeSpan.FromSeconds(5));
        }
        Assert.True(await WaitUntilAsync(() => server.OutboxEntries().All(
            entry => (string?)entry["status"] is "delivered" or "failed")));
        var ben = (await client.MessagesAsync(new MessageFilter { Address = Ben }, 1)).Items[0];
        var anna = (await client.MessagesAsync(new MessageFilter { Address = Anna }, 1)).Items[0];
        await client.DeleteMessageAsync(anna.Id!.Value);
        await client.DeleteMessageAsync(ben.Id!.Value);
        // Events that name no peer always pass.
        await server.EmitAsync("conversations.changed", []);
        await server.EmitAsync("sms.deleted", new JsonObject
        {
            ["id"] = null, ["conversationId"] = 1, ["address"] = null, ["mode"] = "hidden",
        });
        var events = await ListAsync(client.EventsAsync(s_short));
        Assert.Equal(
            ["sms.queued", "sms.sent", "sms.delivered", "sms.deleted", "conversations.changed", "sms.deleted"],
            events.Select(item => item.Name));
        Assert.Equal(
            [Ben, Ben, Ben, Ben, null, null],
            events.Select(item => (string?)(item.Data["to"] ?? item.Data["address"])));
    }

    [Fact]
    public async Task EventsOfASentMessage()
    {
        var server = StartServer(new FakeOptions { DeliveryReports = true });
        server.FailNumber("+43123456789");
        var client = await ConnectAsync(server);
        await client.SubscribeAsync(["sms.queued", "sms.sent", "sms.delivered", "sms.failed"]);
        var good = (await client.SendAsync(Ben, "text")).RequestId;
        var entry = await client.WaitForResultAsync(good, TimeSpan.FromSeconds(5), delivered: true);
        var conversation = (await client.GetMessageAsync(entry.MessageId!.Value)).ConversationId;
        var queued = await client.WaitForEventAsync("sms.queued", TimeSpan.FromSeconds(1));
        AssertJson(
            $$"""{"requestId":"{{good}}","to":"{{Ben}}","parts":1,"sim":1,"source":"api"}""", queued.Data);
        var sent = await client.WaitForEventAsync("sms.sent", TimeSpan.FromSeconds(1));
        Assert.Equal(["requestId", "conversationId", "to", "message"], sent.Data.Select(pair => pair.Key));
        Assert.Equal(conversation, (long?)sent.Data["conversationId"]);
        Assert.Equal(Ben, (string?)sent.Data["to"]);
        Assert.Equal(entry.MessageId, sent.Message!.Id);

        var bad = (await client.SendAsync("+43123456789", "text")).RequestId;
        Assert.Equal("failed", (await client.WaitForResultAsync(bad, TimeSpan.FromSeconds(5))).Status);
        var failed = await client.WaitForEventAsync("sms.failed", TimeSpan.FromSeconds(1));
        // The phone has not stored the message: no id, no conversation.
        AssertJson(
            $$"""
            {"requestId":"{{bad}}","id":null,"conversationId":null,"to":"+43123456789","errorCode":1,
             "reason":"generic_failure"}
            """,
            failed.Data);
    }

    [Fact]
    public async Task WaitForResultWakesUpOnTheEvent()
    {
        var server = StartServer(new FakeOptions { SendDelay = 2.0 });
        var client = await ConnectAsync(server);
        await client.SubscribeAsync(["sms.queued", "sms.sent", "sms.failed"]);
        var requestId = (await client.SendAsync(Ben, "text")).RequestId;
        var watch = Stopwatch.StartNew();
        var entry = await client.WaitForResultAsync(
            requestId, TimeSpan.FromSeconds(10), pollInterval: TimeSpan.FromSeconds(8));
        // The client asks after 0.1, 0.3, 0.7, 1.5 and 3.1 seconds. The message is sent after
        // 2 seconds; only the event can tell that early.
        Assert.InRange(watch.Elapsed.TotalSeconds, 1.9, 2.9);
        Assert.Equal("sent", entry.Status);
        // The events are still there for the application.
        var events = await ListAsync(client.EventsAsync(TimeSpan.FromMilliseconds(50)));
        Assert.Equal(["sms.queued", "sms.sent"], events.Select(item => item.Name));
    }

    [Fact]
    public async Task FullEventQueueDropsTheOldest()
    {
        var client = await ConnectAsync(configure: options => options.EventQueueSize = 2);
        Server.BeforeNextResponse(EventFrame(1), EventFrame(2), EventFrame(3), EventFrame(4));
        await client.PingAsync();
        Assert.Equal(2, client.PendingEvents);
        Assert.Equal(2, client.DroppedEvents);
        var events = await ListAsync(client.EventsAsync(TimeSpan.FromMilliseconds(50)));
        Assert.Equal([3L, 4L], events.Select(item => item.Seq));
    }

    [Fact]
    public async Task ResponsesInAnotherOrder()
    {
        var client = await ConnectAsync();
        Server.ReverseNextResponses(3);
        var first = await client.SendRequestAsync("messages.list", new JsonObject { ["limit"] = 1 });
        var second = await client.SendRequestAsync("status");
        var third = await client.SendRequestAsync("contacts.list");
        Assert.Equal(3, new[] { first, second, third }.Distinct().Count());
        var wait = TimeSpan.FromSeconds(2);
        Assert.True((await client.WaitForResponseAsync(second, wait)).ContainsKey("deleteMode"));
        Assert.Equal(
            "Anna 24", (string?)(await client.WaitForResponseAsync(first, wait))["items"]![0]!["body"]);
        Assert.Equal(2, (await client.WaitForResponseAsync(third, wait))["items"]!.AsArray().Count);
        await Assert.ThrowsAsync<ArgumentException>(() => client.WaitForResponseAsync(first));
        await client.PingAsync();
    }

    [Fact]
    public async Task ResponseErrorOfAPipelinedRequest()
    {
        var client = await ConnectAsync();
        Server.ReverseNextResponses(2);
        var missing = await client.SendRequestAsync("messages.get", new JsonObject { ["messageId"] = 1 });
        var fine = await client.SendRequestAsync("ping");
        var error = await Assert.ThrowsAsync<ApiException>(
            () => client.WaitForResponseAsync(missing, TimeSpan.FromSeconds(2)));
        Assert.Equal("not_found", error.Code);
        Assert.True((long?)(await client.WaitForResponseAsync(fine, TimeSpan.FromSeconds(2)))["time"] > 0);
    }

    [Fact]
    public async Task WaitForResponseTimesOut()
    {
        var client = await ConnectAsync();
        Server.IgnoreNext("ping");
        var id = await client.SendRequestAsync("ping");
        await Assert.ThrowsAsync<TimeoutException>(
            () => client.WaitForResponseAsync(id, TimeSpan.FromMilliseconds(200)));
        Assert.True(client.Connected);
    }

    [Fact]
    public async Task StaleResponsesAreIgnored()
    {
        var client = await ConnectAsync();
        Server.BeforeNextResponse(
            JsonNode.Parse("""{"id":"cs-0","ok":true,"result":{"stale":true}}""")!,
            JsonNode.Parse("""{"id":77,"ok":false,"error":{"code":"internal_error","message":"stale"}}""")!);
        Assert.Equal("hidden", (await client.StatusAsync()).DeleteMode);
        Assert.True((await client.PingAsync()).Time > 0);
    }

    [Fact]
    public async Task ResponseWithoutIdAnswersTheOpenRequest()
    {
        var client = await ConnectAsync();
        Server.BeforeNextResponse(JsonNode.Parse(
            """{"id":null,"ok":false,"error":{"code":"invalid_json","message":"Not a JSON object."}}""")!);
        var error = await Assert.ThrowsAsync<ApiException>(() => client.StatusAsync());
        Assert.Equal("invalid_json", error.Code);
        // The late real response is dropped; the next request works.
        Assert.True((await client.PingAsync()).Time > 0);
    }

    [Theory]
    [InlineData("this is not JSON", "invalid JSON")]
    [InlineData("[1, 2, 3]", "not a JSON object")]
    public async Task MalformedFrames(string frame, string message)
    {
        var client = await ConnectAsync();
        Server.BeforeNextResponse(frame);
        var error = await Assert.ThrowsAsync<ProtocolException>(() => client.StatusAsync());
        Assert.Contains(message, error.Message);
    }

    [Fact]
    public async Task MalformedResponse()
    {
        var client = await ConnectAsync();
        Server.BeforeNextResponse(JsonNode.Parse("""{"id":"cs-2","ok":false}""")!);
        var error = await Assert.ThrowsAsync<ProtocolException>(() => client.StatusAsync());
        Assert.Contains("Malformed", error.Message);
    }
}
