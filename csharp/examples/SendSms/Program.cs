// Send one SMS and wait for the result.
//
// Usage:
//
//     export SMSDOCK_PASSWORD='your password'
//     export SMSDOCK_FINGERPRINT='AB:CD:…'      # from the Server screen of the app
//     dotnet run --project examples/SendSms -- wss://192.168.1.50:9443/ws +4369912345678 "Hello"

using SmsDock;

const string Usage = """
    Send one SMS and wait for the result.

    Usage:

        export SMSDOCK_PASSWORD='your password'
        export SMSDOCK_FINGERPRINT='AB:CD:…'      # from the Server screen of the app
        dotnet run --project examples/SendSms -- wss://192.168.1.50:9443/ws +4369912345678 "Hello"
    """;

if (args.Length != 3)
{
    Console.WriteLine(Usage);
    return 2;
}

try
{
    OutboxEntry outcome;
    // Password and fingerprint come from the environment.
    await using (var dock = await SmsDockClient.ConnectAsync(args[0]))
    {
        SendResult result;
        try
        {
            // SendAsync() returns as soon as the phone has queued the message.
            // The client adds a random requestId, so a repeat after a lost
            // connection cannot send the message twice. Pass your own
            // requestId if the whole program may run again.
            result = await dock.SendAsync(args[1], args[2]);
        }
        catch (ApiException error)
        {
            // For example invalid_number, message_too_long, quota_exceeded.
            Console.WriteLine($"Not sent: {error.Code}: {error.ServerMessage}");
            return 1;
        }
        Console.WriteLine($"{result.Parts} part(s) for {result.To}");

        // Wait until the network accepted the message or refused it.
        outcome = await dock.WaitForResultAsync(result.RequestId, TimeSpan.FromSeconds(60));
    }

    if (outcome.Status == "failed")
    {
        Console.WriteLine($"Failed, error code {outcome.ErrorCode}");
        return 1;
    }
    Console.WriteLine($"Sent, message id {outcome.MessageId}");
    return 0;
}
catch (Exception error) when (error is SmsDockException or TimeoutException or ArgumentException)
{
    Console.Error.WriteLine($"SendSms: {error.Message}");
    return 1;
}
