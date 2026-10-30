using System.Drawing.Drawing2D;

namespace TrayUsage;

internal enum PanelAction
{
    MonthPrev, MonthNext, Today, Day,
    TaskToggle, TaskDelete, TaskNew, TaskClearDone,
    ClipCopy, ClipToTask, ClipDelete, ClipClear,
    FileOpen, FileRemove, FileAddClip, FileClear
}

/// <summary>A control on the panel: where it is, what it does, and which row or day it belongs to.</summary>
internal readonly record struct PanelHit(RectangleF Rect, PanelAction Action, int Index);

internal enum PanelResult { None, Redraw, PageChanged, PromptTask }

/// <summary>
/// What the card's "more" button opens: the pages カレンダー, クリップボード, タスク and ファイル, and the settings page behind the gear.
/// The usage meters and the running sessions are on the card (<see cref="ProgressSurface"/>), which the back button returns to.
/// The controls of a page are registered while it is drawn, so what is hit is always what is shown.
/// </summary>
internal sealed class PanelSurface(IslandStyle style) : IslandSurface
{
    public const int CalendarPage = 0, ClipboardPage = 1, TasksPage = 2, FilesPage = 3, SettingsPage = 4;
    public const float HeaderHeight = 0, Width = 480, PageBody = 220;
    public const int MaxSettingRows = 12;
    // Settings: rows of two columns under group headings. MaxSettingsHeight is what the canvas reserves; the layout asserts it fits.
    private const float SettingRowHeight = 32, GroupHeaderHeight = 26, GroupGap = 8, SettingsTop = 12, SettingsBottom = 14, SettingsPad = 20, ColumnGap = 28;
    private const float SwitchWidth = 36, SwitchHeight = 20, SegmentHeight = 24, MaxSettingsHeight = 300;
    private const float Pad = 20, FileTileHeight = 84;
    private const float ListTop = 44, ClipRow = 40, TaskRow = 34, ButtonHeight = 26;
    private const int ClipRows = 4, TaskRows = 5, FileColumns = 5, FileTileRows = 2, DayTaskRows = 5;
    public static float MaxHeight => HeaderHeight + Math.Max(MaxSettingsHeight, PageBody);

    private static readonly string Weekdays = "日月火水木金土";
    private readonly List<PanelHit> _hits = [];
    private Workspace? _workspace;
    private DateOnly _month = FirstOfMonth(DateOnly.FromDateTime(DateTime.Today));

    public int Page { get; set; }
    public PanelHit? Hover { get; set; }
    public DateOnly SelectedDay { get; private set; } = DateOnly.FromDateTime(DateTime.Today);

    /// <summary>The day a task being typed is due on, or null for none; read when <see cref="PanelResult.PromptTask"/> came back.</summary>
    public DateOnly? PromptDue { get; private set; }

    /// <summary>A short word under the page's title about what the last click did ("コピーしました").</summary>
    public string? Flash { get; set; }

    /// <summary>Files are being dragged over the island, so the file tray lights up.</summary>
    public bool DropHot { get; set; }

    /// <summary>The pictures of the files in the tray.</summary>
    public FileIcons Icons { get; } = new();
    private float _scale = 1;

    public int HoverRow { get; set; } = -1;

    /// <summary>How many files the tray holds, for the Dock's badge.</summary>
    public int FileCount => _workspace?.Files.Count ?? 0;
    private SettingRow[] _settings = [];
    private float[] _knobs = [];

    // Settings layout, in surface coordinates: one rectangle per row and a heading per group.
    private RectangleF[] _rowRects = [];
    private (string Title, RectangleF Rect)[] _groupHeads = [];
    private float _settingsHeight = 72;

    public void SetWorkspace(Workspace workspace) => _workspace = workspace;

    private static DateOnly FirstOfMonth(DateOnly day) => new(day.Year, day.Month, 1);

    public void SetSettings(SettingRow[] rows)
    {
        _settings = rows;
        var colWidth = (Width - 2 * SettingsPad - ColumnGap) / 2;
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
        _settingsHeight = rows.Length == 0 ? 72 : Math.Max(y[0], y[1]) - GroupGap + SettingsBottom;
        System.Diagnostics.Debug.Assert(_settingsHeight <= MaxSettingsHeight, "settings page is taller than the canvas reserves");
    }

