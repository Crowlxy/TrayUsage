using System.Drawing.Drawing2D;

namespace TrayUsage;

/// <summary>What a point on the card hits: a button, one of the two tabs, one of the pager dots, or a row of the history list.</summary>
internal enum ProgressHit { None, Close, Jump, More, Back, ProgressTab, UsageTab, Dot, Row }

/// <summary>
/// The main menu, and the card a notice opens into: one surface. Header: the session's logo, name, and a dot with the turn's
/// message in the state's colour (the agent's accent while it works, green when done), buttons (jump to the terminal, close
/// once a notice's turn is done, and "more" for the settings panel), and the 進捗 / 使用状況 switch.
/// 進捗: on the left the last few tool calls in the order they were made, each with ▶ while it runs and ✓ once it returned; on
/// the right a tile with the model's latest words, or 完了 when the turn is over. With several sessions each is a page, with
/// dots under it. With no session running the tab turns into 履歴: the latest finished turns as a list, a click on one opens it as
/// the card above (with a back button to the list). A turn that finishes under an open card moves it to the list. 使用状況: both agents' 5-hour and weekly meters and the pace verdict. The height never changes, so the card
/// can be refreshed on every scan without the island resizing.
/// </summary>
internal sealed class ProgressSurface : IslandSurface
{
    public const int ProgressPage = 0, UsagePage = 1;
    public const float CardWidth = 480, CardHeight = Header + TileTop + Rows * RowHeight + 32;
    private const float Header = 48, Pad = 16, Gap = 12, RowHeight = 28, TileWidth = 176, TileTop = 8, SegmentHeight = 28, ButtonSize = 24, ButtonGap = 8;
    private const float UsageRow = 70, UsageName = 138, DotY = CardHeight - 15;
    private const int Rows = 4;
    public const int ListRows = 4;
    private const float ListRowHeight = 36, ListTop = Header + 4, BackSize = 28;
    private static readonly string[] Tabs = ["進捗", "使用状況"];
    private readonly RectangleF[] _segments;
    private IReadOnlyList<NoticeData> _pages, _recent;
    private NoticeData? _viewed; // the finished turn opened from the list
    private string _signature;
    private ProviderUsage? _claude, _codex;

    /// <param name="menu">Opened as the main menu (it leaves with the pointer, or with its timer), not as a notice (which has its own).</param>
    public ProgressSurface(IslandStyle style, IReadOnlyList<NoticeData> pages, int index, bool menu, int tab, IReadOnlyList<NoticeData> recent, NoticeData? viewed)
    {
        _pages = pages;
        _recent = recent;
        _viewed = viewed;
        Menu = menu;
        Page = tab;
        PageIndex = Math.Clamp(index, 0, Math.Max(0, pages.Count - 1));
        _signature = SignatureOf(pages);
        var x = CardWidth - Pad - Tabs.Sum(label => style.MeasureWidth(label, style.Body) + 28);
        _segments = Tabs.Select(label =>
        {
            var rect = new RectangleF(x, (Header - SegmentHeight) / 2, style.MeasureWidth(label, style.Body) + 28, SegmentHeight);
            x = rect.Right;
            return rect;
        }).ToArray();
    }

    public bool Menu { get; }
    public int Page { get; set; }
    public int PageIndex { get; private set; }
    public int PageCount => _pages.Count;
    public ProgressHit Hover { get; set; }

    /// <summary>The pager dot or list row under the pointer, or -1.</summary>
    public int HoverIndex { get; set; } = -1;

    /// <summary>With no session running and no finished turn opened, the progress tab is the list of finished turns.</summary>
    public bool ShowsList => Page == ProgressPage && Current is null;
    private bool ShowBack => Page == ProgressPage && _pages.Count == 0 && Current is not null;
    private string TabLabel(int i) => i == 0 && _pages.Count == 0 ? "履歴" : Tabs[i];
    public override SizeF Size => new(CardWidth, CardHeight);
    public override float Radius => 28;
    public override float FixedTop => Header; // the header stays put while the body changes pages

