using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace ClassSoftwareHub.Desktop.Services;

/// <summary>
/// 把**纯文本的数学结论**排成数学视图（π/2 是上下堆叠的分数、x² 是真上标、√2 带顶线、变量斜体）。
///
/// <para>
/// 用途：函数分析面板里的每一项结论本来就是字符串（<c>(-π/2 + 2kπ, π/2 + 2kπ)</c> 这种），
/// 直接塞进 TextBlock 就是一行线性文本 —— Nick 点名「依旧是线性文本，不是函数数学视图」。
/// </para>
/// <para>
/// ⛔ 这是给**我们自己生成的**结论字符串用的排版器，不是通用数学输入解析器：
/// 文法只覆盖结论里会出现的记号（四则、^ _ / √、集合与区间括号、∈ ≠ ∪ \、° · π 等）。
/// 认不出来的字符一律原样当普通文本输出，绝不吃掉 —— 排错了比排不出更糟。
/// </para>
/// <para>
/// ⛔⛔ 2026-10-21 重写为**自绘控件**（<c>MathView : Control</c>，用 FormattedText 逐块画）：
///   之前用 <c>TextBlock + InlineUIContainer</c> 塞分数，分数在行里的垂直位置**完全由
///   Avalonia 的 TextLine 决定**，而 11.2 的 TextLine 对 UI 容器只有"底边贴基线/行框底"
///   这两种行为可选 —— 于是分母要么被裁、要么整个压在基线上和前面的数字挤成一排，
///   用户连着两次截图反馈。自绘之后基线由我们自己算：分数线在基线上方 0.26em、
///   分母垂到基线以下，就是教材里的位置；宽度不够时按**块**折行（分析栏只有 150px 宽，
///   长结论必须能换行）。
/// </para>
/// <para>
/// ⚠️ WinUI → Avalonia 移植（2026-10-18 自上游 1.2.0 移植）：
///   · WinUI 的 <c>RichTextBlock + Paragraph + InlineUIContainer</c> → 本移植版自绘（见上）；
///   · <c>Windows.UI.Text.FontStyle</c> → <c>Avalonia.Media.FontStyle</c>。
/// </para>
/// </summary>
public static class MathText
{
    /// <summary>上标/下标相对正文字号的缩放。</summary>
    private const double ScriptScale = 0.72;

    /// <summary>函数名等应当**正体**的单词（斜体就成变量了）。</summary>
    private static readonly HashSet<string> Upright = new(StringComparer.Ordinal)
    {
        "sin", "cos", "tan", "cot", "sec", "csc",
        "arcsin", "arccos", "arctan", "asin", "acos", "atan",
        "sinh", "cosh", "tanh", "ln", "lg", "log", "exp",
        "abs", "max", "min", "mod", "e",
    };

    /// <summary>把一段结论字符串排成数学视图（可自动折行）。</summary>
    public static Control Render(string? text, double fontSize, IBrush brush)
    {
        var body = text ?? "";

        try
        {
            var ctx = new Ctx(fontSize, brush);
            var boxes = new List<MathBox>();
            foreach (var piece in new Parser(Normalize(body)).ParseAll())
                Build(boxes, piece, ctx);

            return new MathView(boxes);
        }
        catch (Exception ex)
        {
            // ⛔ 不许静默吞：排版退化本身不致命，但"为什么这一项是平文本"必须查得到
            Core.AppLog.Exception("plot", ex, "数学文本排版");

            // 排版出了任何岔子都退回纯文本：宁可不好看，不能空白
            return new TextBlock
            {
                Text = body,
                FontSize = fontSize,
                Foreground = brush,
                TextWrapping = TextWrapping.Wrap,
            };
        }
    }

    // ────────────────────────── 片段模型 ──────────────────────────

    private readonly record struct Ctx(double Size, IBrush Brush)
    {
        public Ctx Script() => new(Size * ScriptScale, Brush);
    }

    private abstract record Piece;

    private sealed record Txt(string Text, bool Italic) : Piece;

    private sealed record Frac(Piece Num, Piece Den) : Piece;

    private sealed record Script(Piece Base, Piece Sup, bool Sub) : Piece;

    private sealed record Radical(Piece Body) : Piece;

    private sealed record Group(List<Piece> Items) : Piece;

    // ────────────────────────── 分词 + 组装 ──────────────────────────

    /// <summary>
    /// 递归下降：<c>项 := 原子 ('/' 原子 | '^' 原子 | '_' 原子)*</c>，
    /// 分数与幂只结合**紧邻的原子**，所以 <c>π/2 + 2kπ</c> 的分数线只盖住 π 与 2。
    /// </summary>
    private sealed class Parser
    {
        private readonly string _s;
        private int _i;

