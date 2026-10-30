using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using ClassSoftwareHub.Desktop.Platform;

namespace ClassSoftwareHub.Desktop.Services;

/// <summary>托盘右键菜单里的一项。</summary>
public sealed class TrayMenuItem
{
    public string Text { get; set; } = "";

    /// <summary>点中后通过 <see cref="TrayIcon.CommandInvoked"/> 发出来的命令名。</summary>
    public string Command { get; set; } = "";

    /// <summary>true 就是一条分隔线（忽略 Text/Command）。</summary>
    public bool Separator { get; set; }

    /// <summary>双击托盘图标时默认执行的那项（生成本默认项，画粗体）。</summary>
    public bool IsDefault { get; set; }
}

/// <summary>
/// 系统托盘图标（走 <c>Shell_NotifyIcon</c>）。
/// 做法：建一个不可见的普通顶层窗口当"消息窗口"，托盘消息都发到它；
/// 左键单击 → <see cref="LeftClick"/>；右键 → 弹原生菜单 → <see cref="CommandInvoked"/>。
/// 用法：new TrayIcon(path) → Setup(tip) → 事件挂上；不用了 Dispose。
///
/// ⚠️ Win7 移植说明（为什么没用 <c>Avalonia.Controls.TrayIcon</c>）：
///   Avalonia 11 内置的 <c>TrayIcon</c> 只覆盖「图标 + 提示 + NativeMenu + Clicked」，
///   **既不支持气泡通知**（原版 <see cref="ShowBalloon"/> 用来提示"发现新版本 / 下载完成"，
///     Win7 上就是托盘气泡、Win10+ 才是 toast），**也没有 TaskbarCreated 重挂逻辑**
///   （资源管理器重启后内置类不会自动补回图标）。这两条都是原版的既有行为，覆盖不到就会丢功能，
///   所以按任务说明走 Win32 <c>Shell_NotifyIcon</c> 兜底 —— 原版本来也就是这套纯 Win32 实现，
///   在 Win7 上完整可用。所有 P/Invoke 已收进 <c>Platform.NativeMethods*</c>。
/// </summary>
public sealed class TrayIcon : IDisposable
{
    // ── Win32 常量 ─────────────────────────────────────────────
    private const uint WM_APP = 0x8000;
    private const uint WM_TRAY = WM_APP + 1;
    private const uint WM_DESTROY = 0x0002;
    private const uint WM_NULL = 0x0000;
    private const uint WM_LBUTTONUP = 0x0202;
    private const uint WM_RBUTTONUP = 0x0205;
    private const uint WM_TIMER = 0x0113;

    /// <summary>失败后退避重试用的定时器 id（挂在消息窗口上，跑 UI 线程）。</summary>
    private const uint RetryTimerId = 1;

    /// <summary>最多重试几次（1+2+…+5 秒递增，约半分钟窗口期）。</summary>
    private const int RetryBudget = 10;

    /// <summary>左键单击托盘图标（一般用来显示/隐藏主窗口）。</summary>
    public event Action? LeftClick;

    /// <summary>右键菜单选中某项，参数是菜单 id（show / palette / update / exit）。</summary>
    public event Action<string>? CommandInvoked;

    /// <summary>用户点了气泡通知（系统通知）本体。</summary>
    public event Action? BalloonClicked;

    /// <summary>
    /// 右键菜单内容（按顺序显示）。留空就用默认那几项。
    /// 可以挂多个托盘图标实例，各自给自己的菜单。
    /// </summary>
    public List<TrayMenuItem> MenuItems { get; } = new();

    // ── 实例状态 ───────────────────────────────────────────────
    private readonly string _className = "CshTrayIconWnd_" + Guid.NewGuid().ToString("N")[..8];
    private readonly string _iconPath;
    private readonly NativeMethodsShell.WndProcDelegate _proc;   // 必须留引用，否则委托被 GC 后回调直接崩
    private readonly uint _taskbarCreated;
    private IntPtr _hwnd = IntPtr.Zero;
    private IntPtr _hIcon = IntPtr.Zero;
    private bool _ownsIcon;                          // 自己 LoadImage/掏出来的才 DestroyIcon（系统共享图标不能删）
    private string _iconSource = "(未解析)";
    private string _tip = "ClassSoftwareHub";
    private bool _added;
    private bool _disposed;
    private int _retries;

    public bool IsReady => _added;

