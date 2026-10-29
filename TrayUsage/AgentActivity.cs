using System.IO.Enumeration;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TrayUsage;

internal enum AgentKind { Claude, Codex }

/// <param name="Waiting">The turn is blocked on a question to the user (AskUserQuestion / request_user_input).</param>
internal sealed record AgentSession(AgentKind Kind, string Project, DateTimeOffset StartedAt, bool Waiting = false, SessionTarget? Target = null, SessionBrief? Brief = null);
internal sealed record CompletedTurn(AgentKind Kind, string Project, TimeSpan Duration, DateTimeOffset FinishedAt, SessionTarget? Target = null, SessionBrief? Brief = null);

/// <summary>What a session's current turn has been doing, as far as its log says: shown on a notice's detail card.</summary>
/// <param name="Prompt">The message that started the turn.</param>
/// <param name="Reply">The model's latest text (or the API error text of a failed turn).</param>
/// <param name="ContextTokens">Input tokens of the latest request (prompt + cache), i.e. how full the context is.</param>
/// <param name="Tools">Tool calls made this turn, most used first.</param>
/// <param name="Question">The question put to the user, with its <paramref name="Choices"/>; <paramref name="Plan"/> is a plan awaiting approval.</param>
internal sealed record SessionBrief(string? Prompt, string? Reply, string? Model, string? Branch, int ContextTokens,
    IReadOnlyList<(string Tool, int Count)> Tools, string? Question, IReadOnlyList<string> Choices, string? Plan);
/// <param name="ReadLarge">A first sight read a few MB; the caller may want to collect that garbage.</param>
/// <summary>Why a session wants a look: a question, a plan awaiting approval, an API error, or a usage limit hit.</summary>
internal enum AskReason { Question, Plan, Failed, Limited }
internal sealed record AskedQuestion(AgentKind Kind, string Project, DateTimeOffset At, SessionTarget? Target = null, AskReason Reason = AskReason.Question, SessionBrief? Brief = null);

/// <summary>Where a notice points: the session's folder and id, enough to find its terminal window.</summary>
internal sealed record SessionTarget(AgentKind Kind, string? Cwd, string? SessionId);
internal sealed record ActivitySnapshot(IReadOnlyList<AgentSession> Running, IReadOnlyList<CompletedTurn> Completed,
    IReadOnlyList<AskedQuestion> Asked, bool ReadLarge = false);

/// <summary>
/// Infers whether Claude Code / Codex are mid-turn by reading their local session logs
/// (~/.claude/projects/*/*.jsonl and ~/.codex/sessions/**/rollout-*.jsonl), so no hooks have to be installed.
/// Each file is read incrementally: the first sight seeds the state from the tail, and later scans read only the
/// bytes appended since, working on raw UTF-8 so no per-line strings are allocated.
/// Not thread-safe: call <see cref="Scan"/> from one thread at a time.
/// </summary>
internal sealed partial class ActivityMonitor
{
    // A session that has not written anything for this long is treated as gone, not as still running.
    private static readonly TimeSpan ActiveWindow = TimeSpan.FromMinutes(10);
    private const int SeedBytes = 3 * 1024 * 1024;
    private readonly Dictionary<string, Tracked> _tracked = new(StringComparer.OrdinalIgnoreCase);

    private sealed class Tracked(AgentKind kind)
    {
        public AgentKind Kind = kind;
        public long Length = -1;
        public long Offset;
        public DateTime LastWriteUtc;
        public TurnState? State;
        public bool Skip;
        public string? CodexProject;
        public string Path = "";
        public bool HeaderRead;
    }

    private sealed class TurnState
    {
        public bool Running;
        public DateTimeOffset Start, End;
        public string? Cwd;
        // A question the model put to the user and nobody has answered yet (tool_use id / call_id).
        public string? QuestionId;
        public bool JustAsked;
        public AskReason Reason;

