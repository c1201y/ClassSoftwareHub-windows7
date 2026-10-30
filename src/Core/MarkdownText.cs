using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace ClassSoftwareHub.Desktop.Core;

/// <summary>
/// 极简 Markdown → 控件（2026-10-18 上游新增；2026-10-22 移植到 Avalonia；2026-10-23 上游补表格，本轮跟随）。
///
/// <para>
/// 为什么自己写而不是引库：模型输出永远是 Markdown
/// （<c>**加粗**</c>、<c>- 列表</c>、<c>### 标题</c>、<c>| 表格 |</c>）。引一个第三方渲染器会带进
/// 一堆用不上的东西，而这里需要的只有这几类语法 —— 够用就行。
/// </para>
/// <para>
/// ⛔ 仍然**不支持**代码块、图片、链接：这三样在 AI 回答里出现频率低，按纯文本显示信息也不会丢。
/// </para>
/// <para>
/// ⚠️ 移植说明：WinUI 的 <c>RichTextBlock + Paragraph</c> 在 Avalonia 没有对应物
/// （<c>TextBlock.Inlines</c> 是扁平的，没有"块"这一层）⇒ 这里改成
/// **往一个 Panel（通常是 StackPanel）里塞若干 TextBlock**，一个 TextBlock 就是一段，
/// 段内用 <c>Run</c> 表达粗体/等宽；表格则渲染成真正的 <see cref="Grid"/>。调用方负责清空/复用这个 Panel。
/// </para>
/// <para>
/// ⚠️ 流式渲染时这个方法会被反复调用（约每 80ms 一次），所以实现要够轻：
/// 只做一次字符串切分，不建语法树、不回溯。
/// </para>
/// </summary>
public static class MarkdownText
{
    /// <summary>把 <paramref name="markdown"/> 渲染成控件填进 <paramref name="target"/>，会清空原有内容。</summary>
    /// <param name="foreground">正文颜色。⚠️ Panel 没有 Foreground 可继承，必须显式往下传。</param>
    public static void Apply(Panel target, string markdown, Brush? foreground = null,
        double fontSize = 14, double lineHeight = 24)
    {
        target.Children.Clear();

        if (string.IsNullOrEmpty(markdown)) return;

        var lines = markdown.Replace("\r\n", "\n").Split('\n');
        var paragraph = new List<string>();

        void FlushParagraph()
        {
            if (paragraph.Count == 0) return;

            target.Children.Add(MakeBlock(string.Join(" ", paragraph).Trim(), fontSize, FontWeight.Normal,
                new Thickness(0, 0, 0, 6), lineHeight, foreground));
            paragraph.Clear();
        }

        for (var i = 0; i < lines.Length; i++)
        {
            var trimmed = lines[i].Trim();

            if (trimmed.Length == 0)
            {
                FlushParagraph();
                continue;
            }

            // 标题：# / ## / ### —— **字号必须分级**，否则标题和正文一样大，层级完全看不出来
            if (trimmed[0] == '#')
            {
                FlushParagraph();

                var level = 0;
                while (level < trimmed.Length && trimmed[level] == '#') level++;

                var text = trimmed[level..].Trim();

                var size = level switch
                {
                    1 => 21.0,
                    2 => 18.0,
                    _ => 16.0,
                };

                target.Children.Add(MakeBlock(text, size, FontWeight.SemiBold,
                    new Thickness(0, level == 1 ? 12 : 9, 0, 3), lineHeight, foreground));
                continue;
            }

            // 表格：当前行以 | 开头、下一行是 |---|---| 分隔行 → 造一个真正的 Grid
            if (trimmed[0] == '|' && i + 1 < lines.Length && IsTableDivider(lines[i + 1]))
            {
                FlushParagraph();
                target.Children.Add(BuildTable(lines, i, foreground, out var consumed));
                i += consumed;      // 整张表吃掉
                continue;
            }

            // 有序列表：1. / 2. / 12. （保留原编号，不重新数）
            if (TryOrderedItem(trimmed, out var marker, out var rest))
            {
                FlushParagraph();

                var block = MakeBlock(rest, fontSize, FontWeight.Normal, new Thickness(0, 0, 0, 3), lineHeight, foreground);
                block.Inlines!.Insert(0, new Run { Text = marker + "  " });
                target.Children.Add(block);
                continue;
            }

            // 无序列表：- / * / •
            if (trimmed.StartsWith("- ", StringComparison.Ordinal)
                || trimmed.StartsWith("* ", StringComparison.Ordinal)
                || trimmed.StartsWith("• ", StringComparison.Ordinal))
            {
                FlushParagraph();

                var block = MakeBlock(trimmed[2..].Trim(), fontSize, FontWeight.Normal, new Thickness(0, 0, 0, 3), lineHeight, foreground);
                block.Inlines!.Insert(0, new Run { Text = "•  " });
                target.Children.Add(block);
                continue;
            }

            // 其余按段落累积（相邻行合并成一段，单换行不硬断 —— 中文排版里那多半是软换行）
            paragraph.Add(trimmed);
        }

        FlushParagraph();
    }