    public NoticeData? Current => _pages.Count > 0 ? _pages[PageIndex] : _viewed is not null && _recent.Contains(_viewed) ? _viewed : null;
    private bool Done => Current?.Type == NoticeType.Completed;
    private bool ShowClose => !Menu && Done;
    private bool ShowJump => Page == ProgressPage && Current?.Target is not null;
    private static RectangleF BackRect => new(Pad, (Header - BackSize) / 2, BackSize, BackSize);
    // The main menu has the Dock to go to the other pages; only a card opened by a notice keeps the button.
    private RectangleF MoreRect => new(_segments[0].X - ButtonGap - (Menu ? 0 : ButtonSize), (Header - ButtonSize) / 2, Menu ? 0 : ButtonSize, ButtonSize);
    private RectangleF CloseRect => new(MoreRect.X - ButtonGap - ButtonSize, MoreRect.Y, ButtonSize, ButtonSize);
    private RectangleF JumpRect => new((ShowClose ? CloseRect.X : MoreRect.X) - ButtonGap - ButtonSize, MoreRect.Y, ButtonSize, ButtonSize);
    private float ButtonsLeft => ShowJump ? JumpRect.X : ShowClose ? CloseRect.X : MoreRect.X;
    private static RectangleF Tile => new(CardWidth - Pad - TileWidth, Header + TileTop, TileWidth, Rows * RowHeight);

    /// <summary>The key a session goes by across scans: its id, or, for a notice with none (a demo), its agent and folder.</summary>
    public static string KeyOf(NoticeData data) => data.Target?.SessionId ?? $"{data.Kind}|{data.Target?.Cwd}";

    private static string SignatureOf(IReadOnlyList<NoticeData> pages) => string.Join('\u001c', pages.Select(data =>
        string.Join('\u001f', KeyOf(data), data.Type, data.Card?.Prompt, data.Card?.Reply, data.Trailing,
            string.Join('\u001e', data.Card?.Steps?.Select(step => $"{step.Tool}\u001d{step.Detail}\u001d{step.Done}") ?? []))));

    /// <summary>
    /// Takes the sessions' newer state, staying on the page of the same session. False when nothing on the card would change,
    /// so it need not be redrawn.
    /// </summary>
    public bool SetPages(IReadOnlyList<NoticeData> pages)
    {
        var key = Current is { } current ? KeyOf(current) : null;
        var signature = SignatureOf(pages);
        _pages = pages;
        if (pages.Count > 0) _viewed = null; // a running session takes the tab back
        var at = key is null ? -1 : pages.ToList().FindIndex(data => KeyOf(data) == key);
        PageIndex = at >= 0 ? at : Math.Clamp(PageIndex, 0, Math.Max(0, pages.Count - 1));
        if (signature == _signature) return false;
        _signature = signature;
        return true;
    }

    public void SetUsage(ProviderUsage? claude, ProviderUsage? codex) => (_claude, _codex) = (claude, codex);

    /// <summary>Takes the list of finished turns, newest first. False when nothing on the card would change.</summary>
    public bool SetRecent(IReadOnlyList<NoticeData> recent)
    {
        var same = recent.Count == _recent.Count && recent.Zip(_recent).All(pair => ReferenceEquals(pair.First, pair.Second));
        _recent = recent;
        return !same && Page == ProgressPage && _pages.Count == 0;
    }

    /// <summary>Opens a row of the list as the card above it; false when there is no such row.</summary>
    public bool Open(int row)
    {
        if (row < 0 || row >= Math.Min(ListRows, _recent.Count)) return false;
        _viewed = _recent[row];
        return true;
    }

    /// <summary>Back from an opened finished turn to the list.</summary>
    public void CloseViewed() => _viewed = null;

