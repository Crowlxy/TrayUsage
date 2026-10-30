using System.Globalization;

namespace TrayUsage;

/// <summary>
/// The 5-hour and weekly usage of both agents over the last days, kept in <c>%LOCALAPPDATA%\TrayUsage\history.csv</c> so the
/// chart survives a restart. A sample is taken at most every 90 s, and only from a reading that really arrived (a value carried
/// over through a failed poll is not a new measurement). One line per sample: unix seconds, then four percentages
/// (Claude 5 h, Claude week, Codex 5 h, Codex week), empty where the provider gave nothing.
/// Used from the UI thread only.
/// </summary>
internal sealed class UsageHistory
{
    private const int KeepDays = 8, RewriteAfter = 3000;
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(90);
    private readonly string _path;
    private readonly List<Sample> _samples = [];
    private int _appended;

    private readonly record struct Sample(long At, float Claude5h, float ClaudeWeek, float Codex5h, float CodexWeek)
    {
        public bool IsEmpty => float.IsNaN(Claude5h) && float.IsNaN(ClaudeWeek) && float.IsNaN(Codex5h) && float.IsNaN(CodexWeek);

        public float Of(AgentKind kind, bool weekly) => kind == AgentKind.Claude ? (weekly ? ClaudeWeek : Claude5h) : (weekly ? CodexWeek : Codex5h);
    }

    public UsageHistory(string? path = null)
    {
        _path = path ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TrayUsage", "history.csv");
        Load();
    }

    /// <summary>Changes whenever samples are added, so a drawn chart knows its data is stale.</summary>
    public int Version { get; private set; }

    public void Record(DateTimeOffset now, ProviderUsage claude, ProviderUsage codex)
    {
        var sample = new Sample(now.ToUnixTimeSeconds(), Read(claude, weekly: false), Read(claude, weekly: true), Read(codex, weekly: false), Read(codex, weekly: true));
        if (sample.IsEmpty) return;
        if (_samples.Count > 0 && sample.At - _samples[^1].At < Interval.TotalSeconds) return;
        _samples.Add(sample);
        Version++;
        if (++_appended >= RewriteAfter) Rewrite();
        else Write(append: true, [sample]);
    }

    /// <summary>The readings of one window since <paramref name="since"/>, oldest first (gaps in the data are left as gaps).</summary>
    public List<(long At, double Value)> Points(AgentKind kind, bool weekly, DateTimeOffset since)
    {
        var from = since.ToUnixTimeSeconds();
        var points = new List<(long, double)>();
        foreach (var sample in _samples)
        {
            var value = sample.Of(kind, weekly);
            if (sample.At >= from && !float.IsNaN(value)) points.Add((sample.At, value));
        }
        return points;
    }

    private static float Read(ProviderUsage usage, bool weekly)
    {
        if (usage.Error is not null) return float.NaN;
        return (weekly ? usage.Weekly : usage.FiveHour) is { } window ? (float)Math.Clamp(window.UsedPercent, 0, 100) : float.NaN;
    }

    private void Load()
    {
        var oldest = DateTimeOffset.UtcNow.AddDays(-KeepDays).ToUnixTimeSeconds();
        var dropped = false;
        try
        {
            if (!File.Exists(_path)) return;
            foreach (var line in File.ReadLines(_path))
            {
                if (!TryParse(line, out var sample)) { dropped = true; continue; }
                if (sample.At < oldest || (_samples.Count > 0 && sample.At <= _samples[^1].At)) { dropped = true; continue; }
                _samples.Add(sample);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return; }
        if (dropped) Rewrite();
    }

    private static bool TryParse(string line, out Sample sample)
    {
        sample = default;
        var parts = line.Split(',');
        if (parts.Length != 5 || !long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var at)) return false;
        float Value(string text) => float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && value is >= 0 and <= 100 ? value : float.NaN;
        sample = new Sample(at, Value(parts[1]), Value(parts[2]), Value(parts[3]), Value(parts[4]));
        return !sample.IsEmpty;
    }

    private static string Format(Sample sample)
    {
        static string Cell(float value) => float.IsNaN(value) ? "" : value.ToString("0.#", CultureInfo.InvariantCulture);
        return $"{sample.At},{Cell(sample.Claude5h)},{Cell(sample.ClaudeWeek)},{Cell(sample.Codex5h)},{Cell(sample.CodexWeek)}";
    }

    /// <summary>Drops what is older than the kept days and rewrites the file.</summary>
    private void Rewrite()
    {
        var oldest = DateTimeOffset.UtcNow.AddDays(-KeepDays).ToUnixTimeSeconds();
        _samples.RemoveAll(sample => sample.At < oldest);
        _appended = 0;
        Write(append: false, _samples);
    }

    private void Write(bool append, IReadOnlyCollection<Sample> samples)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var lines = samples.Select(Format).Select(line => line + "\n");
            if (append) File.AppendAllText(_path, string.Concat(lines));
            else File.WriteAllText(_path, string.Concat(lines));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } // the chart still works from memory
    }
}
