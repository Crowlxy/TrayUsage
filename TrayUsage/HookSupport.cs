using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TrayUsage;

// Approval reminders need hooks: Claude Code does not log a permission prompt until it is answered, so the session
// logs cannot show one. The app therefore installs *command* hooks (opt-in, from the settings page) that run this exe
// with --hook; the exe drops a small event file and exits, and the running app shows it. The hooks only observe: they
// print nothing and exit 0, so no permission decision is ever made or influenced here.
//   PermissionRequest      fires when the permission dialog appears (immediate); shown as Approval.
//   Notification           elicitation_dialog (an MCP input form); shown as Question.
// Agent HUD (Apache-2.0, https://github.com/jazzenchen/agent-hud-open @ 8fb6eb8567d4638b858bda71e4290b406d29c2bc,
// docs/client-hooks.md) uses client hooks the same way, with a plan that preserves other entries; only that idea is
// reused, the code is new. See THIRD_PARTY_NOTICES.md.

/// <summary>The hook process: <c>TrayUsage.exe --hook claude</c>. Reads the event on stdin, never prints, always exits 0.</summary>
internal static class HookRelay
{
    public static void Run(string agent)
    {
        try
        {
            using var input = Console.OpenStandardInput();
            using var reader = new StreamReader(input);
            var text = reader.ReadToEnd();
            if (text.Length is 0 or > 1_000_000) return;
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            var type = Str(root, "hook_event_name") == "PermissionRequest" ? "permission_prompt" : Str(root, "notification_type");
            if (type is not ("permission_prompt" or "elicitation_dialog")) return;
            var directory = HookInbox.Directory;
            Directory.CreateDirectory(directory);
            foreach (var old in Directory.EnumerateFiles(directory, "*.json"))
                if (DateTime.UtcNow - File.GetLastWriteTimeUtc(old) > TimeSpan.FromMinutes(5)) File.Delete(old);
            var node = new JsonObject
            {
                ["agent"] = agent,
                ["type"] = type,
                ["tool"] = Str(root, "tool_name"),
                ["session"] = Str(root, "session_id"),
                ["cwd"] = Str(root, "cwd"),
                ["at"] = DateTimeOffset.UtcNow.ToString("o")
            };
            var name = $"{DateTime.UtcNow.Ticks}-{Guid.NewGuid():N}"[..40];
            var temp = Path.Combine(directory, name + ".tmp");
            File.WriteAllText(temp, node.ToJsonString());
            File.Move(temp, Path.Combine(directory, name + ".json"));
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or InvalidOperationException)
        {
            // A hook must never get in Claude Code's way.
        }
    }

    private static string? Str(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}

internal sealed record HookEvent(AgentKind Kind, string Type, string Project, string? Tool, SessionTarget Target);

/// <summary>Event files written by <see cref="HookRelay"/>; each is read once and deleted.</summary>
internal static class HookInbox
{
    private static readonly Dictionary<string, DateTime> LastApproval = new();

    public static string Directory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TrayUsage", "events");

