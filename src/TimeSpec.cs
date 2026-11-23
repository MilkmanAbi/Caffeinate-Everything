// Time grammar for the extended options: durations, points in time, windows and repeat days.
//
//   DURATION  90s | 45m | 2h | 1h30m | 1.5h | 1d        (a bare number means seconds, like -t)
//   TIME      now | +DURATION | in DURATION | 18:30 | 7am | 7:30pm | noon | midnight
//             today 18:30 | tomorrow 07:00 | 2026-11-23 07:00 | 2026-11-23T07:00
//   WINDOW    TIME-TIME | TIME to TIME | TIME+DURATION | DURATION (= now for DURATION) | TIME (open-ended)
//   DAYS      daily | weekdays | weekends | mon,wed,fri (full names work too)
//
// A bare clock time means its next occurrence. A window whose end is earlier than its start crosses
// midnight. A clock-to-clock window that is already in progress starts now.

using System.Globalization;
using System.Text.RegularExpressions;

namespace Caffeinate;

/// The single source of "now" and the local time zone. Tests pin both with CAFFEINATE_NOW
/// ("yyyy-MM-ddTHH:mm:ss" local) and CAFFEINATE_TZ (a Windows time zone id).
internal static class Clock
{
    public static TimeZoneInfo Zone { get; } = LoadZone();
    private static readonly DateTime? Pinned = LoadPinned();
    private static readonly DateTime StartedUtc = DateTime.UtcNow;

    public static DateTime Now => Pinned is DateTime p
        ? p + (DateTime.UtcNow - StartedUtc)
        : TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, Zone);

    public static DateTime ToUtc(DateTime local)
    {
        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        if (Zone.IsInvalidTime(local)) local = SkipGap(local);
        return TimeZoneInfo.ConvertTimeToUtc(local, Zone);
    }

    public static DateTime ToLocal(DateTime utc) =>
        TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), Zone);

    /// Wall-clock arithmetic that respects DST: add real elapsed time, not calendar time.
    public static DateTime AddElapsed(DateTime local, TimeSpan span) => ToLocal(ToUtc(local) + span);

    /// A local time inside a spring-forward gap does not exist; move it to the first valid minute after.
    public static DateTime SkipGap(DateTime local)
    {
        var t = local;
        for (int i = 0; i < 24 * 60 && Zone.IsInvalidTime(t); i++) t = t.AddMinutes(1);
        return t;
    }

    private static TimeZoneInfo LoadZone()
    {
        string? id = Environment.GetEnvironmentVariable("CAFFEINATE_TZ");
        if (string.IsNullOrEmpty(id)) return TimeZoneInfo.Local;
        try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
        catch (TimeZoneNotFoundException) { return TimeZoneInfo.Local; }
    }

    private static DateTime? LoadPinned()
    {
        string? s = Environment.GetEnvironmentVariable("CAFFEINATE_NOW");
        return DateTime.TryParseExact(s, "yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var t) ? t : null;
    }
}

internal sealed class TimeSpecException(string message) : Exception(message);

internal enum PointKind { Now, Relative, Clock, Absolute }

/// A point in time as the user wrote it, resolved against "now" only when needed.
internal sealed record TimePoint(PointKind Kind, TimeSpan Relative = default, TimeOnly Time = default,
    DateTime Absolute = default, int DayOffset = -1, string Text = "")
{
    public bool IsClock => Kind == PointKind.Clock && DayOffset < 0;

    /// Next occurrence at or after <paramref name="from"/> (strictly after when <paramref name="strict"/>).
    public DateTime Resolve(DateTime from, bool strict = false)
    {
        switch (Kind)
        {
            case PointKind.Now: return from;
            case PointKind.Relative: return Clock.AddElapsed(from, Relative);
            case PointKind.Absolute: return Absolute;
            default:
                if (DayOffset >= 0) return Caffeinate.Clock.SkipGap(from.Date.AddDays(DayOffset) + Time.ToTimeSpan());
                var candidate = Caffeinate.Clock.SkipGap(from.Date + Time.ToTimeSpan());
                if (candidate < from || (strict && candidate == from))
                    candidate = Caffeinate.Clock.SkipGap(from.Date.AddDays(1) + Time.ToTimeSpan());
                return candidate;
        }
    }

    public override string ToString() => Text;
}

/// A window of time. End is null for an open-ended window ("--wake 07:00": just wake at 07:00).
internal sealed record TimeWindow(TimePoint Start, TimePoint? End, TimeSpan? Length, string Text)
{
    public bool IsRecurrable => Start.IsClock && (End is null || End.IsClock || Length is not null);

