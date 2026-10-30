using System.ComponentModel;
using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace TrayUsage;

// Floating-pill look and motion adapted from Oyenza Studio (MIT License, Copyright (c) 2026 Jatevo Labs,
// https://github.com/lucacadalora/oyenza-studio @ 6382a85be2fd149374558a5bcecd1426ede6c53b):
// fixed layered window sized for the largest surface, per-axis springs, hover lift, content that drops in
// and fades after the shape, outgoing content that shrinks away. See IslandMotion.cs and THIRD_PARTY_NOTICES.md.

internal enum IslandMode { Panel, Notice }

/// <summary>Classic hangs from the top edge with concave shoulders; Pill floats below it.</summary>
internal enum IslandTheme { Classic, Pill }

/// <summary>
/// Layered window that morphs between a small pill, a notice capsule and the tabbed panel.
/// Click-through except while the panel is up, when tabs and the wheel need the mouse.
/// </summary>
internal sealed class UsageIsland : IDisposable
{
    private const float TopGap = 8, Shoulder = 9, PillWidth = 120, PillHeight = 32, MaxOvershoot = 1.05f;
    private readonly IslandStyle _style = new();
    private readonly PanelSurface _panel;
    private readonly Stopwatch _clock = new();
    private readonly bool _animate = ShouldAnimate();
    // Every spring the island owns is listed in _all (shape, content, panel) or _knobs (one per switch); _every is both. The frame
    // loop advances, snaps and tests rest on _every, so a spring added to either list is looked after everywhere.
    private readonly Spring _w = Spring.Pixels(PillWidth), _h = Spring.Pixels(PillHeight), _r = Spring.Pixels(PillHeight / 2), _top = Spring.Pixels(0);
    private readonly Spring _contentAlpha = Spring.Unit(0), _ghostAlpha = Spring.Unit(0), _presence = Spring.Unit(0), _lift = Spring.Pixels(0);
    private readonly Spring _dock = Spring.Unit(1), _tabX = Spring.Pixels(0), _tabW = Spring.Pixels(0), _meter = Spring.Unit(1), _gear = Spring.Pixels(0);
    private readonly Spring[] _knobs = Enumerable.Range(0, PanelSurface.MaxSettingRows).Select(_ => Spring.Unit(0)).ToArray();
    private readonly float[] _knobValues = new float[PanelSurface.MaxSettingRows];
    private int _clockMinute = -1, _returnPage;
    private bool _pageSwitch, _ghostScales = true;
    private double _glowTime = -1, _usedPercent;
    private Color _glowColor = Color.Black;
    private float _glowRest;
    private bool _glowDouble;
    private readonly Spring[] _all, _every;
    private SynchronizationContext? _ui;
    private object? _pacer;
    private int _framePending;
    private IslandWindow? _window;
    private LayerSurface? _canvas;
    private IslandSurface? _surface;
    private Bitmap? _contentBitmap, _ghostBitmap;
    private IslandTheme _theme = IslandTheme.Classic;
    private SizeF _ghostSize;
    private Point _origin;
    private float _scale = 1;
    private bool _shown, _fineTimer, _meterArmed;
    private double _lastTick, _contentArm;
    private RectangleF _bodyPx;
    private PointF _contentOrigin;
    private int _wheel;

    public UsageIsland()
    {
        _panel = new PanelSurface(_style);
        _all = [_w, _h, _r, _top, _dock, _contentAlpha, _ghostAlpha, _presence, _lift, _tabX, _tabW, _meter, _gear];
        _every = [.. _all, .. _knobs];
    }

    private bool HasLiveIcons => _shown && _surface is { LiveIcons.Count: > 0 } or { LiveSpinners.Count: > 0 };

    /// <summary>
    /// A working session's logo moves every frame, on the same vsync pacer as the springs. Every step presents: the pacer
    /// waits on DwmFlush, which only returns when the compositor has a new frame, so a skipped frame would stall the loop.
    /// </summary>
    private void UpdateSpin()
    {
        if (_animate && Visible && HasLiveIcons) StartFrames();
    }

    public bool Visible { get; private set; }

    private int _panelPage = -1;

    private bool Docked => _theme != IslandTheme.Pill;
    private float RestTop => Docked ? 0 : TopGap;

    /// <summary>How docked the outline is right now (1 hangs from the edge with shoulders, 0 floats as a capsule); it moves with <see cref="_dock"/> when the theme changes.</summary>
    private Action? _themeLater;

    /// <summary>Starts the second half of a theme change once the first has got far enough: the body is on the edge, or the shoulders are gone.</summary>
    private void ReleaseThemeMove()
    {
        if (_themeLater is null) return;
        var ready = Docked ? _top.Value <= 0.5 : _dock.Value <= 0.5;
        if (!ready && _shown) return;
        _themeLater();
        _themeLater = null;
    }

    private float DockAmount => (float)Math.Clamp(_dock.Value, 0, 1);

    /// <summary>Where the closing pill slides to: past the point where its shadow clears the screen edge, so the spring crosses it instead of crawling in.</summary>
    private const float RetractTop = -(PillHeight + Shadow.Drop + Shadow.Reach + 24);

    public IslandTheme Theme
    {
        get => _theme;
        set
        {
            if (_theme == value) return;
            _theme = value;
            if (!Visible || _surface is null) return;
            // Changing theme under an open island is two motions, one after the other, content untouched. Pill to Classic: the capsule
            // rises until it sticks to the edge, and only then do its corners square off and the shoulders reach out to the sides.
            // Classic to Pill: the shoulders curl away and the corners round, and only then does the capsule drop from the edge.
            if (!_animate)
            {
                _dock.To(Docked ? 1 : 0, Motion.Morph);
                _dock.Snap();
                if (_shown) _top.To(RestTop, Motion.Morph);
                _top.Snap();
                _themeLater = null;
                if (_shown) Present(releaseCanvas: !KeepCanvas);
                return;
            }
            if (Docked)
            {
                if (_shown) _top.To(0, Motion.Morph);
                _themeLater = () => _dock.To(1, Motion.Morph);
            }
            else
            {
                _dock.To(0, Motion.Morph);
                _themeLater = _shown ? () => _top.To(TopGap, Motion.Morph) : null;
            }
            StartFrames();
        }
    }

    /// <summary>Fired when the user clicks a tab or scrolls, so the owner can hold off collapsing.</summary>
    public event Action? Interacted;

    /// <summary>The island finished hiding; its bitmaps are released, so the owner may collect them.</summary>
    public event Action? Hidden;

    /// <summary>Supplies the rows of the settings page; read when the panel opens and after each change.</summary>
    public Func<SettingRow[]>? SettingsProvider { get; set; }

    /// <summary>The user flipped a switch or picked an option: row id and the new value.</summary>
    public event Action<string, int>? SettingChanged;

    /// <summary>A notice was clicked.</summary>
    public event Action? NoticeClicked;

    /// <summary>The pointer left an open detail card.</summary>
    public event Action? DetailLeft;

    /// <summary>An activity row was clicked: the session to go back to.</summary>
    public event Action<SessionTarget>? SessionRequested;

    public NoticeData? CurrentNotice => _shown ? (_surface as NoticeSurface)?.Data : null;

    /// <summary>The card's "more" button was clicked: the owner opens the Done / History / Settings panel in its place.</summary>
    public event Action? MoreRequested;

