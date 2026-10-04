using System;
using Avalonia;
using Avalonia.Threading;
using ClassSoftwareHub.Desktop.Platform;
using ClassSoftwareHub.Desktop.Services;

namespace ClassSoftwareHub.Desktop.Views;

/// <summary>
/// 两个音量浮窗（主音量 + 合成器）的联动：**一起活着、一起收**，并且**按住边条不让它缩回去**。
///
/// ① 一起收：主音量浮窗和合成器浮窗是两个独立窗口，谁先收都会把另一个变成孤儿，所以统一收。
///
/// ② 按住边条：音量浮窗是**挨着边条长出来的一栏**，边条必须在（不然看着就是俩不相关的东西）。
///    但浮窗一显形就会抢焦点，边条一"失焦"就自己缩回去 —— 所以浮窗开着期间要一直压住自动收起。
///    <see cref="ToolSidebarWindow.SuppressAutoCollapse"/> 是有时效的（15 秒到点自己恢复，防"永远不收"），
///    这里用个慢速定时器**持续续上**，浮窗一关就放手。
///
/// ⚠️⚠️ 收窗判据：**看鼠标，不看焦点**（2026-10-02 实机改，这是一次真 bug 的修复）
///
///   旧实现：<c>Deactivated</c> → 等 220ms → 问 GetForegroundWindow 是不是自己 → 不是就收。
///   实机日志把它的下场记得清清楚楚（sidebar.log）：
///       09:30:31 点击「音量」模块 → 音量浮窗现在 开着
///       09:30:32 点击「音量」模块 → 音量浮窗现在 关着（关着且刚才是点击开，说明浮窗没能显示）
///   —— **弹出不到 1 秒就把自己收掉了**，用户看到的就是"点了没反应 / 闪一下就没"。
///   原因：Win7 上 <c>SetForegroundWindow</c> 对这种 Topmost 浮动小窗并不总是生效
///   （前台锁规则 + 边条自己也是 Topmost），浮窗压根没拿到前台 → "失焦"判据当场成立 → 自杀。
///
///   现在改成看**鼠标位置**：不依赖任何焦点语义。
///     · 用户拖着滑块时鼠标必然在窗口里 → 绝不会误收；
///     · 鼠标真的移开了，等 <see cref="CloseAfterMs"/> 才收 → 也正是 Win10/11 音量浮窗的行为；
///     · 鼠标在**侧边栏**上也算"还在自己家"（用户可能从浮窗移回边条点别的模块）。
/// </summary>
public static class VolumeFlyoutGroup
{
    /// <summary>多久看一次鼠标。</summary>
    private const int TickMs = 150;

    /// <summary>鼠标离开「浮窗 + 侧边栏」这个整体满这么久才收（给用户从浮窗挪到合成器的时间）。</summary>
    private const int CloseAfterMs = 900;

    private static DispatcherTimer? _watch;
    private static DispatcherTimer? _holdTimer;
    private static int _awayTicks;

    // ⚠️ 「钉住」机制整段删了（2026-10-03，用户原话：「音量调节浮窗不要有钉住功能要会消失」）：
    //    以前"拖过一次就钉住，鼠标移开也不收"—— 但用户的预期就是 Win10/11 那套：
    //    浮窗是临时面板，鼠标移开就该收，拖到哪里都一样。现在一律鼠标离开满
    //    <see cref="CloseAfterMs"/> 就收（Esc / 再点一次模块也一样）。

    /// <summary>主音量浮窗 / 合成器浮窗的 HWND（由各自窗口在配置时注册）。</summary>
    public static IntPtr MainHwnd { get; set; }
    public static IntPtr MixerHwnd { get; set; }

    /// <summary>
    /// 确保「鼠标离开就收起」的监视在跑。**显示浮窗时调一次**即可；重复调用安全（会重置计数）。
    /// </summary>
    public static void EnsureMouseWatch()
    {
        try
        {
            _watch ??= CreateWatch();
            _awayTicks = 0;
            _watch.Stop();
            _watch.Start();
        }
        catch
        {
        }
    }

