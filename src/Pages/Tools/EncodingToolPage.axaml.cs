using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using ClassSoftwareHub.Desktop.Platform;

namespace ClassSoftwareHub.Desktop.Pages.Tools;

/// <summary>
/// 软件详情页点「校验」时带过来的请求：官方校验值 + 软件名（只用于提示文案）。
/// </summary>
public sealed record HashCheckRequest(string Hash, string AppName);

/// <summary>
/// 编码 / 哈希工具。
///
/// 2026-09-29 重构（Nick）：按**用途**拆成两条主线（<c>SelectorBar</c> 两个页签），
/// 默认停在「文件校验」—— 现实中这个工具绝大多数是被用来核对刚下载的安装包的。
/// 原来两件事挤在同一屏（左边编解码、右边输出、下面一大块哈希 + 模式单选），
/// 用户得先判断"我要看哪一块"，再在上下两处结果之间找。
/// </summary>
public sealed partial class EncodingToolPage : PageBase
{
    private static readonly string[] Algorithms = { "MD5", "SHA-1", "SHA-256", "SHA-512" };
    private const string EmptyMark = "—";

    /// <summary>「文件校验」页签里的四行哈希（算法名 · 值 · 复制）。</summary>
    private readonly Dictionary<string, TextBlock> _fileHashTexts = new();

    /// <summary>「文本编解码」页签里的四行哈希（内容跟输入框实时联动）。</summary>
    private readonly Dictionary<string, TextBlock> _textHashTexts = new();

    private string _filePath = "";
    private Dictionary<string, string> _fileHashes = new();
    private CancellationTokenSource? _fileCts;

    public EncodingToolPage()
    {
        InitializeComponent();
        BuildHashRows(HashPanel, _fileHashTexts);
        BuildHashRows(TextHashPanel, _textHashTexts);

        // 两张卡的 Checked 挂在 XAML 上（Mode_Changed）；这里只设初始状态。
        // 默认停在「文件校验」—— 现实中这个工具绝大多数是用来核对刚下载的安装包的。
        // ⚠️ IsChecked 不能在 XAML 里写 True，必须在代码里赋（见 App.axaml 的 CshModeCardStyle）。
        FileTab.IsChecked = true;
        ShowTab(file: true);

        SetFileRowsEmpty(EmptyMark);
        RefreshTextHashes();

        // 离开页面时取消可能还在跑的哈希计算。
        // ⚠️ 这行原本只写在页内「返回」按钮的点击处理里 —— 统一改用导航栏返回后那个按钮就点不到了，
        //    所以挪到 Unloaded：导航离开同样会触发，还能覆盖「非导航地被移除」的情况。
        Unloaded += (_, _) => _fileCts?.Cancel();
    }


    /// <summary>
    /// 从软件详情页的「校验」键跳进来时，参数里带着那个软件的官方校验值：
    /// 直接落在**文件校验**页签并预填校验值 —— 用户只要把刚下好的安装包拖进来就出结论，
    /// 省掉"自己打开工具 → 找到哈希 → 复制 → 粘贴 → 再选文件"这一串。
    /// 普通从工具列表点进来时参数是 null，一切照旧。
    /// </summary>
    public override void OnNavigatedTo(object? parameter)
    {
        base.OnNavigatedTo(parameter);
        if (parameter is not HashCheckRequest req || string.IsNullOrWhiteSpace(req.Hash)) return;

        FileTab.IsChecked = true;                        // 触发 Mode_Changed → ShowTab(file: true)
        ShowTab(file: true);
        ExpectedBox.Text = req.Hash;
        // ⚠️ 必须自己再刷一次核对行：程序设 Text 时 TextChanged 不一定回来（实测没回来），
        //    不刷的话右边是空的，用户看不到"选文件后自动比对"那句引导。
        RefreshMatch();

        var who = string.IsNullOrWhiteSpace(req.AppName) ? "该软件" : "「" + req.AppName + "」";
        FromAppHint.Text = $"已带入{who}的官方校验值，将下载的安装包拖入下方区域后自动核对。";
        FromAppHint.IsVisible = true;
    }

    // ══════════ 页签 ══════════
    /// <summary>两张「用途」卡片谁被选中，就显示对应那一块。</summary>
    private void Mode_Changed(object? sender, RoutedEventArgs e) => ShowTab(FileTab.IsChecked == true);

    private void ShowTab(bool file)
    {
        FileView.IsVisible = file;
        TextView.IsVisible = !file;

        // 换页签回到顶部，别把上一个页签的滚动位置带过来。
        // ⚠️ 只有**已经挂进视觉树**之后才调 ChangeView：构造函数里控件还没进树，
        //    那时调等于"在布局过程中请求滚动"，原 WinUI 会判成 Layout cycle（本机实测出现过一次
        //    LayoutCycleException，见 App.OnUnhandledException 的注释）。
        var view = file ? FileView : TextView;
        // ⚠️ 原版 ChangeView(0, 0, null) → Avalonia 没有，直接写 Offset（SettingsPage 同款写法）
        if (view.IsLoaded) view.Offset = new Vector(0, 0);
    }

