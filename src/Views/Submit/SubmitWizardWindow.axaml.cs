using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using ClassSoftwareHub.Desktop.Core;
using ClassSoftwareHub.Desktop.Views.Submit.Steps;

namespace ClassSoftwareHub.Desktop.Views.Submit;

/// <summary>
/// 投稿向导窗口（自上游 dv1.2.2 移植，2026-11-14）：页面宿主 + 底部导航条，
/// 具体步骤见 <see cref="SubmitSession"/>。
///
/// <para>
/// 与主窗口**并存**（非模态）：投稿时经常要回软件清单确认 ID 是否撞车、名称怎么写，
/// 模态窗口会把清单挡在外面。同一时刻只开一个，重复调用 <see cref="Show"/> 只把它唤到前面。
/// </para>
///
/// <para>
/// 关窗不丢内容：<see cref="SubmitDraft.Save"/> 在每次翻页与关窗时都会落盘，
/// 下次打开自动带出。⛔ 别再做成「失败才存草稿 + 手动恢复」。
/// </para>
///
/// <para>
/// ⚠️ 移植说明（WinUI → Avalonia）：
/// <list type="bullet">
///   <item>Frame 的 <c>NavigationCacheMode="Required"</c> → 这里自己缓存页面实例
///         （<see cref="_pages"/>），往回翻时用户刚填的值与滚动位置都还在；</item>
///   <item>翻页动画 → 直接切内容，不做滑动（软件渲染下动画会掉帧，翻页卡一下更难受）；</item>
///   <item>AppWindow 的尺寸/居中 → Avalonia 的 <see cref="WindowStartupLocation"/> 与 InitialSize 调整。</item>
/// </list>
/// </para>
/// </summary>
public sealed partial class SubmitWizardWindow : Window
{
    private const double WidthDip = 880;
    private const double HeightDip = 720;

    private static SubmitWizardWindow? _instance;

    private SubmitSession? _session;
    private bool _closed;

    /// <summary>页面实例缓存（对应上游 Frame 的 NavigationCacheMode=Required）。</summary>
    private readonly System.Collections.Generic.Dictionary<Type, Control> _pages = new();

    public SubmitWizardWindow()
    {
        InitializeComponent();

        _session = SubmitSession.Attach(this, SubmitDraft.Load() ?? SubmitDraft.CreateEmpty());

        Opened += (_, _) => FitToWorkArea();
        Closed += (_, _) =>
        {
            _closed = true;
            SaveAndDetach();
        };

        // 先挂好事件再 Prepare：Prepare 里那次就是第一页，晚了按钮文案会停在默认值
        _session.Prepare();

        // ⚠️ 上游靠 Frame.Navigated 事件刷底部按钮；Avalonia 侧没有这个事件
        //    （换页直接换 StepHost 的子控件），所以第一页建好后必须显式刷一次，
        //    否则第一步的按钮会停在 XAML 里写的「下一步」，而不是「开始填写」。
        RefreshNav();
    }

    /// <summary>打开向导；已经开着就把它唤到前面。命名不叫 Show 是为了避开 Window.Show 实例方法。</summary>
    public static void Open()
    {
        if (_instance is not null)
        {
            try { if (_instance.WindowState == WindowState.Minimized) _instance.WindowState = WindowState.Normal; } catch { }
            try { _instance.Activate(); } catch { }
            return;
        }

        try
        {
            var win = new SubmitWizardWindow();

            // 非模态：主窗口仍在，投稿人要能回头看软件清单
            if (App.MainWindow is { } owner) win.Show(owner);
            else win.Show();

            _instance = win;
            AppLog.Info("submit", "投稿向导已打开");
        }
        catch (Exception ex)
        {
            AppLog.Exception("submit", ex, "投稿向导窗口创建失败");
            _instance = null;
        }
    }

