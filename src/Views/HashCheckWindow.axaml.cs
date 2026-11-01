using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using ClassSoftwareHub.Desktop.Core;
using ClassSoftwareHub.Desktop.Platform;
using ClassSoftwareHub.Desktop.Services;

namespace ClassSoftwareHub.Desktop.Views;

/// <summary>
/// 哈希校验悬浮窗（自上游 dv1.2.0-insider1.8 移植）。
///
/// <para>
/// 用途：核对下载到的安装包。拖进文件 → 立刻出四种哈希 → 粘贴官网给的校验值 → 自动比对。
/// 原来这件事只能进「系统工具 → 编码 / 哈希工具」，为核对一个文件把整个应用拉出来太重，
/// 所以单开一扇窗，可以一直摆在旁边。
/// </para>
///
/// <para>
/// ⚠️ 哈希计算用的是 <see cref="FileHash"/>（跟编码页同一份实现），本窗只负责界面与比对展示。
/// </para>
///
/// <para>
/// ⚠️ 关掉 = **真销毁**，跟学科工具窗一致：这扇窗没有"留着复用"的价值，
///    真关最省事，也不会在退出链路上留一个没关的窗口把进程吊住。
/// </para>
///
/// <para>⚠️ 移植说明（WinUI → Avalonia）：</para>
/// <list type="bullet">
/// <item><c>AppWindow</c> / <c>DisplayArea</c> → <c>ClientSize</c> + <c>Screens.Primary.WorkingArea</c>（不用再乘 DPI）；</item>
/// <item><c>ModernWindow.ApplyTitleBar</c> → 仓储既有写法：<see cref="SelfDrawnFrame"/> 接管非客户区、关闭键自己画；</item>
/// <item><c>FileOpenPicker</c> → <see cref="FilePickerCompat"/>（Win7 走 comdlg32 兜底）；</item>
/// <item><c>DataView.GetStorageItemsAsync</c> → <c>DataObject.GetFileNames</c>；<c>Clipboard.SetContent</c> → <c>IClipboard.SetTextAsync</c>。</item>
/// </list>
/// </summary>
public sealed partial class HashCheckWindow : Window
{
    private const int DefaultWidthDip = 560;

    /// <summary>
    /// 首帧用的高度。**随后会按内容重算一次**（见 <see cref="ResizeToContent"/>）——
    /// 内容只有 400 左右，写死 640 下面会空出一大块。
    /// </summary>
    private const int DefaultHeightDip = 480;

    /// <summary>自绘标题栏高度（dip）。必须和 XAML 里那一行的高度一致。</summary>
    private const int TitleBarHeightDip = 32;

    private const string IdleTitle = "把文件拖到这里，或单击选择";

    private static HashCheckWindow? _instance;

    private SelfDrawnFrame? _frame;
    private RoundedCorners? _corners;

    private readonly string _backdropPrefer;

    /// <summary>四种算法各一行（顺序见 <see cref="FileHash.Algorithms"/>）。</summary>
    private readonly ObservableCollection<HashLine> _lines = new();

    private Dictionary<string, string> _hashes = new();

    /// <summary>用户换文件时作废上一轮计算（大文件算到一半再换，不能让它把新结果盖掉）。</summary>
    private CancellationTokenSource? _cts;

    /// <summary>是否已经按内容定过高度（只定一次，之后内容变化走滚动）。</summary>
    private bool _autoSized;

    /// <summary>按内容定高的重试次数（首帧量不到真实高度时再试几拍）。</summary>
    private int _resizeTries;

    private HashCheckWindow()
    {
        InitializeComponent();

        _backdropPrefer = NormalizeBackdrop(App.Settings.Current.Backdrop);

        foreach (var name in FileHash.Algorithms) _lines.Add(new HashLine(name));
        HashList.ItemsSource = _lines;
        SetRowsEmpty("—");

        Configure();

        // 内容排好之后按实际高度收一次窗口（只做一次：之后内容变化走滚动，不让窗口自己跳）
        Root.Loaded += (_, _) => ResizeToContent();
    }

    // ── 对外 ─────────────────────────────────────────────────────────────

    /// <summary>
    /// 摊开这扇窗；已经开着就把它调到前面（不重新建、不丢当前算好的结果）。
    /// </summary>
    public static void Show()
    {
        try
        {
            _instance ??= new HashCheckWindow();
            _instance.BringUp();
        }
        catch (Exception ex)
        {
            AppLog.Exception("hash-check", ex, "打开哈希校验窗口");
        }
    }

    /// <summary>退出应用时真正销毁（由 MainWindow 的退出链路调用；没开过就什么都不做）。</summary>
    public static void CloseForExit()
    {
        var w = _instance;
        _instance = null;
        try { w?.Close(); } catch { }
        AppLog.Info("exit", "哈希校验窗口实例已请求 Close");
    }

