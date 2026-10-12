using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ClassSoftwareHub.Desktop.Controls;
using ClassSoftwareHub.Desktop.Core;
using ClassSoftwareHub.Desktop.Platform;
using ClassSoftwareHub.Desktop.Services;

namespace ClassSoftwareHub.Desktop.Views;

/// <summary>
/// 日志「详细视图」—— 独立窗口，**给开发者定位问题用**。
///
/// 与主界面日志页（<c>LogViewerPage</c>）的分工：
///   · 主页面 = 概览：够用的上下文 + 级别/关键字筛选 + 一行统计；
///   · 本窗口 = 逐行啃：**序号**、**毫秒时间**、**更大读取上限**（8 MB / 40000 行）、
///     右侧常驻**原文面板**（文件里那一行的原始内容，含缩进堆栈）、**全量统计条**。
///
/// ⛔ 这里刻意不追求观感，但要保住**排版正确**：
///    列宽严格对齐、内容可选中可复制、能滚、深浅色都读得清。
///
/// = 标准窗口：拖动 / 阴影 / 圆角交给系统，我们只管内容；**故意留在任务栏和 Alt+Tab 里**
///   （开发者要一边看它一边操作应用复现问题）。
///
/// ⚠️ 关掉 = **真的销毁**（不像工具浮窗那种"收起来"）：排障窗没有"留着复用"的价值，
///    真关掉最省事，也不会在退出链路上留下没关掉的窗口把进程吊住。
///
/// ⚠️ 移植说明（WinUI → Avalonia/FluentAvalonia）：
///   · 原版「自绘标题栏」（<c>ExtendsContentIntoTitleBar</c> + <c>SetTitleBar</c> + AppWindow 的
///     <c>ResizeClient</c> / <c>Move</c> / <c>TitleBar</c> 系统按钮配色 / <c>GetDpiForWindow</c>）
///     按移植契约改为**保留系统标题栏**；原 AppTitleBar 那行降级成内容标识条。
///     因此 <c>UpdateCaptionButtonColors</c> 没有对应物（系统按钮外观归系统管），不再保留。
///   · 尺寸/居中：原版按物理像素手算；Avalonia 一律 DIP，用 Width/Height +
///     WindowStartupLocation="CenterScreen"，并在这里按屏幕工作区夹一遍（教室机还有 1366×768）。
///   · <c>DispatcherQueueTimer</c> → <see cref="DispatcherTimer"/>。
///   · <c>Clipboard.SetContent(DataPackage)</c> + <c>Flush()</c> → <c>TopLevel.Clipboard.SetTextAsync</c>
///     （Avalonia 的 IClipboard 只认文本，没有 Flush 这一步）。
///   · <c>VisualTreeHelper</c> 递归 → <c>GetVisualDescendants()</c>；
///     <c>ViewChanged + IsIntermediate + ScrollableHeight</c> → <c>ScrollChanged + Extent/Viewport/Offset</c>。
///   · <c>LogReader.Look</c> / <c>BrushOf(lv)</c>（无元素参数）→ <c>ThemeBrush.Get(this, …)</c> /
///     <c>BrushOf(lv, this)</c>，按窗口那棵树的主题取色。
///   · <c>ContainerContentChanging</c> → 行模板绑 <see cref="LogLevelBrushConverter"/>。
/// </summary>
public sealed partial class LogDetailWindow : Window
{
    private const int DefaultWidthDip = 1280;
    private const int DefaultHeightDip = 800;
    private const int FollowMs = 2000;
    private const double NearBottomPx = 40;

    private static LogDetailWindow? _instance;

    private readonly LevelChipBar _chips;

    private DispatcherTimer? _followTimer;
    private ScrollViewer? _listScroll;

    private bool _suppress;

    /// <summary>是否停在末尾（跟随的闸门）。初始 false = 打开时**从第 1 行看起**。</summary>
    private bool _atBottom;

    private List<LogEntry> _all = new();

