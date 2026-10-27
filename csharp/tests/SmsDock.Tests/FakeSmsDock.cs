using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace SmsDock.Tests;

/// <summary>A throwaway self-signed EC certificate like the one of the phone, kept in memory only.</summary>
internal sealed class TestCertificate
{
    private TestCertificate(X509Certificate2 certificate)
    {
        Certificate = certificate;
        // Computed here and not with the library, so that the tests compare two implementations.
        Fingerprint = string.Join(
            ":", SHA256.HashData(certificate.RawData).Select(value => value.ToString("X2")));
    }

    /// <summary>The certificate of the fake server.</summary>
    public static TestCertificate Server { get; } = Create();

    /// <summary>A second certificate that the client does not pin.</summary>
    public static TestCertificate Other { get; } = Create();

    public X509Certificate2 Certificate { get; }

    /// <summary>SHA-256 of the DER form as <c>AB:CD:…</c>.</summary>
    public string Fingerprint { get; }

    private static TestCertificate Create()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=SMS Dock", key, HashAlgorithmName.SHA256);
        using var ephemeral = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(2));
        // A TLS server on Windows cannot use a key that lives only in the CertificateRequest,
        // and older .NET versions on macOS could not either. Export and import as PKCS#12,
        // all in memory. (On macOS with .NET 10 the tests also pass without this step.)
        var certificate = X509CertificateLoader.LoadPkcs12(ephemeral.Export(X509ContentType.Pfx), null);
        return new TestCertificate(certificate);
    }
}

/// <summary>Options of <see cref="FakeSmsDock"/>.</summary>
internal sealed class FakeOptions
{
    public string Password { get; init; } = "secret123";

    public TestCertificate Tls { get; init; } = TestCertificate.Server;

    public int Port { get; init; }

    public double AuthTimeout { get; init; } = 10.0;

    public int MaxClients { get; init; } = 8;

    public double SendDelay { get; init; } = 0.02;

    public bool DeliveryReports { get; init; }

    public int RequestsPerSecond { get; init; } = 20;

    public int MaxRefused { get; init; } = 20;
}

/// <summary>
/// A fake SMS Dock server that speaks protocol version 1 of <c>API.md</c> on
/// <c>wss://127.0.0.1:PORT/ws</c>. It keeps all data in memory and sends nothing. A port of
/// <c>python/tests/fake_server.py</c>.
/// </summary>
/// <remarks>
/// All work of the server runs on pool threads (<c>Task.Run</c>), never in the synchronization
/// context of the test that calls it: the tests also read its state with blocking calls.
/// </remarks>
internal sealed partial class FakeSmsDock : IAsyncDisposable
{
    public const int MaxFrameBytes = 64 * 1024;

    public static readonly string[] EventNames =
    [
        "sms.received", "sms.queued", "sms.sent", "sms.delivered", "sms.failed", "sms.deleted",
        "conversations.changed",
    ];

    private static readonly JsonSerializerOptions s_json = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly FakeOptions _options;
    private readonly TcpListener _listener;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private readonly List<Client> _clients = [];
    private readonly List<Task> _handlers = [];
    private readonly List<JsonObject> _sims;
    private readonly List<JsonObject> _contacts = [];
    private readonly List<JsonObject> _messages = [];
    private readonly Dictionary<string, JsonObject> _outbox = [];
    private readonly Dictionary<string, string> _bodies = [];
    private readonly List<JsonObject> _sent = [];
    private readonly List<JsonObject> _requests = [];
    private readonly List<(string Address, long Id)> _conversationIds = [];
    private readonly Dictionary<string, List<string>> _drops = [];
    private readonly Dictionary<string, List<Failure>> _failures = [];
    private readonly Dictionary<string, List<JsonObject>> _results = [];
    private readonly Dictionary<string, int> _ignored = [];
    private readonly List<string> _framesBeforeResponse = [];
    private readonly List<string> _held = [];
    private readonly HashSet<string> _failNumbers = [];
    private readonly HashSet<string> _refuseNumbers = [];
    private Task? _acceptLoop;
    private long _nextMessageId = 2800;
    private long _sequence;
    private int _hold;
    private double _lockedUntil;
    private int _lockCount;
    private bool _stopped;

    public FakeSmsDock(FakeOptions? options = null)
    {
        _options = options ?? new FakeOptions();
        Epoch = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        _sims =
        [
            new JsonObject
            {
                ["subscriptionId"] = 1, ["slot"] = 0, ["name"] = "Play", ["number"] = null, ["isDefault"] = true,
            },
            new JsonObject
            {
                ["subscriptionId"] = 2, ["slot"] = 1, ["name"] = "Work", ["number"] = "+4369087654321",
                ["isDefault"] = false,
            },
        ];
        _listener = new TcpListener(IPAddress.Loopback, _options.Port);
    }

    /// <summary>Identifies one run of the app; <see cref="RestartAsync"/> changes it.</summary>
    public long Epoch { get; private set; }

    public int ProtocolVersion { get; set; } = 1;

    public bool LicenceEnforced { get; set; }

    public bool Licensed { get; set; }

    public string DeleteMode { get; set; } = "hidden";

    public int FailedLogins { get; private set; }

    /// <summary>Number of frames answered with <c>rate_limited</c> by the limiter.</summary>
    public int RefusedCount { get; private set; }

    public int ConnectionCount { get; private set; }

    public int LoginCount { get; private set; }

    public int Port { get; private set; }

    public TestCertificate Tls => _options.Tls;

    public string Password => _options.Password;

    public string Url => $"wss://127.0.0.1:{Port}/ws";

    /// <summary>Number of open connections.</summary>
    public int ClientCount => Locked(() => _clients.Count);

    /// <summary>The stored messages, as copies.</summary>
    public List<JsonObject> Messages => Locked(() => _messages.Select(Copy).ToList());

    /// <summary>Messages handed to the (imaginary) radio, one entry per SMS.</summary>
    public List<JsonObject> Sent => Locked(() => _sent.Select(Copy).ToList());

    /// <summary>Every request object received and not refused, in order.</summary>
    public List<JsonObject> Requests => Locked(() => _requests.Select(Copy).ToList());

    /// <summary>The actions the server received, in order.</summary>
    public List<string?> Actions => Requests.Select(request => PyText.AsString(request["action"])).ToList();

    // ------------------------------------------------------------------
    // Life cycle
    // ------------------------------------------------------------------

    public FakeSmsDock Start()
    {
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _acceptLoop = Task.Run(AcceptLoopAsync);
        return this;
    }

    /// <summary>Stops the server; clients see the close code 1001.</summary>
    public Task StopAsync() => Task.Run(StopCoreAsync);

    private async Task StopCoreAsync()
    {
        if (_stopped)
        {
            return;
        }
        _stopped = true;
        _listener.Stop();
        List<Client> clients;
        List<Task> handlers;
        await _gate.WaitAsync();
        try
        {
            clients = [.. _clients];
            handlers = [.. _handlers];
        }
        finally
        {
            _gate.Release();
        }
        foreach (var client in clients)
        {
            await CloseClientAsync(client, 1001, "server is stopping");
        }
        await Task.WhenAny(Task.WhenAll(handlers), Task.Delay(2000));
        _stop.Cancel();
        foreach (var client in clients)
        {
            client.Abort();
        }
        if (_acceptLoop is not null)
        {
            await _acceptLoop;
        }
    }