    /// <summary>有浮窗开着了：把边条按住（持续续期，别让它自动缩回去）。</summary>
    public static void HoldSidebar()
    {
        try
        {
            ToolSidebarWindow.SuppressAutoCollapse = true;

            if (_holdTimer is null)
            {
                _holdTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };   // 比 SuppressAutoCollapse 的 15 秒寿命短得多
                _holdTimer.Tick += (_, _) => ToolSidebarWindow.SuppressAutoCollapse = true;
            }
            _holdTimer.Start();
        }
        catch
        {
        }
    }

    /// <summary>浮窗都关了：放开边条（之后它会照常走自己的自动收起）。</summary>
    public static void ReleaseSidebar()
    {
        try { _holdTimer?.Stop(); } catch { }
        try { ToolSidebarWindow.SuppressAutoCollapse = false; } catch { }
    }

    /// <summary>某个浮窗收完了叫一声：两个都收干净了才放开边条（滑出动画还得靠边条的位置），
    /// 并且顺手把边条也收回去（点音量时压住的那份自动收起，早就错过了它自己的失焦时机）。</summary>
    public static void OnAnyHidden()
    {
        if (VolumeWindow.IsVisible || VolumeMixerWindow.IsVisible) return;
        StopWatch();
        ReleaseSidebar();
        ToolSidebarWindow.CollapseAfterVolumeFlyoutsClosed();
    }

    /// <summary>把两个浮窗一起收掉（鼠标离开、Esc、边条隐藏等显式收起走这里）。</summary>
    public static void CloseAll()
    {
        ScreenCapture.Log($"VolumeFlyoutGroup.CloseAll（visible: 主={VolumeWindow.IsVisible} 合成器={VolumeMixerWindow.IsVisible}）");
        VolumeWindow.CloseIfOpen();
        VolumeMixerWindow.CloseIfOpen();

        // ⚠️ 两个都已经是"关"了（比如用户点了两次同一个模块），上面那两行就不会走到 OnAnyHidden，
        //    监视器会一直空转 —— 这里补一刀，保证它一定停得下来。
        if (!VolumeWindow.IsVisible && !VolumeMixerWindow.IsVisible) OnAnyHidden();
    }

    // ── 鼠标监视 ──────────────────────────────────────────────────────────

    private static DispatcherTimer CreateWatch()
    {
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(TickMs) };
        t.Tick += (_, _) => Tick();
        return t;
    }

    private static void Tick()
    {
        try
        {
            if (!VolumeWindow.IsVisible && !VolumeMixerWindow.IsVisible)
            {
                StopWatch();
                return;
            }

            // 正拖着滑块 / 正拖着窗口：用户操作中途绝不收窗
            if (VolumeWindow.IsDragging || VolumeMixerWindow.IsDragging)
            {
                _awayTicks = 0;
                return;
            }

            if (IsCursorOnOurSurfaces())
            {
                _awayTicks = 0;
                return;
            }

            _awayTicks++;
            ScreenCapture.Log($"鼠标监视：awayTicks={_awayTicks}（光标不在浮窗/边条上）");
            if (_awayTicks * TickMs >= CloseAfterMs) CloseAll();
        }
        catch
        {
        }
    }

    private static void StopWatch()
    {
        try { _watch?.Stop(); } catch { }
        _awayTicks = 0;
    }

    /// <summary>鼠标是不是还在「两个浮窗 + 侧边栏」这三个矩形里的任意一个里。</summary>
    private static bool IsCursorOnOurSurfaces()
    {
        if (!NativeMethodsShell.GetCursorPos(out var pt))
            return true;      // 问不到鼠标位置 → 当作"还在"，宁可不收也不要误收

        return Hit(VolumeWindow.CurrentRect, pt)
            || Hit(VolumeMixerWindow.CurrentRect, pt)
            || Hit(ToolSidebarWindow.CurrentRect, pt);
    }

    private static bool Hit(PixelRect? rect, NativeMethodsShell.POINT p)
        => rect is { } r
           && p.X >= r.X && p.X < r.X + r.Width
           && p.Y >= r.Y && p.Y < r.Y + r.Height;
}
