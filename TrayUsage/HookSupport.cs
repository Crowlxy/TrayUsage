using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TrayUsage;

// Approval reminders need hooks: Claude Code does not log a permission prompt until it is answered, so the session
// logs cannot show one. The app therefore installs *command* hooks (opt-in, from the settings page) that run this exe
// with --hook; the exe drops a small event file and exits, and the running app shows it. By default the hooks only observe:
// they print nothing and exit 0, so no permission decision is ever made or influenced here.
//   PermissionRequest      fires when the permission dialog appears (immediate); shown as Approval.
//   Notification           elicitation_dialog (an MCP input form); shown as Question.
// A separate, opt-in mode (--answer, "answer from the tray") lets the tray reply: the PermissionRequest hook and a PreToolUse hook
// on AskUserQuestion then wait (see AskRelay) for the user's choice and print Claude Code's documented decision JSON. Anything
// the tray cannot answer (no app, a timeout, "answer in the terminal") prints nothing, so the terminal prompt proceeds as usual.
// Agent HUD (Apache-2.0, https://github.com/jazzenchen/agent-hud-open @ 8fb6eb8567d4638b858bda71e4290b406d29c2bc,
// docs/client-hooks.md) uses client hooks the same way, with a plan that preserves other entries; only that idea is
// reused, the code is new. See THIRD_PARTY_NOTICES.md.

