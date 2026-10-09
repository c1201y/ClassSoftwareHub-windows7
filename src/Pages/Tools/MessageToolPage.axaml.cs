using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;      // RangeBaseValueChangedEventArgs（Slider.ValueChanged）
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using ClassSoftwareHub.Desktop.Data;
using ClassSoftwareHub.Desktop.Platform;

namespace ClassSoftwareHub.Desktop.Pages.Tools;

/// <summary>
/// 桌面留言（2026-10-06 Nick 提）：
/// 输入一句话 → 调字号 / 颜色 / 底色 → 做成**桌面悬浮窗**钉在桌面上，或**全屏**铺满屏幕展示。
///
/// <para>
/// 与其它工具页一致的三件事：
///   · 内容与外观存 <see cref="MessageConfig"/>（%LOCALAPPDATA%\ClassSoftwareHub\message.json），
///     「常用工具」浮窗里的简版（<c>MiniMessage</c>）与悬浮窗/全屏窗都读同一份；
///   · 事件在**初值填完之后**再接（本仓库约定：XAML 里挂事件 + 构造期赋初值会回调到半成品状态）；
///   · 改动防抖落盘（700ms）—— 打字每敲一下都写一次文件没必要。
/// </para>
/// </summary>
public sealed partial class MessageToolPage : PageBase
{
    /// <summary>文字颜色色卡（第一颗是默认的深墨色）。</summary>
    private static readonly string[] TextPalette =
    {
        "#FF1F1F1F", "#FFFFFFFF", "#FFD13438", "#FFE8590C", "#FF0F6CBD", "#FF107C10", "#FF7A3E9D",
    };

    /// <summary>背景底色色卡（纸白 / 纯白 / 淡黄 / 浅灰 / 深灰 / 墨黑）。</summary>
    private static readonly string[] BackPalette =
    {
        "#FFFDFDF7", "#FFFFFFFF", "#FFFFF4CE", "#FFF3F3F3", "#FF2B2B2B", "#FF101418",
    };

    private readonly DispatcherTimer _saveTimer;

    /// <summary>控件初值填完、事件都接好了才置 true —— 在那之前所有回调一律忽略。</summary>
    private bool _ready;

    /// <summary>当前配置（主要用来把悬浮窗的位置/存档版本号原样带过去，别被这里清掉）。</summary>
    private MessageConfig _cfg = new();