        // What the turn has done so far (see SessionBrief). Cleared when a new turn starts; model and branch outlive turns.
        public string? Prompt, Reply, Model, Branch, Question, Plan;
        public int Context;
        public readonly Dictionary<string, int> Tools = new();
        public List<string> Choices = [];

        public void NewTurn()
        {
            Reply = Question = Plan = null;
            Tools.Clear();
            Choices = [];
        }

        public SessionBrief Snapshot() => new(Prompt, Reply, Model, Branch, Context,
            Tools.Where(t => t.Key is not ("AskUserQuestion" or "ExitPlanMode")) // those two are the question / plan themselves
                .OrderByDescending(t => t.Value).Take(5).Select(t => (t.Key, t.Value)).ToArray(), Question, Choices.ToArray(), Plan);
    }

    public ActivitySnapshot Scan()
    {
        var completed = new List<CompletedTurn>();
        var asked = new List<AskedQuestion>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var cutoff = DateTime.UtcNow - ActiveWindow;
        var readLarge = false;
        foreach (var file in Candidates(cutoff))
        {
            seen.Add(file.Path);
            if (!_tracked.TryGetValue(file.Path, out var tracked))
                _tracked[file.Path] = tracked = new Tracked(file.Kind) { Path = file.Path };
            if (tracked.Skip || (tracked.Length == file.Length && tracked.LastWriteUtc == file.LastWriteUtc)) continue;
            tracked.LastWriteUtc = file.LastWriteUtc;
            try
            {
                if (file.Kind == AgentKind.Codex && !tracked.HeaderRead && !ReadCodexHeader(file.Path, tracked))
                {
                    tracked.Skip = true;
                    continue;
                }
                readLarge |= Advance(file.Path, tracked, completed, asked);
                tracked.Length = file.Length;
            }
            catch (IOException) { tracked.Length = -1; } // being rewritten; retry on the next scan
            catch (UnauthorizedAccessException) { tracked.Length = -1; }
        }
        foreach (var gone in _tracked.Keys.Where(key => !seen.Contains(key)).ToList()) _tracked.Remove(gone);

        var running = _tracked.Values
            .Where(t => t is { Skip: false, State.Running: true })
            .Select(t => new AgentSession(t.Kind, ProjectName(t), t.State!.Start, t.State.QuestionId is not null, TargetOf(t), t.State.Snapshot()))
            .OrderBy(s => s.StartedAt)
            .ToList();
        return new(running, completed, asked, readLarge);
    }

    private readonly record struct Candidate(string Path, long Length, DateTime LastWriteUtc, AgentKind Kind);

    /// <summary>Recently written session logs only. Old files are filtered inside the enumeration, so they cost no allocations.</summary>
    private static IEnumerable<Candidate> Candidates(DateTime cutoffUtc)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var claude = Path.Combine(Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") is { Length: > 0 } c ? c : Path.Combine(home, ".claude"), "projects");
        var codex = Path.Combine(Environment.GetEnvironmentVariable("CODEX_HOME") is { Length: > 0 } x ? x : Path.Combine(home, ".codex"), "sessions");
        if (Directory.Exists(claude))
        {
            // Session logs sit one level down (projects/<project>/*.jsonl); deeper folders hold subagent logs and are skipped.
            var root = Path.TrimEndingDirectorySeparator(claude);
            var options = new EnumerationOptions { IgnoreInaccessible = true, RecurseSubdirectories = true };
            var logs = new FileSystemEnumerable<Candidate>(claude,
                (ref FileSystemEntry e) => new Candidate(e.ToFullPath(), e.Length, e.LastWriteTimeUtc.UtcDateTime, AgentKind.Claude), options)
            {
                ShouldRecursePredicate = (ref FileSystemEntry e) => Path.TrimEndingDirectorySeparator(e.Directory).SequenceEqual(root),
                ShouldIncludePredicate = (ref FileSystemEntry e) => !e.IsDirectory && e.LastWriteTimeUtc.UtcDateTime >= cutoffUtc &&
                    !Path.TrimEndingDirectorySeparator(e.Directory).SequenceEqual(root) && FileSystemName.MatchesSimpleExpression("*.jsonl", e.FileName)
            };
            foreach (var file in logs) yield return file;
        }
        if (Directory.Exists(codex))
        {
            var options = new EnumerationOptions { IgnoreInaccessible = true, RecurseSubdirectories = true };
            var logs = new FileSystemEnumerable<Candidate>(codex,
                (ref FileSystemEntry e) => new Candidate(e.ToFullPath(), e.Length, e.LastWriteTimeUtc.UtcDateTime, AgentKind.Codex), options)
            {
                ShouldIncludePredicate = (ref FileSystemEntry e) => !e.IsDirectory && e.LastWriteTimeUtc.UtcDateTime >= cutoffUtc &&
                    FileSystemName.MatchesSimpleExpression("rollout-*.jsonl", e.FileName)
            };
            foreach (var file in logs) yield return file;
        }
    }