    /// <summary>A notice's detail card is up (a click on it fires <see cref="NoticeClicked"/> too, to close it).</summary>
    public bool ShowingDetail => _shown && _surface is DetailSurface or ProgressSurface { Menu: false };

    private bool WantsHand => _shown && (_surface is NoticeSurface or DetailSurface or IdleSurface ||
        _surface is AskSurface { Hover: not AskHit.None } ||
        _surface is ProgressSurface { } card && (card.Hover != ProgressHit.None || card.HoverDot >= 0) ||
        _surface is PanelSurface { } p && (p.HoverTab >= 0 || p.HoverGear || p.HoverBackButton || p.HoverRow >= 0 || p.HoverRange >= 0));

    // The main menu is the card opened by the user (or by Caps Lock / hover) and the panel behind its "more" button; a card opened by a
    // notice is a notice, and goes with it.
    private bool IsMenu => _surface is PanelSurface or ProgressSurface { Menu: true };
    public IslandMode Mode => IsMenu ? IslandMode.Panel : IslandMode.Notice;
    public bool PanelActive => Visible && _shown && IsMenu;

    /// <summary>The visible body in screen pixels, or null when nothing is shown.</summary>
    public Rectangle? ScreenBody => Visible && _shown && !_bodyPx.IsEmpty
        ? Rectangle.Round(new RectangleF(_origin.X + _bodyPx.X, _origin.Y + _bodyPx.Y, _bodyPx.Width, _bodyPx.Height))
        : null;

    public void SetHistory(UsageHistory history) => _panel.SetHistory(history);

    private ProviderUsage? _claudeUsage, _codexUsage;

    public void SetUsage(ProviderUsage claude, ProviderUsage codex)
    {
        (_claudeUsage, _codexUsage) = (claude, codex);
        _usedPercent = new[] { claude.FiveHour?.UsedPercent, claude.Weekly?.UsedPercent, codex.FiveHour?.UsedPercent, codex.Weekly?.UsedPercent }.Max() ?? 0;
        if (_surface is ProgressSurface card && _shown)
        {
            card.SetUsage(claude, codex);
            SetGlow(card, pulse: false);
            Refresh();
        }
        else if (_surface is IdleSurface idle && _shown && idle.SetPercent(_usedPercent)) Refresh();
    }

    /// <summary>A click on the resting pill: the owner opens the main menu.</summary>
    public event Action? PillClicked;

    /// <summary>The user answered (or handed back) the approval or question on the island.</summary>
    public event Action<AskRequest, AskReply>? AskReplied;

    /// <summary>Shows an approval or a question that a hook is waiting on; the one already in view is left alone.</summary>
    public void ShowAsk(AskRequest request)
    {
        if (_shown && _surface is AskSurface current && current.Request.Id == request.Id) return;
        Display(new AskSurface(_style, request), null, crossfade: Visible && _shown);
    }

    /// <summary>
    /// The resting pill of the Pill theme: a small capsule that stays up while nothing else is shown, with a ring that fills with
    /// the heaviest usage. Notices and the menu morph out of it and back into it.
    /// </summary>
    public void ShowIdle()
    {
        if (_shown && _surface is IdleSurface current)
        {
            if (current.SetPercent(_usedPercent)) Refresh();
            return;
        }
        Display(new IdleSurface(_usedPercent), null, crossfade: Visible && _shown);
    }

    /// <summary>The sessions the card pages through (a running turn each, or the finished turn that is in view); an open card follows them.</summary>
    public void SetSessions(IReadOnlyList<NoticeData> pages)
    {
        _cardPages = pages;
        if (!_shown || _surface is not ProgressSurface card || !card.SetPages(pages)) return;
        SetGlow(card, pulse: false); // a turn may have just finished
        Refresh();
    }

    public void SetActivity(IReadOnlyList<CompletedTurn> recent)
    {
        var before = _panel.Size;
        _panel.SetActivity(recent);
        if (_surface != _panel || !_shown) return;
        AimTab(snap: false);
        if (_panel.Size != before) Display(_panel, null, crossfade: false);
        else Refresh();
        UpdateSpin();
    }

    /// <summary>Re-renders the activity page so its elapsed times keep counting.</summary>
    public void RefreshElapsed()
    {
        if (_surface != _panel || !_shown) return;
        var minute = DateTime.Now.Minute;
        var clockChanged = minute != _clockMinute && _panel.ShowClock;
        _clockMinute = minute;
        if (clockChanged) Refresh();
        UpdateSpin();
    }

    /// <summary>Reloads the settings rows; switches slide to their new state.</summary>
    public void RefreshSettings()
    {
        if (SettingsProvider is null) return;
        var rows = SettingsProvider();
        _panel.SetSettings(rows);
        var snap = !Visible || !_animate;
        for (var i = 0; i < _knobs.Length; i++)
        {
            _knobs[i].To(i < rows.Length && rows[i].Options.Length == 0 ? rows[i].Selected : 0, Motion.Slide);
            if (snap) _knobs[i].Snap();
        }
        for (var i = 0; i < _knobs.Length; i++) _knobValues[i] = (float)_knobs[i].Value;
        _panel.SetAnimation((float)_tabX.Value, (float)_tabW.Value, (float)_meter.Value, _knobValues, (float)_gear.Value);
        if (!Visible || !_shown || _surface != _panel) return;
        if (!snap) StartFrames();
        Refresh();
    }

    /// <summary>Opens the panel on the settings page (from the tray menu).</summary>
    public void ShowSettings(Screen? screen = null) => OpenPanel(screen, settings: true);

    /// <summary>Opens the Done / History panel (the card's "more" button).</summary>
    public void ShowMore() => OpenPanel(null, settings: false);

    /// <summary>Opens the main menu: the card, on the tab it was last left on.</summary>
    public void ShowPanel(Screen? screen = null)
    {
        if (_shown && _surface is ProgressSurface { Menu: true }) return;
        OpenCard(screen, menu: true, focus: null);
    }

    private IReadOnlyList<NoticeData> _cardPages = [];
    private int _cardTab = ProgressSurface.UsagePage;

    /// <summary>Opens the card on the page of <paramref name="focus"/>'s session, as a notice (a notice not among the sessions gets a page of its own).</summary>
    private void OpenCard(Screen? screen, bool menu, NoticeData? focus)
    {
        var pages = _cardPages;
        var index = 0;
        if (focus is not null)
        {
            var key = ProgressSurface.KeyOf(focus);
            index = pages.ToList().FindIndex(page => ProgressSurface.KeyOf(page) == key);
            if (index < 0)
            {
                pages = [focus, .. pages];
                index = 0;
            }
        }
        var card = new ProgressSurface(_style, pages, index, menu, menu ? _cardTab : ProgressSurface.ProgressPage);
        card.SetUsage(_claudeUsage, _codexUsage);
        Display(card, screen, crossfade: Visible && _shown);
    }

    private void OpenPanel(Screen? screen, bool settings)
    {
        var showing = Visible && _shown && _surface == _panel;
        if (settings)
        {
            if (_panel.Page != PanelSurface.SettingsPage) _returnPage = _panel.Page;
            _panel.Page = PanelSurface.SettingsPage;
        }
        else if (_panel.Page == PanelSurface.SettingsPage && !showing) _panel.Page = _returnPage; // settings are not the resting page
        RefreshSettings();
        _clockMinute = DateTime.Now.Minute;
        Display(_panel, screen, crossfade: Visible && _surface is NoticeSurface or ProgressSurface);
    }

