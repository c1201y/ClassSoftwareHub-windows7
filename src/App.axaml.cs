using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using ClassSoftwareHub.Desktop.Services;

namespace ClassSoftwareHub.Desktop;

/// <summary>
/// 应用对象。对应 WinUI 原版的 <c>App.xaml.cs</c>。
///
/// ⚠️ 时序差异（WinUI → Avalonia，必须理清）：
///   · WinUI：<c>App</c> 构造函数里先 <c>InitializeComponent()</c>，然后
///     <c>OnLaunched</c> 里 <c>Settings.Load()</c> 并 <c>new MainWindow()</c>。
///   · Avalonia：生命周期是「构造函数 → <see cref="Initialize"/>（载入 XAML）→
///     <see cref="OnFrameworkInitializationCompleted"/>（这时才能建窗口）」。
///     所以：**单实例判定仍旧放在构造函数里**（与 WinUI 完全同刻，能立刻抢互斥体）；
///     <c>Settings.Load()</c> 与主窗口创建挪到 <see cref="OnFrameworkInitializationCompleted"/>。
///     互斥体名 / 事件名必须一字不差，否则和安装包的 AppMutex 对不上，升级时会提示"程序正在运行"。
/// </summary>
public partial class App : Application
{
    /// <summary>主窗口。对应原版 <c>App.MainWindow</c>。</summary>
    public static MainWindow? MainWindow { get; private set; }

    /// <summary>
    /// 「应用正在退出」的全局标记（2026-10-04 修，对齐上游 dv1.1.0-insider1.7）。
    ///
    /// ⛔ 为什么必须有：工具浮窗（<c>ToolPaletteWindow</c>）和 Q 群反馈窗（<c>QqFeedbackGuideWindow</c>）
    ///    都把窗口关闭拦下来当"收起来"用（<c>args.Cancel = true</c>），这是它们自己的正常语义。
    ///    但 Avalonia 的 <c>desktop.Shutdown()</c> 与 WinUI 的 <c>Application.Exit()</c> 一样是**逐个关窗**的，
    ///    碰到被取消的就中止整条退出流程 —— 结果：只要这两个窗里任意一个开着，托盘菜单「退出」就会变成
    ///    「主窗关了、托盘图标摘了、**进程却一直赖在任务管理器里**」。
    ///    退出流程一开始就把它置 true，那两个窗口的 Closing 看到它就放行，不再拦。
    /// </summary>
    public static bool IsExiting { get; set; }

    /// <summary>
    /// 主窗口是否**至少显示过一次**（首次 Activated 时置位）。
    ///
    /// 为什么要单独记：退出那一刻窗口往往已经被关掉，<c>IsVisible</c> 必然为 false，
    /// 会把「用户正常用完主动关闭」误判成「界面压根没出来」。只有「曾经显示过」才是判据，
    /// 启动看门狗也靠它决定该不该兜底显示。
    /// </summary>
    internal static bool MainShownOnce { get; private set; }

    /// <summary>由 MainWindow 的首次 Activated 调用。</summary>
    internal static void NoteMainShown() => MainShownOnce = true;

    /// <summary>设置存储。对应原版 <c>App.Settings</c>（<c>SettingsStore</c> 静态实例）。</summary>
    public static SettingsStore Settings { get; } = new();

    /// <summary>软件内容（原生界面用）。启动时 Load 一次，内含容错解析与问题清单。</summary>
    public static Core.ContentStore Content { get; } = new();

    /// <summary>埋点。⚠️ 具体实现由服务层提供（原版是 <c>ITelemetryService</c>）。</summary>
    public static ITelemetryService Telemetry { get; internal set; } =
        new NoopTelemetryService(Settings);

