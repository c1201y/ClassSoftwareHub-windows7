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
    // ⚠️ 每个登记的根都要带**作用域**（2026-10-03 修，用户反馈「开启外部组件单独设置颜色」后
    //    主界面文字全变白看不清）：以前所有根共用一个 CurrentSetting() —— 开了分体后
    //    Notify() 会把**外部组件**的外观算给**所有**登记过的根，连主窗口的 RootGrid 也一起被
    //    染成深色（背景还是浅色的玻璃 → 白底白字）。现在主窗口 / 音量浮窗登记为 main 作用域，
    //    永远跟「颜色模式」走；侧边栏 / 工具面板 / 截图窗登记为 external，跟分体设置走。
    private static readonly List<(WeakReference<StyledElement> Ref, bool Main)> Roots = new();

    /// <summary>设置里的主题（system | light | dark）映射成 Avalonia 的 ThemeVariant。</summary>
    public static ThemeVariant Map(string? theme) => (theme ?? "").Trim().ToLowerInvariant() switch
    {
        "light" => ThemeVariant.Light,
        "dark" => ThemeVariant.Dark,
        _ => ThemeVariant.Default
    };

    /// <summary>这一类根该用哪个外观：main = 主界面「颜色模式」；external = 分体开着跟「外部组件外观」，没开跟主界面。</summary>
    private static ThemeVariant VariantFor(bool mainScope)
    {
        try
        {
            var s = App.Settings.Current;
            if (mainScope) return Map(s.Theme);

            var ext = s.SplitTheme ? s.ExternalTheme : s.Theme;
            return Map(string.IsNullOrWhiteSpace(ext) ? "system" : ext);
        }
        catch { return ThemeVariant.Default; }
    }

    /// <summary>
    /// 把当前设置的主题应用到某个窗口/控件，并登记（之后改设置会一起变）。
    /// 对应原版 <c>ThemeHost.Apply(FrameworkElement root)</c>。
    /// ⚠️ <paramref name="mainScope"/>：主窗口 / 音量·亮度·合成器这些小浮窗传 **true**
    ///    （永远跟主界面的颜色模式走）；侧边栏 / 常用工具面板 / 截图窗不传（跟外部组件设置走）。
    /// </summary>
    public static void Apply(StyledElement root, bool mainScope = false)
    {
        try
        {
            ApplyTheme(root, VariantFor(mainScope));

            Roots.RemoveAll(r => !r.Ref.TryGetTarget(out var t) || t is null);
            Roots.Add((new WeakReference<StyledElement>(root), mainScope));
        }
        catch (Exception ex)
        {
            Services.ScreenCapture.Log("应用窗口主题失败: " + ex.Message);
        }
    }

    /// <summary>
    /// 设置变了：所有登记过的根一起换 —— **各回各的作用域**（main 跟主界面、external 跟分体设置）。
    /// 对应原版 <c>ThemeHost.Notify()</c>。
    /// </summary>
    public static void Notify()
    {
        var alive = new List<(WeakReference<StyledElement> Ref, bool Main)>();

        foreach (var r in Roots)
        {
            try
            {
                if (r.Ref.TryGetTarget(out var root) && root is not null)
                {
                    ApplyTheme(root, VariantFor(r.Main));
                    alive.Add(r);
                }
            }
            catch { }
        }

        Roots.Clear();
        Roots.AddRange(alive);
    }

    /// <summary>
    /// ⚠️ Avalonia 里 <c>RequestedThemeVariant</c> **不在 <c>StyledElement</c> 上**
    ///    （实测核验：只有 <c>Application</c> / <c>TopLevel</c>（含 Window）/ <c>ThemeVariantScope</c> 有）。
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
