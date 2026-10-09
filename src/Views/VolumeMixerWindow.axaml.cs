using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ClassSoftwareHub.Desktop.Core;
using ClassSoftwareHub.Desktop.Platform;
using ClassSoftwareHub.Desktop.Services;
using ClassSoftwareHub.Desktop.Services.Audio;
using FluentAvalonia.UI.Controls;

namespace ClassSoftwareHub.Desktop.Views;

/// <summary>
/// 音量合成器浮窗（横向）：主音量浮窗上点「展开」弹出，**贴着主音量浮窗往屏幕里侧排**。
///
/// 每个应用**一行** = 软件图标 + 软件名称 + 横滑块（不管边条贴哪条边都是这个搭法 ——
/// 贴着上/下时把它改成"一列一个应用"会很怪，2026-09-26 Nick 定）。
///
/// 刷新策略：1.5s 轮询，但**只增删、绝不重建、绝不重排**：
///   · 重建（Clear + 重加）会把正在拖的滑块从可视树里摘下来 → 指针捕获当场断掉，
///     表现就是"滑一半不滑了"（2026-09-26 修）；
///   · 重排同理，所以新冒出来的应用**追加在最后**，不做排序。
/// 拖动判定的按下/松手要用 <c>handledEventsToo: true</c> 才收得到 ——
/// Slider 内部会把 PointerPressed 标成 handled，普通订阅收不到（也是这个 bug 的一半原因）。
///
/// ⚠️ 移植说明：行 Grid 的 ColumnSpacing="12" → 名称/滑块 Margin 模拟（Avalonia Grid 没有列间距）；
///    AppWindow（MoveAndResize/Size/Position）→ FlyoutChrome 封装；WorkArea → Screens.Primary.WorkingArea。
/// </summary>
public sealed partial class VolumeMixerWindow : Window
{
    private const int RowWidthDip = 380;
    private const int RowHeightDip = 40;
    private const int RowGapDip = 6;
    private const int MaxVisible = 6;

    // 高度 = 上边距 + 标题行 + 标题与列表的间隔 + 各行 + 行间距 + 下边距
    private const int PadTopDip = 14;
    private const int PadBottomDip = 14;
    private const int HeaderHeightDip = 22;      // 标题行现在套了 GripRow（MinHeight 22，拖动把手）
    private const int HeaderGapDip = 10;

    private static VolumeMixerWindow? _instance;

    private readonly FlyoutChrome _chrome;
    private readonly FlyoutSlider _slide = new();

    private DispatcherTimer? _poll;
    private bool _visible;
    private bool _started;
    private bool _syncing;
    private bool _anyDragging;
    private int _visibleCount;

    private readonly Dictionary<uint, SessionRow> _rows = new();

    private VolumeMixerWindow()
    {
        InitializeComponent();
        _chrome = new FlyoutChrome(this, Root, Panel, "音量合成器");
        Configure();
    }

    private sealed class SessionRow
    {
        public uint Pid;
        public AudioSessionItem Item = null!;
        public Grid Root = null!;
        public Slider Slider = null!;
        public Image Icon = null!;
        public FontIcon IconFallback = null!;
        public TextBlock Name = null!;
        public bool Dragging;
    }

    public static void Toggle()
    {
        try
        {
            _instance ??= new VolumeMixerWindow();
            if (_instance._visible) _instance.HideSelf();
            else _instance.ShowSelf();
        }
        catch (Exception ex)
        {
            ScreenCapture.Log("音量合成器开关失败: " + ex.Message);
        }
    }

    public static void CloseIfOpen()
    {
        if (_instance?._visible == true) _instance.HideSelf();
    }

    /// <summary>合成器现在开着吗（主音量浮窗用它点亮「展开」键）。</summary>
    public static new bool IsVisible => _instance?._visible == true;

    /// <summary>合成器浮窗当前在屏幕上的矩形。</summary>
    public static PixelRect? CurrentRect => _instance?._chrome.CurrentRect;

    /// <summary>合成器浮窗正被用户拖着走（鼠标监视据此绝不中途收窗）。</summary>
    public static bool IsDragging => _instance?._chrome.IsDragging == true;

