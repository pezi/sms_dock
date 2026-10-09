// Hook that answers incoming SMS.
//
// An SMS with the text STATUS gets the answer "All systems running.";
// everything else gets a short help text.
//
// An answering machine can end up in a loop, for example with another
// answering machine or with a service that answers every SMS. Three guards
// prevent that (see ReplyGuard.cs):
//
// 1. A number gets at most one answer in CooldownMinutes.
// 2. Senders that are not ordinary phone numbers get no answer: names such
//    as "A1" or "Post" and short codes such as 8810.
// 3. At most MaxRepliesPerHour answers are sent in total.
//
// Usage:
//
//     export SMSDOCK_PASSWORD='your password'
//     export SMSDOCK_FINGERPRINT='AB:CD:…'      # from the Server screen of the app
//     dotnet run --project examples/AutoReply -- wss://192.168.1.50:9443/ws
//
// Stop with Ctrl+C. Every answer is an SMS that your tariff may charge.

using AutoReply;
using SmsDock;

const int CooldownMinutes = 10;
const int MaxRepliesPerHour = 20;
const string Usage = """
    Hook that answers incoming SMS.

    An SMS with the text STATUS gets the answer "All systems running.";
    everything else gets a short help text.

    Usage:

        export SMSDOCK_PASSWORD='your password'
        export SMSDOCK_FINGERPRINT='AB:CD:…'      # from the Server screen of the app
        dotnet run --project examples/AutoReply -- wss://192.168.1.50:9443/ws

    Stop with Ctrl+C. Every answer is an SMS that your tariff may charge.
    """;

if (args.Length != 1)
{
    Console.WriteLine(Usage);
    return 2;
}

using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, pressed) =>
{
    pressed.Cancel = true;
    stop.Cancel();
};

try
{
    var guard = new ReplyGuard(CooldownMinutes * 60, MaxRepliesPerHour);
    var options = new SmsDockClientOptions { ReconnectAttempts = null };
    await using var dock = await SmsDockClient.ConnectAsync(args[0], options: options, cancellationToken: stop.Token);
    await dock.SubscribeAsync("sms.received", cancellationToken: stop.Token);
    Console.WriteLine("Answering SMS. Press Ctrl+C to stop.");

    // SMS that arrive while the connection is down get no answer: the phone
    // does not send events again, and a late answer helps nobody.
    // ListenIncoming shows how to read such messages.
    await foreach (var incoming in dock.EventsAsync(cancellationToken: stop.Token))
    {
        if (incoming.Message is not { } message)
        {
            continue;
        }
        var sender = message.Address;
        var refusal = guard.Refusal(sender);
        if (refusal is not null)
        {
            Console.WriteLine($"{sender}: no answer ({refusal})");
            continue;
        }
        try
        {
            // Answer on the SIM that received the message.
            var result = await dock.SendAsync(
                sender, ReplyGuard.AnswerFor(message.Body), message.Sim, cancellationToken: stop.Token);
            Console.WriteLine($"{sender}: answered, requestId {result.RequestId}");
        }
        catch (ApiException error)
        {
            Console.WriteLine($"{sender}: not answered, {error.Code}: {error.ServerMessage}");
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
    Console.Error.WriteLine($"AutoReply: {error.Message}");
    return 1;
}
