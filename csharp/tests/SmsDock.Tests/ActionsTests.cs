using System.Text.Json.Nodes;

namespace SmsDock.Tests;

/// <summary>The actions of the API.</summary>
public class ActionsTests : ServerTestBase
{
    [Fact]
    public async Task Status()
    {
        var client = await ConnectAsync();
        var status = await client.StatusAsync();
        AssertJson("""{"name":"SMS Dock","version":"1.0.0","protocolVersion":1}""", status.App!.Json);
        Assert.Equal("hidden", status.DeleteMode);
        Assert.Equal(1, status.Sims[0].SubscriptionId);
        Assert.Null(status.Sims[0].Number);
        Assert.True(status.Sims[0].IsDefault);
        Assert.True(status.Permissions["sendSms"]);
        Assert.Null(status.Licence!.Remaining);
        Assert.False(status.DefaultSmsApp);
        Assert.True(status.Popup);
    }

    [Fact]
    public async Task Ping()
    {
        var client = await ConnectAsync();
        var time = (await client.PingAsync()).Time;
        Assert.InRange(Math.Abs(time - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()), 0, 60_000);
    }

    [Fact]
    public async Task Conversations()
    {
        var client = await ConnectAsync();
        var page = await client.ConversationsAsync();
        Assert.Equal([Anna, Ben, "A1"], page.Items.Select(item => item.Address));
        Assert.Null(page.NextCursor);
        var anna = page.Items[0];
        Assert.Equal("Anna Example", anna.Contact);
        Assert.Equal(25, anna.MessageCount);
        Assert.Equal(3, anna.UnreadCount);
        Assert.Equal("Anna 24", anna.Snippet);
        AssertJson(
            """{"id":"cs-2","action":"conversations.list","limit":50}""", LastRequest(Server, "conversations.list"));
    }

    [Fact]
    public async Task ConversationsFilters()
    {
        var client = await ConnectAsync();
        async Task<IEnumerable<string>> Addresses(ConversationFilter filter) =>
            (await client.ConversationsAsync(filter)).Items.Select(item => item.Address);

        Assert.Equal([Ben], await Addresses(new ConversationFilter { Query = "invoice" }));
        Assert.Equal([Anna], await Addresses(new ConversationFilter { Query = "anna" }));
        Assert.Equal([Anna, "A1"], await Addresses(new ConversationFilter { Unread = true }));
        Assert.Equal([Ben], await Addresses(new ConversationFilter { Sim = 2 }));
        var request = LastRequest(Server, "conversations.list");
        Assert.Equal(2, (int?)request["sim"]);
        Assert.False(request.ContainsKey("unread"));
        Assert.False(request.ContainsKey("q"));
    }

    [Fact]
    public async Task ConversationsPaging()
    {
        var client = await ConnectAsync();
        var first = await client.ConversationsAsync(limit: 2);
        Assert.Equal(2, first.Items.Count);
        Assert.NotNull(first.NextCursor);
        var second = await client.ConversationsAsync(limit: 2, cursor: first.NextCursor);
        Assert.Equal(["A1"], second.Items.Select(item => item.Address));
        Assert.Null(second.NextCursor);
        var all = await ListAsync(client.EnumerateConversationsAsync(pageSize: 1));
        Assert.Equal([Anna, Ben, "A1"], all.Select(item => item.Address));
    }

    [Fact]
    public async Task LastTenIncomingMessages()
    {
        var client = await ConnectAsync();
        var page = await client.MessagesAsync(new MessageFilter { Direction = "in" }, limit: 10);
        Assert.Equal(
            Enumerable.Range(0, 10).Select(index => $"Anna {24 - (index * 2)}"),
            page.Items.Select(message => message.Body));
        Assert.All(page.Items, message => Assert.Equal("in", message.Direction));
        Assert.NotNull(page.NextCursor);
        AssertJson(
            """{"id":"cs-2","action":"messages.list","direction":"in","limit":10}""",
            LastRequest(Server, "messages.list"));
    }