    public static IReadOnlyList<HookEvent> Poll()
    {
        var events = new List<HookEvent>();
        if (!System.IO.Directory.Exists(Directory)) return events;
        foreach (var file in System.IO.Directory.EnumerateFiles(Directory, "*.json").Order())
        {
            try
            {
                var stale = DateTime.UtcNow - File.GetLastWriteTimeUtc(file) > TimeSpan.FromSeconds(90);
                using var doc = JsonDocument.Parse(File.ReadAllText(file));
                File.Delete(file);
                if (stale) continue;
                var root = doc.RootElement;
                var cwd = Text(root, "cwd");
                var type = Text(root, "type") ?? "";
                var project = string.IsNullOrWhiteSpace(cwd) ? "Claude Code" : Path.GetFileName(cwd.TrimEnd('\\', '/'));
                if (string.IsNullOrEmpty(project)) project = "Claude Code";
                // A tool that asks several times in a row (or a legacy delayed Notification) is one reminder.
                if (type == "permission_prompt")
                {
                    if (LastApproval.TryGetValue(project, out var last) && DateTime.UtcNow - last < TimeSpan.FromSeconds(20)) continue;
                    LastApproval[project] = DateTime.UtcNow;
                }
                events.Add(new(AgentKind.Claude, type, project, Text(root, "tool"), new(AgentKind.Claude, cwd, Text(root, "session"))));
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
        }
        return events;
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}

/// <summary>
/// Adds and removes the app's own command hooks in Claude Code's user settings.json. Everything else in the file (other
/// hooks, permissions, unknown keys) is kept; a private backup is written once; an unreadable file is never touched.
/// </summary>
internal static class HookInstaller
{
    private const string Marker = "--hook claude";
    private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true };

    private static string SettingsPath => Path.Combine(
        Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") is { Length: > 0 } dir
            ? dir : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude"),
        "settings.json");

    /// <summary>
    /// Claude Code runs hook commands through bash (Git Bash on Windows), so the path uses forward slashes. The
    /// exec form (command + args) is not used: Claude Code 2.1.312 ignores args (checked), so it would run the exe bare.
    /// </summary>
    private static string Command => $"\"{(Environment.ProcessPath ?? "TrayUsage.exe").Replace('\\', '/')}\" {Marker}";

    private static JsonObject Handler() => new() { ["type"] = "command", ["command"] = Command, ["timeout"] = 5 };

    public static bool IsInstalled() => Load(SettingsPath) is { } root && Ours(root).Any();

    /// <summary>Re-points installed hooks at the current exe and brings an older layout up to date.</summary>
    public static void RepairIfStale()
    {
        if (Load(SettingsPath) is not { } root) return;
        var ours = Ours(root).ToList();
        var expected = Handler().ToJsonString();
        if (ours.Count > 0 && (ours.Count != 2 || ours.Any(entry => entry.Handler.ToJsonString() != expected))) Install();
    }

    public static void Install()
    {
        var path = SettingsPath;
        var root = Load(path) ?? (File.Exists(path) ? throw Unreadable() : new JsonObject());
        RemoveOurs(root);
        var hooks = root["hooks"] as JsonObject ?? (JsonObject)(root["hooks"] = new JsonObject());
        Add(hooks, "PermissionRequest", null);
        Add(hooks, "Notification", "elicitation_dialog");
        Save(path, root);
    }

    public static void Uninstall()
    {
        var path = SettingsPath;
        if (!File.Exists(path)) return;
        var root = Load(path) ?? throw Unreadable();
        RemoveOurs(root);
        Save(path, root);
    }

    private static void Add(JsonObject hooks, string name, string? matcher)
    {
        var list = hooks[name] as JsonArray ?? (JsonArray)(hooks[name] = new JsonArray());
        var group = new JsonObject();
        if (matcher is not null) group["matcher"] = matcher;
        group["hooks"] = new JsonArray(Handler());
        list.Add(group);
    }

    private static IOException Unreadable() => new("Claude Code の settings.json を読めないため、Hook を変更しませんでした。");

    private static JsonObject? Load(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            return JsonNode.Parse(File.ReadAllText(path), documentOptions: new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true
            }) as JsonObject;
        }
        catch (JsonException) { return null; }
    }

    private static readonly string[] Events = ["Notification", "PermissionRequest"];

    private static bool IsOurs(JsonObject handler) =>
        handler["command"]?.GetValue<string>() is { } command && command.Contains(Marker, StringComparison.Ordinal);

    /// <summary>The hook handlers this app installed, in any event it has ever used (identified by the command line).</summary>
    private static IEnumerable<(string Event, JsonObject Handler)> Ours(JsonObject root) =>
        Events.SelectMany(name => (root["hooks"]?[name] as JsonArray ?? [])
            .OfType<JsonObject>()
            .SelectMany(group => (group["hooks"] as JsonArray ?? []).OfType<JsonObject>())
            .Where(IsOurs)
            .Select(handler => (name, handler)));

    private static void RemoveOurs(JsonObject root)
    {
        if (root["hooks"] is not JsonObject hooks) return;
        foreach (var name in Events)
        {
            if (hooks[name] is not JsonArray list) continue;
            foreach (var group in list.OfType<JsonObject>().ToList())
            {
                if (group["hooks"] is not JsonArray handlers) continue;
                foreach (var handler in handlers.OfType<JsonObject>().ToList())
                    if (IsOurs(handler)) handlers.Remove(handler);
                if (handlers.Count == 0) list.Remove(group);
            }
            if (list.Count == 0) hooks.Remove(name);
        }
        if (hooks.Count == 0) root.Remove("hooks");
    }

    private static void Save(string path, JsonObject root)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var backup = path + ".trayusage.bak";
        if (File.Exists(path) && !File.Exists(backup))
        {
            File.Copy(path, backup);
            Protect(backup);
        }
        var temp = path + ".trayusage.tmp";
        File.WriteAllText(temp, root.ToJsonString(Pretty));
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>settings.json can hold tokens in its env block: the backup is readable by the current user only.</summary>
    private static void Protect(string path)
    {
        try
        {
            var file = new FileInfo(path);
            var security = file.GetAccessControl();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            security.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.FullControl, AccessControlType.Allow));
            file.SetAccessControl(security);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or PlatformNotSupportedException) { }
    }
}
