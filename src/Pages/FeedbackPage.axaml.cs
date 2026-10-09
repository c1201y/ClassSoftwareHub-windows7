using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using ClassSoftwareHub.Desktop.Core;
using ClassSoftwareHub.Desktop.Data;
using ClassSoftwareHub.Desktop.Platform;
using ClassSoftwareHub.Desktop.Services;
using FluentAvalonia.UI.Controls;

namespace ClassSoftwareHub.Desktop.Pages;

/// <summary>
/// 反馈中心的**选择页**：两张类型卡 + 提交后的处理流程 + 查重入口 + 草稿条。
///
/// 单击类型卡 = **导航到 <see cref="FeedbackFormPage"/>**（2026-09-30 Nick 要求改成正常页面逻辑，
/// ⛔ 不再在同一页里靠 Visibility 互斥切换）。返回走导航栏那一个返回按钮。
///
/// 逻辑都在 <see cref="Feedback"/>（纯数据 + 拼装 + 校验）和
/// <see cref="FeedbackDraftStore"/>（草稿，进程内共享 + 落盘）里，这里只负责把界面接到它们上面。
///
/// ⚠️ 移植说明：
///   · BitmapImage(new Uri(path)) → Avalonia 的 Bitmap(文件名)。
///   · 主视觉横幅：主题色取 Services.ThemeBrush.AccentColor()（原版查 SystemAccentColor 资源，
///     移植版里主题色统一走 ThemeBrush）；深/浅色判定用 ThemeBrush.IsDark（原版 ActualTheme）。
///     ActualThemeChanged → ActualThemeVariantChanged（语义一致：主题切换时重画横幅）。
/// </summary>
public sealed partial class FeedbackPage : PageBase
{
    /// <summary>两张卡片的图（跟网页版同一套 PNG，从嵌入资源解出来）。</summary>
    private IImage? _iconReport;
    private IImage? _iconSuggestion;

    public FeedbackPage()
    {
        InitializeComponent();
        // 每次进本页都跑一遍：从表单页返回时 Frame 会重建本页，草稿条要据此重算
        Loaded += (_, _) => Init();
    }

    private void Init()
    {
        // 卡片图：跟网页版 src/assets/feedback 是同一套文件
        _iconReport = LoadIcon("report.png", "feedback-report.png");
        _iconSuggestion = LoadIcon("suggest.png", "feedback-suggest.png");
        KindIconReport.Source = _iconReport;
        KindIconSuggestion.Source = _iconSuggestion;

        RefreshDraftBar();

        // 主视觉横幅是主题色渐变，深/浅色各一套 —— 主题变了要重画
        ApplyHeroBrush();
        ActualThemeVariantChanged += (_, _) => ApplyHeroBrush();
    }

