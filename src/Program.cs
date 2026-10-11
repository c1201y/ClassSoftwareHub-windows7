using System;
using System.Reflection;
using Avalonia;
using Avalonia.Rendering.Composition;

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
    /// 放行 GPU 渲染链（命令行加 <c>--gpu-render</c>）。
    ///
    /// Win7 默认已压到软件渲染（见 BuildAvaloniaApp 里的降级链说明）。这个参数用于
    /// 在 Win7 上临时恢复 ANGLE → WGL → 软件的 GPU 降级链做对比验证；
    /// 正式使用中若 GPU 路径再出渲染问题，去掉参数即可回到默认软件渲染。
    /// </summary>
    private static bool _forceGpuRender;

    /// <summary>
    /// 直接清场接管（命令行加 <c>--takeover</c>）。
    ///
    /// 单实例冲突时跳过"敲门等待"，只要发现现有实例拿不出一个可见的主窗口就直接结束它。
    /// 现场排障用：万一看不到界面又不想开任务管理器，加这个参数双击一下就行。
    /// </summary>
    internal static bool ForceTakeover;

    /// <summary>
    /// Win7 上 <see cref="CompositionOptions.UseSaveLayerRootClip"/> 是否注册成功
    /// （见 Main 里的说明）。仅供诊断参考；默认软件渲染路径不依赖它。
    /// </summary>
    internal static bool RenderFixVerified;

    [STAThread]
    public static void Main(string[] args)
    {
        _forceSoftwareRender = Array.Exists(args,
            a => a.Equals("--software-render", StringComparison.OrdinalIgnoreCase));

        ForceTakeover = Array.Exists(args,
            a => a.Equals("--takeover", StringComparison.OrdinalIgnoreCase));

        _forceGpuRender = Array.Exists(args,
            a => a.Equals("--gpu-render", StringComparison.OrdinalIgnoreCase));

        // 排障：强制按 Win7 分支跑（开发机上复现教室机的代码路径，见 OsInfo.SimulateWin7）。
        // ⚠️ 必须排在**第一次访问 OsInfo 之前** —— 一旦有代码读过版本判定，再开就晚了。
        Platform.OsInfo.SimulateWin7 =
            Array.Exists(args, a => a.Equals("--simulate-win7", StringComparison.OrdinalIgnoreCase))
            || Environment.GetEnvironmentVariable("CSH_SIMULATE_WIN7") == "1";

        // ⚠️ 2026-10-08/09（抽号页随机灰矩形，真机两轮复核）：
        //   现象：Win7 抽号页出现位置随机的浅灰矩形残影，每帧形状不同。
        //   根因：Avalonia 组合渲染默认按脏矩形局部重绘 —— 每帧只重画变化区域，
        //   其余像素依赖渲染表面"保住上一帧"。Win7 老显卡（WGL / 老 Intel ICD）的
        //   表面保帧不可靠，未重绘区域会露出上一帧残留或随机垃圾像素。
        //   UseSaveLayerRootClip（脏区先画进中间表面再合成，Skia bug
        //   issues.skia.org/issues/327877721 的官方绕法）只解决 saveLayer 裁剪一类问题，
        //   不改变对表面保帧的依赖 —— 2026-10-09 真机复核确认它单独启用无效。
        //   处置：Win7（含 --simulate-win7）默认直接走软件渲染，帧缓冲在进程内存里，
        //   保帧由进程自己保证，随机灰矩形从机制上不可能出现；需要验证 GPU 路径时
        //   加 --gpu-render 临时放行降级链（此时仍会带 UseSaveLayerRootClip）。
        //   软件渲染对这种"大部分时间静止"的界面开销可接受；WebView2 独立进程渲染，不受影响。
        if (Platform.OsInfo.IsWindows7)
        {
            // AvaloniaLocator.CurrentMutable 在 NuGet 的 ref 程序集里被裁掉了（编译期不可见，
            // 运行时仍全量公开）—— 注册只能走反射，一次性开销忽略不计。
            // 注册结果记录到 RenderFixVerified：注册失败只会表现为"还是老样子"，
            // 排障时必须能分辨「没注册上」和「注册了但对该机器不够」这两种情况。
            try
            {
                var locatorType = typeof(Avalonia.Media.Color).Assembly
                    .GetType("Avalonia.AvaloniaLocator", throwOnError: true)!;
                var locator = locatorType.InvokeMember("CurrentMutable",
                    BindingFlags.Public | BindingFlags.Static | BindingFlags.GetProperty,
                    null, null, Array.Empty<object>())!;
                var helper = locatorType.GetMethod("Bind")!
                    .MakeGenericMethod(typeof(CompositionOptions))
                    .Invoke(locator, Array.Empty<object>())!;
                helper.GetType().GetMethod("ToConstant")!
                    .MakeGenericMethod(typeof(CompositionOptions))
                    .Invoke(helper, new object?[] { new CompositionOptions { UseSaveLayerRootClip = true } });

                var current = locatorType.InvokeMember("Current",
                    BindingFlags.Public | BindingFlags.Static | BindingFlags.GetProperty,
                    null, null, Array.Empty<object>())!;
                var readBack = current.GetType().GetMethod("GetService")!
                    .Invoke(current, new object?[] { typeof(CompositionOptions) }) as CompositionOptions;
                RenderFixVerified = readBack?.UseSaveLayerRootClip == true;
            }
            catch
            {
                RenderFixVerified = false;   // 注册不上不挡启动，只影响 --gpu-render 时的渲染质量
            }
        }

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
            //
            // 2026-10-09 定版（真机两轮复核后）：
            //   · Win7 默认**软件渲染**：脏矩形局部重绘依赖渲染表面保帧，老显卡驱动
            //     （WGL / 老 Intel ICD）保帧不可靠，会产生随机灰矩形残影；
            //     软件渲染的帧缓冲在进程内存里，保帧天然可靠，从机制上根治该问题。
            //     实测教室机 D3D11CreateDevice 返回 DXGI_ERROR_UNSUPPORTED，
            //     GPU 降级链本来就只能落到 WGL —— 与软件渲染相比提速有限，还带渲染缺陷。
            //   · --gpu-render 可在 Win7 上临时恢复 GPU 降级链（对比验证用）；
            //   · --software-render 在任何系统上都强制只剩软件渲染（现场排障用）；
            //   · 非 Win7 系统维持 GPU 降级链：ANGLE(D3D11) → WGL(OpenGL) → 软件渲染。
            // 另：先探 D3D11 是为了避免在没有 D3D11 的机器上白等 ANGLE 失败
            //    （省一秒多启动时间，也少刷一段没意义的英文报错）。
            .With(new Win32PlatformOptions
            {
                RenderingMode = _forceSoftwareRender
                    ? new[] { Win32RenderingMode.Software }
                    : Platform.OsInfo.IsWindows7 && !_forceGpuRender
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
