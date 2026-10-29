using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace TrayUsage;

// "Jump back": clicking a notice brings the window of the session it is about to the front, as Agent HUD's one-click
// return does (README, "Jump back"; Apache-2.0, https://github.com/jazzenchen/agent-hud-open @ 8fb6eb8567d4638b858bda71e4290b406d29c2bc).
// Only the behaviour is shared; the Windows lookup below (session file -> process -> owning window) is new.
// See THIRD_PARTY_NOTICES.md.

/// <summary>
/// Finds the terminal (or editor) window that hosts a Claude Code / Codex session and activates it. The session's
/// process comes from Claude Code's own <c>~/.claude/sessions/&lt;pid&gt;.json</c> (pid, session id, folder), or, for
/// Codex and as a fallback, from a running agent process whose current folder matches. The window is the nearest
/// ancestor process that owns one (Windows Terminal, VS Code, conhost); a window title carrying the folder name is
/// the last resort. A terminal with several tabs is brought forward as a whole: the tab cannot be chosen from outside.
/// </summary>
internal static class SessionJump
{
    public static bool Activate(SessionTarget target)
    {
        try
        {
            var window = FindWindow(target);
            if (window == IntPtr.Zero) return false;
            Raise(window);
            return true;
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static IntPtr FindWindow(SessionTarget target)
    {
        var windows = TopLevelWindows();
        var parents = ParentMap();
        foreach (var pid in CandidateProcesses(target))
        {
            var current = pid;
            for (var hop = 0; hop < 12 && current != 0; hop++)
            {
                if (windows.TryGetValue(current, out var found) && !IsShell(current)) return found;
                current = parents.TryGetValue(current, out var parent) && parent != current ? parent : 0;
            }
        }
        // Last resort: a window whose title names the folder (terminals show it in the tab or title bar).
        var folder = string.IsNullOrWhiteSpace(target.Cwd) ? null : Path.GetFileName(target.Cwd.TrimEnd('\\', '/'));
        if (folder is { Length: >= 3 })
            foreach (var (_, handle) in windows)
                if (Title(handle).Contains(folder, StringComparison.OrdinalIgnoreCase)) return handle;
        return IntPtr.Zero;
    }

    private static bool IsShell(int pid)
    {
        try { return Process.GetProcessById(pid).ProcessName.Equals("explorer", StringComparison.OrdinalIgnoreCase); }
        catch (ArgumentException) { return false; }
    }

    /// <summary>Process ids that may be the session's agent, best first.</summary>
    private static IEnumerable<int> CandidateProcesses(SessionTarget target)
    {
        var seen = new HashSet<int>();
        if (target.Kind == AgentKind.Claude)
            foreach (var pid in ClaudeSessionProcesses(target))
                if (Alive(pid) && seen.Add(pid)) yield return pid;
        var names = target.Kind == AgentKind.Claude ? new[] { "claude" } : new[] { "codex" };
        foreach (var name in names)
            foreach (var process in Process.GetProcessesByName(name))
            {
                using (process)
                    if (SameFolder(CurrentDirectory(process.Id), target.Cwd) && seen.Add(process.Id)) yield return process.Id;
            }
    }

    private static IEnumerable<int> ClaudeSessionProcesses(SessionTarget target)
    {
        var directory = Path.Combine(
            Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") is { Length: > 0 } dir
                ? dir : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude"), "sessions");
        if (!Directory.Exists(directory)) yield break;
        var matches = new List<(int Pid, bool ById, long Started)>();
        foreach (var file in Directory.EnumerateFiles(directory, "*.json"))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(file));
                var root = doc.RootElement;
                if (!root.TryGetProperty("pid", out var pidNode) || !pidNode.TryGetInt32(out var pid)) continue;
                var byId = target.SessionId is not null && root.TryGetProperty("sessionId", out var id) && id.GetString() == target.SessionId;
                var byFolder = root.TryGetProperty("cwd", out var cwd) && SameFolder(cwd.GetString(), target.Cwd);
                if (!byId && !byFolder) continue;
                var started = root.TryGetProperty("startedAt", out var at) && at.TryGetInt64(out var value) ? value : 0;
                matches.Add((pid, byId, started));
            }
            catch (Exception ex) when (ex is IOException or JsonException) { }
        }
        foreach (var match in matches.OrderByDescending(m => m.ById).ThenByDescending(m => m.Started)) yield return match.Pid;
    }