    /// <summary>绑给表格的筛选结果。可观察集合 + **增量追加**：跟随时只加新行，不整体重建
    /// （重建会清空再填满，每 2 秒一次就是闪烁，跟主页面同一套规矩）。</summary>
    private readonly System.Collections.ObjectModel.ObservableCollection<LogEntry> _view = new();

    /// <summary>已读进来的文件长度。⛔ 必须自己记：<c>LogFile.Size</c> 是扫描时的快照、
    /// 永不更新，拿它当基准 ⇒ 条件恒为真 ⇒ 每 2 秒重读一次 ⇒ 画面一直抖。</summary>
    private long _loadedSize = -1;

    /// <summary>「Show 时指定要看哪个会话 / 文件」，用完即清（用户随后自己改不该被覆盖）。</summary>
    private string? _wantSession;
    private string? _wantFile;

    private LogDetailWindow()
    {
        InitializeComponent();
        Configure();

        LogList.ItemsSource = _view;

        _chips = new LevelChipBar(ChipHost);
        _chips.SelectionChanged += ApplyFilter;

        Root.Loaded += (_, _) =>
        {
            // Show() 里已经先刷过一次（那样窗口一出来就有内容）；这里只在还没数据时兜底
            if (SessionBox.ItemsSource is null) ReloadSessions();
            StartFollow();
        };
    }

    // ── 对外 ─────────────────────────────────────────────────────────────

    /// <summary>摊开这扇窗。已经开着就切到指定的会话 / 文件，再把它调到前面。</summary>
    public static void Show(string? sessionName, string? fileName)
    {
        try
        {
            _instance ??= new LogDetailWindow();
            _instance._wantSession = sessionName;
            _instance._wantFile = fileName;
            _instance.ReloadSessions();
            _instance.BringUp();
        }
        catch (Exception ex)
        {
            AppLog.Warning("logs", "打开日志详细视图失败：" + ex.Message);
        }
    }

    /// <summary>
    /// 退出应用时真正销毁（见 <c>MainWindow</c> 的 FinishExit）。
    /// ⛔ 没关掉的窗口会把消息循环撑住 ⇒ 进程残留。
    /// </summary>
    public static void CloseForExit()
    {
        var w = _instance;
        _instance = null;
        try { w?.Close(); } catch { }
        AppLog.Info("exit", "日志详细视图实例已请求 Close");
    }

    // ── 窗口本身 ─────────────────────────────────────────────────────────

    private void Configure()
    {
        try
        {
            // 原版：框架留着（拖动/阴影/圆角靠系统），标题栏内容自己画，可拉伸 / 可最大化 / 可最小化。
            // Avalonia 版保留系统标题栏（标题由 XAML 的 Title 属性给），只补上"可拉伸"。
            CanResize = true;

            Backdrop.Apply(this, Root, App.Settings.Current.Backdrop);
            ThemeCompat.Apply(Root);

            ApplySize();

            // Esc 关窗。⚠️ 走 Tunnel + handledEventsToo：焦点多半在里层 ListBox / 搜索框上，
            //    普通订阅收不到 Esc。
            Root.AddHandler(InputElement.KeyDownEvent,
                new EventHandler<KeyEventArgs>((_, e) =>
                {
                    if (e.Key == Key.Escape) Close();
                }), RoutingStrategies.Tunnel, true);

            // 真关掉 ⇒ 清掉单例，下次 Show 重新建
            Closed += (_, _) => { _instance = null; StopFollow(); };
        }
        catch (Exception ex)
        {
            AppLog.Warning("logs", "日志详细视图初始化失败：" + ex.Message);
        }
    }

    /// <summary>
    /// 按逻辑像素（dip）定客户区大小，并夹进当前屏幕（小屏上别长出屏幕外）。
    /// ⚠️ 移植：原版走 AppWindow.ResizeClient + DisplayArea + GetDpiForWindow 手算物理像素；
    ///    Avalonia 一律 DIP，用 Window 的 Width/Height + RenderScaling 换回工作区尺寸。
    /// </summary>
    private void ApplySize()
    {
        try
        {
            var work = WorkArea();
            if (work.Width <= 0 || work.Height <= 0) return;

            var scale = RenderScaling <= 0 ? 1.0 : RenderScaling;
            var maxW = work.Width / scale - 40;
            var maxH = work.Height / scale - 60;

            Width = Math.Clamp(DefaultWidthDip, 760, Math.Max(760, maxW));
            Height = Math.Clamp(DefaultHeightDip, 460, Math.Max(460, maxH));
        }
        catch (Exception ex)
        {
            Debug.WriteLine("[log-detail] 尺寸设置失败: " + ex.Message);
        }
    }