    [Theory]
    [InlineData(Ben, 3)]
    [InlineData("+43 690 876-54321", 3)]
    [InlineData("0043 690 87654321", 3)]
    [InlineData("0690 / 876 54 321", 3)]
    [InlineData("(0690) 876.54.321", 3)]
    [InlineData("87654321", 3)]
    [InlineData("654321", 0)]
    [InlineData("+49 690 87654321", 0)]
    [InlineData("a1", 1)]
    [InlineData("A1", 1)]
    [InlineData("A1 Telekom", 0)]
    public async Task MessagesOfANumber(string address, int count)
    {
        var client = await ConnectAsync();
        Assert.Equal(count, (await client.MessagesAsync(new MessageFilter { Address = address })).Items.Count);
    }

    [Fact]
    public async Task ConversationWinsOverAddress()
    {
        var client = await ConnectAsync();
        var ben = (await client.ConversationsAsync(new ConversationFilter { Query = "invoice" })).Items[0].Id;
        var page = await client.MessagesAsync(new MessageFilter { ConversationId = ben, Address = Anna });
        Assert.Equal([Ben], page.Items.Select(message => message.Address).Distinct());
        var request = LastRequest(Server, "messages.list");
        Assert.Equal(ben, (long?)request["conversationId"]);
        Assert.Equal(Anna, (string?)request["address"]);
    }

    [Fact]
    public async Task MessagesFilters()
    {
        var client = await ConnectAsync();
        async Task<List<string>> Bodies(MessageFilter filter, int limit = 50) =>
            (await client.MessagesAsync(filter, limit)).Items.Select(message => message.Body).ToList();

        Assert.Equal(3, (await Bodies(new MessageFilter { Address = Ben })).Count);
        Assert.Equal(13, (await Bodies(new MessageFilter { Direction = "out" }, 200)).Count);
        Assert.Equal(
            ["Thanks for the invoice", "Your invoice is attached"], await Bodies(new MessageFilter { Query = "INVOICE" }));
        Assert.Equal(3, (await Bodies(new MessageFilter { Sim = 2 })).Count);
        Assert.Equal(
            ["Anna 24", "Anna 23", "Anna 22", "Your bill is ready"], await Bodies(new MessageFilter { Unread = true }));
        Assert.Equal(
            ["Anna 18", "Anna 16", "Anna 14"],
            await Bodies(new MessageFilter { Address = Anna, Direction = "in", Query = "Anna 1" }, 3));
        AssertJson(
            $$"""{"id":"cs-7","action":"messages.list","address":"{{Anna}}","direction":"in","q":"Anna 1","limit":3}""",
            LastRequest(Server, "messages.list"));
    }

    [Fact]
    public async Task MessagesTimeRange()
    {
        var client = await ConnectAsync();
        var page = await client.MessagesAsync(new MessageFilter { Since = BaseTime + 3000, Until = BaseTime + 5000 });
        Assert.Equal(["Anna 5", "Anna 4", "Anna 3"], page.Items.Select(message => message.Body));
        var request = LastRequest(Server, "messages.list");
        Assert.Equal(BaseTime + 3000, (long?)request["from"]);
        Assert.Equal(BaseTime + 5000, (long?)request["to"]);

        var moment = DateTimeOffset.FromUnixTimeMilliseconds(BaseTime + 23_000);
        page = await client.MessagesAsync(new MessageFilter { SinceTime = moment });
        Assert.Equal(["Anna 24", "Anna 23"], page.Items.Select(message => message.Body));
        Assert.Equal(BaseTime + 23_000, (long?)LastRequest(Server, "messages.list")["from"]);
        Assert.Equal(moment, page.Items[1].Time);
    }

    [Fact]
    public async Task MessagesPaging()
    {
        var client = await ConnectAsync();
        var filter = new MessageFilter { Address = Anna };
        var first = await client.MessagesAsync(filter, 10);
        var second = await client.MessagesAsync(filter, 10, first.NextCursor);
        var third = await client.MessagesAsync(filter, 10, second.NextCursor);
        Assert.Equal(
            Enumerable.Range(0, 25).Select(index => $"Anna {24 - index}"),
            new[] { first, second, third }.SelectMany(page => page.Items).Select(message => message.Body));
        Assert.Null(third.NextCursor);
    }

    [Fact]
    public async Task EnumerateMessagesFollowsTheCursor()
    {
        var client = await ConnectAsync();
        var messages = await ListAsync(
            client.EnumerateMessagesAsync(new MessageFilter { Address = Anna }, pageSize: 10));
        Assert.Equal(
            Enumerable.Range(0, 25).Select(index => $"Anna {24 - index}"), messages.Select(message => message.Body));
        var requests = Server.Requests.Where(request => (string?)request["action"] == "messages.list").ToList();
        Assert.Equal(
            [null, $"{BaseTime + 15000}:{messages[9].Id}", $"{BaseTime + 5000}:{messages[19].Id}"],
            requests.Select(request => (string?)request["cursor"]));
        Assert.All(requests, request => Assert.Equal(10, (int?)request["limit"]));
    }

