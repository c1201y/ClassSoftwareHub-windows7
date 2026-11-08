using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using ClassSoftwareHub.Desktop.Core;
using ClassSoftwareHub.Desktop.Services;
using ClassSoftwareHub.Desktop.Services.Docs;

namespace ClassSoftwareHub.Desktop.Controls;

/// <summary>
/// 文档正文渲染（需求 15）。把 <see cref="DocContent"/> 变成原生控件：
/// 标题 / 段落 / 列表 / 引用 / 代码块 / 图片，PDF 则交给系统默认程序打开。
///
/// <para>
/// ⛔ 不引第三方 Markdown / Word / PDF 库：Markdown 与 docx 自己解析（见同名解析器）。
/// </para>
/// <para>
/// 文字一律可选中（<c>SelectableTextBlock</c>）—— 文章常常要照着做操作，不能选就得手抄。
/// 链接点击走 <see cref="DocLinks"/>，与外部 <c>csh://</c> 链接同一条路。
/// </para>
///
/// <para>
/// 移植说明（WinUI → Avalonia）：
/// <list type="bullet">
///   <item>原版用 <c>RichTextBlock + Paragraph</c>；Avalonia 的 <c>TextBlock.Inlines</c> 是扁平的、
///         没有"块"这一层 ⇒ 改成**一个块一个控件**塞进 StackPanel，段内用 <c>Run</c>
///         （与 <see cref="MarkdownText"/> 同一套做法）。</item>
///   <item><c>IsTextSelectionEnabled</c> → <see cref="SelectableTextBlock"/>（TextBlock 本身不能选）。</item>
///   <item><c>BitmapImage.SetSourceAsync(IRandomAccessStream)</c> → <c>new Bitmap(Stream)</c>。</item>
///   <item>PDF：原版用 <c>Windows.Data.Pdf.PdfDocument</c> 逐页画；Win7 没有 WinRT 这一套 ⇒
///         降级成"用系统默认程序打开"，见 <see cref="RenderPdfAsync"/>。</item>
/// </list>
/// </para>
/// </summary>
public sealed class DocView : UserControl
{
    private static readonly HttpClient Http = new(Core.HttpTls.Handler()) { Timeout = TimeSpan.FromSeconds(20) };

    /// <summary>等宽字体。<c>Consolas</c> Win7 起自带，取不到会落到后面的候选。</summary>
    private static readonly FontFamily Monospace = new("Consolas, Courier New, monospace");

    private readonly StackPanel _host = new()
    {
        HorizontalAlignment = HorizontalAlignment.Stretch,
    };

    public DocView()
    {
        Content = _host;
    }

    public bool IsEmpty => _host.Children.Count == 0;