        public Parser(string s) { _s = s; }   // C# 10：没有主构造函数，手写构造器

        public List<Piece> ParseAll()
        {
            var list = new List<Piece>();
            while (_i < _s.Length) list.Add(ParseTerm());
            return list;
        }

        private Piece ParseTerm()
        {
            var p = ParseAtom();

            while (_i < _s.Length)
            {
                var c = _s[_i];

                if (c == '/')
                {
                    _i++;
                    p = new Frac(p, ParseAtom());
                    continue;
                }

                if (c == '^')
                {
                    _i++;
                    p = new Script(p, ParseAtom(), Sub: false);
                    continue;
                }

                if (c == '_')
                {
                    _i++;
                    p = new Script(p, ParseAtom(), Sub: true);
                    continue;
                }

                // 上标字符（x²、10⁻³）：直接吸进上标
                if (IsSuperscript(c))
                {
                    var start = _i;
                    while (_i < _s.Length && IsSuperscript(_s[_i])) _i++;
                    p = new Script(p, new Txt(UnSuperscript(_s[start.._i]), false), Sub: false);
                    continue;
                }

                break;
            }

            return p;
        }

        private Piece ParseAtom()
        {
            if (_i >= _s.Length) return new Txt("", false);

            var c = _s[_i];

            if (c is '(' or '[' or '{')
            {
                var close = c == '(' ? ')' : c == '[' ? ']' : '}';
                _i++;
                var items = new List<Piece> { new Txt(c.ToString(), false) };
                while (_i < _s.Length && _s[_i] != close) items.Add(ParseTerm());
                if (_i < _s.Length) { _i++; items.Add(new Txt(close.ToString(), false)); }
                return new Group(items);
            }

            if (c == '√')
            {
                _i++;
                return new Radical(ParseAtom());
            }

            if (char.IsDigit(c) || (c == '.' && _i + 1 < _s.Length && char.IsDigit(_s[_i + 1])))
            {
                var start = _i;
                while (_i < _s.Length && (char.IsDigit(_s[_i]) || _s[_i] == '.')) _i++;
                return new Txt(_s[start.._i], false);
            }

            if (char.IsLetter(c))
            {
                var start = _i;
                while (_i < _s.Length && char.IsLetter(_s[_i])) _i++;
                var word = _s[start.._i];
                // 单个字母是变量（斜体）；函数名、常数、中文词一律正体
                return new Txt(word, word.Length == 1 && !Upright.Contains(word) && IsVariableLetter(word[0]));
            }

            _i++;
            return new Txt(c.ToString(), false);
        }

        private static bool IsVariableLetter(char c)
            => c is 'x' or 'y' or 'z' or 'k' or 'a' or 'b' or 'c' or 'n' or 'm'
                or 't' or 'T' or 's' or 'u' or 'v' or 'w' or 'f' or 'g' or 'h';
    }

    // ────────────────────────── 排版盒（自绘） ──────────────────────────

    /// <summary>
    /// 一个排版盒：只知道自己的宽、基线上方多高（Ascent）、基线下方多深（Descent），
    /// 以及"把基线放在 <paramref name="yBaseline"/> 时怎么画"。
    /// 分数这类堆叠件的基线由我们自己定（分数线在基线上方），不再受 TextLine 摆布。
    /// </summary>
    private abstract class MathBox
    {
        public double Width { get; protected set; }
        public double Ascent { get; protected set; }
        public double Descent { get; protected set; }

        public abstract void Draw(DrawingContext context, double x, double yBaseline);
    }

    /// <summary>普通文本块（数字、字母、运算符、中文）。</summary>
    private sealed class TextBox : MathBox
    {
        private readonly FormattedText _text;

        public IBrush Brush { get; }

        public TextBox(string text, Ctx ctx, bool italic)
        {
            Brush = ctx.Brush;
            _text = new FormattedText(
                text,
                CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                new Typeface(FontFamily.Default, italic ? FontStyle.Italic : FontStyle.Normal, FontWeight.Normal),
                ctx.Size,
                ctx.Brush);

            Width = _text.WidthIncludingTrailingWhitespace;
            Ascent = _text.Baseline;
            Descent = Math.Max(0, _text.Height - _text.Baseline);
        }

        public override void Draw(DrawingContext context, double x, double yBaseline)
            => context.DrawText(_text, new Point(x, yBaseline - _text.Baseline));
    }

    /// <summary>一行里并排的若干盒（分子、分母、被开方数都用它）。</summary>
    private sealed class RowBox : MathBox
    {
        private readonly List<MathBox> _boxes = new();
        private readonly List<double> _offsets = new();