    private static bool Alive(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    private static bool SameFolder(string? a, string? b) =>
        !string.IsNullOrWhiteSpace(a) && !string.IsNullOrWhiteSpace(b) &&
        string.Equals(Path.TrimEndingDirectorySeparator(a), Path.TrimEndingDirectorySeparator(b), StringComparison.OrdinalIgnoreCase);

    // ---- window activation ----

    private static void Raise(IntPtr window)
    {
        if (IsIconic(window)) ShowWindow(window, 9); // SW_RESTORE
        // The click that started this was ours, so we may take the foreground; joining the current foreground thread
        // is the usual way to make Windows accept it reliably.
        var foreground = GetForegroundWindow();
        var foregroundThread = foreground == IntPtr.Zero ? 0 : GetWindowThreadProcessId(foreground, out _);
        var thisThread = GetCurrentThreadId();
        var attached = foregroundThread != 0 && foregroundThread != thisThread && AttachThreadInput(thisThread, foregroundThread, true);
        BringWindowToTop(window);
        SetForegroundWindow(window);
        if (attached) AttachThreadInput(thisThread, foregroundThread, false);
    }

    private static string Title(IntPtr window)
    {
        var length = GetWindowTextLength(window);
        if (length <= 0) return "";
        var text = new System.Text.StringBuilder(length + 1);
        GetWindowText(window, text, text.Capacity);
        return text.ToString();
    }

    /// <summary>First visible, titled, non-tool window of each process (our own excluded).</summary>
    private static Dictionary<int, IntPtr> TopLevelWindows()
    {
        var result = new Dictionary<int, IntPtr>();
        var self = Environment.ProcessId;
        EnumWindows((window, _) =>
        {
            if (!IsWindowVisible(window) || GetWindowTextLength(window) == 0) return true;
            if ((GetWindowLongPtr(window, -20).ToInt64() & 0x80) != 0) return true; // WS_EX_TOOLWINDOW
            GetWindowThreadProcessId(window, out var pid);
            if (pid != self) result.TryAdd((int)pid, window);
            return true;
        }, IntPtr.Zero);
        return result;
    }

    private static Dictionary<int, int> ParentMap()
    {
        var map = new Dictionary<int, int>();
        var snapshot = CreateToolhelp32Snapshot(2, 0); // TH32CS_SNAPPROCESS
        if (snapshot == new IntPtr(-1)) return map;
        try
        {
            var entry = new PROCESSENTRY32 { Size = (uint)Marshal.SizeOf<PROCESSENTRY32>() };
            for (var ok = Process32First(snapshot, ref entry); ok; ok = Process32Next(snapshot, ref entry))
                map[(int)entry.ProcessId] = (int)entry.ParentProcessId;
        }
        finally { CloseHandle(snapshot); }
        return map;
    }

    /// <summary>Another process's current folder, read from its PEB (same user, 64-bit); null when it cannot be read.</summary>
    private static string? CurrentDirectory(int pid)
    {
        var process = OpenProcess(0x0410, false, pid); // QUERY_INFORMATION | VM_READ
        if (process == IntPtr.Zero) return null;
        try
        {
            var info = new PROCESS_BASIC_INFORMATION();
            if (NtQueryInformationProcess(process, 0, ref info, Marshal.SizeOf<PROCESS_BASIC_INFORMATION>(), out _) != 0) return null;
            var pointer = new byte[8];
            if (!ReadProcessMemory(process, info.PebBaseAddress + 0x20, pointer, 8, out _)) return null; // PEB.ProcessParameters
            var parameters = (IntPtr)BitConverter.ToInt64(pointer);
            var unicode = new byte[16];
            if (!ReadProcessMemory(process, parameters + 0x38, unicode, 16, out _)) return null; // CurrentDirectory.DosPath
            var length = BitConverter.ToUInt16(unicode, 0);
            if (length == 0 || length > 2048) return null;
            var text = new byte[length];
            return ReadProcessMemory(process, (IntPtr)BitConverter.ToInt64(unicode, 8), text, length, out _)
                ? System.Text.Encoding.Unicode.GetString(text) : null;
        }
        finally { CloseHandle(process); }
    }

    private delegate bool EnumWindowsProc(IntPtr window, IntPtr parameter);

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_BASIC_INFORMATION
    {
        public IntPtr ExitStatus, PebBaseAddress, AffinityMask, BasePriority, UniqueProcessId, InheritedFromUniqueProcessId;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PROCESSENTRY32
    {
        public uint Size, Usage, ProcessId;
        public UIntPtr DefaultHeapId;
        public uint ModuleId, Threads, ParentProcessId;
        public int PriClassBase;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string ExeFile;
    }

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr window, int command);
    [DllImport("user32.dll")] private static extern bool BringWindowToTop(IntPtr window);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern int GetWindowTextLength(IntPtr window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr window, System.Text.StringBuilder text, int max);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr window, int index);
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint from, uint to, bool attach);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll")] private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll")] private static extern bool ReadProcessMemory(IntPtr process, IntPtr address, byte[] buffer, int size, out IntPtr read);
    [DllImport("kernel32.dll")] private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool Process32First(IntPtr snapshot, ref PROCESSENTRY32 entry);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool Process32Next(IntPtr snapshot, ref PROCESSENTRY32 entry);
    [DllImport("ntdll.dll")] private static extern int NtQueryInformationProcess(IntPtr process, int infoClass, ref PROCESS_BASIC_INFORMATION info, int size, out int returned);
}
