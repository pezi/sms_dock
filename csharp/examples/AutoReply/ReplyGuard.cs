using System.Diagnostics;
using System.Text.RegularExpressions;

namespace AutoReply;

/// <summary>Decides whether a sender may get an answer now.</summary>
public sealed partial class ReplyGuard(double cooldownSeconds, int repliesPerHour)
{
    private readonly Dictionary<string, double> _lastReply = [];
    private readonly Queue<double> _recent = new();

    /// <summary>Returns why this sender gets no answer, or <c>null</c> if it gets one.</summary>
    /// <param name="address">The sender of the SMS.</param>
    /// <param name="now">The time in seconds; the clock of the computer when <c>null</c>.</param>
    public string? Refusal(string address, double? now = null)
    {
        var time = now ?? Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
        var number = Ignored().Replace(address, "");
        if (!PhoneNumber().IsMatch(number))
        {
            return "not a phone number";
        }
        if (_lastReply.TryGetValue(number, out var last) && time - last < cooldownSeconds)
        {
            return "answered a short time ago";
        }
        while (_recent.Count > 0 && time - _recent.Peek() >= 3600)
        {
            _recent.Dequeue();
        }
        if (_recent.Count >= repliesPerHour)
        {
            return "too many answers in the last hour";
        }
        // Count the answer before it is sent: a failure must not open the
        // door for a loop.
        _lastReply[number] = time;
        _recent.Enqueue(time);
        return null;
    }

    /// <summary>Returns the answer to a message text.</summary>
    /// <param name="text">The text of the incoming SMS.</param>
    public static string AnswerFor(string text) =>
        text.Trim().ToUpperInvariant() == "STATUS" ? "All systems running." : "Commands: STATUS";

    [GeneratedRegex(@"[\s\-.()]")]
    private static partial Regex Ignored();

    // A phone number: optional +, then 7 to 15 digits. Shorter ones are short
    // codes; anything with letters is a sender name that cannot be answered.
    [GeneratedRegex(@"\A\+?[0-9]{7,15}\z")]
    private static partial Regex PhoneNumber();
}
