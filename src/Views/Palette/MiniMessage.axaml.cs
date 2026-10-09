using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using ClassSoftwareHub.Desktop.Data;

namespace ClassSoftwareHub.Desktop.Views.Palette;

/// <summary>
/// 浮窗版「桌面留言」：写一句话、调字号与颜色，一键丢到桌面上或全屏展示。
///
/// <para>
/// ⚠️ 与「内置工具 → 桌面留言」**共用同一份存档**（<see cref="MessageConfig"/>）：
///   在浮窗里改完，工具页打开就是刚改的样子；反过来也一样。
///   悬浮窗开着时也会跟着变（<c>MessageWindow.RefreshIfOpen</c>）。
///   要调「背景底色 / 全屏放大倍数 / 是否透明」这些细项，走「更多」那颗按钮去完整页。
/// </para>
/// </summary>
public sealed partial class MiniMessage : UserControl
{
    /// <summary>色卡跟工具页的**文字颜色**那一排完全一致（浮窗只放得下一排）。</summary>
    private static readonly string[] Palette =
    {
        "#FF1F1F1F", "#FFFFFFFF", "#FFD13438", "#FFE8590C", "#FF0F6CBD", "#FF107C10", "#FF7A3E9D",
    };

    private MessageConfig _cfg = new();
    private bool _ready;

    public MiniMessage()
    {
        InitializeComponent();

        BuildSwatches();

        _cfg = MessageConfig.Load();
        ContentBox.Text = _cfg.Text;
        FontSizeBox.Value = _cfg.FontSize;

        // ⚠️ NumberBox 的 ValueChanged 在初值之后接（本仓库既有约定）
        FontSizeBox.ValueChanged += (_, _) => OnEdited();

        _ready = true;
        Apply();
    }

    /// <summary>浮窗切到这个工具时调一次：重读存档 + 刷新（工具页可能刚改过内容/字号/颜色）。</summary>
    public void Reload()
    {
        _cfg = MessageConfig.Load();
        _pickedColor = null;            // 重读存档 → 色卡选中态跟着存档走

        _ready = false;
        ContentBox.Text = _cfg.Text;
        FontSizeBox.Value = _cfg.FontSize;
        _ready = true;

        Apply();
    }

    // ══════════ 编辑 ══════════

    private void Content_Changed(object? sender, TextChangedEventArgs e) => OnEdited();

    private void OnEdited()
    {
        if (!_ready) return;
        Apply();
    }

    private void Apply()
    {
        var cfg = Read();

        PreviewText.Text = string.IsNullOrWhiteSpace(cfg.Text) ? "（还没有内容）" : cfg.Text;
        PreviewText.FontSize = Math.Clamp(cfg.FontSize / 2.5, 11, 22);   // 浮窗里按比例缩着画
        PreviewText.FontWeight = cfg.Bold ? FontWeight.Bold : FontWeight.Normal;
        PreviewText.Foreground = new SolidColorBrush(ColorOf(cfg.TextColor, Colors.Black));
        PreviewText.Opacity = string.IsNullOrWhiteSpace(cfg.Text) ? 0.5 : 1;
        PreviewCard.Background = new SolidColorBrush(ColorOf(cfg.BackColor, Colors.White));

        PaintSwatches();

        // 悬浮窗开着就跟着改（不移动、不抢焦点）
        MessageWindow.RefreshIfOpen(cfg);

        _cfg = cfg;
        cfg.Save();

        // 「收起」只在真有悬浮窗时才有意义
        HideButton.IsEnabled = MessageWindow.IsOpen;
    }

    /// <summary>从控件读配置；⚠️ 位置/版本号从 <see cref="_cfg"/> 带过来，别把用户拖过的位置清掉。</summary>
    private MessageConfig Read() => new()
    {
        Text = ContentBox.Text ?? "",
        FontSize = double.IsNaN(FontSizeBox.Value) ? 48 : Math.Clamp(FontSizeBox.Value, 12, 300),
        TextColor = MessageConfig.Normalize(SelectedColor(), "#FF1F1F1F"),
        BackColor = _cfg.BackColor,            // 底色只在完整页里调，这里原样带过去
        Bold = _cfg.Bold,
        Transparent = _cfg.Transparent,
        FullscreenScale = _cfg.FullscreenScale,
        X = _cfg.X,
        Y = _cfg.Y,
        PosVersion = _cfg.PosVersion,
    };

    // ══════════ 动作 ══════════

    private void Float_Click(object? sender, RoutedEventArgs e)
    {
        var cfg = Read();
        _cfg = cfg;
        cfg.Save();

        if (string.IsNullOrWhiteSpace(cfg.Text)) return;      // 没内容就别弹一个空框出来
        MessageWindow.Show(cfg);
        HideButton.IsEnabled = true;
    }

    private void Fullscreen_Click(object? sender, RoutedEventArgs e)
    {
        var cfg = Read();
        _cfg = cfg;
        cfg.Save();

        if (string.IsNullOrWhiteSpace(cfg.Text)) return;
        MessageFullscreenWindow.Show(cfg);
    }

    private void Hide_Click(object? sender, RoutedEventArgs e)
    {
        MessageWindow.HideNow();
        HideButton.IsEnabled = MessageWindow.IsOpen;
    }

    private void Details_Click(object? sender, RoutedEventArgs e)
    {
        ToolPaletteWindow.HidePalette();
        App.MainWindow?.OpenToolSettings(typeof(Pages.Tools.MessageToolPage));
    }

    // ══════════ 色卡 ══════════

    private void BuildSwatches()
    {
        ColorHost.Children.Clear();

        foreach (var hex in Palette)
        {
            var swatch = new Button
            {
                Width = 26,
                Height = 26,
                MinWidth = 0,
                Padding = new Thickness(0),
                Margin = new Thickness(0, 0, 6, 0),
                CornerRadius = new CornerRadius(4),
                BorderThickness = new Thickness(1),
                Background = new SolidColorBrush(ColorOf(hex, Colors.Transparent)),
                Tag = hex,
            };

            Avalonia.Automation.AutomationProperties.SetName(swatch, $"颜色 {hex}");
            ToolTip.SetTip(swatch, hex);
            swatch.Click += (_, _) =>
            {
                // ⚠️ 浮窗里没有"自定义颜色"输入框，直接把选中的色卡记在 _cfg 上（见 SelectedColor）
                _pickedColor = hex;
                OnEdited();
            };

            ColorHost.Children.Add(swatch);
        }
    }

    /// <summary>当前选中的文字颜色（点过色卡就用它，否则用存档里的）。</summary>
    private string? _pickedColor;
    private string SelectedColor() => _pickedColor ?? _cfg.TextColor;

    private void PaintSwatches()
    {
        var accent = Services.ThemeBrush.Get(this, "AccentFillColorDefaultBrush");
        var normal = Services.ThemeBrush.Get(this, "ControlStrokeColorDefaultBrush");

        var current = MessageConfig.Normalize(SelectedColor(), "#FF1F1F1F");

        foreach (var child in ColorHost.Children)
        {
            if (child is not Button swatch || swatch.Tag is not string hex) continue;

            var on = string.Equals(hex, current, StringComparison.OrdinalIgnoreCase);
            swatch.BorderThickness = new Thickness(on ? 2 : 1);
            swatch.BorderBrush = on ? accent : normal;
        }
    }

    private static Color ColorOf(string? hex, Color fallback)
        => MessageConfig.TryParseColor(hex, out var c) ? c : fallback;
}
