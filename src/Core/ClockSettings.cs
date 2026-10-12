using System;
using Avalonia.Media;

namespace ClassSoftwareHub.Desktop.Core;

public enum ClockVeil { None, White, Black, Acrylic, Mica }
public enum ClockTone { Theme, Light, Dark }
public enum ClockInk { Auto, White, Black }

/// <summary>全屏时钟的外观设置（对齐网页版 tools/ClockTool.vue）。</summary>
public sealed class ClockSettings
{
    public string BackgroundImagePath { get; set; } = "";
    public ClockVeil Veil { get; set; } = ClockVeil.None;
    /// <summary>0 ~ 100</summary>
    public double VeilStrength { get; set; } = 55;
    public ClockTone Tone { get; set; } = ClockTone.Theme;
    public ClockInk Ink { get; set; } = ClockInk.Auto;
    public string FontFamily { get; set; } = "Bahnschrift";
    public double Scale { get; set; } = 1.0;
    public bool ShowSeconds { get; set; } = true;
    public bool ShowDate { get; set; } = true;
    public bool Hour12 { get; set; }

    /// <summary>
    /// 把另一份设置的值整体拷进来。
    ///
    /// ⚠️ 2026-09-28：这段以前在工具页和浮窗各写了一份（字段列表容易漏同步）。
    /// 现在只有这一处 —— 加字段就改这里，别在调用方再手抄一遍。
    /// </summary>
    public void CopyFrom(ClockSettings other)
    {
        if (other is null) return;
        BackgroundImagePath = other.BackgroundImagePath;
        Veil = other.Veil;
        VeilStrength = other.VeilStrength;
        Tone = other.Tone;
        Ink = other.Ink;
        FontFamily = other.FontFamily;
        Scale = other.Scale;
        ShowSeconds = other.ShowSeconds;
        ShowDate = other.ShowDate;
        Hour12 = other.Hour12;
    }

    /// <summary>复制一份。存"上次使用的样子"时要的是**快照**，不是引用。</summary>
    public ClockSettings Clone()
    {
        var copy = new ClockSettings();
        copy.CopyFrom(this);
        return copy;
    }
}

/// <summary>把设置翻译成颜色 / 画刷 / 文字（页面预览与全屏窗口共用，保证两边一致）。</summary>
public static class ClockRender
{
    public static readonly string[] VeilLabels = { "无", "白色蒙版", "黑色蒙版", "亚克力（Acrylic）", "云母（Mica）" };
    public static readonly string[] ToneLabels = { "跟随应用主题", "白色", "黑色" };
    public static readonly string[] InkLabels = { "自动（跟随底色）", "白色字", "黑色字" };
    public static readonly string[] FontLabels = { "等宽 · 与课堂计时器一致", "工业风 · Bahnschrift", "现代等宽 · Cascadia", "系统 UI · Segoe", "优雅衬线 · Cambria" };
    public static readonly string[] FontFamilies = { "Consolas", "Bahnschrift", "Cascadia Mono", "Segoe UI Variable Display", "Cambria" };

    /// <summary>
    /// 把一个字体名规范化成候选表里认识的那个。
    /// 主要用来兜**旧存档**：老版本存过 <c>Georgia</c>（已剔除，见 <see cref="FontFamilies"/> 的说明），
    /// 直接拿它去 new FontFamily 会让那台机器继续抖 —— 这里统一折成 Cambria。
    /// 空值 / 不认识的一律回默认的 Bahnschrift。
    ///
    /// ⚠️ 剔除 Georgia 的原因（自上游 dv1.1.3 移植）：它的 <c>tnum</c> 只重映射货币符号、不碰数字
    ///    （<c>1</c>=880 vs <c>0</c>=1257），靠等宽数字拉不平 ⇒ 大号时间居中排时每跳一秒整行宽度就变一次，
    ///    看上去就是"数字一直左右抖"。换成同为"优雅衬线"、数字天生等宽的 Cambria。
    /// </summary>
    public static string NormalizeFont(string? family)
    {
        if (string.IsNullOrWhiteSpace(family)) return FontFamilies[1];
        var f = family.Trim();
        if (f == "Georgia") return "Cambria";
        return Array.IndexOf(FontFamilies, f) >= 0 ? f : FontFamilies[1];
    }

    /// <summary>
    /// 「等宽数字」的字形特性，给代码里建的时间 TextBlock 用
    /// （XAML 那边写 <c>FontFeatures="+tnum"</c>，同一个特性的两种写法）。
    ///
    /// ⚠️ 打开 tnum 后每个数字占同样宽度，大号时间居中排时整行宽度恒定 ⇒ 秒数跳动时不会左右抖。
    ///    ⛔ 这不是装饰：字体默认是比例字宽，Bahnschrift 里 <c>1</c> 只有 <c>4</c> 的一半宽。
    /// </summary>
    public static FontFeatureCollection TabularDigits { get; } =
        new() { FontFeature.Parse("+tnum") };