    private void Configure()
    {
        try
        {
            // ⚠️ mainScope: false（2026-10-08 口径变更，与主音量浮窗同步）：
            //    合成器浮窗改跟「外部组件」作用域 —— 分体开着跟外部外观，没开跟主界面。
            ThemeCompat.Apply(Root);
            VolumeFlyoutGroup.MixerHwnd = _chrome.Hwnd;

            Deactivated += (_, _) => VolumeFlyoutGroup.EnsureMouseWatch();   // 收窗判据是鼠标不是焦点，见 VolumeFlyoutGroup

            // ── 拖动：**已全部撤掉**（2026-10-03，用户第 16 轮「悬浮窗还是无法点击展开，
            //    去掉拖动调整位置吧」）—— 与主音量浮窗同一口径，理由见 VolumeWindow.Configure。

            Root.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Escape) VolumeFlyoutGroup.CloseAll();
            };
        }
        catch (Exception ex)
        {
            ScreenCapture.Log("音量合成器初始化失败: " + ex.Message);
        }
    }

    // ── 尺寸 ──────────────────────────────────────────────────────────────

    private PixelPoint SizeDip(int count)
    {
        var n = Math.Clamp(count, 1, MaxVisible);
        var h = PadTopDip + HeaderHeightDip + HeaderGapDip
                + n * RowHeightDip + Math.Max(0, n - 1) * RowGapDip + PadBottomDip;
        return new PixelPoint(RowWidthDip, h);
    }

    // ── 显示 / 收起 ───────────────────────────────────────────────────────

    private PixelRect WorkArea()
    {
        try { return Screens.Primary.WorkingArea; }
        catch { return new PixelRect(0, 0, 1920, 1040); }
    }

    private void ShowSelf()
    {
        try
        {
            var scale = _chrome.Scale;
            _visibleCount = Math.Clamp(CurrentSessionCount(), 1, MaxVisible);
            var dip = SizeDip(_visibleCount);
            var w = (int)Math.Round(dip.X * scale);
            var h = (int)Math.Round(dip.Y * scale);
            var work = WorkArea();

            // 锚点 = 主音量浮窗的**最终落点**（不是半路上的实时位置 —— 主音量还在滑入时
            //    点「展开」，锚实时位置会让主音量随后滑进来正好压住合成器，2026-10-01 修）。
            //    沿边方向与主音量**顶对齐**（左右边）/左对齐（上下边）：两个高矮不一的窗排成一列整齐的块。
            var anchor = VolumeWindow.AnchorRect ?? EdgeGeometry.EdgeBar(VolumeWindow.Edge, work);
            var (start, final) = EdgeGeometry.BesideAnchor(VolumeWindow.Edge, anchor, work, w, h, scale, alignStart: true);

            Start();                                    // 先把条目建好再显形
            _chrome.Present(start, w, h);
            _visible = true;

            _chrome.Finish(Root.ActualThemeVariant == ThemeVariant.Dark);

            FlyoutFade.In(ContentHost, 170);
            _slide.Run(_chrome.Hwnd, start, final, FlyoutSlider.SlideInMs);

            VolumeFlyoutGroup.HoldSidebar();
            VolumeFlyoutGroup.EnsureMouseWatch();
        }
        catch (Exception ex)
        {
            ScreenCapture.Log("音量合成器显示失败: " + ex.Message);
        }
    }

    private void HideSelf()
    {
        if (!_visible) return;
        _visible = false;
        Stop();

        try
        {
            var size = _chrome.SizePx;
            var from = _chrome.Position;
            var to = EdgeGeometry.FullyOut(from, VolumeWindow.Edge,
                                           EdgeGeometry.Thickness(VolumeWindow.Edge, size.Width, size.Height));

            _slide.Run(_chrome.Hwnd, from, to, FlyoutSlider.SlideOutMs, () =>
            {
                _chrome.HideDirect();
                VolumeFlyoutGroup.OnAnyHidden();
            }, easeIn: true);
        }
        catch
        {
            _chrome.HideDirect();
            VolumeFlyoutGroup.OnAnyHidden();
        }
    }

    /// <summary>行数变了就把窗口长/缩到对应高度（滑动动画中间不动位置）。</summary>
    private void ResizeForCount(int count)
    {
        var n = Math.Clamp(count, 1, MaxVisible);
        if (n == _visibleCount) return;
        _visibleCount = n;
        if (_slide.IsRunning) return;

        try
        {
            var scale = _chrome.Scale;
            var dip = SizeDip(n);
            var w = (int)Math.Round(dip.X * scale);
            var h = (int)Math.Round(dip.Y * scale);
            var work = WorkArea();

            var anchor = VolumeWindow.AnchorRect ?? EdgeGeometry.EdgeBar(VolumeWindow.Edge, work);
            var (_, final) = EdgeGeometry.BesideAnchor(VolumeWindow.Edge, anchor, work, w, h, scale, alignStart: true);

            _chrome.MoveResize(final, w, h);
        }
        catch { }
    }

    // ── 轮询 / 刷新 ────────────────────────────────────────────────────────

    private void Start()
    {
        _started = true;
        Refresh();

        _poll ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
        _poll.Tick -= OnPoll;
        _poll.Tick += OnPoll;
        _poll.Start();
    }

    private void Stop()
    {
        _started = false;
        _poll?.Stop();
    }

    private void OnPoll(object? sender, EventArgs e) => Refresh();

    private int CurrentSessionCount() => AudioService.GetActiveSessions().Count;

    /// <summary>
    /// ⚠️ 只增删、不重建、不重排 —— 见类注释（重建会把正在拖的滑块摘出可视树，拖动当场断）。
    /// </summary>
    private void Refresh()
    {
        if (!_started || _anyDragging) return;

        var sessions = AudioService.GetActiveSessions();

        // 已经不发声的进程：把它的行摘掉（只摘这一个，别的行一根手指都不碰）
        var alive = new HashSet<uint>(sessions.Select(s => s.ProcessId));
        foreach (var pid in _rows.Keys.ToList())
        {
            if (alive.Contains(pid)) continue;
            SessionStack.Children.Remove(_rows[pid].Root);
            _rows.Remove(pid);
        }

        // 新出现的进程：建行，**追加在最后**（不插队、不排序，免得把已有的行摘下来重排）
        foreach (var s in sessions)
        {
            if (_rows.TryGetValue(s.ProcessId, out var row))
            {
                row.Item = s;
                if (row.Name.Text != s.Name)
                {
                    row.Name.Text = s.Name;
                    ToolTip.SetTip(row.Name, s.Name);
                }
                if (!row.Dragging) SetSliderSilently(row.Slider, s.VolumePercent);
                UpdateIcon(row, s);
                continue;
            }

            var fresh = BuildSessionRow(s);
            _rows[s.ProcessId] = fresh;
            SessionStack.Children.Add(fresh.Root);
        }

        var count = sessions.Count;
        SessionCountText.Text = count > 0 ? $"（{count}）" : "";
        SessionEmptyText.IsVisible = count == 0;
        SessionScroll.IsVisible = count != 0;

        ResizeForCount(count);
    }

    private void SetSliderSilently(Slider slider, int value)
    {
        if (Math.Abs(slider.Value - value) < 0.5) return;
        _syncing = true;
        try { slider.Value = value; }
        finally { _syncing = false; }
    }

    private void UpdateIcon(SessionRow row, AudioSessionItem s)
    {
        var img = AppIconService.Get(s.ExePath);
        if (img is null)
        {
            if (row.Icon.IsVisible) row.Icon.Source = null;
            row.Icon.IsVisible = false;
            row.IconFallback.IsVisible = true;
        }
        else
        {
            row.IconFallback.IsVisible = false;
            if (!ReferenceEquals(row.Icon.Source, img)) row.Icon.Source = img;
            row.Icon.IsVisible = true;
        }
    }

    /// <summary>
    /// 建一行：图标（24）｜名称（定宽 118，超了省略号）｜滑块（吃掉剩下的宽度）。
    /// 名称定宽是为了**行行对齐**；滑块要拖，所以按下/松手判定挂 handledEventsToo。
    /// ⚠️ 原版 Grid 的 ColumnSpacing="12" 用「名称/滑块左 Margin 12」模拟。
    /// </summary>
    private SessionRow BuildSessionRow(AudioSessionItem session)
    {
        var root = new Grid { Height = RowHeightDip };
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(118) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var icon = new Image
        {
            Width = 22, Height = 22,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            IsVisible = false,
        };
        var fallback = new FontIcon
        {
            Glyph = "\uE767",
            FontSize = 16,
            Opacity = 0.75,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            IsVisible = true,
        };
        var name = new TextBlock
        {
            Text = session.Name,
            FontSize = 13,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap,
        };
        ToolTip.SetTip(name, session.Name);

        var slider = new Slider
        {
            Minimum = 0,
            Maximum = 100,
            Value = session.VolumePercent,
            SmallChange = 1,
            LargeChange = 10,
            Orientation = Orientation.Horizontal,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
            MinWidth = 120,
            Margin = new Thickness(12, 0, 0, 0),
            Tag = session,
        };

        var row = new SessionRow
        {
            Pid = session.ProcessId,
            Item = session,
            Root = root,
            Slider = slider,
            Icon = icon,
            IconFallback = fallback,
            Name = name,
        };

        slider.ValueChanged += (_, e) =>
        {
            if (_syncing) return;
            AudioService.SetSessionPercent(row.Item, (int)Math.Round(e.NewValue));
        };

        // ⚠️ handledEventsToo: true —— Slider 内部会把 PointerPressed 标成 handled，
        //    普通 += 订阅收不到，"_anyDragging" 就永远是 false，轮询照样来拆行（滑一半就断）。
        //（WinUI 的 UIElement 路由事件 → Avalonia 挂在 InputElement 上，与 SidebarLayoutPage 同一套写法）
        slider.AddHandler(InputElement.PointerPressedEvent,
            new EventHandler<PointerPressedEventArgs>((_, _) => { row.Dragging = true; _anyDragging = true; }),
            RoutingStrategies.Bubble, true);
        slider.AddHandler(InputElement.PointerReleasedEvent,
            new EventHandler<PointerReleasedEventArgs>((_, _) => EndDrag(row)),
            RoutingStrategies.Bubble, true);
        slider.AddHandler(InputElement.PointerCaptureLostEvent,
            new EventHandler<PointerCaptureLostEventArgs>((_, _) => EndDrag(row)),
            RoutingStrategies.Bubble, true);

        Grid.SetColumn(icon, 0);
        Grid.SetColumn(fallback, 0);
        Grid.SetColumn(name, 1);
        Grid.SetColumn(slider, 2);
        root.Children.Add(icon);
        root.Children.Add(fallback);
        root.Children.Add(name);
        root.Children.Add(slider);
        return row;
    }

    private void EndDrag(SessionRow row)
    {
        if (!row.Dragging && !_anyDragging) return;
        row.Dragging = false;
        _anyDragging = false;
        Refresh();
    }
}