    [Fact]
    public async Task EnumerateMessagesIsLazyAndBounded()
    {
        var client = await ConnectAsync();
        var enumerable = client.EnumerateMessagesAsync(pageSize: 10, maxItems: 12);
        Assert.Equal(0, Count(Server, "messages.list"));
        Assert.Equal(12, (await ListAsync(enumerable)).Count);
        var requests = Server.Requests.Where(request => (string?)request["action"] == "messages.list");
        Assert.Equal([10, 2], requests.Select(request => (int?)request["limit"]));
        Assert.Empty(await ListAsync(client.EnumerateMessagesAsync(maxItems: 0)));
        Assert.Equal(29, (await ListAsync(client.EnumerateMessagesAsync(pageSize: 200))).Count);
    }

    [Fact]
    public async Task EnumerateMessagesRefusesACursorThatDoesNotMove()
    {
        var client = await ConnectAsync();
        var page = JsonNode.Parse("""{"items":[{"id":1}],"nextCursor":"again"}""")!.AsObject();
        Server.AnswerNext("messages.list", page);
        Server.AnswerNext("messages.list", page);
        var error = await Assert.ThrowsAsync<ProtocolException>(
            () => ListAsync(client.EnumerateMessagesAsync()));
        Assert.Contains("cursor", error.Message);
        Assert.Equal(2, Count(Server, "messages.list"));
    }

    [Fact]
    public async Task EnumerateMessagesRefusesAPageWithoutItems()
    {
        var client = await ConnectAsync();
        Server.AnswerNext("messages.list", new JsonObject { ["nextCursor"] = null });
        var error = await Assert.ThrowsAsync<ProtocolException>(
            () => ListAsync(client.EnumerateMessagesAsync()));
        Assert.Contains("items", error.Message);
    }

    [Fact]
    public async Task PagesMayBeShorterThanTheLimit()
    {
        var client = await ConnectAsync();
        Server.AnswerNext("messages.list", JsonNode.Parse("""{"items":[{"id":1}],"nextCursor":"1:1"}""")!.AsObject());
        Server.AnswerNext("messages.list", JsonNode.Parse("""{"items":[],"nextCursor":"1:0"}""")!.AsObject());
        Server.AnswerNext("messages.list", JsonNode.Parse("""{"items":[{"id":2}],"nextCursor":null}""")!.AsObject());
        var items = await ListAsync(client.EnumerateMessagesAsync(pageSize: 50));
        Assert.Equal([1L, 2L], items.Select(item => item.Id));
    }

    [Fact]
    public async Task RefusesWrongArgumentsBeforeAnythingIsSent()
    {
        var client = await ConnectAsync();
        await Assert.ThrowsAsync<ArgumentException>(
            () => client.MessagesAsync(new MessageFilter { Direction = "sideways" }));
        await Assert.ThrowsAsync<ArgumentException>(() => client.MessagesAsync(limit: 0));
        await Assert.ThrowsAsync<ArgumentException>(() => client.MessagesAsync(limit: 201));
        // The iterators check their arguments at once.
        Assert.Throws<ArgumentException>(() => client.EnumerateMessagesAsync(pageSize: 500));
        Assert.Throws<ArgumentException>(() => client.EnumerateMessagesAsync(maxItems: -1));
        Assert.Throws<ArgumentException>(
            () => client.EnumerateMessagesAsync(new MessageFilter { Direction = "sideways" }));
        Assert.Equal(["authenticate"], Server.Actions);
    }

    [Fact]
    public async Task GetMessage()
    {
        var client = await ConnectAsync();
        var newest = (await client.MessagesAsync(limit: 1)).Items[0];
        var same = await client.GetMessageAsync(newest.Id!.Value);
        Assert.True(JsonNode.DeepEquals(newest.Json, same.Json));
        Assert.Equal(
            [
                "id", "conversationId", "address", "contact", "direction", "body", "timestamp", "sentTimestamp",
                "read", "status", "sim", "requestId",
            ],
            newest.Json.Select(pair => pair.Key));
        AssertJson(
            $$"""{"id":"cs-3","action":"messages.get","messageId":{{newest.Id}}}""",
            LastRequest(Server, "messages.get"));
    }

