using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Styling;

namespace ClassSoftwareHub.Desktop.Platform;

/// <summary>
/// 主题（深浅色）分发。对应 WinUI 原版的 <c>Services/ThemeHost.cs</c>。
///
/// 原版为什么要它：WinUI3 的 <c>RequestedTheme</c> **只作用于设它的那棵树**，
/// 主窗口设了不代表别的窗口跟着变，于是侧边栏 / 工具浮窗 / 截图窗会一直跟着系统主题走。
///
/// Avalonia 的对应机制是 <c>ThemeVariant</c>，同样**按树作用域生效**（<c>ThemeVariantScope</c>），
/// 所以这个「登记所有窗口根 → 改设置时统一刷新」的模式必须原样保留，语义完全一致。
/// </summary>
public static class ThemeCompat
{
    // 两套主题，别搞混（上游 1.8 定稿的口径）：
    //   · 应用主题（设置里的「颜色模式」）：**其余所有窗口都跟这个** —— 主界面、学科工具窗、
    //     音量 / 亮度浮窗、截图窗、条款与声明、日志详细视图、反馈引导窗……
    //   · 外部组件主题（「桌面悬浮窗单独设置外观」+「外部组件外观」）：**只作用于桌面悬浮件** ——
    //     ① 「常用工具」浮窗（随机抽号 / 课堂计时 / 秒表计时 / 全屏时钟）；② 屏幕边缘的工具侧边栏。
    //   其余窗口不能登记成 external，否则分体一开会连文档窗都跟着变深。
    private static readonly List<(WeakReference<StyledElement> Ref, bool External)> Roots = new();

    /// <summary>设置里的主题（system | light | dark）映射成 Avalonia 的 ThemeVariant。</summary>
    public static ThemeVariant Map(string? theme) => (theme ?? "").Trim().ToLowerInvariant() switch
    {
        "light" => ThemeVariant.Light,
        "dark" => ThemeVariant.Dark,
        _ => ThemeVariant.Default
    };

    /// <summary>这一类根该用哪个外观：external = 桌面悬浮件（分体开着跟「外部组件外观」，没开跟主界面）；否则跟主界面。</summary>
    private static ThemeVariant VariantFor(bool external)
    {
        try
        {
            var s = App.Settings.Current;
            if (!external) return Map(s.Theme);

            var ext = s.SplitTheme ? s.ExternalTheme : s.Theme;
            return Map(string.IsNullOrWhiteSpace(ext) ? "system" : ext);
        }
        catch { return ThemeVariant.Default; }
    }

    /// <summary>
    /// 把当前设置的主题应用到某个窗口/控件，并登记（之后改设置会一起变）。
    /// 对应原版 <c>ThemeHost.Apply(FrameworkElement root, bool external)</c>。
    /// <paramref name="external"/> **只对桌面悬浮件传 true**（常用工具浮窗 / 侧边栏），
    ///    其余窗口一律用默认值 —— 让它们跟应用主题走。
    /// </summary>
    public static void Apply(StyledElement root, bool external = false)
    {
        try
        {
            ApplyTheme(root, VariantFor(external));

            Roots.RemoveAll(r => !r.Ref.TryGetTarget(out var t) || t is null);
            Roots.Add((new WeakReference<StyledElement>(root), external));
        }
        catch (Exception ex)
        {
            Services.ScreenCapture.Log("应用窗口主题失败: " + ex.Message);
        }
    }

    /// <summary>
    /// 设置变了：所有登记过的根一起换 —— **各回各的作用域**（悬浮件跟分体设置、其余跟主界面）。
    /// 对应原版 <c>ThemeHost.Notify()</c>。
    /// </summary>
    public static void Notify()
    {
        var alive = new List<(WeakReference<StyledElement> Ref, bool External)>();

        foreach (var r in Roots)
        {
            try
            {
                if (r.Ref.TryGetTarget(out var root) && root is not null)
                {
                    ApplyTheme(root, VariantFor(r.External));
                    alive.Add(r);
                }
            }
            catch { }
        }

        Roots.Clear();
        Roots.AddRange(alive);
    }

    /// <summary>
    /// Avalonia 里 <c>RequestedThemeVariant</c> **不在 <c>StyledElement</c> 上**
    ///    （核验：只有 <c>Application</c> / <c>TopLevel</c>（含 Window）/ <c>ThemeVariantScope</c> 有）。
    ///    所以这里按类型分派；传进来的若是普通控件，就顺着逻辑树往上找到它所在的 TopLevel 再设。
    /// </summary>
    private static void ApplyTheme(StyledElement root, ThemeVariant variant)
    {
        switch (root)
        {
            case ThemeVariantScope scope:
                scope.RequestedThemeVariant = variant;
                return;

            case TopLevel top:
                top.RequestedThemeVariant = variant;
                return;
        }

        // 普通控件：主题按树作用域生效，找到所属 TopLevel 设上去即可
        var current = root;
        while (current is not null)
        {
            if (current is TopLevel host)
            {
                host.RequestedThemeVariant = variant;
                return;
            }
            current = current.Parent;
        }
    }
}