/// <summary>The hook process: <c>TrayUsage.exe --hook claude</c>. Reads the event on stdin, never prints, always exits 0.</summary>
internal static class HookRelay
{
    public static void Run(string agent, bool answer = false)
    {
        try
        {
            using var input = Console.OpenStandardInput();
            using var reader = new StreamReader(input);
            var text = reader.ReadToEnd();
            if (text.Length is 0 or > 1_000_000) return;
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            if (answer && AskRelay.TryHandle(root, agent)) return;
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

/// <summary>What the tray offers for a question: one or more questions, each with a few options (multi-select and free text stay in the terminal).</summary>
internal sealed record AskOption(string Label, string? Description);
internal sealed record AskQuestion(string Question, string? Header, IReadOnlyList<AskOption> Options);

/// <summary>An approval or a question waiting in a hook process for the tray's answer. <see cref="Id"/> is also the name of its files.</summary>
internal sealed record AskRequest(string Id, bool IsQuestion, AgentKind Kind, string Project, string? Tool, string? Detail,
    IReadOnlyList<AskQuestion> Questions, SessionTarget Target);

/// <summary>The tray's answer: allow / deny an approval, answers (question text to chosen label), or "terminal" to leave it to the terminal.</summary>
internal sealed record AskReply(string Action, IReadOnlyDictionary<string, string>? Answers = null);

/// <summary>
/// The waiting half of the answering hooks. The hook writes <c>ask-&lt;id&gt;.req</c> and keeps touching it (a heartbeat the app uses to
/// notice a hook that has gone: Claude Code stopped it, or the prompt was answered in the terminal), then waits for
/// <c>ask-&lt;id&gt;.reply</c>. The extensions differ from the observe-only <c>*.json</c> events on purpose, so the two never meet.
/// It waits less than the timeout written into settings.json, so it ends by itself and prints nothing rather than being killed.
/// </summary>
internal static class AskRelay
{
    public const string AppMutex = @"Local\TrayUsage.SingleInstance";
    public static readonly TimeSpan Wait = TimeSpan.FromSeconds(50);
    private const int MaxQuestions = 4, MaxOptions = 4;

    /// <returns>True when the hook is done with this event (answered, or nothing the tray can do); false to fall back to the observe-only path.</returns>
    public static bool TryHandle(JsonElement root, string agent)
    {
        var eventName = Str(root, "hook_event_name");
        var tool = Str(root, "tool_name");
        var isQuestion = eventName == "PreToolUse" && tool == "AskUserQuestion";
        var isApproval = eventName == "PermissionRequest" && tool is not (null or "AskUserQuestion" or "ExitPlanMode"); // plan approval is not offered
        if (!isQuestion && !isApproval) return false;
        if (!root.TryGetProperty("tool_input", out var input) || input.ValueKind != JsonValueKind.Object) return false;

        var questions = isQuestion ? QuestionsOf(input) : null;
        if (isQuestion && questions is null) return true; // multi-select and the like: the terminal asks, the log notice tells
        if (!AppRunning()) return true; // nothing to answer with: the terminal prompt proceeds at once

        var id = Guid.NewGuid().ToString("N");
        var directory = HookInbox.Directory;
        Directory.CreateDirectory(directory);
        var request = Path.Combine(directory, $"ask-{id}.req");
        var reply = Path.Combine(directory, $"ask-{id}.reply");
        var node = new JsonObject
        {
            ["id"] = id,
            ["agent"] = agent,
            ["kind"] = isQuestion ? "question" : "approval",
            ["tool"] = tool,
            ["detail"] = isQuestion ? null : DetailOf(input),
            ["questions"] = questions,
            ["session"] = Str(root, "session_id"),
            ["cwd"] = Str(root, "cwd")
        };
        try
        {
            File.WriteAllText(request + ".tmp", node.ToJsonString());
            File.Move(request + ".tmp", request);
            var until = DateTime.UtcNow + Wait;
            while (DateTime.UtcNow < until)
            {
                if (File.Exists(reply))
                {
                    if (Decide(reply, input, isQuestion) is { } output) Print(output);
                    return true; // otherwise "terminal", or unreadable: print nothing
                }
                if (!AppRunning()) return true;
                File.SetLastWriteTimeUtc(request, DateTime.UtcNow);
                Thread.Sleep(120);
            }
            return true;
        }
        finally
        {
            foreach (var path in new[] { request, request + ".tmp", reply })
            {
                try { File.Delete(path); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
    }

    private static bool AppRunning()
    {
        if (!Mutex.TryOpenExisting(AppMutex, out var mutex)) return false;
        mutex.Dispose();
        return true;
    }

    /// <summary>The hook's stdout for the tray's reply, per Claude Code's hooks reference; null for "terminal" and anything unreadable.</summary>
    private static string? Decide(string replyPath, JsonElement input, bool isQuestion)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(replyPath));
            var action = Str(doc.RootElement, "action");
            if (isQuestion)
            {
                if (action != "answer" || !doc.RootElement.TryGetProperty("answers", out var answers) || answers.ValueKind != JsonValueKind.Object) return null;
                // The original questions are echoed back with the answers added, as the reference's example does.
                var updated = JsonNode.Parse(input.GetRawText()) as JsonObject ?? new JsonObject();
                updated["answers"] = JsonNode.Parse(answers.GetRawText());
                return new JsonObject
                {
                    ["hookSpecificOutput"] = new JsonObject
                    {
                        ["hookEventName"] = "PreToolUse",
                        ["permissionDecision"] = "allow",
                        ["updatedInput"] = updated
                    }
                }.ToJsonString();
            }
            if (action is not ("allow" or "deny")) return null;
            var decision = new JsonObject { ["behavior"] = action };
            if (action == "deny") decision["message"] = "Denied from the tray (TrayUsage).";
            return new JsonObject
            {
                ["hookSpecificOutput"] = new JsonObject { ["hookEventName"] = "PermissionRequest", ["decision"] = decision }
            }.ToJsonString();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void Print(string output)
    {
        using var stdout = Console.OpenStandardOutput();
        var bytes = System.Text.Encoding.UTF8.GetBytes(output);
        stdout.Write(bytes, 0, bytes.Length);
        stdout.Flush();
    }

    /// <summary>The questions in a form the tray can show; null when it cannot (multi-select, no options, too many).</summary>
    private static JsonArray? QuestionsOf(JsonElement input)
    {
        if (!input.TryGetProperty("questions", out var questions) || questions.ValueKind != JsonValueKind.Array) return null;
        var result = new JsonArray();
        foreach (var question in questions.EnumerateArray())
        {
            if (Str(question, "question") is not { Length: > 0 } text) return null;
            if (question.TryGetProperty("multiSelect", out var multi) && multi.ValueKind == JsonValueKind.True) return null;
            if (!question.TryGetProperty("options", out var options) || options.ValueKind != JsonValueKind.Array) return null;
            var list = new JsonArray();
            foreach (var option in options.EnumerateArray())
                if (Str(option, "label") is { Length: > 0 } label)
                    list.Add(new JsonObject { ["label"] = label, ["description"] = Str(option, "description") });
            if (list.Count is 0 or > MaxOptions) return null;
            result.Add(new JsonObject { ["question"] = text, ["header"] = Str(question, "header"), ["options"] = list });
        }
        return result.Count is 0 or > MaxQuestions ? null : result;
    }

    /// <summary>What the tool is about to do, in a few words: the command, the file, or the start of its input.</summary>
    private static string DetailOf(JsonElement input)
    {
        var text = Str(input, "command") ?? Str(input, "cmd") ?? Str(input, "file_path") ?? Str(input, "url") ?? Str(input, "pattern") ?? input.GetRawText();
        return text.Length > 600 ? text[..600] + "…" : text;
    }

    private static string? Str(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}

/// <summary>The app's side of <see cref="AskRelay"/>: the live requests, and the reply files.</summary>
internal static class AskInbox
{
    private static readonly TimeSpan Alive = TimeSpan.FromSeconds(3);

    public static bool IsValidId(string id) => id.Length == 32 && id.All(char.IsAsciiHexDigitLower);

    /// <summary>Requests whose hook is still waiting (its heartbeat is recent), oldest first.</summary>
    public static IReadOnlyList<AskRequest> Poll()
    {
        var list = new List<AskRequest>();
        if (!Directory.Exists(HookInbox.Directory)) return list;
        foreach (var file in Directory.EnumerateFiles(HookInbox.Directory, "ask-*.req").Order())
        {
            try
            {
                var age = DateTime.UtcNow - File.GetLastWriteTimeUtc(file);
                if (age > TimeSpan.FromMinutes(2)) File.Delete(file);
                if (age > Alive) continue;
                using var doc = JsonDocument.Parse(File.ReadAllText(file));
                if (Parse(doc.RootElement) is { } request) list.Add(request);
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
        }
        return list;
    }

    private static AskRequest? Parse(JsonElement root)
    {
        var id = Text(root, "id");
        if (id is null || !IsValidId(id)) return null;
        var cwd = Text(root, "cwd");
        var project = string.IsNullOrWhiteSpace(cwd) ? "Claude Code" : Path.GetFileName(cwd.TrimEnd('\\', '/'));
        var questions = new List<AskQuestion>();
        if (root.TryGetProperty("questions", out var array) && array.ValueKind == JsonValueKind.Array)
            foreach (var question in array.EnumerateArray())
            {
                var options = new List<AskOption>();
                if (question.TryGetProperty("options", out var raw) && raw.ValueKind == JsonValueKind.Array)
                    foreach (var option in raw.EnumerateArray())
                        if (Text(option, "label") is { } label) options.Add(new(label, Text(option, "description")));
                if (Text(question, "question") is { } text && options.Count > 0) questions.Add(new(text, Text(question, "header"), options));
            }
        var isQuestion = Text(root, "kind") == "question";
        if (isQuestion && questions.Count == 0) return null;
        return new(id, isQuestion, AgentKind.Claude, string.IsNullOrEmpty(project) ? "Claude Code" : project, Text(root, "tool"), Text(root, "detail"),
            questions, new(AgentKind.Claude, cwd, Text(root, "session")));
    }

    /// <summary>Hands the answer to the waiting hook.</summary>
    public static void Reply(string id, AskReply reply)
    {
        if (!IsValidId(id)) return; // a demo request has no hook behind it
        try
        {
            var node = new JsonObject { ["action"] = reply.Action };
            if (reply.Answers is { } answers)
                node["answers"] = new JsonObject(answers.Select(pair => new KeyValuePair<string, JsonNode?>(pair.Key, pair.Value)));
            var path = Path.Combine(HookInbox.Directory, $"ask-{id}.reply");
            File.WriteAllText(path + ".tmp", node.ToJsonString());
            File.Move(path + ".tmp", path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static string? Text(JsonElement element, string name) =>
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
    private const string Marker = "--hook claude", AnswerFlag = "--answer";
    private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true };

    private static string SettingsPath => Path.Combine(
        Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") is { Length: > 0 } dir
            ? dir : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude"),
        "settings.json");

    /// <summary>
    /// Claude Code runs hook commands through bash (Git Bash on Windows), so the path uses forward slashes. The
    /// exec form (command + args) is not used: Claude Code 2.1.312 ignores args (checked), so it would run the exe bare.
    /// </summary>
    private static string Command(bool answer) => $"\"{(Environment.ProcessPath ?? "TrayUsage.exe").Replace('\\', '/')}\" {Marker}{(answer ? " " + AnswerFlag : "")}";

    // An answering hook waits for the tray (up to AskRelay.Wait), so its timeout is longer than that and only a stuck one is killed.
    private static JsonObject Handler(bool answer) => new()
    {
        ["type"] = "command",
        ["command"] = Command(answer),
        ["timeout"] = answer ? (int)AskRelay.Wait.TotalSeconds + 10 : 5
    };

    /// <summary>The hooks of a mode: observe-only (PermissionRequest, Notification) or answering (PermissionRequest and PreToolUse on AskUserQuestion wait for the tray).</summary>
    private static (string Event, string? Matcher, bool Answering)[] Plan(bool answer) => answer
        ? [("PermissionRequest", null, true), ("PreToolUse", "AskUserQuestion", true), ("Notification", "elicitation_dialog", false)]
        : [("PermissionRequest", null, false), ("Notification", "elicitation_dialog", false)];

    public static bool IsInstalled() => Load(SettingsPath) is { } root && Ours(root).Any();

    /// <summary>True when the installed hooks are the answering ones.</summary>
    public static bool IsAnswerMode() => Load(SettingsPath) is { } root && IsAnswering(root);

    private static bool IsAnswering(JsonObject root) =>
        Ours(root).Any(entry => entry.Handler["command"]?.GetValue<string>() is { } command && command.Contains(AnswerFlag, StringComparison.Ordinal));

    /// <summary>Re-points installed hooks at the current exe and brings an older layout up to date. It keeps the mode that is installed: it never switches to answering.</summary>
    public static void RepairIfStale()
    {
        if (Load(SettingsPath) is not { } root) return;
        var ours = Ours(root).ToList();
        if (ours.Count == 0) return;
        var answer = IsAnswering(root);
        var expected = Plan(answer).Select(step => Handler(step.Answering).ToJsonString()).Order(StringComparer.Ordinal);
        if (!ours.Select(entry => entry.Handler.ToJsonString()).Order(StringComparer.Ordinal).SequenceEqual(expected)) Install(answer);
    }

    public static void Install(bool answer = false)
    {
        var path = SettingsPath;
        var root = Load(path) ?? (File.Exists(path) ? throw Unreadable() : new JsonObject());
        RemoveOurs(root);
        var hooks = root["hooks"] as JsonObject ?? (JsonObject)(root["hooks"] = new JsonObject());
        foreach (var (name, matcher, answering) in Plan(answer)) Add(hooks, name, matcher, answering);
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

    private static void Add(JsonObject hooks, string name, string? matcher, bool answering)
    {
        var list = hooks[name] as JsonArray ?? (JsonArray)(hooks[name] = new JsonArray());
        var group = new JsonObject();
        if (matcher is not null) group["matcher"] = matcher;
        group["hooks"] = new JsonArray(Handler(answering));
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

    private static readonly string[] Events = ["Notification", "PermissionRequest", "PreToolUse"];

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
