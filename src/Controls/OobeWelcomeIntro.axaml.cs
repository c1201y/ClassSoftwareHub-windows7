using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using ClassSoftwareHub.Desktop.Core;
using ClassSoftwareHub.Desktop.Services;
using ClassSoftwareHub.Desktop.Views.Oobe;

namespace ClassSoftwareHub.Desktop.Controls;

/// <summary>
/// 欢迎页开场（2026-10-31，按上游 12 格分镜实现，自 dv1.2.0-insider1.8 移植）。
///
/// <para>
/// 一出戏一条时间线，全部在 UI 线程上用 <c>await Task.Delay</c> 串起来：
/// 跑马灯滚出 → 标语逐字曲线飞入 → 乱码 → 收成品牌名 → 品牌名下移、周围内容亮起。
/// 每个阶段只改自己的那几个元素，前一段的元素已经落定就直接停掉，不做"全场统一重排"。
/// </para>
///
/// <para>
/// ⛔ 别再往这里加字距收拢（ClassIsland 那套 Spacing/MinWidth 收紧）。
/// ⛔ 品牌名不是独立的一份文字：它就是 <see cref="Wordmark"/>，靠 TranslateY 先抬到画面中线，
///    最后落回排版里的自然位置 —— 所以中线上的字和落下来的字永远是同一个元素。
/// </para>
///
/// <para>
/// ⚠️ 移植：WinUI 的 DoubleAnimation/Storyboard 在 Avalonia 没有，这里改成手写补间
///    （<see cref="TweenAsync"/> + 逐帧算位置）。好处是「动画跑完回到基准值」这类坑不存在，
///    而且每个字四五个属性同帧推进，不会各飞各的。
/// </para>
/// </summary>
public partial class OobeWelcomeIntro : UserControl
{
    private const string Slogan = "Fast, Useful & Convenient for class";
    private const string Brand = "ClassSoftwareHub";
    private const double SloganFontSize = 30;

    /// <summary>标语落在画面中线略偏下（分镜 3.1 的"居中偏下"）。</summary>
    private const double LineOffsetY = 4;

    private const string GlyphPool =
        "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789#$%&*+-=/<>";

    /// <summary>跑马灯里的软件功能名（分镜第 2 幕）。</summary>
    private static readonly string[] Features =
    {
        "图片取色", "编码", "哈希工具", "函数图像分成求解", "化学方程式配平", "关闭窗口",
        "关闭前台应用", "反馈中心", "提交软件", "系统功能关闭启用", "系统优化",
        "希沃白板强制结束", "亮度调节", "日志回溯", "快速设置", "定时关机", "音量调节",
        "放大镜", "截图钉图", "任务视图", "返回桌面", "全屏时钟", "秒表计时",
        "常用系统修复指令", "剪贴板历史", "快速打开", "随机抽号", "课堂计时",
        "AppData文件管理", "局域网快速配对", "安全选项快速打开", "系统分类",
        "HubAI", "软件下载", "远程教室喊话", "任务管理器快速打开", "开机启动项管理", "时钟",
    };

    private sealed class MarqueeRow
    {
        public TextBlock Block = null!;
        public TranslateTransform Move = null!;
        public double StartX;
        public double CopyWidth;

        /// <summary>每秒滚多少像素（= 一个周期宽度 / 一个周期的时长）。</summary>
        public double SpeedPxPerMs;
    }

    private sealed class GlyphSlot
    {
        public TextBlock Block = null!;
        public ScaleTransform Scale = null!;
        public TranslateTransform Move = null!;
        public double Width;

        /// <summary>空格占位：只提供间距，不画东西，也不会被认成品牌字母。</summary>
        public bool Space;

        /// <summary>乱码时可以换的字形：按字位宽度挑出来的，换上去不会把邻字挤开。</summary>
        public string[] Glyphs = Array.Empty<string>();

        /// <summary>存活字位要认成的品牌名字母；多余字位为 null。</summary>
        public char? Target;

        /// <summary>认成品牌字母后要占的字宽（可能和原来的字宽不一样）。</summary>
        public double BrandWidth;

        /// <summary>
        /// 眼下这格占多宽。**永远是硬值**：字块此刻有多宽就占多宽（没了就 0），不做补间 ——
        /// Nick 2026-10-31：这里要像打字机，字走多宽，行就是多宽，别平滑收缩。
        /// </summary>
        public double Advance;

