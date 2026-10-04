using System;
using Avalonia.Controls;
using FluentAvalonia.UI.Controls;

namespace ClassSoftwareHub.Desktop.Platform;

/// <summary>
/// 页面基类。对应 WinUI 的 <c>Microsoft.UI.Xaml.Controls.Page</c>。
///
/// ⛔ 为什么要有它：Avalonia 没有 Page，页面一律是 <c>UserControl</c>；
///    而原版每个页面都重写了 <c>OnNavigatedTo / OnNavigatedFrom</c>，
///    这套生命周期必须补回来（很多页面在 OnNavigatedTo 里读参数、起计时器、刷数据）。
///
/// 调用链：<see cref="Navigation.Attach"/> 会把 <see cref="Frame.Navigated"/> 接上，
///         导航完成时对**新页**调 <see cref="OnNavigatedTo"/>、对**旧页**调 <see cref="OnNavigatedFrom"/>。
/// </summary>
public abstract class PageBase : UserControl
{
    /// <summary>进入页面。<paramref name="parameter"/> = <c>Frame.Navigate(type, parameter)</c> 的第二个参数。</summary>
    public virtual void OnNavigatedTo(object? parameter) { }

    /// <summary>离开页面（页面即将被丢弃）。</summary>
    public virtual void OnNavigatedFrom() { }
}

/// <summary>
/// 导航助手：把 FA <see cref="Frame"/> 的事件桥接到 <see cref="PageBase"/> 的生命周期。
///
/// ⛔ FA 的 <c>Frame</c> 与 WinUI 的 <c>Frame</c> 同签名
///    （<c>Navigate(Type)</c> / <c>Navigate(Type, object)</c> / <c>BackStack</c> / <c>CanGoBack</c> / <c>GoBack()</c>），
///    所以**不要**自己发明导航控件，直接用 FA 的 Frame。
/// </summary>
public static class Navigation
{
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Frame, object> Hooked = new();

    /// <summary>
    /// 接管一个 Frame。幂等，可重复调用。
    /// ⚠️ 必须在第一次 Navigate 之前调用，否则首页的 OnNavigatedTo 会漏掉。
    /// </summary>
    public static void Attach(Frame frame)
    {
        if (Hooked.TryGetValue(frame, out _)) return;
        Hooked.Add(frame, new object());

        var previous = new StrongBox<PageBase?>();

        frame.Navigated += (_, e) =>
        {
            // 先把上一页收尾（FA 的 Frame 在 CacheSize=0 时会把旧页实例丢掉）
            previous.Value?.OnNavigatedFrom();

            if (e.Content is PageBase page)
            {
                previous.Value = page;
                try
                {
                    page.OnNavigatedTo(e.Parameter);
                }
                catch (Exception ex)
                {
                    Services.ScreenCapture.Log("[nav] OnNavigatedTo 失败: " + ex);
                }
            }
            else
            {
                previous.Value = null;
            }
        };
    }

    private sealed class StrongBox<T>
    {
        public T? Value;
    }
}