    public App()
    {
        // ⚠️ Avalonia 的全局 UI 线程异常事件挂在 Dispatcher 上（Application 本身没有），
        //    见 OnUnhandledException 的移植说明。这里在构造里就挂上，尽早生效。
        Dispatcher.UIThread.UnhandledException += OnUnhandledException;

        // 单实例。这个命名的互斥体同时也是安装程序 [Setup] AppMutex 用的名字 —— 装/升级时 Inno 靠它
        // 判断"应用还在跑"，所以进程活着期间必须一直持有，不能释放。
        var mutex = new Mutex(initiallyOwned: true, Core.ShellConfig.MutexName, out var isFirstInstance);

        if (isFirstInstance)
        {
            _instanceMutex = mutex;
            TryCreateActivateSignal();
            return;
        }

        // ═══════════════════════════════════════════════════════════════════════
        //  已经有一个实例在跑 —— 来的是"用户又点了一次图标"
        //
        //  ⛔ 这条路径先后写错过两版，代价是用户整整一天用不了程序，必须记下来：
        //
        //     第一版：一句 Environment.Exit(0)。界面、提示、日志全无。
        //     第二版：敲一下"叫醒"事件，然后**看互斥体还在不在**决定退不退。
        //             看似合理，实则致命 —— 互斥体只证明"对方进程活着"，
        //             **完全不证明"对方看得见界面"**。
        //
        //             2026-10-01 实机日志里那个 PID=4040 就是这种僵尸：11:40 启动、
        //             活了一整天、常驻内存只剩 4MB、主窗口从头到尾没露过面。它把之后
        //             **每一次**双击都吃掉了，每次都静默退出 3 秒 ——
        //             用户看到的就是「控制台几秒就没了 / 双击没反应 / 界面不出来」，
        //             而真相是：昨晚那 8 次启动，界面一次都没有被真正启动过。
        //
        //  现在改成：以「对方有没有一个看得见的主窗口」为唯一判据。
        //     看得见 → 正常唤醒，本实例退出（绝大多数情况，和以前一样）。
        //     看不见 → 判定僵尸，结束它、由本实例接管启动。用户双击就一定能用上程序。
        // ═══════════════════════════════════════════════════════════════════════
        mutex.Dispose();

        if (ResolveSingleInstanceConflict())
        {
#if CSH_CONSOLE
            // Environment.Exit 不执行 finally，收尾代码会被整个跳过 ——
            // 退出原因必须在这里显式说出来，并且停住窗口，否则控制台跟着一起消失，死无对证。
            Platform.DiagConsole.NoteExit(_exitReason);
            Platform.DiagConsole.Pause();
#endif
            Environment.Exit(0);
            return;
        }

        // 僵尸已经清掉了 —— 踏踏实实重新抢一次互斥体（等它真正释放出来再拿）。
        for (var i = 0; i < 50; i++)
        {
            var m = new Mutex(initiallyOwned: true, Core.ShellConfig.MutexName, out var first);
            if (first)
            {
                _instanceMutex = m;
                break;
            }
            m.Dispose();
            Thread.Sleep(100);
        }
        TryCreateActivateSignal();
    }

    private static Mutex? _instanceMutex;

    /// <summary>第一个实例监听用的"叫醒"事件。</summary>
    private static EventWaitHandle? _activateSignal;

    /// <summary>建"叫醒"事件。建不出来（极罕见）只是丢掉这条兜底路径，不影响启动。</summary>
    private static void TryCreateActivateSignal()
    {
        try
        {
            _activateSignal = new EventWaitHandle(
                initialState: false, EventResetMode.AutoReset, Core.ShellConfig.ActivateEventName);
        }
        catch (Exception ex)
        {
            _activateSignal = null;
            Services.ScreenCapture.Log("[single] 叫醒事件建不出来: " + ex.Message);
        }
    }

    /// <summary>上一次"为什么退出"的原因。给诊断控制台 <c>NoteExit</c> 用。</summary>
    private static string _exitReason = "检测到已有 ClassSoftwareHub 实例正在运行。";