    // ══════════ 哈希行（两个页签各一套，样式完全一致） ══════════
    /// <summary>
    /// 四种算法各一行：算法名（定宽）+ 值（占满，长了折行）+ 复制。
    /// 2026-09-29 前是"标题一行、值框一行"的两行式，四个算法就是八行，一屏看不全。
    ///
    /// ⚠️ 2026-09-29 二次调整（Nick）：「那些哈希值文本框，看起来像是可以编辑的，但实际上只是为了复制」。
    ///    原来每行的值外面套一个灰底圆角 Border，视觉上跟 TextBox 一模一样 —— 用户会去点它想改。
    ///    去掉了底框，改用逐行分隔线。
    ///
    /// ⚠️ dv1.1.0（2026-10-02，Nick：「所有块都是方块包方块，像一张填满格子的 Excel」，照上游移植）：
    ///    逐行分隔线仍是"手画的表格"，整块换成 **ListBox** 承载 —— 行容器 ListBoxItem 自带系统
    ///    悬停底色 / 圆角 / 键盘导航，外面那张大卡片也一并删掉。
    ///    ⚠️ 上游用 WinUI 的 ListView + ItemTemplate（x:Bind 到 HashRow）；Avalonia 无 ListView，
    ///       且资源字典里的 DataTemplate 不参与事件绑定解析（复制键会编译失败），
    ///       所以这里仍走**代码后置建行**（本仓库既有做法），容器换成 ListBox 即可拿到同一份观感。
    /// </summary>
    private void BuildHashRows(ListBox host, Dictionary<string, TextBlock> table)
    {
        for (var i = 0; i < Algorithms.Length; i++)
        {
            var name = Algorithms[i];

            // ⚠️ 原版 TextBlock.IsTextSelectionEnabled → Avalonia 用 SelectableTextBlock（DetailPage 同款），
            //    基类仍是 TextBlock，字典类型不用动。
            var value = new SelectableTextBlock
            {
                Text = EmptyMark,
                FontFamily = new FontFamily("Consolas"),
                FontSize = 12.5,
                TextWrapping = TextWrapping.Wrap,
                // SelectableTextBlock 天生可选中，无需（也没有）IsTextSelectionEnabled
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            };
            table[name] = value;

            var copy = new Button { Content = "复制", Padding = new Thickness(10, 0, 10, 0), FontSize = 12.5 };
            var captured = name;
            copy.Click += (_, _) => Copy(table[captured].Text, captured);

            // 原版 ColumnSpacing=12 → 第 1/2 列 Margin 模拟
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(72) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            row.Children.Add(new TextBlock
            {
                Text = name,
                FontSize = 12.5,
                FontWeight = FontWeight.SemiBold,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            });

            value.Margin = new Thickness(12, 0, 0, 0);
            Grid.SetColumn(value, 1);
            row.Children.Add(value);

            copy.Margin = new Thickness(12, 0, 0, 0);
            Grid.SetColumn(copy, 2);
            row.Children.Add(copy);

            // 行本身交给 ListBoxItem（悬停底色 / 圆角 / 内边距都在 App.axaml 的 cshResultRows 样式里）
            host.Items.Add(new ListBoxItem { Content = row, HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Stretch });
        }
    }

    private Brush Res(string key) => Services.ThemeBrush.Get(this, key);

    // ══════════ 文件模式 ══════════

    private void PickFile_Click(object? sender, RoutedEventArgs e) => ChooseFile();

    /// <summary>拖放区整块也可以点 —— 少一层"我到底该点哪个按钮"的犹豫。</summary>
    private void DropZone_Tapped(object? sender, TappedEventArgs e) => ChooseFile();

