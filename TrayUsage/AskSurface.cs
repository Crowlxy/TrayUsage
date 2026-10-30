using System.Drawing.Drawing2D;

namespace TrayUsage;

/// <summary>What a point on an <see cref="AskSurface"/> hits: a button, or one of the options of the question in view.</summary>
internal enum AskHit { None, Allow, Deny, Terminal, Option }

/// <summary>
/// An approval or a question that a hook is waiting on, answered from the island: an approval has what is about to run and
/// 許可 / 拒否; a question has its options as rows, one question at a time. "ターミナルで答える" gives it back to the terminal.
/// Multi-select and free-text answers are not offered here (the island takes no keyboard), so those never reach this surface.
/// </summary>
internal sealed class AskSurface : IslandSurface
{
    private const float Width = 400, Header = 60, Pad = 16, ButtonHeight = 38, RowHeight = 42, RowGap = 6, FooterHeight = 34, TileInset = 12;
    private readonly IslandStyle _style;
    private readonly Dictionary<string, string> _answers = new();
    private float _height;
    private RectangleF _allow, _deny, _terminal, _tile;
    private readonly List<RectangleF> _options = [];
    private string _text = "";
    private float _textHeight;

    public AskSurface(IslandStyle style, AskRequest request)
    {
        _style = style;
        Request = request;
        Layout();
    }

    public AskRequest Request { get; }
    public int QuestionIndex { get; private set; }
    public AskHit Hover { get; set; }
    public int HoverOption { get; set; } = -1;
    public IReadOnlyDictionary<string, string> Answers => _answers;
    public override SizeF Size => new(Width, _height);
    public override float Radius => 26;

    private AskQuestion? Question => Request.IsQuestion ? Request.Questions[QuestionIndex] : null;
    private Color Accent => Request.IsQuestion ? NoticeLook.Question : NoticeLook.Approval;

    private void Layout()
    {
        var inner = Width - 2 * Pad;
        var y = Header;
        _options.Clear();
        if (Question is { } question)
        {
            _text = question.Question;
            _textHeight = _style.ParagraphHeight(_text, _style.Name, inner, 3);
            y += _textHeight + 12;
            foreach (var _ in question.Options)
            {
                _options.Add(new RectangleF(Pad, y, inner, RowHeight));
                y += RowHeight + RowGap;
            }
            y += 2 - RowGap;
        }
        else
        {
            _text = Request.Detail ?? Request.Tool ?? "";
            var textWidth = inner - 2 * TileInset;
            _textHeight = _style.ParagraphHeight(_text, _style.Body, textWidth, 5);
            _tile = new RectangleF(Pad, y, inner, _textHeight + 2 * TileInset);
            y = _tile.Bottom + 14;
            var denyWidth = (inner - 8) * 0.38f;
            _deny = new RectangleF(Pad, y, denyWidth, ButtonHeight);
            _allow = new RectangleF(_deny.Right + 8, y, inner - denyWidth - 8, ButtonHeight);
            y += ButtonHeight + 2;
        }
        _terminal = new RectangleF(Pad, y + 4, inner, FooterHeight - 8);
        _height = y + FooterHeight + 6;
    }

    public AskHit HitAt(PointF point, out int option)
    {
        option = -1;
        if (Request.IsQuestion)
        {
            for (var i = 0; i < _options.Count; i++)
                if (_options[i].Contains(point))
                {
                    option = i;
                    return AskHit.Option;
                }
        }
        else
        {
            if (_allow.Contains(point)) return AskHit.Allow;
            if (_deny.Contains(point)) return AskHit.Deny;
        }
        return _terminal.Contains(point) ? AskHit.Terminal : AskHit.None;
    }

    /// <summary>Records the chosen option of the question in view and moves to the next; true once every question is answered.</summary>
    public bool Choose(int option)
    {
        if (Question is not { } question) return true;
        _answers[question.Question] = question.Options[Math.Clamp(option, 0, question.Options.Count - 1)].Label;
        if (QuestionIndex + 1 >= Request.Questions.Count) return true;
        QuestionIndex++;
        Hover = AskHit.None;
        HoverOption = -1;
        Layout();
        return false;
    }

    public override Bitmap Render(IslandStyle style, float scale)
    {
        var (bitmap, g) = style.NewCanvas(Width, _height, scale);
        using (g)
        {
            DrawHeader(g, style);
            if (Question is { } question) DrawQuestion(g, style, question);
            else DrawApproval(g, style);
            var linkHot = Hover == AskHit.Terminal;
            const string link = "ターミナルで答える";
            var x = _terminal.X + (_terminal.Width - style.MeasureWidth(link, style.Caption)) / 2;
            IslandStyle.Text(g, link, style.Caption, linkHot ? IslandStyle.Primary : IslandStyle.Tertiary, x, _terminal.Y + _terminal.Height / 2 + 4);
            if (Request.IsQuestion && Request.Questions.Count > 1)
                style.RightAligned(g, $"{QuestionIndex + 1} / {Request.Questions.Count}", style.Caption, IslandStyle.Tertiary, Width - Pad, _terminal.Y + _terminal.Height / 2 + 4);
        }
        return bitmap;
    }

