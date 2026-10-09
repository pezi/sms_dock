using System.Globalization;
using System.Text.RegularExpressions;

namespace SmsDock.Cli;

/// <summary>Reads and writes times the way the Python tool does.</summary>
internal static partial class Timestamps
{
    /// <summary>
    /// Reads milliseconds since the epoch or an ISO 8601 date, which is local time unless it
    /// names an offset. Returns <c>null</c> for anything else.
    /// </summary>
    public static long? Parse(string text)
    {
        if (text.Length > 0 && text.All(char.IsDigit))
        {
            // Python reads the decimal digits of every script.
            var digits = string.Concat(text.Select(digit => (char)('0' + (int)char.GetNumericValue(digit))));
            return long.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var number)
                ? number
                : null;
        }
        var match = IsoDate().Match(text);
        if (!match.Success)
        {
            return null;
        }
        int Part(string name, int fallback = 0) =>
            match.Groups[name].Success ? int.Parse(match.Groups[name].Value, CultureInfo.InvariantCulture) : fallback;

        var hour = Part("hour", Part("bhour"));
        var minute = Part("minute", Part("bminute"));
        var second = Part("second", Part("bsecond"));
        var fraction = match.Groups["fraction"].Success ? match.Groups["fraction"] : match.Groups["bfraction"];
        var micros = fraction.Success
            ? long.Parse(fraction.Value.PadRight(6, '0')[..6], CultureInfo.InvariantCulture)
            : 0L;
        var nextDay = hour == 24 && minute == 0 && second == 0 && micros == 0;
        DateTime local;
        try
        {
            var date = match.Groups["week"].Success
                ? WeekDate(Part("year"), Part("week"), Part("wday", 1))
                : new DateTime(Part("year"), Part("month", Part("bmonth")), Part("day", Part("bday")));
            local = DateTime.SpecifyKind(
                date.AddDays(nextDay ? 1 : 0).Add(new TimeSpan(nextDay ? 0 : hour, minute, second)),
                DateTimeKind.Unspecified);
            if (hour > (nextDay ? 24 : 23) || minute > 59 || second > 59)
            {
                return null;
            }
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
        TimeSpan offset;
        if (match.Groups["zulu"].Success)
        {
            offset = TimeSpan.Zero;
        }
        else if (match.Groups["sign"].Success)
        {
            var offsetMinutes = Part("ominute");
            var offsetSeconds = Part("osecond");
            if (Part("ohour") > 23 || offsetMinutes > 59 || offsetSeconds > 59)
            {
                return null;
            }
            offset = new TimeSpan(Part("ohour"), offsetMinutes, offsetSeconds);
            if (match.Groups["sign"].Value == "-")
            {
                offset = -offset;
            }
        }
        else
        {
            // Python cannot turn the first and the last day it knows into local time.
            if (local < DateTime.MinValue.AddDays(1) || local > DateTime.MaxValue.AddDays(-1))
            {
                return null;
            }
            offset = LocalOffset(local);
        }
        var totalMicros = ((local.Ticks - DateTime.UnixEpoch.Ticks - offset.Ticks) / 10) + micros;
        // As in Python: seconds as a float, times 1000, cut off.
        var seconds = totalMicros / 1e6;
        return (long)(seconds * 1000);
    }

    /// <summary>Returns milliseconds since the epoch as local time, to the minute or the second.</summary>
    public static string Format(double milliseconds, bool seconds, string fallback)
    {
        try
        {
            var value = milliseconds / 1000;
            var whole = Math.Truncate(value);
            var micros = Math.Round((value - whole) * 1e6, MidpointRounding.ToEven);
            if (micros >= 1_000_000)
            {
                whole += 1;
            }
            else if (micros < 0)
            {
                whole -= 1;
            }
            if (double.IsNaN(whole) || Math.Abs(whole) > 1e15)
            {
                return fallback;
            }
            var moment = TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeSeconds((long)whole), TimeZoneInfo.Local);
            return moment.ToString(seconds ? "yyyy-MM-dd HH:mm:ss" : "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        }
        catch (ArgumentException)
        {
            return fallback;
        }
    }

    /// <summary>Returns the day of an ISO 8601 week date.</summary>
    private static DateTime WeekDate(int year, int week, int day)
    {
        if (day is < 1 or > 7 || week < 1 || week > ISOWeek.GetWeeksInYear(year))
        {
            throw new ArgumentOutOfRangeException(nameof(week));
        }
        return ISOWeek.ToDateTime(year, week, (DayOfWeek)(day % 7));
    }

    private static TimeSpan LocalOffset(DateTime local)
    {
        var zone = TimeZoneInfo.Local;
        if (zone.IsAmbiguousTime(local))
        {
            // The first of the two moments, as Python chooses it.
            return zone.GetAmbiguousTimeOffsets(local).Max();
        }
        return zone.GetUtcOffset(local);
    }

    [GeneratedRegex(
        @"\A(?<year>\d{4})"
        + @"(?:-(?<month>\d{2})-(?<day>\d{2})|(?<bmonth>\d{2})(?<bday>\d{2})|-?W(?<week>\d{2})(?:-?(?<wday>\d))?)"
        + @"(?:.(?:(?<hour>\d{2})(?::(?<minute>\d{2})(?::(?<second>\d{2})(?:[.,](?<fraction>\d+))?)?)?"
        + @"|(?<bhour>\d{2})(?<bminute>\d{2})(?:(?<bsecond>\d{2})(?:[.,](?<bfraction>\d+))?)?)"
        + @"(?:(?<zulu>Z)|(?<sign>[+-])(?<ohour>\d{2})(?::?(?<ominute>\d{2})(?::?(?<osecond>\d{2}))?)?)?)?\z",
        RegexOptions.Singleline)]
    private static partial Regex IsoDate();
}