    /// <summary>The session's folder and id: Claude logs are named by session id, Codex rollouts end with it.</summary>
    private static SessionTarget TargetOf(Tracked tracked)
    {
        var name = System.IO.Path.GetFileNameWithoutExtension(tracked.Path);
        var id = tracked.Kind == AgentKind.Claude ? name : name.Length >= 36 ? name[^36..] : null;
        return new(tracked.Kind, tracked.Kind == AgentKind.Codex ? tracked.CodexProject : tracked.State?.Cwd, id);
    }

    private static string ProjectName(Tracked tracked)
    {
        var cwd = tracked.Kind == AgentKind.Codex ? tracked.CodexProject : tracked.State?.Cwd;
        var name = string.IsNullOrWhiteSpace(cwd) ? null : Path.GetFileName(cwd.TrimEnd('\\', '/'));
        return string.IsNullOrEmpty(name) ? cwd ?? (tracked.Kind == AgentKind.Claude ? "Claude Code" : "Codex") : name;
    }

    /// <summary>
    /// Feeds the complete lines added since the last scan into the file's state machine. A first sight (or a file that
    /// shrank) starts over from the tail; turns that finish during a seed are history, not news, so they are not reported.
    /// </summary>
    private static bool Advance(string path, Tracked tracked, List<CompletedTurn> completed, List<AskedQuestion> asked)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var length = stream.Length;
        var seeding = tracked.State is null || length < tracked.Offset;
        if (seeding) tracked.State = new TurnState();
        var from = seeding ? Math.Max(0, length - SeedBytes) : tracked.Offset;
        if (length <= from)
        {
            tracked.Offset = from;
            return false;
        }
        var buffer = new byte[length - from];
        stream.Seek(from, SeekOrigin.Begin);
        stream.ReadExactly(buffer);

