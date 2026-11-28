using System;
using System.Runtime.InteropServices;
using System.Threading;
using ClassSoftwareHub.Desktop.Core;
using ClassSoftwareHub.Desktop.Platform;

namespace ClassSoftwareHub.Desktop.Services;

/// <summary>
/// 放映时鼠标锁定（需求第 7 条，2026-11-28）。
///
/// <para>
/// ── 行为（用户拍板的版本）────────────────────────────────────────
/// 课件全屏放映时：把光标**钉在放映屏正中央**并隐藏；左键＝下一页（PageDown）；
/// 单击右键**放行**给 PowerPoint（右键菜单老师要用）；**3 秒内双击右键**或**单击中键**解锁，
/// 光标恢复自由移动与显示；放映中再**单击中键**重新锁定。功能默认关，设置页右侧标 NEW。
/// </para>
///
/// <para>
/// ── 为什么挂在 PptTouch 上 ──────────────────────────────────
/// 放映判定（<see cref="PptTouch.IsSlideshowShowing"/>）、放映窗句柄、低级鼠标钩子、
/// 送键（<c>VK_NEXT</c>）这里全都有现成的 —— 再起一套钩子只会互抢消息。
/// 所以本文件是 <c>PptTouch</c> 的 partial 部分，只加"锁/解锁 + 吞键策略"。
/// </para>
///
/// <para>
/// ── 事故防线（这类功能唯一可怕的地方就是"鼠标被锁死"）──────────
/// ① 退出放映 / 关功能 / <see cref="PptTouch.Stop"/> / 进程退出 → 立刻 <c>ClipCursor(原值)</c> + 恢复光标；
/// ② 光标隐藏用**计数配对**（<c>ShowCursor</c> 是内部计数器，少还原一次全屏就再没有光标）；
/// ③ 看门狗每 500ms 校一次：不在放映态却还锁着 → 强制解锁；放映态被系统重置 clip → 重新钉。
/// </para>
/// </summary>
public static partial class PptTouch
{
    /// <summary>配置缓存：钩子回调里绝不读盘（一次 IO 就可能把钩子拖到超时）。</summary>
    private static volatile bool _mouseLockEnabled;

    /// <summary>当前是否已锁定（光标钉在正中 + 已隐藏）。</summary>
    private static volatile bool _mouseLocked;

    /// <summary>本轮放映里用户主动解锁过：等中键再锁，别每 500ms 又锁回去。</summary>
    private static volatile bool _mouseLockReleased;

    /// <summary>上一次右键按下的 tick（判 3 秒双击）。</summary>
    private static long _lastRightDownTick;

    private static bool _clipSaved;
    private static PptTouchNative.RECT _savedClip;

    /// <summary>我们压了几次 ShowCursor(false) —— 必须按同样的次数还原。</summary>
    private static int _cursorHideDepth;

    private static Timer? _mouseLockWatch;
    private static long _lastRepinTick;

    private const int DoubleRightMs = 3000;
    private const int RepinMs = 500;

    /// <summary>设置里那个开关变了（或启动时读配置）：开=启动看门狗，关=立刻解锁。</summary>
    private static void SetMouseLockEnabled(bool on)
    {
        try
        {
            _mouseLockEnabled = on;
            if (!on)
            {
                UnlockCursor(force: true);
                _mouseLockWatch?.Dispose();
                _mouseLockWatch = null;
                return;
            }

            _mouseLockWatch ??= new Timer(_ => MouseLockWatchTick(), null, 500, 500);
            AppLog.Info("ppttouch", "放映鼠标锁定：已启用（放映时锁在屏幕正中，左键翻页）");
        }
        catch (Exception ex)
        {
            AppLog.Catch("ppttouch", "启用放映鼠标锁定", ex);
        }
    }

