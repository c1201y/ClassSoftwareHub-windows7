using System;
using ClassSoftwareHub.Desktop.Services;

namespace ClassSoftwareHub.Desktop.Views.Oobe;

/// <summary>
/// 页面可以拦住「下一步」（例如用户协议还没勾选）—— 导航壳在前进前问它。
/// 页面状态变了要调 <see cref="OobeSession.RefreshChrome"/>，按钮才会跟着变灰/恢复。
/// </summary>
public interface IOobeStepGuard
{
    bool CanAdvance { get; }
}

/// <summary>
/// 一次向导运行的全部上下文：步骤表、当前位置、草稿，以及前进/后退/收尾（自上游 dv1.2.0-insider1.8 移植）。
///
/// <para>
/// 照 ClassIsland 的 <c>WelcomeWindow</c> 组织：页面类型排成一条线，前进/后退就是在下标上 ±1。
/// 原版用 Frame 的 <c>NavigationCacheMode="Required"</c> 缓存页面实例；Avalonia 没有同一个东西，
/// 这里改成窗口内 <c>Dictionary&lt;Type, Control&gt;</c> 手动缓存 —— 语义一致：往回翻时用户刚填的值还在。
/// </para>
///
/// <para>
/// 页面从这里拿草稿、发导航请求，不直接碰窗口。窗口销毁时置空 <see cref="Current"/>。
/// </para>
/// </summary>
public sealed class OobeSession
{
    private static readonly Type[] Steps =
    {
        typeof(OobeWelcomePage),
        typeof(OobeAgreementPage),
        typeof(OobeUpdatePage),
        typeof(OobeAppearancePage),
        typeof(OobeSidebarPage),
        typeof(OobeSidebarPositionPage),
        typeof(OobeSidebarSizePage),
        typeof(OobeScreenshotPage),
        typeof(OobeStartupPage),
        typeof(OobeDownloadsPage),
        typeof(OobeAiPage),
        typeof(OobeFinishPage),
    };

    public static OobeSession? Current { get; private set; }

    private readonly WelcomeWindow _window;

    private OobeSession(WelcomeWindow window, OobeDraft draft)
    {
        _window = window;
        Draft = draft;
    }

    public OobeDraft Draft { get; }

    /// <summary>窗口第一次显示出来时触发 —— 开场动画等它，避免在不可见时就播掉了。</summary>
    public event EventHandler? Started;

    public int Index { get; private set; }

    public bool IsLast => Index >= Steps.Length - 1;

    public static OobeSession Attach(WelcomeWindow window, OobeDraft draft) =>
        Current = new OobeSession(window, draft);

    public static void Detach() => Current = null;

    /// <summary>
    /// 构造窗口时就导航到第一页：页面与开场控件的构造放在窗口显示之前，
    /// 显示出来的第一帧就有内容，不会先黑一片再慢慢搭。动画仍等 <see cref="Play"/>。
    /// </summary>
    public void Prepare() => _window.NavigateToPage(Steps[0], forward: true, animate: false);

    /// <summary>窗口真的显示之后才调 —— 构造期间播动画，用户看不到。</summary>
    public void Play() => Started?.Invoke(this, EventArgs.Empty);

    public void Next()
    {
        if (!IsLast) GoTo(Index + 1, forward: true);
    }

    public void Back()
    {
        if (Index > 0) GoTo(Index - 1, forward: false);
    }

    /// <summary>完成页的第一个轮播页再往回，就是退回上一步。</summary>
    public void GoTo(int index, bool forward)
    {
        index = Math.Clamp(index, 0, Steps.Length - 1);
        Index = index;
        _window.NavigateToPage(Steps[index], forward, animate: true);
    }

    /// <summary>外观预览：只改本窗显示，收尾时才写进设置。</summary>
    public void PreviewTheme(string theme) => _window.PreviewTheme(theme);

    /// <summary>背景材质预览：只改本窗显示，收尾时才写进设置。</summary>
    public void PreviewBackdrop(string kind) => _window.PreviewBackdrop(kind);

    /// <summary>向导窗口句柄 —— 目录选择器之类要它做 owner。</summary>
    public IntPtr WindowHandle => _window.WindowHandle;

    /// <summary>页内状态变了（比如完成页翻到最后一屏），让底部导航条重读一次按钮文案。</summary>
    public void RefreshChrome() => _window.UpdateNav();

    public void Finish() => _window.FinishOobe();

    public void FinishAndNavigate(string tag) => _window.FinishAndNavigate(tag);
}
