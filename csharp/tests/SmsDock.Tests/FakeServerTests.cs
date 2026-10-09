using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace SmsDock.Tests;

/// <summary>
/// Tests of the fake server itself, with a bare WebSocket connection. They pin down the
/// behaviour of <c>API.md</c> that the client never triggers on its own: close codes, limits
/// and errors before the login.
/// </summary>
public class FakeServerTests : ServerTestBase
{
    private readonly List<Wire> _wires = [];

    private async Task<Wire> OpenAsync(FakeSmsDock server, string path = "")
    {
        var wire = await Wire.OpenAsync(server.Url + path);
        _wires.Add(wire);
        return wire;
    }

    private async Task<Wire> LoginAsync(FakeSmsDock server)
    {
        var wire = await OpenAsync(server);
        Assert.Equal("hello", (string?)(await wire.ReceiveAsync())["event"]);
        var response = await wire.CallAsync($$"""{"id":0,"action":"authenticate","password":"{{Password}}"}""");
        Assert.True((bool?)response["ok"]);
        return wire;
    }

    [Fact]
    public async Task HelloComesFirst()
    {
        var wire = await OpenAsync(Server);
        AssertJson(
            """{"event":"hello","protocolVersion":1,"authenticationRequired":true}""", await wire.ReceiveAsync());
    }

    [Fact]
    public async Task LoginResult()
    {
        var wire = await OpenAsync(Server);
        await wire.ReceiveAsync();
        await Server.ReceiveSmsAsync(Anna, "one event so far");
        AssertJson(
            $$"""
            {"id":"1","ok":true,"result":{"protocolVersion":1,
             "app":{"name":"SMS Dock","version":"1.0.0","protocolVersion":1},
             "latestSeq":1,"epoch":{{Server.Epoch}} } }
            """,
            await wire.CallAsync($$"""{"id":"1","action":"authenticate","password":"{{Password}}"}"""));
    }

    [Fact]
    public async Task ActionsNeedALogin()
    {
        var wire = await OpenAsync(Server);
        await wire.ReceiveAsync();
        var response = await wire.CallAsync("""{"id":5,"action":"messages.list"}""");
        Assert.Equal(5, (int?)response["id"]);
        Assert.False((bool?)response["ok"]);
        Assert.Equal("not_authenticated", (string?)response["error"]!["code"]);
    }

    [Fact]
    public async Task WrongPasswordClosesWith4401()
    {
        var wire = await OpenAsync(Server);
        await wire.ReceiveAsync();
        var response = await wire.CallAsync("""{"id":"1","action":"authenticate","password":"wrong"}""");
        Assert.Equal("invalid_password", (string?)response["error"]!["code"]);
        Assert.Equal(4401, await wire.CloseCodeAsync());
    }