    [Fact]
    public async Task GetMessageNotFound()
    {
        var client = await ConnectAsync();
        var error = await Assert.ThrowsAsync<ApiException>(() => client.GetMessageAsync(1));
        Assert.Equal("not_found", error.Code);
        Assert.Contains("not_found", error.Message);
        Assert.IsNotType<AuthenticationException>(error);
        await client.PingAsync();
    }

    [Fact]
    public async Task Send()
    {
        var client = await ConnectAsync();
        var result = await client.SendAsync("+43 690 876-54321", "All systems running.");
        Assert.Equal(Ben, result.To);
        Assert.Equal(1, result.Parts);
        Assert.Equal("queued", result.Status);
        Assert.Matches("^[0-9a-f]{32}$", result.RequestId);
        AssertJson(
            $$"""
            {"id":"cs-2","action":"messages.send","to":"+43 690 876-54321","body":"All systems running.",
             "requestId":"{{result.RequestId}}"}
            """,
            LastRequest(Server, "messages.send"));
        AssertJson(
            $$"""[{"requestId":"{{result.RequestId}}","to":"{{Ben}}","body":"All systems running.","sim":1}]""",
            new JsonArray([.. Server.Sent]));
    }

    [Fact]
    public async Task SendCreatesANewRequestIdEachTime()
    {
        var client = await ConnectAsync();
        var first = await client.SendAsync(Ben, "one");
        var second = await client.SendAsync(Ben, "one");
        Assert.NotEqual(first.RequestId, second.RequestId);
        Assert.Equal(2, Server.Sent.Count);
    }

    [Fact]
    public async Task SendWithOwnRequestIdAndSim()
    {
        var client = await ConnectAsync();
        var result = await client.SendAsync(Ben, "Grüße", sim: 2, requestId: "order-42.confirm_1");
        Assert.Equal("order-42.confirm_1", result.RequestId);
        Assert.Equal(2, (long?)Server.Sent[0]["sim"]);
        Assert.Equal("Grüße", (string?)Server.Sent[0]["body"]);
        Assert.Equal("queued", result.Status);
        Assert.Equal(
            "sent", (await client.WaitForResultAsync("order-42.confirm_1", TimeSpan.FromSeconds(5))).Status);
        // A repeat is not sent again; it tells the status of today.
        var again = await client.SendAsync(Ben, "Grüße", sim: 2, requestId: "order-42.confirm_1");
        Assert.Equal("sent", again.Status);
        Assert.Equal((result.RequestId, result.To, result.Parts), (again.RequestId, again.To, again.Parts));
        Assert.Single(Server.Sent);
    }

    [Fact]
    public async Task RepeatWithTheNumberWrittenDifferently()
    {
        var client = await ConnectAsync();
        var first = await client.SendAsync("+43 690 876 54 321", "Hello", requestId: "r-1");
        Assert.Equal(Ben, first.To);
        foreach (var number in new[] { "0043 (690) 876-54321", "0690/876.54.321", Ben })
        {
            Assert.Equal(Ben, (await client.SendAsync(number, "Hello", requestId: "r-1")).To);
        }
        Assert.Single(Server.Sent);
    }

    [Theory]
    [InlineData(Ben, "Another text")]
    [InlineData(Anna, "Hello")]
    public async Task RequestIdOfAnotherMessage(string to, string body)
    {
        var client = await ConnectAsync();
        await client.SendAsync(Ben, "Hello", requestId: "r-1");
        var error = await Assert.ThrowsAsync<ApiException>(() => client.SendAsync(to, body, requestId: "r-1"));
        Assert.Equal("invalid_request", error.Code);
        Assert.Single(Server.Sent);
    }

    [Fact]
    public async Task RepeatAfterAFailureDoesNotSendAgain()
    {
        var client = await ConnectAsync();
        Server.FailNumber("+43123456789");
        var first = await client.SendAsync("+43123456789", "Hello", requestId: "r-1");
        Assert.Equal("queued", first.Status);
        Assert.Equal("failed", (await client.WaitForResultAsync("r-1", TimeSpan.FromSeconds(5))).Status);
        Server.ClearFailNumbers();
        var again = await client.SendAsync("+43123456789", "Hello", requestId: "r-1");
        Assert.Equal("failed", again.Status);
        Assert.Single(Server.Sent);
        // A new attempt needs a new request id.
        var retry = await client.SendAsync("+43123456789", "Hello", requestId: "r-2");
        Assert.Equal("sent", (await client.WaitForResultAsync(retry.RequestId, TimeSpan.FromSeconds(5))).Status);
        Assert.Equal(2, Server.Sent.Count);
    }