        /// <summary>认字时刻 / 多余字位消失的时刻（相对乱码开始，毫秒）。</summary>
        public double ResolveAt = double.MaxValue;
        public double EliminateAt = double.MaxValue;

        /// <summary>已经认成品牌字母，不再参与乱码。</summary>
        public bool Settled;

        /// <summary>已经消失：字块收起来、格子宽度归零，后面的字立刻补上来。</summary>
        public bool Gone;

        public double NextTick;
    }

    private readonly List<MarqueeRow> _marqueeRows = new();
    private readonly List<GlyphSlot> _slots = new();
    private readonly Random _random = new();

    private DispatcherTimer? _marqueeTimer;
    private long _marqueeStartedAt;

    private double _stageWidth;
    private double _stageHeight;
    private double _centerY;
    private double _wordmarkLift;

    private bool _built;
    private bool _requested;
    private bool _played;
    private bool _loaded;
    private bool _stop;
    private bool _completed;

    // ⚠️ Avalonia 的 x:Name 字段生成器**不给属性元素里的 Transform 生成字段**
    //    （与主窗口 WebSheetShift 同一取舍）：从命名控件的 RenderTransform 里现取。
    private TranslateTransform MarqueeShift =>
        (TranslateTransform)((TransformGroup)MarqueeLayer.RenderTransform!).Children[1];

    private ScaleTransform LogoScale =>
        (ScaleTransform)((TransformGroup)Logo.RenderTransform!).Children[0];

    private TranslateTransform LogoShift =>
        (TranslateTransform)((TransformGroup)Logo.RenderTransform!).Children[1];

    private TranslateTransform WelcomeShift => (TranslateTransform)WelcomeLine.RenderTransform!;

    private TranslateTransform WordmarkShift => (TranslateTransform)Wordmark.RenderTransform!;

    private TranslateTransform VersionShift => (TranslateTransform)VersionLine.RenderTransform!;

    public event EventHandler? Completed;

    public OobeWelcomeIntro()
    {
        InitializeComponent();

        VersionLine.Text = $"Desktop({(SystemInfo.IsWin11 ? "Win11" : "Win10")}) {ShellConfig.ShellVersion}";
        LoadLogo();

        Loaded += (_, _) =>
        {
            _loaded = true;
            EnsureBuilt();
            if (_requested) StartPlay();
        };
        SizeChanged += (_, _) => EnsureBuilt();
        Unloaded += (_, _) => OnUnloaded();
    }

    /// <summary>窗口真的露脸之后由欢迎页调。幂等，重复调只会播一次。</summary>
    public void Play()
    {
        _requested = true;
        if (_built && _loaded) StartPlay();
    }

    // ── 搭台 ─────────────────────────────────────────────────────────────

    private void EnsureBuilt()
    {
        if (_built) return;

        var width = Stage.Bounds.Width;
        var height = Stage.Bounds.Height;
        if (width < 200 || height < 150) return;

        _built = true;
        _stageWidth = width;
        _stageHeight = height;
        _centerY = height / 2 + LineOffsetY;

        BuildMarquee();
        BuildSloganSlots();
        BuildWordmarkGlyphs();
        ComputeWordmarkLift();

        if (Wordmark.Bounds.Height <= 0)
            Dispatcher.UIThread.Post(ComputeWordmarkLift, DispatcherPriority.Loaded);

        if (_requested && _loaded) StartPlay();
    }

    /// <summary>图标的位子先占好（透明度 0），落下前整体不可见。</summary>
    private void LoadLogo()
    {
        try
        {
            var path = EmbeddedAssets.ExtractToCache("AppIcon-512.png", "app-icon.png");
            if (!string.IsNullOrEmpty(path) && File.Exists(path))
                Logo.Source = new Bitmap(path);
        }
        catch (Exception ex)
        {
            AppLog.Exception("oobe", ex, "欢迎页图标加载失败");
        }
    }