    public void ShowNotice(NoticeData data)
    {
        if (_surface is NoticeSurface current && _shown && current.Type == data.Type)
        {
            var before = current.Size;
            current.Update(data);
            if (current.Size != before) Display(current, null, crossfade: false);
            else Refresh();
            return;
        }
        Display(new NoticeSurface(_style, data), null, crossfade: Visible && _shown);
    }

    /// <summary>Opens the notice into its card, morphing the capsule: the main card on its session for a running or finished turn, the detail card otherwise.</summary>
    public void ShowDetail(NoticeData data)
    {
        if (data.Type is NoticeType.Running or NoticeType.Completed) OpenCard(null, menu: false, focus: data);
        else Display(new DetailSurface(_style, data), null, crossfade: Visible && _shown);
    }

    /// <summary>The resting capsule lifts a little under the pointer.</summary>
    public void SetPointerOver(bool over)
    {
        var target = over && _shown && _surface is NoticeSurface or IdleSurface ? 1 : 0;
        if (target == _lift.Target || !Visible) return;
        _lift.To(target, Motion.Rise);
        if (!_animate) _lift.Snap();
        StartFrames();
    }

    public void Dismiss()
    {
        if (!Visible || !_shown) return;
        _shown = false;
        _w.To(PillWidth, Motion.Close); // closing is the smooth spring: no bounce on the way in
        _h.To(PillHeight, Motion.Close);
        _r.To(PillHeight / 2, Motion.Close);
        _glowTime = -1; // a pulse in flight would outlive the body as a halo
        // Closing, the card gathers into the pill as it rises into the screen edge: the content shrinks with the body and
        // fades slowly, and the island is gone once it has cleared the edge (see Step).
        _top.To(RetractTop, Motion.Close);
        _pageSwitch = false;
        _glowRest = 0;
        if (!(_animate && ToGhost())) _contentAlpha.To(0, Motion.Fade);
        _lift.To(0, Motion.Rise);
        _contentArm = 0;
        _meterArmed = false;
        SetInteractive(false);
        if (!_animate)
        {
            HideNow();
            return;
        }
        StartFrames();
    }

    private void Display(IslandSurface surface, Screen? screen, bool crossfade)
    {
        var wasVisible = Visible;
        if (!Visible)
        {
            screen ??= Screen.FromHandle(GetForegroundWindow());
            var scale = MonitorScale(screen.Bounds);
            if (Math.Abs(scale - _scale) > 0.001f)
            {
                _scale = scale;
                DisposeBitmaps();
                _canvas?.Dispose();
                _canvas = null;
            }
            var canvas = CanvasSize;
            _origin = new Point(screen.Bounds.Left + (screen.Bounds.Width - canvas.Width) / 2, screen.Bounds.Top);
            foreach (var spring in _all) spring.Jump(0);
            _w.Jump(PillWidth);
            _h.Jump(PillHeight);
            _r.Jump(PillHeight / 2);
            _dock.Jump(Docked ? 1 : 0);
            _themeLater = null;
            _meter.Jump(1);
            DisposeBitmaps();
            _window ??= new IslandWindow(this);
            Visible = true;
            crossfade = false;
            _window.ShowTopmost();
        }
        else if (crossfade && ToGhost())
        {
            // The old content fades and shrinks away while the new one fades in. Between two pages of the same panel
            // it only fades in place, and the header stays put: scaled or shifted text would shimmer.
            _pageSwitch = surface == _surface && surface is PanelSurface or DetailSurface or ProgressSurface;
            _ghostScales = !_pageSwitch;
        }
        else if (crossfade || _surface != surface)
        {
            _contentBitmap?.Dispose();
            _contentBitmap = null;
        }

        var size = surface.Size;
        var growing = size.Width > _w.Value || size.Height > _h.Value;
        var previous = _surface;
        _surface = surface;
        _shown = true;
        UpdateSpin();
        SetGlow(surface, pulse: previous != surface && surface is NoticeSurface or AskSurface);
        // opening bounces, morphing between surfaces is snappy, another page of the same surface swings past on the way in as well
        var morph = !wasVisible ? Motion.Open : crossfade && previous == surface ? Motion.Page : Motion.Morph;
        _w.To(size.Width, morph);
        _h.To(size.Height, morph);
        _r.To(surface.Radius, morph);
        _top.To(RestTop, Motion.Rise);
        _presence.To(1, Motion.Fade);
        if (_contentAlpha.Value < 1 || crossfade)
        {
            _contentAlpha.To(0, Motion.Fade);
            _contentArm = growing ? Motion.GrowDelay : Motion.ShrinkDelay;
        }
        SetInteractive(true); // a notice answers a click, so its body takes the mouse (the rest of the window stays transparent)
        if (surface is PanelSurface panel)
        {
            var opening = !wasVisible || _panelPage != panel.Page;
            AimTab(snap: !wasVisible);
            _panelPage = panel.Page;
            // The usage meters and the history curves fill in after the shape has settled enough to read them.
            if (opening && panel.Page == PanelSurface.HistoryPage && _animate)
            {
                _meter.Jump(0);
                _meterArmed = true;
            }
        }

        if (!_animate)
        {
            foreach (var spring in _all) spring.Snap();
            _contentAlpha.Jump(1);
            _ghostAlpha.Jump(0);
            _contentArm = 0;
            Present(releaseCanvas: true);
            return;
        }
        StartFrames();
    }

    /// <summary>
    /// Colour glow around the body: a steady tint while something needs attention, plus a 1.8 s pulse when a notice
    /// arrives (double for warnings), as Agent HUD flashes the rim. The panel glows only once a quota passes 70 %.
    /// </summary>
    private void SetGlow(IslandSurface surface, bool pulse)
    {
        switch (surface)
        {
            case NoticeSurface { Type: not NoticeType.Running } notice:
                _glowColor = NoticeLook.Accent(notice.Data);
                _glowRest = 0.3f;
                _glowDouble = notice.Type is NoticeType.Warning or NoticeType.Exhausted or NoticeType.Approval;
                break;
            case AskSurface ask:
                _glowColor = ask.Request.IsQuestion ? NoticeLook.Question : NoticeLook.Approval;
                _glowRest = 0.3f;
                _glowDouble = !ask.Request.IsQuestion;
                break;
            case DetailSurface { Data.Type: not NoticeType.Running } detail:
                _glowColor = NoticeLook.Accent(detail.Data);
                _glowRest = 0.3f;
                _glowDouble = false;
                break;
            case ProgressSurface { Menu: false, Current.Type: NoticeType.Completed }:
                _glowColor = IslandStyle.Success;
                _glowRest = 0.3f;
                _glowDouble = false;
                break;
            case ProgressSurface { Menu: true } when _usedPercent >= 70:
                _glowColor = IslandStyle.Level(_usedPercent);
                _glowRest = 0.22f;
                _glowDouble = false;
                break;
            case PanelSurface when _usedPercent >= 70:
                _glowColor = IslandStyle.Level(_usedPercent);
                _glowRest = 0.22f;
                _glowDouble = false;
                break;
            default:
                _glowRest = 0;
                break;
        }
        if (pulse && _glowRest > 0 && _animate) _glowTime = 0;
        if (_glowTime >= 0) StartFrames();
    }

    private float GlowIntensity()
    {
        var pulse = 0.0;
        if (_glowTime >= 0)
        {
            var phase = _glowTime / Motion.GlowSeconds * Math.PI * (_glowDouble ? 2 : 1);
            pulse = Math.Pow(Math.Sin(phase), 2) * 0.9;
        }
        return (float)(_glowRest + pulse);
    }

