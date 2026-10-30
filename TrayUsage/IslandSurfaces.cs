using System.Drawing.Drawing2D;

namespace TrayUsage;

// Surface proportions (two-line notice capsule of height 48 with a 32 disc, 8/10/20 insets, width 200..372,
// 28-high tab pills with an 8 gap) follow Oyenza Studio's pill/notice.rs and ui.rs.
// MIT License, Copyright (c) 2026 Jatevo Labs, https://github.com/lucacadalora/oyenza-studio
// @ 6382a85be2fd149374558a5bcecd1426ede6c53b. See THIRD_PARTY_NOTICES.md.

/// <summary>What a notice announces. Names follow Agent HUD's alert kinds; Approval comes only from the opt-in Claude Code hook.</summary>
internal enum NoticeType { Running, Completed, Question, Approval, Warning, Exhausted, Reset }

internal sealed record NoticeData(AgentKind Kind, NoticeType Type, string Title, string Subtitle, string Trailing, SessionTarget? Target = null,
    NoticeCard? Card = null);

/// <summary>Colour and glyph of each notice type (a solid disc with a dark glyph, like the completion check).</summary>
internal static class NoticeLook
{
    public static readonly Color Question = Color.FromArgb(90, 170, 255), Reset = Color.FromArgb(100, 215, 255), Approval = Color.FromArgb(255, 159, 10);

    public static Color Accent(NoticeData data) => data.Type switch
    {
        NoticeType.Completed => IslandStyle.Success,
        NoticeType.Question => Question,
        NoticeType.Approval => Approval,
        NoticeType.Warning => IslandStyle.Amber,
        NoticeType.Exhausted => IslandStyle.Warning,
        NoticeType.Reset => Reset,
        _ => Agent.Accent(data.Kind)
    };