    private void BuildMarquee()
    {
        var layerWidth = _stageWidth * 2.2;
        var layerHeight = _stageHeight * 1.9;
        MarqueeLayer.Width = layerWidth;
        MarqueeLayer.Height = layerHeight;
        Canvas.SetLeft(MarqueeLayer, (_stageWidth - layerWidth) / 2);
        Canvas.SetTop(MarqueeLayer, (_stageHeight - layerHeight) / 2);

        var probe = new TextBlock { FontWeight = FontWeight.SemiBold };

        // 用不换行空格当分隔符：尾部空白不会被排版裁掉，量出来的周期宽度才和实际渲染一致，
        // 循环滚动时接缝处才不跳。
        const string gap = "\u00A0\u00A0\u00A0\u00A0";

        for (var i = 0; i < 13; i++)
        {
            var fontSize = 11.5 + _random.NextDouble() * 6.5;

            // 一个周期由 5~8 个功能名拼成；整行重复足够多次，滚一个周期也不会露白
            var names = new List<string>();
            var count = 5 + _random.Next(4);
            for (var n = 0; n < count; n++) names.Add(Features[_random.Next(Features.Length)]);
            var period = string.Join(gap, names) + gap;

            probe.FontSize = fontSize;
            probe.Text = period;
            probe.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var periodWidth = Math.Max(160, probe.DesiredSize.Width);

            var repeats = (int)Math.Ceiling(layerWidth / periodWidth) + 2;
            var builder = new StringBuilder(period);
            for (var r = 1; r < repeats; r++) builder.Append(period);

            var move = new TranslateTransform();
            var durationMs = 900 + _random.NextDouble() * 800;

            var row = new TextBlock
            {
                Text = builder.ToString(),
                FontSize = fontSize,
                FontWeight = FontWeight.SemiBold,
                Opacity = 0.24 + _random.NextDouble() * 0.5,
                IsHitTestVisible = false,
                RenderTransform = move,
                // 各行起点错开一点，别像表格一样齐
                Margin = new Thickness(-_random.NextDouble() * periodWidth * 0.8, 0, 0, 0),
            };

            MarqueeRows.Children.Add(row);
            _marqueeRows.Add(new MarqueeRow
            {
                Block = row,
                Move = move,
                StartX = -_random.NextDouble() * periodWidth,
                CopyWidth = periodWidth,
                SpeedPxPerMs = periodWidth / durationMs,
            });
        }
    }

    /// <summary>
    /// 把标语按字符摆到中线上。每个字母是一块 TextBlock（方便各自飞、各自乱码），
    /// 位置按**每个字自己的字宽累积**算好、并把这个宽度钉在 TextBlock 上。
    ///
    /// ⛔ 别改回 StackPanel 自然排布：乱码时 TextBlock.Text 一换，宽度就变，
    ///    后面所有字跟着重排，整行每 40ms 抖一下 —— 这正是用户说的"不流畅"。
    /// ⛔ 也别改回"量整串前缀宽度再左移"：TextBlock 会把行尾空格裁掉，空格会变成 0 宽。
    ///    现在逐字测量、空格用固定宽，两个坑都不沾。
    /// </summary>
    private void BuildSloganSlots()
    {
        var probe = new TextBlock { FontSize = SloganFontSize, FontWeight = FontWeight.Bold };

        double WidthOf(string text)
        {
            probe.Text = text;
            probe.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            return probe.DesiredSize.Width;
        }

        probe.Text = "Ag";
        probe.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var lineHeight = probe.DesiredSize.Height;

        // 用一个可见字符夹出空格宽度（"A A" - "AA"），比直接量 " " 可靠
        var spaceWidth = Math.Max(6, WidthOf("A A") - WidthOf("AA"));

        // 字形池按宽度分好档，字位只挑宽度相近的：换字时邻字不会互相压住
        var pool = new List<(string Text, double Width)>(GlyphPool.Length);
        foreach (var glyph in GlyphPool)
        {
            var text = glyph.ToString();
            pool.Add((text, WidthOf(text)));
        }

        string[] CandidatesFor(double width)
        {
            var list = new List<string>();
            foreach (var (text, w) in pool)
            {
                if (w >= width * 0.6 && w <= width * 1.45) list.Add(text);
            }
            if (list.Count < 4)
            {
                foreach (var (text, _) in pool) list.Add(text);
            }
            return list.ToArray();
        }

        var x = 0.0;
        foreach (var ch in Slogan)
        {
            if (ch == ' ')
            {
                // 空格也要占一个槽：整行是按「每格当下多宽」重排的，不留槽的话
                // 乱码一开始所有空格会一瞬间塌掉，整句会猛地缩一下。
                var scale = new ScaleTransform();
                var move = new TranslateTransform();
                var gapBlock = new TextBlock
                {
                    Text = " ",
                    FontSize = SloganFontSize,
                    FontWeight = FontWeight.Bold,
                    Width = spaceWidth,
                    Opacity = 0,
                    IsHitTestVisible = false,
                    RenderTransformOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative),
                    RenderTransform = Group(scale, move),
                };
                Canvas.SetLeft(gapBlock, x);
                Canvas.SetTop(gapBlock, 0);
                SloganLine.Children.Add(gapBlock);

                _slots.Add(new GlyphSlot
                {
                    Block = gapBlock,
                    Scale = scale,
                    Move = move,
                    Width = spaceWidth,
                    Advance = spaceWidth,
                    Space = true,
                });
                x += spaceWidth;
                continue;
            }

            var width = Math.Max(4, WidthOf(ch.ToString()));
            var glyphScale = new ScaleTransform();
            var glyphMove = new TranslateTransform();
            var block = new TextBlock
            {
                Text = ch.ToString(),
                FontSize = SloganFontSize,
                FontWeight = FontWeight.Bold,
                TextAlignment = TextAlignment.Center,
                Width = width,          // 钉死：乱码换字不重排
                Opacity = 0,
                IsHitTestVisible = false,
                RenderTransformOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative),
                RenderTransform = Group(glyphScale, glyphMove),
            };
            Canvas.SetLeft(block, x);
            Canvas.SetTop(block, 0);
            SloganLine.Children.Add(block);

