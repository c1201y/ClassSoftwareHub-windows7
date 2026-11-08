using System;
using System.Reflection;
using Avalonia;
using Avalonia.Media;
using Avalonia.Rendering.Composition;
using Avalonia.Styling;

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
    /// 离屏诊断进程（<c>--resource-check</c>）。这类进程只加载 XAML 资源、不建窗口，
    /// 因此 <see cref="App"/> 构造函数里必须跳过单实例互斥体，否则会去"叫醒"正在运行的
    /// 正式实例、甚至抢不到互斥体直接退出，自检还没跑就结束了。
    /// </summary>
    internal static bool OffscreenDiagnostic;

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
        // ⛔ 必须是 Main 的第一句：CLR 只在**进程启动之前**读崩溃转储开关，
        //    晚一步就再也补不上了。没有注入时这里会带开关重启自身并让本进程退出。
        //    理由见 CrashDump.TryHandoff（托管崩溃走 CLR 自己的 FailFast，WER 抓不到）。
        if (Platform.CrashDump.TryHandoff(args)) return;

        // ⛔ 平台分支模拟必须排在**第一次访问 OsInfo 之前**：TryHandoff 之后的
        //    --mathshot / BuildAvaloniaApp 等路径都会触发静态版本判定，晚一步分支就被冻死。
        //    与 --simulate-win10 互斥，同时给时以 Win7 为准。
        Platform.OsInfo.SimulateWin7 =
            Array.Exists(args, a => a.Equals("--simulate-win7", StringComparison.OrdinalIgnoreCase))
            || Environment.GetEnvironmentVariable("CSH_SIMULATE_WIN7") == "1";
        Platform.OsInfo.SimulateWin10 =
            Array.Exists(args, a => a.Equals("--simulate-win10", StringComparison.OrdinalIgnoreCase))
            || Environment.GetEnvironmentVariable("CSH_SIMULATE_WIN10") == "1";

        // 排障：数学排版离屏自检（--mathshot <输出 png>）。开主界面之前就跑完并退出，
        // 用来核对分数/上标/根号的基线位置（见 Platform/MathShot）。只用于开发验证。
        var shotAt = Array.FindIndex(args, a => a.Equals("--mathshot", StringComparison.OrdinalIgnoreCase));
        if (shotAt >= 0)
        {
            var outPath = shotAt + 1 < args.Length ? args[shotAt + 1] : "mathshot.png";
            BuildAvaloniaApp().SetupWithoutStarting();
            Platform.MathShot.Run(outPath);
            ShutdownOffscreen();
            return;
        }

        // 排障：静态资源键自检（--resource-check）。不开主界面、不开窗口。
        // XAML 里的 {StaticResource} 在编译期不校验，键取不到时是**页面加载当场抛异常**；
        // 这个入口把全局关键键逐一 TryGetResource 一遍，用来在换 FA 版本 / 改 App.axaml
        // 之后快速确认没有把资源键弄丢（进程返回码 0=全通过，1=有缺失）。
        if (Array.Exists(args, a => a.Equals("--resource-check", StringComparison.OrdinalIgnoreCase)))
        {
            OffscreenDiagnostic = true;   // App 构造函数据此跳过单实例互斥体
            BuildAvaloniaApp().SetupWithoutStarting();
            RunResourceCheck();
            ShutdownOffscreen();
            return;
        }

        _forceSoftwareRender = Array.Exists(args,
            a => a.Equals("--software-render", StringComparison.OrdinalIgnoreCase));

        ForceTakeover = Array.Exists(args,
            a => a.Equals("--takeover", StringComparison.OrdinalIgnoreCase));

        _forceGpuRender = Array.Exists(args,
            a => a.Equals("--gpu-render", StringComparison.OrdinalIgnoreCase));

        // ⛔ 原生崩溃转储必须在这里注册，排在**任何可能崩的代码之前**。
        //    现场反馈的「程序有时候直接没了」不属于 CLR 的异常通道（访问冲突 / 栈溢出 /
        //    FailFast 都是进程当场结束，钩子来不及跑），只能靠 WER 在进程死亡时写内存转储。
        //    注册本身只写注册表，不注入进程内钩子 —— 理由见 CrashDump 类注释。
        Platform.CrashDump.Init(args);

        // 排障：故意制造一次访问冲突，验证上面的转储链路在真机上确实会落地。
        // 仅在显式传 --test-crash 时触发，正式使用不会走到。
        Platform.CrashDump.RunSelfTestIfRequested(args);

        // ⚠️ Win7 抽号页随机灰矩形问题（真机复核确认）：
        //   现象：Win7 抽号页出现位置随机的浅灰矩形残影，每帧形状不同。
        //   根因：Avalonia 组合渲染默认按脏矩形局部重绘 —— 每帧只重画变化区域，
        //   其余像素依赖渲染表面"保住上一帧"。Win7 老显卡（WGL / 老 Intel ICD）的
        //   表面保帧不可靠，未重绘区域会露出上一帧残留或随机垃圾像素。
        //   UseSaveLayerRootClip（脏区先画进中间表面再合成，Skia bug
        //   issues.skia.org/issues/327877721 的官方绕法）只解决 saveLayer 裁剪一类问题，
        //   不改变对表面保帧的依赖 —— 真机复核确认它单独启用无效。
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

    /// <summary>
    /// 离屏自检（<c>--mathshot</c> / <c>--resource-check</c>）跑完后的收尾。
    ///
    /// <para>
    /// 这两条路径用 <c>SetupWithoutStarting()</c> 起 App 后直接 <c>return</c>，
    /// 不走经典桌面生命周期的正常退出流程，因此 <see cref="Services.LogHome"/> 打的
    /// <c>session.live</c> 标记不会被摘掉 —— 下次正常启动会把它误判成「上次运行未正常退出」
    /// 并在 crash.log 里留一条假的异常终止结论。这里主动补上正常退出的收尾。
    /// </para>
    /// </summary>
    private static void ShutdownOffscreen()
    {
        try { Services.LogHome.MarkCleanExit(); } catch { }
        try { Core.AppLog.Shutdown(); } catch { }
    }

    /// <summary>
    /// 离屏静态资源键自检（<c>--resource-check</c>）。逐键调用 <c>TryGetResource</c>，
    /// 任一键取不到就把进程返回码置 1。
    ///
    /// <para>
    /// 为什么值得常驻：XAML 编译器不校验 <c>{StaticResource}</c> 的键，键丢了要等页面
    /// 加载时才炸；而 <c>App.axaml</c> 的全局资源键被 20+ 个页面引用（例如
    /// <c>AccentButtonStyle</c>），换 FluentAvalonia 版本最容易踩这一脚。
    /// </para>
    /// </summary>
    private static void RunResourceCheck()
    {
        // ⛔ 这份清单**必须覆盖全部被 XAML 引用的全局键**，否则只是假安全感。
        //    上一版只硬编码了 11 个键，而仓库实际引用了 59 个不同的键 ——
        //    引用量最大的三个（CardStrokeColorDefaultBrush 94 次、
        //    TextFillColorSecondaryBrush 78 次、CardBackgroundFillColorDefaultBrush 65 次）
        //    全都不在自检范围内：它们一旦在 FluentAvalonia 升级时改名，
        //    104 个 axaml 的卡片边框和次要文字会同时静默消失，而自检仍然绿灯。
        //    本清单 = 仓库自绘/自补的键 + FluentAvalonia 提供的全局键（按引用量取），
        //    新增全局键时请一并加到这里。
        string[] keys =
        {
            // ── 仓库自己补的键（FluentAvalonia 不提供）──
            "AccentButtonStyle",
            "SystemAccentColorPrimaryBrush",
            "CshCaptionGlyphBrush",
            "CshCaptionHoverBrush",
            "HubAiCardBorder",

            // ── FluentAvalonia 提供的键（Acrylic/Mica 之外的常用全局键）──
            "SymbolThemeFontFamily",
            "SystemAccentColor",
            "AccentButtonBackground",
            "AccentButtonBackgroundPointerOver",
            "AccentButtonBackgroundPressed",
            "AccentButtonBackgroundDisabled",
            "AccentButtonForeground",
            "AccentButtonForegroundDisabled",
            "AccentButtonBorderBrush",
            "AccentFillColorDefaultBrush",
            "AccentTextFillColorPrimaryBrush",

            // ── 卡片 / 描边 / 分隔线（引用量最高的三个必须在内）──
            "CardBackgroundFillColorDefaultBrush",
            "CardBackgroundFillColorSecondaryBrush",
            "CardStrokeColorDefaultBrush",
            "DividerStrokeColorDefaultBrush",

            // ── 填充 / 底色 ──
            "ControlFillColorDefaultBrush",
            "ControlFillColorSecondaryBrush",
            "ControlStrokeColorDefaultBrush",
            "LayerFillColorDefaultBrush",
            "SolidBackgroundFillColorBaseBrush",
            "SolidBackgroundFillColorSecondaryBrush",
            "SubtleFillColorSecondaryBrush",
            "ApplicationPageBackgroundThemeBrush",
            "FlyoutPresenterBackground",

            // ── 文字 ──
            "TextFillColorPrimaryBrush",
            "TextFillColorSecondaryBrush",
            "TextFillColorTertiaryBrush",
            "TextOnAccentFillColorPrimaryBrush",

            // ── 状态色 ──
            "SystemFillColorCautionBrush",
            "SystemFillColorCriticalBrush",
        };

        // ⛔ 必须两套主题都查：Dark 字典里有若干键只存在于深色分支
        //    （HubAiTitleForeground、ComboBoxDropDownBackground 等）。
        //    只查 Light 的话，深色字典漏键永远发现不了 —— 用户切到深色主题
        //    才会看到"某个文字看不见"这种极难定位的问题。
        var variants = new[] { ThemeVariant.Light, ThemeVariant.Dark };

        var failed = 0;
        var total = 0;

        foreach (var variant in variants)
        {
            var variantName = variant == ThemeVariant.Dark ? "Dark" : "Light";

            foreach (var key in keys)
            {
                total++;

                object? value = null;
                var ok = false;
                try
                {
                    ok = Application.Current?.TryGetResource(key, variant, out value) == true
                         && value is not null;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[resource-check] FAIL  {variantName}/{key}  异常={ex.GetType().Name}: {ex.Message}");
                    failed++;
                    continue;
                }

                if (!ok)
                {
                    Console.WriteLine($"[resource-check] FAIL  {variantName}/{key}  ← 键不存在，页面加载会抛异常");
                    failed++;
                }
                else
                {
                    Console.WriteLine($"[resource-check] PASS  {variantName}/{key}  ({value!.GetType().Name})");
                }
            }
        }

        // 结论行只用 ASCII：诊断输出常被重定向到 cmd / 日志文件，中文在 OEM 代码页下会变乱码。
        Console.WriteLine($"[resource-check] RESULT {total - failed}/{total} OK, {failed} MISSING");
        Environment.ExitCode = failed == 0 ? 0 : 1;
    }

    /// <summary>Avalonia 构建器。设计器（Previewer）也会调用它，所以必须是 public static。</summary>
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            // ⛔ Win7 关键：**全站默认字体** —— 内嵌 HarmonyOS Sans SC。
            //
            //    Windows 7 的中文默认字体是宋体（SimSun），14px 以下带点阵内嵌位图，
            //    正文呈锯齿/断笔的"老界面"观感；标题加粗还会走 GDI 合成粗体，边缘发糊
            //    （2026-11-06 用户在教室机截图确认：导航项、对话框标题都像宋体点阵）。
            //    FluentAvalonia 的字体栈只覆盖英文（Segoe UI），中文一律落到系统默认，
            //    所以必须在字体管理器这一层声明，而不是逐控件写 FontFamily。
            //
            //    字体文件：Assets/Fonts/HarmonyOS_Sans_SC_{Light,Medium,Bold}.ttf，随程序打包
            //    （见 csproj 里三条 AvaloniaResource）。地址写成**目录 + #族名**：
            //    Avalonia 会把这个目录下的同族字重全部索引进来，FontWeight=Bold 才能拿到
            //    真粗体，而不是拿 Medium 做伪粗体。
            //
            //    FontFallbacks 是兜底：字体文件读不到、或某个码位（符号/生僻字/emoji）字体里没有时，
            //    依次退到 Win7 自带的雅黑（有则用）、宋体，至少不会出现豆腐块。
            .With(new FontManagerOptions
            {
                DefaultFamilyName = "avares://ClassSoftwareHub/Assets/Fonts#HarmonyOS Sans SC",
                FontFallbacks = new[]
                {
                    new FontFallback
                    {
                        FontFamily = new FontFamily("Microsoft YaHei UI"),
                        UnicodeRange = UnicodeRange.Parse("0000-10FFFF"),
                    },
                    new FontFallback
                    {
                        FontFamily = new FontFamily("Microsoft YaHei"),
                        UnicodeRange = UnicodeRange.Parse("0000-10FFFF"),
                    },
                    new FontFallback
                    {
                        FontFamily = new FontFamily("SimSun"),
                        UnicodeRange = UnicodeRange.Parse("0000-10FFFF"),
                    },
                },
            })
            // ⚠️⚠️ 渲染后端降级链 —— 对本移植版是**生死攸关**的一条配置，不是可选优化。
            //
            // 目标机是教室里的 Win7 老机器，显卡驱动经常不支持 D3D11。实机日志已经撞上：
            //     [OpenGL] Unable to initialize ANGLE-based rendering with DirectX11:
            //              'Avalonia.OpenGL.OpenGlException: No adapters found'
            //     [OpenGL] Unknown requested PlatformApi 'DirectX11'
            // ANGLE(D3D11) 起不来 → 渲染器建不出来 → 窗口建不出来 → 进程随即退出。
            //
            // 定版（真机复核后）：
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
