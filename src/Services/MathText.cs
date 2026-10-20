using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
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
/// ⛔⛔ 用 <see cref="TextBlock"/>（Inlines 可换行）而不是横向 StackPanel：
/// 分析栏只有约 150px 宽，长结论必须能自动折行；StackPanel 不会换行，直接横向溢出。
/// </para>
/// <para>
/// ⚠️ WinUI → Avalonia 移植（2026-10-18 自上游 1.2.0 移植）：
///   · WinUI 的 <c>RichTextBlock + Paragraph + InlineUIContainer</c> →
///     Avalonia 的 <c>TextBlock.Inlines</c>（<c>Run</c> + <c>InlineUIContainer</c> 同名可用，
///     折行行为一致）；
///   · <c>Windows.UI.Text.FontStyle</c> → <c>Avalonia.Media.FontStyle</c>；
///   · <c>LineStackingStrategy</c> → 直接设 <c>LineHeight</c>（Avalonia 默认就是精确行高）。
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

    /// <summary>把一段结论字符串排成可换行的数学视图。</summary>
    public static TextBlock Render(string? text, double fontSize, IBrush brush)
    {
        var body = text ?? "";

        var block = new TextBlock
        {
            FontSize = fontSize,
            Foreground = brush,
            TextWrapping = TextWrapping.Wrap,
            // Avalonia 11 的 TextBlock 默认允许复制（SelectableTextBlock 才显式支持选择）；
            // 结论要能选中复制 ⇒ 外面用 SelectableTextBlock 包一层（见下）。
        };

        var para = new InlineCollection();

        try
        {
            var ctx = new Ctx(fontSize, brush);
            foreach (var piece in new Parser(Normalize(body)).ParseAll())
                Append(para, piece, ctx);
        }
        catch (Exception ex)
        {
            // ⛔ 不许静默吞：排版退化本身不致命，但"为什么这一项是平文本"必须查得到
            Core.AppLog.Exception("plot", ex, "数学文本排版");

            // 排版出了任何岔子都退回纯文本：宁可不好看，不能空白
            para.Clear();
            para.Add(new Run { Text = body, FontSize = fontSize, Foreground = brush });
        }

        // ⛔⛔ 必须把排好的 InlineCollection **挂到 TextBlock 上** ——
        //    2026-10-18 修：移植时漏了这一步，于是 block.Inlines 一直是空的，
        //    方程预览 / 函数分析 / 化学式全渲染成空白（用户报「方程求解不显示方程」）。
        block.Inlines = para;
        return block;
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

    // ────────────────────────── 渲染 ──────────────────────────

    private static void Append(InlineCollection para, Piece piece, Ctx ctx)
    {
        switch (piece)
        {
            case Txt t:
                if (t.Text.Length == 0) return;
                para.Add(new Run
                {
                    Text = t.Text,
                    FontSize = ctx.Size,
                    Foreground = ctx.Brush,
                    FontStyle = t.Italic ? Avalonia.Media.FontStyle.Italic : Avalonia.Media.FontStyle.Normal,
                });
                return;

            case Group g:
                foreach (var item in g.Items) Append(para, item, ctx);
                return;

            case Frac f:
                // ⛔ 底部 padding 抬基线（教训见 AlignOnBaseline 注释）
                var frac = FractionElement(f, ctx);
                AlignOnBaseline(frac, ctx);
                para.Add(new InlineUIContainer { Child = frac });
                return;

            case Script sc:
                para.Add(new InlineUIContainer { Child = ScriptElement(sc, ctx) });
                return;

            case Radical r:
                var rad = RadicalElement(r, ctx);
                AlignOnBaseline(rad, ctx);
                para.Add(new InlineUIContainer { Child = rad });
                return;
        }
    }

    /// <summary>把一个片段摊成横排元素（分数内部用，不需要换行）。</summary>
    private static void Flatten(StackPanel panel, Piece piece, Ctx ctx)
    {
        switch (piece)
        {
            case Txt t:
                if (t.Text.Length == 0) return;
                panel.Children.Add(new TextBlock
                {
                    Text = t.Text,
                    FontSize = ctx.Size,
                    Foreground = ctx.Brush,
                    FontStyle = t.Italic ? Avalonia.Media.FontStyle.Italic : Avalonia.Media.FontStyle.Normal,
                    VerticalAlignment = VerticalAlignment.Center,
                    // 堆叠件内部收紧行距：默认行高会把"分子/分母"撑得过高，分数显得又高又散
                    LineHeight = ctx.Size * 1.25,
                });
                return;

            case Group g:
                foreach (var item in g.Items) Flatten(panel, item, ctx);
                return;

            case Frac f:
                panel.Children.Add(FractionElement(f, ctx));
                return;

            case Script sc:
                panel.Children.Add(ScriptElement(sc, ctx));
                return;

            case Radical r:
                panel.Children.Add(RadicalElement(r, ctx));
                return;
        }
    }

    private static Avalonia.Controls.Control Wrap(Piece piece, Ctx ctx)
    {
        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Flatten(panel, piece, ctx);
        return panel;
    }

    /// <summary>
    /// 把堆叠类元素（分数、根号）摆到**基线**上。
    /// <para>
    /// ⛔⛔ 2026-10-20 多轮实测的最终方案（用户截图：x = 56/9 分母被裁；-π/6 分母挤进基线）：
    /// </para>
    /// <para>
    /// · RenderTransform 视觉下移 —— 位移不进布局，行框不涨，下移出界的分母被裁掉。
    /// · VerticalAlignment=Bottom —— UI 底边贴**行框底**，而文本基线在行框内偏上，
    ///   分母整个沉到基线以下、与前后文字重叠。
    /// · 底部负 margin —— margin 同时参与行框计算，等于没抬。
    /// · InlineUIContainer.Baseline —— Avalonia 11.2.8 没有这个属性。
    /// · BaselineAlignment=Baseline —— 11.2 的 TextLine 对 UI 容器**忽略**该值，实测无效。
    /// </para>
    /// <para>
    /// ✅ 最终做法（纯布局、可预测）：给元素加**内部底部 padding = 0.27em**
    ///   （正文行框里"基线到行框底"的距离 ≈ 字形下伸部）。
    ///   UI 底边贴行框底 ⇒ padding 底边就是基线位置 ⇒ 分母底恰好落在主基线上，
    ///   分数线 ≈ 基线上方 0.6em —— 教材排版的标准位置；行框按 padding 撑高，绝不裁剪。
    /// </para>
    /// </summary>
    private static void AlignOnBaseline(Avalonia.Controls.Control element, Ctx ctx)
    {
        var m = element.Margin;
        element.Margin = new Thickness(m.Left, m.Top, m.Right, ctx.Size * 0.27);
    }

    /// <summary>分数：分子 / 分数线 / 分母 三行堆叠（底边对齐基线由 BaselineAlignment 负责）。</summary>
    private static Avalonia.Controls.Control FractionElement(Frac f, Ctx ctx)
    {
        var grid = new Grid
        {
            Margin = new Thickness(2, 0, 2, 0),
        };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var num = Wrap(f.Num, ctx);
        num.HorizontalAlignment = HorizontalAlignment.Center;
        Grid.SetRow(num, 0);
        grid.Children.Add(num);

        var bar = new Rectangle
        {
            Height = Math.Max(1, ctx.Size / 14),
            Fill = ctx.Brush,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(1, 0, 1, 0),
        };
        Grid.SetRow(bar, 1);
        grid.Children.Add(bar);

        var den = Wrap(f.Den, ctx);
        den.HorizontalAlignment = HorizontalAlignment.Center;
        Grid.SetRow(den, 2);
        grid.Children.Add(den);

        return grid;
    }

    private static Avalonia.Controls.Control ScriptElement(Script sc, Ctx ctx)
    {
        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var body = Wrap(sc.Base, ctx);
        body.VerticalAlignment = VerticalAlignment.Center;
        panel.Children.Add(body);

        // ⚠️ 上标往上抬、下标往下压，都不能额外撑高行
        var script = Wrap(sc.Sup, ctx.Script());
        script.Margin = new Thickness(1, sc.Sub ? ctx.Size * 0.30 : -ctx.Size * 0.34, 0, 0);
        script.VerticalAlignment = VerticalAlignment.Center;
        panel.Children.Add(script);

        return panel;
    }

    /// <summary>根号：√ 符号 + 顶部横线的被开方数。</summary>
    private static Avalonia.Controls.Control RadicalElement(Radical r, Ctx ctx)
    {
        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
        };

        panel.Children.Add(new TextBlock
        {
            Text = "√",
            FontSize = ctx.Size * 1.15,
            Foreground = ctx.Brush,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, -1, 0),
        });

        panel.Children.Add(new Border
        {
            BorderBrush = ctx.Brush,
            BorderThickness = new Thickness(0, Math.Max(1, ctx.Size / 16), 0, 0),
            Padding = new Thickness(2, 0, 1, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Child = Wrap(r.Body, ctx),
        });

        return panel;
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
