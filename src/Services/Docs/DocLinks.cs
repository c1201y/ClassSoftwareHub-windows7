using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using ClassSoftwareHub.Desktop.Core;

namespace ClassSoftwareHub.Desktop.Services.Docs;

/// <summary>
/// 文章里点链接时走这里（需求 15 的写作口径：正文可以带超链接）。
///
/// <para>
/// 三种去向：
/// <list type="bullet">
///   <item><c>csh://…</c> —— 应用自己的页面 / 工具 / 设置 / 另一篇文章，交给主窗口统一处理；</item>
///   <item><c>ms-settings:…</c> —— 系统设置页，同样交给主窗口（它认得这个前缀）；</item>
///   <item><c>http(s) / mailto</c> —— 系统浏览器 / 邮件应用。</item>
/// </list>
/// </para>
/// <para>
/// 认不出的地址只记日志、不弹窗 —— 文章可能比应用更新，一条坏链接不该打断阅读。
/// </para>
/// <para>
/// 移植说明：原版用 WinRT 的 <c>Windows.System.Launcher.LaunchUriAsync</c>；
/// Avalonia 的等价物是 <c>TopLevel.Launcher.LaunchUriAsync</c>（Windows 后端内部就是
/// ShellExecute，<c>mailto:</c> 照样能拉起邮件应用）。这里取主窗口当 TopLevel ——
/// 它一定存在且与调用者的窗口一致，不必再把控件实例一路传下来。
/// </para>
/// </summary>
public static class DocLinks
{
    /// <summary>⛔ 只在 UI 线程调用（链接点击事件本身就在 UI 线程）。</summary>
    public static async Task OpenAsync(string? url)
    {
        var text = (url ?? "").Trim();
        if (text.Length == 0) return;

        if (text.StartsWith("csh://", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith(AppUri.SystemSchemePrefix, StringComparison.OrdinalIgnoreCase))
        {
            if (App.MainWindow is { } window) await window.HandleAppUriAsync(text);
            return;
        }

        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri)) return;
        if (uri.Scheme is not ("http" or "https" or "mailto")) return;

        try
        {
            var top = App.MainWindow as TopLevel;
            if (top is null)
            {
                AppLog.Warn("docs", "没有可用的窗口，链接未打开：" + text);
                return;
            }

            if (!await top.Launcher.LaunchUriAsync(uri))
                AppLog.Warn("docs", "系统没有打开链接：" + text);
        }
        catch (Exception ex)
        {
            AppLog.Warn("docs", $"打开链接失败（{text}）：{ex.Message}");
        }
    }
}
