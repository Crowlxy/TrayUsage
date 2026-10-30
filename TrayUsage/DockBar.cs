using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace TrayUsage;

/// <summary>
/// The strip of icons under the island, like the Dock in the reference: home (the card), the four pages and the settings. It is not part of
/// any surface: the island draws it under whichever menu is up, and it is what moves between them.
/// </summary>
internal sealed class DockBar(IslandStyle style)
{
    public const int Home = 0, Settings = 5, Items = 6;
    public const float Height = 34, Gap = 10;
    private const float Pad = 5, SlotWidth = 32, SlotHeight = 26, SlotGap = 2, BadgeWidth = 14;
    private static readonly string[] Names = ["ホーム", "カレンダー", "クリップボード", "タスク", "ファイル", "設定"];

    /// <summary>The slot lit as the page in view, or -1.</summary>
    public int Selected { get; set; } = -1;

    /// <summary>The slot under the pointer, or -1.</summary>
    public int Hover { get; set; } = -1;

    /// <summary>How many files the tray holds: shown beside its icon.</summary>
    public int FileCount { get; set; }

    public static string NameOf(int item) => Names[item];

    private bool HasBadge => FileCount > 0;

    private float WidthOf(int item) => item == 4 && HasBadge ? SlotWidth + BadgeWidth : SlotWidth;

    public float Width => Pad * 2 + Enumerable.Range(0, Items).Sum(WidthOf) + (Items - 1) * SlotGap;

    private RectangleF Slot(int item)
    {
        var x = Pad + Enumerable.Range(0, item).Sum(WidthOf) + item * SlotGap;
        return new RectangleF(x, (Height - SlotHeight) / 2, WidthOf(item), SlotHeight);
    }

    /// <summary>The slot at a point of the dock (in its own units), or -1.</summary>
    public int ItemAt(PointF point)
    {
        for (var i = 0; i < Items; i++)
            if (RectangleF.Inflate(Slot(i), SlotGap / 2, 4).Contains(point)) return i;
        return -1;
    }

    public Bitmap Render(float scale)
    {
        var bitmap = new Bitmap((int)Math.Ceiling(Width * scale), (int)Math.Ceiling(Height * scale), PixelFormat.Format32bppPArgb);
        using var g = Graphics.FromImage(bitmap);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit; // the bitmap is transparent, so ClearType would fringe
        g.ScaleTransform(scale, scale);

        var body = new RectangleF(0.5f, 0.5f, Width - 1, Height - 1);
        using (var path = IslandStyle.ContinuousRect(body, Height / 2, 0))
        {
            using (var fill = new SolidBrush(Color.Black)) g.FillPath(fill, path);
            using var rim = new Pen(Color.FromArgb(30, 255, 255, 255), 1);
            g.DrawPath(rim, path);
        }
        for (var i = 0; i < Items; i++)
        {
            var slot = Slot(i);
            var selected = i == Selected;
            var hot = i == Hover;
            if (selected || hot)
            {
                using var path = IslandStyle.ContinuousRect(slot, SlotHeight / 2, 0);
                using var fill = new SolidBrush(selected ? IslandStyle.Thumb : IslandStyle.Alpha2);
                g.FillPath(fill, path);
            }
            var color = selected || hot ? IslandStyle.Primary : IslandStyle.Secondary;
            var centre = i == 4 && HasBadge ? new PointF(slot.X + SlotWidth / 2, slot.Y + slot.Height / 2) : new PointF(slot.X + slot.Width / 2, slot.Y + slot.Height / 2);
            Glyph(g, i, centre, color);
            if (i == 4 && HasBadge)
            {
                var count = Math.Min(FileCount, 99).ToString();
                IslandStyle.Text(g, count, style.Caption, color, slot.X + SlotWidth - 6, slot.Y + slot.Height / 2 + 4);
            }
        }
        return bitmap;
    }

    private static void Glyph(Graphics g, int item, PointF c, Color color)
    {
        using var pen = new Pen(color, 1.5f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        var (cx, cy) = (c.X, c.Y);
        switch (item)
        {
            case Home:
                g.DrawLines(pen, [new PointF(cx - 7, cy), new PointF(cx, cy - 6.5f), new PointF(cx + 7, cy)]);
                g.DrawLines(pen, [new PointF(cx - 5, cy - 2), new PointF(cx - 5, cy + 6), new PointF(cx + 5, cy + 6), new PointF(cx + 5, cy - 2)]);
                break;
            case 1: // calendar
                using (var path = IslandStyle.ContinuousRect(new RectangleF(cx - 6.5f, cy - 5.5f, 13, 12.5f), 3, 0)) g.DrawPath(pen, path);
                g.DrawLine(pen, cx - 6.5f, cy - 1.5f, cx + 6.5f, cy - 1.5f);
                g.DrawLine(pen, cx - 3, cy - 7.5f, cx - 3, cy - 4);
                g.DrawLine(pen, cx + 3, cy - 7.5f, cx + 3, cy - 4);
                break;
            case 2: // clipboard
                using (var path = IslandStyle.ContinuousRect(new RectangleF(cx - 5.5f, cy - 5.5f, 11, 13), 3, 0)) g.DrawPath(pen, path);
                using (var path = IslandStyle.ContinuousRect(new RectangleF(cx - 2.5f, cy - 7.5f, 5, 3.5f), 1.6f, 0)) g.DrawPath(pen, path);
                g.DrawLine(pen, cx - 2.5f, cy, cx + 2.5f, cy);
                g.DrawLine(pen, cx - 2.5f, cy + 3.2f, cx + 1, cy + 3.2f);
                break;
            case 3: // task: a circle with a tick
                g.DrawEllipse(pen, cx - 6.5f, cy - 6.5f, 13, 13);
                g.DrawLines(pen, [new PointF(cx - 3, cy + 0.2f), new PointF(cx - 0.8f, cy + 2.6f), new PointF(cx + 3.2f, cy - 2.4f)]);
                break;
            case 4: // tray
                g.DrawLines(pen, [new PointF(cx - 7, cy + 0.5f), new PointF(cx - 7, cy + 6), new PointF(cx + 7, cy + 6), new PointF(cx + 7, cy + 0.5f)]);
                g.DrawLines(pen, [new PointF(cx - 7, cy + 0.5f), new PointF(cx - 3, cy + 0.5f), new PointF(cx - 1.8f, cy + 3), new PointF(cx + 1.8f, cy + 3), new PointF(cx + 3, cy + 0.5f), new PointF(cx + 7, cy + 0.5f)]);
                g.DrawLine(pen, cx, cy - 6.5f, cx, cy + 0.5f);
                g.DrawLines(pen, [new PointF(cx - 3, cy - 3), new PointF(cx, cy + 0.2f), new PointF(cx + 3, cy - 3)]);
                break;
            default: // settings: three sliders
                for (var row = 0; row < 3; row++)
                {
                    var y = cy - 5 + row * 5;
                    g.DrawLine(pen, cx - 7, y, cx + 7, y);
                    var knobX = cx + (row == 1 ? 3.5f : -3.5f);
                    using var knob = new SolidBrush(Color.Black);
                    g.FillEllipse(knob, knobX - 2.2f, y - 2.2f, 4.4f, 4.4f);
                    g.DrawEllipse(pen, knobX - 2.2f, y - 2.2f, 4.4f, 4.4f);
                }
                break;
        }
    }
}
