using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace ClassSoftwareHub.Desktop.Services;

/// <summary>
/// 工具页按键的统一手感：按下去底色换成**主题色渐变**，不是系统默认那层灰。
/// （2026-10-18 自上游 1.2.0 移植；WinUI → Avalonia。）
///
/// <para>
/// 上游是靠覆盖 <c>ButtonBackgroundPressed</c> 主题资源让模板自己取到渐变；
/// FluentAvalonia 的 Button 模板没有那组键，所以这里改成**直接挂指针事件**：
/// 按下时把 Background 换成渐变、松开/移出时还原。效果等价，且不依赖模板内部结构。
/// </para>
/// <para>化学式编辑器与方程编辑器共用这一套，手感不能一处一样一处不一样（Nick 2026-10-17）。</para>
/// </summary>
internal static class ButtonFeel
{
    /// <summary>给按钮挂上「按下＝主题色渐变」。</summary>
    /// <param name="button">Button 或 ToggleButton（两者都是 Button 基类）。</param>
    /// <param name="dark">渐变末端的压暗系数，越小越深。</param>
    public static void ApplyPressGradient(Button button, double dark = 0.72)
    {
        var accent = (ThemeBrush.Get(null, "AccentFillColorDefaultBrush") as SolidColorBrush)?.Color
                     ?? ThemeBrush.AccentColor();
        var color = accent;

        var gradient = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
        };
        gradient.GradientStops.Add(new GradientStop { Color = color, Offset = 0 });
        gradient.GradientStops.Add(new GradientStop
        {
            Color = Color.FromArgb(255,
                (byte)(color.R * dark), (byte)(color.G * dark), (byte)(color.B * dark)),
            Offset = 1,
        });

        var original = button.Background;

        button.PointerPressed += (_, _) => button.Background = gradient;
        button.PointerReleased += (_, _) => button.Background = original;
        button.PointerExited += (_, _) => button.Background = original;
        button.Click += (_, _) => button.Background = original;
    }
}
