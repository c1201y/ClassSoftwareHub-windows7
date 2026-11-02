using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using ClassSoftwareHub.Desktop.Core;
using ClassSoftwareHub.Desktop.Platform;
using ClassSoftwareHub.Desktop.Services;
using FluentAvalonia.UI.Controls;

namespace ClassSoftwareHub.Desktop.Views;

/// <summary>
/// 托盘图标的右键菜单（自绘）。
///
/// <para>
/// 替代 <c>TrayIcon.ShowMenu</c> 里的原生 <c>TrackPopupMenu</c>：原生菜单与全站 Fluent 卡片
/// 风格不一致。弹出的为圆角卡片：卡片底 + 1px 描边 + 34dip 行高 + 圆角悬停淡底，首项文字加粗，
/// 行为与原生菜单对齐（点外面 / Esc 收起，上下键移动，回车执行）。
/// </para>
///
/// <para>
/// ⚠️ 这个窗口是**临时窗**：每次右键都新建一个，选完 / 收起就销毁 —— 不做单例缓存，
/// 免得"上次的内容还在"和主题切换残留。
/// </para>
///
/// <para>
/// ⚠️ Win7 能不能弹：菜单显示后立刻用 <c>SetForegroundWindow</c>（必要时 <c>AttachThreadInput</c> 搭桥，
/// 跟 <see cref="FlyoutChrome"/> 同一套）抢前台。**抢不到就返回 false**，调用方退回原生菜单 ——
/// 我们的收窗判据（失焦 / 前台变了）建立在"自己确实是前台"之上，抢不到还硬开着会变成
/// 一块点哪都不消失的置顶窗。
/// </para>
/// </summary>
public sealed partial class TrayMenuWindow : Window
{
    // 尺寸全部是 dip，显示前按显示器缩放换算成物理像素摆位置
    private const double CardWidthDip = 232;
    private const double ItemHeightDip = 34;
    private const double SeparatorBlockDip = 9;     // 1px 线 + 上下各 4
    private const double PanelPaddingDip = 4;
    private const double BorderDip = 1;

    /// <summary>离工作区边缘留的空隙（物理像素），别让卡片贴着屏幕边像被切掉。</summary>
    private const int EdgeGapPx = 8;

    private static TrayMenuWindow? _open;

    private readonly List<Button> _itemButtons = new();
    private Action<TrayMenuItem>? _picked;
    private RoundedCorners? _corners;
    private DispatcherTimer? _foregroundWatch;
    private PixelPoint _anchor;
    private long _openedAt;
    private double _contentHeightDip;
    private int _selected = -1;
    private bool _closing;

    public TrayMenuWindow()
    {
        InitializeComponent();

        Title = "托盘菜单";
        ThemeCompat.Apply(Root);       // 跟主界面「颜色模式」走，不是跟外部组件
        Root.ActualThemeVariantChanged += (_, _) => ApplyChrome();

        _foregroundWatch = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _foregroundWatch.Tick += (_, _) => CheckForeground();

        Deactivated += OnDeactivated;
        Closed += OnClosed;
        KeyDown += OnKeyDown;
    }