    /// <summary>看门狗：不在放映就解锁；在放映且未手动解锁就（重新）钉一次。</summary>
    private static void MouseLockWatchTick()
    {
        try
        {
            if (!_mouseLockEnabled) return;

            var hwnd = FindSlideshowWindow();
            if (hwnd == IntPtr.Zero)
            {
                // 放映结束了：解锁 + 清掉"本轮已解锁"标记，下一场放映重新自动锁
                if (_mouseLocked) UnlockCursor(force: false);
                _mouseLockReleased = false;
                _lastRightDownTick = 0;
                return;
            }

            if (_mouseLockReleased) return;

            // 有些窗口/系统事件会把 clip 清掉，这里按 500ms 的节奏补钉
            var now = Environment.TickCount64;
            if (!_mouseLocked || now - _lastRepinTick >= RepinMs)
            {
                _lastRepinTick = now;
                LockCursorToCenter(hwnd);
            }
        }
        catch (Exception ex)
        {
            AppLog.Catch("ppttouch", "鼠标锁定看门狗", ex, LogLevel.Debug);
        }
    }

    /// <summary>把光标钉在放映窗所在显示器的正中央并隐藏。</summary>
    private static void LockCursorToCenter(IntPtr slideshowHwnd)
    {
        try
        {
            if (!GetSlideshowCenter(slideshowHwnd, out var cx, out var cy)) return;

            if (!_clipSaved)
            {
                if (PptTouchNative.GetClipCursor(out _savedClip)) _clipSaved = true;
            }

            // 用 5x5 的小方块而不是 1 点：个别驱动对"零尺寸可移动区"处理不一样
            var rect = new PptTouchNative.RECT { Left = cx - 2, Top = cy - 2, Right = cx + 2, Bottom = cy + 2 };
            if (!PptTouchNative.ClipCursor(ref rect))
            {
                AppLog.Warning("ppttouch", "鼠标锁定失败（ClipCursor=" + Marshal.GetLastWin32Error() + "）——PPT 若以管理员运行，普通权限的程序锁不住它");
                return;
            }

            PptTouchNative.SetCursorPos(cx, cy);

            if (_cursorHideDepth == 0)
            {
                // ShowCursor 是计数器：压到返回负数为止，并记下压了几次
                var guard = 0;
                while (PptTouchNative.ShowCursor(false) >= 0 && guard++ < 16) _cursorHideDepth++;
                if (_cursorHideDepth == 0) _cursorHideDepth = 1;
            }

            if (!_mouseLocked)
            {
                _mouseLocked = true;
                AppLog.Info("ppttouch", "放映鼠标锁定：已锁在屏幕正中（左键翻页 / 双击右键或中键解锁）");
            }
        }
        catch (Exception ex)
        {
            AppLog.Catch("ppttouch", "锁定鼠标", ex);
        }
    }

    /// <summary>解锁：恢复原来的 clip 区域与光标显示。</summary>
    private static void UnlockCursor(bool force)
    {
        try
        {
            if (_clipSaved)
            {
                PptTouchNative.ClipCursor(ref _savedClip);
                _clipSaved = false;
            }
            else if (force)
            {
                PptTouchNative.ClipCursorAll(IntPtr.Zero);
            }

            while (_cursorHideDepth > 0)
            {
                PptTouchNative.ShowCursor(true);
                _cursorHideDepth--;
            }

            if (_mouseLocked)
            {
                _mouseLocked = false;
                AppLog.Info("ppttouch", "放映鼠标锁定：已解锁（光标恢复自由移动）");
            }
        }
        catch (Exception ex)
        {
            AppLog.Catch("ppttouch", "解锁鼠标", ex);
        }
    }

