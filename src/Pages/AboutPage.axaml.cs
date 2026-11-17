using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using ClassSoftwareHub.Desktop.Core;
using ClassSoftwareHub.Desktop.Platform;
using FluentAvalonia.UI.Controls;

namespace ClassSoftwareHub.Desktop.Pages;

/// <summary>
/// 「关于」页（自上游 dv1.2.2 移植，2026-11-14）。
///
/// <para>
/// 收纳位置对应上游 1.2.2 的导航调整：「回声洞、授权与条款声明移至关于页；
/// 导航栏底部的反馈中心与更新日志不再单列」。本页因此同时承载
/// 应用信息 / 帮助与反馈 / 回声洞 / 条款与声明 / 授权 五组内容。
/// </para>
///
/// <para>
/// ⚠️ 上游在这个页面上画了一条铺满顶部的横幅并做下缘渐隐，实现依赖 WinUI 3 的
/// 合成层遮罩。Avalonia 没有等价 API，而在软件渲染（Win7 默认路径）上模拟渐隐
/// 代价很高，所以**不搬横幅**，直接以标题起头。
/// </para>
///
/// <para>
/// ⚠️ 回声洞那一段是从设置页**整块搬来**的：文案、交互、「先拨 true 再拨 false」
/// 的收折手法、提交自检与兜底链接全部保持一致，只换了宿主页面。
/// 改这块之前先看 <see cref="SettingsPage"/> 里原来的注释。
/// </para>
/// </summary>
public sealed partial class AboutPage : PageBase
{
    public AboutPage()
    {
        InitializeComponent();

        // 与设置页同一个手法：起手把回声洞折起来（模板内层的 Expander 要一起压）。
        AttachedToVisualTree += (_, _) => CollapseEchoCave();
    }

    public override void OnNavigatedTo(object? parameter)
    {
        RenderIdentity();
        RenderLicense();
        CollapseEchoCave();
    }

    // ══════════════════════════════════════════════════════════════════
    //  应用信息
    // ══════════════════════════════════════════════════════════════════

    private void RenderIdentity()
    {
        try
        {
            // 界面上一律用 DisplayVersion —— 预览版显示成 1.2.0-Pr1.11，
            // 不给用户看 insider 这个内部术语；版本比较仍走 ShellVersion。
            VersionText.Text = ShellConfig.DisplayTag;

            CopyrightText.Text =
                $"Copyright © {DateTime.Now.Year} ClassSoftwareHub 贡献者。"
                + "本项目以 GPL-3.0 许可发布，按「现状」提供，不附带任何担保。";
        }
        catch (Exception ex)
        {
            AppLog.Catch("about", "渲染应用信息", ex, LogLevel.Debug);
        }
    }

    private void OpenRepo_Click(object? sender, RoutedEventArgs e)
        => OpenUrl($"https://github.com/{ShellConfig.UpdateRepoOwner}/{ShellConfig.UpdateRepoName}");

    private void OpenSite_Click(object? sender, RoutedEventArgs e)
        => OpenUrl(ShellConfig.SiteUrl);

    private void OpenSponsor_Click(object? sender, RoutedEventArgs e)
        => OpenUrl("https://ifdian.net/a/TinyNickCSHub");

    // ══════════════════════════════════════════════════════════════════
    //  帮助与反馈
    // ══════════════════════════════════════════════════════════════════

    private void OpenFeedback_Click(object? sender, RoutedEventArgs e) => Navigate("feedback");

    private void OpenChangelog_Click(object? sender, RoutedEventArgs e) => Navigate("changelog");

    private void OpenLogs_Click(object? sender, RoutedEventArgs e) => Navigate("log-viewer");

    // ══════════════════════════════════════════════════════════════════
    //  条款与声明
    // ══════════════════════════════════════════════════════════════════

    private void OpenLegal_Click(object? sender, RoutedEventArgs e)
    {
        var key = (sender as Control)?.Tag as string;
        if (string.IsNullOrEmpty(key)) key = "agreement";

        try
        {
            Shell()?.NavigateToLegal(key);
        }
        catch (Exception ex)
        {
            AppLog.Catch("about", "打开法律条款｜键=" + key, ex, LogLevel.Debug);
        }
    }

    // ══════════════════════════════════════════════════════════════════
    //  授权
    // ══════════════════════════════════════════════════════════════════

    private void RenderLicense()
    {
        try
        {
            // ⛔ 与设置页同一套规则：官方发布版与已激活版本都只展示状态，
            //    不摆一个点了只会重念状态的按钮（上游 dv1.2.0-insider1.11）。
            var official = BuildIdentity.IsOfficial;
            var activated = Services.License.LicenseStore.IsActivated;
            LicenseInputButton.IsVisible = !(official || activated);

            if (official)
            {
                LicenseText.Text = "已通过官方激活，永久可用";
                return;
            }

            if (activated)
            {
                var at = Services.License.LicenseStore.ActivatedAtText;
                LicenseText.Text = at is null
                    ? $"已激活；设备码 {Services.License.LicenseStore.DeviceCode}"
                    : $"已于 {at} 激活；设备码 {Services.License.LicenseStore.DeviceCode}";
                return;
            }

            LicenseText.Text = "未激活；在线服务需激活后使用";
        }
        catch (Exception ex)
        {
            AppLog.Catch("about", "渲染授权状态", ex, LogLevel.Debug);
            LicenseText.Text = "授权状态未知";
        }
    }