    [Fact]
    public async Task RequestIdsOutliveARestartOfTheApp()
    {
        var client = await ConnectAsync();
        await client.SendAsync(Ben, "Hello", requestId: "r-1");
        Assert.Equal("sent", (await client.WaitForResultAsync("r-1", TimeSpan.FromSeconds(5))).Status);
        await Server.RestartAsync();
        Assert.Equal("sent", (await client.SendAsync(Ben, "Hello", requestId: "r-1")).Status);
        Assert.Single(Server.Sent);
    }

    [Fact]
    public async Task StatusOfALongMessage()
    {
        var server = StartServer(new FakeOptions { SendDelay = 1.0 });
        var client = await ConnectAsync(server);
        var result = await client.SendAsync(Ben, new string('x', 400));
        Assert.Equal((3, "queued"), (result.Parts, result.Status));
        Assert.True(await WaitUntilAsync(
            () => (string?)server.Outbox(result.RequestId)!["status"] == "sending"));
        var entry = await client.MessageStatusAsync(result.RequestId);
        Assert.Equal(("sending", 1), (entry.Status, entry.PartsSent));
        // Some parts are left: this is not the result yet.
        entry = await client.WaitForResultAsync(result.RequestId, TimeSpan.FromSeconds(5));
        Assert.Equal(("sent", 3), (entry.Status, entry.PartsSent));
    }

    [Theory]
    [InlineData("", "text")]
    [InlineData("   ", "text")]
    [InlineData(Ben, "")]
    [InlineData(null, "text")]
    [InlineData(Ben, null)]
    public async Task SendRefusesEmptyValues(string? to, string? body)
    {
        var client = await ConnectAsync();
        await Assert.ThrowsAsync<ArgumentException>(() => client.SendAsync(to!, body!));
        Assert.Empty(Server.Sent);
    }

    [Theory]
    [InlineData("")]
    [InlineData("a b")]
    [InlineData("ä")]
    [InlineData("xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx")]
    [InlineData("a/b")]
    [InlineData("ab\n")]
    public async Task SendRefusesMalformedRequestIds(string requestId)
    {
        var client = await ConnectAsync();
        var error = await Assert.ThrowsAsync<ArgumentException>(
            () => client.SendAsync(Ben, "text", requestId: requestId));
        Assert.Contains("request_id", error.Message);
        Assert.Empty(Server.Sent);
    }

    [Fact]
    public async Task SendErrors()
    {
        var client = await ConnectAsync();
        (string To, string Body, long? Sim, string Code)[] cases =
        [
            ("not a number", "text", null, "invalid_number"),
            (Ben, new string('x', 1531), null, "message_too_long"),
            (Ben, new string('ü', 671), null, "message_too_long"),
            (Ben, "text", 7, "sim_unavailable"),
        ];
        foreach (var (to, body, sim, code) in cases)
        {
            var error = await Assert.ThrowsAsync<ApiException>(() => client.SendAsync(to, body, sim));
            Assert.Equal(code, error.Code);
        }
        Assert.Empty(Server.Sent);
    }

    [Fact]
    public async Task LongestMessage()
    {
        var client = await ConnectAsync();
        Assert.Equal(10, (await client.SendAsync(Ben, new string('x', 1530))).Parts);
        Assert.Equal(10, (await client.SendAsync(Ben, new string('ü', 670))).Parts);
    }

    [Fact]
    public async Task ErrorDetails()
    {
        var client = await ConnectAsync();
        Server.FailNext(
            "messages.send", "permission_missing", "The permission to send SMS is missing.",
            extra: new JsonObject { ["permission"] = "sendSms" });
        var error = await Assert.ThrowsAsync<ApiException>(() => client.SendAsync(Ben, "text"));
        Assert.Equal("permission_missing", error.Code);
        Assert.Equal("The permission to send SMS is missing.", error.ServerMessage);
        Assert.Equal("permission_missing: The permission to send SMS is missing.", error.Message);
        Assert.Equal("sendSms", error.Permission);
        Assert.Null(error.RetryAfter);
        AssertJson("""{"permission":"sendSms"}""", error.Details);

        Server.FailNext("messages.send", "quota_exceeded");
        error = await Assert.ThrowsAsync<ApiException>(() => client.SendAsync(Ben, "text"));
        Assert.Equal("quota_exceeded", error.Code);
        await client.StatusAsync();
    }