            _slots.Add(new GlyphSlot
            {
                Block = block,
                Scale = glyphScale,
                Move = glyphMove,
                Width = width,
                Advance = width,
                Glyphs = CandidatesFor(width),
            });
            x += width;
        }

        Canvas.SetLeft(SloganLine, (_stageWidth - x) / 2);
        Canvas.SetTop(SloganLine, _centerY - lineHeight / 2);
    }

    /// <summary>缩放先、位移后 —— 与 WinUI CompositeTransform 的时序一致（位移在父空间里算）。</summary>
    private static TransformGroup Group(ScaleTransform scale, TranslateTransform move)
    {
        var group = new TransformGroup();
        group.Children.Add(scale);
        group.Children.Add(move);
        return group;
    }

    /// <summary>
    /// 品牌名也逐字摆：每个字母一块、字宽钉死。
    ///
    /// ⛔ 别改回一个 TextBlock 装整串：整串排版会做 kerning，跟中线上那行（逐字累积字宽）
    ///    差几个像素，交接时两套字叠在一起就是一层双影。逐字摆 ⇒ 两边字位一模一样，
    ///    交接直接换人，看不出来。
    /// </summary>
    private void BuildWordmarkGlyphs()
    {
        foreach (var ch in Brand)
        {
            Wordmark.Children.Add(new TextBlock
            {
                Text = ch.ToString(),
                FontSize = SloganFontSize,
                FontWeight = FontWeight.Bold,
                TextAlignment = TextAlignment.Center,
                Width = Math.Max(4, MeasureText(ch.ToString())),
                IsHitTestVisible = false,
            });
        }
    }

    /// <summary>
    /// 品牌名排版时在页面偏下，先记下它要抬多少才能坐到中线上（第 4–5 幕就在那里演）。
    /// 只在第一次布局后算一次 —— 窗口是固定尺寸，不需要跟着缩放重算。
    /// </summary>
    private void ComputeWordmarkLift()
    {
        if (Wordmark.Bounds.Height <= 0) return;

        WordmarkShift.Y = 0;
        var origin = Wordmark.TranslatePoint(new Point(0, 0), Stage);
        if (origin is null) return;

        _wordmarkLift = _centerY - (origin.Value.Y + Wordmark.Bounds.Height / 2);
        WordmarkShift.Y = _wordmarkLift;
    }

    // ── 时间线 ───────────────────────────────────────────────────────────

    private void StartPlay()
    {
        if (_played || !_built) return;
        _played = true;

        if (!OobeMotion.AnimationsEnabled)
        {
            ShowFinalStatic();
            RaiseCompleted();
            return;
        }

        _ = PlayAsync();
    }

    private async Task PlayAsync()
    {
        try
        {
            MarqueeCanvas.IsVisible = true;
            StartMarqueeScroll();
            _ = FadeAsync(MarqueeCanvas, 0, 1, 220);

            // 第 1 幕：空窗留一口气，再让跑马灯起来
            await Delay(320);
            if (_stop) return;

            // 第 2 幕：滚一段
            await Delay(1800);
            if (_stop) return;

            // 第 3 幕：整层向左下滚出，标语在中线上露出来
            _ = AnimateMarqueeExitAsync();
            await Delay(160);

            // 第 4 幕：逐字曲线飞入（和跑马灯退场有一小段重叠，衔接更顺）
            FlyInSlogan();
            await Delay(900);
            if (_stop) return;

            await Delay(260);
            if (_stop) return;

            // 第 5 幕：整行乱码，同时在乱码里逐渐认字、逐渐变短，最后正好缩成品牌名
            await ScrambleToBrand();
            if (_stop) return;

            await Delay(260);
            if (_stop) return;

            // 交接给真正的品牌名控件（同一套字位，一帧换过去看不出变化）
            HandOffToWordmark();
            if (_stop) return;

            await Delay(60);
            if (_stop) return;

            // 第 6 幕：品牌名落回自然位置，图标 / 欢迎语 / 版本号围上来
            _ = DescendAsync();
            await Delay(680);
            if (_stop) return;

            RaiseCompleted();
        }
        catch (Exception ex)
        {
            AppLog.Warn("oobe", "欢迎页开场失败：" + ex.Message);
            ShowFinalStatic();
            RaiseCompleted();
        }
    }

    private void StartMarqueeScroll()
    {
        StopMarquee();

        foreach (var row in _marqueeRows) row.Move.X = row.StartX;

        _marqueeStartedAt = Environment.TickCount64;
        _marqueeTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(33), DispatcherPriority.Render,
            (_, _) => TickMarquee());
        _marqueeTimer.Start();
    }

    private void TickMarquee()
    {
        if (_stop) return;

        var elapsed = Environment.TickCount64 - _marqueeStartedAt;
        foreach (var row in _marqueeRows)
        {
            var travel = row.SpeedPxPerMs * elapsed % row.CopyWidth;
            row.Move.X = row.StartX - travel;
        }
    }

    private async Task AnimateMarqueeExitAsync()
    {
        try
        {
            await TweenAsync(560, t =>
            {
                var p = EaseInQuart(t);
                MarqueeShift.X = -_stageWidth * 1.5 * p;
                MarqueeShift.Y = _stageHeight * 0.35 * p;
            });
        }
        catch (Exception ex)
        {
            AppLog.Debug("oobe", "跑马灯退场动画失败：" + ex.Message);
        }
        finally
        {
            if (!_stop)
            {
                StopMarquee();
                MarqueeCanvas.IsVisible = false;
            }
        }
    }

    /// <summary>每个字符从画面左侧外沿沿一条弧线落到自己的字位；带点横向拉伸当动态模糊。</summary>
    private void FlyInSlogan()
    {
        LineCanvas.IsVisible = true;
        LineCanvas.Opacity = 1;

        var plan = new List<(GlyphSlot Slot, double Delay, double Duration, double Jitter, double StartX)>();
        var order = 0;
        foreach (var slot in _slots)
        {
            var delay = order * 10 + _random.Next(0, 28);
            var duration = 250 + _random.NextDouble() * 130;
            var jitter = (_random.NextDouble() - 0.5) * 56;

            // 一律从画面左侧外沿起飞（不依赖字位，飞到的是相对位移）
            var startX = -(_stageWidth + 80 + _random.NextDouble() * 220);

            slot.Move.X = startX;
            slot.Move.Y = jitter;
            slot.Scale.ScaleX = 1.4;
            slot.Scale.ScaleY = 1.4;
            slot.Block.Opacity = 0;

            plan.Add((slot, delay, duration, jitter, startX));
            order++;
        }

        _ = FlyInAsync(plan);
    }

    private async Task FlyInAsync(List<(GlyphSlot Slot, double Delay, double Duration, double Jitter, double StartX)> plan)
    {
        const double total = 900;
        try
        {
            await TweenAsync(total, t =>
            {
                var elapsed = t * total;
                foreach (var (slot, delay, duration, jitter, startX) in plan)
                {
                    var local = elapsed - delay;

                    slot.Move.X = Lerp(startX, 0, EaseOutCubic(local / duration));
                    slot.Move.Y = Lerp(jitter, 0, EaseOutQuad(local / (duration * 0.9)));
                    slot.Scale.ScaleX = Lerp(1.4, 1, EaseOutCubic(local / (duration * 0.8)));
                    slot.Scale.ScaleY = Lerp(1.4, 1, EaseOutCubic(local / (duration * 0.8)));
                    slot.Block.Opacity = Math.Clamp((local - 30) / 90, 0, 1);
                }
            });
        }
        catch (Exception ex)
        {
            AppLog.Debug("oobe", "标语飞入动画失败：" + ex.Message);
        }
        finally
        {
            foreach (var (slot, _, _, _, _) in plan)
            {
                slot.Move.X = 0;
                slot.Move.Y = 0;
                slot.Scale.ScaleX = 1;
                slot.Scale.ScaleY = 1;
                slot.Block.Opacity = 1;
            }
        }
    }

    /// <summary>
    /// 第 5 幕：乱码和"字变少"是**同一段时间**里一起发生的 ——
    /// 每个字位各自换字、各自消失，整行按「每格当下多宽」逐帧重排，
    /// 多余的字位一没，后面的字立刻补上来；等噪声散完，这行已经是品牌名的排版。
    ///
    /// <para>
    /// ⛔ 宽度**一律是硬切**，不许做补间（Nick 2026-10-31）：这里要像打字机 ——
    ///    屏幕上此刻是哪些字，行宽就是那些字本身有多宽。字一没，行立刻变短；
    ///    字一认成品牌字母，占宽立刻变成那个字母的字宽。把它做成"平滑收缩"会让
    ///    行宽和实际字形对不上，看着像一条橡皮筋在缩。
    /// </para>
    ///
    /// <para>
    /// ⛔ 也别拆成"先乱码 → 再删字 → 最后整行收拢"三段：那样前两段行宽一动不动，
    ///    收拢那一下就是一次横向突变（用户要的是边乱边短）。
    /// </para>
    /// </summary>
    private async Task ScrambleToBrand()
    {
        var end = PrepareMorphTargets();
        foreach (var slot in _slots) slot.NextTick = 0;

        var start = Environment.TickCount64;
        while (!_stop)
        {
            var elapsed = Environment.TickCount64 - start;
            if (elapsed >= end) break;

            MorphTick(elapsed);
            await Delay(8);
        }

        // 收尾：最后几格也要散干净、认字也要全部落定 —— 之后这行就是品牌名本身
        if (!_stop) MorphTick(end);
    }

    /// <summary>推进一帧：换字 / 消失 / 认字 / 重排。所有位置都由「当下字宽」硬算，不叠加位移。</summary>
    private void MorphTick(double elapsed)
    {
        var total = 0.0;

        foreach (var slot in _slots)
        {
            if (slot.Space)
            {
                // 空格字位同样按时刻硬归零：它也是"多余字位"，只是不画字
                slot.Advance = elapsed >= slot.EliminateAt ? 0 : slot.Width;
                total += slot.Advance;
                continue;
            }

            if (slot.Target is char target)
            {
                if (!slot.Settled)
                {
                    if (elapsed >= slot.ResolveAt)
                    {
                        slot.Settled = true;
                        slot.Block.Text = target.ToString();
                        // 字块宽度直接换成品牌字母自己的字宽：装得下，不会被裁
                        slot.Block.Width = slot.BrandWidth;
                        slot.Block.Opacity = 1;
                        SetGlyphScale(slot, 1);
                    }
                    else
                    {
                        ScrambleGlyph(slot, elapsed);
                    }
                }

                slot.Advance = slot.Settled ? slot.BrandWidth : slot.Width;
            }
            else
            {
                if (!slot.Gone && elapsed >= slot.EliminateAt)
                {
                    slot.Gone = true;
                    slot.Block.IsVisible = false;
                }

                if (!slot.Gone) ScrambleGlyph(slot, elapsed);
                slot.Advance = slot.Gone ? 0 : slot.Width;
            }

            total += slot.Advance;
        }

        // 逐格摆位：字块在格子里居中，格子多宽就摆多宽 —— 格子归零，后面的字当帧补上来
        var x = 0.0;
        foreach (var slot in _slots)
        {
            Canvas.SetLeft(slot.Block, x + (slot.Advance - slot.Block.Width) / 2);
            x += slot.Advance;
        }

        // 整行始终居中：两头一起变短，而不是整句往左缩
        Canvas.SetLeft(SloganLine, (_stageWidth - total) / 2);
    }

    /// <summary>按各自的节拍换一个字；空格槽没有候选字形，什么都不做。</summary>
    private void ScrambleGlyph(GlyphSlot slot, double elapsed)
    {
        if (slot.Glyphs.Length == 0 || elapsed < slot.NextTick) return;

        slot.NextTick = elapsed + 34 + _random.NextDouble() * 40;
        slot.Block.Text = slot.Glyphs[_random.Next(slot.Glyphs.Length)];
    }

    private static void SetGlyphScale(GlyphSlot slot, double scale)
    {
        slot.Scale.ScaleX = scale;
        slot.Scale.ScaleY = scale;
    }

    /// <summary>
    /// 排好这场戏的走位：哪些字位活下来、各自认成哪个字母、什么时候认、多余的什么时候没。
    /// 返回整段 morph 的结束时刻（相对乱码开始，毫秒）。
    /// </summary>
    private double PrepareMorphTargets()
    {
        // 只在能显示字的槽里挑存活字位（空格槽不能变成品牌字母）
        var glyphSlots = new List<int>();
        for (var i = 0; i < _slots.Count; i++)
        {
            if (!_slots[i].Space) glyphSlots.Add(i);
        }

        // 16 个存活字位均匀铺在整行上 —— 认出来的字母从左边排到右边，读起来就是品牌名
        var survivors = new HashSet<int>();
        for (var k = 0; k < Brand.Length; k++)
        {
            var at = (int)Math.Round(k * (glyphSlots.Count - 1) / (double)(Brand.Length - 1));
            survivors.Add(glyphSlots[at]);
        }

        var lastResolve = 0.0;
        var rank = 0;
        var extras = new List<int>();

        for (var i = 0; i < _slots.Count; i++)
        {
            var slot = _slots[i];
            slot.Advance = slot.Width;

            if (!survivors.Contains(i))
            {
                slot.Target = null;
                extras.Add(i);
                continue;
            }

            var ch = Brand[rank];
            slot.Target = ch;
            slot.BrandWidth = MeasureText(ch.ToString());
            slot.ResolveAt = 160 + rank * 46 + _random.NextDouble() * 70;
            lastResolve = Math.Max(lastResolve, slot.ResolveAt);
            rank++;
        }

        // 多余字位从两端往中间没，时刻**在整段乱码里均匀铺开**：
        // 随机撒时刻会扎堆，一眨眼连着掉好几个，看起来就是一记突缩而不是"边乱边短"。
        var half = (_slots.Count - 1) / 2.0;
        extras.Sort((a, b) => Math.Abs(b - half).CompareTo(Math.Abs(a - half)));
        var span = 620.0 / Math.Max(1, extras.Count - 1);
        var lastGone = 0.0;
        for (var j = 0; j < extras.Count; j++)
        {
            var slot = _slots[extras[j]];
            // 从 0 就开始没：整段乱码里都在变短，而不是先乱一阵再开始删
            slot.EliminateAt = j * span + _random.NextDouble() * 50;
            lastGone = Math.Max(lastGone, slot.EliminateAt);
        }

        return Math.Max(lastResolve, lastGone) + 40;
    }

    /// <summary>
    /// 把中线上那行字交给真正的品牌名控件。两边是同一套字、同一种逐字排版、同一个位置，
    /// 所以可以直接一帧换过去 —— 不需要交叉淡化，也就没有双影。
    ///
    /// ⛔ 别再改回"这边淡出、那边淡入"：只要排版差一个像素，淡化期间就会看到一层重影
    ///    （用户说的"突变"，2026-10-31 踩过）。
    /// </summary>
    private void HandOffToWordmark()
    {
        LineCanvas.IsVisible = false;
        LineCanvas.Opacity = 0;
        Wordmark.Opacity = 1;
    }

    private async Task DescendAsync()
    {
        // 先摆好起点
        Logo.Opacity = 0;
        LogoShift.Y = 14;
        LogoScale.ScaleX = 0.88;
        LogoScale.ScaleY = 0.88;

        WelcomeLine.Opacity = 0;
        WelcomeShift.Y = 18;

        VersionLine.Opacity = 0;
        VersionShift.Y = 8;

        // 品牌名：从画面中线**直线**落到排版里的位置。
        // ⛔ 别再顺手加横向位移（以前有个 -22→0 的"弧"）—— 横竖两条动画一叠加，
        //    看上去就是先往左下滑、再往右下滑，用户明确要求直着下来。
        WordmarkShift.X = 0;
        WordmarkShift.Y = _wordmarkLift;

        try
        {
            await TweenAsync(720, t =>
            {
                var elapsed = t * 720;

                WordmarkShift.Y = Lerp(_wordmarkLift, 0, EaseOutCubic(elapsed / 620));

                var logoMove = EaseOutCubic((elapsed - 80) / 460);
                Logo.Opacity = EaseOutCubic((elapsed - 80) / 420);
                LogoShift.Y = Lerp(14, 0, logoMove);
                LogoScale.ScaleX = Lerp(0.88, 1, logoMove);
                LogoScale.ScaleY = Lerp(0.88, 1, logoMove);

                WelcomeLine.Opacity = EaseOutCubic((elapsed - 230) / 380);
                WelcomeShift.Y = Lerp(18, 0, EaseOutCubic((elapsed - 230) / 420));

                VersionLine.Opacity = EaseOutCubic((elapsed - 390) / 320);
                VersionShift.Y = Lerp(8, 0, EaseOutCubic((elapsed - 390) / 320));
            });
        }
        catch (Exception ex)
        {
            AppLog.Debug("oobe", "收尾落位动画失败：" + ex.Message);
        }
        finally
        {
            if (!_stop)
            {
                Logo.Opacity = 1;
                LogoShift.Y = 0;
                LogoScale.ScaleX = 1;
                LogoScale.ScaleY = 1;
                WelcomeLine.Opacity = 1;
                WelcomeShift.Y = 0;
                VersionLine.Opacity = 1;
                VersionShift.Y = 0;
                WordmarkShift.X = 0;
                WordmarkShift.Y = 0;
            }
        }
    }

    // ── 收尾 ─────────────────────────────────────────────────────────────

    private void OnUnloaded()
    {
        // 中途翻走了就停在终态：页面被缓存，回来时不能是一屏滚到一半的跑马灯
        _stop = true;
        StopMarquee();
        ShowFinalStatic();
        RaiseCompleted();
    }

    private void StopMarquee()
    {
        _marqueeTimer?.Stop();
        _marqueeTimer = null;
    }

    private void ShowFinalStatic()
    {
        MarqueeCanvas.IsVisible = false;
        LineCanvas.IsVisible = false;

        Logo.Opacity = 1;
        LogoShift.Y = 0;
        LogoScale.ScaleX = 1;
        LogoScale.ScaleY = 1;

        WelcomeLine.Opacity = 1;
        WelcomeShift.Y = 0;

        Wordmark.Opacity = 1;
        WordmarkShift.X = 0;
        WordmarkShift.Y = 0;

        VersionLine.Opacity = 1;
        VersionShift.Y = 0;
    }

    private void RaiseCompleted()
    {
        if (_completed) return;
        _completed = true;
        Completed?.Invoke(this, EventArgs.Empty);
    }

    // ── 小工具 ───────────────────────────────────────────────────────────

    /// <summary>把 0~1 的进度逐帧交给调用方；16ms 一帧，跑满就停。动画被中途截断也不会留下半截状态。</summary>
    private static async Task TweenAsync(double durationMs, Action<double> apply)
    {
        var start = Environment.TickCount64;
        while (true)
        {
            var t = Math.Min(1, (Environment.TickCount64 - start) / durationMs);
            apply(t);
            if (t >= 1) break;
            await Task.Delay(16);
        }
    }

    private static async Task FadeAsync(Control element, double from, double to, double durationMs)
    {
        try
        {
            await TweenAsync(durationMs, t => element.Opacity = Lerp(from, to, t));
        }
        catch (Exception ex)
        {
            AppLog.Debug("oobe", "淡入动画失败：" + ex.Message);
        }
        finally
        {
            element.Opacity = to;
        }
    }

    private static double Lerp(double from, double to, double p) =>
        from + (to - from) * Math.Clamp(p, 0, 1);

    private static double EaseOutCubic(double p)
    {
        p = Math.Clamp(p, 0, 1);
        return 1 - Math.Pow(1 - p, 3);
    }

    private static double EaseOutQuad(double p)
    {
        p = Math.Clamp(p, 0, 1);
        return 1 - (1 - p) * (1 - p);
    }

    private static double EaseInQuart(double p)
    {
        p = Math.Clamp(p, 0, 1);
        return p * p * p * p;
    }

    /// <summary>按标语的字体量一段文字的显示宽度（品牌名里没有空格，不用管行尾裁剪）。</summary>
    private static double MeasureText(string text)
    {
        var probe = new TextBlock
        {
            Text = text,
            FontSize = SloganFontSize,
            FontWeight = FontWeight.Bold,
        };
        probe.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return probe.DesiredSize.Width;
    }

    private static Task Delay(double ms) => Task.Delay(TimeSpan.FromMilliseconds(ms));
}