    /// <summary>主屏工作区（物理像素）。</summary>
    private PixelRect WorkArea()
    {
        try
        {
            var screen = Screens.ScreenFromWindow(this)
                         ?? Screens.All.FirstOrDefault(s => s.IsPrimary)
                         ?? Screens.All.FirstOrDefault();
            if (screen is not null) return screen.WorkingArea;
        }
        catch { }
        return default;
    }

    /// <summary>
    /// 把窗口调到前面。
    /// ⚠️ 移植：原版 <c>Activate()</c> + <c>ShowWindow(SW_SHOW)</c> + <c>SetForegroundWindow</c>；
    ///    Avalonia 的 <see cref="Window.Activate"/> 自带"从最小化恢复 + 置前"，
    ///    不需要再手调 Win32（Win7 上那套 P/Invoke 反而容易抢焦点抢出问题）。
    /// </summary>
    private void BringUp()
    {
        try
        {
            if (!IsVisible) Show();
            Activate();
        }
        catch (Exception ex)
        {
            AppLog.Warning("logs", "日志详细视图激活失败：" + ex.Message);
        }
    }

    // ── 数据 ─────────────────────────────────────────────────────────────

    private sealed class LogSession
    {
        public string Name { get; init; } = "";
        public string FullPath { get; init; } = "";
        public DateTime Started { get; init; }
        public int Files { get; init; }
        public bool IsCurrent { get; init; }
        public bool IsLegacy { get; init; }

        public string Display =>
            IsCurrent ? $"本次运行 · {Started:HH:mm:ss}"
            : IsLegacy ? "升级前的历史日志"
            : $"{Started:MM-dd HH:mm:ss} · {Files} 个文件";

        public override string ToString() => Display;
    }

    private sealed class LogFile
    {
        public string Path { get; init; } = "";
        public string Name { get; init; } = "";
        public long Size { get; init; }
        public DateTime Modified { get; init; }

        public string Display => $"{Name}　{LogReader.Describe(Name).Short}";
        public override string ToString() => Display;
    }