    [Fact]
    public async Task MessageStatus()
    {
        var client = await ConnectAsync();
        var requestId = (await client.SendAsync(Ben, new string('x', 200))).RequestId;
        Assert.True(await WaitUntilAsync(() => (string?)Server.Outbox(requestId)!["status"] == "sent"));
        var entry = await client.MessageStatusAsync(requestId);
        Assert.Equal("sent", entry.Status);
        Assert.Equal((2, 2), (entry.Parts, entry.PartsSent));
        Assert.Equal("api", entry.Source);
        Assert.Null(entry.ErrorCode);
        Assert.Equal(requestId, (await client.GetMessageAsync(entry.MessageId!.Value)).RequestId);
        var error = await Assert.ThrowsAsync<ApiException>(() => client.MessageStatusAsync("unknown"));
        Assert.Equal("not_found", error.Code);
    }

    [Fact]
    public async Task WaitForResultByPolling()
    {
        var server = StartServer(new FakeOptions { SendDelay = 0.2 });
        var client = await ConnectAsync(server);
        var requestId = (await client.SendAsync(Ben, "text")).RequestId;
        var entry = await client.WaitForResultAsync(
            requestId, TimeSpan.FromSeconds(5), pollInterval: TimeSpan.FromMilliseconds(50));
        Assert.Equal("sent", entry.Status);
        Assert.Equal(requestId, entry.RequestId);
        Assert.Null(client.Subscription);
        Assert.True(Count(server, "messages.status") >= 2);
    }

    [Fact]
    public async Task WaitForResultFailed()
    {
        var client = await ConnectAsync();
        Server.FailNumber("+43123456");
        await client.SubscribeAsync("sms.failed");
        var requestId = (await client.SendAsync("+43123456", "text")).RequestId;
        var entry = await client.WaitForResultAsync(requestId, TimeSpan.FromSeconds(5));
        Assert.Equal("failed", entry.Status);
        Assert.Equal(1, entry.ErrorCode);
        var failed = await client.WaitForEventAsync("sms.failed", TimeSpan.FromSeconds(1));
        Assert.Equal(requestId, (string?)failed.Data["requestId"]);
        Assert.Equal("generic_failure", (string?)failed.Data["reason"]);
    }

    [Fact]
    public async Task WaitForResultDelivered()
    {
        var server = StartServer(new FakeOptions { SendDelay = 0.1, DeliveryReports = true });
        var client = await ConnectAsync(server);
        await client.SubscribeAsync("*");
        var requestId = (await client.SendAsync(Ben, "text")).RequestId;
        var entry = await client.WaitForResultAsync(
            requestId, TimeSpan.FromSeconds(5), delivered: true, pollInterval: TimeSpan.FromMilliseconds(50));
        Assert.Equal("delivered", entry.Status);
        var delivered = await client.WaitForEventAsync("sms.delivered", TimeSpan.FromSeconds(1));
        var message = await client.GetMessageAsync(entry.MessageId!.Value);
        AssertJson(
            $$"""
            {"requestId":"{{requestId}}","id":{{entry.MessageId}},"conversationId":{{message.ConversationId}},
             "to":"{{Ben}}"}
            """,
            delivered.Data);
        Assert.Equal("delivered", message.Status);
    }

    [Fact]
    public async Task WaitForResultTimesOut()
    {
        var server = StartServer(new FakeOptions { SendDelay = 30 });
        var client = await ConnectAsync(server);
        var requestId = (await client.SendAsync(Ben, "text")).RequestId;
        var error = await Assert.ThrowsAsync<TimeoutException>(() => client.WaitForResultAsync(
            requestId, TimeSpan.FromMilliseconds(300), pollInterval: TimeSpan.FromMilliseconds(100)));
        Assert.Contains("'queued'", error.Message);
        Assert.True(Count(server, "messages.status") >= 2);
        await Assert.ThrowsAsync<ArgumentException>(() => client.WaitForResultAsync(
            requestId, TimeSpan.FromSeconds(1), pollInterval: TimeSpan.Zero));
        await client.PingAsync();
    }

