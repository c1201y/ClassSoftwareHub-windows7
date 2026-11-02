using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using ClassSoftwareHub.Desktop.Core;
using ClassSoftwareHub.Desktop.Platform;
using ClassSoftwareHub.Desktop.Services;

namespace ClassSoftwareHub.Desktop.Views;

/// <summary>
/// 文件快搜浮窗。
///
/// <para>
/// 窗口边框与「常用工具」浮窗完全同一套：所有系统都由 <see cref="SelfDrawnFrame"/>
/// 接管非客户区（拖动 / 贴边 / 双击 / 边缘缩放仍由系统执行），关闭键自己画。
/// × / Esc = 收起来，和常用工具浮窗语义一致；真正退出由主窗口的 FinishExit 统一关。
/// </para>
/// </summary>
public sealed partial class FileSearchWindow : Window
{
    private const int DefaultWidthDip = 780;
    private const int DefaultHeightDip = 540;
    private const int TitleBarHeightDip = 32;

    private static FileSearchWindow? _instance;

    private SelfDrawnFrame? _frame;
    private RoundedCorners? _corners;

    private bool _shownOnce;
    private bool _visible;
    private int _fadeEpoch;

    private readonly string _backdropPrefer;

    private FileSearchWindow()
    {
        InitializeComponent();

        var want = App.Settings.Current.Backdrop;
        _backdropPrefer = string.Equals(want, "mica", StringComparison.OrdinalIgnoreCase) ? "acrylic" : want;

        Configure();
    }

    // ── 对外 ─────────────────────────────────────────────────────────────

    /// <summary>摊开浮窗；已经开着就把它调到前面并聚焦搜索框。</summary>
    public static void ShowWindow()
    {
        try
        {
            _instance ??= new FileSearchWindow();
            _instance.BringUp();
        }
        catch (Exception ex)
        {
            AppLog.Exception("file-search", ex, "打开文件快搜窗口");
        }
    }

    public static void HideWindow() => _instance?.Collapse();

    public static void Toggle()
    {
        if (_instance is { _visible: true }) _instance.Collapse();
        else ShowWindow();
    }

    public static void CloseForExit()
    {
        var w = _instance;
        _instance = null;
        try { w?.Close(); } catch { }
        AppLog.Info("exit", "文件快搜窗口实例已请求 Close");
    }

    // ── 窗口本身 ─────────────────────────────────────────────────────────

    private void Configure()
    {
        try
        {
            Title = "文件快搜";
            ShowInTaskbar = false;
            CanResize = true;
            MinWidth = 440;
            MinHeight = 320;
            Topmost = App.Settings.Current.FileSearchOnTop;

            ExtendClientAreaToDecorationsHint = false;
            AttachSelfDrawnFrame();

            try { _corners = RoundedCorners.Attach(this); } catch { }

            ThemeCompat.Apply(Root);
            ClientSize = new Size(DefaultWidthDip, DefaultHeightDip);
            UpdatePinVisual();

            Closed += (_, _) => AppLog.Info("exit", "文件快搜窗口 Closed");

            Closing += (_, args) =>
            {
                if (App.IsExiting) return;
                args.Cancel = true;
                Collapse();
            };
        }
        catch (Exception ex)
        {
            AppLog.Exception("file-search", ex, "初始化文件快搜窗口");
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
            AppLog.Debug("file-search", "自绘窗口框架挂载失败，退回原生标题栏: " + ex.Message);
        }
    }

    private IReadOnlyList<Rect> TitleBarInteractiveRegions()
    {
        var list = new List<Rect>(3);
        AddRegion(list, PinButton);
        AddRegion(list, OpenPageButton);
        AddRegion(list, CaptionCloseButton);
        return list;
    }

    private void AddRegion(List<Rect> list, Control button)
    {
        try
        {
            if (button.IsVisible && button.Bounds.Width > 0 && button.Bounds.Height > 0)
            {
                var origin = button.TranslatePoint(new Point(0, 0), this);
                if (origin is { } p)
                    list.Add(new Rect(p.X, p.Y, button.Bounds.Width, button.Bounds.Height));
            }
        }
        catch { }
    }

    private void CaptionClose_Click(object? sender, RoutedEventArgs e) => Collapse();

    private void Pin_Click(object? sender, RoutedEventArgs e)
    {
        var on = !App.Settings.Current.FileSearchOnTop;
        App.Settings.Current.FileSearchOnTop = on;
        App.Settings.Save();
        Topmost = on;
        UpdatePinVisual();
    }

    private void UpdatePinVisual()
    {
        try { PinButton.Opacity = App.Settings.Current.FileSearchOnTop ? 0.95 : 0.45; } catch { }
    }

    private void OpenPage_Click(object? sender, RoutedEventArgs e)
    {
        Collapse();
        App.MainWindow?.OpenToolSettings(typeof(Pages.Tools.FileSearchPage));
    }

    private void Root_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Collapse();
        }
    }

    // ── 显示 / 收起 ──────────────────────────────────────────────────────

    private void BringUp()
    {
        try
        {
            Topmost = App.Settings.Current.FileSearchOnTop;

            _fadeEpoch++;
            FlyoutFade.Prepare(Root);

            if (!_shownOnce)
            {
                _shownOnce = true;
                Show();

                ThemeCompat.Apply(Root);
                _corners?.Refresh();
                BackdropTheme.Apply(this, Root, _backdropPrefer);
                ApplyPosition();
            }
            else
            {
                var hwnd = Backdrop.TryGetHwnd(this);
                try { NativeMethods.ShowWindow(hwnd, SW_SHOW); } catch { }
                try { NativeMethods.SetForegroundWindow(hwnd); } catch { }
                Activate();
                _corners?.Refresh();
            }

            _visible = true;
            UpdatePinVisual();
            SearchView.FocusSearch();
            FlyoutFade.In(Root, 150);
        }
        catch (Exception ex)
        {
            AppLog.Exception("file-search", ex, "显示文件快搜窗口");
        }
    }

    private void Collapse()
    {
        if (!_visible) return;
        _visible = false;

        var epoch = ++_fadeEpoch;
        FlyoutFade.Out(Root, 150, () =>
        {
            if (epoch != _fadeEpoch) return;
            try { NativeMethods.ShowWindow(Backdrop.TryGetHwnd(this), SW_HIDE); }
            catch (Exception ex) { AppLog.Debug("file-search", "收起失败: " + ex.Message); }
        });
    }

    private void ApplyPosition()
    {
        try
        {
            var work = Screens.Primary?.WorkingArea;
            if (work is null) return;

            var scale = RenderScaling <= 0 ? 1.0 : RenderScaling;
            var w = (int)Math.Round(ClientSize.Width * scale);
            var h = (int)Math.Round(ClientSize.Height * scale);

            Position = new PixelPoint(
                work.Value.X + Math.Max(0, (work.Value.Width - w) / 2),
                work.Value.Y + Math.Max(0, (work.Value.Height - h) / 3));
        }
        catch (Exception ex)
        {
            AppLog.Debug("file-search", "定位失败: " + ex.Message);
        }
    }

    private const int SW_HIDE = 0;
    private const int SW_SHOW = 5;
}
