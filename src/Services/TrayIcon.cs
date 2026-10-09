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

    public bool IsReady => _added;

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

    private bool AddIcon()
    {
        try
        {
            if (_hIcon == IntPtr.Zero) _hIcon = ResolveIcon();

            // ⚠️ hIcon = 0 时 Shell_NotifyIcon(NIM_ADD) **照样返回成功**，托盘里落下的却是一个
            //    看不见的空位 —— 用户找不到入口，程序还以为"我有托盘"，于是关窗口收托盘之后
            //    彻底失联（界面不显示 + 托盘无图标 + 只能去任务管理器）。这种情况必须当失败处理。
            if (_hIcon == IntPtr.Zero)
            {
                Log("拿不到任何可用图标句柄 → 不挂托盘（宁可不收托盘，也不能让用户找不到程序）");
                return false;
            }

            var data = new NativeMethodsShell.NOTIFYICONDATA
            {
                cbSize = (uint)Marshal.SizeOf<NativeMethodsShell.NOTIFYICONDATA>(),
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
                Log($"Shell_NotifyIcon(NIM_ADD) 失败: {Marshal.GetLastWin32Error()} cbSize={data.cbSize} hwnd={_hwnd} icon={_hIcon}");
            else
                Log($"托盘图标已挂上（hwnd={_hwnd}，icon={_hIcon}，来源={_iconSource}，cbSize={data.cbSize}）");
            return _added;
        }
        catch (Exception ex)
        {
            Log("AddIcon 异常: " + ex);
            return false;
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
                uVersion = 4,
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
            else if (msg == WM_DESTROY)
            {
                _added = false;
            }
            else if (_taskbarCreated != 0 && msg == _taskbarCreated)
            {
                // 资源管理器重启 → 图标丢了，重新挂上
                _added = false;
                AddIcon();
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