    // ── 窗口配置 ─────────────────────────────────────────────────────────

    private void Configure()
    {
        try
        {
            Title = "编码 / 哈希校验";
            ShowInTaskbar = true;
            CanResize = true;               // 上游 IsResizable = true
                                            // ⚠️ Avalonia 的 Window 没有 CanMaximize / CanMinimize
                                            //    （上游那两颗按钮已由自绘标题栏去掉了：只画关闭键）。
            MinWidth = 460;
            MinHeight = 340;

            ExtendClientAreaToDecorationsHint = false;
            AttachSelfDrawnFrame();

            try { _corners = RoundedCorners.Attach(this); } catch { }

            ThemeCompat.Apply(Root);

            ApplySize();
            CenterOnScreen();

            Closed += (_, _) =>
            {
                _cts?.Cancel();
                _instance = null;
            };
        }
        catch (Exception ex)
        {
            AppLog.Exception("hash-check", ex, "哈希校验窗口初始化");
        }
    }

    private void AttachSelfDrawnFrame()
    {
        try
        {
            _frame = SelfDrawnFrame.Attach(this);
            _frame.ResizeEnabled = true;
            _frame.TitleBarHeight = TitleBarHeightDip;
            _frame.InteractiveRegions = TitleBarInteractiveRegions;
            _frame.EraseColorProvider = SelfDrawnFrame.EraseColorFrom(Root);
        }
        catch (Exception ex)
        {
            _frame = null;
            AppLog.Debug("hash-check", "自绘窗口框架挂载失败，退回原生标题栏: " + ex.Message);
        }
    }

    /// <summary>
    /// 标题栏里「点了不该拖动窗口」的区域（dip，窗口坐标）。
    /// 不挖出来的话，点关闭键会被系统当成拖窗口 —— 点击直接丢失。
    /// </summary>
    private IReadOnlyList<Rect> TitleBarInteractiveRegions()
    {
        var list = new List<Rect>(1);
        try
        {
            var button = CaptionCloseButton;
            if (button is not null && button.IsVisible && button.Bounds.Width > 0 && button.Bounds.Height > 0)
            {
                var origin = button.TranslatePoint(new Point(0, 0), this);
                if (origin is { } p)
                    list.Add(new Rect(p.X, p.Y, button.Bounds.Width, button.Bounds.Height));
            }
        }
        catch
        {
            // 控件树正在重建时可能取不到坐标：跳过就行，顶多那一下点不动
        }

        return list;
    }

    private void CaptionClose_Click(object? sender, RoutedEventArgs e)
    {
        try { Close(); } catch { }
    }

    private void ApplySize()
    {
        try
        {
            var work = Screens.Primary?.WorkingArea;
            if (work is null) return;

            var scale = Scale();
            var maxW = work.Value.Width / scale - 40;
            var maxH = work.Value.Height / scale - 60;

            var w = Math.Clamp(DefaultWidthDip, 460, Math.Max(460, maxW));
            var h = Math.Clamp(DefaultHeightDip, 340, Math.Max(340, maxH));

            ClientSize = new Size(w, h);
        }
        catch (Exception ex)
        {
            AppLog.Warn("hash-check", "哈希校验窗尺寸设置失败：" + ex.Message);
        }
    }

    /// <summary>
    /// 按内容实际高度把窗口收到合适大小。
    ///
    /// <para>
    /// 算法：标题栏 32 + 上下留白 24 + 内容高 + 56 余量。
    /// 那 56 是给「选了文件之后才出现」的两行（文件名 / 大小 / 路径 + 比对结论）预留的，
    /// 不预留的话一选文件就得往下滚，反而更难用。
    /// </para>
    /// <para>
    /// ⚠️ 只在首次布局后做一次：每次内容变化都重算会让窗口在用户眼前一跳一跳。
    /// </para>
    /// </summary>
    private void ResizeToContent()
    {
        if (_autoSized) return;

        try
        {
            // ⚠️ 用 Extent（内容真实范围），不用视口高：ScrollViewer 会把内容纵向拉伸到视口高，
            //    拿视口高去算等于窗口永远一次都缩不下去。
            var contentHeight = Math.Max(ContentScroll.Extent.Height, ContentPanel.Bounds.Height);
            if (contentHeight <= 0)
            {
                // 还没排好：再等一两拍（最多五次，避免极小概率下的死循环）
                if (++_resizeTries <= 5)
                    DispatcherTimer.RunOnce(ResizeToContent, TimeSpan.FromMilliseconds(120));
                return;
            }

            // 内容没占满窗口时 Extent 也等于视口高，这时用子元素累加兜底
            if (contentHeight >= ContentScroll.Viewport.Height - 1 && ContentPanel.Children.Count > 0)
            {
                var sum = 0.0;
                foreach (var child in ContentPanel.Children)
                    if (child is Control { IsVisible: true } c) sum += c.Bounds.Height;
                sum += ContentPanel.Spacing * Math.Max(0, ContentPanel.Children.Count - 1);
                if (sum > 0) contentHeight = sum;
            }

            _autoSized = true;

            var work = Screens.Primary?.WorkingArea;
            var maxH = work is null ? 800 : work.Value.Height / Scale() - 60;

            // 32 = 自绘标题栏那一行；4 / 20 = ScrollViewer 上下 Padding；16 = 底部留一点呼吸
            var h = 32 + 4 + contentHeight + 20 + 16;
            h = Math.Clamp(h, 360, Math.Max(360, maxH));

            ClientSize = new Size(Math.Min(ClientSize.Width, DefaultWidthDip), h);
            CenterOnScreen();

            AppLog.Step("hash-check", $"窗口按内容定高：内容 {contentHeight:0} → 窗口 {h:0}");
        }
        catch (Exception ex)
        {
            AppLog.Warn("hash-check", "按内容调整窗口高度失败：" + ex.Message);
        }
    }