    public MessageToolPage()
    {
        InitializeComponent();

        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); SaveConfig(); };

        BuildSwatches(TextColorHost, TextPalette, hex => TextColorHex.Text = hex);
        BuildSwatches(BackColorHost, BackPalette, hex => BackColorHex.Text = hex);

        _cfg = MessageConfig.Load();

        // ── 初值（此刻 _ready 还是 false，下面这些赋值触发的回调都会被忽略）──
        ContentBox.Text = _cfg.Text;
        FontSizeBox.Value = _cfg.FontSize;
        BoldBox.IsChecked = _cfg.Bold;
        ScaleSlider.Value = Math.Clamp(_cfg.FullscreenScale, 1, 6);
        TextColorHex.Text = _cfg.TextColor;
        BackColorHex.Text = _cfg.BackColor;
        TransparentBox.IsChecked = _cfg.Transparent;

        // ── 事件在初值之后接 ──
        // ⚠️ NumberBox 的 ValueChanged 在 code-behind 里挂（本仓库既有约定，也在 XAML 里挂不上就不用折腾）
        FontSizeBox.ValueChanged += (_, _) => OnEdited();
        Unloaded += (_, _) => { _saveTimer.Stop(); SaveConfig(); };

        _ready = true;
        Apply();
    }

    /// <summary>把这个工具丢到「常用工具」浮窗里跑（简版：只留文字 / 字号 / 颜色 + 悬浮 / 全屏）。</summary>
    private void OpenPalette_Click(object? sender, RoutedEventArgs e)
        => Views.ToolPaletteWindow.ShowTool("message");

    // ══════════ 编辑 → 预览 / 落盘 ══════════

    private void Content_Changed(object? sender, TextChangedEventArgs e) => OnEdited();
    private void TextColorHex_Changed(object? sender, TextChangedEventArgs e) => OnEdited();
    private void BackColorHex_Changed(object? sender, TextChangedEventArgs e) => OnEdited();
    private void Bold_Changed(object? sender, RoutedEventArgs e) => OnEdited();
    private void Transparent_Changed(object? sender, RoutedEventArgs e) => OnEdited();
    private void Scale_Changed(object? sender, RangeBaseValueChangedEventArgs e) => OnEdited();

    private void OnEdited()
    {
        if (!_ready) return;
        Apply();
    }

    /// <summary>把当前设置画到预览上、顺手刷新还开着的悬浮窗，并把落盘推后 700ms。</summary>
    private void Apply()
    {
        var cfg = ReadControls();

        PreviewText.Text = string.IsNullOrWhiteSpace(cfg.Text) ? "（预览）" : cfg.Text;
        PreviewText.FontSize = Math.Clamp(cfg.FontSize, 12, 64);   // 预览按比例缩小，页面里放不下 200 号的字
        PreviewText.FontWeight = cfg.Bold ? FontWeight.Bold : FontWeight.Normal;
        PreviewText.LineHeight = Math.Round(PreviewText.FontSize * 1.32);
        PreviewText.Foreground = new SolidColorBrush(ColorOf(cfg.TextColor, Colors.Black));
        PreviewText.Opacity = string.IsNullOrWhiteSpace(cfg.Text) ? 0.45 : 1;

        PreviewCard.Background = new SolidColorBrush(ColorOf(cfg.BackColor, Colors.White));
        PreviewCard.BorderBrush = new SolidColorBrush(ColorOf(cfg.TextColor, Colors.Black), 0.18);
        PreviewHint.Text = cfg.Transparent ? "预览 · 悬浮窗无底色" : "预览";

        ScaleLabel.Text = $"全屏放大 {cfg.FullscreenScale:0} 倍";

        PaintSwatches();

        // 悬浮窗开着就跟着改（不动位置、不抢焦点：正在打字的人不该被弹窗打断）
        Views.MessageWindow.RefreshIfOpen(cfg);

        _saveTimer.Stop();
        _saveTimer.Start();
    }

    /// <summary>从控件读出完整配置。⚠️ 位置/版本号从 <see cref="_cfg"/> 带过来 —— 别在这里清掉用户拖过的位置。</summary>
    private MessageConfig ReadControls() => new()
    {
        Text = ContentBox.Text ?? "",
        FontSize = double.IsNaN(FontSizeBox.Value) ? 48 : Math.Clamp(FontSizeBox.Value, 12, 300),
        TextColor = MessageConfig.Normalize(TextColorHex.Text, "#FF1F1F1F"),
        BackColor = MessageConfig.Normalize(BackColorHex.Text, "#FFFDFDF7"),
        Bold = BoldBox.IsChecked == true,
        Transparent = TransparentBox.IsChecked == true,
        FullscreenScale = Math.Clamp(ScaleSlider.Value, 1, 6),
        X = _cfg.X,
        Y = _cfg.Y,
        PosVersion = _cfg.PosVersion,
    };

    private void SaveConfig()
    {
        if (!_ready) return;
        _cfg = ReadControls();
        _cfg.Save();
    }

    // ══════════ 动作 ══════════

    private void Float_Click(object? sender, RoutedEventArgs e)
    {
        var cfg = ReadControls();
        _cfg = cfg;
        cfg.Save();

        if (string.IsNullOrWhiteSpace(cfg.Text))
        {
            Toast.Text = "请先输入留言内容。";
            return;
        }

        Views.MessageWindow.Show(cfg);
        Toast.Text = "已显示在桌面上：拖到任意位置均可，双击或按 Esc 收起，鼠标移上去有关闭键。";
    }

    private void Fullscreen_Click(object? sender, RoutedEventArgs e)
    {
        var cfg = ReadControls();
        _cfg = cfg;
        cfg.Save();

        if (string.IsNullOrWhiteSpace(cfg.Text))
        {
            Toast.Text = "请先输入留言内容。";
            return;
        }

        Views.MessageFullscreenWindow.Show(cfg);
        Toast.Text = "已进入全屏展示：按 Esc、双击屏幕或单击右上角按钮退出。";
    }

    private void Hide_Click(object? sender, RoutedEventArgs e)
    {
        if (!Views.MessageWindow.IsOpen)
        {
            Toast.Text = "当前没有打开留言悬浮窗。";
            return;
        }

        Views.MessageWindow.HideNow();
        Toast.Text = "已收起桌面留言。";
    }

    // ══════════ 色卡 ══════════

    private void BuildSwatches(WrapPanel host, string[] palette, Action<string> onPick)
    {
        host.Children.Clear();

        foreach (var hex in palette)
        {
            var swatch = new Button
            {
                Width = 30,
                Height = 30,
                MinWidth = 0,
                Padding = new Thickness(0),
                Margin = new Thickness(0, 0, 8, 8),
                CornerRadius = new CornerRadius(4),
                BorderThickness = new Thickness(1),
                Background = new SolidColorBrush(ColorOf(hex, Colors.Transparent)),
                Tag = hex,
            };

            Avalonia.Automation.AutomationProperties.SetName(swatch, $"颜色 {hex}");
            ToolTip.SetTip(swatch, hex);
            swatch.Click += (_, _) => onPick(hex);

            host.Children.Add(swatch);
        }
    }

    /// <summary>把"当前正在用"的那颗色卡圈出来（对不上就都按未选中画，不报错）。</summary>
    private void PaintSwatches()
    {
        PaintOne(TextColorHost, MessageConfig.Normalize(TextColorHex.Text, "#FF1F1F1F"));
        PaintOne(BackColorHost, MessageConfig.Normalize(BackColorHex.Text, "#FFFDFDF7"));
    }

    private void PaintOne(WrapPanel host, string current)
    {
        var accent = Res("AccentFillColorDefaultBrush");
        var normal = Res("ControlStrokeColorDefaultBrush");

        foreach (var child in host.Children)
        {
            if (child is not Button swatch || swatch.Tag is not string hex) continue;

            var on = string.Equals(hex, current, StringComparison.OrdinalIgnoreCase);
            swatch.BorderThickness = new Thickness(on ? 2 : 1);
            swatch.BorderBrush = on ? accent : normal;
        }
    }

    // ══════════ 小工具 ══════════

    private Brush Res(string key) => Services.ThemeBrush.Get(this, key);

    private static Color ColorOf(string? hex, Color fallback)
        => MessageConfig.TryParseColor(hex, out var c) ? c : fallback;
}
