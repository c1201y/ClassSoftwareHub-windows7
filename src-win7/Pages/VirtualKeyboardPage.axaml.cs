using System;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Threading;
using ClassSoftwareHub.Desktop.Data;
using ClassSoftwareHub.Desktop.Platform;
using ClassSoftwareHub.Desktop.Services.VirtualKeyboard;
using ClassSoftwareHub.Desktop.Views;

namespace ClassSoftwareHub.Desktop.Pages;

/// <summary>
/// 「实验性功能 → 虚拟键盘」。
///
/// ⚠️ 它归在实验性功能分组里，**不占设置页**（Nick 2026-09-30 定）。
/// ⛔ 所有项一律用系统 <c>SettingsCard</c>，不自行绘制卡片。
///
/// ⚠️ 移植说明（WinUI → Avalonia）：
///   · Page → <see cref="PageBase"/>；DispatcherQueueTimer → Avalonia 的 DispatcherTimer。
///   · ToggleSwitch 的 IsOn/Toggled → IsChecked/IsCheckedChanged。
///   · 原版 Slider 的 StepFrequency 在 Avalonia 没有 → 在各 ValueChanged 里吸附到 0.05 步进
///     （把值写回去一次，第二圈就是步进好的值，见 <see cref="Snap"/>）。
/// </summary>
public sealed partial class VirtualKeyboardPage : PageBase
{
    /// <summary>装载默认值期间把回调憋住（不然初始化会把设置一份份写回去）。</summary>
    private bool _loading = true;

    /// <summary>滑块写盘防抖：拖一次会触发几十次 ValueChanged，逐次写文件没必要。</summary>
    private DispatcherTimer? _saveTimer;

    public VirtualKeyboardPage()
    {
        InitializeComponent();
        LoadFromSettings();
        _loading = false;

        Unloaded += (_, _) => FlushSave();
    }

    private void LoadFromSettings()
    {
        var settings = App.Settings.Current;

        EnableSwitch.IsChecked = settings.VirtualKeyboardEnabled;
        CaptureSwitch.IsChecked = settings.VirtualKeyboardCaptureSystem;

        ModeCombo.SelectedIndex = KeyboardLayouts.NormalizeMode(settings.VirtualKeyboardMode)
            == KeyboardLayouts.ModeCompact ? 1 : 0;

        ScaleSlider.Value = Math.Clamp(settings.VirtualKeyboardScale, ScaleSlider.Minimum, ScaleSlider.Maximum);
        WidthSlider.Value = Math.Clamp(settings.VirtualKeyboardWidthRatio, WidthSlider.Minimum, WidthSlider.Maximum);
        FontSlider.Value = Math.Clamp(settings.VirtualKeyboardFontScale, FontSlider.Minimum, FontSlider.Maximum);

        PlacementCombo.SelectedIndex = settings.VirtualKeyboardFloating ? 1 : 0;

        ThemeCombo.SelectedIndex = settings.VirtualKeyboardTheme switch
        {
            "light" => 1,
            "dark" => 2,
            _ => 0,
        };

        RefreshLabels();
    }

    private void RefreshLabels()
    {
        ScaleText.Text = $"{Math.Round(ScaleSlider.Value * 100)}%";
        WidthText.Text = $"{Math.Round(WidthSlider.Value * 100)}%";
        FontText.Text = $"{Math.Round(FontSlider.Value * 100)}%";
    }

    // ── 读写设置 ────────────────────────────────────────────

    private void DebouncedSave()
    {
        if (_saveTimer is null)
        {
            // ⚠️ 定时器必须用字段持有 —— 局部变量会被回收，回调就再也不来了。
            _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
            _saveTimer.Tick += (timer, _) =>
            {
                ((DispatcherTimer)timer).Stop();
                App.Settings.Save();
            };
        }

        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void FlushSave()
    {
        if (_saveTimer is { IsEnabled: true })
        {
            _saveTimer.Stop();
            App.Settings.Save();
        }
    }

    // ── 事件 ────────────────────────────────────────────────

    private void Enable_Toggled(object? sender, RoutedEventArgs e)
    {
        if (_loading) return;

        var on = EnableSwitch.IsChecked == true;
        App.Settings.Current.VirtualKeyboardEnabled = on;
        App.Settings.Save();

        VirtualKeyboardService.Apply();

        // 打开的那一刻直接摆出来 —— 不用他自己再找按钮。
        if (on) VirtualKeyboardWindow.ShowKeyboard(userInitiated: true);
        else VirtualKeyboardWindow.HideKeyboard();
    }

    private void Capture_Toggled(object? sender, RoutedEventArgs e)
    {
        if (_loading) return;

        App.Settings.Current.VirtualKeyboardCaptureSystem = CaptureSwitch.IsChecked == true;
        App.Settings.Save();
        VirtualKeyboardService.Apply();
    }

    private void Show_Click(object? sender, RoutedEventArgs e) =>
        VirtualKeyboardWindow.Toggle();

    private void Mode_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;

        var compact = ModeCombo.SelectedIndex == 1;
        App.Settings.Current.VirtualKeyboardMode = compact
            ? KeyboardLayouts.ModeCompact
            : KeyboardLayouts.ModeFull;
        App.Settings.Save();

        // 布局变了要整盘重建，不能只刷外观。
        VirtualKeyboardWindow.ApplySettings();
    }

    /// <summary>原版 StepFrequency="0.05" 的等价物：把值吸附到 0.05 的整数倍。</summary>
    private static double Snap(double v) => Math.Round(v / 0.05) * 0.05;

    private void Scale_Changed(object? sender, RangeBaseValueChangedEventArgs e)
    {
        if (_loading) return;

        // ⚠️ Avalonia 没有 StepFrequency：先吸附，写回滑块（会再进来一圈，那一圈值已对齐）。
        var v = Snap(e.NewValue);
        if (Math.Abs(ScaleSlider.Value - v) > 0.001) { ScaleSlider.Value = v; return; }

        App.Settings.Current.VirtualKeyboardScale = v;
        DebouncedSave();
        RefreshLabels();
        VirtualKeyboardWindow.RefreshLook();
    }

    private void Width_Changed(object? sender, RangeBaseValueChangedEventArgs e)
    {
        if (_loading) return;

        var v = Snap(e.NewValue);
        if (Math.Abs(WidthSlider.Value - v) > 0.001) { WidthSlider.Value = v; return; }

        App.Settings.Current.VirtualKeyboardWidthRatio = v;
        DebouncedSave();
        RefreshLabels();
        VirtualKeyboardWindow.RefreshLook();
    }

    private void Font_Changed(object? sender, RangeBaseValueChangedEventArgs e)
    {
        if (_loading) return;

        var v = Snap(e.NewValue);
        if (Math.Abs(FontSlider.Value - v) > 0.001) { FontSlider.Value = v; return; }

        App.Settings.Current.VirtualKeyboardFontScale = v;
        DebouncedSave();
        RefreshLabels();
        VirtualKeyboardWindow.RefreshLook();
    }

    private void Placement_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;

        App.Settings.Current.VirtualKeyboardFloating = PlacementCombo.SelectedIndex == 1;
        App.Settings.Save();
        VirtualKeyboardWindow.RefreshLook();
    }

    private void Theme_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;

        App.Settings.Current.VirtualKeyboardTheme = ThemeCombo.SelectedIndex switch
        {
            1 => "light",
            2 => "dark",
            _ => "system",
        };
        App.Settings.Save();
        VirtualKeyboardWindow.RefreshLook();
    }

    public override void OnNavigatedFrom()
    {
        base.OnNavigatedFrom();
        FlushSave();
    }
}

