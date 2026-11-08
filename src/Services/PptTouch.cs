using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using ClassSoftwareHub.Desktop.Core;
using ClassSoftwareHub.Desktop.Platform;

namespace ClassSoftwareHub.Desktop.Services;

/// <summary>
/// 「PowerPoint 触控优化」的本地存档（%LOCALAPPDATA%\ClassSoftwareHub\ppt-touch.json）。
///
/// <para>
/// ⚠️ 单独一份文件、不和 settings.json 混：这功能将来整个不要了，删掉本文件与
/// <see cref="PptTouch"/> / <c>PptTouchPage</c> 即可，不动用户其它设置。
/// </para>
/// </summary>
public sealed class PptTouchConfig
{
    /// <summary>
    /// 存档路径。**必须与上游逐字节相同**（<c>%LOCALAPPDATA%\ClassSoftwareHub\ppt-touch.json</c>）——
    /// 本仓库的 <see cref="AppPaths.DataDir"/> 正好就是这一层目录（见 Core/ShellConfig.cs），
    /// 所以走统一入口，不再自己拼一遍 LocalApplicationData。
    /// </summary>
    public static readonly string StorePath = Path.Combine(AppPaths.DataDir, "ppt-touch.json");

    /// <summary>总开关。默认**关** —— 实验性功能，改的是全局输入行为。</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// 长按弹出本程序自己的菜单，替代 PowerPoint 原生那个（2026-11-03）。
    /// 默认**开**：总开关本身就是用户主动打开的，这一项再默认关就没意义了。
    /// </summary>
    public bool CustomMenu { get; set; } = true;

    /// <summary>
    /// 额外菜单项的 id 清单（<see cref="PptMenu.Extras"/> 里那些），默认一个都不放 ——
    /// 放映时最常用的就那几项，其余由用户自己按需加。
    /// </summary>
    public List<string> MenuExtras { get; set; } = new();

    public static PptTouchConfig Load()
    {
        try
        {
            if (File.Exists(StorePath))
                return JsonSerializer.Deserialize<PptTouchConfig>(File.ReadAllText(StorePath))
                       ?? new PptTouchConfig();
        }
        catch { /* 存档损坏就退回默认值 */ }

        return new PptTouchConfig();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
            File.WriteAllText(StorePath, JsonSerializer.Serialize(this));
        }
        catch (Exception ex)
        {
            AppLog.Warning("ppttouch", "保存设置失败：" + ex.Message);
        }
    }
}

/// <summary>
/// PowerPoint 幻灯片放映的「轻点翻页」（需求清单第 2 条，2026-11-01）。
///
/// <para>
/// 来由：教室里用一体机放 PPT，老师习惯像 WPS 那样轻点一下翻页，而 PowerPoint 放映时
/// 只认滑动。上游 PowerPoint-Touch-Assist（CC0）的思路是「轻点就补一个空格键」，
/// 但那样连点视频、音频也会翻页（它自己的 issue #5：「触屏点击视频、音频进行播放时也会翻页」），
/// 媒体根本暂停不下来 —— 需求点名不要这个毛病。
/// </para>
///
/// <para>
/// 现在处理三种手势（2026-11-03 定）：
/// 轻点 → 在落点补一次**原地单击**，让 PowerPoint 自己判断点到了什么
///        （空白处翻页、视频暂停、链接跳转；补空格那套会让媒体点不动，2026-11-03 改掉）；
/// 上下滑 → 补 PageDown / PageUp（对齐 WPS 演示的上下滑换页）；
/// 长按并松手 → 弹出**本程序自己的菜单**（<see cref="PptMenu"/>），取代 PowerPoint 原生的右键菜单；
/// 原触摸消息一律照常放行，不拦截。
/// </para>
///
/// <para>
/// 识别方式：WH_MOUSE_LL 全局鼠标钩子 + 只处理触屏/笔合成出来的鼠标消息
/// （<c>dwExtraInfo</c> 的低 24 位是 0xFF5157），并按「按下与抬起的位移、时长」区分轻点与滑动。
/// 钩子跑在独立线程的消息循环里 —— ⛔ 别放到 UI 线程，钩子回调卡住会拖慢整机输入。
/// </para>
///
/// <para>
/// ⚠️ 移植说明（WinUI → Avalonia）：本文件本来就是**纯 Win32**，与 UI 框架无关，
/// 所以逐行直译；唯一改动的两处是
/// ① 原来的内部类 <c>Native</c> 搬到 <see cref="PptTouchNative"/>（PORTING.md 的 P/Invoke 归口要求）；
/// ② <c>GetDpiForWindow</c> 是 Win10 1607+ 的导出，Win7 上不存在，调用点加了 try/catch。
/// 钩子线程、消息循环、<c>volatile</c> 字段与 <c>Interlocked</c> 的时序一个字没动 ——
/// 那是防重入、防卡键的核心，改了会表现为「翻页按一下动两页」或「按键卡住」。
/// </para>
/// </summary>
public static class PptTouch
{
    // ── 判定参数 ──────────────────────────────────────────────

    /// <summary>允许的手指抖动，单位是**DIP**，按放映窗口所在屏幕的缩放换算成像素。</summary>
    private const double TapMaxDip = 16;

    /// <summary>
    /// 光标离目标点多近就算「已经落位」，可以原地点击而不必先移动（像素）。
    ///
    /// <para>
    /// 2026-11-03 加。轻点改成补「原地单击」之后，最怕的就是指针跳一下。
    /// UU 远程本来就把光标同步到手指位置，所以大多数时候距离都是 0 ——
    /// 只要在这个容差内就不发移动，指针一动不动。
    /// </para>
    /// </summary>
    private const int CursorNearPx = 32;