    [Fact]
    public async Task DeleteMessage()
    {
        var client = await ConnectAsync();
        var newest = (await client.MessagesAsync(limit: 1)).Items[0];
        var result = await client.DeleteMessageAsync(newest.Id!.Value);
        AssertJson("""{"deleted":1,"mode":"hidden"}""", result.Json);
        Assert.Equal((1, "hidden"), (result.Deleted, result.Mode));
        AssertJson(
            $$"""{"id":"cs-3","action":"messages.delete","messageId":{{newest.Id}}}""",
            LastRequest(Server, "messages.delete"));
        Assert.Equal(28, Server.Messages.Count);
        var error = await Assert.ThrowsAsync<ApiException>(() => client.DeleteMessageAsync(newest.Id.Value));
        Assert.Equal("not_found", error.Code);
    }

    [Fact]
    public async Task DeleteConversation()
    {
        var client = await ConnectAsync();
        Server.DeleteMode = "provider";
        var anna = (await client.ConversationsAsync(new ConversationFilter { Query = "anna" })).Items[0];
        AssertJson("""{"deleted":25,"mode":"provider"}""", (await client.DeleteConversationAsync(anna.Id)).Json);
        AssertJson(
            $$"""{"id":"cs-3","action":"conversations.delete","conversationId":{{anna.Id}}}""",
            LastRequest(Server, "conversations.delete"));
        Assert.Equal([Ben, "A1"], (await client.ConversationsAsync()).Items.Select(item => item.Address));
        var error = await Assert.ThrowsAsync<ApiException>(() => client.DeleteConversationAsync(anna.Id));
        Assert.Equal("not_found", error.Code);
    }

    [Fact]
    public async Task DeleteSendsAnEvent()
    {
        var client = await ConnectAsync();
        await client.SubscribeAsync("sms.deleted");
        var newest = (await client.MessagesAsync(limit: 1)).Items[0];
        await client.DeleteMessageAsync(newest.Id!.Value);
        var deleted = await client.WaitForEventAsync("sms.deleted", TimeSpan.FromSeconds(2));
        AssertJson(
            $$"""
            {"id":{{newest.Id}},"conversationId":{{newest.ConversationId}},"address":"{{newest.Address}}",
             "mode":"hidden"}
            """,
            deleted.Data);
        await client.DeleteConversationAsync(newest.ConversationId!.Value);
        deleted = await client.WaitForEventAsync("sms.deleted", TimeSpan.FromSeconds(2));
        AssertJson(
            $$"""{"id":null,"conversationId":{{newest.ConversationId}},"address":"{{Anna}}","mode":"hidden"}""",
            deleted.Data);
    }

    [Fact]
    public async Task MarkRead()
    {
        var client = await ConnectAsync();
        Server.DeleteMode = "provider";
        var filter = new ConversationFilter { Query = "anna" };
        var anna = (await client.ConversationsAsync(filter)).Items[0];
        Assert.Equal(3, anna.UnreadCount);
        var result = await client.MarkReadAsync(anna.Id);
        AssertJson("""{"ok":true}""", result.Json);
        Assert.True(result.Ok);
        AssertJson(
            $$"""{"id":"cs-3","action":"conversations.markRead","conversationId":{{anna.Id}}}""",
            LastRequest(Server, "conversations.markRead"));
        Assert.Equal(0, (await client.ConversationsAsync(filter)).Items[0].UnreadCount);
        Assert.Empty((await client.MessagesAsync(new MessageFilter { Address = Anna, Unread = true })).Items);
        var error = await Assert.ThrowsAsync<ApiException>(() => client.MarkReadAsync(1));
        Assert.Equal("not_found", error.Code);
    }

    [Fact]
    public async Task MarkReadNeedsTheDefaultSmsApp()
    {
        var client = await ConnectAsync();
        Assert.False((await client.StatusAsync()).DefaultSmsApp);
        var filter = new ConversationFilter { Query = "anna" };
        var anna = (await client.ConversationsAsync(filter)).Items[0];
        var error = await Assert.ThrowsAsync<ApiException>(() => client.MarkReadAsync(anna.Id));
        Assert.Equal("not_default_app", error.Code);
        Assert.Equal(3, (await client.ConversationsAsync(filter)).Items[0].UnreadCount);
    }

