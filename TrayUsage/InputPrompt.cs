using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace TrayUsage;

/// <summary>
/// A one-line bar for typing, shown under the island's Dock and as wide as the island. The island itself never takes the keyboard (it is
/// click-through and does not activate, so it cannot host a text box or an IME), so what has to be typed (a task) goes into this bar.
/// Enter hands the text over, Esc or a click elsewhere closes it.
/// </summary>
internal sealed class InputPrompt : Form
{
    private readonly TextBox _box;
    private readonly string? _note;
    private readonly string _hint;
    private readonly float _scale;

    /// <param name="hint">Shown in the empty box.</param>
    /// <param name="note">What the text will be attached to ("10月31日"), shown on the right; null for nothing.</param>
    /// <param name="below">The island's area on screen: the bar goes under it, as wide as it is.</param>
    public InputPrompt(string hint, string? note, Rectangle below)
    {
        _note = note;
        _hint = hint;
        _scale = UsageIsland.MonitorScale(below);
        SuspendLayout();
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.Black;
        DoubleBuffered = true;
        var width = Math.Max(below.Width, (int)(360 * _scale));
        var height = (int)(48 * _scale);
        ClientSize = new Size(width, height);
        Location = new Point(below.X + below.Width / 2 - width / 2, below.Bottom + (int)(10 * _scale));
        _box = new TextBox
        {
            BorderStyle = BorderStyle.None,
            BackColor = Color.Black,
            ForeColor = IslandStyle.Primary,
            Font = new Font(SystemFonts.MessageBoxFont!.FontFamily, 11.5f),
            MaxLength = Workspace.MaxTaskChars
        };
        Controls.Add(_box);
        var left = (int)(54 * _scale);
        _box.SetBounds(left, (height - _box.PreferredHeight) / 2, width - left - (int)(RightReserve() * _scale), _box.PreferredHeight);
        ResumeLayout();
        _box.TextChanged += (_, _) => Invalidate(new Rectangle(_box.Left, 0, _box.Width, Height));
        _box.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.SuppressKeyPress = true;
                var text = _box.Text.Trim();
                Close();
                if (text.Length > 0) Submitted?.Invoke(text);
            }
            else if (e.KeyCode == Keys.Escape)
            {
                e.SuppressKeyPress = true;
                Close();
            }
        };
        Deactivate += (_, _) => Close();
    }

    /// <summary>The text typed, once Enter was pressed.</summary>
    public event Action<string>? Submitted;

    protected override bool ShowWithoutActivation => false;

    /// <summary>Width (in the island's units) kept free on the right for the note and the Enter key.</summary>
    private float RightReserve() => 60 + (_note is null ? 0 : TextRenderer.MeasureText(_note, Font).Width / _scale + 22);

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        // Windows 11 rounds the corners and draws the edge and shadow itself; older Windows ignores both.
        var round = 2; // DWMWCP_ROUND
        DwmSetWindowAttribute(Handle, 33, ref round, sizeof(int));
        var edge = 52 | 52 << 8 | 50 << 16; // COLORREF (BGR)
        DwmSetWindowAttribute(Handle, 34, ref edge, sizeof(int));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var s = _scale;
        var disc = new RectangleF(14 * s, (Height - 28 * s) / 2, 28 * s, 28 * s);
        using (var fill = new SolidBrush(IslandStyle.Alpha3)) g.FillEllipse(fill, disc);
        using (var pen = new Pen(IslandStyle.Primary, 1.8f * s) { StartCap = LineCap.Round, EndCap = LineCap.Round })
        {
            var (cx, cy) = (disc.X + disc.Width / 2, disc.Y + disc.Height / 2);
            g.DrawLine(pen, cx - 5 * s, cy, cx + 5 * s, cy);
            g.DrawLine(pen, cx, cy - 5 * s, cx, cy + 5 * s);
        }
        if (_box.TextLength == 0)
            TextRenderer.DrawText(g, _hint, _box.Font, new Point(_box.Left, _box.Top), IslandStyle.Tertiary, TextFormatFlags.NoPadding);
        // the Enter key, and what the task is for
        var key = new RectangleF(Width - 14 * s - 40 * s, (Height - 22 * s) / 2, 40 * s, 22 * s);
        using (var path = IslandStyle.ContinuousRect(key, 7 * s, 0))
        using (var fill = new SolidBrush(IslandStyle.Alpha2))
            g.FillPath(fill, path);
        using var small = new Font(Font.FontFamily, 9f);
        TextRenderer.DrawText(g, "Enter", small, Rectangle.Round(key), IslandStyle.Secondary, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        if (_note is null) return;
        var label = _note + " まで";
        var size = TextRenderer.MeasureText(label, small);
        TextRenderer.DrawText(g, label, small, new Point((int)(key.X - 10 * s - size.Width), (int)((Height - size.Height) / 2f)), IslandStyle.Tertiary, TextFormatFlags.NoPadding);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        // The app is not the foreground one (the island never activates), so ask for it; the click that opened this counts as input.
        SetForegroundWindow(Handle);
        Activate();
        _box.Focus();
    }

    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
}
