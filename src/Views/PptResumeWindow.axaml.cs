using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using ClassSoftwareHub.Desktop.Core;
using ClassSoftwareHub.Desktop.Platform;
using ClassSoftwareHub.Desktop.Services;

namespace ClassSoftwareHub.Desktop.Views;

/// <summary>
/// PPT 续播浮窗（2026-10-31）。
///
/// <para>
/// 窗口边框与「文件快搜」浮窗同一套：<see cref="SelfDrawnFrame"/> 接管非客户区，
/// × / Esc = 收起来（后台仍在记录页码）。
/// </para>
///
/// <para>
/// 显示的科目优先取课表里当前（或前后 45 分钟内）的那一节；课表没导入 / 没排课时，
/// 退到「正在放映的那份课件」或「最近一次记录过的科目」，保证课间也能点到续播。
/// </para>
/// </summary>
public sealed partial class PptResumeWindow : Window
{
    private const int DefaultWidthDip = 480;
    private const int DefaultHeightDip = 300;
    private const int TitleBarHeightDip = 32;

    private static PptResumeWindow? _instance;

    private SelfDrawnFrame? _frame;
    private RoundedCorners? _corners;
    private readonly Avalonia.Threading.DispatcherTimer _clock;

    private bool _shownOnce;
    private bool _visible;
    private int _fadeEpoch;
    private string _subject = "";

    private readonly string _backdropPrefer;

