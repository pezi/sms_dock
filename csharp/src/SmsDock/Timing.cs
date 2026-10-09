using System.Diagnostics;

namespace SmsDock;

/// <summary>Time that only moves forward, and limits for waits.</summary>
internal static class Timing
{
    /// <summary>The longest wait a timer of .NET accepts.</summary>
    public static readonly TimeSpan Longest = TimeSpan.FromMilliseconds(int.MaxValue - 1);

    private static readonly long s_origin = Stopwatch.GetTimestamp();

    /// <summary>The time since the start of the process.</summary>
    public static TimeSpan Now => Stopwatch.GetElapsedTime(s_origin);

    /// <summary>Returns the moment that lies this far ahead.</summary>
    public static TimeSpan After(TimeSpan wait) => Now + Clamp(wait);

    /// <summary>Limits a wait to what timers accept; a negative wait becomes zero.</summary>
    public static TimeSpan Clamp(TimeSpan wait)
    {
        if (wait < TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }
        return wait > Longest ? Longest : wait;
    }
}