    /// <summary>
    /// The notice's badge (a coloured disc with its glyph; a warning is a ⚠ triangle) with the agent's logo on a black
    /// ring at its lower right, so Claude Code and Codex tell apart at a glance.
    /// </summary>
    public static void DrawBadge(Graphics g, IslandStyle style, RectangleF disc, NoticeData data)
    {
        if (data.Type == NoticeType.Warning)
        {
            var (cx, cy) = (disc.X + disc.Width / 2, disc.Y + disc.Height / 2 + 0.5f);
            PointF[] triangle = [new(cx, cy - 10f), new(cx - 11f, cy + 8f), new(cx + 11f, cy + 8f)];
            var color = Accent(data);
            using (var fill = new SolidBrush(color)) g.FillPolygon(fill, triangle);
            using (var edge = new Pen(color, 3f) { LineJoin = LineJoin.Round }) g.DrawPolygon(edge, triangle);
            var ink = Color.FromArgb(20, 20, 22);
            using var mark = new Pen(ink, 2.3f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            using var dot = new SolidBrush(ink);
            g.DrawLine(mark, cx, cy - 4.2f, cx, cy + 1.6f);
            g.FillEllipse(dot, cx - 1.3f, cy + 3.3f, 2.6f, 2.6f);
        }
        else
        {
            using (var fill = new SolidBrush(Accent(data))) g.FillEllipse(fill, disc);
            DrawGlyph(g, style, disc, data.Type);
        }
        var ring = new RectangleF(disc.Right - 13, disc.Bottom - 13, 20, 20);
        using (var black = new SolidBrush(Color.Black)) g.FillEllipse(black, ring);
        g.DrawImage(style.Icon(data.Kind), new RectangleF(ring.X + 3, ring.Y + 3, 14, 14));
    }

    public static void DrawGlyph(Graphics g, IslandStyle style, RectangleF disc, NoticeType type)
    {
        var (cx, cy) = (disc.X + disc.Width / 2, disc.Y + disc.Height / 2);
        var ink = Color.FromArgb(20, 20, 22);
        using var pen = new Pen(ink, 2.4f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        using var brush = new SolidBrush(ink);
        switch (type)
        {
            case NoticeType.Completed:
                g.DrawLines(pen, [new PointF(cx - 5.5f, cy + 0.5f), new PointF(cx - 1.5f, cy + 4.5f), new PointF(cx + 5.5f, cy - 4f)]);
                break;
            case NoticeType.Question:
                // The hook and the stem are one path, so the stem starts exactly where the arc ends.
                using (var hook = new GraphicsPath())
                {
                    hook.AddArc(cx - 4.2f, cy - 9.4f, 8.4f, 8.4f, 195, 235);
                    hook.AddLine(cx + 1.9f, cy - 2.2f, cx, cy + 1.2f);
                    g.DrawPath(pen, hook);
                }
                g.FillEllipse(brush, cx - 1.6f, cy + 4.4f, 3.2f, 3.2f);
                break;
            case NoticeType.Approval:
                // Raised hand, as a silhouette: a solid palm, four round-ended fingers with a clear gap between them, and a thumb.
                using (var finger = new Pen(ink, 2.5f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                {
                    float[] tops = [-6.5f, -8.5f, -8f, -5.5f];
                    for (var i = 0; i < 4; i++) g.DrawLine(finger, cx - 5.7f + i * 3.8f, cy + tops[i], cx - 5.7f + i * 3.8f, cy + 2f);
                    g.DrawLine(finger, cx - 6.4f, cy + 4.6f, cx - 9f, cy + 0.6f);
                    using var palm = IslandStyle.ContinuousRect(new RectangleF(cx - 7f, cy + 0.5f, 14f, 8.5f), 4.2f);
                    g.FillPath(brush, palm);
                }
                break;
            case NoticeType.Warning or NoticeType.Exhausted:
                g.DrawLine(pen, cx, cy - 7, cx, cy + 1.5f);
                g.FillEllipse(brush, cx - 1.6f, cy + 4.2f, 3.2f, 3.2f);
                break;
            case NoticeType.Reset:
                // Circular arrow: three quarters of a circle ending in an arrowhead.
                const float r = 6.5f;
                g.DrawArc(pen, cx - r, cy - r, 2 * r, 2 * r, -50, 280);
                var end = (230 * Math.PI) / 180;
                var tip = new PointF(cx + r * (float)Math.Cos(end), cy + r * (float)Math.Sin(end));
                var along = new PointF(-(float)Math.Sin(end), (float)Math.Cos(end));
                var across = new PointF((float)Math.Cos(end), (float)Math.Sin(end));
                g.FillPolygon(brush,
                [
                    new PointF(tip.X + along.X * 5.5f, tip.Y + along.Y * 5.5f),
                    new PointF(tip.X + across.X * 4f, tip.Y + across.Y * 4f),
                    new PointF(tip.X - across.X * 4f, tip.Y - across.Y * 4f)
                ]);
                break;
        }
    }
}

/// <summary>One line of the settings page, listed under the heading <paramref name="Group"/>. No <paramref name="Options"/> means an on/off switch (Selected is 0 or 1).</summary>
internal sealed record SettingRow(string Id, string Group, string Label, string[] Options, int Selected);

/// <summary>Something the island can show. Sizes are in DIPs; <see cref="Render"/> returns an opaque black bitmap.</summary>
internal abstract class IslandSurface
{
    public abstract SizeF Size { get; }
    public abstract float Radius { get; }
    public abstract Bitmap Render(IslandStyle style, float scale);

    /// <summary>
    /// Logos of sessions that are working. <see cref="Render"/> leaves them out; the island draws them every frame
    /// (a ring sweeping round the logo, RingGap DIPs outside it), so they move without re-rendering the whole surface.
    /// </summary>
    public virtual IReadOnlyList<(RectangleF Rect, AgentKind Kind, float RingGap)> LiveIcons => [];

    /// <summary>Bare rings with an arc sweeping round them (a working session's spinner), drawn every frame like <see cref="LiveIcons"/>.</summary>
    public virtual IReadOnlyList<(RectangleF Rect, Color Color)> LiveSpinners => [];

    /// <summary>Height of the header that stays put while the body below it changes pages (0: nothing is fixed).</summary>
    public virtual float FixedTop => 0;
}

/// <summary>Capsule that announces a finished turn, or shows a running one.</summary>
internal sealed class NoticeSurface : IslandSurface
{
    private const float Height = 48, MinWidth = 200, MaxWidth = 372, Inset = 8, Disc = 32, TextGap = 10, EndGap = 20, TrailGap = 12;
    private const float TextX = Inset + Disc + TextGap;
    private readonly IslandStyle _style;
    private NoticeData _data;
    private float _width;

    public NoticeSurface(IslandStyle style, NoticeData data)
    {
        _style = style;
        _data = data;
        _width = MeasureWidth();
    }

    // A running or finished turn rests as a slim pill: the logo, the time, and the state (a spinner, or a green check), evenly
    // spaced. The time sits in a slot as wide as "00:00", so the pill does not change width as the digits tick.
    private const float CompactHeight = 40, CompactInset = 14, CompactGap = 10, StateSize = 22, CompactLogo = 24;
    private float _compactWidth, _timeSlot;
    private float CompactWidth => _compactWidth;
    private float StateX => CompactWidth - CompactInset - StateSize;

    public NoticeType Type => _data.Type;
    public NoticeData Data => _data;
    private bool Compact => _data.Type is NoticeType.Running or NoticeType.Completed;
    public override SizeF Size => Compact ? new(CompactWidth, CompactHeight) : new(_width, Height);
    public override float Radius => (Compact ? CompactHeight : Height) / 2;

    public override IReadOnlyList<(RectangleF Rect, Color Color)> LiveSpinners => _data.Type == NoticeType.Running
        ? [(new RectangleF(StateX, (CompactHeight - StateSize) / 2, StateSize, StateSize), Agent.Accent(_data.Kind))] : [];

    public void Update(NoticeData data)
    {
        _data = data;
        _width = MeasureWidth();
    }

    private float MeasureWidth()
    {
        _timeSlot = Math.Max(_style.MeasureWidth("00:00", _style.Body), _style.MeasureWidth(_data.Trailing, _style.Body));
        _compactWidth = (float)Math.Ceiling(CompactInset * 2 + CompactLogo + CompactGap + _timeSlot + CompactGap + StateSize);
        _compactWidth += _compactWidth % 2; // even DIPs, so the middle is a whole pixel
        var words = Math.Max(_style.MeasureWidth(_data.Title, _style.Name), _style.MeasureWidth(_data.Subtitle, _style.Caption));
        var trailing = _data.Trailing.Length == 0 ? 0 : _style.MeasureWidth(_data.Trailing, _style.Body) + TrailGap;
        var width = (float)Math.Ceiling(TextX + words + trailing + EndGap);
        return Math.Clamp(width + width % 2, MinWidth, MaxWidth); // even DIPs
    }

    private Bitmap RenderCompact(IslandStyle style, float scale)
    {
        var (bitmap, g) = style.NewCanvas(CompactWidth, CompactHeight, scale);
        using (g)
        {
            g.DrawImage(style.Icon(_data.Kind), new RectangleF(CompactInset, (CompactHeight - CompactLogo) / 2, CompactLogo, CompactLogo));
            IslandStyle.Text(g, _data.Trailing, style.Body, IslandStyle.Secondary,
                CompactInset + CompactLogo + CompactGap + (_timeSlot - style.MeasureWidth(_data.Trailing, style.Body)) / 2, CompactHeight / 2 + 4.5f);
            if (_data.Type == NoticeType.Completed) // a working turn's spinner is drawn live by the island
            {
                var disc = new RectangleF(StateX, (CompactHeight - StateSize) / 2, StateSize, StateSize);
                using (var fill = new SolidBrush(IslandStyle.Success)) g.FillEllipse(fill, disc);
                NoticeLook.DrawGlyph(g, style, disc, NoticeType.Completed);
            }
        }
        return bitmap;
    }

    public override Bitmap Render(IslandStyle style, float scale)
    {
        if (Compact) return RenderCompact(style, scale);
        var (bitmap, g) = style.NewCanvas(_width, Height, scale);
        using (g)
        {
            var disc = new RectangleF(Inset, (Height - Disc) / 2, Disc, Disc);
            NoticeLook.DrawBadge(g, style, disc, _data);

            var trailing = _data.Trailing.Length == 0 ? 0 : style.MeasureWidth(_data.Trailing, style.Body) + TrailGap;
            var textWidth = _width - TextX - EndGap - trailing;
            IslandStyle.TextTrimmed(g, _data.Title, style.Name, IslandStyle.Primary, TextX, 22, textWidth);
            IslandStyle.TextTrimmed(g, _data.Subtitle, style.Caption, IslandStyle.Secondary, TextX, 37, textWidth);
            style.RightAligned(g, _data.Trailing, style.Body, IslandStyle.Secondary, _width - EndGap, 29);
        }
        return bitmap;
    }
}

/// <summary>
/// The Pill theme's resting capsule, up whenever nothing else is: a ring in the middle that fills with the heaviest usage (in the
/// usage level's colour), so progress is always in view. The ring and the dot are centred, so the capsule reads the same when it
/// swells under the pointer.
/// </summary>
internal sealed class IdleSurface(double percent) : IslandSurface
{
    private const float Width = 72, Height = 32, RingSize = 16, RingWidth = 2.4f, DotSize = 4;
    private double _percent = percent;

    public override SizeF Size => new(Width, Height);
    public override float Radius => Height / 2;

    /// <summary>Takes the heaviest usage; false when the ring would not change.</summary>
    public bool SetPercent(double value)
    {
        var changed = Math.Abs(value - _percent) >= 0.5;
        if (changed) _percent = value;
        return changed;
    }

    public override Bitmap Render(IslandStyle style, float scale)
    {
        var (bitmap, g) = style.NewCanvas(Width, Height, scale);
        using (g)
        {
            var ring = new RectangleF((Width - RingSize) / 2, (Height - RingSize) / 2, RingSize, RingSize);
            using (var track = new Pen(IslandStyle.Alpha3, RingWidth)) g.DrawEllipse(track, ring);
            if (_percent >= 1)
            {
                using var fill = new Pen(IslandStyle.Level(_percent), RingWidth) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                g.DrawArc(fill, ring, -90, (float)(Math.Clamp(_percent, 0, 100) * 3.6));
            }
            using var dot = new SolidBrush(IslandStyle.Secondary);
            g.FillEllipse(dot, (Width - DotSize) / 2, (Height - DotSize) / 2, DotSize, DotSize);
        }
        return bitmap;
    }
}

/// <summary>
/// What a notice's detail card shows besides its header. <see cref="Body"/>, <see cref="Choices"/> and <see cref="Percent"/>
/// make the first screen (what happened); <see cref="Stats"/>, <see cref="Tools"/> and the footer make the second (the figures).
/// </summary>
/// <param name="Footer">One quiet line: the branch, or for a usage meter the reset time.</param>
/// <param name="Steps">The tool calls of a running or finished turn; with <paramref name="Prompt"/> and <paramref name="Reply"/> they make the progress card.</param>
internal sealed record NoticeCard(string? Body = null, IReadOnlyList<string>? Choices = null, IReadOnlyList<(string Tool, int Count)>? Tools = null,
    IReadOnlyList<(string Value, string Label)>? Stats = null, double? Percent = null, string? Footer = null, string? Task = null, string? Heading = null,
    IReadOnlyList<ProgressStep>? Steps = null, string? Prompt = null, string? Reply = null);

/// <summary>
/// The card a notice opens into when it is clicked (it replaces jumping to the terminal, which cannot tell windows or tabs
/// apart). The header is the notice's own, at the same place, so the capsule grows into the card; below it come one or two
/// screens drawn like the usage panel: plain text, hairline meters, no boxes. A click goes to the next screen and, on the
/// last one, closes the card; the wheel switches screens.
/// </summary>
internal sealed class DetailSurface : IslandSurface
{
    private const float Width = 380, Header = 48, Inset = 8, Disc = 32, TextX = Inset + Disc + 10, Right = 20, Pad = 10, DotsHeight = 20, BarHeight = 5;
    private readonly IslandStyle _style;
    private readonly NoticeCard _card;
    private readonly List<Action<Graphics>> _pages = [];
    private readonly float _height;
    private readonly float _body; // height of the tallest screen, below the header

    public DetailSurface(IslandStyle style, NoticeData data)
    {
        _style = style;
        Data = data;
        _card = data.Card ?? new NoticeCard();
        var accent = NoticeLook.Accent(data);
        var textWidth = Width - TextX - Right;
        var heights = new List<float>();

        // First screen (questions, errors, limits): what was asked in one quiet line, a heading in the notice's colour (the error
        // code, the reset time), the model's words, and the options or plan steps as hairline-separated rows; or a usage meter.
        // The content sits in the middle of the body, so a short message leaves even space, not a gap below.
        var body = _card.Body is { Length: > 0 } b ? b : null;
        var choices = _card.Choices is { Count: > 0 } c ? c.Take(4).ToArray() : null;
        var heading = _card.Heading is { Length: > 0 } h && (body is not null || choices is not null) ? h : null;
        var task = _card.Task is { Length: > 0 } t && (body is not null || choices is not null) ? t : null;
        if (_card.Percent is { } percent)
        {
            heights.Add(Pad + 44 + Pad);
            _pages.Add(g =>
            {
                var top = Header + (_body - 44) / 2;
                if (_card.Footer is { Length: > 0 } reset) IslandStyle.Text(g, reset, style.Body, IslandStyle.Secondary, TextX, top + 18);
                style.RightAligned(g, $"{percent:0}%", style.Value, IslandStyle.Primary, Width - Right, top + 19);
                Bar(g, TextX, top + 28, textWidth, (float)percent / 100, IslandStyle.Level(percent));
            });
        }
        else if (body is not null || choices is not null)
        {
            var line = style.Body.Height;
            var headingHeight = heading is null ? 0 : style.Value.Height + 2;
            var bodyFont = heading is null ? style.Name : style.Body;
            var text = body is null ? 0 : style.ParagraphHeight(body, bodyFont, textWidth, heading is null ? 3 : 2);
            const float row = 27;
            var content = (task is null ? 0 : line + 10) + headingHeight + text + (choices is null ? 0 : (body is null ? 0 : 6) + choices.Length * row);
            heights.Add(Pad + content + Pad);
            _pages.Add(g =>
            {
                var top = Header + (_body - content) / 2;
                if (task is not null)
                {
                    IslandStyle.TextTrimmed(g, task, style.Body, IslandStyle.Tertiary, TextX, top + line - 4, textWidth);
                    top += line + 10;
                }
                if (heading is not null)
                {
                    IslandStyle.TextTrimmed(g, heading, style.Value, accent, TextX, top + style.Value.Height - 5, textWidth);
                    top += headingHeight;
                }
                if (body is not null)
                {
                    IslandStyle.Paragraph(g, body, bodyFont, heading is null ? IslandStyle.Primary : IslandStyle.Secondary, TextX, top, textWidth, text);
                    top += text + 6;
                }
                for (var i = 0; choices is not null && i < choices.Length; i++)
                {
                    if (i > 0)
                        using (var rule = new Pen(IslandStyle.Alpha2, 1)) g.DrawLine(rule, TextX, top, Width - Right, top);
                    IslandStyle.Text(g, (i + 1).ToString(), style.Caption, accent, TextX, top + 17);
                    IslandStyle.TextTrimmed(g, choices[i], style.Body, IslandStyle.Primary, TextX + 20, top + 17, textWidth - 20);
                    top += row;
                }
            });
        }

        // Second screen: up to three figures, the busiest tools as hairline bars, the branch.
        var stats = _card.Percent is null ? _card.Stats?.Where(s => s.Value.Length > 0).Take(3).ToArray() ?? [] : [];
        var tools = _card.Percent is null ? _card.Tools?.Take(3).ToArray() ?? [] : [];
        if (stats.Length == 0 && tools.Length == 0 && _pages.Count == 0) stats = [(Agent.Name(data.Kind), "Agent"), (data.Trailing, "Status")];
        if (stats.Length > 0 || tools.Length > 0)
        {
            var footer = _card.Percent is null && _card.Footer is { Length: > 0 } f ? f : null;
            heights.Add(Pad + (stats.Length > 0 ? 36 : 0) + (tools.Length > 0 ? (stats.Length > 0 ? 10 : 0) + tools.Length * 20 : 0) + (footer is null ? 0 : 8 + 14) + Pad);
            _pages.Add(g =>
            {
                var top = Header + Pad;
                if (stats.Length > 0)
                {
                    var column = textWidth / stats.Length;
                    for (var i = 0; i < stats.Length; i++)
                    {
                        IslandStyle.TextTrimmed(g, stats[i].Value, style.Value, IslandStyle.Primary, TextX + i * column, top + 15, column - 8);
                        IslandStyle.TextTrimmed(g, stats[i].Label, style.Caption, IslandStyle.Tertiary, TextX + i * column, top + 31, column - 8);
                    }
                    top += 36 + 10;
                }
                var peak = tools.Length == 0 ? 1 : tools.Max(t => t.Count);
                foreach (var (tool, count) in tools)
                {
                    IslandStyle.TextTrimmed(g, tool, style.Body, IslandStyle.Secondary, TextX, top + 14, 74);
                    var track = textWidth - 74 - 30;
                    Bar(g, TextX + 80, top + 8, track, (float)count / peak, ToolColor(tool));
                    style.RightAligned(g, count.ToString(), style.Body, IslandStyle.Primary, Width - Right, top + 14);
                    top += 20;
                }
                if (footer is not null) IslandStyle.TextTrimmed(g, footer, style.Caption, IslandStyle.Tertiary, TextX, top + (tools.Length > 0 || stats.Length > 0 ? 8 : 0) + 11, textWidth);
            });
        }
        _body = heights.Max();
        _height = Header + _body + (_pages.Count > 1 ? DotsHeight : 0);
    }

    private static void Bar(Graphics g, float x, float y, float width, float fraction, Color color)
    {
        var bar = new RectangleF(x, y, width, BarHeight);
        using (var path = IslandStyle.ContinuousRect(bar, BarHeight / 2))
        using (var track = new SolidBrush(IslandStyle.Alpha3)) g.FillPath(track, path);
        using (var path = IslandStyle.ContinuousRect(bar with { Width = Math.Max(BarHeight, width * Math.Clamp(fraction, 0, 1)) }, BarHeight / 2))
        using (var fill = new SolidBrush(color)) g.FillPath(fill, path);
    }

    private static Color ToolColor(string tool) => tool switch
    {
        "Bash" or "PowerShell" or "shell" or "exec_command" or "local_shell" => IslandStyle.Success,
        "Edit" or "Write" or "MultiEdit" or "NotebookEdit" or "apply_patch" => NoticeLook.Question,
        "Read" or "Glob" or "Grep" or "LS" => NoticeLook.Reset,
        "WebFetch" or "WebSearch" => NoticeLook.Approval,
        "Task" or "Agent" => Color.FromArgb(200, 140, 255),
        _ => IslandStyle.Secondary
    };

    public NoticeData Data { get; }
    public int Page { get; set; }
    public int PageCount => _pages.Count;
    public override float FixedTop => Header;
    public override SizeF Size => new(Width, _height);
    public override float Radius => 24;

    public override Bitmap Render(IslandStyle style, float scale)
    {
        var (bitmap, g) = style.NewCanvas(Width, _height, scale);
        using (g)
        {
            // The notice capsule's own layout, so the capsule and the card line up while one grows into the other.
            var disc = new RectangleF(Inset, (Header - Disc) / 2, Disc, Disc);
            NoticeLook.DrawBadge(g, style, disc, Data);
            // A usage meter says the percentage itself, so the header leaves it out.
            var trailingText = _card.Percent is null ? Data.Trailing : "";
            var trailing = trailingText.Length == 0 ? 0 : style.MeasureWidth(trailingText, style.Body) + 12;
            var words = Width - TextX - Right - trailing;
            IslandStyle.TextTrimmed(g, Data.Title, style.Name, IslandStyle.Primary, TextX, 22, words);
            IslandStyle.TextTrimmed(g, Data.Subtitle, style.Caption, IslandStyle.Secondary, TextX, 37, words);
            style.RightAligned(g, trailingText, style.Body, IslandStyle.Secondary, Width - Right, 29);

            _pages[Math.Clamp(Page, 0, _pages.Count - 1)](g);
            if (_pages.Count > 1)
                for (var i = 0; i < _pages.Count; i++)
                {
                    using var dot = new SolidBrush(i == Page ? IslandStyle.Primary : IslandStyle.Alpha4);
                    g.FillEllipse(dot, Width / 2 + (i - (_pages.Count - 1) / 2f) * 12 - 2.5f, _height - 13, 5, 5);
                }
        }
        return bitmap;
    }
}

/// <summary>
/// What the card's "more" button opens: finished turns, usage history, and the settings page. The usage meters and the
/// running sessions are on the card (<see cref="ProgressSurface"/>), which the back button returns to.
/// </summary>
internal sealed class PanelSurface(IslandStyle style) : IslandSurface
{
    public const int PageCount = 3, DonePage = 0, HistoryPage = 1, SettingsPage = 2;
    public const float HeaderHeight = 48, RowHeight = 52, BottomPad = 14, HistoryHeight = 236;
    // Each page is as wide as its content needs: list rows are narrow, the chart and the settings columns want room.
    private const float ListWidth = 500, HistoryWidth = 600, SettingsWidth = 560;
    public static float MaxWidth => Math.Max(ListWidth, Math.Max(HistoryWidth, SettingsWidth));
    public float Width => Page switch { DonePage => ListWidth, HistoryPage => HistoryWidth, _ => SettingsWidth };
    public const int MaxSettingRows = 11;
    private const int MaxRows = 5;
    // Settings: rows of two columns under group headings. MaxSettingsHeight is what the canvas reserves; the layout asserts it fits.
    private const float SettingRowHeight = 36, GroupHeaderHeight = 30, GroupGap = 10, SettingsTop = 10, SettingsBottom = 16, SettingsPad = 24, ColumnGap = 32;
    private const float SwitchWidth = 38, SwitchHeight = 22, SegmentHeight = 24, MaxSettingsHeight = 330;
    private const float TabHeight = 30, TabGap = 8, TabPad = 14, TabLeft = 52, TabTop = 9, RightPad = 28;
    // History chart: plot box inside the page, and the number of columns the curve is reduced to.
    private const float PlotLeft = 56, PlotTop = 58, PlotHeight = 136, ChipHeight = 24, ChipTop = 14, ChartColumns = 400;
    public static float MaxHeight => HeaderHeight + Math.Max(Math.Max(MaxRows * RowHeight + BottomPad, MaxSettingsHeight), HistoryHeight);

    private IReadOnlyList<CompletedTurn> _recent = [];
    private RectangleF[]? _tabs;
    private UsageHistory? _history;
    private ChartCache? _chart;

    public int Page { get; set; }
    public int HoverTab { get; set; } = -1;

    /// <summary>Animated tab highlight (x, width) and meter fill progress; the host drives them with springs.</summary>
    public float IndicatorX { get; private set; }
    public float IndicatorWidth { get; private set; }
    public float MeterProgress { get; private set; } = 1;

    public int HoverRow { get; set; } = -1;
    public bool ShowClock => _settings.FirstOrDefault(row => row.Id == "clock")?.Selected != 0;
    private SettingRow[] _settings = [];
    private float[] _knobs = [];

    // Settings layout, in surface coordinates: one rectangle per row and a heading per group.
    private RectangleF[] _rowRects = [];
    private (string Title, RectangleF Rect)[] _groupHeads = [];
    private float _settingsHeight = 72;

    public void SetSettings(SettingRow[] rows)
    {
        _settings = rows;
        var colWidth = (SettingsWidth - 2 * SettingsPad - ColumnGap) / 2;
        var y = new[] { HeaderHeight + SettingsTop, HeaderHeight + SettingsTop };
        var rects = new RectangleF[rows.Length];
        var heads = new List<(string, RectangleF)>();
        // Each group goes under the shorter column, so the two stay about as tall as each other.
        foreach (var group in rows.Select((row, index) => (row, index)).GroupBy(item => item.row.Group))
        {
            var column = y[0] <= y[1] ? 0 : 1;
            var x = SettingsPad + column * (colWidth + ColumnGap);
            heads.Add((group.Key, new RectangleF(x, y[column], colWidth, GroupHeaderHeight)));
            y[column] += GroupHeaderHeight;
            foreach (var (_, index) in group)
            {
                rects[index] = new RectangleF(x, y[column], colWidth, SettingRowHeight);
                y[column] += SettingRowHeight;
            }
            y[column] += GroupGap;
        }
        _rowRects = rects;
        _groupHeads = heads.ToArray();
        _settingsHeight = rows.Length == 0 ? 72 : Math.Max(y[0], y[1]) - GroupGap + SettingsBottom - HeaderHeight;
        System.Diagnostics.Debug.Assert(_settingsHeight <= MaxSettingsHeight, "settings page is taller than the canvas reserves");
    }

    public RectangleF ActiveTabRect => Tabs[Math.Clamp(Page, 0, Tabs.Length - 1)];

    /// <summary>The chevron in the header's left corner; it returns to the card.</summary>
    public static RectangleF BackRect => new(16, 10, 28, 28);
    public bool HoverBackButton { get; set; }
    public static bool BackAt(PointF point) => RectangleF.Inflate(BackRect, 3, 3).Contains(point);

    /// <summary>The gear in the header's right corner; it opens and closes the settings page.</summary>
    public RectangleF GearRect => new(Width - 24 - 28, 10, 28, 28);
    public bool HoverGear { get; set; }
    public float GearAngle { get; private set; }
    public bool GearAt(PointF point) => RectangleF.Inflate(GearRect, 3, 3).Contains(point);

    /// <returns>True when a value changed, so the cached bitmap is stale.</returns>
    public bool SetAnimation(float x, float width, float meter, float[] knobs, float gear)
    {
        var changed = Math.Abs(gear - GearAngle) > 0.05f || Math.Abs(x - IndicatorX) > 0.01f || Math.Abs(width - IndicatorWidth) > 0.01f || Math.Abs(meter - MeterProgress) > 0.001f ||
            !knobs.AsSpan().SequenceEqual(_knobs);
        _knobs = (float[])knobs.Clone();
        GearAngle = gear;
        (IndicatorX, IndicatorWidth, MeterProgress) = (x, width, meter);
        return changed;
    }

    public float Height => HeaderHeight + Page switch
    {
        DonePage => ListHeight(_recent.Count),
        HistoryPage => HistoryHeight,
        _ => _settingsHeight
    };

    public override SizeF Size => new(Width, Height);
    public override float FixedTop => HeaderHeight;
    public override float Radius => 32;

    private static float ListHeight(int count) => count == 0 ? 72 : Math.Min(MaxRows, count) * RowHeight + BottomPad;

    public void SetHistory(UsageHistory history) => _history = history;

    public void SetActivity(IReadOnlyList<CompletedTurn> recent) => _recent = recent;

    private static readonly string[] Labels = ["Done", "History"];

    private RectangleF[] Tabs
    {
        get
        {
            if (_tabs is not null) return _tabs;
            var x = TabLeft;
            _tabs = Labels.Select(label =>
            {
                var rect = new RectangleF(x, TabTop, style.MeasureWidth(label, style.Body) + TabPad * 2, TabHeight);
                x = rect.Right + TabGap;
                return rect;
            }).ToArray();
            return _tabs;
        }
    }

    /// <summary>Tab under a point in this surface's DIP coordinates, or -1.</summary>
    public int TabAt(PointF point)
    {
        var tabs = Tabs;
        for (var i = 0; i < tabs.Length; i++)
            if (tabs[i].Contains(point)) return i;
        return -1;
    }

    public override Bitmap Render(IslandStyle st, float scale)
    {
        var (bitmap, g) = st.NewCanvas(Width, Height, scale);
        using (g)
        {
            DrawTabs(g, st);
            if (ShowClock) DrawClock(g, st, Tabs[^1].Right);
            switch (Page)
            {
                case DonePage: DrawDone(g, st); break;
                case HistoryPage: DrawHistory(g, st); break;
                default: DrawSettings(g, st); break;
            }
        }
        return bitmap;
    }

    private void DrawTabs(Graphics g, IslandStyle st)
    {
        var labels = Labels;
        var highlight = IndicatorWidth > 0 ? new RectangleF(IndicatorX, TabTop, IndicatorWidth, TabHeight) : ActiveTabRect;
        for (var i = 0; i < Tabs.Length; i++)
        {
            if (i != HoverTab || i == Page) continue;
            using var hoverPath = IslandStyle.ContinuousRect(Tabs[i], TabHeight / 2, 0);
            using var hoverFill = new SolidBrush(IslandStyle.Alpha2);
            g.FillPath(hoverFill, hoverPath);
        }
        if (Page < SettingsPage)
        {
            using var path = IslandStyle.ContinuousRect(highlight, TabHeight / 2, 0);
            using var fill = new SolidBrush(IslandStyle.Thumb);
            g.FillPath(fill, path);
        }
        DrawGear(g);
        DrawBack(g);
        for (var i = 0; i < Tabs.Length; i++)
        {
            var rect = Tabs[i];
            IslandStyle.Text(g, labels[i], st.Body, i == Page ? IslandStyle.Primary : IslandStyle.Secondary, rect.X + TabPad, rect.Y + 19.5f);
        }
    }

    /// <summary>A chevron in a circle: back to the card.</summary>
    private void DrawBack(Graphics g)
    {
        var rect = BackRect;
        using (var fill = new SolidBrush(HoverBackButton ? IslandStyle.Alpha3 : IslandStyle.Alpha2)) g.FillEllipse(fill, rect);
        var (cx, cy) = (rect.X + rect.Width / 2, rect.Y + rect.Height / 2);
        using var pen = new Pen(HoverBackButton ? IslandStyle.Primary : IslandStyle.Secondary, 1.9f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        g.DrawLines(pen, [new PointF(cx + 2, cy - 5), new PointF(cx - 2.5f, cy), new PointF(cx + 2, cy + 5)]);
    }

    private void DrawGear(Graphics g)
    {
        var active = Page == SettingsPage;
        if (active || HoverGear)
        {
            using var back = IslandStyle.ContinuousRect(GearRect, GearRect.Height / 2, 0);
            using var fill = new SolidBrush(active ? IslandStyle.Thumb : IslandStyle.Alpha2);
            g.FillPath(fill, back);
        }
        var (cx, cy) = (GearRect.X + GearRect.Width / 2, GearRect.Y + GearRect.Height / 2);
        const int teeth = 8;
        const float outer = 8.6f, inner = 6.7f, hole = 2.8f;
        var points = new List<PointF>();
        var spin = GearAngle * (float)Math.PI / 180;
        for (var i = 0; i < teeth; i++)
        {
            var a = spin + i * 2 * (float)Math.PI / teeth;
            var wide = (float)Math.PI / teeth * 0.62f;
            var narrow = (float)Math.PI / teeth * 0.34f;
            foreach (var (angle, radius) in new[] { (a - wide, inner), (a - narrow, outer), (a + narrow, outer), (a + wide, inner) })
                points.Add(new PointF(cx + radius * (float)Math.Cos(angle), cy + radius * (float)Math.Sin(angle)));
        }
        using var gear = new GraphicsPath(FillMode.Alternate);
        gear.AddPolygon(points.ToArray());
        gear.AddEllipse(cx - hole, cy - hole, 2 * hole, 2 * hole);
        using var ink = new SolidBrush(active || HoverGear ? IslandStyle.Primary : IslandStyle.Secondary);
        g.FillPath(ink, gear);
    }

    private void DrawClock(Graphics g, IslandStyle st, float tabsRight)
    {
        var now = DateTime.Now;
        var time = now.ToString("HH:mm");
        var date = now.ToString("ddd, MMM d", System.Globalization.CultureInfo.InvariantCulture);
        var timeX = GearRect.X - 12 - st.MeasureWidth(time, st.Value);
        IslandStyle.Text(g, time, st.Value, IslandStyle.Primary, timeX, 29);
        var dateX = timeX - 8 - st.MeasureWidth(date, st.Caption);
        if (dateX < tabsRight + 10) // the weekday goes first, then the whole date, when the tabs need the room
        {
            date = now.ToString("MMM d", System.Globalization.CultureInfo.InvariantCulture);
            dateX = timeX - 8 - st.MeasureWidth(date, st.Caption);
            if (dateX < tabsRight + 10) return;
        }
        IslandStyle.Text(g, date, st.Caption, IslandStyle.Secondary, dateX, 28.5f);
    }

    // ---- settings page ----

    /// <summary>Control rectangles of a row: one for a switch, one per option for a segmented control.</summary>
    private RectangleF[] Controls(int index)
    {
        var row = _settings[index];
        var rect = _rowRects[index];
        if (row.Options.Length == 0)
            return [new RectangleF(rect.Right - SwitchWidth, rect.Y + (rect.Height - SwitchHeight) / 2, SwitchWidth, SwitchHeight)];
        var widths = row.Options.Select(option => Math.Max(40, style.MeasureWidth(option, style.Caption) + 20)).ToArray();
        var x = rect.Right - widths.Sum();
        var top = rect.Y + (rect.Height - SegmentHeight) / 2;
        return widths.Select(w => { var r = new RectangleF(x, top, w, SegmentHeight); x += w; return r; }).ToArray();
    }

    /// <summary>The whole line answers the pointer, a little beyond the column so the hover fill has room around the text.</summary>
    private RectangleF RowHit(int index) => RectangleF.Inflate(_rowRects[index], 8, 0);

    /// <summary>Row and new value under a point (a switch row answers anywhere on the line), or null.</summary>
    public (SettingRow Row, int Value)? SettingAt(PointF point)
    {
        if (Page != SettingsPage) return null;
        for (var i = 0; i < _settings.Length; i++)
        {
            var row = _settings[i];
            if (!RowHit(i).Contains(point)) continue;
            if (row.Options.Length == 0) return (row, 1 - row.Selected);
            var controls = Controls(i);
            for (var option = 0; option < controls.Length; option++)
                if (controls[option].Contains(point)) return (row, option);
        }
        return null;
    }

    /// <summary>The session behind an activity row, for the click that jumps back to it.</summary>
    public SessionTarget? TargetAt(int row)
    {
        if (row < 0 || row >= MaxRows) return null;
        return Page == DonePage && row < _recent.Count ? _recent[row].Target : null;
    }

    public int SettingRowAt(PointF point)
    {
        if (Page == DonePage)
        {
            var row = (int)Math.Floor((point.Y - HeaderHeight) / RowHeight);
            return point.Y >= HeaderHeight && point.X >= 10 && point.X < Width - 10 && TargetAt(row) is not null ? row : -1;
        }
        if (Page != SettingsPage) return -1;
        for (var i = 0; i < _settings.Length; i++)
            if (RowHit(i).Contains(point)) return i;
        return -1;
    }

    private void DrawSettings(Graphics g, IslandStyle st)
    {
        foreach (var (title, rect) in _groupHeads)
        {
            IslandStyle.Text(g, title, st.Caption, IslandStyle.Tertiary, rect.X, rect.Y + 17);
            using var rule = new Pen(IslandStyle.Alpha2, 1);
            g.DrawLine(rule, rect.X, rect.Bottom - 5.5f, rect.Right, rect.Bottom - 5.5f);
        }
        for (var i = 0; i < _settings.Length; i++)
        {
            var row = _settings[i];
            var rect = _rowRects[i];
            if (i == HoverRow)
            {
                using var path = IslandStyle.ContinuousRect(RowHit(i), 10, 0);
                using var hover = new SolidBrush(IslandStyle.Alpha1);
                g.FillPath(hover, path);
            }
            var controls = Controls(i);
            var labelWidth = controls[0].X - rect.X - 12;
            IslandStyle.TextTrimmed(g, row.Label, st.Body, IslandStyle.Primary, rect.X, rect.Y + 22.5f, labelWidth);
            if (row.Options.Length == 0)
            {
                var track = controls[0];
                var on = Math.Clamp(i < _knobs.Length ? _knobs[i] : row.Selected, 0, 1);
                int Mix(int off, int onValue) => (int)Math.Round(off + (onValue - off) * on);
                var color = Color.FromArgb(Mix(38, IslandStyle.Success.R), Mix(38, IslandStyle.Success.G), Mix(38, IslandStyle.Success.B));
                using (var path = IslandStyle.ContinuousRect(track, SwitchHeight / 2, 0))
                using (var fill = new SolidBrush(color))
                    g.FillPath(fill, path);
                var knob = new RectangleF(track.X + 2 + on * (SwitchWidth - 4 - (SwitchHeight - 4)), track.Y + 2, SwitchHeight - 4, SwitchHeight - 4);
                using var knobFill = new SolidBrush(Color.FromArgb(250, 250, 250));
                g.FillEllipse(knobFill, knob);
                continue;
            }
            DrawSegments(g, st, controls, row.Options, row.Selected, -1);
        }
    }

    /// <summary>A row of options in one well; the selected one is lifted. <paramref name="hover"/> is the option under the pointer, or -1.</summary>
    private static void DrawSegments(Graphics g, IslandStyle st, RectangleF[] controls, string[] options, int selected, int hover)
    {
        var span = RectangleF.Union(controls[0], controls[^1]);
        using (var path = IslandStyle.ContinuousRect(span, span.Height / 2, 0))
        using (var well = new SolidBrush(IslandStyle.Alpha2))
            g.FillPath(well, path);
        for (var option = 0; option < controls.Length; option++)
        {
            if (option == selected || option == hover)
            {
                using var path = IslandStyle.ContinuousRect(RectangleF.Inflate(controls[option], -2, -2), (controls[option].Height - 4) / 2, 0);
                using var fill = new SolidBrush(option == selected ? IslandStyle.Thumb : IslandStyle.Alpha2);
                g.FillPath(fill, path);
            }
            var label = options[option];
            var labelX = controls[option].X + (controls[option].Width - st.MeasureWidth(label, st.Caption)) / 2;
            IslandStyle.Text(g, label, st.Caption, option == selected ? IslandStyle.Primary : IslandStyle.Secondary, labelX, controls[option].Y + controls[option].Height / 2 + 4);
        }
    }

    // ---- history page ----

    /// <summary>False: the 5-hour windows over the last 24 hours. True: the weekly windows over the last 7 days.</summary>
    public bool HistoryWeekly { get; set; }

    /// <summary>Option of the range switch under the pointer, or -1.</summary>
    public int HoverRange { get; set; } = -1;

    private static readonly string[] RangeLabels = ["5 hours", "Week"];

    private RectangleF[] RangeRects
    {
        get
        {
            var x = 24f;
            return RangeLabels.Select(label =>
            {
                var rect = new RectangleF(x, HeaderHeight + ChipTop, style.MeasureWidth(label, style.Caption) + 24, ChipHeight);
                x = rect.Right;
                return rect;
            }).ToArray();
        }
    }

    /// <summary>Option of the range switch under a point, or -1.</summary>
    public int RangeAt(PointF point)
    {
        if (Page != HistoryPage) return -1;
        var rects = RangeRects;
        for (var i = 0; i < rects.Length; i++)
            if (rects[i].Contains(point)) return i;
        return -1;
    }

    /// <summary>One provider's curve, already reduced to the plot: segments of points in 0..1 of the range, and the latest reading.</summary>
    private sealed record ChartLine(AgentKind Kind, List<PointF[]> Segments, double? Latest);

    private sealed record ChartCache(int Version, bool Weekly, long Minute, ChartLine[] Lines);

    private ChartLine[] ChartLines(DateTimeOffset now)
    {
        var version = _history?.Version ?? 0;
        if (_chart is { } cached && cached.Version == version && cached.Weekly == HistoryWeekly && cached.Minute == now.ToUnixTimeSeconds() / 60) return cached.Lines;
        var range = HistoryWeekly ? TimeSpan.FromDays(7) : TimeSpan.FromHours(24);
        var since = now - range;
        var gap = range.TotalSeconds / 48; // a hole longer than this (the PC slept, the app was closed) is not drawn across
        ChartLine Build(AgentKind kind)
        {
            var points = _history?.Points(kind, HistoryWeekly, since) ?? [];
            var segments = new List<PointF[]>();
            var current = new List<PointF>();
            long previousAt = 0, previousColumn = -1;
            foreach (var (at, value) in points)
            {
                if (current.Count > 0 && at - previousAt > gap)
                {
                    segments.Add(current.ToArray());
                    current.Clear();
                    previousColumn = -1;
                }
                var x = (float)((at - since.ToUnixTimeSeconds()) / range.TotalSeconds);
                var point = new PointF(Math.Clamp(x, 0, 1), (float)Math.Clamp(value / 100, 0, 1));
                var column = (long)(point.X * ChartColumns);
                if (column == previousColumn) current[^1] = point; // one point per column: the latest in it
                else current.Add(point);
                (previousAt, previousColumn) = (at, column);
            }
            if (current.Count > 0) segments.Add(current.ToArray());
            return new ChartLine(kind, segments, points.Count > 0 ? points[^1].Value : null);
        }
        var lines = new[] { Build(AgentKind.Claude), Build(AgentKind.Codex) };
        _chart = new ChartCache(version, HistoryWeekly, now.ToUnixTimeSeconds() / 60, lines);
        return lines;
    }

    private void DrawHistory(Graphics g, IslandStyle st)
    {
        var now = DateTimeOffset.Now;
        var lines = ChartLines(now);
        var chips = RangeRects;
        DrawSegments(g, st, chips, RangeLabels, HistoryWeekly ? 1 : 0, HoverRange);
        IslandStyle.Text(g, HistoryWeekly ? "Last 7 days" : "Last 24 hours", st.Caption, IslandStyle.Tertiary, chips[^1].Right + 14, chips[0].Y + 16);

        // Legend with the latest reading, right-aligned in the top row.
        var right = Width - RightPad;
        for (var i = lines.Length - 1; i >= 0; i--)
        {
            var text = $"{Agent.Name(lines[i].Kind)}  {(lines[i].Latest is { } latest ? $"{latest:0.#}%" : "—")}";
            var x = right - st.MeasureWidth(text, st.Body);
            IslandStyle.Text(g, text, st.Body, IslandStyle.Primary, x, chips[0].Y + 16);
            using (var dot = new SolidBrush(Agent.Accent(lines[i].Kind))) g.FillEllipse(dot, x - 14, chips[0].Y + 8, 7, 7);
            right = x - 14 - 20;
        }

        var plot = new RectangleF(PlotLeft, HeaderHeight + PlotTop, Width - RightPad - PlotLeft, PlotHeight);
        DrawChartGrid(g, st, plot, now);
        if (lines.All(line => line.Segments.All(segment => segment.Length < 2)))
        {
            const string empty = "Collecting history. The curve appears after a few readings.";
            // Between the 50 % and 80 % guides, so the words sit in clear space.
            IslandStyle.Text(g, empty, st.Body, IslandStyle.Secondary, plot.X + (plot.Width - st.MeasureWidth(empty, st.Body)) / 2, plot.Bottom - plot.Height * 0.65f + 4);
            return;
        }

        // The curves draw in from the left as the fill progress rises.
        var reveal = Math.Clamp(MeterProgress, 0, 1);
        g.SetClip(new RectangleF(plot.X - 3, plot.Y - 6, plot.Width * reveal + 6, plot.Height + 12));
        foreach (var line in lines)
        {
            var color = Agent.Accent(line.Kind);
            using var pen = new Pen(color, 2f) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round };
            using var area = new SolidBrush(Color.FromArgb(34, color));
            using var solid = new SolidBrush(color);
            foreach (var segment in line.Segments)
            {
                var path = segment.Select(p => new PointF(plot.X + p.X * plot.Width, plot.Bottom - p.Y * plot.Height)).ToArray();
                if (path.Length == 1)
                {
                    g.FillEllipse(solid, path[0].X - 2, path[0].Y - 2, 4, 4);
                    continue;
                }
                g.FillPolygon(area, path.Append(new PointF(path[^1].X, plot.Bottom)).Append(new PointF(path[0].X, plot.Bottom)).ToArray());
                g.DrawLines(pen, path);
            }
            if (line.Segments.Count > 0 && line.Segments[^1] is { Length: > 0 } last && reveal >= 0.999f)
            {
                var tip = last[^1];
                g.FillEllipse(solid, plot.X + tip.X * plot.Width - 3.5f, plot.Bottom - tip.Y * plot.Height - 3.5f, 7, 7);
            }
        }
        g.ResetClip();
    }

    /// <summary>Horizontal guides at 0, 50 and 100 %, the 80 % warning level, and the time ticks under the plot.</summary>
    private void DrawChartGrid(Graphics g, IslandStyle st, RectangleF plot, DateTimeOffset now)
    {
        using (var guide = new Pen(IslandStyle.Alpha2, 1))
            foreach (var percent in new[] { 0, 50, 100 })
            {
                var y = (float)Math.Round(plot.Bottom - plot.Height * percent / 100) + 0.5f;
                g.DrawLine(guide, plot.X, y, plot.Right, y);
                var label = percent.ToString();
                IslandStyle.Text(g, label, st.Caption, IslandStyle.Tertiary, plot.X - 8 - st.MeasureWidth(label, st.Caption), y + 4);
            }
        using (var warn = new Pen(Color.FromArgb(70, IslandStyle.Amber), 1) { DashStyle = DashStyle.Dash })
        {
            var y = (float)Math.Round(plot.Bottom - plot.Height * 0.8) + 0.5f;
            g.DrawLine(warn, plot.X, y, plot.Right, y);
        }

        var range = HistoryWeekly ? TimeSpan.FromDays(7) : TimeSpan.FromHours(24);
        var local = (now - range).LocalDateTime;
        var step = HistoryWeekly ? TimeSpan.FromDays(1) : TimeSpan.FromHours(6);
        var tick = HistoryWeekly ? local.Date.AddDays(1) : local.Date.AddHours(Math.Ceiling(local.TimeOfDay.TotalHours / 6) * 6);
        using var tickPen = new Pen(IslandStyle.Alpha1, 1);
        for (; tick <= now.LocalDateTime; tick += step)
        {
            var x = (float)(plot.X + (tick - local).TotalSeconds / range.TotalSeconds * plot.Width);
            g.DrawLine(tickPen, (float)Math.Round(x) + 0.5f, plot.Y, (float)Math.Round(x) + 0.5f, plot.Bottom);
            var label = HistoryWeekly ? tick.ToString("ddd", System.Globalization.CultureInfo.InvariantCulture) : tick.ToString("HH:mm");
            var labelWidth = st.MeasureWidth(label, st.Caption);
            var labelX = x - labelWidth / 2;
            if (labelX >= plot.X - 4 && labelX + labelWidth <= Width - 12)
                IslandStyle.Text(g, label, st.Caption, IslandStyle.Tertiary, labelX, plot.Bottom + 19);
        }
    }

    // ---- done page ----

    private void DrawDone(Graphics g, IslandStyle st)
    {
        if (_recent.Count == 0)
        {
            Empty(g, st, "Nothing finished yet");
            return;
        }
        var y = HeaderHeight;
        for (var i = 0; i < Math.Min(MaxRows, _recent.Count); i++, y += RowHeight)
        {
            var turn = _recent[i];
            HoverRowBack(g, y, i);
            Row(g, st, y, turn.Kind, turn.Project, "Done", $"Took {TimeText.Elapsed(turn.Duration)} · {turn.FinishedAt.ToLocalTime():HH:mm}");
        }
    }

    private void Empty(Graphics g, IslandStyle st, string text) =>
        IslandStyle.Text(g, text, st.Body, IslandStyle.Secondary, (Width - st.MeasureWidth(text, st.Body)) / 2, HeaderHeight + 32);

    private void HoverRowBack(Graphics g, float y, int index)
    {
        if (Page != DonePage || index != HoverRow) return;
        using var path = IslandStyle.ContinuousRect(new RectangleF(12, y, Width - 24, RowHeight - 4), 14, 0);
        using var fill = new SolidBrush(IslandStyle.Alpha1);
        g.FillPath(fill, path);
    }

    private void Row(Graphics g, IslandStyle st, float y, AgentKind kind, string project, string primary, string secondary)
    {
        var icon = new RectangleF(24, y + 8, 28, 28);
        g.DrawImage(st.Icon(kind), icon);
        var badge = new RectangleF(icon.Right - 8, icon.Bottom - 8, 10, 10);
        using (var ring = new SolidBrush(Color.Black)) g.FillEllipse(ring, RectangleF.Inflate(badge, 1.5f, 1.5f));
        using (var dot = new SolidBrush(IslandStyle.Success)) g.FillEllipse(dot, badge);
        IslandStyle.TextTrimmed(g, Agent.Name(kind), st.Name, IslandStyle.Primary, 68, y + 22, 280);
        IslandStyle.TextTrimmed(g, project, st.Caption, IslandStyle.Secondary, 68, y + 39, 280);
        st.RightAligned(g, primary, st.Name, IslandStyle.Success, Width - 24, y + 22);
        st.RightAligned(g, secondary, st.Caption, IslandStyle.Secondary, Width - 24, y + 39);
    }
}