    private void AimTab(bool snap)
    {
        if (_panel.Page < PanelSurface.SettingsPage)
        {
            var tab = _panel.ActiveTabRect;
            _tabX.To(tab.X, Motion.Slide);
            _tabW.To(tab.Width, Motion.Slide);
        }
        _gear.To(_panel.Page == PanelSurface.SettingsPage ? 90 : 0, Motion.Slide);
        if (snap || !_animate)
        {
            _tabX.Snap();
            _tabW.Snap();
            _gear.Snap();
        }
        else if (Visible) StartFrames();
    }

    /// <summary>Hands the current content to the outgoing layer, which shrinks with the body and fades.</summary>
    private bool ToGhost()
    {
        if (_contentBitmap is null || _surface is null || _contentAlpha.Value <= 0.02) return false;
        _ghostBitmap?.Dispose();
        _ghostBitmap = _contentBitmap;
        _contentBitmap = null;
        _ghostSize = _surface.Size;
        _ghostScales = true;
        _ghostAlpha.Jump(_contentAlpha.Value);
        _ghostAlpha.To(0, Motion.Fade);
        _contentAlpha.Jump(0);
        return true;
    }

    private void Refresh()
    {
        _contentBitmap?.Dispose();
        _contentBitmap = null;
        if (_pacer is null && Visible && _shown) Present(releaseCanvas: !KeepCanvas);
    }

    private void StartFrames()
    {
        if (!_animate || _pacer is not null) return;
        _ui = SynchronizationContext.Current;
        if (_ui is null) return;
        _clock.Restart();
        _lastTick = 0;
        if (!_fineTimer) _fineTimer = TimeBeginPeriod(1) == 0;
        // Frames are paced by the compositor: DwmFlush returns once per display refresh, so every
        // step lands on a vsync (any refresh rate) instead of drifting against a 15 ms timer.
        var token = new object();
        _pacer = token;
        new Thread(() =>
        {
            while (ReferenceEquals(_pacer, token))
            {
                if (DwmFlush() != 0) Thread.Sleep(8);
                if (Interlocked.Exchange(ref _framePending, 1) == 0)
                    _ui.Post(_ =>
                    {
                        Interlocked.Exchange(ref _framePending, 0);
                        if (ReferenceEquals(_pacer, token)) Step();
                    }, null);
            }
        }) { IsBackground = true, Name = "island-vsync" }.Start();
    }

    private void Step()
    {
        var now = _clock.Elapsed.TotalSeconds;
        var elapsed = Math.Min(now - _lastTick, Motion.MaxFrame);
        _lastTick = now;
        for (var remaining = elapsed; remaining > 0; remaining -= Motion.Substep)
        {
            var dt = Math.Min(Motion.Substep, remaining);
            foreach (var spring in _every) spring.Advance(dt);
        }
        ReleaseThemeMove();
        if (_glowTime >= 0 && (_glowTime += elapsed) >= Motion.GlowSeconds) _glowTime = -1;
        if (_pageSwitch && _contentAlpha.Value >= 0.999 && _contentArm <= 0) _pageSwitch = false;
        if (_contentArm > 0 && (_contentArm -= elapsed) <= 0)
        {
            _contentAlpha.To(1, Motion.Fade);
            if (_meterArmed) _meter.To(1, Motion.Fill);
            _meterArmed = false;
        }
        // Closing: the body (and its shadow) has risen fully above the screen edge.
        if (!_shown && _top.Target < 0 && _top.Value <= -(_h.Value + Shadow.Drop + Shadow.Reach))
        {
            StopFrames();
            HideNow();
            return;
        }
        var springsSettled = _contentArm <= 0 && !_meterArmed && _glowTime < 0 && _themeLater is null && _every.All(s => s.IsSettled);
        var live = HasLiveIcons;
        var settled = springsSettled && !live;
        if (springsSettled)
            foreach (var spring in _every) spring.Snap();
        for (var i = 0; i < _knobs.Length; i++) _knobValues[i] = (float)_knobs[i].Value;
        if (_surface is PanelSurface animated && animated.SetAnimation((float)_tabX.Value, (float)_tabW.Value, (float)_meter.Value, _knobValues, (float)_gear.Value))
        {
            _contentBitmap?.Dispose();
            _contentBitmap = null;
        }
        Present(releaseCanvas: settled && !KeepCanvas);
        if (settled) StopFrames();
    }

    private void StopFrames()
    {
        _pacer = null;
        if (_fineTimer) TimeEndPeriod(1);
        _fineTimer = false;
        _ghostBitmap?.Dispose();
        _ghostBitmap = null;
    }

    /// <summary>While a panel or card is up its canvas stays: reallocating it for the closing frames costs the first frames of the close.</summary>
    private bool KeepCanvas => _shown && _surface is not (NoticeSurface or IdleSurface);

    private void Present(bool releaseCanvas)
    {
        if (_window is null) return;
        var size = CanvasSize;
        if (_canvas?.Size != size)
        {
            _canvas?.Dispose();
            _canvas = new LayerSurface(size);
        }
        using (var graphics = Graphics.FromImage(_canvas.Image)) Draw(graphics);
        _window.Update(_canvas, _origin);
        if (releaseCanvas)
        {
            // The layered window keeps its own copy of the last frame.
            _canvas.Dispose();
            _canvas = null;
        }
    }

    private Size CanvasSize => new((int)Math.Ceiling((Math.Max(PanelSurface.MaxWidth, ProgressSurface.CardWidth) * MaxOvershoot + (Shadow.Margin + Shoulder) * 2) * _scale),
        (int)Math.Ceiling((TopGap + Math.Max(PanelSurface.MaxHeight, ProgressSurface.CardHeight) * MaxOvershoot + Shadow.Margin) * _scale));