    /// <summary>
    /// 在鼠标位置弹出菜单。**返回 false 表示没能弹出来**（或抢不到前台），调用方应该退回原生菜单。
    /// 必须在 UI 线程调用。
    /// </summary>
    public static bool TryShowAt(IReadOnlyList<TrayMenuItem> items, Action<TrayMenuItem> picked)
    {
        try
        {
            _open?.Dismiss();

            var w = new TrayMenuWindow();
            if (!w.Build(items, picked)) return false;

            _open = w;
            if (!w.Present())
            {
                _open = null;
                w.Close();
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Fail("tray", ex, "自绘托盘菜单弹出失败");
            try { _open?.Close(); } catch { }
            _open = null;
            return false;
        }
    }

    // ══════════════════════════════════════════════════════════════════
    //  构建
    // ══════════════════════════════════════════════════════════════════

    private bool Build(IReadOnlyList<TrayMenuItem> items, Action<TrayMenuItem> picked)
    {
        _picked = picked;
        var theme = Resources["TrayMenuItemTheme"] as ControlTheme;

        double content = 0;
        foreach (var item in items)
        {
            if (item.Separator)
            {
                var sep = new Border();
                sep.Classes.Add("traySeparator");
                sep.PointerEntered += (_, _) => Select(-1);   // 挪到分隔线上 → 不高亮任何一项
                ItemsHost.Children.Add(sep);
                content += SeparatorBlockDip;
                continue;
            }

            var btn = new Button
            {
                Content = BuildRow(item),
                Tag = item,
                Focusable = false,          // 焦点留在窗口上（菜单不是表单），键盘选中由代码统一切 class
            };
            if (theme is not null) btn.Theme = theme;
            AutomationProperties.SetName(btn, item.Text);

            var index = _itemButtons.Count;
            btn.PointerEntered += (_, _) => Select(index);
            btn.Click += (_, e) =>
            {
                e.Handled = true;
                Pick(item);
            };

            ItemsHost.Children.Add(btn);
            _itemButtons.Add(btn);
            content += ItemHeightDip;
        }

        _contentHeightDip = content;
        return _itemButtons.Count > 0;
    }

    private static Control BuildRow(TrayMenuItem item)
    {
        // 固定 18dip 图标列，没有图标的项文字也跟有图标的对齐
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("18,*") };

        if (!string.IsNullOrEmpty(item.Glyph))
        {
            row.Children.Add(new FontIcon
            {
                Glyph = item.Glyph,
                FontSize = 14,
                Opacity = 0.9,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            });
        }

        var text = new TextBlock
        {
            Text = item.Text,
            FontSize = 13,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            FontWeight = item.IsDefault ? FontWeight.SemiBold : FontWeight.Normal,
        };
        Grid.SetColumn(text, 1);
        row.Children.Add(text);

        return row;
    }

    // ══════════════════════════════════════════════════════════════════
    //  显示 / 定位
    // ══════════════════════════════════════════════════════════════════

    private bool Present()
    {
        NativeMethods.GetCursorPos(out var cursor);
        _anchor = new PixelPoint(cursor.X, cursor.Y);

        var totalDip = _contentHeightDip + PanelPaddingDip * 2 + BorderDip * 2;
        Width = CardWidthDip;
        Height = totalDip;

        var screen = ScreenAt(_anchor);
        var scale = screen?.Scaling ?? (RenderScaling > 0 ? RenderScaling : 1.0);
        if (scale <= 0) scale = 1.0;

        int w = (int)Math.Round(CardWidthDip * scale);
        int h = (int)Math.Round(totalDip * scale);

        Position = ComputePosition(_anchor, w, h, screen);

        Show();
        ApplyChrome();

        // 窗口实际像素尺寸可能与按屏幕缩放预算的不一致（如便携版启动器拉起时，
        // 窗口按 100% 渲染而 Screens 仍报 200%，菜单会偏离鼠标）→ 用真实尺寸再摆一次。
        var hwnd = Backdrop.TryGetHwnd(this);
        if (hwnd != IntPtr.Zero && NativeMethods.GetWindowRect(hwnd, out var actual) &&
            (actual.Width != w || actual.Height != h))
        {
            Position = ComputePosition(_anchor, actual.Width, actual.Height, screen);
        }

        if (!EnsureForeground())
        {
            AppLog.Warn("tray", "自绘菜单没能抢到前台 → 退回原生菜单");
            return false;
        }

        Root.Focus();
        _openedAt = Environment.TickCount64;
        StartForegroundWatch();

        if (hwnd != IntPtr.Zero && NativeMethods.GetWindowRect(hwnd, out var r))
            AppLog.Info("tray", $"自绘菜单已弹出：{_itemButtons.Count} 项，{r.Width}×{r.Height}px @ ({r.Left},{r.Top})，缩放 {scale:0.##}");
        else
            AppLog.Info("tray", $"自绘菜单已弹出：{_itemButtons.Count} 项");

        return true;
    }