    private static IImage? LoadIcon(string fileName, string cacheName)
    {
        try
        {
            var path = EmbeddedAssets.ExtractToCache(fileName, cacheName);
            if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path)) return null;
            return new Bitmap(path);
        }
        catch { return null; }        // 图读不到就空着，别把整页搞崩
    }

    /// <summary>
    /// 主视觉横幅的底：网页版是 <c>linear-gradient(100deg, accent 16%, 面色 58%, 卡片色 100%)</c>。
    /// 主题色那一段取系统主题色（拿不到就退回默认蓝），后两段按深/浅色写死
    /// —— 主题色 + 中性面色这套没法用 DynamicResource 直接塞进 GradientStop.Color（那里要 Color 不是 Brush）。
    /// ⚠️ WinUI 的 StartPoint/EndPoint 是相对坐标 → Avalonia 用 RelativePoint(Relative)。
    /// </summary>
    private void ApplyHeroBrush()
    {
        try
        {
            var dark = ThemeBrush.IsDark(this);

            var accent = ThemeBrush.AccentColor();

            var brush = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(1, 0.18, RelativeUnit.Relative),     // ≈ 100°（略向右下）
            };

            brush.GradientStops.Add(new GradientStop
            {
                Offset = 0,
                Color = Color.FromArgb(41, accent.R, accent.G, accent.B),   // 主题色 ≈16%
            });
            brush.GradientStops.Add(new GradientStop
            {
                Offset = 0.58,
                Color = dark ? Color.FromArgb(255, 32, 32, 32)
                             : Color.FromArgb(255, 246, 246, 246),
            });
            brush.GradientStops.Add(new GradientStop
            {
                Offset = 1,
                Color = dark ? Color.FromArgb(255, 43, 43, 43)
                             : Color.FromArgb(255, 252, 252, 252),
            });

            HeroBanner.Background = brush;
        }
        catch { }
    }

    // ════════════════════════════════════════════════════════════════
    // 选类型 → 导航到表单页
    // ════════════════════════════════════════════════════════════════

    /// <summary>
    /// 单击类型卡：把卡片标签（<c>report</c> / <c>suggestion</c>）当导航参数带去表单页。
    /// ⚠️ 走 <c>ShellPage.NavigateToFeedbackForm</c> 而不是直接 <c>Frame.Navigate</c> ——
    ///    前者顺手把左侧导航高亮留在「反馈中心」（人确实还在反馈中心这一区里）。
    /// </summary>
    private void KindCard_Click(object? sender, RoutedEventArgs e)
    {
        var key = (sender as Control)?.Tag as string ?? "";
        if (key.Length == 0) return;
        NavigateToForm(key);
    }

    private static void NavigateToForm(string kind)
        => App.MainWindow?.Shell.NavigateToFeedbackForm(kind);

    // ════════════════════════════════════════════════════════════════
    // 草稿
    // ════════════════════════════════════════════════════════════════

    /// <summary>
    /// 草稿条显不显示。
    ///
    /// ⚠️ 判定标准是「**真的填过东西**」（标题 / 描述 / 联系方式任一处非空），
    ///    ⛔ 不是「草稿文件存在」、⛔ 也不是「草稿里记着类型」。
    ///    原因（2026-10-04 Nick 反馈"这个草稿条一直在，很烦"）：
    ///    点一下类型卡片就会把 Kind 写进草稿并落盘，而 <see cref="FeedbackDraftStore.Load"/> 对
    ///    "只有 Kind、一个字没写"的草稿是**当作有草稿**返回的 —— 于是只要点过一次反馈类型，
    ///    这条提示就永远挂在页面上，可它其实什么都没得恢复。
    /// </summary>
    private void RefreshDraftBar()
    {
        var saved = FeedbackDraftStore.Load();
        DraftBar.IsOpen = HasTyped(FeedbackDraftStore.Current) || (saved is not null && HasTyped(saved));
    }

    /// <summary>有没有值得恢复的正文（标题 / 描述 / 联系方式）。只选了类型、什么都没写 = 没有。</summary>
    private static bool HasTyped(Feedback.Draft draft) =>
        draft.Title.Trim().Length > 0
        || draft.Detail.Trim().Length > 0
        || draft.Contact.Trim().Length > 0;

    private void ResumeDraft_Click(object? sender, RoutedEventArgs e)
    {
        var draft = FeedbackDraftStore.Current;

        // 内存里已经填着（用户点过卡片又退回来的）→ 直接带着这个类型进表单页，
        // 别用文件把它盖回去 —— 文件里可能是更早的一份
        if (draft.Kind.Length == 0)
        {
            var saved = FeedbackDraftStore.Load();
            if (saved is null) return;

            // ⚠️ 就地拷（CopyFrom），不能换引用：Current 是这个进程长期共享的同一个实例
            draft.CopyFrom(saved);
        }

        if (draft.Kind.Length == 0) return;
        NavigateToForm(draft.Kind);
    }

    /// <summary>
    /// 草稿条右上角那个 ✕ = **丢弃草稿**（2026-10-04 Nick 指定）。
    ///
    /// 丢弃会真的删掉已填内容且不可恢复，所以先弹一次确认。
    ///
    /// ⚠️ InfoBar 的关闭按钮点下去就会自己收起，且 <c>CloseButtonClick</c> 的参数里没有
    ///    <c>Handled</c> 可拦 —— 所以「取消」时要在对话框关掉之后再把 <see cref="DraftBar"/> 放回来。
    ///    （对话框是模态的，中间那下收起看不见。）
    /// </summary>
    private async void DraftBar_Close(InfoBar sender, EventArgs args)
    {
        // ⚠️ 原版 ContentDialog.XamlRoot = XamlRoot；FA 的 ContentDialog 没有 XamlRoot，
        //    直接收 TopLevel（与 FeedbackPage 之外的页面同一套写法）。
        var dialog = new ContentDialog
        {
            Title = "丢弃这份反馈草稿？",
            Content = "已填写的内容会被删除，且无法恢复。",
            PrimaryButtonText = "丢弃",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,     // 回车落「取消」，别一键把草稿送走
        };

        var top = TopLevel.GetTopLevel(this);
        var result = top is null ? await dialog.ShowAsync() : await dialog.ShowAsync(top);
        if (result != ContentDialogResult.Primary)
        {
            DraftBar.IsOpen = true;      // 反悔：把条放回去
            return;
        }

        FeedbackDraftStore.Discard();
        DraftBar.IsOpen = false;
    }

    private void OpenIssueList_Click(object? sender, RoutedEventArgs e)
        => App.MainWindow?.OpenExternal(Feedback.IssueListUrl);
}
