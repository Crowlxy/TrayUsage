using Microsoft.Win32;

namespace TrayUsage;

// Warning and Reset reminders follow the idea of Agent HUD's quota alerts (Apache-2.0,
// https://github.com/jazzenchen/agent-hud-open @ 8fb6eb8567d4638b858bda71e4290b406d29c2bc, docs/parity/c07-glow-alerts-theme.md):
// a reset is reported from what the provider reads after the window rolled over, not from a timer reaching zero.
// No code was copied. See THIRD_PARTY_NOTICES.md.

internal enum QuotaEvent { Warning, Exhausted, Reset }

/// <param name="Window">"5-hour" or "weekly".</param>
internal sealed record QuotaAlert(AgentKind Kind, QuotaEvent Event, string Window, double UsedPercent, double PreviousPercent, DateTimeOffset? ResetsAt);

/// <summary>
/// Turns successive usage readings into one-shot reminders: a warning when a window crosses 80 % and again at 95 %,
/// "used up" at 100 %, and "reset" when a later reading shows the window emptied. The last reading and the level
/// already announced are kept in HKCU so a restart does not repeat them.
/// </summary>
internal sealed class QuotaWatcher
{
    private const string KeyPath = @"Software\TrayUsage\Quota";
    private static readonly int[] Levels = [80, 95];
    private readonly Dictionary<string, State> _states = new();

    private sealed class State
    {
        public double? Last;
        public long? LastResetAt; // the window's reset time as last read (unix seconds)
        public int Announced; // highest warning level already shown for the current window
        public bool Exhausted;
        public bool Live; // seen a reading in this run, so a drop is a real reset and not a stale stored value
    }

    public IReadOnlyList<QuotaAlert> Observe(AgentKind kind, ProviderUsage usage)
    {
        var alerts = new List<QuotaAlert>();
        if (usage.Error is not null) return alerts; // keep the last good reading through failures
        Check(alerts, kind, "5-hour", usage.FiveHour);
        Check(alerts, kind, "weekly", usage.Weekly);
        return alerts;
    }

    private void Check(List<QuotaAlert> alerts, AgentKind kind, string window, UsageWindow? reading)
    {
        if (reading is null) return;
        var id = $"{kind}-{window}";
        var state = Load(id);
        var used = reading.UsedPercent;
        var previous = state.Last ?? used;

        // A reset: the window's reset time moved on to the next cycle (and use fell), or use fell sharply on its own.
        // A small dip is just a recount. Failed reads never get here (Observe skips them).
        var resetAt = reading.ResetsAt?.ToUnixTimeSeconds();
        var minAdvance = window == "5-hour" ? 30 * 60 : 24 * 3600;
        var cycleAdvanced = state.LastResetAt is { } lastReset && resetAt is { } nextReset && nextReset - lastReset >= minAdvance;
        if (state.Last is { } last && last >= 30 && ((cycleAdvanced && used < last) || used <= last - 25))
        {
            if (state.Live) alerts.Add(new(kind, QuotaEvent.Reset, window, used, last, reading.ResetsAt));
            state.Announced = 0;
            state.Exhausted = false;
        }
        else if (used < 70 && state.Announced > 0)
        {
            state.Announced = 0; // the window recovered without a visible drop
            state.Exhausted = false;
        }

        if (used >= 99.5)
        {
            if (!state.Exhausted)
            {
                state.Exhausted = true;
                state.Announced = Levels[^1];
                alerts.Add(new(kind, QuotaEvent.Exhausted, window, used, previous, reading.ResetsAt));
            }
        }
        else
        {
            foreach (var level in Levels.Reverse())
            {
                if (used < level || state.Announced >= level) continue;
                state.Announced = level;
                alerts.Add(new(kind, QuotaEvent.Warning, window, used, previous, reading.ResetsAt));
                break;
            }
        }
        state.Last = used;
        state.LastResetAt = resetAt ?? state.LastResetAt;
        state.Live = true;
        Save(id, state);
    }

    private State Load(string id)
    {
        if (_states.TryGetValue(id, out var cached)) return cached;
        var state = new State();
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(KeyPath);
            if (key?.GetValue(id + "-last") is int last && last >= 0) state.Last = last / 10.0;
            if (key?.GetValue(id + "-level") is int level) state.Announced = level;
            if (key?.GetValue(id + "-reset") is long resetAt) state.LastResetAt = resetAt;
            state.Exhausted = state.Announced >= Levels[^1] && state.Last >= 99.5;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
        return _states[id] = state;
    }

    private static void Save(string id, State state)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(KeyPath, true);
            key?.SetValue(id + "-last", (int)Math.Round((state.Last ?? 0) * 10), RegistryValueKind.DWord);
            key?.SetValue(id + "-level", state.Announced, RegistryValueKind.DWord);
            if (state.LastResetAt is { } resetAt) key?.SetValue(id + "-reset", resetAt, RegistryValueKind.QWord);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
    }
}