    /// <summary>渲染一篇文章。同一个实例可以反复调用（会先清空）。</summary>
    public async Task RenderAsync(DocContent content, CancellationToken ct = default)
    {
        _host.Children.Clear();

        try
        {
            switch (content.Type)
            {
                case "pdf":
                    await RenderPdfAsync(content.FilePath, ct);
                    break;

                case "docx":
                {
                    var bytes = await File.ReadAllBytesAsync(content.FilePath, ct);
                    RenderBlocks(DocxDocParser.Parse(bytes), content.Article.Title);
                    break;
                }

                case "txt":
                {
                    var text = await File.ReadAllTextAsync(content.FilePath, ct);
                    RenderBlocks(PlainTextDocParser.Parse(text), content.Article.Title);
                    break;
                }

                default:
                {
                    var text = await File.ReadAllTextAsync(content.FilePath, ct);
                    RenderBlocks(MarkdownDocParser.Parse(text, content.ResolveResource), content.Article.Title);
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppLog.Exception("docs", ex, $"渲染文档 {content.Article.Id}");
            _host.Children.Add(Notice("正文无法显示。可以返回列表重新打开一次，让这篇文档重新下载。"));
        }
    }

    public void Clear() => _host.Children.Clear();

    // ── 块 ───────────────────────────────────────────────────────

    private void RenderBlocks(IReadOnlyList<DocBlock> blocks, string title)
    {
        // 页头已经是大标题，正文第一块若就是同一个标题（md/docx 的 H1、txt 的首行）就不再重复一遍
        var start = 0;
        if (blocks.Count > 0 && title.Trim().Length > 0 && IsTitleBlock(blocks[0], title.Trim()))
            start = 1;

        for (var i = start; i < blocks.Count; i++)
        {
            var block = blocks[i];
            switch (block)
            {
                case DocHeadingBlock heading:
                    _host.Children.Add(BuildHeading(heading));
                    break;

                case DocParagraphBlock paragraph:
                    _host.Children.Add(BuildText(paragraph.Runs));
                    break;

                case DocListBlock list:
                    AddList(list);
                    break;

                case DocQuoteBlock quote:
                    _host.Children.Add(BuildQuote(quote));
                    break;

                case DocCodeBlock code:
                    _host.Children.Add(BuildCode(code));
                    break;

                case DocImageBlock image:
                    AddImage(image);
                    break;

                case DocDividerBlock:
                    _host.Children.Add(new Border
                    {
                        Height = 1,
                        Background = Res("CardStrokeColorDefaultBrush"),
                        HorizontalAlignment = HorizontalAlignment.Stretch,
                        Margin = new Thickness(0, 18, 0, 18),
                    });
                    break;
            }
        }
    }

    /// <summary>正文第一块是不是"又把标题写了一遍"。</summary>
    private static bool IsTitleBlock(DocBlock block, string title)
    {
        var text = block switch
        {
            DocHeadingBlock { Level: 1 } heading => JoinRuns(heading.Runs),
            DocParagraphBlock paragraph => JoinRuns(paragraph.Runs),
            _ => "",
        };

        var trimmed = text.Trim();
        return trimmed.Length > 0 && string.Equals(trimmed, title, StringComparison.CurrentCultureIgnoreCase);
    }

    private static string JoinRuns(IReadOnlyList<DocRun> runs)
    {
        var text = new StringBuilder();
        foreach (var run in runs) text.Append(run.Text);
        return text.ToString();
    }

    private static SelectableTextBlock BuildHeading(DocHeadingBlock heading)
    {
        var text = new SelectableTextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            FontWeight = FontWeight.SemiBold,
            FontSize = heading.Level switch { 1 => 24, 2 => 20, 3 => 17.5, 4 => 16, _ => 15 },
            Margin = new Thickness(0, heading.Level <= 2 ? 24 : 16, 0, 8),
            // ⛔ 显式建集合：Inlines 默认可能是 null，往 null 里 Add 会直接崩（同 MarkdownText 的坑）
            Inlines = new InlineCollection(),
        };

        AppendRuns(text.Inlines!, heading.Runs, text.FontSize);
        return text;
    }

    private static SelectableTextBlock BuildText(
        IReadOnlyList<DocRun> runs, double fontSize = 15, double bottomMargin = 10)
    {
        var text = new SelectableTextBlock
        {
            FontSize = fontSize,
            LineHeight = 27,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, bottomMargin),
            Inlines = new InlineCollection(),
        };

        AppendRuns(text.Inlines!, runs, fontSize);
        return text;
    }

