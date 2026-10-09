using System.Diagnostics;
using System.Text.Json.Nodes;

namespace SmsDock.Tests;

/// <summary>Starts fake servers and clients for a test and stops them at its end.</summary>
public abstract class ServerTestBase : IAsyncLifetime
{
    internal const string Password = "secret123";
    internal const long BaseTime = 1790672531000;
    internal const string Anna = "+4369912345678";
    internal const string Ben = "+4369087654321";
    internal const string Url = "wss://192.168.1.50:9443/ws";

    internal static readonly string Fingerprint = string.Join(":", Enumerable.Repeat("AB", 32));

    private readonly List<FakeSmsDock> _servers = [];
    private readonly List<SmsDockClient> _clients = [];
    private FakeSmsDock? _server;

    /// <summary>What the clients of this test wrote to standard error.</summary>
    internal StringWriter Warnings { get; } = new();

    /// <summary>The pauses the clients of this test asked for, when they are recorded.</summary>
    internal List<double> Pauses { get; } = [];

    /// <summary>A running fake server with the data of <see cref="Fill"/>.</summary>
    internal FakeSmsDock Server
    {
        get
        {
            // Helper tasks of a test ask for the server too; all must get the same one.
            lock (_servers)
            {
                return _server ??= StartServer();
            }
        }
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var client in _clients)
        {
            await client.CloseAsync();
        }
        foreach (var server in _servers)
        {
            await server.StopAsync();
        }
    }

    /// <summary>Stores 29 messages in three conversations.</summary>
    internal static void Fill(FakeSmsDock server)
    {
        server.AddContact("Anna Example", Anna);
        server.AddContact("Ben Example", Ben, "Work");
        for (var index = 0; index < 25; index++)
        {
            server.AddMessage(
                Anna, $"Anna {index}", index % 2 == 0 ? "in" : "out", BaseTime + (index * 1000), index < 22);
        }
        server.AddMessage(Ben, "Your invoice is attached", "out", BaseTime - 5000, sim: 2);
        server.AddMessage(Ben, "Thanks for the invoice", timestamp: BaseTime - 4000, sim: 2);
        server.AddMessage(Ben, "See you", timestamp: BaseTime - 3000, sim: 2);
        server.AddMessage("A1", "Your bill is ready", timestamp: BaseTime - 9000, read: false);
    }

    internal static async Task<bool> WaitUntilAsync(Func<bool> condition, double timeout = 3.0)
    {
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed.TotalSeconds < timeout)
        {
            if (condition())
            {
                return true;
            }
            await Task.Delay(10);
        }
        return condition();
    }

    internal static void AssertJson(string expected, JsonNode? actual) =>
        Assert.True(
            JsonNode.DeepEquals(JsonNode.Parse(expected), actual),
            $"expected {expected}\n  actual {actual?.ToJsonString() ?? "null"}");

    internal static async Task<List<T>> ListAsync<T>(IAsyncEnumerable<T> items)
    {
        var list = new List<T>();
        await foreach (var item in items)
        {
            list.Add(item);
        }
        return list;
    }

    internal static JsonObject LastRequest(FakeSmsDock server, string action) =>
        server.Requests.Last(request => PyText.AsString(request["action"]) == action);

    internal static int Count(FakeSmsDock server, string action) =>
        server.Actions.Count(name => name == action);

    /// <summary>Returns an <c>sms.received</c> event as the server would send it.</summary>
    internal static JsonObject EventFrame(long sequence, string body = "Hi") => new()
    {
        ["event"] = "sms.received",
        ["seq"] = sequence,
        ["data"] = new JsonObject
        {
            ["message"] = new JsonObject
            {
                ["id"] = 9000 + sequence, ["conversationId"] = 1, ["address"] = Anna, ["contact"] = null,
                ["direction"] = "in", ["body"] = body, ["timestamp"] = BaseTime, ["sentTimestamp"] = BaseTime,
                ["read"] = false, ["status"] = "received", ["sim"] = 1, ["requestId"] = null,
            },
        },
    };

    internal FakeSmsDock StartServer(FakeOptions? options = null)
    {
        var server = new FakeSmsDock(options);
        Fill(server);
        _servers.Add(server);
        return server.Start();
    }

    /// <summary>Makes a client for a server without connecting it.</summary>
    internal SmsDockClient NewClient(
        FakeSmsDock server,
        Action<SmsDockClientOptions>? configure = null,
        string password = Password,
        bool recordPauses = false)
    {
        var options = new SmsDockClientOptions
        {
            Fingerprint = server.Tls.Fingerprint,
            ReconnectDelay = TimeSpan.FromMilliseconds(10),
            Timeout = TimeSpan.FromSeconds(5),
            Environment = _ => null,
            Error = Warnings,
        };
        if (recordPauses)
        {
            options.Delay = (pause, _) =>
            {
                Pauses.Add(pause.TotalSeconds);
                return Task.CompletedTask;
            };
        }
        configure?.Invoke(options);
        var client = new SmsDockClient(server.Url, password, options);
        _clients.Add(client);
        return client;
    }

    /// <summary>Makes a client that is connected and logged in.</summary>
    internal async Task<SmsDockClient> ConnectAsync(
        FakeSmsDock? server = null, Action<SmsDockClientOptions>? configure = null, bool recordPauses = false)
    {
        var client = NewClient(server ?? Server, configure, recordPauses: recordPauses);
        await client.ConnectAsync();
        return client;
    }
}
