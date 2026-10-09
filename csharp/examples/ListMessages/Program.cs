// Print the last 10 incoming SMS.
//
// Usage:
//
//     export SMSDOCK_PASSWORD='your password'
//     export SMSDOCK_FINGERPRINT='AB:CD:…'      # from the Server screen of the app
//     dotnet run --project examples/ListMessages -- wss://192.168.1.50:9443/ws

using SmsDock;

const string Usage = """
    Print the last 10 incoming SMS.

    Usage:

        export SMSDOCK_PASSWORD='your password'
        export SMSDOCK_FINGERPRINT='AB:CD:…'      # from the Server screen of the app
        dotnet run --project examples/ListMessages -- wss://192.168.1.50:9443/ws
    """;

if (args.Length != 1)
{
    Console.WriteLine(Usage);
    return 2;
}

try
{
    Page<Message> page;
    // The password comes from SMSDOCK_PASSWORD, the fingerprint from
    // SMSDOCK_FINGERPRINT. It pins the self-signed certificate of the phone.
    await using (var dock = await SmsDockClient.ConnectAsync(args[0]))
    {
        page = await dock.MessagesAsync(new MessageFilter { Direction = "in" }, limit: 10);
    }

    foreach (var message in page.Items)
    {
        // Timestamps are milliseconds since the epoch; show local time.
        var received = message.Time.ToLocalTime();
        var sender = string.IsNullOrEmpty(message.Contact) ? message.Address : message.Contact;
        Console.WriteLine($"{received:yyyy-MM-dd HH:mm}  {sender}: {message.Body}");
    }

    // page.NextCursor leads to the next 10; see EnumerateMessagesAsync() for a
    // loop that follows it.
    return 0;
}
catch (Exception error) when (error is SmsDockException or ArgumentException)
{
    Console.Error.WriteLine($"ListMessages: {error.Message}");
    return 1;
}