    private static Color Ink => Color.FromArgb(255, 17, 17, 17);
    private static Color Paper => Color.FromArgb(255, 255, 255, 255);
    private static Color Black => Color.FromArgb(255, 0, 0, 0);

    public static Color BaseColor(ClockTone tone, bool darkTheme) => tone switch
    {
        ClockTone.Light => Paper,
        ClockTone.Dark => Black,
        _ => darkTheme ? Black : Paper,
    };

    /// <summary>文字颜色：强制 > 有背景图（白蒙版给深色字，其余白字）> 跟随底色。</summary>
    public static Color FaceColor(ClockSettings s, bool darkTheme, bool hasPhoto)
    {
        if (s.Ink == ClockInk.White) return Paper;
        if (s.Ink == ClockInk.Black) return Ink;
        if (hasPhoto) return s.Veil == ClockVeil.White ? Ink : Paper;
        return IsLight(BaseColor(s.Tone, darkTheme)) ? Ink : Paper;
    }

    private static bool IsLight(Color c) => (c.R * 299 + c.G * 587 + c.B * 114) / 1000 > 128;

    public static Brush BaseBrush(ClockSettings s, bool darkTheme)
        => new SolidColorBrush(BaseColor(s.Tone, darkTheme));

    /// <summary>
    /// 蒙版 / 材质。原版亚克力、云母用 WinUI 的 <c>AcrylicBrush</c>（真的会模糊底下的背景图）。
    ///
    /// ⚠️ Avalonia **没有元素级的亚克力画刷**（<c>AcrylicBrush</c> 不存在；它只有窗口级的
    ///    <c>WindowTransparencyLevel</c>，见 <c>Platform.Backdrop</c>）。所以这里用原版的
    ///    <c>FallbackColor</c> 语义 —— 半透明纯色（Acrylic 不支持时 WinUI 自己也是退到它）。
    /// </summary>
    public static Brush VeilBrush(ClockSettings s)
    {
        var a = Math.Clamp(s.VeilStrength / 100.0, 0, 1);
        switch (s.Veil)
        {
            case ClockVeil.White:
                return new SolidColorBrush(Color.FromArgb((byte)(a * 255), 255, 255, 255));
            case ClockVeil.Black:
                return new SolidColorBrush(Color.FromArgb((byte)(a * 255), 0, 0, 0));
            case ClockVeil.Acrylic:
                return Acrylic(Color.FromArgb(255, 255, 255, 255), Math.Clamp(0.04 + 0.28 * a, 0, 1),
                               Color.FromArgb((byte)(255 * Math.Clamp(0.35 + a * 0.4, 0, 1)), 255, 255, 255));
            case ClockVeil.Mica:
                return Acrylic(Color.FromArgb(255, 12, 12, 12), Math.Clamp(0.10 + 0.55 * a, 0, 1),
                               Color.FromArgb((byte)(255 * Math.Clamp(0.45 + a * 0.45, 0, 1)), 12, 12, 12));
            default:
                return new SolidColorBrush(Colors.Transparent);
        }
    }

    /// <summary>
    /// 原版是 <c>AcrylicBrush { TintColor, TintOpacity, FallbackColor }</c>。
    /// Avalonia 没有这个画刷 → 直接取 <paramref name="fallback"/>（它已按蒙版浓度算好 alpha），
    /// 与 WinUI 在"亚克力不可用"时的降级表现一致。
    /// </summary>
    private static Brush Acrylic(Color tint, double opacity, Color fallback)
    {
        // tint / opacity 保留只为与调用处一一对应（原 AcrylicBrush 的 TintColor / TintOpacity）；
        // Avalonia 下没有亚克力画刷可用，真正生效的是 fallback。
        try
        {
            return new SolidColorBrush(fallback);
        }
        catch
        {
            return new SolidColorBrush(fallback);
        }
    }

    /// <summary>时间文字（网页版：12 小时制不带前导零，24 小时制补零）。</summary>
    public static string TimeText(DateTime now, bool hour12)
    {
        var h = hour12 ? (now.Hour % 12 == 0 ? 12 : now.Hour % 12) : now.Hour;
        return hour12 ? $"{h}:{now.Minute:00}" : $"{h:00}:{now.Minute:00}";
    }

    public static string SecText(DateTime now) => ":" + now.ToString("ss");

    public static string DateText(DateTime now)
    {
        var week = "日一二三四五六"[(int)now.DayOfWeek];
        return $"{now.Year} 年 {now.Month} 月 {now.Day} 日 · 星期{week}";
    }
}