    /// <summary>
    /// 教室机器常见 1366×768 / 125%：720dip 会超过工作区，按实际可用高度收。
    /// （上游用 AppWindow.ResizeClient 做同一件事，这里在窗口打开后算。）
    /// </summary>
    private void FitToWorkArea()
    {
        try
        {
            var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
            if (screen is null) return;

            var scaling = RenderScaling <= 0 ? 1.0 : RenderScaling;
            var workW = screen.WorkingArea.Width / scaling;
            var workH = screen.WorkingArea.Height / scaling;

            Width = Math.Clamp(WidthDip, 760, Math.Max(760, workW - 40));
            Height = Math.Clamp(HeightDip, 520, Math.Max(520, workH - 40));
        }
        catch (Exception ex)
        {
            AppLog.Warn("submit", "投稿向导尺寸设置失败：" + ex.Message);
        }
    }

    // ── 换页（由 SubmitSession 调） ───────────────────────────────────────

    internal void NavigateToStep(int index, bool forward, bool animate)
    {
        if (_closed || _session is null) return;

        try
        {
            var type = _session.StepTypeAt(index);

            if (!_pages.TryGetValue(type, out var page))
            {
                try
                {
                    page = (Control)Activator.CreateInstance(type)!;
                    _pages[type] = page;
                }
                catch (Exception ex)
                {
                    // 页面构造失败不能把整个向导带崩：明确告诉用户哪一步打不开
                    AppLog.Exception("submit", ex, "投稿向导步骤页构造失败：" + type.Name);
                    StepHost.Children.Clear();
                    StepHost.Children.Add(new TextBlock
                    {
                        Text = "这一步打不开了：" + ex.Message,
                        Margin = new Thickness(24),
                        TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                    });
                    RefreshNav();
                    return;
                }
            }

            StepHost.Children.Clear();
            StepHost.Children.Add(page);

            // 换页后同步一次底部按钮（等价于上游的 Frame.Navigated 回调）
            RefreshNav();
        }
        catch (Exception ex)
        {
            AppLog.Exception("submit", ex, "投稿向导换页失败：" + index);
        }
    }

    /// <summary>当前显示的那一页（可能为 null）。</summary>
    private Control? Current => StepHost.Children.Count > 0 ? StepHost.Children[0] : null;

    /// <summary>按当前页与当前状态刷新底部按钮与步骤提示。</summary>
    internal void RefreshNav()
    {
        if (_session is null) return;

        var index = _session.Index;
        StepText.Text = $"第 {index + 1} 步，共 {_session.StepCount} 步";

        BackNavButton.IsVisible = index > 0 && !_session.Completed;

        NextNavButton.Content = _session.Completed
            ? "关闭"
            : _session.IsLast ? "提交" : index == 0 ? "开始填写" : "下一步";

        // 提交成功后底部只剩「关闭」，左侧那句「关窗自动保存」也就不成立了
        FooterHint.Text = _session.Completed ? "" : "关闭窗口会自动保存草稿";
    }

    private void NavBack_Click(object? sender, RoutedEventArgs e) => _session?.Back();

    private async void NavNext_Click(object? sender, RoutedEventArgs e)
    {
        if (_session is null) return;

        if (_session.Completed)
        {
            CloseWizard();
            return;
        }

        // 核对页：最后一步的「提交」由页面自己执行，壳只负责压住按钮与恢复
        if (Current is SubmitReviewPage review)
        {
            NextNavButton.IsEnabled = false;
            try { await review.SubmitAsync(); }
            finally
            {
                NextNavButton.IsEnabled = true;
                RefreshNav();
            }
            return;
        }

        // 其余页：先问本页，校验不过就让页面自己指出缺什么（⛔ 不用灰按钮代替提示）
        if (Current is ISubmitStep step)
        {
            var error = step.Validate();
            if (error is not null)
            {
                step.ShowError(error);
                return;
            }
        }

        _session.Next();
    }

    internal void CloseWizard()
    {
        try { Close(); }
        catch (Exception ex) { AppLog.Warn("submit", "投稿向导关闭失败：" + ex.Message); }
    }

    /// <summary>关窗收尾：存草稿、断开会话。幂等。</summary>
    private void SaveAndDetach()
    {
        try { _session?.SaveDraft(); } catch { /* 存不下去也不拦着关窗 */ }
        SubmitSession.Detach();
        _session = null;
        _instance = null;
    }
}