    private void CenterOnScreen()
    {
        try
        {
            var work = Screens.Primary?.WorkingArea;
            if (work is null) return;

            var scale = Scale();
            var pw = (int)Math.Round(ClientSize.Width * scale);
            var ph = (int)Math.Round(ClientSize.Height * scale);

            Position = new PixelPoint(
                work.Value.X + Math.Max(0, (work.Value.Width - pw) / 2),
                work.Value.Y + Math.Max(0, (work.Value.Height - ph) / 2));
        }
        catch (Exception ex)
        {
            AppLog.Warn("hash-check", "哈希校验窗定位失败：" + ex.Message);
        }
    }

    private void BringUp()
    {
        try
        {
            if (!IsVisible) Show();
            _corners?.Refresh();
            ThemeCompat.Apply(Root);
            BackdropTheme.Apply(this, Root, _backdropPrefer);

            var hwnd = Backdrop.TryGetHwnd(this);
            try { NativeMethods.SetForegroundWindow(hwnd); } catch { }
            Activate();
        }
        catch (Exception ex)
        {
            AppLog.Exception("hash-check", ex, "激活哈希校验窗口");
        }
    }

    // ── 选文件 ───────────────────────────────────────────────────────────

    /// <summary>整条都能点：少一层「我到底该点哪个按钮」的犹豫。</summary>
    private async void DropZone_Tapped(object? sender, TappedEventArgs e) => await ChooseFileAsync();

    private async Task ChooseFileAsync()
    {
        try
        {
            // ⚠️ Win7 走 FilePickerCompat 兜底（Avalonia 选择器在「库」里选文件会静默失败）。
            var r = await FilePickerCompat.PickFilesAsync(this, "选择文件", allowMultiple: false, null);
            if (r.Error is not null)
            {
                DropSub.Text = "选择文件失败：" + r.Error;
                return;
            }
            if (r.Files.Count == 0) return;

            await LoadFileAsync(r.Files[0]);
        }
        catch (Exception ex)
        {
            AppLog.Warn("hash-check", "选择文件失败：" + ex.Message);
            DropSub.Text = "选择文件失败：" + ex.Message;
        }
    }