    /// <summary>
    /// 滑动翻页的最小竖直位移（DIP）。2026-11-02 加：WPS 演示里上下滑也能换页，
    /// 这里补同样的手感 —— 手指往上滑下一页、往下滑上一页。
    /// </summary>
    private const double SwipeMinDip = 60;

    /// <summary>
    /// 轻点的时长上限：按下到抬起超过这么久，就不当成轻点。
    ///
    /// <para>
    /// 2026-11-03 定。原来这个数还兼当「长按」的门槛（长按补右键菜单），
    /// 那条路径折腾一晚上没走通（PowerPoint 长按会进激光笔，进去之后注入的右键一律被吞），
    /// 现在长按改由本程序自己弹菜单（<see cref="PptMenu"/>），它只负责把「轻点」摘出去。
    /// </para>
    /// <para>
    /// ⚠️ 不能删掉这个上限：去掉之后「按住两秒再松手」会被当成轻点补一个空格，
    /// 变成长按翻页 —— 那不是任何一方想要的行为。
    /// </para>
    /// </summary>
    private const long TapMaxMs = 400;

    // ── 长按弹菜单（2026-11-03）──────────────────────────────────

    /// <summary>按下超过这么久才可能算长按。和 <see cref="TapMaxMs"/> 之间留一档空隙，两边都不抢。</summary>
    private const long LongPressMinMs = 450;

    /// <summary>
    /// 长按的最长有效时长。按住超过这么久还不松手，就不再当长按 ——
    /// 那是把手指杵在屏幕上没动，弹菜单反而碍事（也避免误把「慢慢挪」判成长按）。
    /// </summary>
    private const long LongPressMaxMs = 2500;

    /// <summary>
    /// 长按允许的抖动（DIP）。比轻点的 <see cref="TapMaxDip"/> 松得多：
    /// 按住不放的时候手指本来就会有位移，远程触屏还会让光标慢慢漂。
    /// </summary>
    private const double LongPressMaxDip = 48;



    // ── 运行状态 ──────────────────────────────────────────────

    private static Thread? _thread;
    private static uint _threadId;
    private static IntPtr _hook;
    private static PptTouchNative.HookProc? _hookProc;
    private static volatile bool _active;
    private static long _taps;

    /// <summary>
    /// 「长按弹本程序菜单」开着吗。**缓存**，启动时读一次 —— 钩子回调里绝不能去读配置文件，
    /// 一次磁盘 IO 就能让全机输入卡一下（钩子超时 300ms 系统就把这次输入丢掉了）。
    /// </summary>
    private static volatile bool _customMenu = true;

    /// <summary>上次记「前台不是放映窗口」诊断日志的时刻，用来限频（2026-11-02）。</summary>
    private static long _lastDiagTick;

    /// <summary>
    /// <c>GetDpiForWindow</c> 的探测结果：0 = 还没问过，1 = 可用，-1 = 该系统没有这个导出（Win7）。
    /// 只在钩子线程读写，不需要同步。
    /// </summary>
    private static int _dpiProbe;

    /// <summary>
    /// 上次收到触摸原始输入的时刻（<see cref="Environment.TickCount"/>）。
    /// 2026-11-02 加：这台机器的触屏驱动把手指当普通鼠标上报（extraInfo=0x0），
    /// 光看鼠标消息分不出手指和鼠标，靠它来补。
    /// </summary>
    private static volatile int _lastTouchTick;

    /// <summary>收触摸 Raw Input 的 message-only 窗口。</summary>
    private static IntPtr _touchHwnd;

    /// <summary>窗口过程委托要存字段，否则被 GC 掉之后窗口一分消息就崩。</summary>
    private static PptTouchNative.WndProcDelegate? _touchWndProc;

    /// <summary>窗口类名在非托管内存里的指针（RegisterClassEx 之后必须一直有效）。</summary>
    private static IntPtr _touchClassName;



    // 当前这次按下的记录（只在钩子线程读写）
    private static bool _tracking;
    private static int _startX;
    private static int _startY;
    private static long _startTick;
    private static double _scale;

    /// <summary>钩子是否已装上（页面用它显示运行状态）。</summary>
    public static bool IsActive => _active;

    /// <summary>本次运行期间由轻点触发的翻页次数。</summary>
    public static long Taps => Interlocked.Read(ref _taps);

    /// <summary>当前是不是有 PowerPoint 在全屏放映（页面定时刷新状态用）。</summary>
    public static bool IsSlideshowShowing() => FindSlideshowWindow() != IntPtr.Zero;

    /// <summary>
    /// 找 PowerPoint 的放映窗口，**不要求它是前台**。
    ///
    /// <para>
    /// 2026-11-02 修：原来这里用 <c>GetForegroundWindow()</c> 判断，可用户必须切到本程序
    /// 才能看到这行状态 —— 一切过来 PPT 就丢了前台，于是永远显示「未检测到放映」。
    /// 改成枚举所有可见顶层窗口找 <c>screenClass</c>（PowerPoint 放映窗口的类名）。
    /// </para>
    /// <para>
    /// ⚠️ 枚举回调不能抛异常（会穿过 native 边界），一律就地吞掉。
    /// </para>
    /// </summary>
    private static IntPtr FindSlideshowWindow()
    {
        var found = IntPtr.Zero;

        try
        {
            PptTouchNative.EnumWindows((hwnd, _) =>
            {
                try
                {
                    if (!PptTouchNative.IsWindowVisible(hwnd)) return true;

                    var cls = new StringBuilder(64);
                    if (PptTouchNative.GetClassName(hwnd, cls, cls.Capacity) == 0) return true;
                    if (!string.Equals(cls.ToString(), "screenClass", StringComparison.OrdinalIgnoreCase))
                        return true;

                    PptTouchNative.GetWindowThreadProcessId(hwnd, out var pid);
                    if (!IsPowerPointProcess(pid)) return true;

                    found = hwnd;
                    return false;   // 找到了，停止枚举
                }
                catch
                {
                    return true;
                }
            }, IntPtr.Zero);
        }
        catch (Exception ex)
        {
            AppLog.Exception("ppttouch", ex, "枚举 PowerPoint 放映窗口");
        }

        return found;
    }