    /// Concrete start/end for the next (or current) occurrence.
    public (DateTime Start, DateTime? End, bool InProgress) Resolve(DateTime now)
    {
        // Clock-to-clock window that we are already inside of (today's, or last night's if it crosses midnight).
        if (Start.IsClock && (End?.IsClock == true || Length is not null))
        {
            foreach (int dayBack in new[] { 0, 1 })
            {
                var s = Clock.SkipGap(now.Date.AddDays(-dayBack) + Start.Time.ToTimeSpan());
                var e = EndAfter(s);
                if (s <= now && now < e) return (now, e, true);
            }
        }
        var start = Start.Resolve(now);
        return (start, End is null && Length is null ? null : EndAfter(start), false);
    }

    private DateTime EndAfter(DateTime start)
    {
        if (Length is TimeSpan len) return Clock.AddElapsed(start, len);
        var end = End!.Resolve(start, strict: true);
        if (end <= start) throw new TimeSpecException($"window '{Text}' ends before it starts");
        return end;
    }

    public override string ToString() => Text;
}

[Flags]
internal enum Days
{
    None = 0, Sun = 1, Mon = 2, Tue = 4, Wed = 8, Thu = 16, Fri = 32, Sat = 64,
    Weekdays = Mon | Tue | Wed | Thu | Fri, Weekends = Sat | Sun, All = Weekdays | Weekends,
}

internal static partial class TimeSpec
{
    [GeneratedRegex(@"^(?:(\d+(?:\.\d+)?)(d|h|m|s))+$", RegexOptions.IgnoreCase)]
    private static partial Regex DurationRx();

    [GeneratedRegex(@"(\d+(?:\.\d+)?)(d|h|m|s)", RegexOptions.IgnoreCase)]
    private static partial Regex DurationPartRx();

    [GeneratedRegex(@"^(\d{1,2})(?::(\d{2}))?(?::(\d{2}))?\s*(am|pm)?$", RegexOptions.IgnoreCase)]
    private static partial Regex ClockRx();