    private void DropZone_DragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = DragDropEffects.Copy;
        DropTitle.Text = "松开鼠标即开始计算";
    }

    private void DropZone_DragLeave(object? sender, DragEventArgs e) => DropTitle.Text = IdleTitle;

    private async void DropZone_Drop(object? sender, DragEventArgs e)
    {
        try
        {
            DropTitle.Text = IdleTitle;

            // 原版经 DataView.GetStorageItemsAsync() 取文件 → Avalonia 直接给文件名列表
            var path = e.Data?.GetFileNames() is { } names ? System.Linq.Enumerable.FirstOrDefault(names) : null;
            if (path is not null) await LoadFileAsync(path);
        }
        catch (Exception ex)
        {
            AppLog.Warn("hash-check", "拖入文件失败：" + ex.Message);
            DropSub.Text = "拖入的文件读取失败：" + ex.Message;
        }
    }

    private async Task LoadFileAsync(string path)
    {
        _hashes = new();
        SetRowsEmpty("计算中…");
        HashProgress.IsVisible = true;
        MatchText.IsVisible = false;

        _cts?.Cancel();
        var cts = new CancellationTokenSource();
        _cts = cts;

        try
        {
            var info = new FileInfo(path);
            DropTitle.Text = IdleTitle;
            DropSub.Text = "已选文件，可再拖入新文件重新计算";
            FileInfo.IsVisible = true;
            FileInfo.Text = $"{info.Name}（{SizeText(info.Length)}）· {info.FullName}";

            var hashes = await Task.Run(() => FileHash.Compute(path, cts.Token), cts.Token);
            if (cts.IsCancellationRequested) return;

            _hashes = hashes;
            foreach (var line in _lines)
                line.Value = hashes.TryGetValue(line.Name, out var v) && v.Length > 0 ? v : "—";
        }
        catch (OperationCanceledException)
        {
            // 换文件了，这一轮作废
        }
        catch (Exception ex)
        {
            AppLog.Warn("hash-check", "计算哈希失败：" + ex.Message);
            SetRowsEmpty("—");
            FileInfo.Text = "计算失败：" + ex.Message;
        }
        finally
        {
            if (!cts.IsCancellationRequested)
            {
                HashProgress.IsVisible = false;
                RefreshMatch();
            }
        }
    }

    // ── 比对 ─────────────────────────────────────────────────────────────

    private void Expected_TextChanged(object? sender, TextChangedEventArgs e) => RefreshMatch();

    private void RefreshMatch()
    {
        var expected = FileHash.Normalize(ExpectedBox.Text);
        if (expected.Length == 0)
        {
            MatchText.IsVisible = false;
            return;
        }

        MatchText.IsVisible = true;

        // 还没算完 / 还没选文件：说清下一步是什么，别拿红字吓人
        if (_hashes.Count == 0)
        {
            var guess = FileHash.GuessAlgorithm(ExpectedBox.Text);
            MatchText.Text = guess is null
                ? "已填入校验值，选择文件后自动比对。"
                : $"已填入 {guess} 校验值（{expected.Length} 位），选择文件后自动比对。";
            MatchText.Foreground = ThemeBrush.Get(Root, "TextFillColorSecondaryBrush");
            return;
        }

        var hit = FileHash.Match(ExpectedBox.Text, _hashes);
        if (hit is not null)
        {
            MatchText.Text = $"✓ 与 {hit} 一致，文件与发布方给出的那份完全相同。";
            MatchText.Foreground = ThemeBrush.Get(Root, "SystemFillColorSuccessBrush");
            return;
        }

        // 不一致要分两种说：格式认得出来 → 大概率是版本不对；认不出来 → 多半粘错了东西。
        var kind = FileHash.GuessAlgorithm(ExpectedBox.Text);
        MatchText.Text = kind is null
            ? "✗ 与上面四种哈希值都不一致。这段文本不像是 MD5 / SHA-1 / SHA-256 / SHA-512，请确认粘的是校验值而不是网址或版本号。"
            : $"✗ 与上面四种哈希值都不一致。填入的是 {kind} 格式（{expected.Length} 位），请确认文件来源与版本是否对应。";
        MatchText.Foreground = ThemeBrush.Get(Root, "SystemFillColorCriticalBrush");
    }

    // ── 复制 ─────────────────────────────────────────────────────────────

    private async void CopyLine_Click(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not HashLine line) return;
        if (line.Value.Length == 0 || line.Value == "—" || line.Value == "计算中…") return;

        try
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard is null) return;

            await clipboard.SetTextAsync(line.Value);
            AppLog.Step("hash-check", $"已复制 {line.Name} 值");
        }
        catch (Exception ex)
        {
            AppLog.Warn("hash-check", "复制失败：" + ex.Message);
        }
    }

    // ── 小工具 ───────────────────────────────────────────────────────────

    private void SetRowsEmpty(string placeholder)
    {
        foreach (var line in _lines) line.Value = placeholder;
    }

    private static string SizeText(long bytes)
        => bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):0.##} GB"
         : bytes >= 1L << 20 ? $"{bytes / (double)(1L << 20):0.##} MB"
         : bytes >= 1L << 10 ? $"{bytes / (double)(1L << 10):0.#} KB"
         : $"{bytes} B";

    private double Scale() => RenderScaling > 0 ? RenderScaling : 1.0;

    /// <summary>主窗口开了 Mica 才在浮窗上退回亚克力（Win7/10 没有 Mica，见 BackdropTheme 的说明）。</summary>
    private static string NormalizeBackdrop(string? want)
        => string.Equals(want, "mica", StringComparison.OrdinalIgnoreCase) ? "acrylic" : (want ?? "solid");
}

/// <summary>哈希列表的一行：算法名 + 值。值的更新走属性通知，不在界面里逐个找控件改。</summary>
public sealed class HashLine : INotifyPropertyChanged
{
    public HashLine(string name) => Name = name;

    /// <summary>算法名（MD5 / SHA-1 / SHA-256 / SHA-512），构造后不变。</summary>
    public string Name { get; }

    private string _value = "";
    public string Value
    {
        get => _value;
        set
        {
            if (_value == value) return;
            _value = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Value)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
