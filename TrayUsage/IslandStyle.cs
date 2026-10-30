using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;

namespace TrayUsage;

internal static class Agent
{
    public static string Name(AgentKind kind) => kind == AgentKind.Claude ? "Claude Code" : "Codex";
    public static Color Accent(AgentKind kind) => kind == AgentKind.Claude ? IslandStyle.ClaudeAccent : IslandStyle.CodexAccent;
}

internal static class TimeText
{
    /// <summary>"Resets 14:30" for a reset today, "Resets 10/31 14:30" for a later day.</summary>
    public static string ResetAt(DateTimeOffset time) =>
        time.Date == DateTime.Today ? $"Resets {time:HH:mm}" : $"Resets {time:M/d HH:mm}";

    public static string Elapsed(TimeSpan span)
    {
        if (span < TimeSpan.Zero) span = TimeSpan.Zero;
        return span.TotalHours >= 1 ? $"{(int)span.TotalHours}:{span.Minutes:00}:{span.Seconds:00}" : $"{span.Minutes}:{span.Seconds:00}";
    }
}

/// <summary>Palette, type scale (12 / 13 / 14 / 16, nothing smaller) and drawing helpers shared by every island surface.</summary>
internal sealed class IslandStyle : IDisposable
{
    // Text and fills follow the Claude Design System's warm gray ramp (Open Claude UI, MIT, cds-theme.css): gray-30 / gray-200 / gray-350
    // for text on black (about 19 : 1, 11 : 1, 7 : 1, where the old neutral grays gave 7 : 1 and 5 : 1), gray-750 for a raised pill,
    // and the white alpha steps 5 / 9 / 18 / 30 % for hover, hairlines, tracks and heavy dividers. See THIRD_PARTY_NOTICES.md.
    public static readonly Color Primary = Color.FromArgb(246, 246, 244);
    public static readonly Color Secondary = Color.FromArgb(195, 194, 183);
    public static readonly Color Tertiary = Color.FromArgb(151, 149, 141);
    public static readonly Color Thumb = Color.FromArgb(44, 44, 42);
    public static readonly Color Alpha1 = Color.FromArgb(13, 255, 255, 255);
    public static readonly Color Alpha2 = Color.FromArgb(23, 255, 255, 255);
    public static readonly Color Alpha3 = Color.FromArgb(46, 255, 255, 255);
    public static readonly Color Alpha4 = Color.FromArgb(77, 255, 255, 255);
    public static readonly Color Amber = Color.FromArgb(255, 214, 10);
    public static readonly Color Warning = Color.FromArgb(255, 69, 58);
    public static readonly Color Success = Color.FromArgb(48, 209, 88);
    public static readonly Color ClaudeAccent = Color.FromArgb(217, 119, 87); // CDS --cds-clay
    public static readonly Color CodexAccent = Color.FromArgb(138, 152, 255);

    /// <summary>Quota level colour: green under 70 % used, amber from 70, red from 90 (the scale Agent HUD uses).</summary>
    public static Color Level(double usedPercent) => usedPercent >= 90 ? Warning : usedPercent >= 70 ? Amber : Success;

    private static readonly StringFormat Typographic = StringFormat.GenericTypographic;

    private readonly FontFamily _display = Family("Noto Sans JP Medium", "Noto Sans JP", "Segoe UI Variable Display Semib", "Segoe UI Semibold");
    private readonly FontFamily _text = Family("Noto Sans JP", "Segoe UI Variable Text", "Segoe UI");
    private readonly Bitmap _measureBitmap = new(1, 1);
    private readonly Graphics _measure;

    public IslandStyle()
    {
        Name = new Font(_display, 14, FontStyle.Regular, GraphicsUnit.Pixel);
        Value = new Font(_display, 16, FontStyle.Regular, GraphicsUnit.Pixel);
        Body = new Font(_text, 13, FontStyle.Regular, GraphicsUnit.Pixel); // CDS chat body is 13.2 px
        Caption = new Font(_text, 12, FontStyle.Regular, GraphicsUnit.Pixel);
        ClaudeIcon = LoadIcon("claude-logo.png");
        CodexIcon = LoadIcon("codex-logo.png");
        _measure = Graphics.FromImage(_measureBitmap);
    }

    public Font Name { get; }
    public Font Value { get; }
    public Font Body { get; }
    public Font Caption { get; }
    public Image ClaudeIcon { get; }
    public Image CodexIcon { get; }

    public Image Icon(AgentKind kind) => kind == AgentKind.Claude ? ClaudeIcon : CodexIcon;