    private void OpenActivation_Click(object? sender, RoutedEventArgs e) => Navigate("settings");

    // ══════════════════════════════════════════════════════════════════
    //  回声洞（自设置页整块搬来，交互不变）
    // ══════════════════════════════════════════════════════════════════

    private void EchoCave_Click(object? sender, RoutedEventArgs e) => EchoCave.ShowNext();

    /// <summary>
    /// 强制把回声洞折起来。
    /// <para>
    /// 光设 <c>IsExpanded = false</c> 在这个控件上不够稳：只在 IsExpanded **发生变化**时
    /// 才驱动 VisualState。所以先拨到 true 再拨回 false，强制它把「折叠」真的跑一遍。
    /// 外层 SettingsExpander 与模板里那个内层 Expander 都要压 —— 内层才是真身
    /// （模板里绑的是 TwoWay，它起手展开会把 true 反推回外层）。
    /// </para>
    /// </summary>
    private void CollapseEchoCave()
    {
        if (!EchoCaveExpander.IsExpanded) EchoCaveExpander.IsExpanded = true;
        EchoCaveExpander.IsExpanded = false;

        var inner = FindDescendant<Expander>(EchoCaveExpander);
        if (inner is not null)
        {
            if (!inner.IsExpanded) inner.IsExpanded = true;
            inner.IsExpanded = false;
        }
    }

    /// <summary>深度优先找一个后裔控件（模板里的内层控件用；找不到返回 null）。</summary>
    private static T? FindDescendant<T>(Visual root) where T : Visual
    {
        foreach (var child in root.GetVisualChildren())
        {
            if (child is T hit) return hit;
            var deeper = FindDescendant<T>(child);
            if (deeper is not null) return deeper;
        }
        return null;
    }

    /// <summary>
    /// 「投稿」弹层里的提交：走「提交软件」同一套自建服务（令牌在服务端）。
    /// 回执**就留在弹层里** —— 成功时给一句回执、停一下自己收起，失败时留着让用户看。
    /// </summary>
    private async void SubmitEchoCave_Click(object? sender, RoutedEventArgs e)
    {
        var text = EchoSubmitBox.Text?.Trim() ?? "";
        if (text.Length == 0)
        {
            ShowEchoSubmitStatus("还没写内容。");
            return;
        }

        EchoSubmitButton.IsEnabled = false;
        EchoSubmitButton.Content = "正在提交";
        ShowEchoSubmitStatus("正在提交…");

        // ⚠️ async void 里抛出去会直接带走进程；而且按钮会永远停在「正在提交」且不可点。
        //    异常必须在本地收口，复位交给 finally。
        bool ok;
        string message;
        try
        {
            (ok, message) = await Services.EchoCaveService.SubmitAsync(text);
        }
        catch (Exception ex)
        {
            AppLog.Exception("echo-cave", ex, "提交回声洞投稿");
            ok = false;
            message = "提交失败：" + ex.Message;
        }
        finally
        {
            EchoSubmitButton.IsEnabled = true;
            EchoSubmitButton.Content = "提交";
        }

        ShowEchoSubmitStatus(message);
        // 服务端还没接上（或网络被挡）时给条退路：去 GitHub 网页投。成功时不给，免得画蛇添足。
        EchoSubmitFallback.IsVisible = !ok;

        if (ok)
        {
            EchoSubmitBox.Text = "";

            // 回执看一眼够了就自己收（成功才收；失败留着，还能点兜底链接）。
            await Task.Delay(1800);
            if (EchoSubmitStatus.Text == message) EchoSubmitButton.Flyout?.Hide();
        }
    }

    private async void SubmitEchoCaveFallback_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (await EchoCave.OpenSubmitPage()) EchoSubmitButton.Flyout?.Hide();
            else ShowEchoSubmitStatus("无法打开浏览器，请手动访问 GitHub 投稿。");
        }
        catch (Exception ex)
        {
            AppLog.Catch("echo-cave", "打开回声洞网页投稿", ex);
        }
    }

    private void ShowEchoSubmitStatus(string message)
    {
        EchoSubmitStatus.Text = message;
        EchoSubmitStatus.IsVisible = true;
    }

    // ══════════════════════════════════════════════════════════════════
    //  小工具
    // ══════════════════════════════════════════════════════════════════

    /// <summary>取承载本页的外壳窗口（导航、法律条款都由它统一处理）。</summary>
    private ShellPage? Shell() => this.FindAncestorOfType<ShellPage>();

    private void Navigate(string tag)
    {
        try
        {
            Shell()?.NavigateTo(tag);
        }
        catch (Exception ex)
        {
            AppLog.Catch("about", "页面跳转｜目标=" + tag, ex, LogLevel.Debug);
        }
    }

    private static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppLog.Catch("about", "打开链接｜地址=" + url, ex, LogLevel.Debug);
        }
    }
}