    /// <summary>最近一次 NIM_ADD 的 Win32 错误码（0 = 失败但系统没给错误码）。排障/提示用。</summary>
    public int LastError { get; private set; }

    public TrayIcon(string iconPath)
    {
        _iconPath = iconPath;
        _proc = WndProc;
        _taskbarCreated = NativeMethodsShell.RegisterWindowMessage("TaskbarCreated");
    }

    private static void Log(string message)
    {
        try
        {
            Core.AppLog.Info("tray", message);
        }
        catch { }
    }

    /// <summary>建窗口 + 把图标挂上托盘。失败返回 false（不会抛）。</summary>
    public bool Setup(string tip)
    {
        _tip = tip;
        try
        {
            var hInstance = NativeMethodsShell.GetModuleHandle(null);
            var wc = new NativeMethodsShell.WNDCLASSEX
            {
                cbSize = (uint)Marshal.SizeOf<NativeMethodsShell.WNDCLASSEX>(),
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_proc),
                hInstance = hInstance,
                lpszClassName = _className,
            };
            if (NativeMethodsShell.RegisterClassEx(ref wc) == 0)
                Log($"RegisterClassEx 失败: {Marshal.GetLastWin32Error()}");

            // 不可见的顶层窗口（不能用 message-only：那类窗口收不到 TaskbarCreated 广播）
            _hwnd = NativeMethodsShell.CreateWindowEx(0, _className, "ClassSoftwareHub tray", 0,
                0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, hInstance, IntPtr.Zero);
            if (_hwnd == IntPtr.Zero)
            {
                Log($"CreateWindowEx 失败: {Marshal.GetLastWin32Error()}");
                return false;
            }

            return AddIcon();
        }
        catch (Exception ex)
        {
            Log("Setup 异常: " + ex);
            return false;
        }
    }

    /// <param name="slow">
    /// 允许同步等 300ms 重试一次。只在 Setup 的首次尝试里为 true；
    /// 定时器重试走 false —— 那是 UI 线程上的回调，不能每次都卡 300ms。
    /// </param>
    private bool AddIcon(bool slow = true)
    {
        try
        {
            if (_disposed) return false;
            if (_hIcon == IntPtr.Zero) _hIcon = ResolveIcon();

            // ⚠️ hIcon = 0 时 Shell_NotifyIcon(NIM_ADD) **照样返回成功**，托盘里落下的却是一个
            //    看不见的空位 —— 用户找不到入口，程序还以为"我有托盘"，于是关窗口收托盘之后
            //    彻底失联（界面不显示 + 托盘无图标 + 只能去任务管理器）。这种情况必须当失败处理。
            if (_hIcon == IntPtr.Zero)
            {
                Log("拿不到任何可用图标句柄 → 不挂托盘（宁可不收托盘，也不能让用户找不到程序）");
                return false;
            }

            var full = (uint)Marshal.SizeOf<NativeMethodsShell.NOTIFYICONDATA>();

            var ok = TryAddIcon(full);
            if (ok) return true;

            if (slow)
            {
                // 重试一次：explorer 托盘服务偶发瞬时无响应（ERROR_TIMEOUT）时，缓一下再挂就能过。
                System.Threading.Thread.Sleep(300);
                Log($"NIM_ADD 首次失败（err={DescribeError(LastError)}，调用上下文：{DescribeCaller()}），300ms 后重试");
                ok = TryAddIcon(full);
                if (ok)
                {
                    Log("重试成功：托盘图标已挂上");
                    return true;
                }
            }

            // 仍失败：cbSize 退到老版（Win95 基线，不含 guidItem / hBalloonIcon）尺寸再试一次 ——
            // 少数环境（精简系统 / 第三方 shell 替换）对 V3 尺寸的 cbSize 挑食，老版布局是最大公约数。
            // ⚠️ x64 上老版 = V3 全量 - 24（guidItem 16 + hBalloonIcon 8）；带 guidItem 的真 V2 尺寸是 full - 8，别混。
            var legacy = full - 24;
            Log($"重试仍失败（err={DescribeError(LastError)}），cbSize 退到老版尺寸 {legacy} 再试");
            if (TryAddIcon(legacy)) return true;

            ScheduleRetry();
            return false;
        }
        catch (Exception ex)
        {
            Log("AddIcon 异常: " + ex);
            return false;
        }
    }

    /// <summary>
    /// 失败后隔一会儿再试。explorer 重启、锁屏解锁、权限变化都可能让原本被拒的注册变得可行，
    /// 而用户不会为此重开程序 —— 放弃得太早等于永久丢托盘（并连带把「关闭=退出」的兜底一直钉死）。
    /// </summary>
    private void ScheduleRetry()
    {
        if (_disposed || _hwnd == IntPtr.Zero || _added) return;
        if (_retries++ >= RetryBudget)
        {
            Log($"托盘图标重试 {RetryBudget} 次仍未成功，停止自动重试（最后一次 err={DescribeError(LastError)}）");
            return;
        }
        // 1s、2s、3s…… 最长 5s 一次，避免在 explorer 真的拒绝时狂刷
        var ms = (uint)Math.Min(1000 * _retries, 5000);
        NativeMethodsShell.SetTimer(_hwnd, RetryTimerId, ms, IntPtr.Zero);
    }

    /// <summary>发一次 NIM_ADD。cbSize 可指定（V3 全量 / 老版兼容两档）。</summary>
    private bool TryAddIcon(uint cbSize)
    {
        var data = new NativeMethodsShell.NOTIFYICONDATA
        {
            cbSize = cbSize,
            hWnd = _hwnd,
            uID = 1,
            uFlags = NativeMethodsShell.NIF_MESSAGE | NativeMethodsShell.NIF_ICON | NativeMethodsShell.NIF_TIP,
            uCallbackMessage = WM_TRAY,
            hIcon = _hIcon,
            szTip = _tip,
            szInfo = "",
            szInfoTitle = "",
        };

        _added = NativeMethodsShell.Shell_NotifyIcon(NativeMethodsShell.NIM_ADD, ref data);
        if (!_added)
        {
            // ⚠️ 错误码必须**紧跟调用**读：中间插一次日志写盘，LastError 就被刷成 0 了
            LastError = Marshal.GetLastWin32Error();
            Log($"Shell_NotifyIcon(NIM_ADD) 失败: err={DescribeError(LastError)} cbSize={data.cbSize} hwnd={_hwnd} icon={_hIcon} tip={_tip} {DescribeCaller()}");
        }
        else
        {
            LastError = 0;
            NativeMethodsShell.KillTimer(_hwnd, RetryTimerId);
            Log($"托盘图标已挂上（hwnd={_hwnd}，icon={_hIcon}，来源={_iconSource}，cbSize={data.cbSize}）");
        }
        return _added;
    }

    /// <summary>把 Win32 错误码翻成人话（排障时一眼定位方向，不用现查表）。</summary>
    private static string DescribeError(int err) => err switch
    {
        0 => "0（返回 FALSE 但没有错误码 —— 静默拒绝，查调用上下文）",
        5 => "5（ERROR_ACCESS_DENIED —— 完整性级别不足，被 UIPI 拦下：本进程比资源管理器低）",
        87 => "87（ERROR_INVALID_PARAMETER —— 参数或结构体布局不被接受）",
        1460 => "1460（ERROR_TIMEOUT —— explorer 托盘服务无响应）",
        _ => err.ToString(),
    };

    /// <summary>
    /// 调用现场快照：线程、COM 套间、管理员、以及**本进程与资源管理器的完整性级别**。
    ///
    /// UIPI 的判据是"低完整性不能向高完整性窗口发消息"，光看「是不是管理员」判断不了 ——
    /// 2026-10-28 实机就踩过：进程自称管理员、实际令牌是 Medium，托盘窗口在 High 的 explorer 里，
    /// err=5 看着像权限不足，翻文档才发现方向被记反了。把两边的 IL 直接打出来，远端排障不用再猜。
    /// </summary>
    private static string DescribeCaller()
    {
        try
        {
            var t = System.Threading.Thread.CurrentThread;
            string admin;
            try
            {
                using var id = System.Security.Principal.WindowsIdentity.GetCurrent();
                var principal = new System.Security.Principal.WindowsPrincipal(id);
                admin = principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator) ? "是" : "否";
            }
            catch { admin = "?"; }

            return $"[线程={t.ManagedThreadId} 套间={t.GetApartmentState()} 管理员={admin} "
                 + $"本进程IL={IntegrityOf(NativeMethodsShell.GetCurrentProcessId())} "
                 + $"资源管理器IL={ShellIntegrity()}]";
        }
        catch { return "[上下文获取失败]"; }
    }

    /// <summary>资源管理器（托盘宿主）的完整性级别。</summary>
    private static string ShellIntegrity()
    {
        var shell = NativeMethodsShell.GetShellWindow();
        if (shell == IntPtr.Zero) return "(找不到 Shell 窗口)";
        NativeMethods.GetWindowThreadProcessId(shell, out var pid);
        return pid == 0 ? "(取不到 pid)" : IntegrityOf(pid);
    }

    /// <summary>读进程令牌的强制完整性级别（S-1-16-xxxx）。失败返回错误说明，绝不抛。</summary>
    private static string IntegrityOf(uint pid)
    {
        var proc = NativeMethodsShell.OpenProcess(NativeMethodsShell.PROCESS_QUERY_LIMITED_INFORMATION, false, (int)pid);
        if (proc == IntPtr.Zero) return "err=" + Marshal.GetLastWin32Error();
        IntPtr token = IntPtr.Zero;
        IntPtr buffer = IntPtr.Zero;
        try
        {
            if (!NativeMethodsShell.OpenProcessToken(proc, 0x0008 /*TOKEN_QUERY*/, out token))
                return "open-token err=" + Marshal.GetLastWin32Error();

            NativeMethodsShell.GetTokenInformation(token, 25 /*TokenIntegrityLevel*/, IntPtr.Zero, 0, out var size);
            if (size <= 0) return "?";
            buffer = Marshal.AllocHGlobal(size);
            if (!NativeMethodsShell.GetTokenInformation(token, 25, buffer, size, out _))
                return "info err=" + Marshal.GetLastWin32Error();

            // TOKEN_MANDATORY_LABEL { SID_AND_ATTRIBUTES { PSID Sid; DWORD Attributes; } }
            var sid = Marshal.ReadIntPtr(buffer, 0);
            if (!NativeMethodsShell.ConvertSidToStringSidW(sid, out var text)) return "sid err=" + Marshal.GetLastWin32Error();
            try { return Marshal.PtrToStringUni(text) ?? "?"; }
            finally { NativeMethodsShell.LocalFree(text); }
        }
        catch (Exception ex) { return ex.GetType().Name; }
        finally
        {
            if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
            if (token != IntPtr.Zero) NativeMethodsShell.CloseHandle(token);
            NativeMethodsShell.CloseHandle(proc);
        }
    }

    /// <summary>
    /// 解析托盘图标句柄，三级兜底（每一级的结果都写进 tray.log，方便远端排障）：
    ///   1. 直接加载 .ico 文件 —— 正常路径，最清晰
    ///   2. 从宿主 exe 里掏图标（<c>SHGetFileInfo</c>，Win2000+ 就有）—— .ico 被删 / 被杀软拦 / 路径失效时的兜底
    ///   3. 系统默认应用图标（<c>LoadIcon(IDI_APPLICATION)</c>）—— 最后的保命项，丑但一定有
    /// 绝不返回 0：返回 0 等于"挂一个看不见的图标"。
    /// </summary>
    private IntPtr ResolveIcon()
    {
        // ── 1) .ico 文件 ─────────────────────────────────────────────
        try
        {
            if (!string.IsNullOrEmpty(_iconPath) && File.Exists(_iconPath) &&
                _iconPath.EndsWith(".ico", StringComparison.OrdinalIgnoreCase))
            {
                var h = NativeMethods.LoadImage(IntPtr.Zero, _iconPath, NativeMethods.IMAGE_ICON, 0, 0,
                    NativeMethods.LR_LOADFROMFILE | NativeMethods.LR_DEFAULTSIZE);
                if (h != IntPtr.Zero)
                {
                    _ownsIcon = true;
                    _iconSource = "ico 文件";
                    return h;
                }
                Log($"LoadImage 加载 ico 失败（err={Marshal.GetLastWin32Error()}，path={_iconPath}）→ 改用 exe 图标");
            }
            else
            {
                Log($"图标文件不可用（path={(string.IsNullOrEmpty(_iconPath) ? "(空)" : _iconPath)}）→ 改用 exe 图标");
            }
        }
        catch (Exception ex)
        {
            Log("LoadImage 异常: " + ex.Message);
        }

        // ── 2) 宿主 exe 的图标 ───────────────────────────────────────
        try
        {
            var exe = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exe) && File.Exists(exe))
            {
                var info = default(NativeMethodsIcons.SHFILEINFO);
                NativeMethodsIcons.SHGetFileInfo(exe, 0, ref info,
                    (uint)Marshal.SizeOf<NativeMethodsIcons.SHFILEINFO>(),
                    NativeMethodsIcons.SHGFI_ICON | NativeMethodsIcons.SHGFI_SMALLICON);
                if (info.hIcon != IntPtr.Zero)
                {
                    _ownsIcon = true;
                    _iconSource = "exe 内嵌图标";
                    return info.hIcon;
                }
                Log("SHGetFileInfo 没掏出 exe 图标 → 退回系统默认图标");
            }
        }
        catch (Exception ex)
        {
            Log("SHGetFileInfo 异常: " + ex.Message);
        }

        // ── 3) 系统默认应用图标 ──────────────────────────────────────
        try
        {
            var h = NativeMethods.LoadIcon(IntPtr.Zero, NativeMethods.IDI_APPLICATION);
            if (h != IntPtr.Zero)
            {
                _ownsIcon = false;                       // 共享图标，不能删
                _iconSource = "系统默认图标";
                return h;
            }
        }
        catch (Exception ex)
        {
            Log("LoadIcon 异常: " + ex.Message);
        }

        return IntPtr.Zero;
    }

    /// <summary>改提示文字（鼠标悬停显示）。</summary>
    public void SetTip(string tip)
    {
        _tip = tip;
        if (!_added) return;
        try
        {
            var data = new NativeMethodsShell.NOTIFYICONDATA
            {
                cbSize = (uint)Marshal.SizeOf<NativeMethodsShell.NOTIFYICONDATA>(),
                hWnd = _hwnd,
                uID = 1,
                uFlags = NativeMethodsShell.NIF_TIP,
                szTip = tip,
                szInfo = "",
                szInfoTitle = "",
            };
            NativeMethodsShell.Shell_NotifyIcon(NativeMethodsShell.NIM_MODIFY, ref data);
        }
        catch { }
    }

    /// <summary>
    /// 弹一条系统通知（托盘气泡）。主窗口没露脸的时候用它提醒"有新版本"。
    /// 用户点通知 → <see cref="BalloonClicked"/>。
    ///
    /// ⚠️ Win7 说明：Win7 上这就是**托盘气泡**，显示时长由系统决定（<c>NOTIFYICONDATA</c> 里的
    ///    <c>uTimeout</c> 字段与 <c>uVersion</c> 共用同一偏移；本结构里称 <c>uVersion</c>）；
    ///    Win10+ 才会被转成 Action Center 的 toast。
    /// </summary>
    public void ShowBalloon(string title, string text)
    {
        if (!_added) return;
        try
        {
            var data = new NativeMethodsShell.NOTIFYICONDATA
            {
                cbSize = (uint)Marshal.SizeOf<NativeMethodsShell.NOTIFYICONDATA>(),
                hWnd = _hwnd,
                uID = 1,
                uFlags = NativeMethodsShell.NIF_INFO | NativeMethodsShell.NIF_MESSAGE | NativeMethodsShell.NIF_ICON | NativeMethodsShell.NIF_TIP,
                uCallbackMessage = WM_TRAY,
                hIcon = _hIcon,
                szTip = _tip,
                szInfo = Clip(text, 255),
                szInfoTitle = Clip(title, 63),
                dwInfoFlags = NativeMethodsShell.NIIF_INFO,
                // ⚠️ 不设 uVersion：NIM_MODIFY 下这个偏移是 uTimeout（气泡超时毫秒），写 4 是语义错位
                //    （XP 会钳到 10~30s，Vista+ 直接忽略）。想要 V4 回调行为必须走 NIM_SETVERSION，
                //    而本类按老版回调语义解析 lParam（事件码直接在 lParam 里），不该切 V4。
            };
            var ok = NativeMethodsShell.Shell_NotifyIcon(NativeMethodsShell.NIM_MODIFY, ref data);
            if (!ok) Log($"ShowBalloon 失败: {Marshal.GetLastWin32Error()}");
        }
        catch (Exception ex)
        {
            Log("ShowBalloon 异常: " + ex.Message);
        }
    }

    private static string Clip(string? s, int max) =>
        string.IsNullOrEmpty(s) ? "" : (s.Length <= max ? s : s[..max]);

    private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (msg == WM_TRAY)
            {
                var evt = (uint)(lParam.ToInt64() & 0xFFFF);
                if (evt == WM_LBUTTONUP)
                {
                    LeftClick?.Invoke();
                    return IntPtr.Zero;
                }
                if (evt == WM_RBUTTONUP)
                {
                    ShowMenu();
                    return IntPtr.Zero;
                }
                if (evt == NativeMethodsShell.NIN_BALLOONUSERCLICK)
                {
                    BalloonClicked?.Invoke();
                    return IntPtr.Zero;
                }
            }
            else if (msg == WM_TIMER && wParam == (IntPtr)RetryTimerId)
            {
                // 退避重试：成功会 KillTimer + 把 _added 置 true；失败会自己排下一次
                NativeMethodsShell.KillTimer(_hwnd, RetryTimerId);
                if (AddIcon(slow: false))
                    Log("退避重试成功：托盘图标已挂上");
                return IntPtr.Zero;
            }
            else if (msg == WM_DESTROY)
            {
                _added = false;
            }
            else if (_taskbarCreated != 0 && msg == _taskbarCreated)
            {
                // 资源管理器重启 → 图标丢了，重新挂上（顺便清掉退避计时，重新算一轮预算）
                _added = false;
                _retries = 0;
                AddIcon(slow: false);
            }
        }
        catch (Exception ex)
        {
            Log("WndProc 异常: " + ex);
        }

        return NativeMethodsShell.DefWindowProc(hWnd, msg, wParam, lParam);
    }

    private void ShowMenu()
    {
        var items = MenuItems.Count > 0 ? MenuItems : DefaultMenu();

        var menu = NativeMethodsShell.CreatePopupMenu();
        if (menu == IntPtr.Zero) return;
        try
        {
            var live = new List<TrayMenuItem>();
            uint nextId = 1;
            foreach (var item in items)
            {
                if (item.Separator)
                {
                    NativeMethodsShell.AppendMenu(menu, NativeMethodsShell.MF_SEPARATOR, IntPtr.Zero, null);
                    continue;
                }

                var id = nextId++;
                live.Add(item);
                NativeMethodsShell.AppendMenu(menu, NativeMethodsShell.MF_STRING | (item.IsDefault ? NativeMethodsShell.MF_DEFAULT : 0), new IntPtr(id), item.Text);
            }

            NativeMethodsShell.GetCursorPos(out var pt);
            NativeMethods.SetForegroundWindow(_hwnd);   // 不设前台，菜单点外面不会消失
            var cmd = NativeMethodsShell.TrackPopupMenu(menu, NativeMethodsShell.TPM_RETURNCMD | NativeMethodsShell.TPM_NONOTIFY, pt.X, pt.Y, 0, _hwnd, IntPtr.Zero);
            NativeMethodsShell.PostMessage(_hwnd, WM_NULL, IntPtr.Zero, IntPtr.Zero);

            if (cmd > 0 && cmd <= live.Count)
                CommandInvoked?.Invoke(live[cmd - 1].Command);
        }
        catch (Exception ex)
        {
            Log("ShowMenu 异常: " + ex);
        }
        finally
        {
            NativeMethodsShell.DestroyMenu(menu);
        }
    }

    private static List<TrayMenuItem> DefaultMenu() => new()
    {
        new TrayMenuItem { Text = "打开主界面", Command = "show", IsDefault = true },
        new TrayMenuItem { Text = "常用工具", Command = "palette" },
        new TrayMenuItem { Separator = true },
        new TrayMenuItem { Text = "检查更新", Command = "update" },
        new TrayMenuItem { Separator = true },
        new TrayMenuItem { Text = "退出", Command = "exit" },
    };

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            if (_hwnd != IntPtr.Zero) NativeMethodsShell.KillTimer(_hwnd, RetryTimerId);
            if (_added)
            {
                var data = new NativeMethodsShell.NOTIFYICONDATA
                {
                    cbSize = (uint)Marshal.SizeOf<NativeMethodsShell.NOTIFYICONDATA>(),
                    hWnd = _hwnd,
                    uID = 1,
                    szTip = "",
                    szInfo = "",
                    szInfoTitle = "",
                };
                NativeMethodsShell.Shell_NotifyIcon(NativeMethodsShell.NIM_DELETE, ref data);
                _added = false;
            }
            if (_hwnd != IntPtr.Zero) NativeMethodsShell.DestroyWindow(_hwnd);
            NativeMethodsShell.UnregisterClass(_className, NativeMethodsShell.GetModuleHandle(null));
            // ⚠️ 只销毁自己创建的图标；LoadIcon 拿回来的是系统共享句柄，删了会影响别的程序
            if (_hIcon != IntPtr.Zero && _ownsIcon) NativeMethods.DestroyIcon(_hIcon);
        }
        catch { }
        _hwnd = IntPtr.Zero;
        _hIcon = IntPtr.Zero;
    }
}
