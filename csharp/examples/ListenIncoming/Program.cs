// Hook for incoming SMS: call a method for every SMS that arrives.
//
// Usage:
//
//     export SMSDOCK_PASSWORD='your password'
//     export SMSDOCK_FINGERPRINT='AB:CD:…'      # from the Server screen of the app
//     dotnet run --project examples/ListenIncoming -- wss://192.168.1.50:9443/ws
//
// Stop with Ctrl+C.

using SmsDock;

const string Usage = """
    Hook for incoming SMS: call a method for every SMS that arrives.

    Usage:

        export SMSDOCK_PASSWORD='your password'
        export SMSDOCK_FINGERPRINT='AB:CD:…'      # from the Server screen of the app
        dotnet run --project examples/ListenIncoming -- wss://192.168.1.50:9443/ws

    Stop with Ctrl+C.
    """;

if (args.Length != 1)
{
    Console.WriteLine(Usage);
    return 2;
}

// Ids of the messages the hook has seen, and the time of the newest one.
var seen = new HashSet<long>();
long newest = 0;

using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, pressed) =>
{
    pressed.Cancel = true;
    stop.Cancel();
};

try
{
    // Password and fingerprint come from the environment.
    var options = new SmsDockClientOptions
    {
        // Keep trying when the phone is out of reach for a while.
        ReconnectAttempts = null,
        OnReconnect = CatchUpAsync,
    };
    await using var dock = await SmsDockClient.ConnectAsync(args[0], options: options, cancellationToken: stop.Token);
    // Only messages from now on: remember the time of the phone.
    newest = (await dock.PingAsync(stop.Token)).Time;
    // A new connection gets no events. Ask for the ones you need.
    await dock.SubscribeAsync("sms.received", cancellationToken: stop.Token);
    Console.WriteLine("Waiting for SMS. Press Ctrl+C to stop.");

    // EventsAsync() yields forever. After a lost connection it reconnects,
    // logs in and subscribes again, then calls CatchUpAsync().
    await foreach (var incoming in dock.EventsAsync(cancellationToken: stop.Token))
    {
        if (incoming.Message is { } message)
        {
            Handle(message);
        }
    }
    return 0;
}
catch (OperationCanceledException)
{
    return 0;
}
catch (Exception error) when (error is SmsDockException or ArgumentException)
{
    Console.Error.WriteLine($"ListenIncoming: {error.Message}");
    return 1;
}

// Your hook. The message is a Message object of API.md.
static void OnSms(Message message)
{
    // Timestamps are milliseconds since the epoch; show local time.
    var received = message.Time.ToLocalTime();
    var sender = string.IsNullOrEmpty(message.Contact) ? message.Address : message.Contact;
    Console.WriteLine($"{received:HH:mm:ss}  {sender}: {message.Body}");
}

// Calls the hook once per message.
void Handle(Message message)
{
    if (message.Id is { } id && !seen.Add(id))
    {
        return;
    }
    newest = Math.Max(newest, message.Timestamp);
    OnSms(message);
}

// Reads what arrived while the connection was down. The phone does not send
// events again. The client calls this method after every reconnect, when the
// subscription is in force again.
async Task CatchUpAsync(SmsDockClient dock, CancellationToken cancellationToken)
{
    if (dock.ServerRestarted || dock.MissedEvents is not 0)
    {
        // Messages of the same millisecond come again; Handle() knows them.
        var missed = new List<Message>();
        var filter = new MessageFilter { Direction = "in", Since = newest };
        await foreach (var message in dock.EnumerateMessagesAsync(filter, cancellationToken: cancellationToken))
        {
            missed.Add(message);
        }
        missed.Reverse();
        foreach (var message in missed)
        {
            Handle(message);
        }
    }
}