    private PptResumeWindow()
    {
        InitializeComponent();

        var want = App.Settings.Current.Backdrop;
        _backdropPrefer = string.Equals(want, "mica", StringComparison.OrdinalIgnoreCase) ? "acrylic" : want;

        Configure();

        PptResumeService.Current.Changed += Refresh;

        // 课表中的「当前这节」会随时间变化，浮窗开着时每 30 秒重算一次
        _clock = new Avalonia.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(30),
        };
        _clock.Tick += (_, _) => { if (_visible) Refresh(); };
        _clock.Start();
    }

    // ── 对外 ─────────────────────────────────────────────────────────────

    public static void ShowWindow()
    {
        try
        {
            _instance ??= new PptResumeWindow();
            _instance.BringUp();
        }
        catch (Exception ex)
        {
            AppLog.Exception("ppt-resume", ex, "打开 PPT 续播窗口");
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
        var window = _instance;
        _instance = null;
        try { window?.Close(); } catch { }
        AppLog.Info("exit", "PPT 续播窗口实例已请求 Close");
    }

    // ── 窗口本身 ─────────────────────────────────────────────────────────

    private void Configure()
    {
        try
        {
            Title = "PPT 续播";
            ShowInTaskbar = false;
            CanResize = false;
            Topmost = App.Settings.Current.PptResumeOnTop;

            ExtendClientAreaToDecorationsHint = false;
            AttachSelfDrawnFrame();

            try { _corners = RoundedCorners.Attach(this); } catch { }

            ThemeCompat.Apply(Root);
            ClientSize = new Size(DefaultWidthDip, DefaultHeightDip);
            UpdatePinVisual();

            Closed += (_, _) => AppLog.Info("exit", "PPT 续播窗口 Closed");

            Closing += (_, args) =>
            {
                if (App.IsExiting) return;
                args.Cancel = true;
                Collapse();
            };
        }
        catch (Exception ex)
        {
            AppLog.Exception("ppt-resume", ex, "初始化 PPT 续播窗口");
        }
    }

    private void AttachSelfDrawnFrame()
    {
        try
        {
            _frame = SelfDrawnFrame.Attach(this);
            _frame.ResizeEnabled = false;
            _frame.TitleBarHeight = TitleBarHeightDip;
            _frame.InteractiveRegions = TitleBarInteractiveRegions;
            _frame.EraseColorProvider = SelfDrawnFrame.EraseColorFrom(Root);
        }
        catch (Exception ex)
        {
            _frame = null;
            AppLog.Debug("ppt-resume", "自绘窗口框架挂载失败，退回原生标题栏: " + ex.Message);
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

    // ── 刷新内容 ─────────────────────────────────────────────────────────

    private void Refresh()
    {
        try
        {
            var snapshot = PptResumeService.Current.Snapshot();

            var subject = snapshot.Lesson?.Subject ?? "";
            if (subject.Length == 0) subject = snapshot.Live.Subject;
            if (subject.Length == 0)
            {
                subject = snapshot.Subjects
                    .Where(s => s.HasRecord)
                    .OrderByDescending(s => s.SavedAt, StringComparer.Ordinal)
                    .Select(s => s.Name)
                    .FirstOrDefault() ?? "";
            }
            _subject = subject;

            var view = snapshot.Subjects.FirstOrDefault(s =>
                string.Equals(s.Name, subject, StringComparison.OrdinalIgnoreCase));

            LessonText.Text = snapshot.Lesson is not null
                ? (snapshot.Lesson.Ongoing ? "现在：" : "临近：")
                  + snapshot.Lesson.Subject + " · " + snapshot.Lesson.TimeText
                : snapshot.Live.Playing
                    ? "正在放映"
                    : "当前没有排课";

            SubjectText.Text = subject.Length > 0 ? subject : "未识别科目";

            FileText.Text = view is { HasRecord: true }
                ? view.FileName
                : subject.Length > 0
                    ? "还没有这个科目的课件记录"
                    : "导入课表或打开需要续播的 PPT，程序会自动识别科目";

            SlideText.Text = view is { HasRecord: true }
                ? "上次讲到第 " + view.Slide + " 页" + (view.Total > 0 ? "（共 " + view.Total + " 页）" : "")
                : "放映时会自动记录页码";

            ResumeButton.IsEnabled = view is { HasRecord: true };
            PickButton.IsEnabled = subject.Length > 0;

            var live = snapshot.Live;
            if (live.Playing)
            {
                StatusText.Text = "正在放映：" + Path.GetFileName(live.File) + " · 第 " + live.Slide
                                  + (live.Total > 0 ? " / " + live.Total : "") + " 页";
            }
            else
            {
                // 只显示比实时状态更新的操作提示：PPT 关掉后旧的成功提示不能盖住「未在放映」
                StatusText.Text = snapshot.LastMessage.Length > 0 && snapshot.MessageAt > snapshot.LiveAt
                    ? snapshot.LastMessage
                    : live.Status;
            }
        }
        catch (Exception ex)
        {
            StatusText.Text = "刷新失败：" + ex.Message;
        }
    }

    // ── 按钮 ─────────────────────────────────────────────────────────────

    private void CaptionClose_Click(object? sender, RoutedEventArgs e) => Collapse();

    private void Resume_Click(object? sender, RoutedEventArgs e)
    {
        if (_subject.Length == 0)
        {
            StatusText.Text = "先导入课表，或在页面上给某个科目指定课件。";
            return;
        }

        PptResumeService.Current.Resume(_subject);
        StatusText.Text = "正在唤起演示程序，从上次那一页继续…";
    }

    private async void Pick_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (_subject.Length == 0)
            {
                StatusText.Text = "先导入课表，或在完整页面里指定课件。";
                return;
            }

            var top = TopLevel.GetTopLevel(this);
            if (top is null) return;

            var filters = new List<FilePickerFileType>
            {
                new("演示文稿") { Patterns = new[] { "*.pptx", "*.ppt", "*.ppsx", "*.pps", "*.pptm" } },
                new("所有文件") { Patterns = new[] { "*" } },
            };

            var result = await FilePickerCompat.PickFilesAsync(top, "选择「" + _subject + "」的课件", false, filters);
            if (result.Files.Count == 0)
            {
                if (!string.IsNullOrEmpty(result.Error)) StatusText.Text = result.Error;
                return;
            }

            var (_, message) = PptResumeService.Current.AssignFile(_subject, result.Files[0]);
            StatusText.Text = message;
            Refresh();
        }
        catch (Exception ex)
        {
            StatusText.Text = "选择课件失败：" + ex.Message;
        }
    }

    private void OpenPage_Click(object? sender, RoutedEventArgs e)
    {
        Collapse();
        App.MainWindow?.OpenToolSettings(typeof(Pages.Tools.PptResumePage));
    }

    private void Pin_Click(object? sender, RoutedEventArgs e)
    {
        var on = !App.Settings.Current.PptResumeOnTop;
        App.Settings.Current.PptResumeOnTop = on;
        App.Settings.Save();
        Topmost = on;
        UpdatePinVisual();
    }

    private void UpdatePinVisual()
    {
        try { PinButton.Opacity = App.Settings.Current.PptResumeOnTop ? 0.95 : 0.45; } catch { }
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
            Topmost = App.Settings.Current.PptResumeOnTop;

            _fadeEpoch++;
            FlyoutFade.Prepare(Root);

            Refresh();

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
            FlyoutFade.In(Root, 150);
        }
        catch (Exception ex)
        {
            AppLog.Exception("ppt-resume", ex, "显示 PPT 续播窗口");
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
            catch (Exception ex) { AppLog.Debug("ppt-resume", "收起失败: " + ex.Message); }
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
            AppLog.Debug("ppt-resume", "定位失败: " + ex.Message);
        }
    }

    private const int SW_HIDE = 0;
    private const int SW_SHOW = 5;
}
