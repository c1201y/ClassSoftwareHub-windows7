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
/// 由课堂计时器页顶部的「使用全屏时钟背景设置」开关控制。
///
/// 只搬**背景那一套**：背景图、蒙版、底色、字色（外加进度条，否则白条压在浅底上等于没有）。
/// 字体、12/24 小时制、秒针等属于时钟自身外观，不在此范围内，以免投影结果与计时器页显示不一致。
///
/// 开关关闭或未保存过时钟外观时不做任何处理，全屏窗口保持默认深底；调用方负责先判定开关。
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
                // Avalonia 无 UriSource，本机文件直接 new Bitmap(路径)。
                // 旧位图先解码新图再释放，避免渲染期拿到已释放的位图。
                var next = new Bitmap(s.BackgroundImagePath);
                (bg.Source as Bitmap)?.Dispose();
                bg.Source = next;
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
            AppLog.Catch("clock-bg", "铺设全屏时钟背景", ex);
        }
    }
}