    public float MeasureWidth(string text, Font font) => _measure.MeasureString(text, font, PointF.Empty, Typographic).Width;

    /// <summary>Opaque black canvas with ClearType valid text, drawn in logical pixels through a scale transform.</summary>
    public (Bitmap Bitmap, Graphics Graphics) NewCanvas(float width, float height, float scale)
    {
        var bitmap = new Bitmap((int)Math.Round(width * scale), (int)Math.Round(height * scale), PixelFormat.Format32bppPArgb);
        var g = Graphics.FromImage(bitmap);
        g.Clear(Color.Black);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit; // opaque background keeps ClearType valid
        g.ScaleTransform(scale, scale);
        return (bitmap, g);
    }

    /// <summary>Draws text with its baseline at <paramref name="baseline"/>.</summary>
    public static void Text(Graphics g, string text, Font font, Color color, float x, float baseline)
    {
        if (text.Length == 0) return;
        using var brush = new SolidBrush(color);
        g.DrawString(text, font, brush, x, baseline - Ascent(font), Typographic);
    }

    public void RightAligned(Graphics g, string text, Font font, Color color, float right, float baseline) =>
        Text(g, text, font, color, right - MeasureWidth(text, font), baseline);

    public static void TextTrimmed(Graphics g, string text, Font font, Color color, float x, float baseline, float maxWidth)
    {
        if (text.Length == 0) return;
        using var brush = new SolidBrush(color);
        using var format = (StringFormat)Typographic.Clone();
        format.Trimming = StringTrimming.EllipsisCharacter;
        g.DrawString(text, font, brush, new RectangleF(x, baseline - Ascent(font), maxWidth, font.Height + 2), format);
    }

    private static StringFormat WrapFormat()
    {
        var format = (StringFormat)Typographic.Clone();
        format.FormatFlags = StringFormatFlags.LineLimit; // GenericTypographic is NoWrap; a paragraph wraps and ends in an ellipsis
        format.Trimming = StringTrimming.EllipsisWord;
        return format;
    }

    /// <summary>Height of <paramref name="text"/> wrapped to <paramref name="width"/>, capped at <paramref name="maxLines"/> lines.</summary>
    public float ParagraphHeight(string text, Font font, float width, int maxLines)
    {
        using var format = WrapFormat();
        var lines = (int)Math.Round(_measure.MeasureString(text, font, new SizeF(width, 10000), format).Height / font.Height);
        return Math.Clamp(lines, 1, maxLines) * font.Height;
    }

    public static void Paragraph(Graphics g, string text, Font font, Color color, float x, float top, float width, float height)
    {
        using var brush = new SolidBrush(color);
        using var format = WrapFormat();
        g.DrawString(text, font, brush, new RectangleF(x, top, width, height), format);
    }

    private static float Ascent(Font font)
    {
        var family = font.FontFamily;
        return font.Size * family.GetCellAscent(font.Style) / family.GetEmHeight(font.Style);
    }

    /// <summary>Rounded rectangle with continuous (squircle-like) corners, following Figma's corner smoothing.</summary>
    public static GraphicsPath ContinuousRect(RectangleF rect, float radius, float smoothing = 0.6f)
    {
        var path = new GraphicsPath();
        var budget = Math.Min(rect.Width, rect.Height) / 2;
        radius = Math.Clamp(radius, 0, budget);
        if (radius < 0.5f)
        {
            path.AddRectangle(rect);
            return path;
        }
        smoothing = Math.Clamp(Math.Min(smoothing, budget / radius - 1), 0, 1);
        var p = Math.Min((1 + smoothing) * radius, budget);
        var arcMeasure = 90 * (1 - smoothing);
        var arcLength = (float)(Math.Sin(Radians(arcMeasure / 2)) * radius * Math.Sqrt(2));
        var alphaAngle = (90 - arcMeasure) / 2;
        var p3ToP4 = (float)(radius * Math.Tan(Radians(alphaAngle / 2)));
        var beta = 45 * smoothing;
        var c = (float)(p3ToP4 * Math.Cos(Radians(beta)));
        var d = (float)(c * Math.Tan(Radians(beta)));
        var b = (p - arcLength - c - d) / 3;
        var a = 2 * b;

        // Corner vertex, incoming edge direction (u) and outgoing edge direction (v), clockwise from top-right.
        (PointF Vertex, PointF U, PointF V)[] corners =
        [
            (new(rect.Right, rect.Top), new(1, 0), new(0, 1)),
            (new(rect.Right, rect.Bottom), new(0, 1), new(-1, 0)),
            (new(rect.Left, rect.Bottom), new(-1, 0), new(0, -1)),
            (new(rect.Left, rect.Top), new(0, -1), new(1, 0))
        ];
        foreach (var (vertex, u, v) in corners)
        {
            PointF At(float back, float along) => new(vertex.X - back * u.X + along * v.X, vertex.Y - back * u.Y + along * v.Y);
            if (smoothing > 0) path.AddBezier(At(p, 0), At(p - a, 0), At(p - a - b, 0), At(p - a - b - c, d));
            var center = At(radius, radius);
            var start = At(p - a - b - c, d);
            var startAngle = (float)(Math.Atan2(start.Y - center.Y, start.X - center.X) * 180 / Math.PI);
            path.AddArc(center.X - radius, center.Y - radius, radius * 2, radius * 2, startAngle, arcMeasure);
            if (smoothing > 0) path.AddBezier(At(d, p - a - b - c), At(0, p - a - b), At(0, p - a), At(0, p));
        }
        path.CloseFigure();
        return path;
    }

