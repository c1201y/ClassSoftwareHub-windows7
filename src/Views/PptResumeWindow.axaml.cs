using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Selection;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using ClassSoftwareHub.Desktop.Core;
using ClassSoftwareHub.Desktop.Platform;
using ClassSoftwareHub.Desktop.Services;

namespace ClassSoftwareHub.Desktop.Views;

/// <summary>
/// PPT 续播浮窗。
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

    /// <summary>
    /// 用户在下拉里**手动选过**的科目。
    /// ⛔ 一旦手动选过，就不能再被「自动识别的当前课表科目」覆盖 ——
    ///    否则课间想续上一节的数学，浮窗却每 30 秒自己跳回当前这节语文，
    ///    用户点了没效果，会以为程序坏了。
    /// </summary>
    private string _manualSubject = "";

    /// <summary>正在重建下拉项时压住 SelectionChanged，避免自己触发自己。</summary>
    private bool _rebuildingCombo;

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
        AppLog.Info("window", "窗口关闭请求｜窗口=PPT 续播｜触发=程序化 Close");
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

            Closed += (_, _) => AppLog.Info("window", "窗口已关闭｜窗口=PPT 续播");

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
            AppLog.Catch("ppt-resume", "挂载自绘窗口框架（已退回原生标题栏）", ex, Core.LogLevel.Debug);
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

            // ── 决定当前该续哪个科目 ──
            // 优先级：① 用户手动选过的（最高，不许被覆盖）
            //        ② 课表里当前/临近的这一节
            //        ③ 正在放映的那份课件识别出来的科目
            //        ④ 最近一次记录过进度的科目
            var auto = snapshot.Lesson?.Subject ?? "";
            if (auto.Length == 0) auto = snapshot.Live.Subject;
            if (auto.Length == 0)
            {
                auto = snapshot.Subjects
                    .Where(s => s.HasRecord)
                    .OrderByDescending(s => s.SavedAt, StringComparer.Ordinal)
                    .Select(s => s.Name)
                    .FirstOrDefault() ?? "";
            }

            var subject = _manualSubject.Length > 0 ? _manualSubject : auto;
            _subject = subject;

            // ── 填充下拉：课表科目 + 有记录的科目，去重并排序 ──
            RebuildSubjectCombo(snapshot, subject);

            var view = snapshot.Subjects.FirstOrDefault(s =>
                string.Equals(s.Name, subject, StringComparison.OrdinalIgnoreCase));

            LessonText.Text = snapshot.Lesson is not null
                ? (snapshot.Lesson.Ongoing ? "现在：" : "临近：")
                  + snapshot.Lesson.Subject + " · " + snapshot.Lesson.TimeText
                : snapshot.Live.Playing
                    ? "正在放映"
                    : "当前没有排课";

            FileText.Text = view is { HasRecord: true }
                ? view.FileName
                : subject.Length > 0
                    ? "还没有这个科目的课件记录，点下面「给这个科目选课件」"
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

    /// <summary>
    /// 重建科目下拉项。
    ///
    /// <para>
    /// 列表 = 课表科目 ∪ 有记录的科目。
    /// ⛔ 不能只列课表科目：用户可能想续一节当天没排的课（调课、补课），
    ///    也可能根本没导入课表，只手工给几个科目配过课件。
    /// </para>
    /// <para>
    /// 只有内容真的变了才重建 —— 每 30 秒刷新重建一次会让下拉在用户操作时闪。
    /// </para>
    /// </summary>
    private void RebuildSubjectCombo(PptResumeSnapshot snapshot, string selected)
    {
        try
        {
            var names = new List<string>();

            foreach (var row in snapshot.Subjects)
            {
                var name = (row.Name ?? "").Trim();
                if (name.Length > 0 && !names.Contains(name, StringComparer.OrdinalIgnoreCase))
                    names.Add(name);
            }

            // 手动选过一个连记录都没有的科目时，也要能显示出来
            if (selected.Length > 0 && !names.Contains(selected, StringComparer.OrdinalIgnoreCase))
                names.Add(selected);

            if (names.Count == 0)
            {
                _rebuildingCombo = true;
                try
                {
                    SubjectCombo.ItemsSource = new[] { "未识别科目" };
                    SubjectCombo.SelectedIndex = 0;
                    SubjectCombo.IsEnabled = false;
                }
                finally { _rebuildingCombo = false; }
                return;
            }

            // 内容没变就不动，避免打断用户正在操作的下拉
            var current = SubjectCombo.ItemsSource as IReadOnlyList<string>;
            var same = current is not null && current.Count == names.Count
                       && current.Select((v, i) => string.Equals(v, names[i], StringComparison.Ordinal)).All(ok => ok);

            _rebuildingCombo = true;
            try
            {
                SubjectCombo.IsEnabled = true;

                if (!same) SubjectCombo.ItemsSource = names;

                var index = names.FindIndex(n => string.Equals(n, selected, StringComparison.OrdinalIgnoreCase));
                if (index < 0) index = 0;
                if (SubjectCombo.SelectedIndex != index) SubjectCombo.SelectedIndex = index;
            }
            finally { _rebuildingCombo = false; }
        }
        catch (Exception ex)
        {
            AppLog.Catch("ppt-resume", "重建科目下拉", ex, Core.LogLevel.Debug);
        }
    }

    /// <summary>用户在下拉里换了科目：记成手动选择，并立刻刷新这一科的进度。</summary>
    private void SubjectCombo_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_rebuildingCombo) return;

        var picked = SubjectCombo.SelectedItem as string ?? "";
        if (picked.Length == 0 || picked == "未识别科目") return;

        _manualSubject = picked;
        Refresh();
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
            catch (Exception ex) { AppLog.Catch("ppt-resume", "收起 PPT 续播窗口", ex, Core.LogLevel.Debug); }
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
            AppLog.Catch("ppt-resume", "定位 PPT 续播窗口", ex, Core.LogLevel.Debug);
        }
    }

    private const int SW_HIDE = 0;
    private const int SW_SHOW = 5;
}