    private void Draw(Graphics g)
    {
        g.CompositingMode = CompositingMode.SourceCopy;
        g.Clear(Color.Transparent);
        g.CompositingMode = CompositingMode.SourceOver;
        var presence = (float)Math.Clamp(_presence.Value, 0, 1);
        if (presence <= 0.004f || _surface is null)
        {
            _bodyPx = RectangleF.Empty;
            return;
        }
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;

        var s = _scale;
        var lift = (float)Math.Clamp(_lift.Value, 0, 1.2);
        var width = ((float)_w.Value + Motion.LiftSize * lift) * s;
        var height = ((float)_h.Value + Motion.LiftSize * lift) * s;
        var dock = DockAmount;
        var top = ((float)_top.Value - Motion.LiftTop * lift * (1 - dock)) * s; // hanging islands stay on the edge
        var body = new RectangleF((CanvasSize.Width - width) / 2, top, width, height);
        var radius = Math.Clamp(((float)_r.Value + Motion.LiftRadius * lift) * s, 0, Math.Min(width, height) / 2);
        _bodyPx = body;
        // Shoulders curl away over the first half of the dock spring, the top corners round over the second: never both at once.
        var shoulder = Shoulder * s * Math.Clamp(dock * 2 - 1, 0, 1);
        var topRound = radius * Math.Clamp(1 - dock * 2, 0, 1);

        foreach (var (inflate, alpha) in Shadow.Layers)
        {
            var spread = inflate * s;
            var halo = RectangleF.Inflate(body, spread, spread);
            halo.Offset(0, Shadow.Drop * s);
            using var haloPath = Outline(halo, radius + spread, shoulder > 0 ? shoulder + spread : 0, topRound > 0 ? topRound + spread : 0);
            using var haloBrush = new SolidBrush(Color.FromArgb((int)Math.Round(255 * alpha * presence), 0, 0, 0));
            g.FillPath(haloBrush, haloPath);
        }
        var glow = GlowIntensity() * presence;
        if (glow > 0.004f)
        {
            foreach (var (inflate, alpha) in Shadow.Layers)
            {
                var spread = inflate * s;
                var halo = RectangleF.Inflate(body, spread, spread);
                using var haloPath = Outline(halo, radius + spread, shoulder > 0 ? shoulder + spread : 0, topRound > 0 ? topRound + spread : 0);
                using var haloBrush = new SolidBrush(Color.FromArgb((int)Math.Round(255 * Math.Min(1, alpha * 1.3f * glow)), _glowColor));
                g.FillPath(haloBrush, haloPath);
            }
        }
        using var bodyPath = Outline(body, radius, shoulder, topRound);
        using (var fill = new SolidBrush(Color.FromArgb((int)Math.Round(255 * presence), 0, 0, 0)))
            g.FillPath(fill, bodyPath);

        DrawContent(g, body, radius, lift);

        using var rimPath = Outline(RectangleF.Inflate(body, -0.5f * s, -0.5f * s), radius - 0.5f * s, shoulder, topRound > 0 ? topRound - 0.5f * s : 0, closed: false);
        using var rim = new Pen(Color.FromArgb((int)(30 * presence), 255, 255, 255), Math.Max(1, s));
        g.DrawPath(rim, rimPath);
    }

    private void DrawContent(Graphics g, RectangleF body, float radius, float lift)
    {
        var s = _scale;
        var inset = 1.5f * s;
        using var clip = _dock.Value > 0.001 || Docked
            ? IslandStyle.DockedPath(new RectangleF(body.X + inset, body.Y + inset * (1 - DockAmount), body.Width - 2 * inset, body.Height - inset * (2 - DockAmount)),
                radius - inset, 0, true, Math.Max(0, radius - inset) * Math.Clamp(1 - DockAmount * 2, 0, 1))
            : IslandStyle.ContinuousRect(RectangleF.Inflate(body, -inset, -inset), radius - inset);
        g.SetClip(clip);

        var headerPx = (int)Math.Ceiling((_surface?.FixedTop ?? 0) * s);
        var pageSwitch = _pageSwitch && _surface is PanelSurface or DetailSurface or ProgressSurface;
        if (_ghostBitmap is not null && _ghostAlpha.Value > 0.004 && pageSwitch)
        {
            // Same panel, other page: the old body fades where it stands, pixel for pixel.
            var x = (float)Math.Round(body.X + body.Width / 2 - _ghostBitmap.Width / 2f);
            var y = (float)Math.Round(body.Top);
            DrawFaded(g, _ghostBitmap, new RectangleF(x, y + headerPx, _ghostBitmap.Width, _ghostBitmap.Height - headerPx), _ghostAlpha.Value,
                new RectangleF(0, headerPx, _ghostBitmap.Width, _ghostBitmap.Height - headerPx));
        }
        else if (_ghostBitmap is not null && _ghostAlpha.Value > 0.004)
        {
            // Outgoing content never enlarges: it scales with the smaller of the width and height ratios.
            var scale = _ghostScales ? Math.Clamp(Math.Min(body.Width / (_ghostSize.Width * s), body.Height / (_ghostSize.Height * s)), Motion.GhostMinScale, 1f) : 1f;
            var (gw, gh) = (_ghostBitmap.Width * scale, _ghostBitmap.Height * scale);
            var pivotY = body.Top + Math.Min(body.Height, _ghostBitmap.Height) / 2;
            DrawBlurFaded(g, _ghostBitmap, _ghostBlur, new RectangleF(body.X + body.Width / 2 - gw / 2, pivotY - gh / 2, gw, gh), _ghostAlpha.Value, _ghostAlpha.Value);
        }

        var alpha = Math.Clamp(_contentAlpha.Value, 0, 1);
        if (_surface is not null && (alpha > 0.004 || pageSwitch))
        {
            var bitmap = _contentBitmap ??= _surface.Render(_style, s);
            // Whole pixels only: ClearType text resampled at a fractional offset shimmers while it moves.
            var drop = pageSwitch ? 0 : (float)Math.Round(Motion.ContentDrop * s * (1 - alpha));
            // The resting capsule swells under the pointer; its content stays in the middle of it, not pinned to the top.
            var swell = Motion.LiftSize * lift * s / 2;
            var origin = new PointF((float)Math.Round(body.X + body.Width / 2 - bitmap.Width / 2f), (float)Math.Round(body.Top + swell + drop));
            _contentOrigin = origin;
            if (pageSwitch)
            {
                // The header is the new page's from the first frame; only the body cross-fades.
                g.CompositingMode = CompositingMode.SourceCopy;
                g.DrawImage(bitmap, new Rectangle((int)origin.X, (int)origin.Y, bitmap.Width, headerPx), 0, 0, bitmap.Width, headerPx, GraphicsUnit.Pixel);
                g.CompositingMode = CompositingMode.SourceOver;
                if (alpha > 0.004)
                    DrawFaded(g, bitmap, new RectangleF(origin.X, origin.Y + headerPx, bitmap.Width, bitmap.Height - headerPx), alpha,
                        new RectangleF(0, headerPx, bitmap.Width, bitmap.Height - headerPx));
            }
            else if (alpha >= 0.999 && drop < 0.01f)
            {
                g.CompositingMode = CompositingMode.SourceCopy;
                g.DrawImage(bitmap, new Rectangle((int)origin.X, (int)origin.Y, bitmap.Width, bitmap.Height), 0, 0, bitmap.Width, bitmap.Height, GraphicsUnit.Pixel);
                g.CompositingMode = CompositingMode.SourceOver;
            }
            else
            {
                // Content enters the way the Dynamic Island's does: blurred, squeezed to EnterSqueeze of its height from the top, fading in.
                DrawBlurFaded(g, bitmap, _contentBlur, new RectangleF(origin.X, origin.Y, bitmap.Width, bitmap.Height * (Motion.EnterSqueeze + (1 - Motion.EnterSqueeze) * (float)alpha)), alpha, alpha);
            }
        }
        if (_surface is { LiveIcons.Count: > 0 } && alpha > 0.004) DrawLiveIcons(g, alpha);
        if (_surface is { LiveSpinners.Count: > 0 } && alpha > 0.004) DrawLiveSpinners(g, alpha);
        g.ResetClip();
    }

    /// <summary>A working turn's spinner: a bare ring with the arc sweeping round it, in the agent's colour.</summary>
    private void DrawLiveSpinners(Graphics g, double alpha)
    {
        var s = _scale;
        var saved = g.Save();
        g.ResetTransform();
        foreach (var (rect, color) in _surface!.LiveSpinners)
        {
            var cx = _contentOrigin.X + (rect.X + rect.Width / 2) * s;
            var cy = _contentOrigin.Y + (rect.Y + rect.Height / 2) * s;
            DrawSweep(g, cx, cy, rect.Width / 2 * s, s, color, (int)Math.Round(255 * alpha));
        }
        g.Restore(saved);
    }