        public RowBox(IEnumerable<MathBox> boxes)
        {
            var x = 0.0;
            foreach (var b in boxes)
            {
                _boxes.Add(b);
                _offsets.Add(x);
                x += b.Width;
                Ascent = Math.Max(Ascent, b.Ascent);
                Descent = Math.Max(Descent, b.Descent);
            }
            Width = x;
        }

        public override void Draw(DrawingContext context, double x, double yBaseline)
        {
            for (var i = 0; i < _boxes.Count; i++)
                _boxes[i].Draw(context, x + _offsets[i], yBaseline);
        }
    }

    /// <summary>分数：分子在上、分数线在基线上方 0.26em、分母**垂到基线以下**（教材位置）。</summary>
    private sealed class FracBox : MathBox
    {
        private readonly MathBox _num;
        private readonly MathBox _den;
        private readonly IBrush _brush;
        private readonly double _bar;
        private readonly double _axis;          // 分数线距基线上方
        private readonly double _numLift;       // 分子基线距基线上方
        private readonly double _denAbove;      // 分母基线相对主基线（负 = 在基线以下）

        public FracBox(MathBox num, MathBox den, Ctx ctx)
        {
            _num = num;
            _den = den;
            _brush = ctx.Brush;

            // ⚠️ 2026-10-21：上下位置**按 em 比例**定，不能拿字框的 ascent/descent 去算 ——
            //    字框里含大量上下留白（行距），照它算出来的分数会被拉成两三倍高。
            //    教材位置：分数线在基线上方 0.26em、分子基线 +0.36em、分母基线 −0.54em
            //    ⇒ 分子底贴着分数线、分母顶贴着分数线、分母整体垂到基线以下。
            var em = ctx.Size;
            _bar = Math.Max(1, em / 18);
            _axis = em * 0.26;
            var gap = em * 0.10;

            _numLift = _axis + gap;                       // 分子基线在基线上方
            _denAbove = _axis - gap - em * 0.70;           // 分母基线相对主基线（负 = 在下方）

            var pad = em * 0.12;
            Width = Math.Max(num.Width, den.Width) + pad * 2;
            Ascent = _numLift + em * 0.75;
            Descent = Math.Max(0, -_denAbove) + em * 0.22;
        }

        public override void Draw(DrawingContext context, double x, double yBaseline)
        {
            // 分子
            _num.Draw(context, x + (Width - _num.Width) / 2, yBaseline - _numLift);

            // 分数线
            var barTop = yBaseline - _axis - _bar / 2;
            context.FillRectangle(_brush, new Rect(x, barTop, Width, _bar));

            // 分母（垂到基线以下）
            _den.Draw(context, x + (Width - _den.Width) / 2, yBaseline - _denAbove);
        }
    }

    /// <summary>上标 / 下标。</summary>
    private sealed class ScriptBox : MathBox
    {
        private readonly MathBox _base;
        private readonly MathBox _script;
        private readonly double _shift;      // 正数 = 基线以下（下标）

        public ScriptBox(MathBox baseBox, MathBox script, double shift)
        {
            _base = baseBox;
            _script = script;
            _shift = shift;

            Width = baseBox.Width + script.Width;
            Ascent = Math.Max(baseBox.Ascent, -shift + script.Ascent);
            Descent = Math.Max(baseBox.Descent, shift + script.Descent);
        }

        public override void Draw(DrawingContext context, double x, double yBaseline)
        {
            _base.Draw(context, x, yBaseline);
            _script.Draw(context, x + _base.Width, yBaseline + _shift);
        }
    }

    /// <summary>根号：√ 符号 + 被开方数顶上的横线。</summary>
    private sealed class RadicalBox : MathBox
    {
        private readonly TextBox _sign;
        private readonly MathBox _body;
        private readonly double _lineThickness;
        private readonly double _gap;

        public RadicalBox(MathBox body, Ctx ctx)
        {
            _body = body;
            _sign = new TextBox("√", ctx, italic: false);
            _lineThickness = Math.Max(1, ctx.Size / 16);
            _gap = ctx.Size * 0.06;

            Width = _sign.Width + body.Width + 1;
            Ascent = Math.Max(_sign.Ascent, body.Ascent + _gap + _lineThickness);
            Descent = Math.Max(_sign.Descent, body.Descent);
        }

        public override void Draw(DrawingContext context, double x, double yBaseline)
        {
            _sign.Draw(context, x, yBaseline);
            var bodyX = x + _sign.Width;
            _body.Draw(context, bodyX, yBaseline);

            var y = yBaseline - _body.Ascent - _gap - _lineThickness / 2;
            context.FillRectangle(_sign.Brush, new Rect(bodyX, y, _body.Width + 1, _lineThickness));
        }
    }

    // ────────────────────────── 画布控件 ──────────────────────────

