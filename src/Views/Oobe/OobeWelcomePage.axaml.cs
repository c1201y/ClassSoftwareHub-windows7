using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using ClassSoftwareHub.Desktop.Core;

namespace ClassSoftwareHub.Desktop.Views.Oobe;

/// <summary>
/// 第 1 步：欢迎（自上游 dv1.2.0-insider1.8 移植）。开场演完（跑马灯 → 标语 → 品牌名 →
/// 图标 / 欢迎语 / 版本号）才亮出页底提示。页面只负责把窗口"已经露脸"这件事转告给开场控制 ——
/// 动画本身都在 <see cref="Controls.OobeWelcomeIntro"/> 里。
/// </summary>
public partial class OobeWelcomePage : UserControl
{
    private bool _revealed;

    public OobeWelcomePage()
    {
        InitializeComponent();
        LoadCaveat();

        // ⚠️ 原版用 OnNavigatedTo/From；Avalonia 的 UserControl 没有导航事件，
        //    页面是被窗口常驻缓存的，用 Loaded/Unloaded 对应"进/出这一屏"。
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        if (OobeSession.Current is { } session) session.Started += OnSessionStarted;
    }

    private void OnUnloaded(object? sender, RoutedEventArgs e)
    {
        if (OobeSession.Current is { } session) session.Started -= OnSessionStarted;
    }

    /// <summary>窗口真的显示出来了才开始唱开场，否则动画全在不可见的窗口里跑完。</summary>
    private void OnSessionStarted(object? sender, EventArgs e) => Intro.Play();

    /// <summary>Win10 上确实有功能会灰掉，与其以后在页面上撞见，不如首屏底下先说一句。</summary>
    private void LoadCaveat()
    {
        if (SystemInfo.IsWin11)
        {
            Caveat.IsVisible = false;
            return;
        }

        Caveat.Text = "部分功能仅支持 Windows 11，在 Windows 10 上会显示为不可用";
    }

    private void Intro_Completed(object? sender, EventArgs e)
    {
        if (_revealed) return;
        _revealed = true;

        if (Caveat.IsVisible) OobeMotion.Fade(Caveat, 0);
    }
}