    private void AddList(DocListBlock list)
    {
        foreach (var item in list.Items)
        {
            var row = new Grid { Margin = new Thickness(item.Level * 20, 0, 0, 4) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var marker = new TextBlock
            {
                Text = item.Marker,
                FontSize = 15,
                LineHeight = 27,
                MinWidth = 14,
                Margin = new Thickness(0, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Top,
                Opacity = 0.7,
            };

            var content = BuildText(item.Runs, bottomMargin: 0);

            Grid.SetColumn(marker, 0);
            Grid.SetColumn(content, 1);
            row.Children.Add(marker);
            row.Children.Add(content);
            _host.Children.Add(row);
        }

        _host.Children.Add(new Border { Height = 4 });
    }

    private static Border BuildQuote(DocQuoteBlock quote)
    {
        var panel = new StackPanel();
        foreach (var line in quote.Lines)
            panel.Children.Add(BuildText(line, fontSize: 14.5, bottomMargin: 4));

        return new Border
        {
            BorderThickness = new Thickness(3, 0, 0, 0),
            BorderBrush = Res("CardStrokeColorDefaultBrush"),
            Padding = new Thickness(14, 4, 8, 4),
            Margin = new Thickness(0, 4, 0, 14),
            Child = panel,
        };
    }

    private static Border BuildCode(DocCodeBlock code)
    {
        return new Border
        {
            Background = Res("CardBackgroundFillColorDefaultBrush"),
            BorderBrush = Res("CardStrokeColorDefaultBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(14, 10, 14, 12),
            Margin = new Thickness(0, 4, 0, 14),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Child = new SelectableTextBlock
            {
                Text = code.Text,
                FontFamily = Monospace,
                FontSize = 13,
                LineHeight = 20,
                TextWrapping = TextWrapping.Wrap,
            },
        };
    }

    // ── 图片 ─────────────────────────────────────────────────────

    private void AddImage(DocImageBlock block)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 6, 0, 16) };

        var image = new Image
        {
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Left,
            MaxWidth = 760,
        };
        panel.Children.Add(image);

        if (block.Alt.Length > 0)
        {
            panel.Children.Add(new TextBlock
            {
                Text = block.Alt,
                FontSize = 12.5,
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.6,
                Margin = new Thickness(0, 6, 0, 0),
            });
        }

        _host.Children.Add(panel);
        _ = LoadImageAsync(block, image, panel);
    }

    private static async Task LoadImageAsync(DocImageBlock block, Image image, StackPanel panel)
    {
        var bitmap = await ReadBitmapAsync(block);
        if (bitmap is null)
        {
            panel.Children.Clear();
            panel.Children.Add(Notice(block.Alt.Length > 0 ? $"图片无法显示：{block.Alt}" : "图片无法显示"));
            return;
        }

        image.Source = bitmap;
    }

    private static async Task<Bitmap?> ReadBitmapAsync(DocImageBlock block)
    {
        try
        {
            byte[]? data = block.Data;

            if (data is null && block.Source.Length > 0)
            {
                if (block.Source.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                    || block.Source.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    data = await Http.GetByteArrayAsync(block.Source);
                }
                else if (File.Exists(block.Source))
                {
                    data = await File.ReadAllBytesAsync(block.Source);
                }
            }

            if (data is null || data.Length == 0) return null;

            // WinUI 的 InMemoryRandomAccessStream + BitmapImage.SetSourceAsync → Avalonia 直接吃 Stream。
            // ⚠️ 流不能 using 掉就完：Avalonia 的 Bitmap 在构造时就解完码，这里 nonetheless 保留引用，
            //    交给 GC —— Bitmap 持有它的话提前 Dispose 会崩。
            return new Bitmap(new MemoryStream(data, writable: false));
        }
        catch (Exception ex)
        {
            AppLog.Warn("docs", "图片读取失败：" + ex.Message);
            return null;
        }
    }

    // ── PDF ─────────────────────────────────────────────────────

    /// <summary>
    /// PDF 正文。
    ///
    /// <para>
    /// <c>// TODO(win7):</c> 原版用 WinRT 的 <c>Windows.Data.Pdf.PdfDocument</c> 把每一页渲染成
    /// 位图（<c>RenderToStreamAsync</c>）。那套 API 属于 Windows 10 才有的 WinRT 组件，Windows 7 上
    /// 根本没有（Avalonia 也没有内置 PDF 渲染器，仓库又不引第三方 PDF 库），所以这里**不假装能渲染**：
    /// 显示一行说明 + 一个按钮，用系统默认的 PDF 阅读器打开本机缓存里的那份文件。
    /// </para>
    /// <para>
    /// 缓存文件名是去扩展名的随机十六进制（见 <see cref="DocLibrary"/>），系统按扩展名挑程序，
    /// 所以这里要先把内容落到一个临时 .pdf 再打开 —— 否则双击只会"没有任何反应"。
    /// </para>
    /// </summary>
    private async Task RenderPdfAsync(string path, CancellationToken ct)
    {
        // 说明 + 按钮：保持原版"正文里要能读到东西"的结构，只是换成交给外部程序
        var panel = new StackPanel { Margin = new Thickness(0, 8, 0, 12), Spacing = 10 };
        panel.Children.Add(Notice("这份文档是 PDF。当前系统无法在本页内嵌预览，可以用系统的默认程序打开。"));

        var button = new Button
        {
            Content = "用默认程序打开",
            HorizontalAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(12, 6, 12, 6),
        };
        button.Click += (_, _) => OpenExternally(path);
        panel.Children.Add(button);

        _host.Children.Add(panel);

        // 渲染路径上没有任何 await 的真实异步工作了，保留签名与取消语义
        await Task.CompletedTask;
        ct.ThrowIfCancellationRequested();
    }