    public async ValueTask DisposeAsync() => await StopAsync();

    // ------------------------------------------------------------------
    // Controls for tests
    // ------------------------------------------------------------------

    public void AddContact(string name, string number, string label = "Mobile") => Locked(() =>
        _contacts.Add(new JsonObject
        {
            ["name"] = name,
            ["numbers"] = new JsonArray(new JsonObject { ["number"] = number, ["label"] = label }),
        }));

    /// <summary>Stores a message without sending an event.</summary>
    public JsonObject AddMessage(
        string address,
        string body,
        string direction = "in",
        long? timestamp = null,
        bool read = true,
        long? sim = 1,
        string? requestId = null) =>
        Locked(() => Copy(StoreMessage(address, body, direction, timestamp, read, sim, requestId)));

    /// <summary>Lets an SMS arrive: stores it and sends <c>sms.received</c>.</summary>
    public Task<JsonObject> ReceiveSmsAsync(string address, string body, long sim = 1) =>
        Task.Run(() => ReceiveSmsCoreAsync(address, body, sim));

    private async Task<JsonObject> ReceiveSmsCoreAsync(string address, string body, long sim)
    {
        await _gate.WaitAsync();
        try
        {
            var message = StoreMessage(address, body, "in", null, false, sim, null);
            await EmitLockedAsync("sms.received", new JsonObject { ["message"] = Copy(message) });
            return Copy(message);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Sends an event to the subscribed clients.</summary>
    public Task EmitAsync(string name, JsonObject data) => Task.Run(() => EmitCoreAsync(name, data));

    private async Task EmitCoreAsync(string name, JsonObject data)
    {
        await _gate.WaitAsync();
        try
        {
            await EmitLockedAsync(name, data);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Behaves like a restart of the app: all connections are lost, the sequence numbers begin
    /// at 1 again and the epoch changes. Messages and the outbox are kept.
    /// </summary>
    public Task RestartAsync() => Task.Run(RestartCoreAsync);

    private async Task RestartCoreAsync()
    {
        await _gate.WaitAsync();
        try
        {
            foreach (var client in _clients.ToList())
            {
                client.Abort();
            }
            _sequence = 0;
            Epoch = Math.Max(Epoch + 1, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Closes all connections: abruptly, or with a close code.</summary>
    public Task DropConnectionsAsync(int? code = null, string reason = "") =>
        Task.Run(() => DropConnectionsCoreAsync(code, reason));

    private async Task DropConnectionsCoreAsync(int? code, string reason)
    {
        await _gate.WaitAsync();
        try
        {
            foreach (var client in _clients.ToList())
            {
                if (code is null)
                {
                    client.Abort();
                }
                else
                {
                    await CloseClientAsync(client, code.Value, reason);
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Drops the connection at the next request with this action: before the request is
    /// carried out, or after it was carried out but before the response is sent.
    /// </summary>
    public void DropOn(string action, string when = "after") => Locked(() =>
    {
        if (when is not ("before" or "after"))
        {
            throw new ArgumentException("when must be 'before' or 'after'");
        }
        Queue(_drops, action).Add(when);
    });

    /// <summary>Answers the next request with this action with an error.</summary>
    public void FailNext(
        string action,
        string code,
        string message = "Injected failure.",
        int? closeCode = null,
        JsonObject? extra = null) =>
        Locked(() => Queue(_failures, action).Add(new Failure(code, message, closeCode, extra)));

    /// <summary>Answers the next request with this action with this result.</summary>
    public void AnswerNext(string action, JsonObject result) => Locked(() => Queue(_results, action).Add(result));

    /// <summary>Does not answer the next requests with this action at all.</summary>
    public void IgnoreNext(string action, int times = 1) =>
        Locked(() => _ignored[action] = _ignored.GetValueOrDefault(action) + times);

    /// <summary>Sends these frames (JSON objects or raw text) right before the next response.</summary>
    public void BeforeNextResponse(params object[] frames) => Locked(() =>
        _framesBeforeResponse.AddRange(frames.Select(frame => frame as string ?? Serialize((JsonNode)frame))));

    /// <summary>Holds the next responses and sends them in reverse order.</summary>
    public void ReverseNextResponses(int count = 2) => Locked(() => _hold = count);

    /// <summary>Locks logins as after too many wrong passwords.</summary>
    public void LockLogins(double seconds) => Locked(() => _lockedUntil = Now() + seconds);

    public void FailNumber(string number) => Locked(() => _failNumbers.Add(number));

    public void ClearFailNumbers() => Locked(_failNumbers.Clear);

    /// <summary>Lets the phone refuse this number at once (<c>send_refused</c>), as it can in a group.</summary>
    public void RefuseNumber(string number) => Locked(() => _refuseNumbers.Add(number));

    /// <summary>The outbox entry of a request, or <c>null</c>.</summary>
    public JsonObject? Outbox(string requestId) =>
        Locked(() => _outbox.TryGetValue(requestId, out var entry) ? Copy(entry) : null);

    public List<JsonObject> OutboxEntries() => Locked(() => _outbox.Values.Select(Copy).ToList());

    /// <summary>Fills the server with a few contacts and conversations.</summary>
    public void SeedDemoData()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        AddContact("Anna Example", "+4369912345678");
        AddContact("Ben Example", "+4369087654321", "Work");
        (string Address, string Direction, string Body, int Age)[] rows =
        [
            ("+4369912345678", "in", "Are you coming on Friday?", 300),
            ("+4369912345678", "out", "Yes, at eight.", 290),
            ("+4369912345678", "in", "Great, see you then.", 280),
            ("+4369087654321", "out", "The server is up again.", 200),
            ("+4369087654321", "in", "Thanks!", 190),
            ("A1", "in", "Your invoice is ready.", 120),
            ("+436641234567", "in", "STATUS", 5),
        ];
        foreach (var (address, direction, body, age) in rows)
        {
            AddMessage(address, body, direction, now - (age * 60_000L), age > 100);
        }
    }

    // ------------------------------------------------------------------
    // Phone numbers
    // ------------------------------------------------------------------

    /// <summary>Removes the characters that are ignored in phone numbers.</summary>
    public static string NormalizeNumber(string number) => IgnoredCharacters().Replace(number.Trim(), "");

    /// <summary>Whether an address is a phone number and not a sender name.</summary>
    public static bool IsNumber(string address) => DigitsOnly().IsMatch(NormalizeNumber(address));

    /// <summary>Whether two addresses mean the same peer ("Phone numbers" in API.md).</summary>
    public static bool SamePeer(string first, string second)
    {
        var a = AddressKey(first);
        var b = AddressKey(second);
        if (a == b)
        {
            return true;
        }
        if (!IsNumber(first) || !IsNumber(second) || (a.StartsWith('+') && b.StartsWith('+')))
        {
            return false;
        }

        static bool Ends(string full, string national)
        {
            var significant = national.TrimStart('0');
            return significant.Length >= 7 && full.EndsWith(significant, StringComparison.Ordinal);
        }

        if (a.StartsWith('+'))
        {
            return Ends(a, b);
        }
        return b.StartsWith('+') ? Ends(b, a) : Ends(a, b) || Ends(b, a);
    }

    /// <summary>Returns the number of SMS parts of a text (simplified alphabet test).</summary>
    public static int CountParts(string body)
    {
        var length = body.EnumerateRunes().Count();
        var plain = body.All(character => character < 128);
        var (single, multi) = plain ? (160, 153) : (70, 67);
        return length <= single ? 1 : (int)Math.Ceiling(length / (double)multi);
    }

    private static string AddressKey(string address)
    {
        if (!IsNumber(address))
        {
            return address.Trim().ToLowerInvariant();
        }
        var digits = NormalizeNumber(address);
        return digits.StartsWith("00", StringComparison.Ordinal) && digits.Length > 4 ? "+" + digits[2..] : digits;
    }

    // ------------------------------------------------------------------
    // Connections
    // ------------------------------------------------------------------

    private static double Now() => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

    private static JsonObject Copy(JsonObject value) => (JsonObject)value.DeepClone();

    private static string Serialize(JsonNode? node) => node is null ? "null" : node.ToJsonString(s_json);

    private static List<T> Queue<T>(Dictionary<string, List<T>> map, string action)
    {
        if (!map.TryGetValue(action, out var list))
        {
            map[action] = list = [];
        }
        return list;
    }

    private T Locked<T>(Func<T> read)
    {
        _gate.Wait();
        try
        {
            return read();
        }
        finally
        {
            _gate.Release();
        }
    }

    private void Locked(Action change) => Locked(() =>
    {
        change();
        return 0;
    });

    private async Task AcceptLoopAsync()
    {
        while (true)
        {
            TcpClient tcp;
            try
            {
                tcp = await _listener.AcceptTcpClientAsync(_stop.Token);
            }
            catch (Exception error) when (error is SocketException or ObjectDisposedException
                or OperationCanceledException or InvalidOperationException)
            {
                return;
            }
            var handler = Task.Run(() => HandleAsync(tcp));
            await _gate.WaitAsync();
            _handlers.Add(handler);
            _gate.Release();
        }
    }

    private async Task HandleAsync(TcpClient tcp)
    {
        Client? client = null;
        try
        {
            tcp.NoDelay = true;
            var tls = new SslStream(tcp.GetStream(), leaveInnerStreamOpen: false);
            await tls.AuthenticateAsServerAsync(
                new SslServerAuthenticationOptions { ServerCertificate = _options.Tls.Certificate }, _stop.Token);
            var head = await ReadHeadAsync(tls);
            var lines = head.Split("\r\n");
            var path = lines[0].Split(' ').ElementAtOrDefault(1) ?? "";
            var key = lines.Skip(1).Select(line => line.Split(':', 2))
                .FirstOrDefault(pair => pair[0].Equals("Sec-WebSocket-Key", StringComparison.OrdinalIgnoreCase))
                ?.ElementAtOrDefault(1)?.Trim();
            if (path.Split('?')[0] != "/ws" || key is null)
            {
                await tls.WriteAsync(Encoding.ASCII.GetBytes(
                    "HTTP/1.1 404 Not Found\r\nContent-Type: text/plain\r\nContent-Length: 10\r\n"
                    + "Connection: close\r\n\r\nNot found\n"));
                await tls.FlushAsync();
                tcp.Close();
                return;
            }
            var accept = Convert.ToBase64String(
                SHA1.HashData(Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
            await tls.WriteAsync(Encoding.ASCII.GetBytes(
                "HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n"
                + $"Sec-WebSocket-Accept: {accept}\r\n\r\n"));
            await tls.FlushAsync();
            var socket = WebSocket.CreateFromStream(
                tls, new WebSocketCreationOptions { IsServer = true, KeepAliveInterval = TimeSpan.FromSeconds(20) });
            client = new Client(socket, tcp);

            await _gate.WaitAsync();
            var full = _clients.Count >= _options.MaxClients;
            if (!full)
            {
                _clients.Add(client);
                ConnectionCount++;
            }
            _gate.Release();
            if (full)
            {
                await CloseClientAsync(client, 4409, "too many clients");
                await DrainAsync(client);
                return;
            }
            _ = AuthTimeoutAsync(client);
            await SendAsync(client, Serialize(new JsonObject
            {
                ["event"] = "hello",
                ["protocolVersion"] = ProtocolVersion,
                ["authenticationRequired"] = true,
            }));
            await ReceiveLoopAsync(client);
        }
        catch (Exception error) when (error is IOException or SocketException or WebSocketException
            or ObjectDisposedException or OperationCanceledException or InvalidOperationException
            or System.Security.Authentication.AuthenticationException)
        {
            // The client went away.
        }
        finally
        {
            if (client is not null)
            {
                await _gate.WaitAsync();
                _clients.Remove(client);
                _gate.Release();
                client.Abort();
            }
            tcp.Dispose();
        }
    }

    private static async Task<string> ReadHeadAsync(Stream stream)
    {
        var bytes = new List<byte>();
        var one = new byte[1];
        while (bytes.Count < 16 * 1024)
        {
            if (await stream.ReadAsync(one) == 0)
            {
                throw new IOException("closed");
            }
            bytes.Add(one[0]);
            var count = bytes.Count;
            if (count >= 4 && bytes[count - 4] == '\r' && bytes[count - 3] == '\n'
                && bytes[count - 2] == '\r' && bytes[count - 1] == '\n')
            {
                break;
            }
        }
        return Encoding.ASCII.GetString(bytes.ToArray());
    }

    private async Task AuthTimeoutAsync(Client client)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(_options.AuthTimeout), client.Closed.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        if (!client.Authenticated)
        {
            await CloseClientAsync(client, 4401, "not authenticated in time");
        }
    }

    private async Task ReceiveLoopAsync(Client client)
    {
        var buffer = new byte[16 * 1024];
        using var message = new MemoryStream();
        while (true)
        {
            var result = await client.Socket.ReceiveAsync(buffer.AsMemory(), _stop.Token);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                await client.AnswerCloseAsync();
                return;
            }
            if (client.Closing)
            {
                continue;
            }
            message.Write(buffer, 0, result.Count);
            if (message.Length > MaxFrameBytes)
            {
                await CloseClientAsync(client, 1009, "frame too large");
                continue;
            }
            if (!result.EndOfMessage)
            {
                continue;
            }
            if (result.MessageType != WebSocketMessageType.Text)
            {
                await CloseClientAsync(client, 4400, "binary frame");
                continue;
            }
            var frame = Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
            message.SetLength(0);
            await _gate.WaitAsync();
            try
            {
                await OnFrameAsync(client, frame);
            }
            finally
            {
                _gate.Release();
            }
            if (client.Aborted)
            {
                return;
            }
        }
    }

    /// <summary>Reads until the client answers a close frame, but not for long.</summary>
    private async Task DrainAsync(Client client)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        limit.CancelAfter(2000);
        var buffer = new byte[1024];
        while (true)
        {
            var result = await client.Socket.ReceiveAsync(buffer.AsMemory(), limit.Token);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return;
            }
        }
    }

    private static async Task CloseClientAsync(Client client, int code, string reason)
    {
        client.Closing = true;
        await client.SendLock.WaitAsync();
        try
        {
            using var limit = new CancellationTokenSource(2000);
            await client.Socket.CloseOutputAsync((WebSocketCloseStatus)code, reason, limit.Token);
        }
        catch (Exception error) when (error is IOException or WebSocketException or ObjectDisposedException
            or OperationCanceledException or InvalidOperationException)
        {
            // Gone already.
        }
        finally
        {
            client.SendLock.Release();
        }
        // A client that does not answer the close frame is cut off.
        _ = Task.Delay(2000).ContinueWith(_ => client.Abort(), TaskScheduler.Default);
    }

    private static async Task SendAsync(Client client, string frame)
    {
        await client.SendLock.WaitAsync();
        try
        {
            using var limit = new CancellationTokenSource(10_000);
            await client.Socket.SendAsync(
                Encoding.UTF8.GetBytes(frame), WebSocketMessageType.Text, true, limit.Token);
        }
        catch (Exception error) when (error is IOException or WebSocketException or ObjectDisposedException
            or OperationCanceledException or InvalidOperationException)
        {
            // Gone already.
        }
        finally
        {
            client.SendLock.Release();
        }
    }

    private async Task OnFrameAsync(Client client, string frame)
    {
        JsonNode? parsed;
        try
        {
            parsed = JsonNode.Parse(frame);
        }
        catch (JsonException)
        {
            parsed = null;
        }
        var request = parsed as JsonObject;
        if (!WithinRate(client))
        {
            // Every frame counts, the login and unreadable ones as well.
            var given = request?["id"];
            var echoed = given is JsonValue value
                && value.GetValueKind() is JsonValueKind.String or JsonValueKind.Number
                ? given.DeepClone()
                : null;
            RefusedCount++;
            await SendAsync(client, Serialize(new JsonObject
            {
                ["id"] = echoed,
                ["ok"] = false,
                ["error"] = new JsonObject
                {
                    ["code"] = "rate_limited", ["message"] = "Too many requests.", ["retryAfter"] = 1,
                },
            }));
            client.Refused++;
            if (client.Refused > _options.MaxRefused)
            {
                await CloseClientAsync(client, 4429, "Too many requests.");
            }
            return;
        }
        client.Refused = 0;
        if (request is null)
        {
            await RespondAsync(client, Serialize(new JsonObject
            {
                ["id"] = null,
                ["ok"] = false,
                ["error"] = new JsonObject
                {
                    ["code"] = "invalid_json", ["message"] = "The frame is not a JSON object.",
                },
            }));
            return;
        }
        _requests.Add(Copy(request));
        var identifier = request["id"];
        var action = PyText.AsString(request["action"]);
        var drops = action is not null && _drops.TryGetValue(action, out var queued) ? queued : null;
        if (drops is { Count: > 0 } && drops[0] == "before")
        {
            drops.RemoveAt(0);
            client.Abort();
            return;
        }

        int? closeCode = null;
        string? closeReason = null;
        JsonObject response;
        try
        {
            var result = await DispatchAsync(client, request);
            if (result is null)
            {
                return;
            }
            response = new JsonObject { ["id"] = identifier?.DeepClone(), ["ok"] = true, ["result"] = result };
        }
        catch (Failure failure)
        {
            response = new JsonObject
            {
                ["id"] = identifier?.DeepClone(), ["ok"] = false, ["error"] = failure.Error(),
            };
            closeCode = failure.CloseCode;
            closeReason = failure.Code;
        }

        if (drops is { Count: > 0 } && drops[0] == "after")
        {
            drops.RemoveAt(0);
            client.Abort();
            return;
        }
        await RespondAsync(client, Serialize(response));
        if (closeCode is { } code)
        {
            await CloseClientAsync(client, code, closeReason ?? "");
        }
    }

    /// <summary>Counts a frame; only so many fit in a second.</summary>
    private bool WithinRate(Client client)
    {
        var now = Now();
        var times = client.RequestTimes;
        while (times.Count > 0 && now - times.Peek() >= 1.0)
        {
            times.Dequeue();
        }
        if (times.Count >= _options.RequestsPerSecond)
        {
            return false;
        }
        times.Enqueue(now);
        return true;
    }

    private async Task RespondAsync(Client client, string response)
    {
        var frames = _framesBeforeResponse.ToList();
        _framesBeforeResponse.Clear();
        foreach (var frame in frames)
        {
            await SendAsync(client, frame);
        }
        if (_hold > 0)
        {
            _held.Add(response);
            if (_held.Count < _hold)
            {
                return;
            }
            var held = _held.ToList();
            _held.Clear();
            _hold = 0;
            held.Reverse();
            foreach (var item in held)
            {
                await SendAsync(client, item);
            }
            return;
        }
        await SendAsync(client, response);
    }

    // ------------------------------------------------------------------
    // Actions
    // ------------------------------------------------------------------

    private async Task<JsonObject?> DispatchAsync(Client client, JsonObject request)
    {
        if (PyText.AsString(request["action"]) is not { } action)
        {
            throw new Failure("invalid_request", "The field 'action' is missing.");
        }
        var identifier = request["id"];
        if (identifier is not null
            && !(identifier is JsonValue && identifier.GetValueKind() is JsonValueKind.String or JsonValueKind.Number))
        {
            throw new Failure("invalid_request", "'id' must be a string or a number.");
        }
        if (_ignored.GetValueOrDefault(action) > 0)
        {
            _ignored[action]--;
            return null;
        }
        if (action == "authenticate")
        {
            return Authenticate(client, request);
        }
        if (!client.Authenticated)
        {
            throw new Failure("not_authenticated", "Authenticate first.");
        }
        if (_failures.TryGetValue(action, out var failures) && failures.Count > 0)
        {
            var failure = failures[0];
            failures.RemoveAt(0);
            throw failure;
        }
        if (_results.TryGetValue(action, out var results) && results.Count > 0)
        {
            var result = results[0];
            results.RemoveAt(0);
            return Copy(result);
        }
        return action switch
        {
            "status" => Status(),
            "ping" => new JsonObject { ["time"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() },
            "conversations.list" => ConversationsList(request),
            "messages.list" => MessagesList(request),
            "messages.get" => Copy(FindMessage(request)),
            "messages.status" => MessagesStatus(request),
            "messages.delete" => await MessagesDeleteAsync(request),
            "conversations.delete" => await ConversationsDeleteAsync(request),
            "conversations.markRead" => ConversationsMarkRead(request),
            "contacts.list" => ContactsList(request),
            "messages.send" => await MessagesSendAsync(request),
            "messages.sendGroup" => await MessagesSendGroupAsync(request),
            "subscribe" => Subscribe(client, request),
            "unsubscribe" => Unsubscribe(client, request),
            _ => throw new Failure("unknown_action", $"Unknown action '{action}'."),
        };
    }

    private JsonObject Authenticate(Client client, JsonObject request)
    {
        LoginCount++;
        // Assumption, as in the app: the licence is looked at before the password, so a
        // login without licence never counts as wrong.
        if (LicenceEnforced && !Licensed)
        {
            throw new Failure("licence_required", "The API needs the full licence of SMS Dock.", 4403);
        }
        var remaining = _lockedUntil - Now();
        if (remaining > 0)
        {
            var seconds = (int)Math.Ceiling(remaining);
            throw new Failure(
                "locked_out", $"Too many attempts. Try again in {seconds} seconds.", 4401,
                new JsonObject { ["retryAfter"] = seconds });
        }
        if (PyText.AsString(request["password"]) != _options.Password)
        {
            FailedLogins++;
            if (FailedLogins >= 5)
            {
                var seconds = Math.Min(30 * Math.Pow(2, _lockCount), 900);
                _lockCount++;
                FailedLogins = 0;
                _lockedUntil = Now() + seconds;
            }
            throw new Failure("invalid_password", "Wrong password.", 4401);
        }
        FailedLogins = 0;
        _lockCount = 0;
        client.Authenticated = true;
        return new JsonObject
        {
            ["protocolVersion"] = ProtocolVersion,
            ["app"] = AppInfo(),
            ["latestSeq"] = _sequence,
            ["epoch"] = Epoch,
        };
    }

    private JsonObject AppInfo() => new()
    {
        ["name"] = "SMS Dock", ["version"] = "1.0.0", ["protocolVersion"] = ProtocolVersion,
    };

    private JsonObject Status() => new()
    {
        ["app"] = AppInfo(),
        ["sims"] = new JsonArray(_sims.Select(sim => (JsonNode)Copy(sim)).ToArray()),
        ["permissions"] = new JsonObject
        {
            ["readSms"] = true, ["sendSms"] = true, ["receiveSms"] = true, ["contacts"] = true,
            ["phoneState"] = true, ["notifications"] = true,
        },
        ["defaultSmsApp"] = DeleteMode == "provider",
        ["deleteMode"] = DeleteMode,
        ["licence"] = new JsonObject
        {
            ["enforced"] = LicenceEnforced, ["licensed"] = Licensed, ["dailyLimit"] = 10,
            ["sentToday"] = _sent.Count, ["remaining"] = null,
        },
        ["popup"] = true,
    };

    private JsonObject ConversationsList(JsonObject request)
    {
        var query = OptionalString(request, "q");
        var unread = OptionalBool(request, "unread") ?? false;
        var sim = OptionalInteger(request, "sim");
        IEnumerable<JsonObject> items = Conversations();
        if (!string.IsNullOrEmpty(query))
        {
            var needle = query.ToLowerInvariant();
            var matching = _messages.Where(message => Text(message, "body").ToLowerInvariant().Contains(needle))
                .Select(message => Number(message, "conversationId")).ToHashSet();
            items = items.Where(item => matching.Contains(Number(item, "id"))
                || Text(item, "address").ToLowerInvariant().Contains(needle)
                || (PyText.AsString(item["contact"]) ?? "").ToLowerInvariant().Contains(needle));
        }
        if (unread)
        {
            items = items.Where(item => Number(item, "unreadCount") > 0);
        }
        if (sim is not null)
        {
            items = items.Where(item => PyText.AsInteger(item["sim"]) == sim);
        }
        return Page(items.ToList(), request);
    }

    private JsonObject MessagesList(JsonObject request)
    {
        var address = OptionalString(request, "address");
        var conversation = OptionalInteger(request, "conversationId");
        var direction = OptionalString(request, "direction");
        direction = string.IsNullOrEmpty(direction) ? "all" : direction;
        var query = OptionalString(request, "q");
        var start = OptionalInteger(request, "from");
        var end = OptionalInteger(request, "to");
        var sim = OptionalInteger(request, "sim");
        var unread = OptionalBool(request, "unread") ?? false;
        if (direction is not ("in" or "out" or "all"))
        {
            throw new Failure("invalid_request", "'direction' must be 'in', 'out' or 'all'.");
        }
        IEnumerable<JsonObject> items = _messages
            .OrderByDescending(message => Number(message, "timestamp"))
            .ThenByDescending(message => Number(message, "id"));
        if (conversation is not null)
        {
            // conversationId wins if address is given too.
            items = items.Where(message => Number(message, "conversationId") == conversation);
        }
        else if (address is not null)
        {
            items = items.Where(message => SamePeer(Text(message, "address"), address));
        }
        if (direction != "all")
        {
            items = items.Where(message => Text(message, "direction") == direction);
        }
        if (!string.IsNullOrEmpty(query))
        {
            items = items.Where(
                message => Text(message, "body").ToLowerInvariant().Contains(query.ToLowerInvariant()));
        }
        if (start is not null)
        {
            items = items.Where(message => Number(message, "timestamp") >= start);
        }
        if (end is not null)
        {
            items = items.Where(message => Number(message, "timestamp") <= end);
        }
        if (sim is not null)
        {
            items = items.Where(message => PyText.AsInteger(message["sim"]) == sim);
        }
        if (unread)
        {
            items = items.Where(message => !message["read"]!.GetValue<bool>());
        }
        return Page(items.ToList(), request);
    }

    private JsonObject FindMessage(JsonObject request)
    {
        var identifier = RequiredInteger(request, "messageId");
        return _messages.FirstOrDefault(message => Number(message, "id") == identifier)
            ?? throw new Failure("not_found", $"No message with the id {identifier}.");
    }

    private JsonObject MessagesStatus(JsonObject request)
    {
        var requestId = RequiredString(request, "requestId");
        return _outbox.TryGetValue(requestId, out var entry)
            ? Copy(entry)
            : throw new Failure("not_found", $"No message with the requestId {requestId}.");
    }

    private async Task<JsonObject> MessagesDeleteAsync(JsonObject request)
    {
        var message = FindMessage(request);
        _messages.Remove(message);
        await EmitLockedAsync("sms.deleted", new JsonObject
        {
            ["id"] = Number(message, "id"),
            ["conversationId"] = Number(message, "conversationId"),
            ["address"] = Text(message, "address"),
            ["mode"] = DeleteMode,
        });
        return new JsonObject { ["deleted"] = 1, ["mode"] = DeleteMode };
    }

    private async Task<JsonObject> ConversationsDeleteAsync(JsonObject request)
    {
        var identifier = RequiredInteger(request, "conversationId");
        var doomed = _messages.Where(message => Number(message, "conversationId") == identifier).ToList();
        if (doomed.Count == 0)
        {
            throw new Failure("not_found", "This conversation does not exist.");
        }
        var newest = doomed.MaxBy(message => (Number(message, "timestamp"), Number(message, "id")))!;
        _messages.RemoveAll(doomed.Contains);
        await EmitLockedAsync("sms.deleted", new JsonObject
        {
            ["id"] = null,
            ["conversationId"] = identifier,
            ["address"] = Text(newest, "address"),
            ["mode"] = DeleteMode,
        });
        return new JsonObject { ["deleted"] = doomed.Count, ["mode"] = DeleteMode };
    }

    private JsonObject ConversationsMarkRead(JsonObject request)
    {
        // API.md names no order of the two errors. Assumption, as in the app: the default
        // SMS app is checked first.
        var identifier = RequiredInteger(request, "conversationId");
        if (DeleteMode != "provider")
        {
            throw new Failure("not_default_app", "Only the default SMS app can mark messages as read.");
        }
        var found = _messages.Where(message => Number(message, "conversationId") == identifier).ToList();
        if (found.Count == 0)
        {
            throw new Failure("not_found", "This conversation does not exist.");
        }
        foreach (var message in found)
        {
            message["read"] = true;
        }
        return new JsonObject { ["ok"] = true };
    }

    private JsonObject ContactsList(JsonObject request)
    {
        var query = OptionalString(request, "q");
        IEnumerable<JsonObject> items = _contacts.OrderBy(
            contact => Text(contact, "name").ToLowerInvariant(), StringComparer.Ordinal);
        if (!string.IsNullOrEmpty(query))
        {
            var needle = query.ToLowerInvariant();
            items = items.Where(contact => Text(contact, "name").ToLowerInvariant().Contains(needle)
                || ((JsonArray)contact["numbers"]!).Any(number => Text((JsonObject)number!, "number").Contains(needle)));
        }
        return Page(items.ToList(), request, byIndex: true);
    }

    private async Task<JsonObject> MessagesSendAsync(JsonObject request)
    {
        var to = RequiredString(request, "to");
        var body = RequiredString(request, "body");
        var sim = OptionalInteger(request, "sim");
        var requestId = OptionalString(request, "requestId");
        if (requestId is not null && !RequestIdPattern().IsMatch(requestId))
        {
            throw new Failure("invalid_request", "'requestId' must be 1 to 64 characters of A-Z a-z 0-9 . _ -.");
        }
        var number = NormalizeNumber(to);
        if (!PhoneNumber().IsMatch(number))
        {
            throw new Failure("invalid_number", "'to' is not a phone number.");
        }
        if (body.Length == 0)
        {
            throw new Failure("invalid_request", "'body' is empty.");
        }
        var parts = CountParts(body);
        if (parts > 10)
        {
            throw new Failure(
                "message_too_long", $"The message needs {parts} SMS parts; at most 10 are allowed.");
        }
        if (requestId is not null && _outbox.TryGetValue(requestId, out var known))
        {
            // A repeat: never sent twice, answered with the current status.
            if (_bodies[requestId] != body || !SamePeer(Text(known, "to"), number))
            {
                throw new Failure("invalid_request", "'requestId' was already used for another message.");
            }
            return new JsonObject
            {
                ["requestId"] = requestId, ["to"] = Text(known, "to"), ["parts"] = Number(known, "parts"),
                ["status"] = Text(known, "status"),
            };
        }
        var subscription = Subscription(sim);
        requestId ??= Guid.NewGuid().ToString("N");
        var entry = await AcceptAsync(requestId, number, body, parts, subscription, null);
        return SendResultOf(entry);
    }

    /// <summary>Returns the SIM to send with; the default one without <paramref name="sim"/>.</summary>
    private long Subscription(long? sim)
    {
        if (sim is null)
        {
            return Number(_sims.First(candidate => candidate["isDefault"]!.GetValue<bool>()), "subscriptionId");
        }
        if (_sims.All(candidate => Number(candidate, "subscriptionId") != sim))
        {
            throw new Failure("sim_unavailable", $"No SIM with the subscription id {sim}.");
        }
        return sim.Value;
    }

    /// <summary>Queues one message that passed the checks and starts sending it.</summary>
    private async Task<JsonObject> AcceptAsync(
        string requestId, string number, string body, long parts, long sim, string? group)
    {
        var entry = new JsonObject
        {
            ["requestId"] = requestId, ["to"] = number, ["parts"] = parts, ["partsSent"] = 0,
            ["status"] = "queued", ["errorCode"] = null, ["messageId"] = null, ["sim"] = sim,
            ["source"] = "api", ["createdAt"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };
        _outbox[requestId] = entry;
        _bodies[requestId] = body;
        var sent = new JsonObject { ["requestId"] = requestId, ["to"] = number, ["body"] = body, ["sim"] = sim };
        if (group is not null)
        {
            sent["group"] = group;
        }
        _sent.Add(sent);
        await EmitLockedAsync("sms.queued", new JsonObject
        {
            ["requestId"] = requestId, ["to"] = number, ["parts"] = parts, ["sim"] = sim, ["source"] = "api",
        });
        if (_refuseNumbers.Contains(number))
        {
            // The phone did not even take the message.
            entry["status"] = "failed";
            await EmitLockedAsync("sms.failed", new JsonObject
            {
                ["requestId"] = requestId, ["id"] = null, ["conversationId"] = null, ["to"] = number,
                ["errorCode"] = null, ["reason"] = "send_refused",
            });
            return entry;
        }
        _ = Task.Run(() => TransmitAsync(entry, body));
        return entry;
    }

    private static JsonObject SendResultOf(JsonObject entry) => new()
    {
        ["requestId"] = Text(entry, "requestId"), ["to"] = Text(entry, "to"), ["parts"] = Number(entry, "parts"),
        ["status"] = Text(entry, "status"),
    };

    private static readonly string[] s_statusOrder = ["queued", "sending", "sent", "delivered"];

    /// <summary>Returns the GroupSendResult of the members as the app builds it.</summary>
    private static JsonObject GroupResultOf(string requestId, List<JsonObject> members)
    {
        var alive = members.Select(member => Text(member, "status")).Where(status => status != "failed").ToList();
        var status = alive.Count == 0 ? "failed" : alive.MinBy(value => Array.IndexOf(s_statusOrder, value))!;
        return new JsonObject
        {
            ["requestId"] = requestId,
            ["to"] = new JsonArray([.. members.Select(member => (JsonNode)Text(member, "to"))]),
            ["parts"] = Number(members[0], "parts"),
            ["status"] = status,
            ["messages"] = new JsonArray([.. members.Select(member => (JsonNode)SendResultOf(member))]),
        };
    }

    /// <summary>The members of a group sent before, oldest first.</summary>
    private List<JsonObject> KnownGroup(string requestId)
    {
        var members = new List<JsonObject>();
        for (var index = 1; _outbox.TryGetValue($"{requestId}.{index}", out var member); index++)
        {
            members.Add(member);
        }
        return members;
    }

    private async Task<JsonObject> MessagesSendGroupAsync(JsonObject request)
    {
        if (request["to"] is not JsonArray list)
        {
            throw new Failure("invalid_request", "'to' is missing.");
        }
        var body = RequiredString(request, "body");
        var sim = OptionalInteger(request, "sim");
        var requestId = OptionalString(request, "requestId");
        if (list.Count == 0)
        {
            throw new Failure("invalid_request", "'to' is missing.");
        }
        var numbers = new List<string>();
        foreach (var item in list)
        {
            var text = PyText.AsString(item) ?? throw new Failure("invalid_request", "'to' must contain strings.");
            var number = NormalizeNumber(text);
            if (!PhoneNumber().IsMatch(number))
            {
                throw new Failure("invalid_number", $"'to' contains '{text}', which is not a phone number.");
            }
            if (!numbers.Any(known => SamePeer(known, number)))
            {
                numbers.Add(number);
            }
        }
        if (numbers.Count > 20)
        {
            throw new Failure("invalid_request", "A group message goes to at most 20 recipients.");
        }
        if (body.Length == 0)
        {
            throw new Failure("invalid_request", "'body' is empty.");
        }
        var parts = CountParts(body);
        if (parts > 10)
        {
            throw new Failure(
                "message_too_long", $"The message needs {parts} SMS parts; at most 10 are allowed.");
        }
        if (requestId is not null && (!RequestIdPattern().IsMatch(requestId) || requestId.Length > 61))
        {
            throw new Failure(
                "invalid_request", "'requestId' of a group must be 1 to 61 characters of A-Z a-z 0-9 . _ -.");
        }
        requestId ??= Guid.NewGuid().ToString("N");
        var known = KnownGroup(requestId);
        if (known.Count > 0)
        {
            var same = known.Count == numbers.Count
                && known.All(member => _bodies[Text(member, "requestId")] == body)
                && numbers.All(number => known.Any(member => SamePeer(Text(member, "to"), number)));
            if (!same)
            {
                throw new Failure("invalid_request", "'requestId' was already used for another message.");
            }
            return GroupResultOf(requestId, known);
        }
        var total = parts * numbers.Count;
        if (total > 20)
        {
            throw new Failure(
                "invalid_request",
                $"This group needs {total} SMS parts; at most 20 can be sent per minute. "
                + "Send to fewer recipients or shorten the text.");
        }
        var subscription = Subscription(sim);
        var members = new List<JsonObject>();
        for (var index = 0; index < numbers.Count; index++)
        {
            members.Add(await AcceptAsync(
                $"{requestId}.{index + 1}", numbers[index], body, parts, subscription, requestId));
        }
        return GroupResultOf(requestId, members);
    }

    private async Task TransmitAsync(JsonObject entry, string body)
    {
        try
        {
            var delay = TimeSpan.FromSeconds(_options.SendDelay);
            // Read once: only this task and requests under the gate touch the entry.
            var number = Locked(() => Text(entry, "to"));
            var parts = Locked(() => Number(entry, "parts"));
            var fails = Locked(() => _failNumbers.Contains(number));
            if (parts > 1 && !fails)
            {
                // Some parts are sent, some are left.
                await Task.Delay(delay / 2, _stop.Token);
                Locked(() =>
                {
                    entry["status"] = "sending";
                    entry["partsSent"] = 1;
                });
                await Task.Delay(delay / 2, _stop.Token);
            }
            else
            {
                await Task.Delay(delay, _stop.Token);
            }
            await _gate.WaitAsync(_stop.Token);
            JsonObject message;
            try
            {
                var requestId = Text(entry, "requestId");
                if (_failNumbers.Contains(number))
                {
                    // The phone has not stored the message: no id, no conversation.
                    entry["status"] = "failed";
                    entry["errorCode"] = 1;
                    await EmitLockedAsync("sms.failed", new JsonObject
                    {
                        ["requestId"] = requestId, ["id"] = null, ["conversationId"] = null, ["to"] = number,
                        ["errorCode"] = 1, ["reason"] = "generic_failure",
                    });
                    return;
                }
                message = StoreMessage(
                    number, body, "out", null, true, PyText.AsInteger(entry["sim"]), requestId);
                entry["status"] = "sent";
                entry["partsSent"] = parts;
                entry["messageId"] = Number(message, "id");
                await EmitLockedAsync("sms.sent", new JsonObject
                {
                    ["requestId"] = requestId,
                    ["conversationId"] = Number(message, "conversationId"),
                    ["to"] = number,
                    ["message"] = Copy(message),
                });
            }
            finally
            {
                _gate.Release();
            }
            if (_options.DeliveryReports)
            {
                await Task.Delay(delay, _stop.Token);
                await _gate.WaitAsync(_stop.Token);
                try
                {
                    entry["status"] = "delivered";
                    message["status"] = "delivered";
                    await EmitLockedAsync("sms.delivered", new JsonObject
                    {
                        ["requestId"] = Text(entry, "requestId"),
                        ["id"] = Number(message, "id"),
                        ["conversationId"] = Number(message, "conversationId"),
                        ["to"] = number,
                    });
                }
                finally
                {
                    _gate.Release();
                }
            }
        }
        catch (OperationCanceledException)
        {
            // The server stopped.
        }
    }

    private static JsonObject Subscribe(Client client, JsonObject request)
    {
        // subscribe adds the names; its address replaces the filter, and without address
        // (or with null) the filter is removed.
        var names = Names(request);
        var address = OptionalString(request, "address");
        client.Events.UnionWith(names);
        client.Address = address;
        return Subscription(client);
    }

    private static JsonObject Unsubscribe(Client client, JsonObject request)
    {
        client.Events.ExceptWith(Names(request));
        if (client.Events.Count == 0)
        {
            client.Address = null;
        }
        return Subscription(client);
    }

    // ------------------------------------------------------------------
    // Data
    // ------------------------------------------------------------------

    private static string Text(JsonObject value, string name) => PyText.AsString(value[name]) ?? "";

    private static long Number(JsonObject value, string name) => PyText.AsInteger(value[name]) ?? 0;

    private static string? OptionalString(JsonObject request, string name) => request[name] switch
    {
        null => null,
        JsonValue value when value.GetValueKind() == JsonValueKind.String => value.GetValue<string>(),
        _ => throw new Failure("invalid_request", $"The field '{name}' is malformed."),
    };

    private static long? OptionalInteger(JsonObject request, string name)
    {
        if (request[name] is null)
        {
            return null;
        }
        return PyText.AsInteger(request[name])
            ?? throw new Failure("invalid_request", $"The field '{name}' is malformed.");
    }

    private static bool? OptionalBool(JsonObject request, string name) => request[name] switch
    {
        null => null,
        JsonValue value when value.GetValueKind() is JsonValueKind.True or JsonValueKind.False =>
            value.GetValue<bool>(),
        _ => throw new Failure("invalid_request", $"The field '{name}' is malformed."),
    };

    private static string RequiredString(JsonObject request, string name) =>
        OptionalString(request, name) ?? throw new Failure("invalid_request", $"The field '{name}' is missing.");

    private static long RequiredInteger(JsonObject request, string name) =>
        OptionalInteger(request, name) ?? throw new Failure("invalid_request", $"The field '{name}' is missing.");

    /// <summary>Returns the event names of a request, with <c>*</c> replaced.</summary>
    private static List<string> Names(JsonObject request)
    {
        if (request["events"] is not JsonArray { Count: > 0 } array
            || array.Any(item => PyText.AsString(item) is null))
        {
            throw new Failure("invalid_request", "'events' is missing.");
        }
        var names = array.Select(item => PyText.AsString(item)!).ToList();
        if (names.Contains("*"))
        {
            return [.. EventNames];
        }
        foreach (var name in names)
        {
            if (!EventNames.Contains(name))
            {
                throw new Failure("invalid_request", $"Unknown event '{name}'.");
            }
        }
        return names;
    }

    private static JsonObject Subscription(Client client) => new()
    {
        ["events"] = new JsonArray(
            EventNames.Where(client.Events.Contains).Select(name => (JsonNode)name).ToArray()),
        ["address"] = client.Address,
    };

    /// <summary>
    /// Whether a client subscribed to an event and its filter lets it pass. The filter applies
    /// to <c>sms.*</c> events that name a peer.
    /// </summary>
    private static bool Wants(Client client, string name, JsonObject data)
    {
        if (!client.Events.Contains(name))
        {
            return false;
        }
        if (client.Address is null || !name.StartsWith("sms.", StringComparison.Ordinal))
        {
            return true;
        }
        var peer = data["message"] is JsonObject message ? PyText.AsString(message["address"]) : null;
        if (string.IsNullOrEmpty(peer))
        {
            peer = PyText.AsString(data["to"]);
        }
        if (string.IsNullOrEmpty(peer))
        {
            peer = PyText.AsString(data["address"]);
        }
        return string.IsNullOrEmpty(peer) || SamePeer(peer, client.Address);
    }

    private static JsonObject Page(List<JsonObject> items, JsonObject request, bool byIndex = false)
    {
        long limit = 50;
        if (request.ContainsKey("limit"))
        {
            limit = PyText.AsInteger(request["limit"]) ?? 0;
        }
        if (limit is < 1 or > 200)
        {
            throw new Failure("invalid_request", "'limit' must be between 1 and 200.");
        }
        var cursor = OptionalString(request, "cursor");
        var start = 0;
        if (cursor is not null && byIndex)
        {
            if (cursor.Length == 0 || !cursor.All(char.IsAsciiDigit) || cursor.Length > 9)
            {
                throw new Failure("invalid_request", "'cursor' is invalid.");
            }
            start = int.Parse(cursor);
        }
        else if (cursor is not null)
        {
            var index = items.FindIndex(item => $"{Number(item, "timestamp")}:{Number(item, "id")}" == cursor);
            if (index < 0)
            {
                throw new Failure("invalid_request", "'cursor' is invalid.");
            }
            start = index + 1;
        }
        var chunk = items.Skip(start).Take((int)limit).ToList();
        string? next = null;
        if (chunk.Count > 0 && start + limit < items.Count)
        {
            next = byIndex
                ? (start + chunk.Count).ToString()
                : $"{Number(chunk[^1], "timestamp")}:{Number(chunk[^1], "id")}";
        }
        return new JsonObject
        {
            ["items"] = new JsonArray(chunk.Select(item => (JsonNode)Copy(item)).ToArray()),
            ["nextCursor"] = next,
        };
    }

    private JsonObject StoreMessage(
        string address, string body, string direction, long? timestamp, bool read, long? sim, string? requestId)
    {
        var time = timestamp ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var incoming = direction == "in";
        _nextMessageId++;
        var message = new JsonObject
        {
            ["id"] = _nextMessageId,
            ["conversationId"] = ConversationId(address),
            ["address"] = address,
            ["contact"] = ContactName(address),
            ["direction"] = direction,
            ["body"] = body,
            ["timestamp"] = time,
            ["sentTimestamp"] = incoming ? time - 2000 : null,
            ["read"] = read,
            ["status"] = incoming ? "received" : "sent",
            ["sim"] = sim,
            ["requestId"] = requestId,
        };
        _messages.Add(message);
        return message;
    }

    private async Task EmitLockedAsync(string name, JsonObject data)
    {
        _sequence++;
        var frame = Serialize(new JsonObject { ["event"] = name, ["seq"] = _sequence, ["data"] = Copy(data) });
        foreach (var client in _clients.ToList())
        {
            if (client.Authenticated && Wants(client, name, data))
            {
                await SendAsync(client, frame);
            }
        }
    }

    private long ConversationId(string address)
    {
        foreach (var (known, identifier) in _conversationIds)
        {
            if (SamePeer(known, address))
            {
                return identifier;
            }
        }
        var created = 170 + _conversationIds.Count;
        _conversationIds.Add((address, created));
        return created;
    }

    private string? ContactName(string address)
    {
        foreach (var contact in _contacts)
        {
            foreach (var number in (JsonArray)contact["numbers"]!)
            {
                if (SamePeer(Text((JsonObject)number!, "number"), address))
                {
                    return Text(contact, "name");
                }
            }
        }
        return null;
    }

    private List<JsonObject> Conversations()
    {
        var items = new List<JsonObject>();
        foreach (var group in _messages.GroupBy(message => Number(message, "conversationId")))
        {
            var newest = group.MaxBy(message => (Number(message, "timestamp"), Number(message, "id")))!;
            var snippet = Text(newest, "body");
            items.Add(new JsonObject
            {
                ["id"] = group.Key,
                ["address"] = Text(newest, "address"),
                ["contact"] = PyText.AsString(newest["contact"]),
                ["snippet"] = snippet.Length > 160 ? snippet[..160] : snippet,
                ["timestamp"] = Number(newest, "timestamp"),
                ["direction"] = Text(newest, "direction"),
                ["messageCount"] = group.Count(),
                ["unreadCount"] = group.Count(message => !message["read"]!.GetValue<bool>()),
                ["sim"] = PyText.AsInteger(newest["sim"]),
            });
        }
        return items.OrderByDescending(item => Number(item, "timestamp"))
            .ThenByDescending(item => Number(item, "id")).ToList();
    }

    [GeneratedRegex(@"[\s\-./()]")]
    private static partial Regex IgnoredCharacters();

    [GeneratedRegex(@"\A\+?[0-9]+\z")]
    private static partial Regex DigitsOnly();

    [GeneratedRegex(@"\A\+?[0-9]{3,15}\z")]
    private static partial Regex PhoneNumber();

    [GeneratedRegex(@"\A[A-Za-z0-9._-]{1,64}\z")]
    private static partial Regex RequestIdPattern();

    /// <summary>An error response, optionally followed by a close frame.</summary>
    private sealed class Failure(string code, string message, int? closeCode = null, JsonObject? extra = null)
        : Exception(message)
    {
        public string Code { get; } = code;

        public int? CloseCode { get; } = closeCode;

        public JsonObject Error()
        {
            var error = new JsonObject { ["code"] = Code, ["message"] = Message };
            if (extra is not null)
            {
                foreach (var pair in extra)
                {
                    error[pair.Key] = pair.Value?.DeepClone();
                }
            }
            return error;
        }
    }

    /// <summary>One connection of a client.</summary>
    private sealed class Client(WebSocket socket, TcpClient tcp)
    {
        public WebSocket Socket { get; } = socket;

        public SemaphoreSlim SendLock { get; } = new(1, 1);

        public CancellationTokenSource Closed { get; } = new();

        public HashSet<string> Events { get; } = [];

        public Queue<double> RequestTimes { get; } = new();

        public volatile bool Authenticated;

        public string? Address { get; set; }

        public int Refused { get; set; }

        public volatile bool Closing;

        public volatile bool Aborted;

        /// <summary>Cuts the connection without a close frame.</summary>
        public void Abort()
        {
            Aborted = true;
            try
            {
                Closed.Cancel();
                tcp.Client.Close(0);
            }
            catch (Exception error) when (error is ObjectDisposedException or SocketException
                or NullReferenceException)
            {
                // Closed already.
            }
        }

        public async Task AnswerCloseAsync()
        {
            Closed.Cancel();
            if (Socket.State != WebSocketState.CloseReceived)
            {
                return;
            }
            await SendLock.WaitAsync();
            try
            {
                using var limit = new CancellationTokenSource(2000);
                await Socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, limit.Token);
            }
            finally
            {
                SendLock.Release();
            }
        }
    }
}