    public static TimeSpan ParseDuration(string text, bool allowBareSeconds = true)
    {
        string s = text.Trim();
        if (allowBareSeconds && long.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out long secs))
            return TimeSpan.FromSeconds(secs);
        if (!DurationRx().IsMatch(s))
            throw new TimeSpecException($"invalid duration '{text}' (examples: 90s, 45m, 2h, 1h30m, 1d)");
        double total = 0;
        foreach (Match m in DurationPartRx().Matches(s))
        {
            double n = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            total += char.ToLowerInvariant(m.Groups[2].Value[0]) switch
            {
                'd' => n * 86400, 'h' => n * 3600, 'm' => n * 60, _ => n,
            };
        }
        if (total <= 0) throw new TimeSpecException($"duration '{text}' must be greater than zero");
        if (total > TimeSpan.FromDays(3650).TotalSeconds) throw new TimeSpecException($"duration '{text}' is too long");
        return TimeSpan.FromSeconds(Math.Round(total));
    }

    public static bool LooksLikeDuration(string s) =>
        DurationRx().IsMatch(s.Trim()) && !ClockRx().IsMatch(s.Trim());

    public static TimePoint ParsePoint(string text)
    {
        string s = Regex.Replace(text.Trim(), @"\s+", " ");
        string lower = s.ToLowerInvariant();
        if (lower.Length == 0) throw new TimeSpecException("empty time");
        if (lower == "now") return new TimePoint(PointKind.Now, Text: s);
        if (lower.StartsWith('+')) return new TimePoint(PointKind.Relative, Relative: ParseDuration(lower[1..], false), Text: s);
        if (lower.StartsWith("in ")) return new TimePoint(PointKind.Relative, Relative: ParseDuration(lower[3..], false), Text: s);

        int dayOffset = -1;
        if (lower.StartsWith("today ")) { dayOffset = 0; lower = lower[6..]; }
        else if (lower.StartsWith("tomorrow ")) { dayOffset = 1; lower = lower[9..]; }

        if (TryParseClock(lower, out var clock))
            return new TimePoint(PointKind.Clock, Time: clock, DayOffset: dayOffset, Text: s);

        if (dayOffset < 0 && DateTime.TryParseExact(lower.Replace('t', ' '),
                ["yyyy-MM-dd HH:mm", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd H:mm"],
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var abs))
        {
            if (Clock.Zone.IsInvalidTime(abs)) abs = Clock.SkipGap(abs);
            return new TimePoint(PointKind.Absolute, Absolute: abs, Text: s);
        }

        throw new TimeSpecException(
            $"invalid time '{text}' (examples: 18:30, 7am, tomorrow 07:00, 2026-11-23 07:00, +2h, now)");
    }

    private static bool TryParseClock(string s, out TimeOnly clock)
    {
        clock = default;
        if (s == "noon") { clock = new TimeOnly(12, 0); return true; }
        if (s == "midnight") { clock = new TimeOnly(0, 0); return true; }
        var m = ClockRx().Match(s);
        if (!m.Success) return false;
        // A bare number with no colon and no am/pm is not a clock time ("7" is ambiguous).
        if (!m.Groups[2].Success && !m.Groups[4].Success) return false;
        int h = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        int min = m.Groups[2].Success ? int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) : 0;
        int sec = m.Groups[3].Success ? int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture) : 0;
        if (m.Groups[4].Success)
        {
            if (h < 1 || h > 12) return false;
            bool pm = m.Groups[4].Value.Equals("pm", StringComparison.OrdinalIgnoreCase);
            h = h % 12 + (pm ? 12 : 0);
        }
        if (h > 23 || min > 59 || sec > 59) return false;
        clock = new TimeOnly(h, min, sec);
        return true;
    }

    public static TimeWindow ParseWindow(string text)
    {
        string s = text.Trim();
        int to = s.IndexOf(" to ", StringComparison.OrdinalIgnoreCase);
        if (to > 0) return Build(s, ParsePoint(s[..to]), ParsePoint(s[(to + 4)..]), null);

        // "START+DURATION"
        int plus = s.LastIndexOf('+');
        if (plus > 0)
            return new TimeWindow(ParsePoint(s[..plus]), null, ParseDuration(s[(plus + 1)..], false), s);

        // "DURATION" alone: from now
        if (LooksLikeDuration(s))
            return new TimeWindow(new TimePoint(PointKind.Now, Text: "now"), null, ParseDuration(s, false), s);

        // "A-B" where the dash separates two times (dates also contain dashes, so try every split)
        for (int i = s.IndexOf('-'); i > 0; i = s.IndexOf('-', i + 1))
        {
            if (TryPoint(s[..i], out var a) && TryPoint(s[(i + 1)..], out var b)) return Build(s, a, b, null);
        }

        return new TimeWindow(ParsePoint(s), null, null, s); // open-ended
    }

    private static TimeWindow Build(string text, TimePoint a, TimePoint b, TimeSpan? len)
    {
        if (b.Kind == PointKind.Now) throw new TimeSpecException($"window '{text}' cannot end 'now'");
        if (b.Kind == PointKind.Relative)  // "18:00 to +2h" reads as 2h after the start
            return new TimeWindow(a, null, b.Relative, text);
        return new TimeWindow(a, b, len, text);
    }

    private static bool TryPoint(string s, out TimePoint p)
    {
        try { p = ParsePoint(s); return true; }
        catch (TimeSpecException) { p = null!; return false; }
    }

    public static Days ParseDays(string text)
    {
        var days = Days.None;
        foreach (string raw in text.ToLowerInvariant().Split([',', ' '], StringSplitOptions.RemoveEmptyEntries))
        {
            days |= raw switch
            {
                "daily" or "everyday" or "every-day" or "all" => Days.All,
                "weekdays" or "weekday" => Days.Weekdays,
                "weekends" or "weekend" => Days.Weekends,
                "sun" or "sunday" => Days.Sun,
                "mon" or "monday" => Days.Mon,
                "tue" or "tues" or "tuesday" => Days.Tue,
                "wed" or "wednesday" => Days.Wed,
                "thu" or "thur" or "thurs" or "thursday" => Days.Thu,
                "fri" or "friday" => Days.Fri,
                "sat" or "saturday" => Days.Sat,
                _ => throw new TimeSpecException(
                    $"invalid day '{raw}' (use daily, weekdays, weekends, or a list like mon,wed,fri)"),
            };
        }
        if (days == Days.None) throw new TimeSpecException("no days given");
        return days;
    }

    public static bool Includes(this Days days, DayOfWeek d) => (days & (Days)(1 << (int)d)) != 0;

    public static string Describe(this Days days) => days switch
    {
        Days.All => "daily",
        Days.Weekdays => "weekdays",
        Days.Weekends => "weekends",
        _ => string.Join(",", Enum.GetValues<DayOfWeek>().Where(d => days.Includes(d))
            .Select(d => d.ToString()[..3].ToLowerInvariant())),
    };

    public static string Format(DateTime local, DateTime now)
    {
        string time = local.ToString(local.Second == 0 ? "HH:mm" : "HH:mm:ss", CultureInfo.InvariantCulture);
        if (local.Date == now.Date) return $"today {time}";
        if (local.Date == now.Date.AddDays(1)) return $"tomorrow {time}";
        return local.ToString("ddd yyyy-MM-dd ", CultureInfo.InvariantCulture) + time;
    }

    public static string Format(TimeSpan span)
    {
        if (span.TotalSeconds < 60) return $"{(int)span.TotalSeconds}s";
        var parts = new List<string>();
        if (span.Days > 0) parts.Add($"{span.Days}d");
        if (span.Hours > 0) parts.Add($"{span.Hours}h");
        if (span.Minutes > 0) parts.Add($"{span.Minutes}m");
        return string.Join("", parts);
    }
}