    [Fact]
    public async Task Contacts()
    {
        var client = await ConnectAsync();
        Server.AddContact("Zoe Example", "+43660111222");
        var page = await client.ContactsAsync();
        Assert.Equal(["Anna Example", "Ben Example", "Zoe Example"], page.Items.Select(contact => contact.Name));
        AssertJson($$"""[{"number":"{{Ben}}","label":"Work"}]""", page.Items[1].Json["numbers"]);
        Assert.Equal("Work", page.Items[1].Numbers[0].Label);
        Assert.Equal(["Zoe Example"], (await client.ContactsAsync("zoe")).Items.Select(contact => contact.Name));
        Assert.Equal(["Anna Example"], (await client.ContactsAsync("5678")).Items.Select(contact => contact.Name));
        var first = await client.ContactsAsync(limit: 2);
        Assert.NotNull(first.NextCursor);
        var second = await client.ContactsAsync(limit: 2, cursor: first.NextCursor);
        Assert.Equal(["Zoe Example"], second.Items.Select(contact => contact.Name));
        var two = await ListAsync(client.EnumerateContactsAsync(pageSize: 1, maxItems: 2));
        Assert.Equal(["Anna Example", "Ben Example"], two.Select(contact => contact.Name));
    }

    [Fact]
    public async Task GenericRequest()
    {
        var client = await ConnectAsync();
        var page = await client.RequestAsync("messages.list", new JsonObject { ["limit"] = 1, ["direction"] = "out" });
        Assert.Single(page["items"]!.AsArray());
        var error = await Assert.ThrowsAsync<ApiException>(() => client.RequestAsync("messages.archive"));
        Assert.Equal("unknown_action", error.Code);
    }

    [Fact]
    public async Task TheRequestIdBelongsToTheClient()
    {
        var client = await ConnectAsync();
        var error = await Assert.ThrowsAsync<ArgumentException>(
            () => client.RequestAsync("messages.get", new JsonObject { ["id"] = 1 }));
        Assert.Contains("id", error.Message);
        await Assert.ThrowsAsync<ArgumentException>(
            () => client.SendRequestAsync("ping", new JsonObject { ["id"] = "mine" }));
        Assert.Equal(["authenticate"], Server.Actions);
    }

    [Fact]
    public async Task AnswersMayBeLargerThan64KiB()
    {
        var client = await ConnectAsync();
        for (var index = 0; index < 200; index++)
        {
            Server.AddMessage("+436601234567", $"{index} " + new string('x', 1500));
        }
        var page = await client.MessagesAsync(new MessageFilter { Address = "+436601234567" }, limit: 200);
        Assert.Equal(200, page.Items.Count);
        Assert.True(page.ToString().Length > 300_000);
    }

    [Fact]
    public async Task RefusesRequestsOver64KiB()
    {
        var client = await ConnectAsync();
        var error = await Assert.ThrowsAsync<ArgumentException>(
            () => client.SendAsync(Ben, new string('x', 70_000)));
        Assert.Contains("64 KiB", error.Message);
        Assert.Equal(["authenticate"], Server.Actions);
        await client.PingAsync();
    }

    [Fact]
    public async Task CloseCode1009()
    {
        var client = await ConnectAsync();
        client.MaxFrameBytes = 1024 * 1024;
        var error = await Assert.ThrowsAsync<ProtocolException>(
            () => client.SendAsync(Ben, new string('x', 70_000)));
        Assert.Contains("64 KiB", error.Message);
        // The same frame would fail again: no second connection.
        Assert.Equal(1, client.ConnectionCount);
        Assert.False(client.Connected);
    }

    [Fact]
    public async Task FramesAreCompactJson()
    {
        var client = await ConnectAsync();
        await client.SendAsync(Ben, "Grüße \"x\"", requestId: "r1");
        var request = LastRequest(Server, "messages.send");
        Assert.Equal(
            ["id", "action", "to", "body", "requestId"], request.Select(pair => pair.Key));
        Assert.Equal("Grüße \"x\"", (string?)request["body"]);
        Assert.Equal(
            """{"id":"cs-2","action":"messages.send","to":"+4369087654321","body":"Grüße \"x\"","requestId":"r1"}""",
            PyText.Compact(request));
    }
}