    private static List<LogFile> ScanFiles(string dir)
    {
        var list = new List<LogFile>();
        try
        {
            if (!Directory.Exists(dir)) return list;

            foreach (var path in Directory.GetFiles(dir, "*.log"))
            {
                try
                {
                    var info = new FileInfo(path);
                    list.Add(new LogFile { Path = path, Name = info.Name, Size = info.Length, Modified = info.LastWriteTime });
                }
                catch { }
            }
        }
        catch { }

        return list
            .OrderByDescending(f => f.Name.Equals("app.log", StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(f => f.Modified)
            .ToList();
    }

    private void ReloadSessions()
    {
        var wanted = _wantSession ?? (SessionBox.SelectedItem as LogSession)?.Name;
        _wantSession = null;

        List<LogSession> items;
        try
        {
            items = LogHome.Scan()
                .Select(s => new LogSession
                {
                    Name = s.Name,
                    FullPath = s.FullPath,
                    Started = s.StartedLocal,
                    Files = s.FileCount,
                    IsCurrent = s.IsCurrent,
                    IsLegacy = s.IsLegacy,
                })
                .ToList();
        }
        catch
        {
            items = new List<LogSession>();
        }

        _suppress = true;
        SessionBox.ItemsSource = items;
        SessionBox.SelectedItem = (wanted is null ? null : items.FirstOrDefault(s => s.Name == wanted))
                                  ?? items.FirstOrDefault();
        _suppress = false;

        ReloadFiles(keepSelection: false);
    }

    private void ReloadFiles(bool keepSelection)
    {
        var wanted = _wantFile ?? (keepSelection ? (FileBox.SelectedItem as LogFile)?.Name : null);
        _wantFile = null;

        var items = SessionBox.SelectedItem is LogSession s
            ? ScanFiles(s.FullPath)
            : new List<LogFile>();

        _suppress = true;
        FileBox.ItemsSource = items;
        FileBox.SelectedItem = (wanted is null ? null : items.FirstOrDefault(f => f.Name == wanted))
                               ?? items.FirstOrDefault();
        _suppress = false;

        LoadCurrent();
    }

    private void LoadCurrent()
    {
        if (FileBox.SelectedItem is not LogFile f)
        {
            _all = new List<LogEntry>();
            _loadedSize = -1;
            ApplyFilter();
            StatusText.Text = "没有可读的日志文件。";
            ShowRaw(null);
            return;
        }

        // ⚠️ 先量长度、再读内容（理由同主页面：顺序反了会把"读的这一刻新增的行"永久漏掉）
        long size = 0;
        try { size = new FileInfo(f.Path).Length; } catch { }

        var (text, truncated) = LogReader.ReadTail(f.Path, LogReader.DetailMaxBytes, LogReader.DetailMaxLines);
        _loadedSize = size;

        _all = LogReader.Parse(text, Path.GetFileNameWithoutExtension(f.Name));
        ApplyFilter();

        // ⚠️ 必须碰一下 ListScroll：它是"用户有没有停在末尾"的唯一数据源，属性里才挂 ScrollChanged。
        _ = ListScroll;

        StatusText.Text = $"{f.Path}\n{LogReader.SizeText(size)} · " +
                          (truncated
                              ? $"已截断：只读了末尾 {LogReader.DetailMaxLines:N0} 行"
                              : "已完整载入")
                          + " · 关闭窗口即可，不会修改任何文件";
        // ⚠️ 刻意**不**在这里滚到底：打开 / 刷新时应该从第 1 行看起（看到一半开始会让人
        //    以为前面的丢了）。只有静默跟随（用户在末尾）才滚到底，见 FollowTick。
    }

    /// <summary>
    /// 统计条：把开发者真正要看的数字一次摆齐（条数 / 级别分布 / 模块数 / 堆栈数 / 时间跨度）。
    /// 主页面那行是"一句话讲清"，这里是"分组数字"，扫一眼就知道该不该细看。
    /// ⚠️ 组与组之间必须有点分隔符：十几个小字挤在一条线上，没有分隔就是一团糊。
    ///    文件大小与"是否载入完整"不放进来 —— 右下角状态行已经写了，重复占宽度。
    /// ⚠️ 用**一个 TextBlock 的 Inlines**而不是一排小 StackPanel：
    ///    这样字号不同的"数字 / 标签"自动共基线，而且能整条**换行**，窄窗口下不会被顶出可视区。
    /// </summary>
    private void BuildMetrics(LogStats stats)
    {
        MetricBar.Inlines?.Clear();

        var dim = ThemeBrush.Get(this, "TextFillColorTertiaryBrush");

        void Add(string label, string value, LogLevel? level = null, bool labelFirst = false)
        {
            if (MetricBar.Inlines is null) return;

            if (MetricBar.Inlines.Count > 0)
                MetricBar.Inlines.Add(new Run { Text = "   ·   ", FontSize = 11, Foreground = dim });

            var valueRun = new Run
            {
                Text = value,
                FontSize = 13,
                FontWeight = FontWeight.SemiBold,
                FontFamily = new FontFamily("Consolas"),
            };
            if (level is { } lv && value != "0") valueRun.Foreground = LogReader.BrushOf(lv, this);

            var labelRun = new Run { Text = label, FontSize = 10.5, Foreground = dim };
            var gap = new Run { Text = " " };

            if (labelFirst) { MetricBar.Inlines.Add(labelRun); MetricBar.Inlines.Add(gap); MetricBar.Inlines.Add(valueRun); }
            else { MetricBar.Inlines.Add(valueRun); MetricBar.Inlines.Add(gap); MetricBar.Inlines.Add(labelRun); }
        }

        Add("总行数", stats.Total.ToString("N0"));
        Add("调试", stats.Debug.ToString("N0"), LogLevel.Debug);
        Add("信息", stats.Info.ToString("N0"), LogLevel.Info);
        Add("警告", stats.Warning.ToString("N0"), LogLevel.Warning);
        Add("错误", stats.Error.ToString("N0"), LogLevel.Error);
        Add("模块", stats.ModuleCount.ToString("N0"));
        Add("含堆栈", stats.MultiLineCount.ToString("N0"));

        if (stats.FirstStamp.Length > 0 && stats.LastStamp.Length > 0)
        {
            var a = stats.FirstStamp;
            var b = stats.LastStamp;
            // 同一天就只留时间，日期重复一遍纯占宽度（跨度那一组本来就最长）
            if (a.Length >= 19 && b.Length >= 19 && a[..10] == b[..10]) { a = a[11..]; b = b[11..]; }
            Add("跨度", $"{a} → {b}", labelFirst: true);
        }
    }

    // ── 筛选 ─────────────────────────────────────────────────────────────

    private void ApplyFilter()
    {
        var list = LogReader.Filter(_all, _chips.FilterLevels, FilterBox.Text ?? "");

        // ⚠️ 整表重建只在"用户主动改筛选 / 换文件 / 手动刷新"时发生；
        //    静默跟随走 AppendNew()，只追加新行（否则画面一抖一抖）。
        _view.Clear();
        foreach (var e in list) _view.Add(e);

        RefreshStats();

        // 详情面板跟着筛选走：选中的那条被筛掉了就把面板复位
        if (LogList.SelectedItem is LogEntry sel && !_view.Contains(sel)) ShowRaw(null);
    }

    /// <summary>统计条 + 芯片计数 —— 数据变了就调一次，**不碰列表内容**。</summary>
    private void RefreshStats()
    {
        var stats = LogReader.Compute(_all, _view.Count);
        _chips.SetCounts(stats);
        BuildMetrics(stats);
    }

    private void ScrollToEnd()
    {
        if (_view.Count == 0) return;
        try
        {
            LogList.UpdateLayout();
            LogList.ScrollIntoView(_view[^1]);
        }
        catch { }
    }

    /// <summary>
    /// ListBox 模板里的 ScrollViewer，拿它判断"用户是否停在底部"。
    /// ⚠️ 移植：<c>VisualTreeHelper</c> 递归 → <c>GetVisualDescendants()</c>；
    ///    WinUI 的 <c>ViewChanged + IsIntermediate + ScrollableHeight</c> →
    ///    Avalonia 的 <c>ScrollChanged + Extent/Viewport/Offset</c>（默认不做滚动动画，无需中间态过滤）。
    /// </summary>
    private ScrollViewer? ListScroll
    {
        get
        {
            if (_listScroll is not null) return _listScroll;

            var sv = LogList.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
            if (sv is not null)
            {
                sv.ScrollChanged += (_, _) =>
                {
                    _atBottom = sv.Extent.Height - sv.Viewport.Height - sv.Offset.Y < NearBottomPx;
                };
            }

            _listScroll = sv;
            return _listScroll;
        }
    }

    // ── 原文面板 ─────────────────────────────────────────────────────────

    private void LogList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
        => ShowRaw(LogList.SelectedItem as LogEntry);

    /// <summary>把选中那条的**文件原文**摆到右侧面板里（多选时显示最上面那条，其余用复制按钮）。</summary>
    private void ShowRaw(LogEntry? entry)
    {
        if (entry is null)
        {
            RawTitle.Text = "原文";
            RawText.Text = "在上面的表格里选中一条日志，这里会显示它在文件里的原始内容。";
            RawText.Foreground = LogReader.BrushOf(LogLevel.Info, this);
            return;
        }

        RawTitle.Text = $"原文 · #{entry.Index} · {entry.LevelText}" +
                        (entry.Module.Length > 0 ? $" · {entry.Module}" : "") +
                        (entry.Stamp.Length > 0 ? $" · {entry.Stamp}" : "");
        RawText.Text = entry.Raw.Length > 0 ? entry.Raw : entry.Message;
        RawText.Foreground = LogReader.NeutralBrush(this);
    }

    // ── 动作 ─────────────────────────────────────────────────────────────

    private void SessionBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppress) return;
        ReloadFiles(keepSelection: false);
    }