    /// <returns>True when a value changed, so the cached bitmap is stale.</returns>
    public bool SetAnimation(float[] knobs)
    {
        var changed = !knobs.AsSpan().SequenceEqual(_knobs);
        _knobs = (float[])knobs.Clone();
        return changed;
    }

    /// <summary>A page is as tall as what it holds, up to <see cref="PageBody"/>: a short list or an empty tray does not leave a blank well.</summary>
    public float Height => Page switch
    {
        SettingsPage => _settingsHeight,
        ClipboardPage => ListHeight(_workspace?.Clips.Count ?? 0, ClipRows, ClipRow),
        TasksPage => ListHeight(_workspace?.Tasks.Count ?? 0, TaskRows, TaskRow),
        FilesPage => (_workspace?.Files.Count ?? 0) switch { 0 => 132, <= FileColumns => HeaderHeight + ListTop + FileTileHeight + 12, _ => PageBody },
        _ => PageBody
    };

    private static float ListHeight(int count, int rows, float rowHeight) =>
        count == 0 ? 120 : HeaderHeight + ListTop + Math.Min(count, rows) * rowHeight + 8;

    public override SizeF Size => new(Width, Height);
    public override float Radius => 32;

    /// <summary>The control under a point (the topmost one), from the last drawing.</summary>
    public PanelHit? HitAt(PointF point)
    {
        for (var i = _hits.Count - 1; i >= 0; i--)
            if (_hits[i].Rect.Contains(point)) return _hits[i];
        return null;
    }

    /// <summary>Carries out a click on a control.</summary>
    public PanelResult Activate(PanelHit hit)
    {
        var ws = _workspace;
        if (ws is null) return PanelResult.None;
        var index = hit.Index;
        switch (hit.Action)
        {
            case PanelAction.MonthPrev: _month = _month.AddMonths(-1); break;
            case PanelAction.MonthNext: _month = _month.AddMonths(1); break;
            case PanelAction.Today:
                SelectedDay = DateOnly.FromDateTime(DateTime.Today);
                _month = FirstOfMonth(SelectedDay);
                break;
            case PanelAction.Day:
                SelectedDay = DateOnly.FromDayNumber(index);
                _month = FirstOfMonth(SelectedDay);
                break;
            case PanelAction.TaskToggle when index < ws.Tasks.Count: ws.ToggleTask(ws.Tasks[index].Id); break;
            case PanelAction.TaskDelete when index < ws.Tasks.Count: ws.DeleteTask(ws.Tasks[index].Id); break;
            case PanelAction.TaskNew:
                PromptDue = index == 1 ? SelectedDay : null;
                return PanelResult.PromptTask;
            case PanelAction.TaskClearDone: ws.ClearDoneTasks(); break;
            case PanelAction.ClipCopy: Flash = ws.CopyClip(index) ? "コピーしました" : "コピーできませんでした"; break;
            case PanelAction.ClipToTask when index < ws.Clips.Count:
                Flash = ws.AddTask(ws.Clips[index].Text, null) ? "タスクに追加しました" : "タスクを追加できません";
                break;
            case PanelAction.ClipDelete: ws.RemoveClip(index); break;
            case PanelAction.ClipClear: ws.ClearClips(); break;
            case PanelAction.FileOpen when index < ws.Files.Count: Workspace.Open(ws.Files[index]); break;
            case PanelAction.FileRemove: ws.RemoveFile(index); break;
            case PanelAction.FileAddClip:
                var added = ws.AddFilesFromClipboard();
                Flash = added > 0 ? $"{added} 件追加しました" : "クリップボードにファイルがありません";
                break;
            case PanelAction.FileClear: ws.ClearFiles(); break;
        }
        return PanelResult.Redraw;
    }

    /// <summary>The path of a file-tray row, for dragging it out.</summary>
    public string? FileAt(int index) => _workspace is { } ws && index >= 0 && index < ws.Files.Count ? ws.Files[index] : null;

    /// <summary>The wheel on the calendar turns its months.</summary>
    public bool ScrollMonth(int direction)
    {
        if (Page != CalendarPage) return false;
        _month = _month.AddMonths(direction);
        return true;
    }