    /// <summary>Moves to another session's page; false when there is none there.</summary>
    public bool GoTo(int index)
    {
        if (index < 0 || index >= _pages.Count || index == PageIndex) return false;
        PageIndex = index;
        return true;
    }

    public ProgressHit HitAt(PointF point, out int index)
    {
        index = -1;
        if (ShowBack && RectangleF.Inflate(BackRect, 4, 4).Contains(point)) return ProgressHit.Back;
        if (ShowClose && RectangleF.Inflate(CloseRect, 4, 4).Contains(point)) return ProgressHit.Close;
        if (ShowJump && RectangleF.Inflate(JumpRect, 4, 4).Contains(point)) return ProgressHit.Jump;
        if (!Menu && RectangleF.Inflate(MoreRect, 4, 4).Contains(point)) return ProgressHit.More;
        if (_segments[0].Contains(point)) return ProgressHit.ProgressTab;
        if (_segments[1].Contains(point)) return ProgressHit.UsageTab;
        if (ShowsList)
            for (var i = 0; i < Math.Min(ListRows, _recent.Count); i++)
                if (RowRect(i).Contains(point))
                {
                    index = i;
                    return ProgressHit.Row;
                }
        if (Page == ProgressPage && _pages.Count > 1)
            for (var i = 0; i < _pages.Count; i++)
                if (RectangleF.Inflate(DotRect(i), 5, 7).Contains(point))
                {
                    index = i;
                    return ProgressHit.Dot;
                }
        return ProgressHit.None;
    }

    /// <summary>The pager dots, centred under the card: the page in view is a longer bar.</summary>
    private RectangleF DotRect(int index)
    {
        const float small = 5, wide = 16, gap = 4;
        float WidthOf(int i) => i == PageIndex ? wide : small;
        var span = Enumerable.Range(0, _pages.Count).Sum(WidthOf) + (_pages.Count - 1) * gap;
        var x = (CardWidth - span) / 2 + Enumerable.Range(0, index).Sum(WidthOf) + index * gap;
        return new RectangleF(x, DotY - small / 2, WidthOf(index), small);
    }

    public override Bitmap Render(IslandStyle style, float scale)
    {
        var (bitmap, g) = style.NewCanvas(CardWidth, CardHeight, scale);
        using (g)
        {
            DrawHeader(g, style);
            if (Page == UsagePage) DrawUsage(g, style);
            else DrawProgress(g, style);
        }
        return bitmap;
    }