    [Fact]
    public async Task LockAfterFiveWrongPasswords()
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var wrong = await OpenAsync(Server);
            await wrong.ReceiveAsync();
            var refused = await wrong.CallAsync("""{"id":"1","action":"authenticate","password":"wrong"}""");
            Assert.Equal("invalid_password", (string?)refused["error"]!["code"]);
        }
        var wire = await OpenAsync(Server);
        await wire.ReceiveAsync();
        var response = await wire.CallAsync($$"""{"id":"1","action":"authenticate","password":"{{Password}}"}""");
        Assert.Equal("locked_out", (string?)response["error"]!["code"]);
        Assert.InRange((int)response["error"]!["retryAfter"]!, 1, 30);
        Assert.Equal(4401, await wire.CloseCodeAsync());
    }

    [Fact]
    public async Task LicenceRequiredClosesWith4403()
    {
        Server.LicenceEnforced = true;
        var wire = await OpenAsync(Server);
        await wire.ReceiveAsync();
        var response = await wire.CallAsync($$"""{"id":"1","action":"authenticate","password":"{{Password}}"}""");
        Assert.Equal("licence_required", (string?)response["error"]!["code"]);
        Assert.Equal(4403, await wire.CloseCodeAsync());
        // The licence is looked at first: a wrong password is not counted.
        wire = await OpenAsync(Server);
        await wire.ReceiveAsync();
        response = await wire.CallAsync("""{"id":"1","action":"authenticate","password":"wrong"}""");
        Assert.Equal("licence_required", (string?)response["error"]!["code"]);
        Assert.Equal(0, Server.FailedLogins);
        Server.Licensed = true;
        await LoginAsync(Server);
    }

    [Fact]
    public async Task LoginTimeoutClosesWith4401()
    {
        var server = StartServer(new FakeOptions { AuthTimeout = 0.2 });
        var wire = await OpenAsync(server);
        Assert.Equal("hello", (string?)(await wire.ReceiveAsync())["event"]);
        Assert.Equal(4401, await wire.CloseCodeAsync());
    }

    [Fact]
    public async Task NoTimeoutAfterTheLogin()
    {
        var server = StartServer(new FakeOptions { AuthTimeout = 0.2 });
        var wire = await LoginAsync(server);
        await Assert.ThrowsAsync<TimeoutException>(() => wire.ReceiveAsync(0.5));
        Assert.True((bool?)(await wire.CallAsync("""{"id":1,"action":"ping"}"""))["ok"]);
    }

    [Fact]
    public async Task LargeFrameClosesWith1009()
    {
        var wire = await LoginAsync(Server);
        var body = new string('x', 64 * 1024);
        await wire.SendAsync($$"""{"id":1,"action":"messages.send","to":"{{Anna}}","body":"{{body}}"}""");
        Assert.Equal(1009, await wire.CloseCodeAsync());
        Assert.Empty(Server.Sent);
    }

    [Fact]
    public async Task BinaryFrameClosesWith4400()
    {
        var wire = await LoginAsync(Server);
        await wire.SendBinaryAsync([0, 1]);
        Assert.Equal(4400, await wire.CloseCodeAsync());
    }

    [Fact]
    public async Task NinthClientClosesWith4409()
    {
        for (var index = 0; index < 8; index++)
        {
            await LoginAsync(Server);
        }
        Assert.Equal(4409, await (await OpenAsync(Server)).CloseCodeAsync());
        Assert.Equal(8, Server.ClientCount);
    }

    [Fact]
    public async Task OtherPathsDoNotExist()
    {
        var error = await Assert.ThrowsAsync<WebSocketException>(() => OpenAsync(Server, "/other"));
        Assert.Contains("404", error.Message);
    }

    [Theory]
    [InlineData("not JSON")]
    [InlineData("[1]")]
    [InlineData("\"text\"")]
    [InlineData("12")]
    public async Task InvalidJson(string frame)
    {
        var wire = await LoginAsync(Server);
        var response = await wire.CallAsync(frame);
        Assert.False((bool?)response["ok"]);
        Assert.Equal("invalid_json", (string?)response["error"]!["code"]);
        Assert.True((bool?)(await wire.CallAsync("""{"id":1,"action":"ping"}"""))["ok"]);
    }

    [Theory]
    [InlineData("""{"action":"unknown"}""", "unknown_action")]
    [InlineData("""{}""", "invalid_request")]
    [InlineData("""{"action":"messages.list","limit":0}""", "invalid_request")]
    [InlineData("""{"action":"messages.list","limit":201}""", "invalid_request")]
    [InlineData("""{"action":"messages.list","limit":"10"}""", "invalid_request")]
    [InlineData("""{"action":"messages.list","direction":"up"}""", "invalid_request")]
    [InlineData("""{"action":"messages.list","cursor":"nonsense"}""", "invalid_request")]
    [InlineData("""{"action":"messages.get"}""", "invalid_request")]
    [InlineData("""{"action":"messages.get","messageId":"2813"}""", "invalid_request")]
    [InlineData("""{"action":"messages.get","messageId":1}""", "not_found")]
    [InlineData("""{"action":"messages.delete"}""", "invalid_request")]
    [InlineData("""{"action":"conversations.delete"}""", "invalid_request")]
    [InlineData("""{"action":"conversations.delete","conversationId":1}""", "not_found")]
    [InlineData("""{"action":"conversations.markRead"}""", "invalid_request")]
    [InlineData("""{"action":"conversations.markRead","conversationId":170}""", "not_default_app")]
    [InlineData("""{"action":"contacts.list","cursor":"x"}""", "invalid_request")]
    [InlineData("""{"action":"messages.send","to":"+4369912345678"}""", "invalid_request")]
    [InlineData("""{"action":"messages.send","to":"+4369912345678","body":"x","requestId":"a b"}""", "invalid_request")]
    [InlineData("""{"action":"messages.status","requestId":"none"}""", "not_found")]
    [InlineData("""{"action":"subscribe","events":"sms.received"}""", "invalid_request")]
    [InlineData("""{"action":"subscribe"}""", "invalid_request")]
    public async Task Errors(string request, string code)
    {
        var wire = await LoginAsync(Server);
        var response = await wire.CallAsync(request);
        Assert.False((bool?)response["ok"]);
        Assert.Equal(code, (string?)response["error"]!["code"]);
        Assert.NotNull((string?)response["error"]!["message"]);
    }

    [Fact]
    public async Task RequestWithoutId()
    {
        var wire = await LoginAsync(Server);
        var response = await wire.CallAsync("""{"action":"ping"}""");
        Assert.Null(response["id"]);
        Assert.True((bool?)response["ok"]);
        // The field "id" names the request only, never a message.
        response = await wire.CallAsync("""{"id":2801,"action":"messages.get"}""");
        Assert.Equal(2801, (int?)response["id"]);
        Assert.Equal("invalid_request", (string?)response["error"]!["code"]);
    }

    [Fact]
    public async Task SequenceNumbersCountAllEvents()
    {
        var wire = await LoginAsync(Server);
        await Server.ReceiveSmsAsync(Anna, "not subscribed yet");
        Assert.True((bool?)(await wire.CallAsync("""{"id":1,"action":"subscribe","events":["sms.received"]}"""))["ok"]);
        await Server.EmitAsync("conversations.changed", []);
        await Server.ReceiveSmsAsync(Anna, "third event");
        var received = await wire.ReceiveAsync();
        Assert.Equal("sms.received", (string?)received["event"]);
        Assert.Equal(3, (int?)received["seq"]);
    }

    [Fact]
    public async Task StoppingClosesWith1001()
    {
        var server = StartServer();
        var wire = await LoginAsync(server);
        var stop = server.StopAsync();
        Assert.Equal(1001, await wire.CloseCodeAsync());
        await stop;
    }

    [Fact]
    public async Task RestartChangesTheEpoch()
    {
        var wire = await LoginAsync(Server);
        await Server.ReceiveSmsAsync(Anna, "before");
        var first = Server.Epoch;
        await Server.RestartAsync();
        Assert.Null(await wire.CloseCodeAsync());
        wire = await OpenAsync(Server);
        await wire.ReceiveAsync();
        var result = (await wire.CallAsync($$"""{"id":1,"action":"authenticate","password":"{{Password}}"}"""))["result"]!;
        Assert.Equal(Server.Epoch, (long?)result["epoch"]);
        Assert.NotEqual(first, Server.Epoch);
        Assert.Equal(0, (int?)result["latestSeq"]);
        Assert.True((bool?)(await wire.CallAsync("""{"id":2,"action":"subscribe","events":["*"]}"""))["ok"]);
        await Server.ReceiveSmsAsync(Anna, "after");
        Assert.Equal(1, (int?)(await wire.ReceiveAsync())["seq"]);
    }

    [Fact]
    public async Task RateLimit()
    {
        var server = StartServer(new FakeOptions { RequestsPerSecond = 5 });
        var wire = await LoginAsync(server);
        for (var number = 0; number < 8; number++)
        {
            await wire.SendAsync($$"""{"id":{{number}},"action":"ping"}""");
        }
        var responses = new List<JsonObject>();
        for (var number = 0; number < 8; number++)
        {
            responses.Add(await wire.ReceiveAsync());
        }
        Assert.Equal(Enumerable.Range(0, 8), responses.Select(response => (int)response["id"]!));
        // The login was the first request of this second.
        Assert.Equal(
            [true, true, true, true, false, false, false, false], responses.Select(response => (bool)response["ok"]!));
        AssertJson(
            """{"code":"rate_limited","message":"Too many requests.","retryAfter":1}""", responses[4]["error"]);
        Assert.Equal(4, server.RefusedCount);
        Assert.Equal(4, Count(server, "ping"));
        // Frames that cannot be read count as well and get the id null.
        var refused = await wire.CallAsync("not JSON");
        Assert.Null(refused["id"]);
        Assert.Equal("rate_limited", (string?)refused["error"]!["code"]);
        await Task.Delay(1100);
        Assert.True((bool?)(await wire.CallAsync("""{"id":9,"action":"ping"}"""))["ok"]);
    }

    [Fact]
    public async Task FloodingClosesWith4429()
    {
        var server = StartServer(new FakeOptions { RequestsPerSecond = 3, MaxRefused = 5 });
        var wire = await LoginAsync(server);
        for (var number = 0; number < 2 + 6; number++)
        {
            await wire.SendAsync($$"""{"id":{{number}},"action":"ping"}""");
        }
        Assert.Equal(4429, await wire.CloseCodeAsync());
        // 5 refused requests in a row are tolerated, the 6th is one too many.
        Assert.Equal(6, server.RefusedCount);
    }

    [Fact]
    public async Task SubscriptionListsNamesInsteadOfTheStar()
    {
        var wire = await LoginAsync(Server);
        var names = string.Join(",", FakeSmsDock.EventNames.Select(name => $"\"{name}\""));
        var result = (await wire.CallAsync(
            """{"id":1,"action":"subscribe","events":["*"],"address":"0699 1234567"}"""))["result"];
        AssertJson($$"""{"events":[{{names}}],"address":"0699 1234567"}""", result);
        result = (await wire.CallAsync(
            """{"id":2,"action":"subscribe","events":["sms.sent"],"address":null}"""))["result"];
        AssertJson($$"""{"events":[{{names}}],"address":null}""", result);
        result = (await wire.CallAsync("""{"id":3,"action":"unsubscribe","events":["*"]}"""))["result"];
        AssertJson("""{"events":[],"address":null}""", result);
    }

    [Theory]
    [InlineData("+43 699 1234567", "0043 699 1234567", true)]
    [InlineData("+43 699 1234567", "0699 1234567", true)]
    [InlineData("0043 699 1234567", "0699 1234567", true)]
    [InlineData("0699 1234567", "+43 699 1234567", true)]
    [InlineData("0699/12 34-567", "(0699) 12.34.567", true)]
    [InlineData("0699 1234567", "699 1234567", true)]
    [InlineData("1234567", "+43 699 1234567", true)]
    [InlineData("234567", "+43 699 1234567", false)]
    [InlineData("0699 1234567", "0699 1234568", false)]
    [InlineData("+43 699 1234567", "+49 699 1234567", false)]
    [InlineData("+43 699 1234567", "+43 699 12345678", false)]
    [InlineData("8810", "8810", true)]
    [InlineData("8810", "08810", false)]
    [InlineData("8810", "+43 699 1238810", false)]
    [InlineData("MyBank", "mybank", true)]
    [InlineData(" MyBank ", "MYBANK", true)]
    [InlineData("MyBank", "MyBank2", false)]
    [InlineData("A1", "+431", false)]
    public void SamePeer(string first, string second, bool same)
    {
        Assert.Equal(same, FakeSmsDock.SamePeer(first, second));
        Assert.Equal(same, FakeSmsDock.SamePeer(second, first));
    }

    [Fact]
    public void Helpers()
    {
        Assert.Equal("+4369912345678", FakeSmsDock.NormalizeNumber("+43 (699) 123-456.78"));
        Assert.Equal("069912345678", FakeSmsDock.NormalizeNumber(" 0699/123 456 78 "));
        Assert.Equal(1, FakeSmsDock.CountParts(new string('x', 160)));
        Assert.Equal(2, FakeSmsDock.CountParts(new string('x', 161)));
        Assert.Equal(10, FakeSmsDock.CountParts(new string('x', 1530)));
        Assert.Equal(1, FakeSmsDock.CountParts(new string('ü', 70)));
        Assert.Equal(2, FakeSmsDock.CountParts(new string('ü', 71)));
        Assert.Equal(10, FakeSmsDock.CountParts(new string('ü', 670)));
        Assert.Equal(11, FakeSmsDock.CountParts(new string('ü', 671)));
    }

    [Fact]
    public async Task SeededDataIsThatOfThePythonServer()
    {
        var server = new FakeSmsDock();
        server.SeedDemoData();
        server.Start();
        await using (server)
        {
            var client = await ConnectAsync(server);
            Assert.Equal(
                ["+436641234567", "A1", "+4369087654321", "+4369912345678"],
                (await client.ConversationsAsync()).Items.Select(item => item.Address));
            Assert.Equal(7, (await client.MessagesAsync()).Items.Count);
            Assert.Equal(
                ["Anna Example", "Ben Example"], (await client.ContactsAsync()).Items.Select(item => item.Name));
            await client.CloseAsync();
        }
    }

    /// <summary>A bare connection that sends and receives JSON objects.</summary>
    private sealed class Wire
    {
        private readonly ClientWebSocket _socket;
        private readonly Channel<string> _frames = Channel.CreateUnbounded<string>();
        private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int? _closeCode;

        private Wire(ClientWebSocket socket)
        {
            _socket = socket;
            _ = Task.Run(PumpAsync);
        }

        public static async Task<Wire> OpenAsync(string url)
        {
            var socket = new ClientWebSocket();
            // The test server has a self-signed certificate; these tests are not about it.
            socket.Options.RemoteCertificateValidationCallback = (_, _, _, _) => true;
            using var limit = new CancellationTokenSource(5000);
            await socket.ConnectAsync(new Uri(url), limit.Token);
            return new Wire(socket);
        }

        public Task SendAsync(string frame) => _socket.SendAsync(
            Encoding.UTF8.GetBytes(frame), WebSocketMessageType.Text, true, CancellationToken.None);

        public Task SendBinaryAsync(byte[] frame) =>
            _socket.SendAsync(frame, WebSocketMessageType.Binary, true, CancellationToken.None);

        public async Task<JsonObject> ReceiveAsync(double timeout = 3.0)
        {
            using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(timeout));
            try
            {
                return JsonNode.Parse(await _frames.Reader.ReadAsync(limit.Token))!.AsObject();
            }
            catch (OperationCanceledException)
            {
                throw new TimeoutException();
            }
        }

        public async Task<JsonObject> CallAsync(string frame)
        {
            await SendAsync(frame);
            return await ReceiveAsync();
        }

        /// <summary>Reads until the server closes; returns the close code, or null without one.</summary>
        public async Task<int?> CloseCodeAsync()
        {
            using var limit = new CancellationTokenSource(5000);
            await _closed.Task.WaitAsync(limit.Token);
            return _closeCode;
        }

        private async Task PumpAsync()
        {
            var buffer = new byte[16 * 1024];
            using var message = new MemoryStream();
            try
            {
                while (true)
                {
                    var result = await _socket.ReceiveAsync(buffer.AsMemory(), CancellationToken.None);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        _closeCode = (int?)_socket.CloseStatus;
                        await _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
                        return;
                    }
                    message.Write(buffer, 0, result.Count);
                    if (result.EndOfMessage)
                    {
                        _frames.Writer.TryWrite(Encoding.UTF8.GetString(message.ToArray()));
                        message.SetLength(0);
                    }
                }
            }
            catch (Exception error) when (error is WebSocketException or IOException or ObjectDisposedException)
            {
                // Cut off without a close frame.
            }
            finally
            {
                _frames.Writer.TryComplete();
                _closed.TrySetResult();
                _socket.Dispose();
            }
        }
    }
}