    private async void ChooseFile()
    {
        try
        {
            // 原版 FileOpenPicker(PickerViewMode.List / PickerLocationId.Downloads / "*" 全类型)
            // → Avalonia 的 IStorageProvider 没有起始位置与视图模式概念，只保留"选一个任意文件"的语义。
            var top = TopLevel.GetTopLevel(this);
            if (top is null) return;
            var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "选择文件",
                AllowMultiple = false,
            });
            if (files.Count == 0) return;
            await LoadFileAsync(files[0].Path.LocalPath);
        }
        catch (Exception ex)
        {
            Toast.Text = "选择文件失败：" + ex.Message;
        }
    }

    private void ClearFile_Click(object? sender, RoutedEventArgs e)
    {
        _fileCts?.Cancel();
        _filePath = "";
        _fileHashes = new();
        ClearFileButton.IsEnabled = false;
        HashProgress.IsVisible = false;
        PickFileButton.IsEnabled = true;
        DropHintText.Text = IdleHint;
        SourceText.Text = IdleSubHint;
        SetFileRowsEmpty(EmptyMark);
        RefreshMatch();
    }

    // 文件条上的两行默认文案（同 XAML 里的初始值）—— 清空文件时回到这个状态
    private const string IdleHint = "把文件拖到这里，或单击选择";
    private const string IdleSubHint = "支持任意文件类型，大文件流式读取";

    private void File_DragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = DragDropEffects.Copy;
        DropHintText.Text = "松开鼠标即开始计算哈希";
    }

    private void File_DragLeave(object? sender, DragEventArgs e)
    {
        DropHintText.Text = _filePath.Length == 0 ? IdleHint : "可拖入新文件重新计算";
    }

    private async void File_Drop(object? sender, DragEventArgs e)
    {
        try
        {
            // 原版经 DataView.GetStorageItemsAsync() 取文件 → Avalonia 直接给文件名列表
            var path = e.Data?.GetFileNames()?.FirstOrDefault();
            if (path is not null) await LoadFileAsync(path);
        }
        catch (Exception ex)
        {
            Toast.Text = "拖入的文件读取失败：" + ex.Message;
            DropHintText.Text = IdleHint;
        }
    }

    private async Task LoadFileAsync(string path)
    {
        _filePath = path;
        _fileHashes = new();
        ClearFileButton.IsEnabled = true;
        PickFileButton.IsEnabled = false;
        HashProgress.IsVisible = true;
        SetFileRowsEmpty("计算中…");
        SourceText.Text = $"{System.IO.Path.GetFileName(path)} · 计算中";
        DropHintText.Text = "已选文件，可再拖入新文件重新计算";

        _fileCts?.Cancel();
        var cts = new CancellationTokenSource();
        _fileCts = cts;

        try
        {
            var info = new FileInfo(path);
            var hashes = await Task.Run(() => HashFile(path, cts.Token), cts.Token);
            if (cts.IsCancellationRequested) return;

            _fileHashes = hashes;
            ApplyFileHashes(hashes);
            SourceText.Text = $"{info.Name}（{SizeText(info.Length)}）· {info.FullName}";
        }
        catch (OperationCanceledException) { /* 换文件了，旧计算作废 */ }
        catch (Exception ex)
        {
            SourceText.Text = "计算哈希失败：" + ex.Message;
            SetFileRowsEmpty(EmptyMark);
        }
        finally
        {
            if (!cts.IsCancellationRequested)
            {
                HashProgress.IsVisible = false;
                PickFileButton.IsEnabled = true;
            }
        }
        RefreshMatch();
    }

    /// <summary>流式算四种哈希：1MB 一块，多大的文件都不吃内存。</summary>
    private static Dictionary<string, string> HashFile(string path, CancellationToken token)
    {
        using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        using var sha1 = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        using var sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var sha512 = IncrementalHash.CreateHash(HashAlgorithmName.SHA512);

        var buffer = new byte[1 << 20];
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, buffer.Length, FileOptions.SequentialScan))
        {
            int read;
            while ((read = fs.Read(buffer, 0, buffer.Length)) > 0)
            {
                token.ThrowIfCancellationRequested();
                var span = buffer.AsSpan(0, read);
                md5.AppendData(span);
                sha1.AppendData(span);
                sha256.AppendData(span);
                sha512.AppendData(span);
            }
        }

        return new Dictionary<string, string>
        {
            ["MD5"] = Convert.ToHexString(md5.GetHashAndReset()).ToLowerInvariant(),
            ["SHA-1"] = Convert.ToHexString(sha1.GetHashAndReset()).ToLowerInvariant(),
            ["SHA-256"] = Convert.ToHexString(sha256.GetHashAndReset()).ToLowerInvariant(),
            ["SHA-512"] = Convert.ToHexString(sha512.GetHashAndReset()).ToLowerInvariant(),
        };
    }

    private static string SizeText(long bytes)
        => bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):0.##} GB"
         : bytes >= 1L << 20 ? $"{bytes / (double)(1L << 20):0.##} MB"
         : bytes >= 1L << 10 ? $"{bytes / (double)(1L << 10):0.#} KB"
         : $"{bytes} B";

    // ══════════ 哈希显示 + 核对 ══════════
    private void ApplyFileHashes(Dictionary<string, string> hashes)
    {
        foreach (var name in Algorithms)
            _fileHashTexts[name].Text = hashes.TryGetValue(name, out var v) && v.Length > 0 ? v : EmptyMark;
    }

    private void SetFileRowsEmpty(string placeholder)
    {
        foreach (var name in Algorithms) _fileHashTexts[name].Text = placeholder;
    }

    // ══════════ 文本（编解码 + 实时哈希） ══════════
    private void Input_TextChanged(object? sender, TextChangedEventArgs e) => RefreshTextHashes();

    private void Output_TextChanged(object? sender, TextChangedEventArgs e)
    {
        var has = !string.IsNullOrEmpty(OutputBox.Text);
        CopyOutButton.IsEnabled = has;
        UseOutputButton.IsEnabled = has;
    }

    private void RefreshTextHashes()
    {
        var text = InputBox.Text ?? "";
        if (text.Length == 0)
        {
            foreach (var name in Algorithms) _textHashTexts[name].Text = EmptyMark;
            return;
        }

        var hashes = ComputeTextHashes(text);
        foreach (var name in Algorithms) _textHashTexts[name].Text = hashes[name];
    }

    private static Dictionary<string, string> ComputeTextHashes(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        return new Dictionary<string, string>
        {
            ["MD5"] = Convert.ToHexString(MD5.HashData(bytes)).ToLowerInvariant(),
            ["SHA-1"] = Convert.ToHexString(SHA1.HashData(bytes)).ToLowerInvariant(),
            ["SHA-256"] = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
            ["SHA-512"] = Convert.ToHexString(SHA512.HashData(bytes)).ToLowerInvariant(),
        };
    }

    private void Expected_TextChanged(object? sender, TextChangedEventArgs e) => RefreshMatch();

    private static string Norm(string s)
        => new string(s.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    /// <summary>
    /// 核对结论 —— 只针对**文件**校验（粘贴框就在文件页签里）。
    /// 结论直接写在粘贴框右边，粘完不用往下翻。
    /// </summary>
    private void RefreshMatch()
    {
        var expected = Norm(ExpectedBox.Text ?? "");
        if (expected.Length == 0)
        {
            MatchText.Text = "";
            return;
        }

        if (_fileHashes.Count == 0)
        {
            // "还没选文件"是指引不是错误 —— 用次要色，别拿红字吓人
            MatchText.Text = "选择文件后自动比对";
            MatchText.Foreground = Res("TextFillColorSecondaryBrush");
            return;
        }

        var hit = Algorithms.FirstOrDefault(a => Norm(_fileHashTexts[a].Text) == expected);
        if (hit is not null)
        {
            MatchText.Text = $"✓ 与 {hit} 一致";
            MatchText.Foreground = Res("SystemFillColorSuccessBrush");
        }
        else
        {
            MatchText.Text = "✗ 与上面四种哈希值均不一致";
            MatchText.Foreground = Res("SystemFillColorCriticalBrush");
        }
    }

    // ══════════ 编解码 ══════════
    private void B64Enc_Click(object? sender, RoutedEventArgs e)
    {
        try { OutputBox.Text = Convert.ToBase64String(Encoding.UTF8.GetBytes(InputBox.Text ?? "")); StatusText.Text = ""; }
        catch (Exception ex) { StatusText.Text = "编码失败：" + ex.Message; }
    }

    private void B64Dec_Click(object? sender, RoutedEventArgs e)
    {
        try { OutputBox.Text = Encoding.UTF8.GetString(Convert.FromBase64String((InputBox.Text ?? "").Trim())); StatusText.Text = ""; }
        catch { StatusText.Text = "解码失败，请检查 Base64 内容"; }
    }

    private void UrlEnc_Click(object? sender, RoutedEventArgs e)
        => OutputBox.Text = Uri.EscapeDataString(InputBox.Text ?? "");

    private void UrlDec_Click(object? sender, RoutedEventArgs e)
    {
        try { OutputBox.Text = Uri.UnescapeDataString(InputBox.Text ?? ""); StatusText.Text = ""; }
        catch { StatusText.Text = "解码失败，请检查内容"; }
    }

    private void CopyOut_Click(object? sender, RoutedEventArgs e) => Copy(OutputBox.Text, "结果");

    private void UseOutput_Click(object? sender, RoutedEventArgs e) => InputBox.Text = OutputBox.Text;

    /// <summary>清空输入框（文本哈希会跟着一起归零）。</summary>
    private void ClearInput_Click(object? sender, RoutedEventArgs e) => InputBox.Text = "";

    private async void Copy(string? text, string what)
    {
        if (string.IsNullOrEmpty(text) || text == EmptyMark || text.StartsWith("计算中")) return;
        try
        {
            // 原版 DataPackage + Clipboard.SetContent → Avalonia 的 IClipboard.SetTextAsync
            var top = TopLevel.GetTopLevel(this);
            if (top?.Clipboard is not { } clip) return;
            await clip.SetTextAsync(text);
            StatusText.Text = $"已复制{what}";
            Toast.Text = $"已复制{what}";
        }
        catch { StatusText.Text = "复制失败"; }
    }
}
