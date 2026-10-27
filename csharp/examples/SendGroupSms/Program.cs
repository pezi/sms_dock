// Send one text to several numbers, one SMS each, and wait for the results.
//
// Usage:
//
//     export SMSDOCK_PASSWORD='your password'
//     export SMSDOCK_FINGERPRINT='AB:CD:…'      # from the Server screen of the app
//     dotnet run --project examples/SendGroupSms -- wss://192.168.1.50:9443/ws "Hello all" \
//         +4369912345678 +436641112223

using SmsDock;

const string Usage = """
    Send one text to several numbers, one SMS each, and wait for the results.

    Usage:

        export SMSDOCK_PASSWORD='your password'
        export SMSDOCK_FINGERPRINT='AB:CD:…'      # from the Server screen of the app
        dotnet run --project examples/SendGroupSms -- wss://192.168.1.50:9443/ws "Hello all" \
            +4369912345678 +436641112223
    """;

if (args.Length < 3)
{
    Console.WriteLine(Usage);
    return 2;
}

try
{
    var failed = 0;
    // Password and fingerprint come from the environment.
    await using (var dock = await SmsDockClient.ConnectAsync(args[0]))
    {
        GroupSendResult result;
        try
        {
            // SendGroupAsync() returns as soon as the phone has queued one SMS
            // per recipient. The phone checks the whole group first: one bad
            // number refuses all of them, nothing is sent then. The client
            // adds a random requestId; the members get the ids requestId.1,
            // requestId.2 and so on.
            result = await dock.SendGroupAsync(args[2..], args[1]);
        }
        catch (ApiException error)
        {
            // For example invalid_number, message_too_long, quota_exceeded.
            Console.WriteLine($"Not sent: {error.Code}: {error.ServerMessage}");
            return 1;
        }
        Console.WriteLine($"{result.To.Count} recipient(s), {result.Parts} part(s) each");

        // Wait for every member; a member the phone refused at once is
        // already failed and needs no waiting.
        foreach (var member in result.Messages)
        {
            if (member.Status == "failed")
            {
                Console.WriteLine($"{member.To}: refused by the phone");
                failed++;
                continue;
            }
            var outcome = await dock.WaitForResultAsync(member.RequestId, TimeSpan.FromSeconds(60));
            if (outcome.Status == "failed")
            {
                Console.WriteLine($"{member.To}: failed, error code {outcome.ErrorCode}");
                failed++;
            }
            else
            {
                Console.WriteLine($"{member.To}: {outcome.Status}, message id {outcome.MessageId}");
            }
        }
    }

    return failed > 0 ? 1 : 0;
}
catch (Exception error) when (error is SmsDockException or TimeoutException or ArgumentException)
{
    Console.Error.WriteLine($"SendGroupSms: {error.Message}");
    return 1;
}
