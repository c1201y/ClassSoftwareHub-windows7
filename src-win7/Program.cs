using System;
using Avalonia;

namespace ClassSoftwareHub.Desktop;

/// <summary>
/// 程序入口。
///
/// 与 WinUI 原版 <c>App.xaml.cs</c> 的分工不同：
///   · WinUI 由 <c>Microsoft.UI.Xaml.Application</c> 承担入口，构造函数里做单实例判定；
///   · Avalonia 这里用经典桌面生命周期，单实例判定仍放在 <see cref="App"/> 构造函数里，
///     保持与原版完全一致的「第二个实例敲门 → 第一个实例亮窗」行为。
/// </summary>
internal static class Program
{
    // ⚠️ 单实例互斥体 / 叫醒事件的逻辑在 App 构造函数，不在 Main —— 别挪到这里，
    //    否则 Avalonia 还没起来就去抢互斥体，托盘恢复那条路径会拿不到主窗口。
    /// <summary>
    /// 强制软件渲染（命令行加 <c>--software-render</c>）。
    ///
    /// 应急开关：有些老显卡 D3D11 和 OpenGL 都不给用，自动降级链也救不回来时，
    /// 让用户能手动把渲染压到软件实现上 —— 界面会慢一点，但至少能出来。
    /// 正式版也认这个参数（现场排障时不用专门换包）。
    /// </summary>
    private static bool _forceSoftwareRender;

    /// <summary>
    /// 直接清场接管（命令行加 <c>--takeover</c>）。
    ///
    /// 单实例冲突时跳过"敲门等待"，只要发现现有实例拿不出一个可见的主窗口就直接结束它。
    /// 现场排障用：万一看不到界面又不想开任务管理器，加这个参数双击一下就行。
    /// </summary>
    internal static bool ForceTakeover;

    [STAThread]
    public static void Main(string[] args)
    {
        _forceSoftwareRender = Array.Exists(args,
            a => a.Equals("--software-render", StringComparison.OrdinalIgnoreCase));

        ForceTakeover = Array.Exists(args,
            a => a.Equals("--takeover", StringComparison.OrdinalIgnoreCase));

        // 排障：强制按 Win7 分支跑（开发机上复现教室机的代码路径，见 OsInfo.SimulateWin7）。
        // ⚠️ 必须排在**第一次访问 OsInfo 之前** —— 一旦有代码读过版本判定，再开就晚了。
        Platform.OsInfo.SimulateWin7 =
            Array.Exists(args, a => a.Equals("--simulate-win7", StringComparison.OrdinalIgnoreCase))
            || Environment.GetEnvironmentVariable("CSH_SIMULATE_WIN7") == "1";

#if CSH_CONSOLE
        // 控制台版：先把输出通道架好，之后发生的一切（包括启动期崩溃）都看得见。
        // 必须排在 BuildAvaloniaApp() 之前 —— Avalonia 起不来正是最需要看到输出的时候。
        Platform.DiagConsole.Init(args);

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            Platform.DiagConsole.Fatal("Avalonia 主循环启动失败（界面根本没起来）", ex);
            Platform.DiagConsole.Pause();   // 停住，别让黑窗一闪而过
        }
        finally
        {
            Platform.DiagConsole.Shutdown();
        }
#else
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
#endif
    }

    /// <summary>Avalonia 构建器。设计器（Previewer）也会调用它，所以必须是 public static。</summary>
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            // ⚠️⚠️ 渲染后端降级链 —— 对本移植版是**生死攸关**的一条配置，不是可选优化。
            //
            // 目标机是教室里的 Win7 老机器，显卡驱动经常不支持 D3D11。实机日志已经撞上：
            //     [OpenGL] Unable to initialize ANGLE-based rendering with DirectX11:
            //              'Avalonia.OpenGL.OpenGlException: No adapters found'
            //     [OpenGL] Unknown requested PlatformApi 'DirectX11'
            // ANGLE(D3D11) 起不来 → 渲染器建不出来 → 窗口建不出来 → 进程随即退出。
            // 用户看到的就是「主界面不显示 + 控制台一闪就没了」，而日志里只有一句
            // 对普通人毫无意义的 PlatformApi 报错。
            //
            // 所以这里把降级链**显式写死**，保证显卡再老也能落到一个能用的后端：
            //     ANGLE(D3D11) → WGL(OpenGL) → 软件渲染
            // 软件渲染是最后的保底。教室软件下载站这种静态 UI 用它完全够用，
            // 而且 WebView2 是独立进程自己渲染的，不受这里影响。
            //
            // ⚠️ 2026-10-02 补：ANGLE 后端本身是**架在 D3D11 上**的。目标机上
            //    D3D11CreateDevice 直接返回 0x887A0004（DXGI_ERROR_UNSUPPORTED），
            //    那就完全没有必要去试 ANGLE —— 试了也是白等一秒多，还在控制台刷出一段
            //    `No adapters found` / `Unknown requested PlatformApi` 的英文报错，
            //    对现场排障的人纯粹是噪音。所以先探一下 D3D11：没有就跳过 ANGLE。
            // 另：命令行加 --software-render 可手动压到只剩软件渲染（见 _forceSoftwareRender）。
            .With(new Win32PlatformOptions
            {
                RenderingMode = _forceSoftwareRender
                    ? new[] { Win32RenderingMode.Software }
                    : Platform.GpuInfo.IsD3D11Available
                        ? new[]
                        {
                            Win32RenderingMode.AngleEgl,
                            Win32RenderingMode.Wgl,
                            Win32RenderingMode.Software,
                        }
                        : new[]
                        {
                            Win32RenderingMode.Wgl,
                            Win32RenderingMode.Software,
                        },
            })
#if CSH_CONSOLE
            // 诊断版把 Avalonia 内部日志放宽到 Information（默认只到 Warning）：
            // 平台/字体/绑定初始化出了什么问题都在这条流里，监听器已把它接到控制台
            // （见 DiagConsole.AvaloniaTraceListener，里面会把 [Layout] 这类刷屏噪音滤掉）。
            .LogToTrace(Avalonia.Logging.LogEventLevel.Information);
#else
            .LogToTrace();
#endif
}
