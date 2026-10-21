using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using ClassSoftwareHub.Desktop.Services;

namespace ClassSoftwareHub.Desktop.Platform;

/// <summary>
/// 数学排版的**离屏自检**：不进主界面、不开窗口，直接把 <see cref="MathText"/> 的
/// 典型输出画进一张 PNG，用来看分数/上标/根号的基线位置对不对。
///
/// <para>
/// 为什么需要它：分数排版这类"位置对不对"的问题，靠跑主界面 + 截图 + 点按钮
/// 一条龙验证既慢又不稳定（侧边栏 flyout 抢焦点、窗口被最小化到托盘都会让截图扑空）。
/// 直接渲染控件再存图，一次就能确认。
/// </para>
/// <para>
/// 用法：<c>ClassSoftwareHub.exe --mathshot C:\temp\math.png</c>（进程画完即退出，不开界面）
/// </para>
/// </summary>
internal static class MathShot
{
    private static readonly string[] Samples =
    {
        "x = π/6",
        "x = 13π/6",
        "x = -11π/6",
        "x = 56/9",
        "y = (3√2)/4",
        "f(x) = x² − 5x + 6",
        "x ∈ (-π/2 + 2kπ, π/2 + 2kπ)",
    };

    public static void Run(string outPath)
    {
        var panel = new StackPanel { Spacing = 10 };
        foreach (var sample in Samples)
        {
            panel.Children.Add(new TextBlock { Text = sample + "   ← 原文", FontSize = 11, Foreground = Brushes.Gray });
            panel.Children.Add(MathText.Render(sample, 26, Brushes.Black));
        }

        var root = new Border
        {
            Background = Brushes.White,
            Padding = new Thickness(16),
            Child = panel,
            Width = 620,
        };

        root.Measure(new Size(620, double.PositiveInfinity));
        root.Arrange(new Rect(root.Bounds.Width > 0 ? root.Bounds.Size : root.DesiredSize));
        root.UpdateLayout();

        var width = (int)Math.Ceiling(root.Bounds.Width <= 0 ? 620 : root.Bounds.Width);
        var height = (int)Math.Ceiling(root.Bounds.Height <= 0 ? root.DesiredSize.Height : root.Bounds.Height);
        if (height <= 0) height = 600;

        var bitmap = new RenderTargetBitmap(new PixelSize(Math.Max(1, width), Math.Max(1, height)), new Vector(96, 96));
        bitmap.Render(root);
        bitmap.Save(outPath);
    }
}