    /// <summary>
    /// 诊断：把当前所有可见的 PowerPoint 窗口类名写进日志（30 秒最多一次）。
    /// 2026-11-02 加 —— 状态显示「未检测到放映」时，日志里能直接看到 PPT 现在是什么窗口，
    /// 省得靠猜（放映窗口是 screenClass，编辑窗口是 PPTFrameClass）。
    /// </summary>
    public static void DescribePowerPointWindows()
    {
        var now = Environment.TickCount64;
        if (now - _lastDiagTick < 30_000) return;
        _lastDiagTick = now;

        var list = new List<string>();

        try
        {
            PptTouchNative.EnumWindows((hwnd, _) =>
            {
                try
                {
                    if (!PptTouchNative.IsWindowVisible(hwnd)) return true;

                    PptTouchNative.GetWindowThreadProcessId(hwnd, out var pid);
                    if (!IsPowerPointProcess(pid)) return true;

                    var cls = new StringBuilder(64);
                    PptTouchNative.GetClassName(hwnd, cls, cls.Capacity);
                    PptTouchNative.GetWindowRect(hwnd, out var r);
                    list.Add($"{cls}（{r.Right - r.Left}×{r.Bottom - r.Top}）");
                }
                catch { }

                return true;
            }, IntPtr.Zero);
        }
        catch { }

        AppLog.Info("ppttouch", list.Count == 0
            ? "未检测到放映：当前没有可见的 PowerPoint 窗口"
            : "未检测到放映：现有 PowerPoint 窗口 " + string.Join("、", list));
    }

    /// <summary>按存档里的开关启动。已开着就什么都不做。</summary>
    public static void Start()
    {
        if (_active) return;

        // 上一次启动失败留下的死线程不算「已经在跑」，重试时要能重新起一个
        if (_thread is { IsAlive: true }) return;
        _thread = null;

        var config = PptTouchConfig.Load();
        _customMenu = config.CustomMenu;
        if (!config.Enabled) return;

        var thread = new Thread(HookThread)
        {
            IsBackground = true,
            Name = "ppttouch-hook",
        };
        _thread = thread;
        thread.Start();

        // 等钩子真正装上再返回：页面点完开关马上读状态，不能还停在「没开」
        // （装失败时线程会自己退出，这里也就不会再等满 1 秒）
        for (var i = 0; i < 100 && thread.IsAlive && !_active; i++) Thread.Sleep(10);

        // 同时起「放映指针类型」轮询（自上游 dv1.2.0）：用 Office COM 读真实指针状态，
        // 这样才能知道用户是不是切到了笔 / 荧光笔 —— 墨迹模式下手指是在画线，
        // 位移轻松越过滑动阈值，光看鼠标消息根本分辨不出来。
        try { PptPointerState.Start(); } catch (Exception ex) { AppLog.Catch("ppttouch", "启动指针状态轮询", ex); }
    }

    /// <summary>卸载钩子。没在跑就直接返回。</summary>
    public static void Stop()
    {
        try { PptPointerState.Stop(); } catch { /* 停不下来不影响卸载钩子 */ }

        var thread = _thread;
        if (thread is null) return;

        var id = _threadId;
        if (id != 0) PptTouchNative.PostThreadMessage(id, PptTouchNative.WM_QUIT, IntPtr.Zero, IntPtr.Zero);

        thread.Join(1000);

        _thread = null;
        _threadId = 0;
        _tracking = false;
    }

    /// <summary>
    /// 设置页改完开关后叫一声：让「长按弹菜单」的判定立刻跟上，不必重装钩子。
    /// 钩子回调里不读配置文件（一次磁盘 IO 就够拖出钩子超时），只认这个缓存值。
    /// </summary>
    public static void ApplyConfig()
    {
        _customMenu = PptTouchConfig.Load().CustomMenu;
    }

    /// <summary>当前 PowerPoint 放映窗口的句柄；没有放映就返回 <see cref="IntPtr.Zero"/>。</summary>
    public static IntPtr SlideshowWindow() => FindSlideshowWindow();

    /// <summary>
    /// 把某个窗口强行拉到前台。
    ///
    /// <para>
    /// 为什么不能直接 <c>SetForegroundWindow</c>：系统的前台锁只允许「当前前台进程」直接切，
    /// 否则调用会被忽略、只在任务栏闪一下。菜单窗口自己就是前台，从它这里切回 PowerPoint
    /// 属于跨进程切换，正好撞在这条锁上。把两个线程的输入队列临时接起来再切，系统就认了。
    /// </para>
    /// </summary>
    public static void ForceForeground(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;

        try
        {
            if (PptTouchNative.GetForegroundWindow() == hwnd) return;

            var fg = PptTouchNative.GetForegroundWindow();
            var fgThread = fg == IntPtr.Zero ? 0u : PptTouchNative.GetWindowThreadProcessId(fg, out _);
            var self = PptTouchNative.GetCurrentThreadId();

            var attached = fgThread != 0 && fgThread != self
                           && PptTouchNative.AttachThreadInput(self, fgThread, true);
            try
            {
                PptTouchNative.SetForegroundWindow(hwnd);
                PptTouchNative.SetFocus(hwnd);
            }
            finally
            {
                if (attached) PptTouchNative.AttachThreadInput(self, fgThread, false);
            }
        }
        catch (Exception ex)
        {
            AppLog.Warning("ppttouch", "切回放映窗口失败：" + ex.Message);
        }
    }

