namespace TrayUsage;

// Dwell-with-slop opening, collapse delay and hold grace follow Oyenza Studio's hover behaviour
// (DWELL_SLOP 4 DIP, HOLD_GRACE 380 ms in crates/poni/src/app.rs; MIT License, Copyright (c) 2026 Jatevo Labs,
// https://github.com/lucacadalora/oyenza-studio @ 6382a85be2fd149374558a5bcecd1426ede6c53b).
// See THIRD_PARTY_NOTICES.md. The island stays invisible until the pointer rests on the top edge.

/// <summary>
/// Polls the cursor (lighter than a low-level mouse hook) to open the panel when the pointer rests on the top-center
/// edge or on a notice, and to collapse a hover-opened panel once the pointer has left it.
/// </summary>
internal sealed class HoverTrigger : IDisposable
{
    private const double CollapseSeconds = 0.35, HoldSeconds = 0.38;
    private const float DwellSlopDip = 4, ZoneHalfWidthDip = 130, MarginDip = 12;
    private readonly UsageIsland _island;
    private readonly System.Windows.Forms.Timer _poll = new() { Interval = 60 };
    private Point _anchor;
    private DateTime _anchorAt, _holdUntil;
    private DateTime? _leftAt;
    private bool _hasAnchor, _fired;
    private Rectangle[]? _monitors;

    public HoverTrigger(UsageIsland island)
    {
        _island = island;
        _poll.Tick += (_, _) => Poll();
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplayChanged;
        _poll.Start();
    }

    /// <summary>The pointer rested on the top edge (or on a notice) long enough.</summary>
    public event Action<Screen>? Opened;

    /// <summary>The pointer left a hover-opened panel and the collapse delay passed.</summary>
    public event Action? Left;

    /// <summary>True while the visible panel was opened by hover, so only then does leaving collapse it.</summary>
    public bool PanelOpenedByHover { get; set; }

    public bool PointerOnIsland { get; private set; }

    /// <summary>Opening by hover can be switched off; the panel then opens only from Caps Lock or the tray icon.</summary>
    public bool Enabled { get; set; } = true;

    public double DwellSeconds { get; set; } = 0.22;

    /// <summary>Keeps a hover-opened panel up a little longer after a click or scroll.</summary>
    public void Hold() => _holdUntil = DateTime.UtcNow.AddSeconds(HoldSeconds);

    private void Poll()
    {
        var p = Cursor.Position;
        var bounds = BoundsAt(p);
        // Island hidden and the pointer nowhere near the top edge: nothing to track, and nothing to allocate.
        if (!_island.Visible && p.Y > bounds.Top + 1 && !PanelOpenedByHover)
        {
            _hasAnchor = false;
            _fired = false;
            return;
        }
        var now = DateTime.UtcNow;
        var scale = UsageIsland.MonitorScale(bounds);
        var buttonDown = Control.MouseButtons != MouseButtons.None; // dragging a window or tab must not open the island

        var onIsland = false;
        if (_island.ScreenBody is { } body)
        {
            var margin = (int)(MarginDip * scale);
            // From the screen edge down, so the gap above the island counts as part of it.
            onIsland = Rectangle.FromLTRB(body.Left - margin, bounds.Top, body.Right + margin, body.Bottom + margin).Contains(p);
        }
        PointerOnIsland = onIsland;
        _island.SetPointerOver(onIsland);

        if (PanelOpenedByHover)
        {
            if (onIsland || buttonDown) _leftAt = null;
            else
            {
                _leftAt ??= now;
                if ((now - _leftAt.Value).TotalSeconds >= CollapseSeconds && now >= _holdUntil)
                {
                    PanelOpenedByHover = false;
                    _leftAt = null;
                    Left?.Invoke();
                }
            }
        }

        var onEdge = p.Y <= bounds.Top + 1 &&
            Math.Abs(p.X - (bounds.Left + bounds.Width / 2)) <= ZoneHalfWidthDip * scale;
        // A notice answers a click (jump to the session), so resting on it does not open the panel.
        if (!onEdge) _fired = false;
        if (!Enabled || !onEdge || buttonDown || PanelOpenedByHover || _island.PanelActive || _fired)
        {
            _hasAnchor = false;
            return;
        }
        // Brushing past does not open it: moving more than the slop restarts the wait.
        if (!_hasAnchor || Math.Sqrt(Math.Pow(p.X - _anchor.X, 2) + Math.Pow(p.Y - _anchor.Y, 2)) > DwellSlopDip * scale)
        {
            (_anchor, _anchorAt, _hasAnchor) = (p, now, true);
            return;
        }
        if ((now - _anchorAt).TotalSeconds < DwellSeconds) return;
        _fired = true;
        _hasAnchor = false;
        Opened?.Invoke(Screen.FromPoint(p));
    }

    /// <summary>Monitor rectangle under a point, from a list that is rebuilt only when the display layout changes.</summary>
    private Rectangle BoundsAt(Point p)
    {
        if (_monitors is null)
            _monitors = Screen.AllScreens.Select(screen => screen.Bounds).ToArray();
        foreach (var bounds in _monitors)
            if (bounds.Contains(p)) return bounds;
        return _monitors.Length > 0 ? _monitors[0] : Rectangle.Empty;
    }

    private void OnDisplayChanged(object? sender, EventArgs e) => _monitors = null;

    public void Dispose()
    {
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplayChanged;
        _poll.Stop();
        _poll.Dispose();
    }
}