    /// <summary>把缓存里的 PDF 用系统默认程序打开（先补一个 .pdf 扩展名到临时文件）。</summary>
    private static void OpenExternally(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                AppLog.Warn("docs", "PDF 缓存文件不存在：" + path);
                return;
            }

            var temp = Path.Combine(Path.GetTempPath(), "csh-doc-" + Guid.NewGuid().ToString("N") + ".pdf");
            File.Copy(path, temp, overwrite: true);

            Process.Start(new ProcessStartInfo(temp) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppLog.Warn("docs", "打开 PDF 失败：" + ex.Message);
        }
    }

    // ── 行内 ─────────────────────────────────────────────────────

    /// <summary>
    /// 把 run 列表拼进 Inlines。
    ///
    /// <para>
    /// 移植说明：原版用 <c>Hyperlink</c> 内联（WinUI 有 <c>Hyperlink</c> 这个 Inline）。
    /// Avalonia 的内联层没有 Hyperlink，能做点击的只有 <c>HyperlinkButton</c> 控件 ⇒
    /// 链接那一段包进 <see cref="InlineUIContainer"/>。代价是链接文字不参与"整段选中"，
    /// 换来的是点击行为与原版一致（都走 <see cref="DocLinks"/>）。
    /// </para>
    /// </summary>
    private static void AppendRuns(InlineCollection inlines, IReadOnlyList<DocRun> runs, double fontSize)
    {
        foreach (var run in runs)
        {
            var pieces = run.Text.Split('\n');
            for (var i = 0; i < pieces.Length; i++)
            {
                if (i > 0) inlines.Add(new LineBreak());
                if (pieces[i].Length == 0) continue;

                if (run.Link is { Length: > 0 } link)
                {
                    var button = new HyperlinkButton
                    {
                        Content = pieces[i],
                        Padding = new Thickness(0),
                        MinWidth = 0,
                        MinHeight = 0,
                        FontSize = fontSize,
                        Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
                    };
                    if (run.Bold) button.FontWeight = FontWeight.SemiBold;
                    if (run.Italic) button.FontStyle = FontStyle.Italic;
                    if (run.Code) button.FontFamily = Monospace;

                    button.Click += (_, _) => { _ = DocLinks.OpenAsync(link); };
                    inlines.Add(new InlineUIContainer { Child = button });
                    continue;
                }

                var text = new Run { Text = pieces[i] };
                StyleRun(text, run);
                inlines.Add(text);
            }
        }
    }

    private static void StyleRun(Run run, DocRun style)
    {
        if (style.Bold) run.FontWeight = FontWeight.SemiBold;
        if (style.Italic) run.FontStyle = FontStyle.Italic;
        if (style.Code) run.FontFamily = Monospace;
    }

    private static TextBlock Notice(string text) => new()
    {
        Text = text,
        TextWrapping = TextWrapping.Wrap,
        FontSize = 13.5,
        Opacity = 0.65,
        Margin = new Thickness(0, 4, 0, 12),
    };

    /// <summary>主题画笔：WinUI 的 <c>Application.Current.Resources[key]</c> → <see cref="ThemeBrush.Get"/>。</summary>
    private static IBrush? Res(string key) => ThemeBrush.Get(null, key);
}