    /// <summary>
    /// 本实例该不该退出 —— 单实例冲突的全部决策都在这里。
    ///
    /// 返回 <c>true</c>  = 对方是好好的（或刚启动、值得再等），本实例退出。
    /// 返回 <c>false</c> = 对方是「进程活着但界面永远不显示」的僵尸，已经清掉了，本实例接管启动。
    ///
    /// ⚠️ 判据是「**对方有没有一个看得见的主窗口**」，绝不是「互斥体还在不在」。
    ///    这是这次修复的全部要点 —— 详见构造函数里那段说明。
    /// </summary>
    private static bool ResolveSingleInstanceConflict()
    {
        var name = Process.GetCurrentProcess().ProcessName;

        // 1) 先敲门：请现有实例把主窗口亮出来。
        //    --takeover 时跳过这步，直接进入"拿不出窗口就清场"。
        if (!Program.ForceTakeover) KnockExistingInstance();

        // 2) 最多等 6 秒，反复确认对方到底有没有把主窗口露出来。
        //    ⚠️ 这段必须放在**带超时的后台线程**上跑：
        //       枚举别的进程的窗口走的是 P/Invoke（EnumWindows / GetWindowRect），
        //       对面如果是个**挂死**的进程，这些调用有可能卡住不返回 ——
        //       单实例判定卡在这儿，用户看到的就是"双击后毫无反应"，和要修的毛病一样糟。
        //       超时一律按"拿不出窗口"处理（僵尸），这是安全的那一侧。
        if (WaitForVisibleWindowOrExit(name, 6000)) return true;

        // 3) 对方占着互斥体整整 6 秒，却始终拿不出一个看得见的主窗口。
        //    先给「老机器启动慢」留余地：进程刚起来没多久就别急着杀。
        if (!Program.ForceTakeover && Platform.WindowProbe.SiblingStartedWithin(name, TimeSpan.FromSeconds(20)))
        {
            _exitReason = "检测到另有一个 ClassSoftwareHub 正在启动中（进程刚起来不到 20 秒），已向它发出唤醒请求。"
                        + "请再等几秒；如果界面一直不出来，重新运行本程序即可 —— 新版会自动清理掉卡住的实例。";
            return true;
        }

        // 4) 判定僵尸 → 结束它，本实例接管。
        Platform.PortLog.Step("单实例: 现有实例活着但主窗口始终不可见 → 判定为僵尸，开始清理");

        var killed = 0;
        var failed = new List<string>();
        foreach (var pid in Platform.WindowProbe.SiblingPids(name))
        {
            try
            {
                using var p = Process.GetProcessById(pid);
                p.Kill();
                p.WaitForExit(3000);
                killed++;
                Platform.PortLog.Step($"单实例: 已结束卡住的实例 PID={pid}");
            }
            catch (Exception ex)
            {
                failed.Add($"PID={pid}（{ex.GetType().Name}）");
            }
        }

        if (failed.Count > 0)
        {
            // 没权限清掉 → 别硬撑，给一句人能照做的话，别让程序静默消失。
            _exitReason = "检测到一个卡住的 ClassSoftwareHub 实例（进程活着但界面不显示），"
                        + "但本程序没有权限结束它：" + string.Join("、", failed)
                        + "。请打开任务管理器，结束所有 ClassSoftwareHub.exe，然后再运行本程序。";
            WarnUser(_exitReason);
            return true;
        }

        if (killed > 0)
        {
            Platform.PortLog.Step($"单实例: 僵尸实例已清理（{killed} 个）—— 本次以干净状态启动");
            return false;
        }

        // 一个都没杀到、也没报错 → 它们刚好在枚举与结束之间自己退了
        return true;
    }

    /// <summary>
    /// 在后台线程上轮询：现有实例是不是**要么自己退了、要么把主窗口露出来了**。
    ///
    /// 返回 <c>true</c> = 两者之一成立（本实例该退出）。
    /// 返回 <c>false</c> = 超时，或者探测线程被对面的挂死进程卡住 —— 一律按"僵尸"处理。
    ///
    /// 为什么要专门起线程：见调用点的说明（对面挂死时 EnumWindows/GetWindowRect 可能不返回）。
    /// </summary>
    private static bool WaitForVisibleWindowOrExit(string processName, int timeoutMs)
    {
        var ok = false;
        using var done = new ManualResetEventSlim(false);

        var probe = new Thread(() =>
        {
            try
            {
                var deadline = Environment.TickCount64 + timeoutMs;
                while (Environment.TickCount64 < deadline)
                {
                    var siblings = Platform.WindowProbe.SiblingPids(processName);
                    if (siblings.Count == 0) { ok = true; break; }                            // 对方自己退了
                    if (Platform.WindowProbe.HasVisibleMainWindow(siblings)) { ok = true; break; }  // 界面看得见
                    Thread.Sleep(100);
                }
            }
            catch { /* 探测本身出问题 → 保持 ok=false，按僵尸处理 */ }
            finally { try { done.Set(); } catch { } }
        })
        {
            IsBackground = true,          // 万一真被卡住，绝不能拖住进程退出
            Name = "csh-single-probe",
        };

        probe.Start();
        done.Wait(timeoutMs + 1500);      // 线程卡住时这里到点就返回，不死等
        return ok;
    }

