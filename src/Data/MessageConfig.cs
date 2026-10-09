using System;
using System.IO;
using System.Text.Json;

namespace ClassSoftwareHub.Desktop.Data;

/// <summary>
/// 「桌面留言」的本地存档（%LOCALAPPDATA%\ClassSoftwareHub\message.json）。
///
/// <para>
/// 工具页、常用工具浮窗（<c>MiniMessage</c>）、桌面悬浮窗（<c>MessageWindow</c>）与
/// 全屏窗（<c>MessageFullscreenWindow</c>）**共用这一份**：在哪儿改都是一个样，
/// 也避免两边各写一份自己的字段、互相覆盖（<see cref="PickNumberConfig"/> 踩过同一个坑）。
/// </para>
/// </summary>
public sealed class MessageConfig
{
    public static readonly string StorePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ClassSoftwareHub", "message.json");

    /// <summary>留言内容（支持多行，\n 换行）。</summary>
    public string Text { get; set; } = "认真听讲，做好笔记";

    /// <summary>字号（悬浮窗与页面预览用；全屏按屏幕自适应，见全屏窗）。</summary>
    public double FontSize { get; set; } = 48;

    /// <summary>文字颜色（#RRGGBB 或 #AARRGGBB）。</summary>
    public string TextColor { get; set; } = "#FF1F1F1F";

    /// <summary>底色（#RRGGBB 或 #AARRGGBB）。</summary>
    public string BackColor { get; set; } = "#FFFDFDF7";

    /// <summary>是否加粗。</summary>
    public bool Bold { get; set; } = true;

    /// <summary>
    /// 悬浮窗是否用**无底色（透明）**。真正的逐像素透明要系统合成支持
    /// （Win7 经典主题 / 关了 DWM 合成时不成立），此时窗口会自动退回上面的底色 ——
    /// 见 <c>MessageWindow</c> 里对 <c>ActualTransparencyLevel</c> 的运行时判定。
    /// </summary>
    public bool Transparent { get; set; }

    /// <summary>全屏时的放大倍数（相对字号，1~6，默认 3 倍）。</summary>
    public double FullscreenScale { get; set; } = 3;

    // ── 悬浮窗的位置（物理像素；用户拖过才有效，见 PosVersion）──
    public int X { get; set; } = int.MinValue;
    public int Y { get; set; } = int.MinValue;

    /// <summary>位置存档版本。改了默认摆放算法就 +1，让老坐标作废一次。</summary>
    public int PosVersion { get; set; }

    public const int CurrentPosVersion = 1;

    public static MessageConfig Load()
    {
        var cfg = new MessageConfig();
        try
        {
            if (File.Exists(StorePath))
                cfg = JsonSerializer.Deserialize<MessageConfig>(File.ReadAllText(StorePath)) ?? new MessageConfig();
        }
        catch { /* 存档坏了就用默认值 */ }

        // 手改过 json / 老存档缺字段 —— 统一兜住，调用方不用到处判空
        cfg.Text ??= "";
        cfg.TextColor = Normalize(cfg.TextColor, "#FF1F1F1F");
        cfg.BackColor = Normalize(cfg.BackColor, "#FFFDFDF7");
        if (cfg.FontSize < 8 || cfg.FontSize > 400) cfg.FontSize = 48;
        if (cfg.FullscreenScale < 1 || cfg.FullscreenScale > 6) cfg.FullscreenScale = 3;
        return cfg;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
            File.WriteAllText(StorePath, JsonSerializer.Serialize(this));
        }
        catch { /* 存不上不影响使用 */ }
    }

    /// <summary>把颜色字符串规整成 #AARRGGBB；解析不了就用兜底色。</summary>
    public static string Normalize(string? color, string fallback)
    {
        if (TryParseColor(color, out var c))
            return $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}";
        return fallback;
    }

    /// <summary>宽松解析 <c>#RGB</c> / <c>#RRGGBB</c> / <c>#AARRGGBB</c>（也可以带不带 #）。</summary>
    public static bool TryParseColor(string? text, out Avalonia.Media.Color color)
    {
        color = default;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var hex = text.Trim().TrimStart('#').Trim();
        try
        {
            switch (hex.Length)
            {
                case 6:     // RRGGBB
                    color = Avalonia.Media.Color.FromRgb(B(hex, 0), B(hex, 2), B(hex, 4));
                    return true;
                case 8:     // AARRGGBB
                    color = Avalonia.Media.Color.FromArgb(B(hex, 0), B(hex, 2), B(hex, 4), B(hex, 6));
                    return true;
                case 3:     // RGB → RRGGBB
                    color = Avalonia.Media.Color.FromRgb(
                        (byte)(H(hex[0]) * 17), (byte)(H(hex[1]) * 17), (byte)(H(hex[2]) * 17));
                    return true;
                default:
                    return false;
            }
        }
        catch
        {
            return false;
        }

        static byte B(string s, int i) => (byte)((H(s[i]) << 4) | H(s[i + 1]));
        static int H(char c) => Convert.ToInt32(char.ToString(c), 16);
    }
}