        var position = 0;
        if (seeding && from > 0)
        {
            var cut = buffer.AsSpan().IndexOf((byte)'\n'); // the first line is cut mid-way
            if (cut < 0)
            {
                tracked.Offset = length;
                return true;
            }
            position = cut + 1;
        }
        while (position < buffer.Length)
        {
            var newline = buffer.AsSpan(position).IndexOf((byte)'\n');
            if (newline < 0) break; // a line still being written stays unread until it is complete
            var line = new ReadOnlyMemory<byte>(buffer, position, newline);
            position += newline + 1;
            var done = tracked.Kind == AgentKind.Claude ? FeedClaude(line, tracked.State!) : FeedCodex(line, tracked.State!);
            if (done is { } finished && !seeding)
                completed.Add(new(tracked.Kind, ProjectName(tracked), finished.End - finished.Start, finished.End, TargetOf(tracked), tracked.State!.Snapshot()));
            if (tracked.State!.JustAsked)
            {
                tracked.State.JustAsked = false;
                if (!seeding) asked.Add(new(tracked.Kind, ProjectName(tracked), tracked.State.End, TargetOf(tracked), tracked.State.Reason, tracked.State.Snapshot()));
                tracked.State.Reason = AskReason.Question;
            }
        }
        tracked.Offset = from + position;
        return buffer.Length >= 1024 * 1024;
    }

    private readonly record struct Finished(DateTimeOffset Start, DateTimeOffset End);

    private static Finished? FeedClaude(ReadOnlyMemory<byte> line, TurnState state)
    {
        var span = line.Span;
        var isUser = span.IndexOf("\"type\":\"user\""u8) >= 0;
        if (!isUser && span.IndexOf("\"type\":\"assistant\""u8) < 0) return null;
        using var doc = TryParse(line);
        if (doc is null) return null;
        var root = doc.RootElement;
        var type = Str(root, "type");
        if (type is not ("user" or "assistant") || Bool(root, "isSidechain")) return null;
        if (Str(root, "cwd") is { } cwd) state.Cwd = cwd;
        if (Str(root, "gitBranch") is { Length: > 0 } branch) state.Branch = branch;
        var time = Time(root, "timestamp");

        if (type == "assistant")
        {
            if (!state.Running) state.NewTurn(); // a record that arrives with no turn open (a seed starting mid-turn) opens one
            ReadAssistant(root, state);
            if ((span.IndexOf("\"name\":\"AskUserQuestion\""u8) >= 0 || span.IndexOf("\"name\":\"ExitPlanMode\""u8) >= 0) && FindQuestion(root) is { } pending)
            {
                state.QuestionId = pending.Id;
                state.Reason = pending.Plan ? AskReason.Plan : AskReason.Question;
                state.JustAsked = true;
            }
            if (span.IndexOf("\"isApiErrorMessage\":true"u8) >= 0)
            {
                // A failed turn (an API error, or the usage limit) is not a completion: it is reported as its own notice.
                var wasRunning = state.Running;
                End(state, time);
                state.Reply = Clean(ApiMessage(root) ?? Str(root, "error"), 240);
                if (wasRunning)
                {
                    state.Reason = ApiErrorText(root).Contains("limit", StringComparison.OrdinalIgnoreCase) ? AskReason.Limited : AskReason.Failed;
                    state.JustAsked = true;
                }
                return null;
            }
            var stop = root.TryGetProperty("message", out var message) ? Str(message, "stop_reason") : null;
            if (stop is null or "tool_use" or "pause_turn")
            {
                Begin(state, time, newTurn: false);
                return null;
            }
            return End(state, time);
        }
        switch (ClassifyClaudeUser(root, out var promptText))
        {
            case UserRecord.ToolResult:
                if (state.QuestionId is not null && AnswersQuestion(root, state.QuestionId)) state.QuestionId = null;
                Begin(state, time, newTurn: false);
                return null;
            case UserRecord.Interrupt:
                state.Running = false;
                state.QuestionId = null;
                state.End = time;
                return null; // an interrupted turn is not a completion
            case UserRecord.Prompt:
                // A message typed while the model is still working joins that turn; only a turn left open for a long time
                // (a session that died mid-turn) starts over, as does one whose question the user answered by typing.
                var stale = state.Running && time - state.End > TimeSpan.FromMinutes(10);
                if (!state.Running || stale || state.QuestionId is not null)
                {
                    state.NewTurn();
                    state.Prompt = Clean(promptText, 200);
                }
                state.QuestionId = null;
                Begin(state, time, newTurn: stale);
                return null;
        }
        return null;
    }

    /// <summary>Collapses whitespace and shortens to <paramref name="max"/> characters, for a line or two on the detail card.</summary>
    private static string? Clean(string? text, int max)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        text = WhiteSpace().Replace(text, " ").Trim();
        return text.Length > max ? text[..(max - 1)] + "…" : text;
    }

    [GeneratedRegex(@"\s+")] private static partial Regex WhiteSpace();

    /// <summary>Model, branch-independent facts of an assistant record: context size, text, tool calls, question and plan.</summary>
    private static void ReadAssistant(JsonElement root, TurnState state)
    {
        if (!root.TryGetProperty("message", out var message)) return;
        if (Str(message, "model") is { Length: > 0 } model && model != "<synthetic>") state.Model = model;
        if (message.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
        {
            var tokens = Int(usage, "input_tokens") + Int(usage, "cache_read_input_tokens") + Int(usage, "cache_creation_input_tokens");
            if (tokens > 0) state.Context = tokens;
        }
        if (!message.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array) return;
        foreach (var block in content.EnumerateArray())
            switch (Str(block, "type"))
            {
                case "text":
                    if (Clean(Str(block, "text"), 400) is { } text) state.Reply = text;
                    break;
                case "tool_use":
                    var name = Str(block, "name");
                    if (name is null) break;
                    state.Tools[name] = state.Tools.GetValueOrDefault(name) + 1;
                    if (!block.TryGetProperty("input", out var input) || input.ValueKind != JsonValueKind.Object) break;
                    if (name == "ExitPlanMode") state.Plan = Clean(Str(input, "plan")?.Replace("\n", " / "), 400);
                    else if (name == "AskUserQuestion" && input.TryGetProperty("questions", out var questions) && questions.ValueKind == JsonValueKind.Array)
                        foreach (var question in questions.EnumerateArray())
                        {
                            state.Question = Clean(Str(question, "question"), 300);
                            state.Choices = [];
                            if (question.TryGetProperty("options", out var options) && options.ValueKind == JsonValueKind.Array)
                                foreach (var option in options.EnumerateArray().Take(4))
                                    if (Clean(Str(option, "label"), 40) is { } label) state.Choices.Add(label);
                            break;
                        }
                    break;
            }
    }

    private static int Int(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : 0;

    /// <summary>The id of an AskUserQuestion or ExitPlanMode (a plan awaiting approval) tool_use block in an assistant record.</summary>
    private static (string Id, bool Plan)? FindQuestion(JsonElement root)
    {
        if (!root.TryGetProperty("message", out var message) || !message.TryGetProperty("content", out var content) ||
            content.ValueKind != JsonValueKind.Array) return null;
        foreach (var block in content.EnumerateArray())
            if (Str(block, "type") == "tool_use" && Str(block, "name") is "AskUserQuestion" or "ExitPlanMode" && Str(block, "id") is { } id)
                return (id, Str(block, "name") == "ExitPlanMode");
        return null;
    }

    /// <summary>The text blocks of an error record (what the CLI prints), without the error code.</summary>
    private static string? ApiMessage(JsonElement root)
    {
        if (!root.TryGetProperty("message", out var message) || !message.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array) return null;
        var text = string.Join(" ", content.EnumerateArray().Select(block => Str(block, "text")).Where(t => !string.IsNullOrWhiteSpace(t)));
        return text.Length > 0 ? text : null;
    }

    private static string ApiErrorText(JsonElement root)
    {
        var text = Str(root, "error") ?? "";
        if (root.TryGetProperty("message", out var message) && message.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
            foreach (var block in content.EnumerateArray())
                if (Str(block, "text") is { } t) text += " " + t;
        return text;
    }

    private static bool AnswersQuestion(JsonElement root, string id)
    {
        if (!root.TryGetProperty("message", out var message) || !message.TryGetProperty("content", out var content) ||
            content.ValueKind != JsonValueKind.Array) return false;
        foreach (var block in content.EnumerateArray())
            if (Str(block, "type") == "tool_result" && Str(block, "tool_use_id") == id) return true;
        return false;
    }

    private static void Begin(TurnState state, DateTimeOffset time, bool newTurn)
    {
        // Mid-turn records seen without their prompt (a seed that starts inside a turn) date the turn from the first one.
        if (newTurn || !state.Running) state.Start = time;
        state.Running = true;
        state.End = time;
    }

    private static Finished? End(TurnState state, DateTimeOffset time)
    {
        state.QuestionId = null;
        var finished = state.Running ? new Finished(state.Start, time) : (Finished?)null;
        state.Running = false;
        state.End = time;
        return finished;
    }

    private enum UserRecord { Ignorable, ToolResult, Interrupt, Prompt }

    private static UserRecord ClassifyClaudeUser(JsonElement root, out string? promptText)
    {
        promptText = null;
        if (!root.TryGetProperty("message", out var message) || !message.TryGetProperty("content", out var content))
            return UserRecord.Ignorable;
        var toolResult = false;
        string? text = null;
        if (content.ValueKind == JsonValueKind.String) text = content.GetString();
        else if (content.ValueKind == JsonValueKind.Array)
            foreach (var block in content.EnumerateArray())
            {
                var blockType = Str(block, "type");
                if (blockType == "tool_result") toolResult = true;
                else if (blockType == "text")
                {
                    var blockText = Str(block, "text");
                    if (blockText?.StartsWith("[Request interrupted", StringComparison.Ordinal) == true) return UserRecord.Interrupt;
                    text ??= blockText;
                }
            }
        if (toolResult) return UserRecord.ToolResult;
        if (string.IsNullOrWhiteSpace(text)) return UserRecord.Ignorable;
        if (text.StartsWith("[Request interrupted", StringComparison.Ordinal)) return UserRecord.Interrupt;
        // Local slash-command bookkeeping (/clear, /model, ...) is not a model turn.
        return text.StartsWith("<command-", StringComparison.Ordinal) || text.StartsWith("<local-command", StringComparison.Ordinal) ||
            text.StartsWith("Caveat:", StringComparison.Ordinal) ? UserRecord.Ignorable : Prompt(text, out promptText);
    }

    private static UserRecord Prompt(string text, out string? promptText)
    {
        promptText = text;
        return UserRecord.Prompt;
    }

    /// <summary>Codex rollout lines that say what the turn is doing: tool calls, the model's text, the user's message, model and token use.</summary>
    private static void ReadCodexBrief(ReadOnlyMemory<byte> line, TurnState state)
    {
        var span = line.Span;
        var call = span.IndexOf("\"type\":\"function_call\""u8) >= 0;
        var message = span.IndexOf("\"type\":\"message\""u8) >= 0;
        var context = span.IndexOf("\"type\":\"turn_context\""u8) >= 0;
        var tokens = span.IndexOf("\"type\":\"token_count\""u8) >= 0;
        if (!call && !message && !context && !tokens) return;
        using var doc = TryParse(line);
        if (doc is null || !doc.RootElement.TryGetProperty("payload", out var payload)) return;
        if (call)
        {
            if (Str(payload, "name") is { Length: > 0 } name) state.Tools[name] = state.Tools.GetValueOrDefault(name) + 1;
        }
        else if (context)
        {
            if (Str(payload, "model") is { Length: > 0 } model) state.Model = model;
        }
        else if (tokens)
        {
            if (payload.TryGetProperty("info", out var info) && info.ValueKind == JsonValueKind.Object &&
                info.TryGetProperty("last_token_usage", out var last) && last.ValueKind == JsonValueKind.Object && Int(last, "input_tokens") is > 0 and var input)
                state.Context = input;
        }
        else if (Str(payload, "type") == "message" && Str(payload, "role") is ("assistant" or "user") and var role &&
            payload.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
        {
            foreach (var block in content.EnumerateArray())
            {
                if (Clean(Str(block, "text"), role == "user" ? 200 : 400) is not { } text) continue;
                if (role == "assistant") state.Reply = text;
                else if (!text.StartsWith('#') && !text.StartsWith('<')) state.Prompt = text; // instructions the CLI injects are not the user's message
                break;
            }
        }
    }

    private static Finished? FeedCodex(ReadOnlyMemory<byte> line, TurnState state)
    {
        var span = line.Span;
        ReadCodexBrief(line, state);
        if (span.IndexOf("\"type\":\"function_call\""u8) >= 0 && span.IndexOf("\"name\":\"request_user_input"u8) >= 0)
        {
            using var call = TryParse(line);
            if (call is not null && call.RootElement.TryGetProperty("payload", out var callPayload) && Str(callPayload, "call_id") is { } callId)
            {
                state.QuestionId = callId;
                state.JustAsked = true;
                state.End = Time(call.RootElement, "timestamp");
            }
            return null;
        }
        if (state.QuestionId is not null && span.IndexOf("\"type\":\"function_call_output\""u8) >= 0 &&
            span.IndexOf(Encoding.UTF8.GetBytes($"\"call_id\":\"{state.QuestionId}\"")) >= 0)
        {
            state.QuestionId = null;
            return null;
        }
        var started = span.IndexOf("\"type\":\"task_started\""u8) >= 0;
        var finished = !started && span.IndexOf("\"type\":\"task_complete\""u8) >= 0;
        var aborted = !started && !finished && span.IndexOf("\"type\":\"turn_aborted\""u8) >= 0;
        if (!started && !finished && !aborted) return null;
        using var doc = TryParse(line);
        if (doc is null) return null;
        var root = doc.RootElement;
        if (Str(root, "type") != "event_msg" || !root.TryGetProperty("payload", out var payload)) return null;
        var time = Time(root, "timestamp");
        switch (Str(payload, "type"))
        {
            case "task_started":
                state.QuestionId = null;
                state.NewTurn(); // the user's message is logged before task_started, so the prompt stays
                Begin(state, time, newTurn: true);
                return null;
            case "task_complete":
                var begin = payload.TryGetProperty("started_at", out var at) && at.ValueKind == JsonValueKind.Number && at.TryGetInt64(out var seconds)
                    ? DateTimeOffset.FromUnixTimeSeconds(seconds) : state.Start;
                var result = state.Running ? new Finished(begin, time) : (Finished?)null;
                state.QuestionId = null;
                state.Running = false;
                state.End = time;
                return result;
            case "turn_aborted":
                state.QuestionId = null;
                state.Running = false;
                state.End = time;
                return null;
        }
        return null;
    }

    /// <summary>Codex also logs helper threads (guardian reviews, sub-agents); only user-facing threads are tracked.</summary>
    private static bool ReadCodexHeader(string path, Tracked tracked)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var buffer = new byte[Math.Min(8192, stream.Length)];
        stream.ReadExactly(buffer);
        var head = Encoding.UTF8.GetString(buffer);
        tracked.HeaderRead = true;
        if (CodexThreadSource().Match(head) is { Success: true } source && source.Groups[1].Value != "user") return false;
        if (CodexCwd().Match(head) is { Success: true } cwd)
            tracked.CodexProject = cwd.Groups[1].Value.Replace(@"\\", @"\");
        return true;
    }

    [GeneratedRegex("\"thread_source\":\"([^\"]*)\"")] private static partial Regex CodexThreadSource();
    [GeneratedRegex("\"cwd\":\"((?:[^\"\\\\]|\\\\.)*)\"")] private static partial Regex CodexCwd();

    private static JsonDocument? TryParse(ReadOnlyMemory<byte> line)
    {
        try { return JsonDocument.Parse(line); }
        catch (JsonException) { return null; } // a line still being written
    }

    private static string? Str(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;

    private static bool Bool(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    private static DateTimeOffset Time(JsonElement element, string name) =>
        Str(element, name) is { } text && DateTimeOffset.TryParse(text, out var time) ? time : DateTimeOffset.UtcNow;
}