    private void FileBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppress) return;
        LoadCurrent();
    }

    /// <summary>
    /// ⚠️ 移植：原版 AutoSuggestBox 只在 <c>UserInput</c> 时重筛；
    ///    Avalonia 的 TextBox.TextChanged 没有 Reason，任何文本变化都等同用户输入。
    /// </summary>
    private void FilterBox_TextChanged(object? sender, TextChangedEventArgs e) => ApplyFilter();

    private void Refresh_Click(object? sender, RoutedEventArgs e) => ReloadSessions();

    private void Copy_Click(object? sender, RoutedEventArgs e)
    {
        if (_view.Count == 0) { StatusText.Text = "当前筛选下没有内容可复制。"; return; }
        Copy(LogReader.ToText(_view), $"已复制筛选结果 {_view.Count} 行（原文）。");
    }

    private void CopySelected_Click(object? sender, RoutedEventArgs e)
    {
        var picked = LogList.SelectedItems?.OfType<LogEntry>().ToList() ?? new List<LogEntry>();
        if (picked.Count == 0) { StatusText.Text = "先在表格里选中要复制的行。"; return; }
        Copy(LogReader.ToText(picked), $"已复制选中的 {picked.Count} 行（原文）。");
    }

    private void CopyRaw_Click(object? sender, RoutedEventArgs e)
    {
        if (LogList.SelectedItem is not LogEntry entry)
        {
            StatusText.Text = "先在表格里选中一条日志。";
            return;
        }
        Copy(entry.Raw.Length > 0 ? entry.Raw : entry.Message, "已复制这条日志的原文。");
    }

    /// <summary>
    /// ⚠️ 移植：原版 <c>Clipboard.SetContent(DataPackage)</c> + <c>Flush()</c>；
    ///    Avalonia 的 <c>IClipboard</c> 只认文本（<c>SetTextAsync</c>），没有 Flush 这一步。
    /// </summary>
    private async void Copy(string text, string done)
    {
        try
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard is null) { StatusText.Text = "复制失败：拿不到剪贴板。"; return; }

            await clipboard.SetTextAsync(text);
            StatusText.Text = done;
        }
        catch (Exception ex)
        {
            StatusText.Text = "复制失败：" + ex.Message;
        }
    }

    private void LogList_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.C) return;
        if (!e.KeyModifiers.HasFlag(KeyModifiers.Control)) return;

        var picked = LogList.SelectedItems?.OfType<LogEntry>().ToList() ?? new List<LogEntry>();
        if (picked.Count == 0) return;

        Copy(LogReader.ToText(picked), $"已复制选中的 {picked.Count} 行（原文）。");
        e.Handled = true;
    }

    private void OpenFolder_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            var dir = AppLog.Root;
            Directory.CreateDirectory(dir);
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{dir}\"",
                UseShellExecute = true,
            });
        }
        catch { }
    }

    /// <summary>把全部会话打成一个 zip 放到桌面（跟主页面那个导出同一套做法）。</summary>
    private void Export_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            var sessions = LogHome.Scan();
            if (sessions.Count == 0) { StatusText.Text = "没有可导出的日志。"; return; }

            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            if (string.IsNullOrEmpty(desktop)) desktop = AppLog.Root;

            var zip = Path.Combine(desktop, $"ClassSoftwareHub-日志-{DateTime.Now:yyyyMMdd-HHmmss}.zip");
            if (File.Exists(zip)) File.Delete(zip);

            var count = 0;
            using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
            {
                foreach (var s in sessions)
                {
                    foreach (var file in ScanFiles(s.FullPath))
                    {
                        try
                        {
                            using var src = new FileStream(file.Path, FileMode.Open, FileAccess.Read,
                                                           FileShare.ReadWrite | FileShare.Delete);
                            var entry = archive.CreateEntry($"{s.Name}/{file.Name}", CompressionLevel.Optimal);
                            using var dst = entry.Open();
                            src.CopyTo(dst);
                            count++;
                        }
                        catch { }
                    }
                }
            }

            StatusText.Text = $"已导出 {sessions.Count} 次运行的 {count} 个日志文件到：{zip}";
        }
        catch (Exception ex)
        {
            StatusText.Text = "导出失败：" + ex.Message;
        }
    }

    private void Root_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) Close();
    }

    // ── 静默跟随 ─────────────────────────────────────────────────────────

    private void StartFollow()
    {
        StopFollow();

        _followTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(FollowMs) };
        _followTimer.Tick += (_, _) => FollowTick();
        _followTimer.Start();
    }

    private void StopFollow()
    {
        try { _followTimer?.Stop(); } catch { }
        _followTimer = null;
    }

    /// <summary>
    /// 只在「本次运行」且「停在末尾」时静默跟随（跟主页面同一套规矩）。
    /// ⛔⛔ 基准长度用自己记的 <see cref="_loadedSize"/>，别用 <c>LogFile.Size</c>
    /// （那是扫描时的快照、永不更新 ⇒ 条件恒真 ⇒ 每 2 秒重读一次 ⇒ 画面一直抖）；
    /// 有新增时只**追加**，不重建整表。这两条就是"一直抖却没新内容"的解药。
    /// </summary>
    private void FollowTick()
    {
        try
        {
            // ⚠️ "是否停在末尾"每轮重新算（理由同主页面：短内容不触发滚动事件、
            //    ScrollToEnd 之后布局没跟上会拿旧 offset 误判）。
            // ⛔ Viewport.Height==0 = 还没布局完 ⇒ "装得下"那条会误判为真，窗口一开就滚到末尾。
            if (ListScroll is { Viewport.Height: > 0 } sv)
            {
                _atBottom = (sv.Extent.Height > 0 && sv.Extent.Height <= sv.Viewport.Height + 1)
                            || sv.Extent.Height - sv.Viewport.Height - sv.Offset.Y < NearBottomPx;
            }

            if (_suppress || !_atBottom) return;
            if (SessionBox.SelectedItem is not LogSession { IsCurrent: true }) return;
            if (FileBox.SelectedItem is not LogFile cur) return;

            long size = 0;
            try { size = new FileInfo(cur.Path).Length; } catch { return; }

            if (size == _loadedSize) return;   // 没长 ⇒ 什么都不做

            // 变短（被清空 / 换了文件）或续接不上（读取上限截断、文件被重写）⇒ 整套重来
            if (size < _loadedSize || !AppendNew(cur, size)) LoadCurrent();

            ScrollToEnd();
        }
        catch { }
    }

    /// <summary>增量续读：只把新多出来的那几行解析出来追加进列表。
    /// 返回 false = 新内容和旧的对不上，调用方要整套重读。</summary>
    private bool AppendNew(LogFile f, long size)
    {
        var (text, truncated) = LogReader.ReadTail(f.Path, LogReader.DetailMaxBytes, LogReader.DetailMaxLines);
        if (truncated) return false;

        // 序号接着老条目往下编（前缀那段的序号是重编的，比对只看 Raw）
        var fresh = LogReader.Parse(text, Path.GetFileNameWithoutExtension(f.Name), _all.Count + 1);
        if (!LogReader.IsExtensionOf(_all, fresh)) return false;

        var levels = _chips.FilterLevels;
        var kw = FilterBox.Text ?? "";

        for (var i = _all.Count; i < fresh.Count; i++)
        {
            var e = fresh[i];
            _all.Add(e);
            // 新增行单独过一遍筛选 —— 与整表用的是同一个 Filter，口径不会漂
            if (LogReader.Filter(new[] { e }, levels, kw).Count > 0) _view.Add(e);
        }

        _loadedSize = size;
        RefreshStats();
        return true;
    }
}
