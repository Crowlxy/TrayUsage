using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace TrayUsage;

internal sealed record TaskItem(Guid Id, string Text, bool Done, DateOnly? Due);

internal sealed record ClipEntry(string Text, DateTime At);

/// <summary>
/// What the panel's four pages (カレンダー, クリップボード, タスク, ファイル) show. Tasks and the file tray are kept in
/// <c>%LOCALAPPDATA%\TrayUsage\workspace.json</c>; the clipboard history lives in memory only, so what was copied (a password, say)
/// never reaches the disk. Tasks are typed into <see cref="InputPrompt"/> (the island takes no keyboard input), and files are added
/// by dropping them on the island or from the clipboard.
/// </summary>
internal sealed class Workspace
{
    public const int MaxClips = 20, MaxTasks = 60, MaxFiles = 20, MaxClipChars = 4000, MaxTaskChars = 200;
    private readonly string _path;

    private sealed record Saved(List<TaskItem>? Tasks, List<string>? Files);

    public Workspace(string? path = null)
    {
        _path = path ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TrayUsage", "workspace.json");
        try
        {
            if (File.Exists(_path) && JsonSerializer.Deserialize<Saved>(File.ReadAllText(_path)) is { } saved)
            {
                Tasks.AddRange((saved.Tasks ?? []).Where(task => !string.IsNullOrWhiteSpace(task.Text)).Take(MaxTasks));
                Files.AddRange((saved.Files ?? []).Take(MaxFiles));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
    }

    public List<TaskItem> Tasks { get; } = [];
    public List<ClipEntry> Clips { get; } = [];
    public List<string> Files { get; } = [];

    /// <summary>Something the panel shows changed; it redraws if it is open.</summary>
    public event Action? Changed;

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(new Saved(Tasks, Files)));
            File.Move(temp, _path, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        Changed?.Invoke();
    }

    // ---- clipboard ----

    /// <summary>A text was copied: it goes to the top of the history (an earlier copy of the same text moves up instead of repeating).</summary>
    public void AddClip(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        if (text.Length > MaxClipChars) text = text[..MaxClipChars];
        Clips.RemoveAll(clip => clip.Text == text);
        Clips.Insert(0, new ClipEntry(text, DateTime.Now));
        if (Clips.Count > MaxClips) Clips.RemoveRange(MaxClips, Clips.Count - MaxClips);
        Changed?.Invoke();
    }

    public void RemoveClip(int index)
    {
        if (index < 0 || index >= Clips.Count) return;
        Clips.RemoveAt(index);
        Changed?.Invoke();
    }

    public void ClearClips()
    {
        Clips.Clear();
        Changed?.Invoke();
    }

    /// <summary>Puts a history entry back on the clipboard; false when another program holds the clipboard.</summary>
    public bool CopyClip(int index)
    {
        if (index < 0 || index >= Clips.Count) return false;
        try
        {
            Clipboard.SetText(Clips[index].Text);
            return true;
        }
        catch (ExternalException)
        {
            return false;
        }
    }

    // ---- tasks ----

    public bool AddTask(string? text, DateOnly? due)
    {
        var line = (text ?? "").Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (string.IsNullOrEmpty(line) || Tasks.Count >= MaxTasks) return false;
        Tasks.Add(new TaskItem(Guid.NewGuid(), line.Length > MaxTaskChars ? line[..MaxTaskChars] : line, false, due));
        Save();
        return true;
    }

    public void ToggleTask(Guid id)
    {
        var at = Tasks.FindIndex(task => task.Id == id);
        if (at < 0) return;
        Tasks[at] = Tasks[at] with { Done = !Tasks[at].Done };
        Save();
    }

    public void DeleteTask(Guid id)
    {
        if (Tasks.RemoveAll(task => task.Id == id) > 0) Save();
    }

    public void ClearDoneTasks()
    {
        if (Tasks.RemoveAll(task => task.Done) > 0) Save();
    }

    // ---- file tray ----

    /// <returns>How many of the paths were new.</returns>
    public int AddFiles(IEnumerable<string> paths)
    {
        var added = 0;
        foreach (var path in paths)
        {
            if (!File.Exists(path) && !Directory.Exists(path)) continue;
            if (Files.Any(known => string.Equals(known, path, StringComparison.OrdinalIgnoreCase))) continue;
            Files.Insert(0, path);
            added++;
        }
        if (added == 0) return 0;
        if (Files.Count > MaxFiles) Files.RemoveRange(MaxFiles, Files.Count - MaxFiles);
        Save();
        return added;
    }

    public int AddFilesFromClipboard()
    {
        try
        {
            return Clipboard.ContainsFileDropList() ? AddFiles(Clipboard.GetFileDropList().Cast<string>().ToArray()) : 0;
        }
        catch (ExternalException)
        {
            return 0;
        }
    }

    public void RemoveFile(int index)
    {
        if (index < 0 || index >= Files.Count) return;
        Files.RemoveAt(index);
        Save();
    }

    public void ClearFiles()
    {
        if (Files.Count == 0) return;
        Files.Clear();
        Save();
    }

    /// <summary>Opens a file with its program, or a folder in Explorer.</summary>
    public static void Open(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or FileNotFoundException or InvalidOperationException) { }
    }
}

/// <summary>
/// Watches the clipboard (the system tells it when it changes, so it costs nothing in between) and keeps the texts in
/// <see cref="Workspace.Clips"/>. Copies that a password manager marks as private are left out.
/// </summary>
internal sealed class ClipboardWatcher : NativeWindow, IDisposable
{
    private const int WmClipboardUpdate = 0x031D;
    private readonly Workspace _workspace;
    private readonly System.Windows.Forms.Timer _retry = new() { Interval = 150 };
    private int _attempts;

    public ClipboardWatcher(Workspace workspace)
    {
        _workspace = workspace;
        CreateHandle(new CreateParams { Parent = new IntPtr(-3) }); // HWND_MESSAGE: no window, only messages
        AddClipboardFormatListener(Handle);
        _retry.Tick += (_, _) => Capture();
    }

    protected override void WndProc(ref Message message)
    {
        if (message.Msg == WmClipboardUpdate)
        {
            _attempts = 0;
            Capture();
        }
        base.WndProc(ref message);
    }

    /// <summary>The program that copied may still hold the clipboard for a moment; a few tries, then give up.</summary>
    private void Capture()
    {
        _retry.Stop();
        try
        {
            if (Clipboard.ContainsData("ExcludeClipboardContentFromMonitorProcessing") || !Clipboard.ContainsText()) return;
            _workspace.AddClip(Clipboard.GetText());
        }
        catch (ExternalException)
        {
            if (++_attempts < 4) _retry.Start();
        }
    }

    public void Dispose()
    {
        _retry.Dispose();
        if (Handle != IntPtr.Zero)
        {
            RemoveClipboardFormatListener(Handle);
            DestroyHandle();
        }
    }

    [DllImport("user32.dll", SetLastError = true)] private static extern bool AddClipboardFormatListener(IntPtr window);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool RemoveClipboardFormatListener(IntPtr window);
}