    /// <summary>造一段：行内语法解析进 Inlines。</summary>
    private static TextBlock MakeBlock(string text, double size, FontWeight weight, Thickness margin,
        double lineHeight, Brush? foreground)
    {
        var block = new TextBlock
        {
            FontSize = size,
            FontWeight = weight,
            TextWrapping = TextWrapping.Wrap,
            Margin = margin,
            LineHeight = lineHeight,
            Inlines = new InlineCollection(),
        };

        if (foreground is not null) block.Foreground = foreground;

        AppendInline(block.Inlines!, text);
        return block;
    }

    /// <summary>识别「1. 」开头的有序列表项，marker 原样保留（不重新编号）。</summary>
    private static bool TryOrderedItem(string line, out string marker, out string rest)
    {
        marker = "";
        rest = "";

        var i = 0;
        while (i < line.Length && char.IsDigit(line[i])) i++;

        if (i == 0 || i + 1 >= line.Length) return false;
        if (line[i] != '.' && line[i] != ')') return false;
        if (line[i + 1] != ' ') return false;

        marker = line[..(i + 1)];
        rest = line[(i + 1)..].Trim();
        return true;
    }

    // ══════════════════════════════════════════════════════════════════
    //  表格（2026-10-23 上游补，本轮跟随移植）
    // ══════════════════════════════════════════════════════════════════

    /// <summary>这一行是不是 |---|---| 形态的分隔行（只允许 | - : 空白，且至少 3 个连字符）。</summary>
    private static bool IsTableDivider(string line)
    {
        var trimmed = line.Trim();
        if (trimmed.Length == 0 || trimmed[0] != '|') return false;

        var dashes = 0;
        foreach (var ch in trimmed)
        {
            if (ch == '-') { dashes++; continue; }
            if (ch is '|' or ':' or ' ' or '\t') continue;
            return false;
        }

        return dashes >= 3;
    }

    /// <summary>把一行 | a | b | 拆成单元格（剥掉首尾的 |，按 | 切，去空白）。</summary>
    private static List<string> SplitRow(string line)
    {
        var trimmed = line.Trim();
        if (trimmed.StartsWith('|')) trimmed = trimmed[1..];
        if (trimmed.EndsWith('|')) trimmed = trimmed[..^1];

        var cells = new List<string>();
        foreach (var part in trimmed.Split('|')) cells.Add(part.Trim());
        return cells;
    }

    /// <summary>表格单元格只留纯文本：粗体/代码标记剥掉，[文字](url) 留文字。</summary>
    private static string PlainText(string text)
    {
        var s = text.Replace("**", "", StringComparison.Ordinal).Replace("`", "", StringComparison.Ordinal);

        var open = s.IndexOf('[');
        while (open >= 0)
        {
            var close = s.IndexOf(']', open + 1);
            var parenStart = close >= 0 ? s.IndexOf('(', close + 1) : -1;
            var parenEnd = parenStart >= 0 ? s.IndexOf(')', parenStart + 1) : -1;

            if (close < 0 || parenStart != close + 1 || parenEnd < 0) break;

            s = s[..open] + s[(open + 1)..close] + s[(parenEnd + 1)..];
            open = s.IndexOf('[', open);
        }

        return s.Trim();
    }