    private void DrawHeader(Graphics g, IslandStyle style)
    {
        var current = Page == ProgressPage ? Current : null;
        if (current is null)
        {
            // Nothing to name: the page's own title stands where the session would.
            IslandStyle.Text(g, Page == UsagePage ? "使用状況" : "履歴", style.Name, IslandStyle.Primary, Pad, Header / 2 + 5);
        }
        else
        {
            var done = current.Type == NoticeType.Completed;
            var state = done ? IslandStyle.Success : Agent.Accent(current.Kind);
            var lead = ShowBack ? BackSize + 10 : 0; // the back button stands before the logo
            g.DrawImage(style.Icon(current.Kind), new RectangleF(Pad + lead, (Header - 30) / 2, 30, 30));
            var name = Agent.Name(current.Kind);
            IslandStyle.Text(g, name, style.Name, IslandStyle.Primary, Pad + lead + 30 + 10, Header / 2 + 5);
            var x = Pad + lead + 30 + 10 + style.MeasureWidth(name, style.Name) + 14;
            using (var dot = new SolidBrush(state)) g.FillEllipse(dot, x, Header / 2 - 3.5f, 7, 7);
            // The message that started the turn; a turn begun by a picture alone has none, so the folder it works in stands in.
            var folder = current.Target?.Cwd is { Length: > 0 } cwd ? Path.GetFileName(cwd.TrimEnd('\\', '/')) : null;
            var message = current.Card?.Prompt ?? (string.IsNullOrEmpty(folder) ? current.Card?.Footer : folder) ?? "";
            IslandStyle.TextTrimmed(g, message, style.Body, state, x + 14, Header / 2 + 4.5f, Math.Max(0, ButtonsLeft - 12 - x - 14));
        }

        if (ShowBack)
        {
            var back = BackRect;
            var hot = Hover == ProgressHit.Back;
            Button(g, back, hot);
            using var chevron = new Pen(hot ? IslandStyle.Primary : IslandStyle.Secondary, 1.9f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
            var (cx, cy) = (back.X + back.Width / 2, back.Y + back.Height / 2);
            g.DrawLines(chevron, [new PointF(cx + 2, cy - 5), new PointF(cx - 2.5f, cy), new PointF(cx + 2, cy + 5)]);
        }
        if (ShowJump)
        {
            var jump = JumpRect;
            var hot = Hover == ProgressHit.Jump;
            Button(g, jump, hot);
            using var arrow = new Pen(hot ? IslandStyle.Primary : IslandStyle.Secondary, 1.7f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
            var (cx, cy) = (jump.X + jump.Width / 2, jump.Y + jump.Height / 2);
            g.DrawLine(arrow, cx - 3.5f, cy + 3.5f, cx + 3.5f, cy - 3.5f);
            g.DrawLines(arrow, [new PointF(cx - 1, cy - 3.5f), new PointF(cx + 3.5f, cy - 3.5f), new PointF(cx + 3.5f, cy + 1)]);
        }
        if (ShowClose)
        {
            var close = CloseRect;
            Button(g, close, Hover == ProgressHit.Close);
            using var cross = new Pen(Hover == ProgressHit.Close ? IslandStyle.Primary : IslandStyle.Secondary, 1.8f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            var (cx, cy) = (close.X + close.Width / 2, close.Y + close.Height / 2);
            g.DrawLine(cross, cx - 4, cy - 4, cx + 4, cy + 4);
            g.DrawLine(cross, cx - 4, cy + 4, cx + 4, cy - 4);
        }

        // More (Settings): four small squares, as a launcher's grid.
        var more = MoreRect;
        var moreHot = Hover == ProgressHit.More;
        if (!Menu) Button(g, more, moreHot);
        using (var square = new SolidBrush(moreHot ? IslandStyle.Primary : IslandStyle.Secondary))
            for (var i = 0; i < (Menu ? 0 : 4); i++)
            {
                var cell = new RectangleF(more.X + more.Width / 2 - 6 + i % 2 * 7, more.Y + more.Height / 2 - 6 + i / 2 * 7, 5, 5);
                using var path = IslandStyle.ContinuousRect(cell, 1.4f, 0);
                g.FillPath(square, path);
            }

        var well = RectangleF.Union(_segments[0], _segments[1]);
        using (var path = IslandStyle.ContinuousRect(well, well.Height / 2, 0))
        using (var fill = new SolidBrush(IslandStyle.Alpha2))
            g.FillPath(fill, path);
        for (var i = 0; i < Tabs.Length; i++)
        {
            var selected = i == Page;
            var hovered = !selected && Hover == (i == 0 ? ProgressHit.ProgressTab : ProgressHit.UsageTab);
            if (selected || hovered)
            {
                using var path = IslandStyle.ContinuousRect(RectangleF.Inflate(_segments[i], -2, -2), (SegmentHeight - 4) / 2, 0);
                using var fill = new SolidBrush(selected ? IslandStyle.Thumb : IslandStyle.Alpha2);
                g.FillPath(fill, path);
            }
            var label = TabLabel(i);
            IslandStyle.Text(g, label, style.Body, selected ? IslandStyle.Primary : IslandStyle.Secondary,
                _segments[i].X + (_segments[i].Width - style.MeasureWidth(label, style.Body)) / 2, _segments[i].Y + SegmentHeight / 2 + 4.5f);
        }
    }

    private static void Button(Graphics g, RectangleF rect, bool hover)
    {
        using var fill = new SolidBrush(hover ? IslandStyle.Alpha3 : IslandStyle.Alpha2);
        g.FillEllipse(fill, rect);
    }

    // ---- progress page ----

    private void DrawProgress(Graphics g, IslandStyle style)
    {
        if (Current is not { } current)
        {
            DrawList(g, style);
            return;
        }
        var accent = Agent.Accent(current.Kind);
        DrawSteps(g, style, current, accent);
        DrawTile(g, style, current, current.Type == NoticeType.Completed ? IslandStyle.Success : accent);
        if (_pages.Count > 1) DrawDots(g);
    }

    private void DrawDots(Graphics g)
    {
        for (var i = 0; i < _pages.Count; i++)
        {
            var rect = DotRect(i);
            using var path = IslandStyle.ContinuousRect(rect, rect.Height / 2, 0);
            var color = i == PageIndex ? Agent.Accent(_pages[i].Kind) : i == HoverIndex ? IslandStyle.Alpha4 : IslandStyle.Alpha3;
            using var fill = new SolidBrush(color);
            g.FillPath(fill, path);
        }
    }

    private void DrawSteps(Graphics g, IslandStyle style, NoticeData data, Color accent)
    {
        var steps = data.Card?.Steps is { } all ? all.Skip(Math.Max(0, all.Count - Rows)).ToArray() : [];
        var textX = Pad + 28;
        var textWidth = Tile.X - Gap - textX;
        for (var i = 0; i < steps.Length; i++)
        {
            var top = Header + TileTop + i * RowHeight;
            var (cx, cy) = (Pad + 6f, top + RowHeight / 2);
            if (steps[i].Done)
            {
                using var check = new Pen(accent, 2f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
                g.DrawLines(check, [new PointF(cx - 5, cy + 0.5f), new PointF(cx - 1.5f, cy + 4), new PointF(cx + 5, cy - 3.5f)]);
            }
            else
            {
                using var play = new SolidBrush(accent);
                using var edge = new Pen(accent, 1.5f) { LineJoin = LineJoin.Round };
                PointF[] triangle = [new(cx - 3.5f, cy - 5), new(cx - 3.5f, cy + 5), new(cx + 5, cy)];
                g.FillPolygon(play, triangle);
                g.DrawPolygon(edge, triangle);
            }
            var (head, tail) = LabelOf(steps[i]);
            var tailWidth = style.MeasureWidth(tail, style.Body);
            var shown = MiddleTrimmed(style, head, Math.Max(20, textWidth - tailWidth));
            IslandStyle.Text(g, shown + tail, style.Body, IslandStyle.Primary, textX, top + RowHeight / 2 + 4.5f);
        }
    }

    /// <summary>A command reads "ls -la を実行中" while it runs and "ls -la を実行" after; other tools name themselves and what they touch.</summary>
    private static (string Head, string Tail) LabelOf(ProgressStep step)
    {
        if (step.Tool is "Bash" or "PowerShell" or "shell" or "exec_command" or "local_shell")
            return (step.Detail ?? step.Tool, step.Done ? " を実行" : " を実行中");
        return (step.Detail is { Length: > 0 } detail ? $"{step.Tool} {detail}" : step.Tool, "");
    }

    /// <summary>Shortens <paramref name="text"/> in the middle ("cd /Users/a8/…/wa-site"), so the start and the end both stay readable.</summary>
    private static string MiddleTrimmed(IslandStyle style, string text, float width)
    {
        if (style.MeasureWidth(text, style.Body) <= width) return text;
        for (var keep = text.Length - 1; keep > 1; keep--)
        {
            var head = (int)Math.Ceiling(keep * 0.55);
            var candidate = text[..head] + "…" + text[^(keep - head)..];
            if (style.MeasureWidth(candidate, style.Body) <= width) return candidate;
        }
        return "…";
    }

    private static void DrawTile(Graphics g, IslandStyle style, NoticeData data, Color state)
    {
        var tile = Tile;
        using (var path = IslandStyle.ContinuousRect(tile, 18))
        using (var fill = new SolidBrush(IslandStyle.Alpha2))
            g.FillPath(fill, path);
        if (data.Type == NoticeType.Completed)
        {
            const string label = "完了";
            var duration = data.Trailing.Length > 0 ? $"所要 {data.Trailing}" : "";
            var group = 24 + 8 + style.MeasureWidth(label, style.Name);
            var x = tile.X + (tile.Width - group) / 2;
            var cy = tile.Y + tile.Height / 2 - (duration.Length > 0 ? 8 : 0);
            var disc = new RectangleF(x, cy - 12, 24, 24);
            using (var fill = new SolidBrush(state)) g.FillEllipse(fill, disc);
            NoticeLook.DrawGlyph(g, style, disc, NoticeType.Completed);
            IslandStyle.Text(g, label, style.Name, IslandStyle.Primary, x + 32, cy + 5);
            if (duration.Length > 0)
                IslandStyle.Text(g, duration, style.Caption, IslandStyle.Tertiary, tile.X + (tile.Width - style.MeasureWidth(duration, style.Caption)) / 2, cy + 34);
            return;
        }
        var text = data.Card?.Reply is { Length: > 0 } reply ? reply : "作業中…";
        using var brush = new SolidBrush(data.Card?.Reply is { Length: > 0 } ? IslandStyle.Primary : IslandStyle.Secondary);
        using var format = (StringFormat)StringFormat.GenericTypographic.Clone();
        format.Alignment = StringAlignment.Center;
        format.LineAlignment = StringAlignment.Center;
        format.FormatFlags = StringFormatFlags.LineLimit;
        format.Trimming = StringTrimming.EllipsisWord;
        g.DrawString(text, style.Body, brush, RectangleF.Inflate(tile, -16, -12), format);
    }

    // ---- history list (the progress tab with no session running) ----

    private static RectangleF RowRect(int index) => new(Pad - 6, ListTop + index * ListRowHeight, CardWidth - 2 * Pad + 12, ListRowHeight - 2);

    private void DrawList(Graphics g, IslandStyle style)
    {
        if (_recent.Count == 0)
        {
            const string empty = "完了したものはまだありません";
            IslandStyle.Text(g, empty, style.Body, IslandStyle.Secondary, (CardWidth - style.MeasureWidth(empty, style.Body)) / 2, Header + TileTop + Rows * RowHeight / 2 + 4.5f);
            return;
        }
        for (var i = 0; i < Math.Min(ListRows, _recent.Count); i++)
        {
            var turn = _recent[i];
            var row = RowRect(i);
            if (Hover == ProgressHit.Row && HoverIndex == i)
            {
                using var path = IslandStyle.ContinuousRect(row, 12, 0);
                using var fill = new SolidBrush(IslandStyle.Alpha1);
                g.FillPath(fill, path);
            }
            var icon = new RectangleF(Pad, row.Y + (row.Height - 22) / 2, 22, 22);
            g.DrawImage(style.Icon(turn.Kind), icon);
            var badge = new RectangleF(icon.Right - 6, icon.Bottom - 6, 9, 9);
            using (var ring = new SolidBrush(Color.Black)) g.FillEllipse(ring, RectangleF.Inflate(badge, 1.5f, 1.5f));
            using (var dot = new SolidBrush(IslandStyle.Success)) g.FillEllipse(dot, badge);
            var finished = turn.Card?.Stats?.FirstOrDefault(stat => stat.Label == "Finished").Value;
            var took = turn.Trailing.Length > 0 ? $"所要 {turn.Trailing}" : "";
            var right = string.Join(" · ", new[] { took, finished }.Where(part => !string.IsNullOrEmpty(part)));
            var rightWidth = style.MeasureWidth(right, style.Caption);
            style.RightAligned(g, right, style.Caption, IslandStyle.Secondary, CardWidth - Pad, row.Y + row.Height / 2 + 4);
            var textX = Pad + 22 + 12;
            var name = Agent.Name(turn.Kind);
            IslandStyle.Text(g, name, style.Body, IslandStyle.Primary, textX, row.Y + row.Height / 2 + 4.5f);
            var projectX = textX + style.MeasureWidth(name, style.Body) + 12;
            IslandStyle.TextTrimmed(g, turn.Subtitle, style.Caption, IslandStyle.Tertiary, projectX, row.Y + row.Height / 2 + 4, Math.Max(0, CardWidth - Pad - rightWidth - 16 - projectX));
        }
    }

    // ---- usage page ----

    private void DrawUsage(Graphics g, IslandStyle style)
    {
        var top = Header + TileTop - 2;
        DrawProvider(g, style, top, AgentKind.Claude, _claude);
        using (var rule = new Pen(IslandStyle.Alpha2, 1))
            g.DrawLine(rule, Pad, top + UsageRow + 0.5f, CardWidth - Pad, top + UsageRow + 0.5f);
        DrawProvider(g, style, top + UsageRow, AgentKind.Codex, _codex);
    }

    private void DrawProvider(Graphics g, IslandStyle style, float y, AgentKind kind, ProviderUsage? usage)
    {
        g.DrawImage(style.Icon(kind), new RectangleF(Pad, y + 12, 24, 24));
        IslandStyle.Text(g, Agent.Name(kind), style.Name, IslandStyle.Primary, Pad + 24 + 10, y + 29);
        var meters = Pad + UsageName;
        if (usage is { HasData: false } or null)
        {
            var (text, color) = usage is null ? ("読み込み中…", IslandStyle.Secondary) : ("取得できません", IslandStyle.Amber);
            IslandStyle.Text(g, text, style.Body, color, meters, y + 31);
            return;
        }
        var width = (CardWidth - Pad - meters - 24) / 2;
        Meter(g, style, meters, y, width, "5 hours", usage.FiveHour);
        Meter(g, style, meters + width + 24, y, width, "Week", usage.Weekly);
        if (UsagePace.Of(usage, DateTimeOffset.Now) is { } pace)
        {
            var color = pace.Level switch { PaceLevel.Fine => IslandStyle.Success, PaceLevel.Heavy => IslandStyle.Amber, _ => IslandStyle.Warning };
            using (var dot = new SolidBrush(color)) g.FillEllipse(dot, meters, y + 58, 7, 7);
            IslandStyle.TextTrimmed(g, pace.Text, style.Caption, IslandStyle.Primary, meters + 14, y + 65, CardWidth - Pad - meters - 14);
        }
    }

    private static void Meter(Graphics g, IslandStyle style, float x, float y, float width, string label, UsageWindow? window)
    {
        IslandStyle.Text(g, label, style.Body, IslandStyle.Secondary, x, y + 20);
        style.RightAligned(g, window is null ? "—" : $"{window.UsedPercent:0.#}%", style.Name, IslandStyle.Primary, x + width, y + 21);
        var bar = new RectangleF(x, y + 28, width, 5);
        using (var track = IslandStyle.ContinuousRect(bar, 2.5f))
        using (var fill = new SolidBrush(IslandStyle.Alpha3))
            g.FillPath(fill, track);
        if (window is { UsedPercent: > 0 })
        {
            using var path = IslandStyle.ContinuousRect(bar with { Width = Math.Max(5, width * (float)Math.Clamp(window.UsedPercent / 100, 0, 1)) }, 2.5f);
            using var fill = new SolidBrush(IslandStyle.Level(window.UsedPercent));
            g.FillPath(fill, path);
        }
        if (window?.ResetsAt is { } reset)
            IslandStyle.Text(g, TimeText.ResetAt(reset.ToLocalTime()), style.Caption, IslandStyle.Secondary, x, y + 47);
    }
}