    /// <summary>
    /// A working session, live: its logo with a ring sweeping round it, drawn every frame so it stays smooth. Own drawing;
    /// Agent HUD's bots and Clawd are not used.
    /// </summary>
    private void DrawLiveIcons(Graphics g, double alpha)
    {
        var s = _scale;
        var saved = g.Save();
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        using var attributes = new ImageAttributes();
        attributes.SetColorMatrix(new ColorMatrix { Matrix33 = (float)alpha });
        attributes.SetWrapMode(WrapMode.TileFlipXY);
        foreach (var (rect, kind, gap) in _surface!.LiveIcons)
        {
            var cx = _contentOrigin.X + (rect.X + rect.Width / 2) * s;
            var cy = _contentOrigin.Y + (rect.Y + rect.Height / 2) * s;
            g.ResetTransform();
            var image = _style.Icon(kind);
            g.DrawImage(image, new Rectangle((int)Math.Round(cx - rect.Width * s / 2), (int)Math.Round(cy - rect.Height * s / 2), (int)Math.Round(rect.Width * s), (int)Math.Round(rect.Height * s)),
                0, 0, image.Width, image.Height, GraphicsUnit.Pixel, attributes);
            DrawSweep(g, cx, cy, (rect.Width / 2 + gap) * s, s, Agent.Accent(kind), (int)Math.Round(255 * alpha));
        }
        g.Restore(saved);
    }

    /// <summary>A ring with an arc that circles once every <see cref="Motion.SweepSeconds"/> and stretches and shrinks as it goes.</summary>
    private static void DrawSweep(Graphics g, float cx, float cy, float radius, float scale, Color accent, int alpha)
    {
        const long period = (long)(Motion.SweepSeconds * 1000);
        var turn = Environment.TickCount64 % period / (double)period;
        var length = 36 + 44 * (0.5 - 0.5 * Math.Cos(turn * 2 * Math.PI)); // 36..80 degrees
        var ring = new RectangleF(cx - radius, cy - radius, 2 * radius, 2 * radius);
        using var track = new Pen(Color.FromArgb(alpha * 40 / 255, 255, 255, 255), 2f * scale);
        using var sweep = new Pen(Color.FromArgb(alpha, accent), 2f * scale) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        g.DrawEllipse(track, ring);
        g.DrawArc(sweep, ring, (float)(turn * 360 - 90), (float)length);
    }

    private GraphicsPath Outline(RectangleF rect, float radius, float shoulder, float topRound, bool closed = true) =>
        _dock.Value > 0.001 || Docked ? IslandStyle.DockedPath(rect, radius, shoulder, closed, topRound) : IslandStyle.ContinuousRect(rect, radius);

    /// <summary>A bitmap and its blurred copy (the bitmap shrunk, which smooths when drawn large again; GDI+ has no blur).</summary>
    private sealed class BlurCache : IDisposable
    {
        private Bitmap? _source, _small;

        public Bitmap Of(Bitmap source)
        {
            if (ReferenceEquals(source, _source) && _small is not null) return _small;
            _small?.Dispose();
            _small = new Bitmap(Math.Max(1, source.Width / 6), Math.Max(1, source.Height / 6));
            using (var g = Graphics.FromImage(_small))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                g.DrawImage(source, new Rectangle(0, 0, _small.Width, _small.Height), 0, 0, source.Width, source.Height, GraphicsUnit.Pixel);
            }
            _source = source;
            return _small;
        }

