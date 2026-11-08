using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using ClassSoftwareHub.Desktop.Platform;
using ClassSoftwareHub.Desktop.Services;

namespace ClassSoftwareHub.Desktop.Pages;

/// <summary>
/// 「PowerPoint 触控优化」页（需求清单第 2 条，tag = <c>ppttouch</c>；自上游 dv1.2.0-insider1.9 移植）。
///
/// 识别与补点击都在 <see cref="PptTouch"/>，菜单内容与动作在 <see cref="PptMenu"/>，
/// 这里只管开关、额外菜单项的勾选与状态。
/// ⚠️ 状态每秒刷一次：放映是否在跑是外部状态，页面不做订阅，轮询最省事。
///
/// <para>
/// ⚠️ 移植坑（上游 1.8 就踩过）：**开关状态必须在 <c>Loaded</c> 之后回填**。
/// 构造函数里控件还不在视觉树 / 主题树上，这时给 <c>ToggleSwitch.IsChecked</c> 赋值，
/// 视觉状态不会跟过去，页面上会全显示为「关」。
/// </para>
/// </summary>
public sealed partial class PptTouchPage : PageBase
{
    private readonly DispatcherTimer _timer;

    /// <summary>构造期给开关设初值会触发 IsCheckedChanged，铺完初值再放行。</summary>
    private bool _ready;

    /// <summary>初值只在第一次 Loaded 回填一次；之后来回切页只刷新状态。</summary>
    private bool _initialized;

    public PptTouchPage()
    {
        InitializeComponent();

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => UpdateState();

        // ⚠️ 回填必须等进入视觉树之后（见类注释）
        Loaded += (_, _) =>
        {
            if (!_initialized)
            {
                _initialized = true;

                var config = PptTouchConfig.Load();

                _ready = false;
                EnableSwitch.IsChecked = config.Enabled;
                MenuSwitch.IsChecked = config.CustomMenu;
                _ready = true;

                BuildExtras(config.MenuExtras);
            }

            UpdateState();
            _timer.Start();
        };

        // 页面离开可视树就停表：放映状态是轮询来的，后台一直跑没必要
        DetachedFromVisualTree += (_, _) => _timer.Stop();
    }

    /// <summary>
    /// 铺「额外菜单项」的勾选框。
    /// ⚠️ 清单只认 <see cref="PptMenu.Extras"/>，这里不再抄一份文案 —— 两处维护必然会写歪。
    /// </summary>
    private void BuildExtras(ICollection<string> selected)
    {
        ExtraHost.Children.Clear();

        foreach (var extra in PptMenu.Extras)
        {
            var box = new CheckBox
            {
                Content = extra.Text,
                IsChecked = selected.Contains(extra.Id),
                Tag = extra.Id,
                MinWidth = 0,
            };

            // ⚠️ 先赋初值再挂事件：否则铺初值这一下会被当成「用户改了」，把存档写一遍
            box.IsCheckedChanged += Extra_Changed;

            var tip = new TextBlock
            {
                Text = extra.Note,
                FontSize = 12,
                Opacity = 0.6,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(28, -4, 0, 8),
            };

            ExtraHost.Children.Add(box);
            ExtraHost.Children.Add(tip);
        }
    }

    private void EnableSwitch_Toggled(object? sender, RoutedEventArgs e)
    {
        if (!_ready) return;

        var config = PptTouchConfig.Load();
        config.Enabled = EnableSwitch.IsChecked == true;
        config.Save();

        if (config.Enabled) PptTouch.Start();
        else PptTouch.Stop();

        UpdateState();
    }

    private void MenuSwitch_Toggled(object? sender, RoutedEventArgs e)
    {
        if (!_ready) return;

        var config = PptTouchConfig.Load();
        config.CustomMenu = MenuSwitch.IsChecked == true;
        config.Save();

        PptTouch.ApplyConfig();
        UpdateState();
    }

    private void Extra_Changed(object? sender, RoutedEventArgs e)
    {
        if (!_ready) return;

        var picked = new List<string>();
        foreach (var child in ExtraHost.Children)
        {
            if (child is CheckBox { IsChecked: true, Tag: string id }) picked.Add(id);
        }

        var config = PptTouchConfig.Load();
        config.MenuExtras = picked;
        config.Save();
    }

    private void UpdateState()
    {
        if (PptTouch.IsActive)
        {
            RunStateText.Text = MenuSwitch.IsChecked == true
                ? "触控手势与长按菜单已开启，程序运行期间一直生效。"
                : "触控手势已开启，长按菜单已关闭。";
        }
        else
        {
            RunStateText.Text = "触控手势未开启。";
        }

        var showing = PptTouch.IsSlideshowShowing();
        ShowStateText.Text = showing
            ? "当前检测到 PowerPoint 正在放映。"
            : "当前未检测到 PowerPoint 放映。";

        // 没检测到时把 PowerPoint 现有的窗口类名写进日志（内部限频 30 秒），
        // 下次「明明在放映却显示未检测到」就能直接从日志看出是类名不符还是真没放映。
        if (!showing) PptTouch.DescribePowerPointWindows();

        // 只统计「真正补过动作」的次数：没补过就不显示，免得页面上挂一行 0。
        var taps = PptTouch.Taps;
        TapCountText.Text = taps > 0 ? $"已翻页 {taps} 次" : "";
    }
}