    public override Bitmap Render(IslandStyle st, float scale)
    {
        _scale = scale;
        var (bitmap, g) = st.NewCanvas(Width, Height, scale);
        using (g)
        {
            _hits.Clear();
            switch (Page)
            {
                case CalendarPage: DrawCalendar(g, st); break;
                case ClipboardPage: DrawClipboard(g, st); break;
                case TasksPage: DrawTasks(g, st); break;
                case FilesPage: DrawFiles(g, st); break;
                default: DrawSettings(g, st); break;
            }
        }
        return bitmap;
    }

    // ---- header ----

    private bool IsHot(PanelAction action, int index) => Hover is { } hover && hover.Action == action && hover.Index == index;

    private RectangleF Add(RectangleF rect, PanelAction action, int index = 0)
    {
        _hits.Add(new PanelHit(rect, action, index));
        return rect;
    }

    private static void Chevron(Graphics g, RectangleF rect, int direction, bool hot)
    {
        var (cx, cy) = (rect.X + rect.Width / 2, rect.Y + rect.Height / 2);
        using var pen = new Pen(hot ? IslandStyle.Primary : IslandStyle.Secondary, 1.9f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        var d = direction;
        g.DrawLines(pen, [new PointF(cx - d * 2, cy - 5), new PointF(cx + d * 2.5f, cy), new PointF(cx - d * 2, cy + 5)]);
    }

    // ---- shared pieces ----

    private float ButtonWidth(string label) => style.MeasureWidth(label, style.Caption) + 24;

    private void TextButton(Graphics g, RectangleF rect, string label, PanelAction action, int index = 0)
    {
        Add(rect, action, index);
        var hot = IsHot(action, index);
        using (var path = IslandStyle.ContinuousRect(rect, 9, 0))
        using (var fill = new SolidBrush(hot ? IslandStyle.Alpha3 : IslandStyle.Alpha2))
            g.FillPath(fill, path);
        IslandStyle.Text(g, label, style.Caption, hot ? IslandStyle.Primary : IslandStyle.Secondary,
            rect.X + (rect.Width - style.MeasureWidth(label, style.Caption)) / 2, rect.Y + rect.Height / 2 + 4);
    }

    /// <summary>The row of buttons at the top of a list page, right-aligned, the first label being the rightmost.</summary>
    private void HeaderButtons(Graphics g, params (string Label, PanelAction Action, int Index)[] buttons)
    {
        var right = Width - Pad;
        foreach (var (label, action, index) in buttons)
        {
            var rect = new RectangleF(right - ButtonWidth(label), HeaderHeight + 9, ButtonWidth(label), ButtonHeight);
            TextButton(g, rect, label, action, index);
            right = rect.X - 8;
        }
    }

    /// <summary>The page's title line: what it holds, and the word about the last click.</summary>
    private void Caption(Graphics g, string text)
    {
        IslandStyle.Text(g, text, style.Caption, IslandStyle.Tertiary, Pad, HeaderHeight + 27);
        if (Flash is { Length: > 0 } flash)
            IslandStyle.Text(g, flash, style.Caption, IslandStyle.Success, Pad + style.MeasureWidth(text, style.Caption) + 12, HeaderHeight + 27);
    }

    private void RowHover(Graphics g, RectangleF row, bool hot)
    {
        if (!hot) return;
        using var path = IslandStyle.ContinuousRect(row, 12, 0);
        using var fill = new SolidBrush(IslandStyle.Alpha1);
        g.FillPath(fill, path);
    }

    private void Empty(Graphics g, string text) =>
        IslandStyle.Text(g, text, style.Body, IslandStyle.Secondary, (Width - style.MeasureWidth(text, style.Body)) / 2, HeaderHeight + Height / 2 + 8);

    private static void Check(Graphics g, RectangleF box, bool done, bool hot)
    {
        if (done)
        {
            using var fill = new SolidBrush(IslandStyle.Success);
            g.FillEllipse(fill, box);
            using var tick = new Pen(Color.FromArgb(18, 18, 18), 1.8f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
            var (cx, cy) = (box.X + box.Width / 2, box.Y + box.Height / 2);
            g.DrawLines(tick, [new PointF(cx - 3.5f, cy + 0.3f), new PointF(cx - 1, cy + 3), new PointF(cx + 3.5f, cy - 2.5f)]);
            return;
        }
        using var ring = new Pen(hot ? IslandStyle.Secondary : IslandStyle.Alpha4, 1.6f);
        g.DrawEllipse(ring, RectangleF.Inflate(box, -0.8f, -0.8f));
    }

    private static void Cross(Graphics g, RectangleF box, bool hot)
    {
        var (cx, cy) = (box.X + box.Width / 2, box.Y + box.Height / 2);
        using var pen = new Pen(hot ? IslandStyle.Primary : IslandStyle.Alpha4, 1.6f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        g.DrawLine(pen, cx - 3.5f, cy - 3.5f, cx + 3.5f, cy + 3.5f);
        g.DrawLine(pen, cx - 3.5f, cy + 3.5f, cx + 3.5f, cy - 3.5f);
    }

    /// <summary>One line of a text that may span several: line breaks and tabs become spaces.</summary>
    private static string OneLine(string text) => string.Join(' ', text.Split(['\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    // ---- calendar ----

    private static readonly Color Red = Color.FromArgb(255, 69, 58);

    private void DrawCalendar(Graphics g, IslandStyle st)
    {
        // Left: the chosen day and what is due on it. Right: the month, today as a red disc.
        const float cell = 30, rowHeight = 26, gridX = Width - Pad - 7 * cell;
        var top = HeaderHeight;
        var ws = _workspace;
        var today = DateOnly.FromDateTime(DateTime.Today);

        // month line: arrows and 今日 on the left of the grid, the month's name on the right
        var prev = new RectangleF(gridX, top + 9, 24, 24);
        var next = new RectangleF(prev.Right + 6, top + 9, 24, 24);
        foreach (var (rect, direction, action) in new[] { (prev, -1, PanelAction.MonthPrev), (next, 1, PanelAction.MonthNext) })
        {
            Add(rect, action);
            var hot = IsHot(action, 0);
            using (var fill = new SolidBrush(hot ? IslandStyle.Alpha3 : IslandStyle.Alpha2)) g.FillEllipse(fill, rect);
            Chevron(g, rect, direction, hot);
        }
        TextButton(g, new RectangleF(next.Right + 8, top + 9, ButtonWidth("今日"), 24), "今日", PanelAction.Today);
        var month = $"{(_month.Year == today.Year ? "" : _month.Year + "年")}{_month.Month}月";
        style.RightAligned(g, month, st.Name, Red, Width - Pad, top + 27);

        for (var i = 0; i < 7; i++)
        {
            var label = Weekdays[i].ToString();
            IslandStyle.Text(g, label, st.Caption, IslandStyle.Tertiary, gridX + i * cell + (cell - st.MeasureWidth(label, st.Caption)) / 2, top + 52);
        }

        var dueDays = ws is null ? [] : ws.Tasks.Where(task => !task.Done && task.Due is not null).Select(task => task.Due!.Value).ToHashSet();
        var start = _month.AddDays(-(int)_month.DayOfWeek);
        for (var i = 0; i < 42; i++)
        {
            var day = start.AddDays(i);
            var rect = new RectangleF(gridX + i % 7 * cell, top + 58 + i / 7 * rowHeight, cell, rowHeight);
            Add(rect, PanelAction.Day, day.DayNumber);
            var disc = new RectangleF(rect.X + rect.Width / 2 - 12, rect.Y + 1, 24, 24);
            var isToday = day == today;
            var selected = day == SelectedDay;
            if (isToday || selected || IsHot(PanelAction.Day, day.DayNumber))
            {
                using var fill = new SolidBrush(isToday ? Red : selected ? IslandStyle.Alpha3 : IslandStyle.Alpha2);
                g.FillEllipse(fill, disc);
            }
            var weekend = day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;
            var color = isToday ? Color.White
                : day.Month != _month.Month ? IslandStyle.Alpha3
                : weekend ? IslandStyle.Tertiary : IslandStyle.Primary;
            var font = isToday ? st.Name : st.Body;
            var number = day.Day.ToString();
            IslandStyle.Text(g, number, font, color, disc.X + (disc.Width - st.MeasureWidth(number, font)) / 2, disc.Y + 17);
            if (dueDays.Contains(day))
                using (var dot = new SolidBrush(isToday ? Color.White : IslandStyle.Amber))
                    g.FillEllipse(dot, disc.X + disc.Width / 2 - 1.5f, disc.Bottom - 4, 3, 3);
        }

        // the chosen day: 土 31日 第44週, a plus to add a task, and what is due
        var left = Pad;
        var width = gridX - 24 - left;
        var weekday = Weekdays[(int)SelectedDay.DayOfWeek].ToString();
        IslandStyle.Text(g, weekday, st.Name, SelectedDay == today ? Red : IslandStyle.Primary, left, top + 27);
        var dayText = $"{SelectedDay.Day}日";
        var dayX = left + st.MeasureWidth(weekday, st.Name) + 8;
        IslandStyle.Text(g, dayText, st.Name, IslandStyle.Primary, dayX, top + 27);
        var weekText = $"第{System.Globalization.ISOWeek.GetWeekOfYear(SelectedDay.ToDateTime(TimeOnly.MinValue))}週";
        IslandStyle.Text(g, weekText, st.Caption, IslandStyle.Tertiary, dayX + st.MeasureWidth(dayText, st.Name) + 8, top + 26.5f);
        var plus = new RectangleF(left + width - 24, top + 9, 24, 24);
        Add(plus, PanelAction.TaskNew, 1);
        var plusHot = IsHot(PanelAction.TaskNew, 1);
        using (var fill = new SolidBrush(plusHot ? IslandStyle.Alpha3 : IslandStyle.Alpha2)) g.FillEllipse(fill, plus);
        using (var pen = new Pen(plusHot ? IslandStyle.Primary : IslandStyle.Secondary, 1.8f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
        {
            var (cx, cy) = (plus.X + plus.Width / 2, plus.Y + plus.Height / 2);
            g.DrawLine(pen, cx - 5, cy, cx + 5, cy);
            g.DrawLine(pen, cx, cy - 5, cx, cy + 5);
        }

        if (Flash is { Length: > 0 } flash) IslandStyle.TextTrimmed(g, flash, st.Caption, IslandStyle.Success, left, top + 48, width);
        var due = ws is null ? [] : ws.Tasks.Select((task, index) => (task, index)).Where(item => item.task.Due == SelectedDay).Take(DayTaskRows).ToArray();
        if (due.Length == 0)
        {
            IslandStyle.Text(g, "予定なし", st.Name, IslandStyle.Tertiary, left, top + 70);
            IslandStyle.Text(g, "この日はタスクがありません", st.Body, IslandStyle.Alpha4, left, top + 90);
        }
        for (var i = 0; i < due.Length; i++)
        {
            var row = new RectangleF(left - 8, top + 56 + i * 28, width + 16, 26);
            Add(row, PanelAction.TaskToggle, due[i].index);
            RowHover(g, row, IsHot(PanelAction.TaskToggle, due[i].index));
            Check(g, new RectangleF(left, row.Y + 4, 18, 18), due[i].task.Done, IsHot(PanelAction.TaskToggle, due[i].index));
            IslandStyle.TextTrimmed(g, due[i].task.Text, st.Body, due[i].task.Done ? IslandStyle.Tertiary : IslandStyle.Primary, left + 28, row.Y + 17.5f, width - 28);
        }
    }

    // ---- clipboard ----

    private void DrawClipboard(Graphics g, IslandStyle st)
    {
        var clips = _workspace?.Clips ?? [];
        Caption(g, $"クリップボードの履歴 · {clips.Count} 件");
        if (clips.Count > 0) HeaderButtons(g, ("すべて消去", PanelAction.ClipClear, 0));
        if (clips.Count == 0)
        {
            Empty(g, "コピーした文字がここに並びます");
            return;
        }
        // Each copy is a card: a tile saying what it is (a link or text), its words, how big and how long ago; the pointer shows its buttons.
        for (var i = 0; i < Math.Min(ClipRows, clips.Count); i++)
        {
            var clip = clips[i];
            var row = new RectangleF(Pad - 8, HeaderHeight + ListTop + i * ClipRow, Width - 2 * Pad + 16, ClipRow - 6);
            var copy = Add(row, PanelAction.ClipCopy, i);
            var delete = new RectangleF(row.Right - 10 - 24, row.Y + (row.Height - 24) / 2, 24, 24);
            var task = new RectangleF(delete.X - 6 - 24, delete.Y, 24, 24);
            var hot = IsHot(PanelAction.ClipCopy, i) || IsHot(PanelAction.ClipToTask, i) || IsHot(PanelAction.ClipDelete, i);
            using (var path = IslandStyle.ContinuousRect(row, 14, 0))
            using (var fill = new SolidBrush(hot ? IslandStyle.Alpha2 : IslandStyle.Alpha1))
                g.FillPath(fill, path);
            var tile = new RectangleF(row.X + 8, row.Y + (row.Height - 24) / 2, 24, 24);
            using (var path = IslandStyle.ContinuousRect(tile, 8, 0))
            using (var fill = new SolidBrush(IslandStyle.Alpha2))
                g.FillPath(fill, path);
            DrawClipGlyph(g, tile, clip.Text.StartsWith("http://") || clip.Text.StartsWith("https://"));
            var right = hot ? task.X - 8 : row.Right - 12;
            var textX = tile.Right + 10;
            IslandStyle.TextTrimmed(g, OneLine(clip.Text), st.Body, IslandStyle.Primary, textX, copy.Y + 14.5f, right - textX);
            IslandStyle.Text(g, $"{clip.Text.Length} 文字 · {clip.At:HH:mm}", st.Caption, IslandStyle.Tertiary, textX, copy.Y + 28.5f);
            if (!hot) continue;
            IconButton(g, task, PanelAction.ClipToTask, i, bin: false);
            IconButton(g, delete, PanelAction.ClipDelete, i, bin: true);
        }
    }

    private static void DrawClipGlyph(Graphics g, RectangleF tile, bool link)
    {
        var (cx, cy) = (tile.X + tile.Width / 2, tile.Y + tile.Height / 2);
        using var pen = new Pen(IslandStyle.Secondary, 1.6f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        if (link)
        {
            // two links of a chain
            g.DrawArc(pen, cx - 7.5f, cy - 3.5f, 9, 7, 90, 180);
            g.DrawArc(pen, cx - 1.5f, cy - 3.5f, 9, 7, -90, 180);
            g.DrawLine(pen, cx - 3, cy, cx + 3, cy);
            return;
        }
        for (var i = 0; i < 3; i++) g.DrawLine(pen, cx - 6, cy - 4.5f + i * 4.5f, cx + (i == 2 ? 1 : 6), cy - 4.5f + i * 4.5f);
    }

    /// <summary>A round button with a glyph: a plus (make it a task) or a bin (remove it).</summary>
    private void IconButton(Graphics g, RectangleF rect, PanelAction action, int index, bool bin)
    {
        Add(rect, action, index);
        var hot = IsHot(action, index);
        using (var fill = new SolidBrush(hot ? IslandStyle.Alpha4 : IslandStyle.Alpha3)) g.FillEllipse(fill, rect);
        var (cx, cy) = (rect.X + rect.Width / 2, rect.Y + rect.Height / 2);
        using var pen = new Pen(IslandStyle.Primary, 1.6f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        if (!bin)
        {
            g.DrawLine(pen, cx - 5, cy, cx + 5, cy);
            g.DrawLine(pen, cx, cy - 5, cx, cy + 5);
            return;
        }
        g.DrawLine(pen, cx - 5, cy - 4, cx + 5, cy - 4); // lid
        g.DrawLine(pen, cx - 1.5f, cy - 5.5f, cx + 1.5f, cy - 5.5f);
        g.DrawLines(pen, [new PointF(cx - 3.5f, cy - 2), new PointF(cx - 3, cy + 5), new PointF(cx + 3, cy + 5), new PointF(cx + 3.5f, cy - 2)]);
    }

    // ---- tasks ----

    private void DrawTasks(Graphics g, IslandStyle st)
    {
        var tasks = _workspace?.Tasks ?? [];
        var open = tasks.Count(task => !task.Done);
        Caption(g, $"未完了 {open} 件 · 完了 {tasks.Count - open} 件");
        HeaderButtons(g, ("＋ 追加", PanelAction.TaskNew, 0), ("完了を消去", PanelAction.TaskClearDone, 0));
        if (tasks.Count == 0)
        {
            Empty(g, "「＋ 追加」でタスクを入力できます");
            return;
        }
        // Open tasks first, in the order they were made; the finished ones after them.
        var order = tasks.Select((task, index) => (task, index)).OrderBy(item => item.task.Done).Take(TaskRows).ToArray();
        using var struck = new Font(st.Body, FontStyle.Strikeout);
        for (var i = 0; i < order.Length; i++)
        {
            var (task, index) = order[i];
            var row = new RectangleF(Pad - 8, HeaderHeight + ListTop + i * TaskRow, Width - 2 * Pad + 16, TaskRow - 4);
            Add(row, PanelAction.TaskToggle, index);
            var cross = Add(new RectangleF(row.Right - 30, row.Y + (row.Height - 24) / 2, 24, 24), PanelAction.TaskDelete, index);
            var hot = IsHot(PanelAction.TaskToggle, index) || IsHot(PanelAction.TaskDelete, index);
            RowHover(g, row, hot);
            Check(g, new RectangleF(Pad + 2, row.Y + (row.Height - 18) / 2, 18, 18), task.Done, hot);
            var right = cross.X - 8;
            if (task.Due is { } due)
            {
                var label = due == DateOnly.FromDateTime(DateTime.Today) ? "今日" : $"{due.Month}/{due.Day}";
                var color = !task.Done && due < DateOnly.FromDateTime(DateTime.Today) ? IslandStyle.Warning : IslandStyle.Tertiary;
                style.RightAligned(g, label, st.Caption, color, right, row.Y + row.Height / 2 + 4);
                right -= st.MeasureWidth(label, st.Caption) + 12;
            }
            IslandStyle.TextTrimmed(g, task.Text, task.Done ? struck : st.Body, task.Done ? IslandStyle.Tertiary : IslandStyle.Primary, Pad + 32, row.Y + row.Height / 2 + 4.5f, right - (Pad + 32));
            Cross(g, cross, IsHot(PanelAction.TaskDelete, index));
        }
    }

    // ---- file tray ----

    private void DrawFiles(Graphics g, IslandStyle st)
    {
        var files = _workspace?.Files ?? [];
        Caption(g, $"ファイルトレイ · {files.Count} 件");
        HeaderButtons(g, ("クリップボードから追加", PanelAction.FileAddClip, 0), ("すべて消去", PanelAction.FileClear, 0));
        var zone = new RectangleF(Pad - 8, HeaderHeight + ListTop - 4, Width - 2 * Pad + 16, Height - ListTop - 6);
        if (DropHot)
        {
            using var path = IslandStyle.ContinuousRect(zone, 16, 0);
            using var fill = new SolidBrush(IslandStyle.Alpha1);
            using var edge = new Pen(IslandStyle.Success, 1.6f) { DashStyle = DashStyle.Dash };
            g.FillPath(fill, path);
            g.DrawPath(edge, path);
        }
        if (files.Count == 0)
        {
            Icons.Keep(files);
            if (!DropHot)
            {
                using var path = IslandStyle.ContinuousRect(zone, 16, 0);
                using var edge = new Pen(IslandStyle.Alpha3, 1.4f) { DashStyle = DashStyle.Dash };
                g.DrawPath(edge, path);
            }
            DrawTrayGlyph(g, new PointF(Width / 2, HeaderHeight + ListTop + 22));
            var hint = DropHot ? "ファイルトレイへドロップ" : "ファイルをここにドロップ";
            IslandStyle.Text(g, hint, st.Body, IslandStyle.Secondary, (Width - st.MeasureWidth(hint, st.Body)) / 2, HeaderHeight + ListTop + 66);
            return;
        }
        // Each file is a tile like Finder's icon view: the picture Explorer shows for it, its name under it, and a cross on the picture while the pointer is on it.
        const float tileWidth = (Width - 2 * Pad) / FileColumns, tileHeight = FileTileHeight, picture = 48;
        var shown = files.Take(FileColumns * FileTileRows).ToArray();
        Icons.Keep(files);
        for (var i = 0; i < shown.Length; i++)
        {
            var path = shown[i];
            var tile = new RectangleF(Pad + i % FileColumns * tileWidth, HeaderHeight + ListTop + i / FileColumns * tileHeight, tileWidth, tileHeight - 2);
            Add(tile, PanelAction.FileOpen, i);
            var hot = IsHot(PanelAction.FileOpen, i) || IsHot(PanelAction.FileRemove, i);
            RowHover(g, tile, hot);
            var folder = Directory.Exists(path);
            var missing = !folder && !File.Exists(path);
            var box = new RectangleF(tile.X + (tile.Width - picture) / 2, tile.Y + 6, picture, picture);
            var icon = missing ? null : Icons.Get(path, (int)Math.Ceiling(picture * _scale));
            if (icon is not null)
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.DrawImage(icon, box);
            }
            else DrawFileGlyph(g, new RectangleF(box.X + (picture - 24) / 2, box.Y + (picture - 26) / 2, 24, 26), folder, missing);
            var name = Path.GetFileName(path.TrimEnd('\\', '/'));
            var label = missing ? "見つかりません" : name.Length > 0 ? name : path;
            var width = Math.Min(tile.Width - 6, st.MeasureWidth(label, st.Caption));
            IslandStyle.TextTrimmed(g, label, st.Caption, missing ? IslandStyle.Amber : IslandStyle.Primary, tile.X + (tile.Width - width) / 2, box.Bottom + 17, tile.Width - 6);
            if (!hot) continue;
            var cross = Add(new RectangleF(tile.Right - 22, tile.Y + 1, 18, 18), PanelAction.FileRemove, i);
            using (var fill = new SolidBrush(IsHot(PanelAction.FileRemove, i) ? IslandStyle.Alpha4 : IslandStyle.Alpha3)) g.FillEllipse(fill, cross);
            Cross(g, cross, true);
        }
    }

    /// <summary>An inbox tray with an arrow dropping into it, centred on a point.</summary>
    private static void DrawTrayGlyph(Graphics g, PointF centre)
    {
        using var pen = new Pen(IslandStyle.Secondary, 1.8f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        var (cx, cy) = (centre.X, centre.Y);
        g.DrawLines(pen, [new PointF(cx - 12, cy + 2), new PointF(cx - 12, cy + 11), new PointF(cx + 12, cy + 11), new PointF(cx + 12, cy + 2)]);
        g.DrawLines(pen, [new PointF(cx - 12, cy + 2), new PointF(cx - 5, cy + 2), new PointF(cx - 3, cy + 6), new PointF(cx + 3, cy + 6), new PointF(cx + 5, cy + 2), new PointF(cx + 12, cy + 2)]);
        g.DrawLine(pen, cx, cy - 12, cx, cy + 2);
        g.DrawLines(pen, [new PointF(cx - 5, cy - 3), new PointF(cx, cy + 2), new PointF(cx + 5, cy - 3)]);
    }

    private static void DrawFileGlyph(Graphics g, RectangleF box, bool folder, bool missing)
    {
        var color = missing ? IslandStyle.Alpha4 : folder ? IslandStyle.Amber : IslandStyle.Secondary;
        using var pen = new Pen(color, 1.6f) { LineJoin = LineJoin.Round };
        if (folder)
        {
            var body = new RectangleF(box.X, box.Y + 5, box.Width, box.Height - 9);
            using var path = IslandStyle.ContinuousRect(body, 4, 0);
            g.DrawPath(pen, path);
            g.DrawLines(pen, [new PointF(box.X + 1, box.Y + 5), new PointF(box.X + 1, box.Y + 2.5f), new PointF(box.X + 8, box.Y + 2.5f), new PointF(box.X + 10, box.Y + 5)]);
            return;
        }
        var page = RectangleF.Inflate(box, -2, 0);
        using var outline = IslandStyle.ContinuousRect(page, 4, 0);
        g.DrawPath(pen, outline);
        g.DrawLine(pen, page.X + 4, page.Y + 10, page.Right - 4, page.Y + 10);
        g.DrawLine(pen, page.X + 4, page.Y + 15, page.Right - 4, page.Y + 15);
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

    public int SettingRowAt(PointF point)
    {
        if (Page != SettingsPage) return -1;
        for (var i = 0; i < _settings.Length; i++)
            if (RowHit(i).Contains(point)) return i;
        return -1;
    }

    private void DrawSettings(Graphics g, IslandStyle st)
    {
        foreach (var (title, rect) in _groupHeads)
        {
            IslandStyle.Text(g, title, st.Caption, IslandStyle.Tertiary, rect.X, rect.Y + 15);
            using var rule = new Pen(IslandStyle.Alpha2, 1);
            g.DrawLine(rule, rect.X, rect.Bottom - 4.5f, rect.Right, rect.Bottom - 4.5f);
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
            IslandStyle.TextTrimmed(g, row.Label, st.Body, IslandStyle.Primary, rect.X, rect.Y + 20.5f, labelWidth);
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
            st.Segments(g, controls, row.Options, row.Selected, -1);
        }
    }
}
