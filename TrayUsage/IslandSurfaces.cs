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