    /// <summary>
    /// Outline of an island that hangs from the top screen edge: flat top, rounded bottom corners, and a concave
    /// flare ("shoulder") where each side meets the edge. Open (no top line) when <paramref name="closed"/> is false.
    /// </summary>
    public static GraphicsPath DockedPath(RectangleF body, float radius, float shoulder, bool closed = true, float topRadius = 0)
    {
        var path = new GraphicsPath();
        radius = Math.Clamp(radius, 0, Math.Min(body.Width / 2, Math.Max(0, body.Height - shoulder)));
        topRadius = Math.Clamp(topRadius, 0, Math.Min(body.Width / 2, body.Height / 2));
        var withShoulder = shoulder >= 0.5f;
        var withTop = topRadius >= 0.5f && !withShoulder; // the shoulders and the rounded top never coexist: one melts away before the other grows
        if (withShoulder) path.AddArc(body.Left - 2 * shoulder, body.Top, 2 * shoulder, 2 * shoulder, 270, 90);
        path.AddLine(body.Left, body.Top + (withShoulder ? shoulder : withTop ? topRadius : 0), body.Left, body.Bottom - radius);
        if (radius >= 0.5f) path.AddArc(body.Left, body.Bottom - 2 * radius, 2 * radius, 2 * radius, 180, -90);
        path.AddLine(body.Left + radius, body.Bottom, body.Right - radius, body.Bottom);
        if (radius >= 0.5f) path.AddArc(body.Right - 2 * radius, body.Bottom - 2 * radius, 2 * radius, 2 * radius, 90, -90);
        path.AddLine(body.Right, body.Bottom - radius, body.Right, body.Top + (withShoulder ? shoulder : withTop ? topRadius : 0));
        if (withShoulder) path.AddArc(body.Right, body.Top, 2 * shoulder, 2 * shoulder, 180, 90);
        else if (withTop)
        {
            path.AddArc(body.Right - 2 * topRadius, body.Top, 2 * topRadius, 2 * topRadius, 0, -90);
            path.AddLine(body.Right - topRadius, body.Top, body.Left + topRadius, body.Top);
            path.AddArc(body.Left, body.Top, 2 * topRadius, 2 * topRadius, 270, -90);
        }
        else path.AddLine(body.Right, body.Top, body.Left, body.Top);
        if (closed) path.CloseFigure();
        return path;
    }

    private static double Radians(double degrees) => degrees * Math.PI / 180;

    private static FontFamily Family(params string[] names)
    {
        foreach (var name in names)
        {
            try { return new FontFamily(name); }
            catch (ArgumentException) { }
        }
        return FontFamily.GenericSansSerif;
    }

    private static Image LoadIcon(string fileName)
    {
        using var stream = typeof(IslandStyle).Assembly.GetManifestResourceStream($"TrayUsage.Assets.{fileName}")
            ?? throw new InvalidOperationException($"Missing icon: {fileName}");
        using var image = Image.FromStream(stream);
        // Keep only a small raster, not the full source image.
        var thumbnail = new Bitmap(64, 64, PixelFormat.Format32bppPArgb);
        using var graphics = Graphics.FromImage(thumbnail);
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        graphics.DrawImage(image, new Rectangle(0, 0, 64, 64));
        return thumbnail;
    }

    public void Dispose()
    {
        _measure.Dispose();
        _measureBitmap.Dispose();
        Name.Dispose(); Value.Dispose(); Body.Dispose(); Caption.Dispose();
        _display.Dispose();
        _text.Dispose();
        ClaudeIcon.Dispose();
        CodexIcon.Dispose();
    }
}