    /// <summary>
    /// 敲一下"叫醒"事件，请已经在跑的那个实例把主窗口亮出来。
    /// 敲不敲得响都不影响后面的判据 —— 最终看的是"窗口有没有出现"。
    /// </summary>
    private static void KnockExistingInstance()
    {
        var name = Core.ShellConfig.ActivateEventName;

        // 第一个实例理论上可能还卡在启动途中（事件还没建出来），给它几秒。
        for (var attempt = 0; attempt < 30; attempt++)
        {
            try
            {
                using var handle = EventWaitHandle.OpenExisting(name);
                handle.Set();
                return;
            }
            catch (WaitHandleCannotBeOpenedException)
            {
                Thread.Sleep(100);
            }
            catch (Exception ex)
            {
                // 打不开又不是"不存在"（多半是权限）→ 放弃敲门，交给窗口判据决定
                Services.ScreenCapture.Log("[single] 叫醒事件打不开: " + ex.Message);
                return;
            }
        }
    }

    /// <summary>
    /// 在 GUI 版里也必须把话说到 —— 单实例冲突是"双击了但什么都没发生"的头号原因，
    /// 让程序静默退出等于又把用户推回猜谜。
    /// </summary>
    private static void WarnUser(string message)
    {
#if CSH_CONSOLE
        // 控制台版：调用方紧接着会 NoteExit + Pause 把内容呈现出来，
        // 这里再弹系统对话框反而会挡住控制台，所以只交给控制台。
        _ = message;
#else
        try
        {
            MessageBoxW(IntPtr.Zero, message, "ClassSoftwareHub", MB_OK | MB_ICONINFORMATION | MB_SETFOREGROUND);
        }
        catch { }
#endif
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "MessageBoxW")]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);

    private const uint MB_OK = 0x00000000;
    private const uint MB_ICONINFORMATION = 0x00000040;
    private const uint MB_SETFOREGROUND = 0x00010000;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        // 对应原版 OnLaunched：先读设置，再建主窗口。
        Settings.Load();

        // 数据目录分区：日志 → logs\，内嵌解包的图片/图标缓存 → cache\（旧版全堆在根上）。
        // 尽早搬 —— 后面任何模块一写日志就落到新位置了。
        try
        {
            Core.AppLog.MigrateLegacyFiles();
            Services.EmbeddedAssets.MigrateLegacyCache();
        }
        catch { /* 迁移失败不影响启动，文件留在原地 */ }
        Telemetry = new NoopTelemetryService(Settings);
        Telemetry.Track("app_launch", new Dictionary<string, object?>
        {
            ["shellVersion"] = Core.ShellConfig.ShellVersion,
            // ⛔ 不用 Environment.OSVersion 判版本（见 OsInfo），这里只是埋点，取真实内核版本更准
            ["osBuild"] = Platform.OsInfo.Version.Build
        });

        // 弹出层（下拉菜单等）在深色模式下会露白边 —— 在无 DWM 合成的机器上，
        // 弹窗窗口没有逐像素透明度，圆角之外那圈露出的是窗口自己的白底。
        // 修复必须在任何弹窗出现之前挂好（设置页那个主题下拉框首帧就要用到）。
        Platform.PopupSurfaceFix.Install();
        Platform.PopupSurfaceFix.LogDecision();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // ⚠️ 软件 gamma 调光是**系统级**状态（改的是显卡输出查找表，不属于本进程）——
            //    程序退出时必须还原，否则用户关掉程序之后屏幕还一直暗着，只能重启才恢复。
            desktop.Exit += (_, _) =>
            {
                try { Services.BrightnessService.RestoreGamma(); } catch { }
            };

            Platform.PortLog.Step("App: 开始构造主窗口");
            try
            {
                MainWindow = new MainWindow();
                desktop.MainWindow = MainWindow;
                Platform.PortLog.Step("App: 主窗口对象构造完成（接下来交给 Avalonia 显示）");
            }
            catch (Exception ex)
            {
                // ⚠️⚠️ 这条路径从前是**完全静默**的，而且它恰好能解释「侧边栏有、主界面不出来」：
                //     · 侧边栏是在 MainWindow 构造函数的**前段**（InitTray）挂上去的，所以它还活着；
                //     · 主窗口构造若在中后段抛异常，整个窗口对象就废了，永远不会显示；
                //     · Release 下 Dispatcher 会把 UI 线程异常标记为 Handled —— 进程继续跑，
                //       界面却永远不出来，用户只能看到一个孤零零的侧边栏。
                //     所以这里必须留下铁证，并且给用户一个**能读到原因**的窗口。
                Platform.PortLog.Fail("App: 主窗口构造失败（界面永远不会出来）", ex);

                try
                {
                    Directory.CreateDirectory(SettingsStore.Dir);
                    File.AppendAllText(Path.Combine(SettingsStore.Dir, "crash.log"),
                        $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}] [MainWindow ctor] {ex}\n\n");
                }
                catch { }

                MainWindow = null;
                desktop.MainWindow = BuildStartupFailureWindow(ex);
            }
        }

        // 起一条后台线程守"叫醒"事件：用户又点了一次桌面图标 → 第二个实例 Set 这个事件 →
        // 这里回到 UI 线程把主窗口叫出来（ShowFromTray 对"已可见"的窗口也会重新激活并抢前台）。
        StartActivateListener();

        // 看门狗：正常情况下 Avalonia 会把 MainWindow 显示出来。万一某个环境上它没显示
        // （用户看到的就是「进程活着、侧边栏有、主界面不出来」），这里兜一次，保证界面能出现。
        // 只对「从未显示过」的窗口动手 —— 所以 --minimized / --palette 那种故意收起来的场景
        // 不会被误拉出来（那两种情况在首次 Activated 里就置位过了）。
        DispatcherTimer.RunOnce(() =>
        {
            try
            {
                if (MainShownOnce) return;
                if (MainWindow is not { } win) return;

                Platform.PortLog.Step("看门狗: 启动 6 秒后主窗口仍未显示过 → 强制 Show() 兜底");
                win.Show();
                win.Activate();
            }
            catch (Exception ex)
            {
                Platform.PortLog.Fail("看门狗强制显示主窗口失败", ex);
            }
        }, TimeSpan.FromSeconds(6));

        // 两个实验性功能的到点巡检。⚠️ 都靠本进程内的定时器 —— 本程序没在跑就不会查杀
        //    （2026-09-28 Nick 确认按这个来，不往系统里装计划任务）。
        //    「白板专杀」与「程序专杀」各跑各的定时器、各存各的配置，刻意不合并（Nick 明确要求白板独立）。
        Data.EasiNoteGuard.Start();
        Data.ProcessGuard.Start();

        // 虚拟键盘（实验性功能）：总开关是单一的 —— 关着的时候 Start() 第一句就 return，
        // 触摸钩子、UIA 探测、注册表接管一个都不会上电（见 VirtualKeyboardService）。
        Services.VirtualKeyboard.VirtualKeyboardService.Start();

        // 更新安装包自动清理：updates 目录只留最近 N 个（默认 3），更早的删掉。
        // 走后台线程，不沾首帧；新版本装完后的第一次启动正好把旧包收掉。
        Services.Updating.InstallerCleanup.Start();

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// 主窗口构造失败时的兜底窗口 —— 把异常原样摊开给用户看。
    ///
    /// 为什么值得专门做一个：Win7 教室机上，用户既看不到界面、也未必找得到日志文件。
    /// 有了这个窗口，「把上面这段发给我」就变成一句可执行的话，而不是再猜一轮。
    /// </summary>
    private static Window BuildStartupFailureWindow(Exception ex)
    {
        var body = new TextBlock
        {
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            Margin = new Thickness(24),
            Text = "ClassSoftwareHub 启动失败\n\n"
                 + "主界面没能创建出来（这是程序自身的问题，不是操作失误）。\n"
                 + "下面这段是具体原因，请整段复制或截图发给我：\n\n"
                 + ex,
        };

        return new Window
        {
            Title = "ClassSoftwareHub · 启动失败",
            Width = 960,
            Height = 600,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Content = new ScrollViewer { Content = body },
        };
    }

    /// <summary>
    /// 起一条后台线程守"叫醒"事件：用户又点了一次桌面图标 → 第二个实例 Set 这个事件 →
    /// 这里回到 UI 线程把主窗口叫出来（<see cref="MainWindow.ShowFromTray"/> 对"已可见"的窗口
    /// 也会重新激活并抢前台，所以"程序开着但被压在后面"时点图标同样有效）。
    ///
    /// ⚠️ <c>IsBackground = true</c>：这条线程绝不能拦住进程退出。
    /// ⚠️ 事件是 AutoReset 的 —— 第二次点击如果发生在处理途中，信号不会丢。
    /// ⚠️ 移植说明：原版用 <c>DispatcherQueue.TryEnqueue</c>，Avalonia 里是 <c>Dispatcher.UIThread.Post</c>。
    /// </summary>
    private static void StartActivateListener()
    {
        var signal = _activateSignal;
        if (signal is null) return;

        var listener = new Thread(() =>
        {
            while (true)
            {
                try
                {
                    if (!signal.WaitOne()) break;
                }
                catch
                {
                    break;
                }

                // 落一条日志：这条链路出问题时，日志是唯一能分辨"根本没收到"还是"收到了但窗口没出来"的依据
                Services.ScreenCapture.Log("[single] 收到唤醒请求，把主窗口叫出来");

                Dispatcher.UIThread.Post(() =>
                {
                    try
                    {
                        MainWindow?.ShowFromTray();
                    }
                    catch (Exception ex)
                    {
                        Services.ScreenCapture.Log("[single] 叫醒主窗口失败: " + ex.Message);
                    }
                });
            }
        })
        {
            IsBackground = true,
            Name = "csh-activate-listener",
        };

        listener.Start();
    }

    /// <summary>
    /// 全局兜底。在原版里 <c>UnhandledException</c> 主要干两件事：写一份排障用的 crash.log、别让页面级异常把进程带走。
    ///
    /// ⚠️ 移植说明（Avalonia 没有 1:1 的等价物，这里是**尽力而为**）：
    ///    · Avalonia 的 <c>Application</c> 没有 WinUI 那种「XamlRoot 级、可 Handled」的全局异常事件；
    ///      对应机制是 <c>Dispatcher.UIThread.UnhandledException</c>（<see cref="DispatcherUnhandledExceptionEventArgs"/>），
    ///      它覆盖 UI 线程上未被捕获的异常，**能记日志、也能标记 <c>Handled</c> 让进程别死**。
    ///    · 原版对「布局循环」(LayoutCycleException) 单独网开一面（DEBUG 下照样炸以便定位）；
    ///      Avalonia 没有这个异常类型，故不做该特判 —— TODO(win7)：若将来 Avalonia 侧出现同类布局异常，再补判断。
    /// </summary>
    private void OnUnhandledException(object? sender, DispatcherUnhandledExceptionEventArgs e)
    {
        var ex = e.Exception;

        try
        {
            if (ex is not null) Telemetry.TrackException(ex, "xaml_unhandled");

            // 排障信息：当时停在哪一页 + 窗口多大（只在特定尺寸下冒出来的问题，没有这两个数无从复现）
            var where = "page=" + Pages.ShellPage.CurrentTag;
            try
            {
                if (MainWindow is { } win)
                    where += $" window={win.ClientSize.Width:0}x{win.ClientSize.Height:0}";
            }
            catch { /* 拿不到窗口尺寸不影响记录 */ }

            // ⚠️ 移植说明：不再自己往根目录写 crash.log —— 日志统一走 Core.AppLog（logs\crash.log），
            //    与 AppLog.MigrateLegacyFiles 的"分区"保持一致（原版同样改成 AppLog.Write）。
            Core.AppLog.Write("crash", Core.LogLevel.Error, $"[{where}] {ex?.Message}\n{ex}");
        }
        catch { /* 记录失败也不影响 */ }

        // 开发期不静默退出，异常直接炸出来方便定位（VS 里能断到现场）。
        // 发布版必须兜底：老师正在上课，任何一个页面级异常都不该让整个应用消失、界面状态全丢
        // —— 那比"这个功能坏了"严重得多。上面已经写进 crash.log 了，事后能查；这里只负责"别死"。
#if DEBUG
        e.Handled = false;
#else
        e.Handled = true;
#endif
    }
}