        public void Dispose()
        {
            _small?.Dispose();
            _small = null;
            _source = null;
        }
    }

    private readonly BlurCache _contentBlur = new(), _ghostBlur = new();

    /// <summary>Draws <paramref name="image"/> at <paramref name="opacity"/>, sharp only as <paramref name="focus"/> (0..1) rises: it comes into focus as it fades in and loses it as it fades out.</summary>
    private static void DrawBlurFaded(Graphics g, Bitmap image, BlurCache cache, RectangleF target, double opacity, double focus)
    {
        var sharp = focus * focus * (3 - 2 * focus);
        if (sharp < 0.999) DrawFaded(g, cache.Of(image), target, opacity * (1 - sharp));
        if (sharp > 0.001) DrawFaded(g, image, target, opacity * sharp);
    }

    private static void DrawFaded(Graphics g, Image image, RectangleF target, double opacity, RectangleF? source = null)
    {
        using var attributes = new ImageAttributes();
        attributes.SetColorMatrix(new ColorMatrix { Matrix33 = (float)opacity });
        attributes.SetWrapMode(WrapMode.TileFlipXY);
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.DrawImage(image, [target.Location, new PointF(target.Right, target.Top), new PointF(target.Left, target.Bottom)],
            source ?? new RectangleF(0, 0, image.Width, image.Height), GraphicsUnit.Pixel, attributes);
    }

    private void HideNow()
    {
        _window?.Hide();
        Visible = false;
        _shown = false;
        _surface = null;
        _bodyPx = RectangleF.Empty;
        _wheel = 0;
        DisposeBitmaps();
        _canvas?.Dispose();
        _canvas = null;
        Hidden?.Invoke();
    }

    private void DisposeBitmaps()
    {
        _contentBlur.Dispose();
        _ghostBlur.Dispose();
        _contentBitmap?.Dispose();
        _contentBitmap = null;
        _ghostBitmap?.Dispose();
        _ghostBitmap = null;
    }

    // ---- mouse (only delivered while the panel is up) ----

    private void SetInteractive(bool interactive) => _window?.SetInteractive(interactive);

    private bool HitBody(Point client) => _bodyPx.Contains(client);

    private void OnMouse(int message, Point client, int delta)
    {
        if (_surface is ProgressSurface card)
        {
            // The card answers its buttons, the two tabs and the pager (dots and wheel). Opened as a notice it goes away with the
            // pointer, or with its close button once the turn is done; as the main menu it is left to the hover logic and the timers.
            var spot = new PointF((client.X - _contentOrigin.X) / _scale, (client.Y - _contentOrigin.Y) / _scale);
            switch (message)
            {
                case 0x0200 when _shown: // WM_MOUSEMOVE
                    var over = card.HitAt(spot, out var overDot);
                    if (over != card.Hover || overDot != card.HoverDot)
                    {
                        card.Hover = over;
                        card.HoverDot = overDot;
                        Refresh();
                    }
                    break;
                case 0x0201 when _shown: // WM_LBUTTONDOWN
                    Interacted?.Invoke();
                    switch (card.HitAt(spot, out var dot))
                    {
                        case ProgressHit.Close: NoticeClicked?.Invoke(); break;
                        case ProgressHit.Jump:
                            if (card.Current?.Target is { } target) SessionRequested?.Invoke(target);
                            break;
                        case ProgressHit.More: MoreRequested?.Invoke(); break;
                        case ProgressHit.ProgressTab: SetCardTab(card, ProgressSurface.ProgressPage); break;
                        case ProgressHit.UsageTab: SetCardTab(card, ProgressSurface.UsagePage); break;
                        case ProgressHit.Dot: SetCardPage(card, dot); break;
                    }
                    break;
                case 0x020A when _shown && card.Page == ProgressSurface.ProgressPage && card.PageCount > 1: // WM_MOUSEWHEEL
                    _wheel += delta;
                    if (Math.Abs(_wheel) >= 120)
                    {
                        Interacted?.Invoke();
                        SetCardPage(card, card.PageIndex + (_wheel < 0 ? 1 : -1));
                        _wheel = 0;
                    }
                    break;
                case 0x02A3 when _shown: // WM_MOUSELEAVE
                    if (card.Hover != ProgressHit.None || card.HoverDot >= 0)
                    {
                        card.Hover = ProgressHit.None;
                        card.HoverDot = -1;
                        Refresh();
                    }
                    if (!card.Menu) DetailLeft?.Invoke();
                    break;
            }
            return;
        }
        if (_surface is AskSurface ask)
        {
            var spot = new PointF((client.X - _contentOrigin.X) / _scale, (client.Y - _contentOrigin.Y) / _scale);
            switch (message)
            {
                case 0x0200 when _shown: // WM_MOUSEMOVE
                    var over = ask.HitAt(spot, out var overOption);
                    if (over != ask.Hover || overOption != ask.HoverOption)
                    {
                        ask.Hover = over;
                        ask.HoverOption = overOption;
                        Refresh();
                    }
                    break;
                case 0x02A3 when _shown: // WM_MOUSELEAVE
                    if (ask.Hover != AskHit.None)
                    {
                        ask.Hover = AskHit.None;
                        ask.HoverOption = -1;
                        Refresh();
                    }
                    break;
                case 0x0201 when _shown: // WM_LBUTTONDOWN
                    Interacted?.Invoke();
                    switch (ask.HitAt(spot, out var option))
                    {
                        case AskHit.Allow: AskReplied?.Invoke(ask.Request, new("allow")); break;
                        case AskHit.Deny: AskReplied?.Invoke(ask.Request, new("deny")); break;
                        case AskHit.Terminal: AskReplied?.Invoke(ask.Request, new("terminal")); break;
                        case AskHit.Option:
                            if (ask.Choose(option)) AskReplied?.Invoke(ask.Request, new("answer", ask.Answers));
                            else Display(ask, null, crossfade: true); // the next question
                            break;
                    }
                    break;
            }
            return;
        }
        if (_surface is DetailSurface detail)
        {
            // The card has one or two screens: a click moves on, and on the last one closes it; the wheel switches screens.
            if (message == 0x0201 && _shown)
            {
                Interacted?.Invoke();
                if (detail.Page + 1 < detail.PageCount) SetDetailPage(detail, detail.Page + 1);
                else NoticeClicked?.Invoke();
            }
            else if (message == 0x02A3 && _shown) DetailLeft?.Invoke(); // the pointer left the card
            else if (message == 0x020A)
            {
                _wheel += delta;
                if (Math.Abs(_wheel) >= 120)
                {
                    Interacted?.Invoke();
                    SetDetailPage(detail, detail.Page + (_wheel < 0 ? 1 : -1));
                    _wheel = 0;
                }
            }
            return;
        }
        if (_surface is IdleSurface)
        {
            if (message == 0x0201 && _shown)
            {
                Interacted?.Invoke();
                PillClicked?.Invoke();
            }
            return;
        }
        if (_surface is NoticeSurface)
        {
            if (message == 0x0201 && _shown)
            {
                Interacted?.Invoke();
                NoticeClicked?.Invoke();
            }
            return;
        }
        if (_surface is not PanelSurface panel) return;
        var point = new PointF((client.X - _contentOrigin.X) / _scale, (client.Y - _contentOrigin.Y) / _scale);
        switch (message)
        {
            case 0x0200: // WM_MOUSEMOVE
                var hover = panel.TabAt(point);
                var hoverRow = panel.SettingRowAt(point);
                var hoverGear = panel.GearAt(point);
                var hoverRange = panel.RangeAt(point);
                var hoverBack = PanelSurface.BackAt(point);
                if (hover != panel.HoverTab || hoverRow != panel.HoverRow || hoverGear != panel.HoverGear || hoverRange != panel.HoverRange || hoverBack != panel.HoverBackButton)
                {
                    panel.HoverTab = hover;
                    panel.HoverRow = hoverRow;
                    panel.HoverGear = hoverGear;
                    panel.HoverRange = hoverRange;
                    panel.HoverBackButton = hoverBack;
                    Refresh();
                }
                break;
            case 0x02A3: // WM_MOUSELEAVE
                if (panel.HoverTab != -1 || panel.HoverRow != -1 || panel.HoverGear || panel.HoverRange != -1 || panel.HoverBackButton)
                {
                    panel.HoverTab = -1;
                    panel.HoverRow = -1;
                    panel.HoverGear = false;
                    panel.HoverRange = -1;
                    panel.HoverBackButton = false;
                    Refresh();
                }
                break;
            case 0x0201: // WM_LBUTTONDOWN
                if (PanelSurface.BackAt(point))
                {
                    Interacted?.Invoke();
                    panel.HoverBackButton = false;
                    OpenCard(null, menu: true, focus: null);
                }
                else if (panel.TargetAt(panel.SettingRowAt(point)) is { } session)
                {
                    Interacted?.Invoke();
                    SessionRequested?.Invoke(session);
                }
                else if (panel.GearAt(point)) SetPage(panel.Page == PanelSurface.SettingsPage ? _returnPage : PanelSurface.SettingsPage);
                else if (panel.TabAt(point) is >= 0 and var tab) SetPage(tab);
                else if (panel.RangeAt(point) is >= 0 and var range) SetHistoryRange(panel, range == 1);
                else if (panel.SettingAt(point) is { } change)
                {
                    Interacted?.Invoke();
                    SettingChanged?.Invoke(change.Row.Id, change.Value);
                }
                break;
            case 0x020A: // WM_MOUSEWHEEL
                _wheel += delta;
                if (Math.Abs(_wheel) >= 120)
                {
                    if (panel.Page != PanelSurface.SettingsPage) SetPage(Math.Clamp(panel.Page + (_wheel < 0 ? 1 : -1), 0, PanelSurface.SettingsPage - 1));
                    _wheel = 0;
                }
                break;
        }
    }

    /// <summary>Switches the card between 進捗 and 使用状況; the main menu remembers the tab it was left on.</summary>
    private void SetCardTab(ProgressSurface card, int tab)
    {
        if (card.Page == tab) return;
        card.Page = tab;
        if (card.Menu) _cardTab = tab;
        Display(card, null, crossfade: true);
    }

    /// <summary>Pages to another session.</summary>
    private void SetCardPage(ProgressSurface card, int index)
    {
        if (!card.GoTo(index)) return;
        SetGlow(card, pulse: false); // the page may be a finished turn, which glows
        Display(card, null, crossfade: true);
    }

    private void SetDetailPage(DetailSurface detail, int page)
    {
        page = Math.Clamp(page, 0, detail.PageCount - 1);
        if (page == detail.Page) return;
        detail.Page = page;
        Display(detail, null, crossfade: true);
    }

    /// <summary>Switches the history chart between the 5-hour and weekly windows; the curves draw in again.</summary>
    private void SetHistoryRange(PanelSurface panel, bool weekly)
    {
        Interacted?.Invoke();
        if (panel.HistoryWeekly == weekly) return;
        panel.HistoryWeekly = weekly;
        if (_animate)
        {
            _meter.Jump(0);
            _meter.To(1, Motion.Fill);
            StartFrames();
        }
        Refresh();
    }

    private void SetPage(int page)
    {
        page = Math.Clamp(page, 0, PanelSurface.PageCount - 1);
        Interacted?.Invoke();
        if (page == _panel.Page) return;
        if (page == PanelSurface.SettingsPage) _returnPage = _panel.Page;
        _panel.Page = page;
        _panel.HoverTab = -1;
        _panel.HoverRow = -1;
        _panel.HoverRange = -1;
        AimTab(snap: false);
        Display(_panel, null, crossfade: true);
    }

    public void Dispose()
    {
        StopFrames();
        DisposeBitmaps();
        _canvas?.Dispose();
        _window?.DestroyHandle();
        _style.Dispose();
    }

    public static float MonitorScale(Rectangle bounds)
    {
        var monitor = MonitorFromPoint(new POINT(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2), 2);
        return GetDpiForMonitor(monitor, 0, out var dpi, out _) == 0 && dpi > 0 ? dpi / 96f : 1f;
    }

    private static bool ShouldAnimate() =>
        SystemParametersInfo(0x1042, 0, out var enabled, 0) && enabled;

    private sealed class IslandWindow : NativeWindow
    {
        private const int ExTransparent = 0x00000020;
        private readonly UsageIsland _owner;
        private bool _interactive, _tracking;

        public IslandWindow(UsageIsland owner)
        {
            _owner = owner;
            CreateHandle(new CreateParams
            {
                Style = unchecked((int)0x80000000), // WS_POPUP
                ExStyle = 0x00080000 | ExTransparent | 0x00000080 | 0x08000000 | 0x00000008 // LAYERED, TRANSPARENT, TOOLWINDOW, NOACTIVATE, TOPMOST
            });
        }

        public void ShowTopmost()
        {
            ShowWindow(Handle, 4); // SW_SHOWNOACTIVATE
            SetWindowPos(Handle, new IntPtr(-1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010);
        }

        public void Hide() => ShowWindow(Handle, 0);

        /// <summary>WS_EX_TRANSPARENT is what lets clicks reach other apps; drop it only while the panel needs the mouse.</summary>
        public void SetInteractive(bool interactive)
        {
            if (interactive == _interactive) return;
            _interactive = interactive;
            var style = GetWindowLongPtr(Handle, -20).ToInt64();
            SetWindowLongPtr(Handle, -20, new IntPtr(interactive ? style & ~ExTransparent : style | ExTransparent));
            if (!interactive) _tracking = false;
        }

        public void Update(LayerSurface surface, Point origin)
        {
            var destination = new POINT(origin.X, origin.Y);
            var source = new POINT(0, 0);
            var size = new SIZE(surface.Size.Width, surface.Size.Height);
            var blend = new BLENDFUNCTION { SourceConstantAlpha = 255, AlphaFormat = 1 };
            UpdateLayeredWindow(Handle, IntPtr.Zero, ref destination, ref size, surface.Dc, ref source, 0, ref blend, 2);
        }

        protected override void WndProc(ref Message message)
        {
            switch (message.Msg)
            {
                case 0x0084 when !_interactive: // WM_NCHITTEST
                    message.Result = new IntPtr(-1); // HTTRANSPARENT
                    return;
                case 0x0084:
                    var screen = new Point((short)(message.LParam.ToInt64() & 0xFFFF), (short)((message.LParam.ToInt64() >> 16) & 0xFFFF));
                    message.Result = new IntPtr(_owner.HitBody(new Point(screen.X - _owner._origin.X, screen.Y - _owner._origin.Y)) ? 1 : -1);
                    return;
                case 0x0020 when _interactive && (message.LParam.ToInt64() & 0xFFFF) == 1 && _owner.WantsHand: // WM_SETCURSOR over our window
                    SetCursor(LoadCursor(IntPtr.Zero, 32649)); // IDC_HAND
                    message.Result = new IntPtr(1);
                    return;
                case 0x0021: // WM_MOUSEACTIVATE
                    message.Result = new IntPtr(3); // MA_NOACTIVATE
                    return;
                case 0x0200 or 0x0201 or 0x02A3:
                    if (message.Msg == 0x0200 && !_tracking)
                    {
                        var track = new TRACKMOUSEEVENT { Size = Marshal.SizeOf<TRACKMOUSEEVENT>(), Flags = 2, Window = Handle };
                        _tracking = TrackMouseEvent(ref track);
                    }
                    if (message.Msg == 0x02A3) _tracking = false;
                    _owner.OnMouse(message.Msg, new Point((short)(message.LParam.ToInt64() & 0xFFFF), (short)((message.LParam.ToInt64() >> 16) & 0xFFFF)), 0);
                    return;
                case 0x020A: // WM_MOUSEWHEEL carries screen coordinates
                    _owner.OnMouse(message.Msg, default, (short)((message.WParam.ToInt64() >> 16) & 0xFFFF));
                    return;
            }
            base.WndProc(ref message);
        }
    }

    /// <summary>Premultiplied 32-bit DIB that GDI+ draws into and UpdateLayeredWindow reads from.</summary>
    private sealed class LayerSurface : IDisposable
    {
        private readonly IntPtr _bitmap;
        private readonly IntPtr _previous;

        public LayerSurface(Size size)
        {
            Size = size;
            Dc = CreateCompatibleDC(IntPtr.Zero);
            var header = new BITMAPINFOHEADER { Size = 40, Width = size.Width, Height = -size.Height, Planes = 1, BitCount = 32 };
            _bitmap = CreateDIBSection(Dc, ref header, 0, out var bits, IntPtr.Zero, 0);
            if (_bitmap == IntPtr.Zero)
            {
                DeleteDC(Dc);
                throw new Win32Exception();
            }
            _previous = SelectObject(Dc, _bitmap);
            Image = new Bitmap(size.Width, size.Height, size.Width * 4, PixelFormat.Format32bppPArgb, bits);
        }

        public Size Size { get; }
        public IntPtr Dc { get; }
        public Bitmap Image { get; }

        public void Dispose()
        {
            Image.Dispose();
            SelectObject(Dc, _previous);
            DeleteObject(_bitmap);
            DeleteDC(Dc);
        }
    }

    [StructLayout(LayoutKind.Sequential)] private struct POINT(int x, int y) { public int X = x, Y = y; }
    [StructLayout(LayoutKind.Sequential)] private struct SIZE(int cx, int cy) { public int Cx = cx, Cy = cy; }
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }
    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public int Size, Width, Height;
        public short Planes, BitCount;
        public int Compression, SizeImage, XPelsPerMeter, YPelsPerMeter, ClrUsed, ClrImportant;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct TRACKMOUSEEVENT { public int Size, Flags; public IntPtr Window; public int HoverTime; }

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr window, int command);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr window, int index, IntPtr value);
    [DllImport("user32.dll")] private static extern bool TrackMouseEvent(ref TRACKMOUSEEVENT track);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UpdateLayeredWindow(IntPtr window, IntPtr screenDc, ref POINT destination, ref SIZE size,
        IntPtr sourceDc, ref POINT source, int colorKey, ref BLENDFUNCTION blend, int flags);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(POINT point, uint flags);
    [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint dpiX, out uint dpiY);
    [DllImport("user32.dll")] private static extern bool SystemParametersInfo(uint action, uint parameter, out bool value, uint flags);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr CreateDIBSection(IntPtr dc, ref BITMAPINFOHEADER header, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr item);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr item);
    [DllImport("user32.dll")] private static extern IntPtr SetCursor(IntPtr cursor);
    [DllImport("user32.dll")] private static extern IntPtr LoadCursor(IntPtr instance, int name);
    [DllImport("dwmapi.dll")] private static extern int DwmFlush();
    [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")] private static extern uint TimeBeginPeriod(uint period);
    [DllImport("winmm.dll", EntryPoint = "timeEndPeriod")] private static extern uint TimeEndPeriod(uint period);
}