    /// <summary>把前台还给放映窗口（菜单被点空白处关掉时用，否则下一次触摸会被判定成「前台不是放映」）。</summary>
    public static void RestoreSlideshowForeground()
    {
        var hwnd = FindSlideshowWindow();
        if (hwnd != IntPtr.Zero) ForceForeground(hwnd);
    }

    /// <summary>这个窗口现在是不是前台。注入按键之前的最后一道检查。</summary>
    public static bool IsForeground(IntPtr hwnd) => hwnd != IntPtr.Zero && PptTouchNative.GetForegroundWindow() == hwnd;

    /// <summary>
    /// 按顺序发一次组合键：先依次按下 <paramref name="keys"/>，再倒序抬起。
    /// 例：<c>SendCombo(PptKey.Ctrl, PptKey.S)</c> 就是 Ctrl+S。
    /// </summary>
    public static void SendCombo(params ushort[] keys)
    {
        if (keys.Length == 0) return;

        var list = new List<PptTouchNative.INPUT>(keys.Length * 2);
        foreach (var vk in keys) list.Add(KeyInput(vk, up: false));
        for (var i = keys.Length - 1; i >= 0; i--) list.Add(KeyInput(keys[i], up: true));

        var inputs = list.ToArray();
        var sent = PptTouchNative.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<PptTouchNative.INPUT>());
        if (sent != inputs.Length)
            AppLog.Warning("ppttouch", $"补按键失败（SendInput 返回 {sent}）：{Marshal.GetLastWin32Error()}");
    }

    private static PptTouchNative.INPUT KeyInput(ushort vk, bool up) => new()
    {
        type = PptTouchNative.INPUT_KEYBOARD,
        U = new PptTouchNative.InputUnion
        {
            ki = new PptTouchNative.KEYBDINPUT
            {
                wVk = vk,
                dwFlags = up ? PptTouchNative.KEYEVENTF_KEYUP : 0,
                dwExtraInfo = PptTouchNative.OurInjectedMark,
            },
        },
    };



    /// <summary>
    /// 建一个 message-only 窗口，向触摸屏设备注册 Raw Input —— 只为知道「刚才有没有手指碰屏幕」。
    ///
    /// <para>
    /// 2026-11-02 加。来由：这台机器的触屏驱动把手指当**普通鼠标**上报（<c>dwExtraInfo=0x0</c>），
    /// 而 PowerPoint 收到这种点击不翻页（触摸带一点细微位移，被它判成拖拽）；鼠标点击却能翻。
    /// 两者在鼠标消息上完全一样，只能另外向设备要触摸事件来区分。
    /// </para>
    /// <para>
    /// ⚠️ 必须在线程自己有消息循环的地方调用（现在在 <see cref="HookThread"/> 里），
    /// 否则 <c>WM_INPUT</c> 没人泵，窗口收不到任何东西。
    /// </para>
    /// </summary>
    private static void EnsureTouchWindow()
    {
        if (_touchHwnd != IntPtr.Zero) return;

        try
        {
            var hInstance = PptTouchNative.GetModuleHandle(null);
            _touchWndProc = TouchWndProc;

            // 类名要一直留在非托管内存里（窗口类靠它索引），进程退出才释放，故意不 free
            _touchClassName = Marshal.StringToHGlobalUni("CshPptTouchRawInput");

            var wc = new PptTouchNative.WNDCLASSEX
            {
                cbSize = (uint)Marshal.SizeOf<PptTouchNative.WNDCLASSEX>(),
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_touchWndProc),
                hInstance = hInstance,
                lpszClassName = _touchClassName,
            };
            PptTouchNative.RegisterClassEx(ref wc);

            _touchHwnd = PptTouchNative.CreateWindowEx(
                0, "CshPptTouchRawInput", "", PptTouchNative.WS_POPUP, 0, 0, 0, 0,
                IntPtr.Zero, IntPtr.Zero, hInstance, IntPtr.Zero);

            if (_touchHwnd == IntPtr.Zero)
            {
                AppLog.Warning("ppttouch", "创建触摸监听窗口失败：" + Marshal.GetLastWin32Error());
                return;
            }

            var devices = new[]
            {
                new PptTouchNative.RAWINPUTDEVICE
                {
                    usUsagePage = PptTouchNative.HID_USAGE_PAGE_DIGITIZER,
                    usUsage = PptTouchNative.HID_USAGE_DIGITIZER_TOUCH_SCREEN,
                    dwFlags = PptTouchNative.RIDEV_INPUTSINK,
                    hwndTarget = _touchHwnd,
                },
            };

            if (PptTouchNative.RegisterRawInputDevices(
                    devices, (uint)devices.Length, (uint)Marshal.SizeOf<PptTouchNative.RAWINPUTDEVICE>()))
            {
                AppLog.Info("ppttouch", "触摸 Raw Input 已注册（用来区分手指与鼠标）");
            }
            else
            {
                AppLog.Warning("ppttouch", "注册触摸 Raw Input 失败：" + Marshal.GetLastWin32Error());
            }
        }
        catch (Exception ex)
        {
            AppLog.Exception("ppttouch", ex, "建立触摸监听");
        }
    }

    private static IntPtr TouchWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == PptTouchNative.WM_INPUT)
        {
            // 只记时刻，给「这条鼠标消息是不是触摸来的」当判据用（见 HandleMouse 的 touched）。
            _lastTouchTick = Environment.TickCount;
        }

        return PptTouchNative.DefWindowProc(hWnd, msg, wParam, lParam);
    }

    // ── 钩子线程 ──────────────────────────────────────────────

    private static void HookThread()
    {
        _threadId = PptTouchNative.GetCurrentThreadId();

        // 先把线程的消息队列建出来：⚠️ 没有队列时 PostThreadMessage(WM_QUIT) 会失败，
        // 用户开关拨得太快的话 Stop() 就停不掉这个线程了。
        PptTouchNative.PeekMessage(out _, IntPtr.Zero, 0, 0, PptTouchNative.PM_NOREMOVE);

        _hookProc = OnMouse;
        _hook = PptTouchNative.SetWindowsHookEx(
            PptTouchNative.WH_MOUSE_LL, _hookProc, PptTouchNative.GetModuleHandle(null), 0);

        if (_hook == IntPtr.Zero)
        {
            AppLog.Warning("ppttouch", "安装鼠标钩子失败：" + Marshal.GetLastWin32Error());
            _threadId = 0;
            return;
        }

        _active = true;
        AppLog.Info("ppttouch", "鼠标钩子已安装，放映时轻点翻页待命");

        // 触摸监听必须在这个线程里建：message-only 窗口的 WM_INPUT 要靠下面这个消息循环泵上来。
        EnsureTouchWindow();

        while (PptTouchNative.GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            PptTouchNative.TranslateMessage(ref msg);
            PptTouchNative.DispatchMessage(ref msg);
        }

        PptTouchNative.UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
        _hookProc = null;
        _active = false;
        AppLog.Info("ppttouch", "鼠标钩子已卸载");
    }

    private static IntPtr OnMouse(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            try
            {
                var msg = wParam.ToInt32();
                var info = Marshal.PtrToStructure<PptTouchNative.MSLLHOOKSTRUCT>(lParam);

                // ⚠️ 右键要在走到 HandleMouse 之前就拦掉：返回非 0 = 这条消息不再往下传。
                if (SwallowRightButton(msg, info)) return new IntPtr(1);

                // 菜单开着：点在菜单外面就收起来，并吞掉这一下（原生右键菜单也是只收不点）。
                if (PptMenu.IsBlocking && SwallowOutsideMenuClick(msg, info)) return new IntPtr(1);

                HandleMouse(msg, info);
            }
            catch (Exception ex)
            {
                // 钩子回调里抛异常会把整个钩子带走，这里一律吞掉只记一次
                AppLog.Exception("ppttouch", ex, "处理鼠标事件");
            }
        }

        return PptTouchNative.CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    /// <summary>
    /// 吞掉放映中的右键，返回 true 表示这条消息不再传给 PowerPoint。
    ///
    /// <para>
    /// 2026-11-03 加。来由：长按屏幕时，**Windows 自己会把「按住不放」合成成一次右键点击**，
    /// PowerPoint 放映中收到右键就切到激光笔 —— 激光笔一出，长按菜单的体验就断了
    /// （日志里长按 1328ms 是能正常弹出菜单的，真正打断的是这中间那次右键）。
    /// 这是系统级的触摸手势，只能在钩子里把消息截下来。
    /// </para>
    /// <para>
    /// 顺带把鼠标右键也接过来：放映时鼠标右键原本弹的是 PowerPoint 原生菜单，
    /// 既然本程序已经有自己的菜单，两个菜单并存反而乱。现在右键统一弹本程序的菜单，
    /// 和触摸长按完全一致。已经不拦的情况下（没在放映、或不是触摸）照常放行。
    /// </para>
    /// </summary>
    private static bool SwallowRightButton(int msg, PptTouchNative.MSLLHOOKSTRUCT info)
    {
        if (msg is not (PptTouchNative.WM_RBUTTONDOWN or PptTouchNative.WM_RBUTTONUP)) return false;

        // 我们自己注入的不算
        if (info.dwExtraInfo == PptTouchNative.OurInjectedMark) return false;

        // 只在放映时动手 —— 平时右键是用户和别的程序的事，一个都不能碰
        if (!IsSlideshowForeground(out _, out _)) return false;

        // 鼠标右键也弹我们的菜单，但只在按下那一下弹，且菜单已经开着就别再弹一次
        if (msg == PptTouchNative.WM_RBUTTONDOWN && !PptMenu.IsBlocking && _customMenu)
        {
            AppLog.Info("ppttouch", $"放映中收到右键（flags=0x{info.flags:X}），改弹本程序菜单");
            PptMenu.ShowAt(info.pt.x, info.pt.y);
        }

        return true;
    }

    /// <summary>
    /// 菜单开着的时候处理点击：点在菜单外面就收起菜单，并把这消息吞掉。返回 true = 吞掉。
    ///
    /// <para>
    /// 2026-11-03 加。为什么要自己判断、不让菜单自己收：
    /// 本程序把菜单做成了独立的小窗（原版是 1×1 宿主窗口 + 系统 MenuFlyout），
    /// 「点窗外收起」没法靠浮层自带的 dismiss 区域，窗外点击统一由这里接管。
    /// </para>
    /// <para>
    /// ⚠️ 点在菜单上时必须**放行**：钩子是全局的，吞掉的话菜单自己也收不到点击。
    /// 判断靠 <see cref="PptTouchNative.WindowFromPoint"/> 拿该点下的窗口，再看是不是本进程的
    /// （菜单浮层是独立窗口，属于本进程；幻灯片属于 POWERPNT.EXE）。
    /// </para>
    /// </summary>
    private static bool SwallowOutsideMenuClick(int msg, PptTouchNative.MSLLHOOKSTRUCT info)
    {
        if (msg is not (PptTouchNative.WM_LBUTTONDOWN or PptTouchNative.WM_LBUTTONUP)) return false;

        _tracking = false;

        if (IsOverOwnWindow(info.pt)) return false;     // 点在菜单上，交给菜单

        if (msg == PptTouchNative.WM_LBUTTONDOWN)
        {
            AppLog.Info("ppttouch", "点在菜单外面，收起菜单");
            PptMenu.MarkDismissed();
            PptMenu.HideMenu();
        }

        return true;                                    // 收起菜单的那一下不穿透到 PowerPoint
    }

    /// <summary>该屏幕坐标下是不是本程序的窗口（菜单浮层、宿主窗口）。</summary>
    private static bool IsOverOwnWindow(PptTouchNative.POINT pt)
    {
        var hwnd = PptTouchNative.WindowFromPoint(pt);
        if (hwnd == IntPtr.Zero) return false;

        PptTouchNative.GetWindowThreadProcessId(hwnd, out var pid);
        return pid == (uint)Environment.ProcessId;
    }

    private static void HandleMouse(int msg, PptTouchNative.MSLLHOOKSTRUCT info)
    {
        if (msg is not (PptTouchNative.WM_LBUTTONDOWN or PptTouchNative.WM_LBUTTONUP)) return;

        // 注入事件里只丢「我们自己补的」那一次，靠 dwExtraInfo 上的标记认。
        // ⛔ 不能再用 LLMHF_INJECTED 一刀切：Windows 触摸驱动合成鼠标消息时同样会带注入标记
        //    （2026-11-02 实测：手指点击 flags=0x1、extraInfo=0xFF5157xx），
        //    一刀切会把手指点击全丢掉 —— 这就是「轻点不翻页」的真正原因。
        if (info.dwExtraInfo == PptTouchNative.OurInjectedMark) return;

        // 菜单正开着：这几下触摸交给菜单（点在窗外的已经由 SwallowOutsideMenuClick 处理掉了）。
        if (PptMenu.IsBlocking)
        {
            _tracking = false;
            return;
        }

        // 刚点完菜单项 / 刚收起菜单：接下来这 400ms 的抬起消息一律让过，
        // 否则会被当成一次「轻点」补发到刚切回前台的 PowerPoint 上。
        if (unchecked(Environment.TickCount64 - PptMenu.LastInvokeTick) < 400)
        {
            _tracking = false;
            return;
        }

        // 只处理触屏 / 笔。标准驱动会在 extraInfo 上带 0xFF5157xx；
        // 但这台机器的驱动打的是 0x0，所以再补一条判据：800ms 内收到过触摸原始输入。
        // ⚠️ 两条都不满足就当鼠标点击丢掉 —— 鼠标单击 PPT 自己会翻页，我们再补一次就变两页。
        var touched = unchecked(Environment.TickCount - _lastTouchTick) <= 800;

        // 抬起时以「这次按下已经在跟踪中」兜底：UU 远程的抬起消息有时不带 0xFF5157xx。
        // 跟踪状态只可能由通过触摸判定的按下置上，所以拿它兜底不会把纯鼠标点击带进来。
        var isTouch = IsTouchOrPen(info.dwExtraInfo) || touched || _tracking;
        if (!isTouch) return;

        if (msg == PptTouchNative.WM_LBUTTONDOWN)
        {
            if (!IsSlideshowForeground(out var fgHwnd, out var scale))
            {
                _tracking = false;
                DiagNotSlideshow(fgHwnd);
                return;
            }

            _tracking = true;
            _startX = info.pt.x;
            _startY = info.pt.y;
            _startTick = Environment.TickCount64;
            _scale = scale;
            return;
        }

        // ── 抬起 ──
        if (!_tracking)
        {
            AppLog.Info("ppttouch", "抬起被丢弃：这次按下没有进入跟踪（按下时前台不是放映窗口）");
            return;
        }

        _tracking = false;

        var dx = info.pt.x - _startX;
        var dy = info.pt.y - _startY;
        var elapsed = Environment.TickCount64 - _startTick;

        // 抬起那一刻放映已经退出（按 Esc / 切窗口）就别再动作
        if (!IsSlideshowForeground(out var endHwnd, out _))
        {
            var cls = new StringBuilder(64);
            PptTouchNative.GetClassName(endHwnd, cls, cls.Capacity);
            AppLog.Info("ppttouch", $"抬起被丢弃：放映已退出，前台是「{cls}」");
            return;
        }

        var limit = (int)Math.Ceiling(TapMaxDip * _scale);
        var movedSq = (long)dx * dx + (long)dy * dy;

        // ⛔⛔ 用笔 / 荧光笔 / 橡皮 / 激光笔时不翻页（上游 dv1.2.0）。
        //    老师拿着笔在屏幕上写字，抬笔那一下时间短、位移小，正好符合下面的"轻点"判据，
        //    于是刚写完一横就翻到下一页 —— 这是上课时最要命的误触。
        //    这里只拦**我们自己补发的翻页动作**（轻点补单击 / 上下滑翻页）；
        //    真正的鼠标点击和 PowerPoint 自己的处理不受影响。
        //
        //    判据有两路，**COM 优先**：
        //      ① PptPointerState（读 Office 的 SlideShowView.PointerType）—— 准，
        //         连用户用 PPT 自带快捷键/右键菜单切工具也能知道；
        //      ② PptMenu.ActivePointer（记我们自己菜单里选过什么）—— 兜底，
        //         没装 PowerPoint / WPS 演示 / COM 读不到时用。
        //    ⚠️ 两路是"或"的关系：任一路说在画，就不翻页。宁可少翻一次
        //       （菜单里还有上一页/下一页可用），也不要在写字时乱翻页。
        var drawing = PptPointerState.IsToolMode || PptMenu.IsDrawingPointer;

        // ── 轻点：时间短、位移小 → 在落点补一次干净的单击 ──
        // 2026-11-03 改：原来补空格，空格只告诉 PowerPoint「下一页」，不告诉它点了哪里，
        // 于是视频暂停、音频、超链接、动作按钮全部失效。改成补单击后这些都由 PowerPoint 判断。
        // ⚠️ 光标已在落点附近时不发移动，只发按下抬起 —— 指针因此不会闪（见 SendTapAt）。
        if (elapsed <= TapMaxMs && movedSq <= (long)limit * limit)
        {
            if (drawing)
            {
                // 笔模式下这一下是"落笔/收笔"，PowerPoint 自己会画，我们什么都不补。
                AppLog.Info("ppttouch",
                    $"轻点已忽略（当前是 {PptMenu.ActivePointer}，不翻页）：({_startX},{_startY})");
                return;
            }

            SendTapAt(_startX, _startY);
            Interlocked.Increment(ref _taps);
            AppLog.Info("ppttouch",
                $"轻点：({_startX},{_startY})　{elapsed}ms　位移 {Math.Sqrt(movedSq):0.#}px");
            return;
        }

        // ── 滑动：竖直方向够长、且明显比横向多 → 翻页 ──
        // 2026-11-02 加，对齐 WPS 的上下滑翻页：手指往上滑 = 下一页，往下滑 = 上一页。
        // 要求 |dy| > |dx| 是为了避开「横向拖进度条」那类操作。
        var swipe = (int)Math.Ceiling(SwipeMinDip * _scale);
        if (Math.Abs(dy) >= swipe && Math.Abs(dy) > Math.Abs(dx))
        {
            if (drawing)
            {
                // 笔模式下竖向拖动是在画线，绝不能当翻页
                AppLog.Info("ppttouch",
                    $"滑动已忽略（当前是 {PptMenu.ActivePointer}，不翻页）：({_startX},{_startY})→({info.pt.x},{info.pt.y})");
                return;
            }

            var next = dy < 0;
            SendKey(next ? PptTouchNative.VK_NEXT : PptTouchNative.VK_PRIOR);
            Interlocked.Increment(ref _taps);
            AppLog.Info("ppttouch",
                $"滑动翻{(next ? "下一页" : "上一页")}：({_startX},{_startY})→({info.pt.x},{info.pt.y})　{elapsed}ms");
            return;
        }

        // ── 长按：按住够久、几乎没动 → 弹出本程序自己的菜单（2026-11-03）──
        // 为什么不等「按下就计时弹」：这台机器上手指的按下与抬起靠鼠标消息配不出可靠时长
        //（远程触屏有时把两者压在同一刻），只有「松手」这个动作是确定的。松手弹菜单，
        // 手指也刚好抬起来去点菜单，体验上反而顺。
        if (_customMenu && elapsed >= LongPressMinMs && elapsed <= LongPressMaxMs)
        {
            var longLimit = (int)Math.Ceiling(LongPressMaxDip * _scale);
            if (movedSq <= (long)longLimit * longLimit)
            {
                AppLog.Info("ppttouch", $"长按 {elapsed}ms，在 ({_startX},{_startY}) 弹出菜单");
                PptMenu.ShowAt(_startX, _startY);
                return;
            }
        }

        // 其余情况（长按超时、慢慢拖）**一律不干预**，原样留给 PowerPoint 自己处理。
        // 长按会出激光笔，那是它自带的行为，本程序不再插手。
    }

    /// <summary>
    /// 在指定屏幕坐标补一次**原地左键单击** —— 让 PowerPoint 自己判断点到了什么。
    ///
    /// <para>
    /// 2026-11-03 改（用户选定的方案）。原来轻点补的是空格，空格只告诉 PowerPoint
    /// 「下一页」，不告诉它「你点了哪里」，于是幻灯片里的视频暂停、音频、超链接、
    /// 动作按钮全部失效，一律变成翻页。改成补一次干净的单击之后，这些都由 PowerPoint 自己处理：
    /// 点空白翻页、点视频暂停、点链接跳转。
    /// </para>
    /// <para>
    /// ⚠️ 关键细节：**光标已经在落点附近时不发移动**。
    /// 只发「按下/抬起」（不带 ABSOLUTE 标志），系统就用当前位置，指针一动不动；
    /// 一旦带上 ABSOLUTE，即使坐标相同也会走一次定位，肉眼能看到指针闪。
    /// UU 远程本来就把光标同步到手指处，所以绝大多数情况下不会出现位移。
    /// </para>
    /// </summary>
    private static void SendTapAt(int x, int y)
    {
        var needMove = true;

        if (PptTouchNative.GetCursorPos(out var cur))
        {
            long ddx = cur.x - x;
            long ddy = cur.y - y;
            needMove = ddx * ddx + ddy * ddy > (long)CursorNearPx * CursorNearPx;
        }

        var list = new System.Collections.Generic.List<PptTouchNative.INPUT>(3);

        if (needMove)
        {
            var (mx, my) = ToAbsolute(x, y);
            list.Add(new PptTouchNative.INPUT
            {
                type = PptTouchNative.INPUT_MOUSE,
                U = new PptTouchNative.InputUnion
                {
                    mi = new PptTouchNative.MOUSEINPUT
                    {
                        dx = mx,
                        dy = my,
                        dwFlags = PptTouchNative.MOUSEEVENTF_MOVE
                                | PptTouchNative.MOUSEEVENTF_ABSOLUTE
                                | PptTouchNative.MOUSEEVENTF_VIRTUALDESK,
                        dwExtraInfo = PptTouchNative.OurInjectedMark,
                    },
                },
            });
        }

        // 不带 ABSOLUTE = 用光标当前位置，不产生任何移动。
        list.Add(new PptTouchNative.INPUT
        {
            type = PptTouchNative.INPUT_MOUSE,
            U = new PptTouchNative.InputUnion
            {
                mi = new PptTouchNative.MOUSEINPUT
                {
                    dwFlags = PptTouchNative.MOUSEEVENTF_LEFTDOWN,
                    dwExtraInfo = PptTouchNative.OurInjectedMark,
                },
            },
        });

        list.Add(new PptTouchNative.INPUT
        {
            type = PptTouchNative.INPUT_MOUSE,
            U = new PptTouchNative.InputUnion
            {
                mi = new PptTouchNative.MOUSEINPUT
                {
                    dwFlags = PptTouchNative.MOUSEEVENTF_LEFTUP,
                    dwExtraInfo = PptTouchNative.OurInjectedMark,
                },
            },
        });

        var inputs = list.ToArray();
        var sent = PptTouchNative.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<PptTouchNative.INPUT>());
        if (sent != inputs.Length)
            AppLog.Warning("ppttouch", $"补单击失败（SendInput 返回 {sent}）：{Marshal.GetLastWin32Error()}");
    }

    /// <summary>屏幕坐标 → SendInput 需要的虚拟桌面归一化坐标（0..65535）。</summary>
    private static (int X, int Y) ToAbsolute(int x, int y)
    {
        var vx = PptTouchNative.GetSystemMetrics(PptTouchNative.SM_XVIRTUALSCREEN);
        var vy = PptTouchNative.GetSystemMetrics(PptTouchNative.SM_YVIRTUALSCREEN);
        var vw = Math.Max(1, PptTouchNative.GetSystemMetrics(PptTouchNative.SM_CXVIRTUALSCREEN));
        var vh = Math.Max(1, PptTouchNative.GetSystemMetrics(PptTouchNative.SM_CYVIRTUALSCREEN));

        return (
            (int)Math.Round((x - vx) * 65535.0 / (vw - 1)),
            (int)Math.Round((y - vy) * 65535.0 / (vh - 1)));
    }

    /// <summary>
    /// 发一次按键（真实键盘事件，**不碰鼠标**）。
    ///
    /// <para>
    /// 2026-11-02 加。原来轻点补的是 <c>SendInput</c> 鼠标点击，用户反馈「能看到鼠标替我们点一下」，
    /// 而且比发键多走一次「光标移动到绝对坐标」。上游 PowerPoint-Touch-Assist 也是发空格。
    /// 用 <c>SendInput</c> 而不是 <c>PostMessage</c>：投递的键盘消息有些程序不认，只认真实键盘焦点。
    /// </para>
    /// </summary>
    private static void SendKey(ushort vk) => SendCombo(vk);

    private static void DiagNotSlideshow(IntPtr hwnd)
    {
        var now = Environment.TickCount64;
        if (now - _lastDiagTick < 30_000) return;
        _lastDiagTick = now;

        var cls = new StringBuilder(64);
        PptTouchNative.GetClassName(hwnd, cls, cls.Capacity);
        PptTouchNative.GetWindowThreadProcessId(hwnd, out var pid);

        var path = new StringBuilder(512);
        var size = path.Capacity;
        var handle = PptTouchNative.OpenProcess(PptTouchNative.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (handle != IntPtr.Zero)
        {
            try { PptTouchNative.QueryFullProcessImageName(handle, 0, path, ref size); }
            finally { PptTouchNative.CloseHandle(handle); }
        }

        AppLog.Info("ppttouch",
            $"收到触屏点击，但前台不是放映窗口：类名「{cls}」，进程「{Path.GetFileName(path.ToString())}」");
    }

    /// <summary>
    /// 触屏 / 笔合成出来的鼠标消息，<c>dwExtraInfo</c> 形如 <c>0xFF5157xx</c>。
    /// </summary>
    private static bool IsTouchOrPen(IntPtr extraInfo) =>
        (extraInfo.ToInt64() & 0xFFFFFF00L) == 0xFF515700L;

    /// <summary>
    /// 前台窗口是 PowerPoint 的全屏放映窗口（窗口类名 <c>screenClass</c>）。
    /// ⚠️ 必须同时核对进程名：只看类名的话，别的程序起个同名窗口就能让本功能乱点。
    /// </summary>
    private static bool IsSlideshowForeground(out IntPtr hwnd, out double scale)
    {
        hwnd = PptTouchNative.GetForegroundWindow();
        scale = 1.0;
        if (hwnd == IntPtr.Zero) return false;

        var cls = new StringBuilder(64);
        if (PptTouchNative.GetClassName(hwnd, cls, cls.Capacity) == 0) return false;
        if (!string.Equals(cls.ToString(), "screenClass", StringComparison.OrdinalIgnoreCase)) return false;

        PptTouchNative.GetWindowThreadProcessId(hwnd, out var pid);
        if (!IsPowerPointProcess(pid)) return false;

        // ⚠️ 移植差异：GetDpiForWindow 是 Win10 1607+ 的导出，Win7 上**根本不存在**，
        //    直接调会抛 EntryPointNotFoundException（而且是在钩子回调里，消息会被整条丢掉）。
        //    Win7 只有系统级 DPI，按 100% 算是对的；拿不到就保持 scale = 1.0。
        //    ⚠️ 只缓存「这个系统有没有这个导出」，**不缓存缩放值** —— 放映窗口换到另一块
        //       DPI 不同的显示器时，阈值仍要按那一块屏算（不缓存的话 Win7 上每次触摸抛一次异常，
        //       钩子线程的开销会拖慢整机输入）。
        if (_dpiProbe < 0)
        {
            scale = 1.0;
        }
        else
        {
            try
            {
                var dpi = PptTouchNative.GetDpiForWindow(hwnd);
                _dpiProbe = 1;
                if (dpi > 0) scale = dpi / 96.0;
            }
            catch
            {
                _dpiProbe = -1;   // Win7 / 更早的系统：以后不再问
                scale = 1.0;
            }
        }

        return true;
    }

    private static bool IsPowerPointProcess(uint pid)
    {
        if (pid == 0) return false;

        var handle = PptTouchNative.OpenProcess(PptTouchNative.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (handle == IntPtr.Zero) return false;

        try
        {
            var path = new StringBuilder(512);
            var size = path.Capacity;
            if (!PptTouchNative.QueryFullProcessImageName(handle, 0, path, ref size)) return false;

            return string.Equals(Path.GetFileName(path.ToString()), "POWERPNT.EXE",
                                 StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            PptTouchNative.CloseHandle(handle);
        }
    }
}
