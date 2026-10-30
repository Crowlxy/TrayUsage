using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Win32;

namespace TrayUsage;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        // The hook process Claude Code runs: drop an event file and leave, without starting the UI.
        if (args.Length >= 2 && args[0] == "--hook")
        {
            HookRelay.Run(args[1], args.Contains("--answer"));
            return;
        }
        // One tray app only (also keeps a hook started without its arguments from opening a second one).
        using var single = new Mutex(true, @"Local\TrayUsage.SingleInstance", out var first);
        if (!first) return;
        ApplicationConfiguration.Initialize();
        Application.Run(new UsageContext());
    }
}

internal sealed record UsageWindow(double UsedPercent, DateTimeOffset? ResetsAt);
/// <param name="Transient">The failure may clear on the next poll, so the last good values stay on screen.</param>
internal sealed record ProviderUsage(UsageWindow? FiveHour, UsageWindow? Weekly, string? Error = null,
    bool Transient = false, DateTime? UpdatedAt = null)
{
    public bool HasData => FiveHour is not null || Weekly is not null;
}

internal sealed class UsageContext : ApplicationContext
{
    private readonly NotifyIcon _icon;
    private readonly ContextMenuStrip _menu = new();
    private readonly ToolStripMenuItem _claude = new("Claude Code: 読み込み中…") { Enabled = false };
    private readonly ToolStripMenuItem _codex = new("Codex: 読み込み中…") { Enabled = false };
    private readonly ToolStripMenuItem _updated = new("更新中…") { Enabled = false };
    private readonly ToolStripMenuItem _refresh = new("今すぐ更新");
    private readonly ToolStripMenuItem _preview = new("使用量を表示 (5秒)");
    private readonly ToolStripMenuItem _startup = new("Windows 起動時に実行");
    private readonly ToolStripMenuItem _showProgress = new("実行中の進捗を表示");
    private readonly ToolStripMenuItem _notifyDone = new("完了を通知");
    private readonly ToolStripMenuItem _quietFullscreen = new("全画面アプリ中は通知しない");
    private readonly ToolStripMenuItem _themeMenu = new("テーマ");
    private readonly ToolStripMenuItem _hoverMenu = new("上端ホバー");
    private readonly ToolStripMenuItem _hoverOpen = new("ホバーで開く");
    private static readonly (string Label, IslandTheme Theme)[] Themes =
    [
        ("Classic（上端に付く）", IslandTheme.Classic), ("Pill（浮かぶカプセル）", IslandTheme.Pill)
    ];
    private static readonly (string Label, int Ms)[] HoverDelays = [("開くまで 短い (0.12 秒)", 120), ("開くまで 標準 (0.22 秒)", 220), ("開くまで 長い (0.4 秒)", 400)];
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 1000 };
    private readonly System.Windows.Forms.Timer _holdTimer = new() { Interval = 320 };
    private readonly System.Windows.Forms.Timer _previewTimer = new() { Interval = 5000 };
    private readonly System.Windows.Forms.Timer _activityTimer = new() { Interval = 1500 };
    private readonly System.Windows.Forms.Timer _spinTimer = new() { Interval = 1000 }; // the notice's elapsed time
    private readonly System.Windows.Forms.Timer _doneTimer = new() { Interval = 4500 };
    private readonly ActivityMonitor _monitor = new();
    private IReadOnlyList<AgentSession> _running = [];
    private readonly List<NoticeData> _recent = []; // finished turns, newest first: the card's 履歴 list
    private NoticeData? _alertNotice;
    private readonly List<AskRequest> _asks = []; // approvals and questions whose hooks are waiting for the tray
    private readonly HashSet<string> _answeredAsks = [];
    private readonly System.Windows.Forms.Timer _askTimer = new() { Interval = 400 };
    private NoticeData? _detail; // the notice whose detail card is up
    private readonly Queue<(NoticeData Data, int Ms)> _alertQueue = new();
    private readonly QuotaWatcher _quota = new();
    private readonly ToolStripMenuItem _settingsItem = new("設定…");
    private bool _usageShown;
    private bool _scanning;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private readonly UsageIsland _island = new();
    private readonly Workspace _workspace = new(Environment.GetCommandLineArgs().Any(arg => arg.StartsWith("--demo-panel")) ? Path.Combine(Path.GetTempPath(), "trayusage-demo.json") : null);
    private ClipboardWatcher? _clipboard;
    private readonly HoverTrigger _hover;
    private CapsLockHook? _capsLock;
    private ProviderUsage? _claudeUsage;
    private ProviderUsage? _codexUsage;
    private bool? _lightTaskbar;
    private DateTime _lastRefreshAt;
    private bool _disposed;

    public UsageContext()
    {
        _menu.Items.AddRange([new ToolStripMenuItem("Caps Lock 長押しで表示") { Enabled = false },
            new ToolStripSeparator(), _claude, _codex, new ToolStripSeparator(), _updated, _preview, _refresh,
            new ToolStripSeparator(), _showProgress, _notifyDone, _quietFullscreen,
            new ToolStripSeparator(), _settingsItem, _themeMenu, _hoverMenu, _startup,
            new ToolStripMenuItem("終了", null, (_, _) => ExitThread())]);
        _refresh.Click += async (_, _) => await RefreshAsync();
        _preview.Click += (_, _) => ShowPreview();
        _settingsItem.Click += (_, _) => ShowSettings();
        _menu.Opening += (_, _) => { UpdateStartupMenu(); UpdateActivityMenu(); };
        _showProgress.Click += (_, _) => ToggleActivity(ActivitySettings.ShowProgress);
        _notifyDone.Click += (_, _) => ToggleActivity(ActivitySettings.NotifyCompletion);
        _quietFullscreen.Click += (_, _) => ToggleActivity(ActivitySettings.FullscreenQuiet);
        foreach (var (label, theme) in Themes)
            _themeMenu.DropDownItems.Add(new ToolStripMenuItem(label, null, (_, _) => ChangeSetting(() => ActivitySettings.SetInt(ActivitySettings.Theme, (int)theme))));
        _hoverOpen.Click += (_, _) => ToggleActivity(ActivitySettings.HoverOpen);
        _hoverMenu.DropDownItems.Add(_hoverOpen);
        _hoverMenu.DropDownItems.Add(new ToolStripSeparator());
        foreach (var (label, ms) in HoverDelays)
            _hoverMenu.DropDownItems.Add(new ToolStripMenuItem(label, null, (_, _) => ChangeSetting(() => ActivitySettings.SetInt(ActivitySettings.HoverDelayMs, ms))));
        _startup.Click += (_, _) => ToggleStartup();
        _icon = new NotifyIcon { Text = "Claude Code / Codex 使用量", ContextMenuStrip = _menu, Visible = true };
        UpdateTrayIcon();
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        _icon.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) ShowPreview(); };
        _hover = new HoverTrigger(_island);
        _hover.Opened += screen => { _previewTimer.Stop(); ShowUsage(screen, byHover: true); RefreshIfStale(); };
        _hover.Left += EndUsage;
        ApplyAppearance();
        _island.Interacted += () =>
        {
            _hover.Hold();
            if (_previewTimer.Enabled) { _previewTimer.Stop(); _previewTimer.Start(); } // a panel opened from the tray stays while it is used
            if (_detail is not null) { _doneTimer.Stop(); _doneTimer.Start(); } // so does a detail card
        };
        _island.SettingsProvider = SettingRows;
        _island.SetWorkspace(_workspace);
        _island.FilesDropped += files => _workspace.AddFiles(files);
        _island.FilesDragEntered += ShowFileTray;
        _island.TaskPromptRequested += PromptTask;
        _clipboard = new ClipboardWatcher(_workspace);
        _island.SettingChanged += OnIslandSetting;
        _island.NoticeClicked += OnNoticeClicked;
        _island.PillClicked += ShowPreview;
        _island.AskReplied += OnAskReplied;
        _askTimer.Tick += (_, _) => PollAsks();
        _askTimer.Start();
        _island.DetailLeft += () => { if (_detail is not null) FinishAlert(); }; // a card goes away when the pointer does
        _island.MoreRequested += ShowMore; // the card's "more" button: the calendar / clipboard / tasks / files panel takes its place
        _island.SessionRequested += target => { if (!SessionJump.Activate(target)) ShowPreview(); };
        _island.Hidden += () => GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        _holdTimer.Tick += (_, _) => { _holdTimer.Stop(); _previewTimer.Stop(); ShowUsage(); RefreshIfStale(); };
        _previewTimer.Tick += (_, _) =>
        {
            if (_hover.PointerOnIsland) return; // in use: it goes with the pointer (HoverTrigger), not with the timer
            _previewTimer.Stop();
            _previewTimer.Interval = 5000;
            EndUsage();
        };
        _activityTimer.Tick += async (_, _) => await ScanActivityAsync();
        _spinTimer.Tick += (_, _) => UpdateActivityIsland();
        try { HookInstaller.RepairIfStale(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
        _doneTimer.Tick += (_, _) =>
        {
            if (_hover.PointerOnIsland) return; // keep the notice up while the pointer rests on it
            FinishAlert();
        };
        try
        {
            _capsLock = new CapsLockHook();
            _capsLock.Pressed += () => { _previewTimer.Stop(); _holdTimer.Start(); };
            _capsLock.Released += () => { _holdTimer.Stop(); EndUsage(); };
        }
        catch (System.ComponentModel.Win32Exception)
        {
            _menu.Items.Insert(0, new ToolStripMenuItem("Caps Lock フックを開始できませんでした") { Enabled = false });
        }
        _timer.Tick += (_, _) =>
        {
            var interval = _island.Visible ? TimeSpan.FromSeconds(15) : TimeSpan.FromSeconds(60);
            if (DateTime.UtcNow - _lastRefreshAt >= interval) _ = RefreshAsync();
        };
        _timer.Start();
        UpdateActivityMenu();
        _activityTimer.Start();
        Application.Idle += RefreshOnFirstIdle;
        if (Environment.GetCommandLineArgs().Contains("--demo")) StartDemo();
        if (Environment.GetCommandLineArgs().Contains("--demo-ask")) StartDemoAsk(approvalOnly: false);
        if (Environment.GetCommandLineArgs().Contains("--demo-approval")) StartDemoAsk(approvalOnly: true);
        if (Environment.GetCommandLineArgs().Contains("--demo-history")) StartDemoHistory();
        if (Environment.GetCommandLineArgs().FirstOrDefault(arg => arg.StartsWith("--demo-panel")) is { } demoPanel) StartDemoPanel(demoPanel);
    }

    /// <summary>--demo-panel[=page]: the panel opens on a page (0 calendar, 1 clipboard, 2 tasks, 3 files) with sample content, kept in a temporary file.</summary>
    private void StartDemoPanel(string argument)
    {
        var page = int.TryParse(argument.Split('=').ElementAtOrDefault(1), out var parsed) ? parsed : 0;
        var today = DateOnly.FromDateTime(DateTime.Today);
        _workspace.Tasks.Clear();
        _workspace.Files.Clear();
        _workspace.AddTask("請求書を送る", today);
        _workspace.AddTask("週次レポートを書く", today.AddDays(1));
        _workspace.AddTask("歯医者の予約をとる", null);
        _workspace.AddTask("Review the pull request for the settings page", today);
        _workspace.AddTask("牛乳を買う", null);
        _workspace.ToggleTask(_workspace.Tasks[4].Id);
        _workspace.AddClip("https://github.com/lucacadalora/oyenza-studio");
        _workspace.AddClip("TrayUsage/PanelSurface.cs\nline 2 of a copied block\nline 3");
        _workspace.AddClip("明日の会議は 10:30 から、場所は 3F の小会議室です。");
        _workspace.AddFiles([Path.Combine(Environment.CurrentDirectory, "README.md"), Environment.CurrentDirectory, Path.Combine(Environment.CurrentDirectory, "missing-file.txt")]);
        _usageShown = true;
        _hover.PanelOpenedByHover = false;
        _island.ShowPage(page);
        _previewTimer.Interval = 120000;
        _previewTimer.Start();
    }

    /// <summary>--demo-history: a few finished turns in the card's 履歴 list, to look at it without waiting for real ones.</summary>
    private void StartDemoHistory()
    {
        var now = DateTimeOffset.Now;
        _recent.AddRange(
        [
            DoneNotice(new(AgentKind.Claude, "Context", TimeSpan.FromSeconds(134), now.AddMinutes(-3), Demo(AgentKind.Claude, "claude-done"))),
            DoneNotice(new(AgentKind.Codex, "wa-site", TimeSpan.FromMinutes(12) + TimeSpan.FromSeconds(8), now.AddMinutes(-26), Demo(AgentKind.Codex, "codex-done"))),
            DoneNotice(new(AgentKind.Claude, "TrayUsage", TimeSpan.FromSeconds(48), now.AddHours(-2), Demo(AgentKind.Claude, "claude-done-2")))
        ]);
        _island.SetRecent(_recent);
    }

    /// <summary>--demo-ask: an approval and a question on the island, answered into nothing (no hook is waiting), to look at their layout.</summary>
    private void StartDemoAsk(bool approvalOnly)
    {
        var target = Demo(AgentKind.Claude, "ask");
        if (!approvalOnly) _asks.Add(new("demo-question", true, AgentKind.Claude, "TrayUsage", null, null,
        [
            new("How should the new settings page open?", "Settings", [new("In the current window", "Replaces what is on screen"), new("In a separate window", "Opens beside it"), new("As a panel", null)]),
            new("Which theme should it start in?", "Theme", [new("Classic", null), new("Pill", null)])
        ], target));
        _asks.Add(new("demo-approval", false, AgentKind.Claude, "TrayUsage", "Bash", "dotnet publish TrayUsage/TrayUsage.csproj -c Release -r win-x64 --no-self-contained -o dist", [], target));
        UpdateActivityIsland();
    }

    // The sessions --demo shows as running, so the card's pager has pages to turn.
    private List<NoticeData>? _demoSessions;

    /// <summary>--demo: cycles through sample notices (like Agent HUD's demo flag) to preview every kind without real events.</summary>
    private static SessionTarget Demo(AgentKind kind, string id) => new(kind, Environment.CurrentDirectory, $"demo-{id}");

    private void StartDemo()
    {
        var claude = Demo(AgentKind.Claude, "claude");
        var claudeDone = Demo(AgentKind.Claude, "claude-done"); // a finished turn is a page of its own, apart from the running one
        var codex = Demo(AgentKind.Codex, "codex");
        var now = DateTime.Now.ToString("HH:mm");
        const string reply = "Added the format check to LoginForm and three tests; all pass.";
        NoticeData[] samples =
        [
            new(AgentKind.Claude, NoticeType.Running, "Claude Code", "Context · Running", "0:42", claude,
                FiguresOf(new("Fix the email check on the login form", "ログインフォームとテストを読んで、メールの検証を直します。", "claude-sonnet-5-5", "main", 64000,
                    [("Read", 5), ("Grep", 2), ("Edit", 1)], null, [], null,
                    [new("Bash", "git status", true), new("Read", "LoginForm.tsx", true), new("Grep", "validateEmail", true), new("Edit", "LoginForm.tsx", false)]),
                    claude, ("Running", "0:42"))),
            new(AgentKind.Claude, NoticeType.Completed, "Claude Code finished", "Context", "2:14", claudeDone,
                FiguresOf(new("Fix the email check on the login form and add tests", reply, "claude-sonnet-5-5", "main", 122000,
                    [("Bash", 6), ("Edit", 3), ("Read", 4)], null, [], null,
                    [new("Read", "LoginForm.tsx", true), new("Edit", "LoginForm.tsx", true), new("Edit", "LoginForm.test.tsx", true),
                        new("Bash", "npm test -- LoginForm", true)]), claudeDone, ("Took", "2:14"), ("Finished", now))),
            new(AgentKind.Claude, NoticeType.Completed, "Claude Code finished a long task", "Context", "12:08", claudeDone,
                FiguresOf(new("Migrate the settings page to the new form components", "Moved all eleven settings rows; the old components are removed.", "claude-sonnet-5-5", "refactor/settings", 168000,
                    [("Edit", 14), ("Bash", 9), ("Read", 11)], null, [], null), claudeDone, ("Took", "12:08"), ("Finished", now))),
            AskedNotice(new(AgentKind.Codex, "Context", DateTimeOffset.Now, codex, AskReason.Question,
                new("Pick a validation library", null, "gpt-6", "main", 31000, [("shell", 2)], "Which library should validate the form?", ["zod", "yup", "Write our own"], null))),
            AskedNotice(new(AgentKind.Claude, "Context", DateTimeOffset.Now, claude, AskReason.Plan,
                new("Plan the login validation work", null, "claude-sonnet-5-5", "main", 48000, [("Read", 6), ("Grep", 3)], null, [],
                    "1. Validate the email format in LoginForm / 2. Show the error under the field / 3. Add three tests"))),
            new(AgentKind.Claude, NoticeType.Approval, "Claude Code needs approval", "Context · Bash", "Approval", claude,
                FiguresOf(new("Run the tests and fix what fails", null, "claude-sonnet-5-5", "main", 91000, [("Edit", 4), ("Read", 7)], null, [], null),
                    claude, ("Tool", "Bash"), ("Waiting for", "You"))),
            AskedNotice(new(AgentKind.Claude, "Context", DateTimeOffset.Now, claude, AskReason.Failed,
                new("Run the full test suite", "API Error: 500 overloaded_error. Please try again in a moment.", "claude-sonnet-5-5", "main", 77000, [("Bash", 3)], null, [], null))),
            new(AgentKind.Claude, NoticeType.Warning, "Claude Code 5-hour limit at 85%", "15% left · Resets 13:29", "85%", Card: new(Percent: 85, Footer: "Resets 13:29")),
            new(AgentKind.Codex, NoticeType.Exhausted, "Codex weekly limit reached", "Resets 11/4 16:04", "100%", Card: new(Percent: 100, Footer: "Resets 11/4 16:04")),
            AskedNotice(new(AgentKind.Claude, "Context", DateTimeOffset.Now, claude, AskReason.Limited,
                new("Keep going on the refactor", "5-hour limit reached · resets 3pm", "claude-sonnet-5-5", "main", 140000, [("Edit", 2)], null, [], null))),
            new(AgentKind.Claude, NoticeType.Reset, "Claude Code 5-hour limit reset", "Usage 92% → 0%", "0%", Card: new(Percent: 0, Footer: "Was 92%"))
        ];
        _demoSessions =
        [
            samples[0],
            new(AgentKind.Codex, NoticeType.Running, "Codex", "Context · Running", "1:05", codex,
                FiguresOf(new("Build the release", "swift build で、リリース設定のビルドを確認します。", "gpt-6", "main", 31000, [("shell", 2)], null, [], null,
                    [new("exec_command", "swift package resolve", true), new("exec_command", "swift build --configuration release", false)]),
                    codex, ("Running", "1:05")))
        ];
        // One notice every 7 s, each shown for 6.5 s; click one to open its card (the card holds the queue until it closes).
        var next = 0;
        var timer = new System.Windows.Forms.Timer { Interval = 7000 };
        timer.Tick += (_, _) => PostAlert(samples[next++ % samples.Length], 6500);
        timer.Start();
        PostAlert(samples[next++ % samples.Length], 6500);
    }

    private async void RefreshOnFirstIdle(object? sender, EventArgs e)
    {
        Application.Idle -= RefreshOnFirstIdle;
        UpdateActivityIsland();
        await RefreshAsync();
    }

    /// <summary>Opens the panel. Hover-opened panels collapse when the pointer leaves; Caps Lock and tray ones own their dismissal.</summary>
    private void ShowUsage(Screen? screen = null, bool byHover = false)
    {
        _detail = null;
        _usageShown = true;
        _hover.PanelOpenedByHover = byHover;
        _island.SetSessions(SessionPages());
        _island.ShowPanel(screen);
    }

    /// <summary>Hides the panel, falling back to a notice while there is one to show.</summary>
    private void EndUsage()
    {
        _usageShown = false;
        _previewTimer.Stop();
        _hover.PanelOpenedByHover = false;
        if (!UpdateActivityIsland()) _island.Dismiss();
    }

    private void UpdateActivityMenu()
    {
        _showProgress.Checked = ActivitySettings.Get(ActivitySettings.ShowProgress);
        _notifyDone.Checked = ActivitySettings.Get(ActivitySettings.NotifyCompletion);
        _quietFullscreen.Checked = ActivitySettings.Get(ActivitySettings.FullscreenQuiet);
        _hoverOpen.Checked = ActivitySettings.Get(ActivitySettings.HoverOpen);
        var theme = ActivitySettings.GetInt(ActivitySettings.Theme, 0);
        for (var i = 0; i < Themes.Length; i++) ((ToolStripMenuItem)_themeMenu.DropDownItems[i]).Checked = i == theme;
        var delay = ActivitySettings.GetInt(ActivitySettings.HoverDelayMs, 220);
        for (var i = 0; i < HoverDelays.Length; i++) ((ToolStripMenuItem)_hoverMenu.DropDownItems[i + 2]).Checked = HoverDelays[i].Ms == delay;
    }

    private static readonly string[] DelayLabels = ["短い", "標準", "長い"];

    private SettingRow[] SettingRows()
    {
        static int Flag(string name) => ActivitySettings.Get(name) ? 1 : 0;
        var delay = ActivitySettings.GetInt(ActivitySettings.HoverDelayMs, 220);
        var startup = false;
        var hookInstalled = false;
        var answerMode = false;
        try
        {
            hookInstalled = HookInstaller.IsInstalled();
            answerMode = hookInstalled && HookInstaller.IsAnswerMode();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
        try { startup = StartupRegistration.IsEnabled(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
        // Rows of a group are listed together; the settings page puts each group under its own heading.
        return
        [
            new("notify", "通知", "完了を通知", [], Flag(ActivitySettings.NotifyCompletion)),
            new("question", "通知", "質問待ちを通知", [], Flag(ActivitySettings.NotifyQuestion)),
            new("quota", "通知", "使用量の警告・リセット", [], Flag(ActivitySettings.NotifyQuota)),
            new("hook", "通知", "承認待ちを通知 (Hook)", [], hookInstalled ? 1 : 0),
            new("answer", "通知", "トレイから回答 (Hook)", [], answerMode ? 1 : 0),
            new("fullscreen", "通知", "全画面中は通知しない", [], Flag(ActivitySettings.FullscreenQuiet)),
            new("theme", "表示", "テーマ", ["Classic", "Pill"], Math.Clamp(ActivitySettings.GetInt(ActivitySettings.Theme, 0), 0, Themes.Length - 1)),
            new("progress", "表示", "実行中の進捗を表示", [], Flag(ActivitySettings.ShowProgress)),
            new("hover", "上端ホバー", "上端ホバーで開く", [], Flag(ActivitySettings.HoverOpen)),
            new("delay", "上端ホバー", "待ち時間", DelayLabels, Math.Max(0, Array.FindIndex(HoverDelays, d => d.Ms == delay))),
            new("startup", "システム", "Windows 起動時に実行", [], startup ? 1 : 0)
        ];
    }

    private void OnIslandSetting(string id, int value)
    {
        switch (id)
        {
            case "notify": ChangeSetting(() => ActivitySettings.Set(ActivitySettings.NotifyCompletion, value != 0)); break;
            case "progress": ChangeSetting(() => ActivitySettings.Set(ActivitySettings.ShowProgress, value != 0)); break;
            case "fullscreen": ChangeSetting(() => ActivitySettings.Set(ActivitySettings.FullscreenQuiet, value != 0)); break;
            case "hover": ChangeSetting(() => ActivitySettings.Set(ActivitySettings.HoverOpen, value != 0)); break;
            case "question": ChangeSetting(() => ActivitySettings.Set(ActivitySettings.NotifyQuestion, value != 0)); break;
            case "quota": ChangeSetting(() => ActivitySettings.Set(ActivitySettings.NotifyQuota, value != 0)); break;
            case "hook":
                try
                {
                    if (value != 0) HookInstaller.Install();
                    else HookInstaller.Uninstall();
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
                {
                    MessageBox.Show($"Hook を変更できませんでした。\n{ex.Message}", "Tray Usage", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                break;
            case "answer":
                // Answering needs hooks that wait for the tray; off keeps the observe-only ones (or none, if they were never installed).
                try
                {
                    if (value != 0) HookInstaller.Install(answer: true);
                    else if (HookInstaller.IsInstalled()) HookInstaller.Install(answer: false);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
                {
                    MessageBox.Show($"Hook を変更できませんでした。\n{ex.Message}", "Tray Usage", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                break;
            case "delay": ChangeSetting(() => ActivitySettings.SetInt(ActivitySettings.HoverDelayMs, HoverDelays[Math.Clamp(value, 0, HoverDelays.Length - 1)].Ms)); break;
            case "theme": ChangeSetting(() => ActivitySettings.SetInt(ActivitySettings.Theme, value)); break;
            case "startup":
                try { StartupRegistration.SetEnabled(value != 0); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
                break;
        }
        if (!ActivitySettings.Get(ActivitySettings.NotifyCompletion)) { _doneTimer.Stop(); _alertNotice = null; _detail = null; _alertQueue.Clear(); }
        UpdateActivityIsland();
        _island.RefreshSettings();
    }

    /// <summary>Pushes the saved theme and hover settings into the island and the hover trigger.</summary>
    private void ApplyAppearance()
    {
        _island.Theme = Themes[Math.Clamp(ActivitySettings.GetInt(ActivitySettings.Theme, 0), 0, Themes.Length - 1)].Theme;
        _hover.Enabled = ActivitySettings.Get(ActivitySettings.HoverOpen);
        _hover.DwellSeconds = ActivitySettings.GetInt(ActivitySettings.HoverDelayMs, 220) / 1000.0;
    }

    private void ChangeSetting(Action save)
    {
        try { save(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            MessageBox.Show($"設定を変更できませんでした。\n{ex.Message}", "Tray Usage", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        UpdateActivityMenu();
        ApplyAppearance();
        UpdateActivityIsland(); // the Pill theme keeps a resting capsule up; the Classic one has none
    }

    private void ToggleActivity(string name)
    {
        ChangeSetting(() => ActivitySettings.Set(name, !ActivitySettings.Get(name)));
        if (!_notifyDone.Checked) { _doneTimer.Stop(); _alertNotice = null; _detail = null; _alertQueue.Clear(); }
        UpdateActivityIsland();
    }

    private async Task ScanActivityAsync()
    {
        if (_disposed || _scanning) return;
        foreach (var hook in HookInbox.Poll())
            PostAlert(hook.Type == "elicitation_dialog"
                ? new(hook.Kind, NoticeType.Question, $"{Agent.Name(hook.Kind)} has a question", hook.Project, "Waiting", hook.Target,
                    CardOf(BriefOf(hook.Target), BriefOf(hook.Target)?.Question, hook.Target))
                : new(hook.Kind, NoticeType.Approval, $"{Agent.Name(hook.Kind)} needs approval",
                    hook.Tool is { Length: > 0 } tool ? $"{hook.Project} · {tool}" : hook.Project, "Approval", hook.Target,
                    FiguresOf(BriefOf(hook.Target), hook.Target, ("Tool", hook.Tool ?? "—"), ("Waiting for", "You"))), 10000);
        var progress = ActivitySettings.Get(ActivitySettings.ShowProgress);
        var notify = ActivitySettings.Get(ActivitySettings.NotifyCompletion);
        var question = ActivitySettings.Get(ActivitySettings.NotifyQuestion);
        if (!progress && !notify && !question)
        {
            _running = [];
            return;
        }
        _scanning = true;
        try
        {
            // Reads only the tails of recently written session logs; kept off the UI thread.
            var snapshot = await Task.Run(_monitor.Scan);
            if (_disposed) return;
            // Only a big read is collected right away; a forced collection on every scan would stall the animation.
            if (snapshot.ReadLarge) GC.Collect(2, GCCollectionMode.Forced, blocking: true);
            var absorbed = UpdateProgressCard(snapshot);
            _running = snapshot.Running;
            foreach (var turn in snapshot.Completed) _recent.Insert(0, DoneNotice(turn));
            if (_recent.Count > ProgressSurface.ListRows) _recent.RemoveRange(ProgressSurface.ListRows, _recent.Count - ProgressSurface.ListRows);
            _island.SetRecent(_recent);
            _island.SetSessions(SessionPages());
            // A turn that finished under an open progress card was shown by that card; it needs no notice of its own.
            var announce = snapshot.Completed.Where(turn => !ReferenceEquals(turn, absorbed)).ToList();
            if (notify && announce.Count > 0) PostAlert(DoneNotice(announce[^1]), announce[^1].Duration >= LongTurn ? 9000 : 4500);
            if (question)
                foreach (var asked in snapshot.Asked)
                {
                    // A question the tray can answer is already on the island, as buttons.
                    if (asked.Reason == AskReason.Question && _asks.Any(ask => ask.IsQuestion && SameSession(ask.Target, asked.Target))) continue;
                    PostAlert(AskedNotice(asked), 8000);
                }
            UpdateActivityIsland();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or ArgumentException)
        {
            // Session logs are written by other processes; try again on the next tick.
        }
        finally
        {
            _scanning = false;
        }
    }

    /// <summary>
    /// What the card pages through: a page per running session, in the order they started. The notice whose card is open stays a
    /// page while its turn runs (and for a demo notice, which no log backs). A finished turn is no page: with no pages the card
    /// turns into the list of finished turns.
    /// </summary>
    private List<NoticeData> SessionPages()
    {
        var pages = _running.Select(session => RunningNotice([session])).ToList();
        if (_demoSessions is not null) pages.AddRange(_demoSessions);
        if (_detail is { Type: NoticeType.Running } open &&
            !pages.Any(page => ProgressSurface.KeyOf(page) == ProgressSurface.KeyOf(open)))
            pages.Add(open);
        return pages;
    }

    /// <summary>
    /// Keeps the notice behind an open card in step with its session: when its turn ends the card turns into the finished one (the page
    /// follows by session id, see <see cref="SessionPages"/>). A turn that was cancelled or whose terminal was closed has nothing left
    /// to show, so the card goes.
    /// </summary>
    /// <returns>The finished turn the card took over, or null.</returns>
    private CompletedTurn? UpdateProgressCard(ActivitySnapshot snapshot)
    {
        if (_demoSessions is not null || _detail is not { Type: NoticeType.Running } open || open.Target?.SessionId is not { } id) return null;
        if (snapshot.Running.FirstOrDefault(session => session.Target?.SessionId == id) is { } running)
        {
            _detail = RunningNotice([running]);
            return null;
        }
        if (snapshot.Completed.LastOrDefault(turn => turn.Target?.SessionId == id) is { } finished)
        {
            _detail = DoneNotice(finished);
            _doneTimer.Stop();
            _doneTimer.Interval = 8000;
            _doneTimer.Start();
            return finished;
        }
        FinishAlert();
        return null;
    }

    /// <summary>Shows the finished notice or the running capsule. Returns false when there is nothing to show.</summary>
    private bool UpdateActivityIsland()
    {
        if (_usageShown || _detail is not null) return true; // the panel (or a detail card) owns the island until it is dismissed
        if (_asks.Count > 0)
        {
            // A hook is waiting for the answer, so it comes before every notice, and the full-screen quiet does not hide it: the agent is
            // blocked until the hook gives up.
            _spinTimer.Stop();
            _island.ShowAsk(_asks[0]);
            return true;
        }
        if (ActivitySettings.Get(ActivitySettings.FullscreenQuiet) && FullscreenGuard.IsForegroundFullscreen())
        {
            _spinTimer.Stop();
            if (_island.Visible && _island.Mode == IslandMode.Notice) _island.Dismiss();
            return false;
        }
        if (_alertNotice is not null)
        {
            _spinTimer.Stop();
            _island.ShowNotice(_alertNotice);
            return true;
        }
        if (_running.Count > 0 && ActivitySettings.Get(ActivitySettings.ShowProgress))
        {
            _spinTimer.Start();
            _island.ShowNotice(RunningNotice(_running));
            return true;
        }
        _spinTimer.Stop();
        if (PillTheme)
        {
            _island.ShowIdle(); // the Pill theme keeps its resting capsule up, with the usage ring in it
            return true;
        }
        if (_island.Visible && _island.Mode == IslandMode.Notice) _island.Dismiss();
        return false;
    }

    private static bool PillTheme => ActivitySettings.GetInt(ActivitySettings.Theme, 0) == (int)IslandTheme.Pill;

    private static NoticeData RunningNotice(IReadOnlyList<AgentSession> running)
    {
        var first = running[0];
        var title = running.Count == 1 ? Agent.Name(first.Kind) : $"{running.Count} running";
        var subtitle = running.Count == 1 ? $"{first.Project} · Running" : $"{first.Project} and more";
        var elapsed = TimeText.Elapsed(DateTimeOffset.Now - first.StartedAt);
        var card = FiguresOf(first.Brief, first.Target, ("Running", elapsed));
        if (running.Count > 1) card = card with { Footer = $"{running.Count - 1} more running · {string.Join(", ", running.Skip(1).Take(3).Select(s => s.Project))}" };
        return new(first.Kind, NoticeType.Running, title, subtitle, elapsed, first.Target, card);
    }

    /// <summary>A turn that ran this long is easy to miss, so its notice says so and stays up longer (Agent HUD's long-done card, 10 minutes).</summary>
    private static readonly TimeSpan LongTurn = TimeSpan.FromMinutes(10);

    private static NoticeData DoneNotice(CompletedTurn turn) =>
        new(turn.Kind, NoticeType.Completed, $"{Agent.Name(turn.Kind)} {(turn.Duration >= LongTurn ? "finished a long task" : "finished")}", turn.Project, TimeText.Elapsed(turn.Duration), turn.Target,
            FiguresOf(turn.Brief, turn.Target, ("Took", TimeText.Elapsed(turn.Duration)), ("Finished", $"{turn.FinishedAt.ToLocalTime():HH:mm}")));

    private static NoticeData AskedNotice(AskedQuestion asked)
    {
        var name = Agent.Name(asked.Kind);
        var brief = asked.Brief;
        var at = ("At", $"{asked.At.ToLocalTime():HH:mm}");
        return asked.Reason switch
        {
            AskReason.Plan => new(asked.Kind, NoticeType.Question, $"{name} has a plan ready", asked.Project, "Review", asked.Target,
                CardOf(brief, null, asked.Target, at) with { Choices = PlanSteps(brief?.Plan) }),
            AskReason.Failed => new(asked.Kind, NoticeType.Warning, $"{name} stopped on an error", asked.Project, "Failed", asked.Target,
                CardOf(brief, brief?.Reply, asked.Target, at) with { Heading = ErrorHeading(brief?.Reply) }),
            AskReason.Limited => new(asked.Kind, NoticeType.Exhausted, $"{name} hit its usage limit", asked.Project, "Limit", asked.Target,
                CardOf(brief, brief?.Reply, asked.Target, at) with { Heading = LimitHeading(brief?.Reply) }),
            _ => new(asked.Kind, NoticeType.Question, $"{name} has a question", asked.Project, "Waiting", asked.Target,
                CardOf(brief, brief?.Question, asked.Target, at))
        };
    }

    /// <summary>
    /// The body of a notice's detail card. First screen: the message (and the choices on offer). Second screen: up to three
    /// figures (the given facts, then context size and model), the busiest tools, and the branch.
    /// </summary>
    private static NoticeCard CardOf(SessionBrief? brief, string? body, SessionTarget? target, params (string Label, string Value)[] facts)
    {
        var stats = facts.Select(f => (f.Value, f.Label)).ToList();
        if (brief is { ContextTokens: > 0 }) stats.Add((Tokens(brief.ContextTokens), "Context"));
        if (brief?.Model is { Length: > 0 } model) stats.Add((model.StartsWith("claude-") ? model[7..] : model, "Model"));
        var folder = target?.Cwd is { Length: > 0 } cwd ? Path.GetFileName(cwd.TrimEnd('\\', '/')) : null;
        return new(body, brief?.Choices, brief?.Tools, stats, null, brief?.Branch is { Length: > 0 } branch ? branch : folder, brief?.Prompt);
    }

    /// <summary>The figures-only card (completions, running sessions, approvals): no first screen.</summary>
    private static NoticeCard FiguresOf(SessionBrief? brief, SessionTarget? target, params (string Label, string Value)[] facts) =>
        CardOf(brief, null, target, facts) with { Task = null, Choices = null, Steps = brief?.Steps, Prompt = brief?.Prompt, Reply = brief?.Reply };

    /// <summary>The numbered steps of a plan, as the log collector joined them (" / "), without their own numbers.</summary>
    private static IReadOnlyList<string>? PlanSteps(string? plan) => plan?.Split(" / ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(step => System.Text.RegularExpressions.Regex.Replace(step, @"^\d+[.)]\s*", "")).Take(4).ToArray();

    private static string ErrorHeading(string? text) =>
        text is not null && System.Text.RegularExpressions.Regex.Match(text, @"\b([45]\d\d)\b") is { Success: true } code ? $"Error {code.Value}" : "Error";

    private static string LimitHeading(string? text) =>
        text is not null && System.Text.RegularExpressions.Regex.Match(text, @"resets?\s+(.+)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase) is { Success: true } at
            ? $"Resets {at.Groups[1].Value}" : "Limit reached";

    private static string Tokens(int count) => count >= 1_000_000 ? $"{count / 1e6:0.0}M" : count >= 1000 ? $"{count / 1000}k" : count.ToString();

    /// <summary>What a running session has been doing, looked up by its id (a hook event carries no log details).</summary>
    private SessionBrief? BriefOf(SessionTarget? target) =>
        target?.SessionId is { } id ? _running.FirstOrDefault(s => s.Target?.SessionId == id)?.Brief : null;

    private static bool SameSession(SessionTarget? a, SessionTarget? b) => a?.SessionId is { } id && id == b?.SessionId;

    /// <summary>Takes the hooks' waiting requests into <see cref="_asks"/> and drops those whose hook has gone.</summary>
    private void PollAsks()
    {
        if (_disposed) return;
        var live = AskInbox.Poll();
        var changed = false;
        foreach (var request in live)
        {
            if (_asks.Any(ask => ask.Id == request.Id) || _answeredAsks.Contains(request.Id)) continue;
            _asks.Add(request);
            changed = true;
            if (request.IsQuestion) DropQuestionNotices(request.Target);
        }
        // A demo request has no hook behind it, so it never goes away by itself.
        changed |= _asks.RemoveAll(ask => AskInbox.IsValidId(ask.Id) && live.All(item => item.Id != ask.Id)) > 0;
        if (_answeredAsks.Count > 50) _answeredAsks.Clear();
        if (changed) UpdateActivityIsland();
    }

    /// <summary>The notice the log raised for a question the tray is now asking is the same question: it is not shown twice.</summary>
    private void DropQuestionNotices(SessionTarget target)
    {
        if (_alertNotice is { Type: NoticeType.Question } current && SameSession(current.Target, target))
        {
            _alertNotice = null;
            _doneTimer.Stop();
        }
        var kept = _alertQueue.Where(item => !(item.Data.Type == NoticeType.Question && SameSession(item.Data.Target, target))).ToList();
        if (kept.Count == _alertQueue.Count) return;
        _alertQueue.Clear();
        foreach (var item in kept) _alertQueue.Enqueue(item);
    }

    private void OnAskReplied(AskRequest request, AskReply reply)
    {
        _asks.RemoveAll(ask => ask.Id == request.Id);
        _answeredAsks.Add(request.Id);
        AskInbox.Reply(request.Id, reply);
        if (reply.Action == "terminal" && !SessionJump.Activate(request.Target)) ShowPreview();
        UpdateActivityIsland();
    }

    private void FinishAlert()
    {
        _detail = null;
        _doneTimer.Stop();
        _alertNotice = null;
        if (_alertQueue.TryDequeue(out var next)) StartAlert(next.Data, next.Ms);
        else UpdateActivityIsland();
    }

    /// <summary>
    /// A click on a notice opens its detail card in the island, and a click on the card closes it. Jumping to the
    /// terminal is not offered: with several windows or tabs open, it cannot say which one the session lives in.
    /// </summary>
    private void OnNoticeClicked()
    {
        if (_detail is not null)
        {
            FinishAlert();
            return;
        }
        if (_island.CurrentNotice is not { } notice) return;
        _detail = notice;
        _island.SetSessions(SessionPages());
        _island.ShowDetail(notice);
        _doneTimer.Stop();
        _doneTimer.Interval = 15000;
        _doneTimer.Start();
    }

    /// <summary>Queues a notice; one that arrives while another is up waits its turn.</summary>
    private void PostAlert(NoticeData data, int ms)
    {
        if (_detail is not null || (_alertNotice is not null && _doneTimer.Enabled))
        {
            if (_alertQueue.Count < 4) _alertQueue.Enqueue((data, ms));
            return;
        }
        StartAlert(data, ms);
    }

    private void StartAlert(NoticeData data, int ms)
    {
        _alertNotice = data;
        _doneTimer.Stop();
        _doneTimer.Interval = ms;
        _doneTimer.Start();
        UpdateActivityIsland();
    }

    private void AlertQuota(AgentKind kind, ProviderUsage usage)
    {
        foreach (var alert in _quota.Observe(kind, usage))
        {
            if (!ActivitySettings.Get(ActivitySettings.NotifyQuota)) continue;
            var name = Agent.Name(kind);
            var reset = alert.ResetsAt is { } at ? at.ToLocalTime() : (DateTimeOffset?)null;
            var resetText = reset is { } time ? (time.Date == DateTime.Today ? $"Resets {time:HH:mm}" : $"Resets {time:M/d HH:mm}") : "Reset time unknown";
            var used = alert.UsedPercent;
            var data = alert.Event switch
            {
                QuotaEvent.Reset => new NoticeData(kind, NoticeType.Reset, $"{name} {alert.Window} limit reset", $"Usage {alert.PreviousPercent:0}% → {used:0}%", $"{used:0}%",
                    Card: new(Percent: used, Footer: $"Was {alert.PreviousPercent:0}%")),
                QuotaEvent.Exhausted => new NoticeData(kind, NoticeType.Exhausted, $"{name} {alert.Window} limit reached", resetText, "100%",
                    Card: new(Percent: 100, Footer: resetText)),
                _ => new NoticeData(kind, NoticeType.Warning, $"{name} {alert.Window} limit at {used:0}%", $"{Math.Max(0, 100 - used):0}% left · {resetText}", $"{used:0}%",
                    Card: new(Percent: used, Footer: resetText))
            };
            PostAlert(data, alert.Event == QuotaEvent.Reset ? 4500 : 6500);
        }
    }

    private void ShowSettings()
    {
        _holdTimer.Stop();
        _hover.PanelOpenedByHover = false;
        _detail = null;
        _usageShown = true;
        _island.ShowSettings();
        _hover.PanelOpenedByHover = true; // like every menu not held open by Caps Lock, it goes when the pointer leaves it
        _hover.AwaitPointer();
        RefreshIfStale();
        _previewTimer.Stop();
        _previewTimer.Interval = 20000;
        _previewTimer.Start();
    }

    /// <summary>
    /// The card's "more" button: the panel of calendar, clipboard, tasks and files. The pointer is on the island when it is pressed, so the panel
    /// goes when the pointer leaves it, as a hover-opened one does; no timer closes it while it is in use.
    /// </summary>
    private void ShowMore()
    {
        _holdTimer.Stop();
        _previewTimer.Stop();
        _detail = null;
        _doneTimer.Stop();
        _alertNotice = null;
        _usageShown = true;
        _hover.PanelOpenedByHover = true;
        _island.ShowSettings();
    }

    /// <summary>Asks for a task's text in a small box under the island and adds it; the island stays up meanwhile.</summary>
    private void PromptTask(DateOnly? due)
    {
        _hover.PanelOpenedByHover = false; // the box takes the focus and the pointer leaves the island; it must not collapse under the typing
        var below = _island.ScreenBody ?? Screen.PrimaryScreen!.Bounds with { Height = 0 };
        var prompt = new InputPrompt("タスクを入力", due is { } day ? $"{day.Month}月{day.Day}日" : null, below);
        prompt.Submitted += text => _workspace.AddTask(text, due);
        prompt.FormClosed += (_, _) =>
        {
            prompt.Dispose();
            _hover.PanelOpenedByHover = true; // back to leaving with the pointer
            _hover.AwaitPointer();
        };
        prompt.Show();
    }

    /// <summary>Files are being dragged over the island: the file tray opens under them, like a hover-opened panel.</summary>
    private void ShowFileTray()
    {
        if (_island.PanelPage == PanelSurface.FilesPage) return;
        _holdTimer.Stop();
        _previewTimer.Stop();
        _detail = null;
        _doneTimer.Stop();
        _alertNotice = null;
        _usageShown = true;
        _hover.PanelOpenedByHover = true;
        _island.ShowPage(PanelSurface.FilesPage);
    }

    private void ShowPreview()
    {
        _previewTimer.Interval = 5000;
        _holdTimer.Stop();
        ShowUsage(byHover: true); // the same mouse-out close as a hover-opened menu, once the pointer has been on it
        _hover.AwaitPointer();
        RefreshIfStale();
        _previewTimer.Stop();
        _previewTimer.Start();
    }

    private void RefreshIfStale()
    {
        if (DateTime.UtcNow - _lastRefreshAt >= TimeSpan.FromSeconds(5))
            _ = RefreshAsync();
    }

    private void UpdateStartupMenu()
    {
        try
        {
            _startup.Checked = StartupRegistration.IsEnabled();
            _startup.Enabled = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            _startup.Enabled = false;
        }
    }

    private void ToggleStartup()
    {
        try
        {
            StartupRegistration.SetEnabled(!_startup.Checked);
            UpdateStartupMenu();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            MessageBox.Show($"自動起動の設定を変更できませんでした。\n{ex.Message}", "Tray Usage",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            UpdateStartupMenu();
        }
    }

    private async Task RefreshAsync()
    {
        if (_disposed || !await _refreshGate.WaitAsync(0)) return;
        _lastRefreshAt = DateTime.UtcNow;
        _refresh.Enabled = false;
        _updated.Text = "更新中…";
        try
        {
            var claudeTask = UsageReader.ReadClaudeAsync(_http);
            var codexTask = UsageReader.ReadCodexAsync();
            await Task.WhenAll(claudeTask, codexTask);
            if (_disposed) return;
            var now = DateTime.Now;
            var claude = _claudeUsage = Merge(_claudeUsage, await claudeTask, now);
            var codex = _codexUsage = Merge(_codexUsage, await codexTask, now);
            SetProviderMenu(_claude, "Claude Code", claude);
            SetProviderMenu(_codex, "Codex", codex);
            _updated.Text = $"更新: {now:HH:mm:ss}";
            _island.SetUsage(claude, codex);
            AlertQuota(AgentKind.Claude, claude);
            AlertQuota(AgentKind.Codex, codex);
            _icon.Text = ShortTooltip(claude, codex);
        }
        finally
        {
            _lastRefreshAt = DateTime.UtcNow;
            if (!_disposed) _refresh.Enabled = true;
            _refreshGate.Release();
        }
    }

    /// <summary>Keeps the last successful values through transient failures such as timeouts or HTTP 429.</summary>
    private static ProviderUsage Merge(ProviderUsage? previous, ProviderUsage current, DateTime now)
    {
        if (current.Error is null) return current with { UpdatedAt = now };
        return current.Transient && previous is { HasData: true } ? previous with { Error = current.Error, Transient = true } : current;
    }

    private static void SetProviderMenu(ToolStripMenuItem item, string name, ProviderUsage usage)
    {
        item.Text = usage.Error is null ? name
            : usage.HasData ? $"{name}: {usage.UpdatedAt:HH:mm} の値（{usage.Error}）" : $"{name}: {usage.Error}";
        item.Enabled = usage.HasData;
        if (!usage.HasData) return;
        if (item.DropDownItems.Count == 0)
        {
            item.DropDownItems.Add(new ToolStripMenuItem());
            item.DropDownItems.Add(new ToolStripMenuItem());
        }
        item.DropDownItems[0].Text = FormatWindow("5時間", usage.FiveHour);
        item.DropDownItems[1].Text = FormatWindow("週次", usage.Weekly);
    }

    private static string FormatWindow(string label, UsageWindow? window)
    {
        if (window is null) return $"{label}: データなし";
        var reset = window.ResetsAt is { } time ? $" / リセット {time.ToLocalTime():M/d HH:mm}" : "";
        return $"{label}: {window.UsedPercent:0.#}% 使用  ({Math.Max(0, 100 - window.UsedPercent):0.#}% 残り){reset}";
    }

    private static string ShortTooltip(ProviderUsage claude, ProviderUsage codex)
    {
        static string Short(string name, ProviderUsage value) =>
            $"{name} 5h:{(value.FiveHour is { } h ? $"{h.UsedPercent:0}%" : "--")} 7d:{(value.Weekly is { } w ? $"{w.UsedPercent:0}%" : "--")}";
        var text = $"{Short("Claude", claude)} | {Short("Codex", codex)}";
        return text.Length <= 63 ? text : text[..63];
    }

    private void OnUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category == UserPreferenceCategory.General) UpdateTrayIcon();
    }

    /// <summary>Uses the dark or light glyph that matches the taskbar theme, at the tray's icon size.</summary>
    private void UpdateTrayIcon()
    {
        var light = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
            "SystemUsesLightTheme", 0) is int value && value != 0;
        if (_lightTaskbar == light && _icon.Icon is not null) return;
        _lightTaskbar = light;
        using var stream = typeof(UsageContext).Assembly.GetManifestResourceStream(
            $"TrayUsage.Assets.{(light ? "tray-light.ico" : "tray-dark.ico")}")
            ?? throw new InvalidOperationException("Missing tray icon.");
        var previous = _icon.Icon;
        _icon.Icon = new Icon(stream, SystemInformation.SmallIconSize);
        previous?.Dispose();
    }

    protected override void ExitThreadCore()
    {
        if (!_disposed)
        {
            _disposed = true;
            SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
            _timer.Stop();
            _timer.Dispose();
            _holdTimer.Dispose();
            _previewTimer.Dispose();
            _activityTimer.Dispose();
            _spinTimer.Dispose();
            _doneTimer.Dispose();
            _capsLock?.Dispose();
            _clipboard?.Dispose();
            _hover.Dispose();
            _island.Dispose();
            _icon.Visible = false;
            _icon.Icon?.Dispose();
            _icon.Dispose();
            _menu.Dispose();
            _http.Dispose();
        }
        base.ExitThreadCore();
    }
}

internal static class UsageReader
{
    private static DateTime _claudeRetryAt;

    public static async Task<ProviderUsage> ReadClaudeAsync(HttpClient http)
    {
        if (DateTime.UtcNow < _claudeRetryAt) return new(null, null, "Rate limited", Transient: true);
        try
        {
            var path = Path.Combine(ClaudeConfigDirectory(), ".credentials.json");
            if (!File.Exists(path)) return await ReadClaudeEngineAsync() ?? new(null, null, "Sign in to Claude Code");
            // Claude Code may be rewriting the file while it refreshes its token.
            await using var credentialStream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 4096, useAsync: true);
            using var credentials = await JsonDocument.ParseAsync(credentialStream);
            if (!credentials.RootElement.TryGetProperty("claudeAiOauth", out var oauth) ||
                !oauth.TryGetProperty("accessToken", out var tokenNode) ||
                string.IsNullOrWhiteSpace(tokenNode.GetString()))
                return await ReadClaudeEngineAsync() ?? new(null, null, "No Claude Code credentials");

            using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.anthropic.com/api/oauth/usage");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokenNode.GetString());
            request.Headers.Add("anthropic-beta", "oauth-2025-04-20");
            request.Headers.UserAgent.ParseAdd("claude-code/2.0.0");
            using var response = await http.SendAsync(request);
            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                return await ReadClaudeEngineAsync() ?? new(null, null, "Session expired. Restart Claude Code");
            if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
            {
                var retry = response.Headers.RetryAfter;
                var delay = retry?.Delta ?? (retry?.Date is { } date ? date - DateTimeOffset.UtcNow : TimeSpan.FromMinutes(2));
                _claudeRetryAt = DateTime.UtcNow + TimeSpan.FromSeconds(Math.Clamp(delay.TotalSeconds, 30, 900));
                return new(null, null, "Rate limited", Transient: true);
            }
            if (!response.IsSuccessStatusCode)
                return new(null, null, $"Fetch failed (HTTP {(int)response.StatusCode})", Transient: (int)response.StatusCode >= 500);
            using var responseStream = await response.Content.ReadAsStreamAsync();
            using var json = await JsonDocument.ParseAsync(responseStream);
            return ParseClaude(json.RootElement);
        }
        catch (Exception ex) when (ex is IOException or JsonException or HttpRequestException or TaskCanceledException)
        {
            return new(null, null, "Fetch failed. Check your connection and sign-in", Transient: ex is not JsonException);
        }
    }

    /// <summary>
    /// Fallback when the stored token is missing or expired: ask Claude Code's own engine (it refreshes its token itself),
    /// the way Agent HUD does (control_request get_usage over stream-json; Apache-2.0, https://github.com/jazzenchen/agent-hud-open
    /// @ 8fb6eb8567d4638b858bda71e4290b406d29c2bc, crates/agents/src/claude/account.rs). The protocol is the idea; the code is new.
    /// Returns null when the engine is unavailable or answers without limits.
    /// </summary>
    private static DateTime _engineNextAt;

    /// <summary>The engine starts a process, so it is asked at most every 3 minutes (5 after a failure); in between the last values stay.</summary>
    private static async Task<ProviderUsage?> ReadClaudeEngineAsync()
    {
        if (DateTime.UtcNow < _engineNextAt) return new ProviderUsage(null, null, "Waiting for token refresh", Transient: true);
        var usage = await RunClaudeEngineAsync();
        _engineNextAt = DateTime.UtcNow + TimeSpan.FromMinutes(usage is null ? 5 : 3);
        return usage;
    }

    private static async Task<ProviderUsage?> RunClaudeEngineAsync()
    {
        try
        {
            var start = new ProcessStartInfo("claude")
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetTempPath()
            };
            foreach (var argument in new[] { "-p", "--input-format", "stream-json", "--output-format", "stream-json", "--verbose", "--settings", "{\"disableAllHooks\":true}" })
                start.ArgumentList.Add(argument);
            using var process = new Process { StartInfo = start };
            if (!process.Start()) return null;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var drainErrors = DrainErrorsAsync(process, timeout.Token);
            try
            {
                await SendAsync(process, @"{""type"":""control_request"",""request_id"":""tray-usage"",""request"":{""subtype"":""get_usage"",""skip_behaviors"":true}}");
                while (await process.StandardOutput.ReadLineAsync(timeout.Token) is { } line)
                {
                    JsonDocument message;
                    try { message = JsonDocument.Parse(line); }
                    catch (JsonException) { continue; }
                    using (message)
                    {
                        var root = message.RootElement;
                        if (!root.TryGetProperty("type", out var type) || type.GetString() != "control_response") continue;
                        if (root.TryGetProperty("response", out var outer) && outer.TryGetProperty("response", out var payload) &&
                            payload.TryGetProperty("rate_limits", out var limits) && limits.ValueKind == JsonValueKind.Object)
                        {
                            var usage = ParseClaude(limits);
                            return usage.HasData ? usage : null;
                        }
                        return null;
                    }
                }
                return null;
            }
            finally
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
                await drainErrors;
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception or OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>Same lookup order as Claude Code: secure-storage override, then CLAUDE_CONFIG_DIR, then ~/.claude.</summary>
    private static string ClaudeConfigDirectory()
    {
        foreach (var name in new[] { "CLAUDE_SECURESTORAGE_CONFIG_DIR", "CLAUDE_CONFIG_DIR" })
            if (Environment.GetEnvironmentVariable(name) is { Length: > 0 } directory) return directory;
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
    }

    public static ProviderUsage ParseClaude(JsonElement root) => new(
        ReadClaudeWindow(root, "five_hour"), ReadClaudeWindow(root, "seven_day"));

    private static UsageWindow? ReadClaudeWindow(JsonElement root, string key)
    {
        if (!root.TryGetProperty(key, out var node) || node.ValueKind != JsonValueKind.Object ||
            !node.TryGetProperty("utilization", out var percent) || percent.ValueKind != JsonValueKind.Number ||
            !percent.TryGetDouble(out var used)) return null;
        DateTimeOffset? reset = null;
        if (node.TryGetProperty("resets_at", out var resetNode) &&
            resetNode.ValueKind == JsonValueKind.String &&
            DateTimeOffset.TryParse(resetNode.GetString(), out var parsed)) reset = parsed;
        return new(used, reset);
    }

    public static async Task<ProviderUsage> ReadCodexAsync()
    {
        try
        {
            using var process = new Process { StartInfo = new ProcessStartInfo("codex", "app-server")
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            } };
            if (!process.Start()) return new(null, null, "Cannot start Codex");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var drainErrors = DrainErrorsAsync(process, timeout.Token);
            try
            {
                // Fixed messages; written literally to avoid loading reflection-based JSON serialization.
                await SendAsync(process, """{"id":1,"method":"initialize","params":{"clientInfo":{"name":"tray-usage","title":"Tray Usage","version":"1.0"}}}""");
                using var initialization = await ReadResponseAsync(process, 1, timeout.Token);
                await SendAsync(process, """{"method":"initialized"}""");
                // The protocol asks background usage polls to skip the reset-credit detail lookup.
                await SendAsync(process, """{"id":2,"method":"account/rateLimits/read","params":{"excludeResetCreditDetails":true}}""");
                using var response = await ReadResponseAsync(process, 2, timeout.Token);
                if (response.RootElement.TryGetProperty("error", out _)) return new(null, null, "Sign in to Codex");
                return ParseCodex(response.RootElement.GetProperty("result"));
            }
            finally
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
                await drainErrors;
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException or System.ComponentModel.Win32Exception or TaskCanceledException or KeyNotFoundException)
        {
            return new(null, null, "Fetch failed. Check your Codex sign-in and CLI",
                Transient: ex is IOException or TaskCanceledException);
        }
    }

    private static async Task SendAsync(Process process, string message)
    {
        await process.StandardInput.WriteLineAsync(message);
        await process.StandardInput.FlushAsync();
    }

    private static async Task DrainErrorsAsync(Process process, CancellationToken cancellation)
    {
        var buffer = new char[1024];
        try
        {
            while (await process.StandardError.ReadAsync(buffer.AsMemory(), cancellation) != 0) { }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
    }

    private static async Task<JsonDocument> ReadResponseAsync(Process process, int id, CancellationToken cancellation)
    {
        while (true)
        {
            var line = await process.StandardOutput.ReadLineAsync(cancellation);
            if (line is null) throw new IOException("Codex app-server ended before responding.");
            var message = JsonDocument.Parse(line);
            if (message.RootElement.TryGetProperty("id", out var responseId) &&
                responseId.ValueKind == JsonValueKind.Number && responseId.GetInt32() == id)
                return message;
            message.Dispose();
        }
    }

    public static ProviderUsage ParseCodex(JsonElement root)
    {
        JsonElement limits = default;
        if (root.TryGetProperty("rateLimitsByLimitId", out var byId) &&
            byId.ValueKind == JsonValueKind.Object && byId.TryGetProperty("codex", out var codex)) limits = codex;
        else if (root.TryGetProperty("rateLimits", out var fallback)) limits = fallback;
        if (limits.ValueKind != JsonValueKind.Object) return new(null, null, "No usage data");
        UsageWindow? fiveHour = null, weekly = null;
        foreach (var key in new[] { "primary", "secondary" })
        {
            if (!limits.TryGetProperty(key, out var window) || window.ValueKind != JsonValueKind.Object ||
                !window.TryGetProperty("usedPercent", out var usedNode) || usedNode.ValueKind != JsonValueKind.Number ||
                !usedNode.TryGetDouble(out var used)) continue;
            // windowDurationMins is optional in the protocol; without it, fall back to the slot's usual meaning.
            var minutes = window.TryGetProperty("windowDurationMins", out var duration) &&
                duration.ValueKind == JsonValueKind.Number && duration.TryGetInt32(out var declared)
                ? declared : key == "primary" ? 300 : 10080;
            DateTimeOffset? reset = null;
            if (window.TryGetProperty("resetsAt", out var resetNode) && resetNode.ValueKind == JsonValueKind.Number &&
                resetNode.TryGetInt64(out var seconds)) reset = DateTimeOffset.FromUnixTimeSeconds(seconds);
            var value = new UsageWindow(used, reset);
            if (minutes == 300) fiveHour = value;
            if (minutes == 10080) weekly = value;
        }
        return new(fiveHour, weekly);
    }
}
