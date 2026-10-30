namespace TrayUsage;

// The one-sentence verdict under each provider on the usage page ("On track. Plenty of room until the weekly reset."), after the
// plain-language line Claude's own usage screen gives. The idea only; the rule is ours: project the use so far over the rest
// of the window and compare the projection with the limit.

internal enum PaceLevel { Unknown, Fine, Heavy, Fast, Exhausted }

internal sealed record Pace(PaceLevel Level, string Text);

internal static class UsagePace
{
    /// <summary>Projected use at the reset that counts as heavy (percent); at 100 the limit is on pace to be hit.</summary>
    private const double HeavyAt = 90;

    /// <summary>The window is too young to project from: below this share of it elapsed, only high use is flagged.</summary>
    private const double MinElapsed = 0.05;

    private static readonly TimeSpan FiveHours = TimeSpan.FromHours(5), Week = TimeSpan.FromDays(7);

    /// <summary>The verdict of whichever window looks worse (the 5-hour one when they tie), or null with no readings.</summary>
    public static Pace? Of(ProviderUsage? usage, DateTimeOffset now)
    {
        if (usage is null) return null;
        var pace = new[] { Judge(usage.FiveHour, FiveHours, "5-hour", now), Judge(usage.Weekly, Week, "weekly", now) }
            .MaxBy(p => p.Level) ?? new Pace(PaceLevel.Unknown, "");
        return pace.Level == PaceLevel.Unknown ? null : pace;
    }

    private static Pace Judge(UsageWindow? window, TimeSpan length, string label, DateTimeOffset now)
    {
        if (window is null) return new Pace(PaceLevel.Unknown, "");
        var used = window.UsedPercent;
        if (used >= 99.5)
            return new Pace(PaceLevel.Exhausted, window.ResetsAt is { } at
                ? $"Limit reached. The {label} window resets {ResetText(at.ToLocalTime(), now)}."
                : $"Limit reached in the {label} window.");
        if (window.ResetsAt is not { } resets) return new Pace(PaceLevel.Unknown, "");
        var remaining = resets - now;
        if (remaining <= TimeSpan.Zero || remaining > length) return new Pace(PaceLevel.Unknown, ""); // a stale or odd reading

        var elapsed = length - remaining;
        var share = elapsed / length;
        if (share < MinElapsed)
            return used >= HeavyAt
                ? new Pace(PaceLevel.Heavy, $"Heavy use. May run short before the {label} reset.")
                : new Pace(PaceLevel.Fine, $"On track. Plenty of room until the {label} reset.");

        var projected = used / share;
        if (projected >= 100 && used > 0)
        {
            var untilLimit = TimeSpan.FromSeconds((100 - used) / (used / elapsed.TotalSeconds));
            return new Pace(PaceLevel.Fast, $"Fast pace. At this rate the {label} limit comes in about {Span(untilLimit)}.");
        }
        return projected >= HeavyAt
            ? new Pace(PaceLevel.Heavy, $"Heavy use. May run short before the {label} reset.")
            : new Pace(PaceLevel.Fine, $"On track. Plenty of room until the {label} reset.");
    }

    private static string ResetText(DateTimeOffset local, DateTimeOffset now) =>
        local.Date == now.LocalDateTime.Date ? $"at {local:HH:mm}" : $"on {local:M/d HH:mm}";

    /// <summary>"2 h 10 m", "45 m", "1 d 3 h": the two largest units, rounded to the minute.</summary>
    private static string Span(TimeSpan span)
    {
        var minutes = (int)Math.Max(1, Math.Round(span.TotalMinutes));
        if (minutes >= 24 * 60) return $"{minutes / (24 * 60)} d {minutes % (24 * 60) / 60} h";
        if (minutes >= 60) return $"{minutes / 60} h {minutes % 60} m";
        return $"{minutes} m";
    }
}