    /// <summary>放映窗所在显示器的正中央（物理像素）。</summary>
    private static bool GetSlideshowCenter(IntPtr hwnd, out int cx, out int cy)
    {
        cx = 0;
        cy = 0;
        try
        {
            var monitor = PptTouchNative.MonitorFromWindow(hwnd, PptTouchNative.MONITOR_DEFAULTTONEAREST);
            if (monitor != IntPtr.Zero)
            {
                var info = new PptTouchNative.MONITORINFO
                {
                    cbSize = Marshal.SizeOf<PptTouchNative.MONITORINFO>(),
                };
                if (PptTouchNative.GetMonitorInfo(monitor, ref info))
                {
                    cx = (info.rcMonitor.Left + info.rcMonitor.Right) / 2;
                    cy = (info.rcMonitor.Top + info.rcMonitor.Bottom) / 2;
                    return true;
                }
            }

            // 兜底：主屏
            cx = PptTouchNative.GetSystemMetrics(0) / 2;
            cy = PptTouchNative.GetSystemMetrics(1) / 2;
            return cx > 0 && cy > 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 钩子里的鼠标锁策略。返回 true = 这条消息已被吞掉，不再往下传。
    /// ⚠️ 只在"锁开着 + 放映窗是前台"时介入，其余情况一律放行，别影响日常用鼠标。
    /// </summary>
    private static bool HandleMouseLock(int msg, PptTouchNative.MSLLHOOKSTRUCT info)
    {
        if (!_mouseLockEnabled) return false;
        if (msg is not (PptTouchNative.WM_MOUSEMOVE
            or PptTouchNative.WM_LBUTTONDOWN or PptTouchNative.WM_LBUTTONUP
            or PptTouchNative.WM_RBUTTONDOWN or PptTouchNative.WM_RBUTTONUP
            or PptTouchNative.WM_MBUTTONDOWN or PptTouchNative.WM_MBUTTONUP)) return false;
        if (PptMenu.IsBlocking) return false;               // 我们自己的菜单开着时交回原逻辑

        if (!IsSlideshowForeground(out var slideshow, out _)) return false;

        // ── 中键：解锁 / 重新锁定（放映中随时可用）──
        if (msg == PptTouchNative.WM_MBUTTONDOWN)
        {
            if (_mouseLocked)
            {
                _mouseLockReleased = true;
                UnlockCursor(force: false);
                AppLog.Info("ppttouch", "中键：鼠标锁定已解除");
            }
            else
            {
                _mouseLockReleased = false;
                LockCursorToCenter(slideshow);
            }
            return true;
        }
        if (msg == PptTouchNative.WM_MBUTTONUP) return true;

        if (!_mouseLocked)
        {
            // 已解锁（或还没锁上）：右键照常给 PowerPoint
            return false;
        }

        // ── 锁定态：左键 = 下一页，右键单击放行 / 3 秒内双击解锁 ──
        switch (msg)
        {
            case PptTouchNative.WM_LBUTTONDOWN:
                SendKey(PptTouchNative.VK_NEXT);
                return true;

            case PptTouchNative.WM_LBUTTONUP:
                return true;

            case PptTouchNative.WM_RBUTTONDOWN:
                var now = Environment.TickCount64;
                if (_lastRightDownTick != 0 && now - _lastRightDownTick <= DoubleRightMs)
                {
                    _lastRightDownTick = 0;
                    _mouseLockReleased = true;
                    UnlockCursor(force: false);
                    AppLog.Info("ppttouch", "双击右键：鼠标锁定已解除");
                    return true;                 // 这一下也吞掉，免得解锁瞬间弹出 PowerPoint 菜单
                }
                _lastRightDownTick = now;
                return false;                    // 单击右键：放行，右键菜单照常用

            case PptTouchNative.WM_RBUTTONUP:
                return false;

            case PptTouchNative.WM_MOUSEMOVE:
                // ClipCursor 已经限制住移动了，这里只兜一次"位置被别的程序挪走"的情况
                if (GetSlideshowCenter(slideshow, out var cx, out var cy)
                    && (Math.Abs(info.pt.x - cx) > 2 || Math.Abs(info.pt.y - cy) > 2))
                    PptTouchNative.SetCursorPos(cx, cy);
                return false;
        }

        return false;
    }
}
