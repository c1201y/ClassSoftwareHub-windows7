using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace ClassSoftwareHub.Desktop.Core;

/// <summary>
/// 极简 Markdown → 控件（2026-10-18 上游新增；2026-10-22 移植到 Avalonia）。
///
/// <para>
/// 为什么自己写而不是引库：WinUI 3 没有内置 Markdown 控件，而模型输出永远是 Markdown
/// （`**加粗**`、`- 列表`、`### 标题`）。引一个第三方渲染器会带进一堆用不上的东西，
/// 而这里需要的只有四类语法 —— 够用就行。
/// </para>
/// <para>
/// ⛔ 刻意**不支持**表格、代码块、图片、链接。理由：AI 回答里出现这些的概率极低，
/// 而每多支持一种语法就多一堆边界情况；真遇到了按纯文本显示，信息也不会丢。
/// </para>
/// <para>
/// ⚠️ 流式渲染时这个方法会被反复调用（约每 80ms 一次），所以实现要够轻：
/// 只做一次字符串切分，不建语法树、不回溯。
/// </para>
/// <para>
/// ⚠️ 移植说明：WinUI 的 <c>RichTextBlock + Paragraph</c> 在 Avalonia 没有对应物
/// （<c>TextBlock.Inlines</c> 是扁平的，没有"块"这一层）⇒ 这里改成
/// **往一个 Panel（通常是 StackPanel）里塞若干 TextBlock**，一个 TextBlock 就是一段，
/// 段内用 <c>Run</c> 表达粗体/等宽。调用方负责清空/复用这个 Panel。
/// </para>
/// </summary>
public static class MarkdownText
{
    /// <summary>把 <paramref name="markdown"/> 渲染成控件填进 <paramref name="target"/>，会清空原有内容。</summary>
    public static void Apply(Panel target, string markdown)
    {
        target.Children.Clear();

        if (string.IsNullOrEmpty(markdown)) return;

        var lines = markdown.Replace("\r\n", "\n").Split('\n');
        var paragraph = new List<string>();

        void FlushParagraph()
        {
            if (paragraph.Count == 0) return;

            target.Children.Add(MakeBlock(string.Join(" ", paragraph).Trim(), 14, FontWeight.Normal, new Thickness(0, 0, 0, 6)));
            paragraph.Clear();
        }

        foreach (var raw in lines)
        {
            var trimmed = raw.Trim();

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
                    new Thickness(0, level == 1 ? 12 : 9, 0, 3)));
                continue;
            }

            // 有序列表：1. / 2. / 12. （保留原编号，不重新数）
            if (TryOrderedItem(trimmed, out var marker, out var rest))
            {
                FlushParagraph();

                var block = MakeBlock(rest, 14, FontWeight.Normal, new Thickness(0, 0, 0, 3));
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

                var block = MakeBlock(trimmed[2..].Trim(), 14, FontWeight.Normal, new Thickness(0, 0, 0, 3));
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
    private static TextBlock MakeBlock(string text, double size, FontWeight weight, Thickness margin)
    {
        var block = new TextBlock
        {
            FontSize = size,
            FontWeight = weight,
            TextWrapping = TextWrapping.Wrap,
            Margin = margin,
            Inlines = new InlineCollection(),
        };

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