    private void DrawHeader(Graphics g, IslandStyle style)
    {
        g.DrawImage(style.Icon(Request.Kind), new RectangleF(Pad, (Header - 32) / 2, 32, 32));
        var textX = Pad + 32 + 10;
        var tag = Request.IsQuestion ? "質問" : "承認待ち";
        var tagWidth = style.MeasureWidth(tag, style.Body);
        using (var dot = new SolidBrush(Accent)) g.FillEllipse(dot, Width - Pad - tagWidth - 14, Header / 2 - 3.5f, 7, 7);
        style.RightAligned(g, tag, style.Body, Accent, Width - Pad, Header / 2 + 4.5f);
        var room = Width - Pad - tagWidth - 14 - 12 - textX;
        IslandStyle.TextTrimmed(g, Agent.Name(Request.Kind), style.Name, IslandStyle.Primary, textX, 27, room);
        var subtitle = Request.IsQuestion || Request.Tool is null ? Request.Project : $"{Request.Tool} · {Request.Project}";
        IslandStyle.TextTrimmed(g, subtitle, style.Caption, IslandStyle.Secondary, textX, 43, room);
    }

    private void DrawQuestion(Graphics g, IslandStyle style, AskQuestion question)
    {
        IslandStyle.Paragraph(g, _text, style.Name, IslandStyle.Primary, Pad, Header, Width - 2 * Pad, _textHeight);
        for (var i = 0; i < _options.Count; i++)
        {
            var row = _options[i];
            using (var path = IslandStyle.ContinuousRect(row, 14))
            using (var fill = new SolidBrush(i == HoverOption ? IslandStyle.Alpha3 : IslandStyle.Alpha2))
                g.FillPath(fill, path);
            var option = question.Options[i];
            var room = row.Width - 32;
            if (option.Description is { Length: > 0 } description)
            {
                IslandStyle.TextTrimmed(g, option.Label, style.Body, IslandStyle.Primary, row.X + 16, row.Y + 17, room);
                IslandStyle.TextTrimmed(g, description, style.Caption, IslandStyle.Secondary, row.X + 16, row.Y + 33, room);
            }
            else IslandStyle.TextTrimmed(g, option.Label, style.Body, IslandStyle.Primary, row.X + 16, row.Y + row.Height / 2 + 4.5f, room);
        }
    }

    private void DrawApproval(Graphics g, IslandStyle style)
    {
        using (var path = IslandStyle.ContinuousRect(_tile, 16))
        using (var fill = new SolidBrush(IslandStyle.Alpha2))
            g.FillPath(fill, path);
        IslandStyle.Paragraph(g, _text, style.Body, IslandStyle.Primary, _tile.X + TileInset, _tile.Y + TileInset, _tile.Width - 2 * TileInset, _textHeight);

        Button(g, style, _deny, "拒否", Hover == AskHit.Deny, IslandStyle.Alpha2, IslandStyle.Alpha3, IslandStyle.Primary);
        // 許可 takes the agent's own accent; its label is whichever of dark and white reads better on it.
        var accent = Agent.Accent(Request.Kind);
        var ink = Luminance(accent) > 0.4 ? Color.FromArgb(20, 20, 22) : Color.White;
        Button(g, style, _allow, "許可", Hover == AskHit.Allow, accent, ControlPaint.Light(accent, 0.15f), ink);
    }

    /// <summary>Relative luminance (0 black .. 1 white) of an sRGB colour.</summary>
    private static double Luminance(Color color)
    {
        static double Linear(int channel)
        {
            var c = channel / 255.0;
            return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Linear(color.R) + 0.7152 * Linear(color.G) + 0.0722 * Linear(color.B);
    }

    private static void Button(Graphics g, IslandStyle style, RectangleF rect, string label, bool hot, Color rest, Color over, Color text)
    {
        using (var path = IslandStyle.ContinuousRect(rect, rect.Height / 2))
        using (var fill = new SolidBrush(hot ? over : rest))
            g.FillPath(fill, path);
        IslandStyle.Text(g, label, style.Name, text, rect.X + (rect.Width - style.MeasureWidth(label, style.Name)) / 2, rect.Y + rect.Height / 2 + 5);
    }
}