    /// <summary>
    /// 自绘的数学视图：按**块**折行（宽度不够时整块换行），逐行按自己的基线画。
    /// 行高 = Ascent + Descent，所以分数再高也不会被裁，也不会和相邻行挤在一起。
    /// </summary>
    private sealed class MathView : Control
    {
        private readonly List<MathBox> _boxes;
        private readonly List<(List<(MathBox Box, double X)> Items, double Ascent, double Descent, double Width)> _lines = new();

        public MathView(List<MathBox> boxes) => _boxes = boxes;

        protected override Size MeasureOverride(Size availableSize)
        {
            var maxWidth = double.IsInfinity(availableSize.Width)
                ? double.PositiveInfinity
                : Math.Max(1, availableSize.Width);

            _lines.Clear();

            var items = new List<(MathBox, double)>();
            var x = 0.0;
            var ascent = 0.0;
            var descent = 0.0;
            var width = 0.0;

            void Flush()
            {
                if (items.Count == 0) return;
                _lines.Add((new List<(MathBox, double)>(items), ascent, descent, width));
                items.Clear();
                x = 0;
                ascent = 0;
                descent = 0;
                width = 0;
            }

            foreach (var b in _boxes)
            {
                if (x > 0 && x + b.Width > maxWidth) Flush();
                items.Add((b, x));
                x += b.Width;
                width = Math.Max(width, x);
                ascent = Math.Max(ascent, b.Ascent);
                descent = Math.Max(descent, b.Descent);
            }
            Flush();

            var height = 0.0;
            var totalWidth = 0.0;
            foreach (var line in _lines)
            {
                height += line.Ascent + line.Descent;
                totalWidth = Math.Max(totalWidth, line.Width);
            }

            return new Size(totalWidth, height);
        }

        public override void Render(DrawingContext context)
        {
            // 极端情况下 MeasureOverride 还没跑过（比如刚构造就画），先补一次，避免整块空着
            if (_lines.Count == 0 && _boxes.Count > 0)
                MeasureOverride(new Size(Bounds.Width > 0 ? Bounds.Width : double.PositiveInfinity, double.PositiveInfinity));

            var y = 0.0;
            foreach (var line in _lines)
            {
                var baseline = y + line.Ascent;
                foreach (var item in line.Items)
                    item.Box.Draw(context, item.X, baseline);
                y += line.Ascent + line.Descent;
            }
        }
    }

    // ────────────────────────── 组装：Piece → 排版盒 ──────────────────────────

    private static void Build(List<MathBox> sink, Piece piece, Ctx ctx)
    {
        switch (piece)
        {
            case Txt t:
                if (t.Text.Length > 0) sink.Add(new TextBox(t.Text, ctx, t.Italic));
                return;

            case Group g:
                foreach (var item in g.Items) Build(sink, item, ctx);
                return;

            case Frac f:
                sink.Add(new FracBox(BuildRow(f.Num, ctx), BuildRow(f.Den, ctx), ctx));
                return;

            case Script sc:
                var body = BuildRow(sc.Base, ctx);
                var script = BuildRow(sc.Sup, ctx.Script());
                var shift = sc.Sub ? ctx.Size * 0.20 : -ctx.Size * 0.42;   // 正 = 往下（下标）
                sink.Add(new ScriptBox(body, script, shift));
                return;

            case Radical r:
                sink.Add(new RadicalBox(BuildRow(r.Body, ctx), ctx));
                return;
        }
    }

    private static MathBox BuildRow(Piece piece, Ctx ctx)
    {
        var list = new List<MathBox>();
        Build(list, piece, ctx);
        return new RowBox(list);
    }

    // ────────────────────────── 预处理与字符表 ──────────────────────────

    /// <summary>全角标点收成半角（粘贴进来的结论偶尔带全角括号）。</summary>
    private static string Normalize(string s) => s
        .Replace('（', '(').Replace('）', ')')
        .Replace('，', ',').Replace('：', ':')
        .Replace('　', ' ')
        .Replace('-', '\u2212');   // 结论里混着 ASCII 连字符与数学负号，统一成 −

    private static bool IsSuperscript(char c) => c is '⁰' or '¹' or '²' or '³' or '⁴'
        or '⁵' or '⁶' or '⁷' or '⁸' or '⁹' or '⁻' or '⁺';

    private static string UnSuperscript(string s)
    {
        var sb = new System.Text.StringBuilder(s.Length);
        foreach (var c in s)
        {
            sb.Append(c switch
            {
                '⁻' => '-',
                '⁺' => '+',
                '⁰' => '0',
                '¹' => '1',
                '²' => '2',
                '³' => '3',
                '⁴' => '4',
                '⁵' => '5',
                '⁶' => '6',
                '⁷' => '7',
                '⁸' => '8',
                '⁹' => '9',
                _ => c,
            });
        }
        return sb.ToString();
    }
}
