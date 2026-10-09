using System;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using ClassSoftwareHub.Desktop.Core;

namespace ClassSoftwareHub.Desktop.Views;

/// <summary>
/// 把「全屏时钟」的外观铺到全屏倒计时 / 全屏秒表上。
/// 由课堂计时器页顶那个「使用全屏时钟背景设置」开关控制（2026-10-04 Nick 要的）。
///
/// 只搬**背景那一套**：背景图、蒙版、底色、字色（外加进度条，否则白条压在浅底上等于没有）。
/// 字体、12/24 小时制、秒针那些是时钟自己的事，计时盘不该被改 —— 搬过来的话投影出来的
/// 数字会跟计时器页面上看到的对不上，反而怪。
///
/// ⚠️ 开关关着、或者时钟外观压根没存过，就**什么都不做**：全屏窗口保持原来那个深底。
///    所以调用方先把开关判掉，这里不再重复判一次。
///
/// ⚠️ 移植说明（WinUI → Avalonia）：
///   · <c>Application.Current.RequestedTheme == ApplicationTheme.Dark</c>
///     → <c>Application.Current?.ActualThemeVariant == ThemeVariant.Dark</c>（Avalonia 无 RequestedTheme）。
///   · <c>BitmapImage(new Uri(path))</c> → <c>new Bitmap(path)</c>（Avalonia.Media.Imaging.Bitmap）。
///   · <c>Visibility.Visible/Collapsed</c> → <c>IsVisible true/false</c>。
///   · <c>Windows.UI.Color.FromArgb</c> → <c>Avalonia.Media.Color.FromArgb</c>（签名一致）。
///   · <c>ClockRender</c> 已由 Core/ClockSettings.cs 提供，且确有
///     <c>BaseBrush</c> / <c>VeilBrush</c> / <c>FaceColor</c> 三个成员，调用签名不变（见交付说明）。
/// </summary>
internal static class ClockBackdrop
{
    /// <summary>当前是不是深色主题（时钟的 Tone=跟随主题 时要靠它定底色）。</summary>
    private static bool IsDark => Application.Current?.ActualThemeVariant == ThemeVariant.Dark;

    /// <param name="bar">进度条；没有就传 null（全屏秒表没有进度条）。</param>
    /// <param name="ink">要吃字色的文本（大字、状态行、秒表那截小数……）。</param>
    public static void Apply(Grid root, Image bg, Border veil, ProgressBar? bar, params TextBlock[] ink)
    {
        try
        {
            var s = ClockPresetStore.Load().LastUsed ?? new ClockSettings();

            var hasPhoto = !string.IsNullOrWhiteSpace(s.BackgroundImagePath)
                           && File.Exists(s.BackgroundImagePath);

            root.Background = ClockRender.BaseBrush(s, IsDark);

            if (hasPhoto)
            {
                // ⚠️ 原版直接给 UriSource：本机文件走这条最省事；
                //    Avalonia 没有 UriSource，直接 new Bitmap(路径) 即可。
                bg.Source = new Bitmap(s.BackgroundImagePath);
                bg.IsVisible = true;
            }
            else
            {
                bg.Source = null;
                bg.IsVisible = false;
            }

            veil.Background = ClockRender.VeilBrush(s);

            var face = ClockRender.FaceColor(s, IsDark, hasPhoto);
            var brush = new SolidColorBrush(face);
            foreach (var t in ink) t.Foreground = brush;

            if (bar is not null)
            {
                // 满的那截用字色（跟大字同色），底槽用同一色的极淡版 —— 浅底深字 / 深底浅字都不会丢
                bar.Foreground = new SolidColorBrush(Color.FromArgb(150, face.R, face.G, face.B));
                bar.Background = new SolidColorBrush(Color.FromArgb(38, face.R, face.G, face.B));
            }
        }
        catch (Exception ex)
        {
            AppLog.Info("clock-bg", "铺全屏时钟背景失败：" + ex.Message);
        }
    }
}