    /// <summary>贴着鼠标摆卡片：默认右下角贴光标、往左上翻；放不下就翻到另一侧，最后夹进工作区。</summary>
    private static PixelPoint ComputePosition(PixelPoint cursor, int w, int h, Screen? screen)
    {
        int x = cursor.X - w;
        int y = cursor.Y - h - 2;        // 托盘在屏幕底边，往上让 2px 别压住任务栏图标
        if (screen is { } sc)
        {
            var work = sc.WorkingArea;
            if (x < work.X + EdgeGapPx) x = cursor.X + 2;                       // 左边放不下 → 向右开
            if (y < work.Y + EdgeGapPx) y = cursor.Y + 2;                        // 上方放不下 → 向下开
            x = Math.Clamp(x, work.X + EdgeGapPx, Math.Max(work.X + EdgeGapPx, work.Right - w - EdgeGapPx));
            y = Math.Clamp(y, work.Y + EdgeGapPx, Math.Max(work.Y + EdgeGapPx, work.Bottom - h - EdgeGapPx));
        }
        return new PixelPoint(x, y);
    }

    private Screen? ScreenAt(PixelPoint p)
    {
        try { return Screens.ScreenFromPoint(p); }
        catch { return null; }
    }

    /// <summary>
    /// 把前台抢过来（跟 FlyoutChrome 同一套：裸 SetForegroundWindow 会被前台锁静默拒绝，
    /// 跟前台线程 AttachThreadInput 搭桥后再设一次）。返回是否真的成了前台。
    /// </summary>
    private bool EnsureForeground()
    {
        try
        {
            var hwnd = Backdrop.TryGetHwnd(this);
            if (hwnd == IntPtr.Zero) return false;

            if (NativeMethods.GetForegroundWindow() == hwnd) return true;

            _ = NativeMethods.SetForegroundWindow(hwnd);
            if (NativeMethods.GetForegroundWindow() == hwnd) return true;

            var fg = NativeMethods.GetForegroundWindow();
            var fgThread = fg != IntPtr.Zero ? NativeMethods.GetWindowThreadProcessId(fg, out _) : 0;
            var myThread = NativeMethods.GetCurrentThreadId();
            if (fgThread != 0 && fgThread != myThread)
            {
                _ = NativeMethods.AttachThreadInput(myThread, fgThread, true);
                _ = NativeMethods.SetForegroundWindow(hwnd);
                _ = NativeMethods.AttachThreadInput(myThread, fgThread, false);
            }

            try { Activate(); } catch { }
            return NativeMethods.GetForegroundWindow() == hwnd;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>显示之后才能调（HWND 这时才存在）：去掉系统框架 + 按真实尺寸裁圆角 + 刷深浅色。</summary>
    private void ApplyChrome()
    {
        ApplyThemeColors();

        var hwnd = Backdrop.TryGetHwnd(this);
        if (hwnd == IntPtr.Zero) return;

        var dark = Root.ActualThemeVariant == ThemeVariant.Dark;
        try { WindowChrome.RemoveBorder(hwnd, rounded: true, dark: dark); } catch { }

        // 无边框浮窗在 Win11 上 DWM 也不给圆角，只能区域裁剪 → force
        _corners ??= RoundedCorners.Attach(this, RoundedCorners.DefaultCornerRadiusDip, force: true);
        _corners.Refresh();
    }

    /// <summary>
    /// 卡片配色。不用半透明的 CardBackgroundFillColorDefaultBrush：Win7 上没有逐像素透明，
    /// 半透明底会透出窗口自己的底色而发灰。直接给成品色，口径跟 FlyoutChrome 一致。
    /// </summary>
    private void ApplyThemeColors()
    {
        var dark = Root.ActualThemeVariant == ThemeVariant.Dark;
        var bg = new SolidColorBrush(dark ? Color.FromRgb(0x20, 0x20, 0x20) : Color.FromRgb(0xFC, 0xFC, 0xFC));
        var border = new SolidColorBrush(dark ? Color.FromRgb(0x3A, 0x3A, 0x3A) : Color.FromRgb(0xE1, 0xE1, 0xE1));
        var sep = new SolidColorBrush(dark ? Color.FromRgb(0x33, 0x33, 0x33) : Color.FromRgb(0xE8, 0xE8, 0xE8));

        Background = bg;          // 显示前先定成品色，防"白闪"（Win7 关 Aero 时尤其明显）
        Panel.Background = bg;
        Panel.BorderBrush = border;

        foreach (var child in ItemsHost.Children)
        {
            if (child is Border b && b.Classes.Contains("traySeparator"))
                b.Background = sep;
        }
    }

    // ══════════════════════════════════════════════════════════════════
    //  收起
    // ══════════════════════════════════════════════════════════════════

    private void OnDeactivated(object? sender, EventArgs e)
    {
        // 假失焦：前台其实还是自己（Win7 上 Topmost 小窗偶尔会收到这种），不收起
        if (ForegroundIsMe()) return;
        Dismiss();
    }

    /// <summary>
    /// 兜底看守：失焦事件在 Win7 的置顶小窗上不完全可靠（侧边栏同款教训），
    /// 每 150ms 对一次前台窗口 —— 只在前台**真的变成别人**时收。
    /// 刚弹出的 150ms 不算，留给激活流程。
    /// </summary>
    private void StartForegroundWatch()
    {
        if (_foregroundWatch is null) return;
        _foregroundWatch.Stop();
        _foregroundWatch.Start();
    }

    private void CheckForeground()
    {
        if (_closing) return;
        if (Environment.TickCount64 - _openedAt < 150) return;
        if (ForegroundIsMe()) return;
        Dismiss();
    }

    private bool ForegroundIsMe()
    {
        try
        {
            var hwnd = Backdrop.TryGetHwnd(this);
            if (hwnd == IntPtr.Zero) return false;
            var fg = NativeMethods.GetForegroundWindow();
            if (fg == IntPtr.Zero) return true;      // 问不到就当是自己，宁可不收
            return fg == hwnd;
        }
        catch { return false; }
    }

    private void Dismiss()
    {
        if (_closing) return;
        _closing = true;
        StopWatch();
        try { Close(); } catch { }
    }

    private void StopWatch()
    {
        try { _foregroundWatch?.Stop(); } catch { }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        StopWatch();
        try { _corners?.Detach(); } catch { }
        _corners = null;
        if (ReferenceEquals(_open, this)) _open = null;
    }

    // ══════════════════════════════════════════════════════════════════
    //  点选 / 键盘
    // ══════════════════════════════════════════════════════════════════

    private void Pick(TrayMenuItem item)
    {
        if (_closing) return;
        _closing = true;
        StopWatch();

        var cb = _picked;
        try { Close(); } catch { }

        try { cb?.Invoke(item); }
        catch (Exception ex) { AppLog.Fail("tray", ex, $"托盘菜单命令执行失败：{item.Command}"); }
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                e.Handled = true;
                Dismiss();
                break;

            case Key.Down:
                e.Handled = true;
                MoveSelection(1);
                break;

            case Key.Up:
                e.Handled = true;
                MoveSelection(-1);
                break;

            case Key.Enter:
            case Key.Space:
                if (_selected >= 0 && _selected < _itemButtons.Count &&
                    _itemButtons[_selected].Tag is TrayMenuItem picked)
                {
                    e.Handled = true;
                    Pick(picked);
                }
                break;
        }
    }

    /// <summary>当前高亮行（-1 = 没有）。鼠标移进哪行、上下键走到哪行都归这里，同一时刻只亮一行。</summary>
    private void Select(int index)
    {
        if (index == _selected) return;
        if (_selected >= 0 && _selected < _itemButtons.Count)
            _itemButtons[_selected].Classes.Remove("traySelected");
        _selected = index;
        if (_selected >= 0 && _selected < _itemButtons.Count)
            _itemButtons[_selected].Classes.Add("traySelected");
    }

    private void MoveSelection(int delta)
    {
        if (_itemButtons.Count == 0) return;

        var next = _selected < 0
            ? (delta > 0 ? 0 : _itemButtons.Count - 1)
            : (_selected + delta + _itemButtons.Count) % _itemButtons.Count;

        Select(next);
    }
}