    /// <summary>
    /// 造一张表：列宽按各列最长内容的字符数加权（给下限，免得两字列被挤成一条缝）；
    /// 表头行加底色 + 半粗，单元格之间用 0.5px 细线分隔，外框 0.5px + 8px 圆角。
    /// </summary>
    private static Border BuildTable(string[] lines, int start, Brush? foreground, out int consumed)
    {
        var rows = new List<List<string>> { SplitRow(lines[start]) };

        var i = start + 2;      // 跳过表头与分隔行
        while (i < lines.Length)
        {
            var trimmed = lines[i].Trim();
            if (trimmed.Length == 0 || trimmed[0] != '|') break;

            rows.Add(SplitRow(trimmed));
            i++;
        }

        consumed = i - 1;

        var columns = 1;
        foreach (var row in rows) columns = Math.Max(columns, row.Count);

        var weights = new double[columns];
        foreach (var row in rows)
            for (var c = 0; c < columns; c++)
                weights[c] = Math.Max(weights[c], c < row.Count ? PlainText(row[c]).Length : 0);

        var total = 0.0;
        for (var c = 0; c < columns; c++)
        {
            weights[c] = Math.Max(weights[c], 4);
            total += weights[c];
        }

        var grid = new Grid();

        for (var c = 0; c < columns; c++)
            grid.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(weights[c] / total, GridUnitType.Star),
            });

        for (var r = 0; r < rows.Count; r++)
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var stroke = Services.ThemeBrush.Get(null, "CardStrokeColorDefaultBrush");
        var headerFill = Services.ThemeBrush.Get(null, "CardBackgroundFillColorDefaultBrush");

        for (var r = 0; r < rows.Count; r++)
        {
            for (var c = 0; c < columns; c++)
            {
                var text = c < rows[r].Count ? rows[r][c] : "";

                var cell = new Border
                {
                    Padding = new Thickness(10, 7, 10, 7),
                    Background = r == 0 ? headerFill : null,
                    BorderBrush = stroke,
                    BorderThickness = new Thickness(
                        0,
                        0,
                        c == columns - 1 ? 0 : 0.5,
                        r == rows.Count - 1 ? 0 : 0.5),
                    Child = new TextBlock
                    {
                        Text = PlainText(text),
                        FontSize = 13.5,
                        LineHeight = 20,
                        TextWrapping = TextWrapping.Wrap,
                        FontWeight = r == 0 ? FontWeight.SemiBold : FontWeight.Normal,
                        Foreground = foreground,
                    },
                };

                Grid.SetRow(cell, r);
                Grid.SetColumn(cell, c);
                grid.Children.Add(cell);
            }
        }

        return new Border
        {
            BorderThickness = new Thickness(0.5),
            BorderBrush = stroke,
            CornerRadius = new CornerRadius(8),
            Margin = new Thickness(0, 6, 0, 10),
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
            Child = grid,
        };
    }

    /// <summary>
    /// 行内解析：只认 <c>**粗体**</c> 和 <c>`代码`</c>。
    /// 用最简单的分隔符切分 —— 成对的星号交替出现即为粗体区间。
    /// </summary>
    private static void AppendInline(InlineCollection inlines, string text)
    {
        if (text.Length == 0) return;

        var bold = text.Split("**");
        for (var i = 0; i < bold.Length; i++)
        {
            if (bold[i].Length == 0) continue;

            var isBold = i % 2 == 1;
            AppendCode(inlines, bold[i], isBold);
        }
    }

    /// <summary>段内再切一层反引号，代码片段用等宽字体。</summary>
    private static void AppendCode(InlineCollection inlines, string text, bool isBold)
    {
        var parts = text.Split('`');
        for (var i = 0; i < parts.Length; i++)
        {
            if (parts[i].Length == 0) continue;

            var run = new Run { Text = parts[i] };

            if (isBold) run.FontWeight = FontWeight.SemiBold;
            // 等宽：Avalonia 里直接用系统字体名，Win7 自带 Consolas（没有就落到默认等宽）
            if (i % 2 == 1) run.FontFamily = new FontFamily("Consolas, Courier New, monospace");

            inlines.Add(run);
        }
    }
}
