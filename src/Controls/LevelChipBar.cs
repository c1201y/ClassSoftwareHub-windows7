using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using ClassSoftwareHub.Desktop.Core;
using ClassSoftwareHub.Desktop.Services;

namespace ClassSoftwareHub.Desktop.Controls;

/// <summary>
/// 一排「级别」筛选芯片 —— 借鉴 ClassIsland 日志窗顶部那条筛选栏。
///
/// 形态：<c>[⌕ 调试 2] [ⓘ 信息 27] [⚠ 警告 0] [⊗ 错误 0]</c>
///
///   · **多选**：每个芯片是一个独立开关，"勾上 = 显示该级别"。四个全勾 = 全显示。
///   · **带计数**：每个芯片右侧挂该级别的条数，这是排查时最先要看的东西。
///   · 一个都不勾 = 一条也不显示（字面语义）。页面会用空表提示引导勾回来，
///     所以这里不需要"至少留一个"的兜底，也不需要在按钮上做禁用态。
///
/// 选中态**不走 Accent**：四个 Accent 药丸排一排既刺眼，又违反"一页 Accent 一到两处"。
/// 所以把 ToggleButton 的 on/off 画刷就地换成浅填充/透明
/// —— 看得见"开着"，但不抢戏。实现的还是系统原生控件（能用系统控件就不自己画）。
///
/// 勾选态只驱动**观感**，筛选逻辑走 Click：
/// 程序里改 IsChecked 不会触发 Click，所以不需要"抑制标志"。
///
/// Avalonia 适配：FontIcon 用 FluentAvalonia；画刷统一经 ThemeBrush.Get 取
/// （本控件无 XAML 模板，直接以 host 作为主题查找锚点）；
/// ToolTip 走 ToolTip.SetTip；透明一律用 Brushes.Transparent 现造，不查主题键。
/// </summary>
public sealed class LevelChipBar
{
    private sealed class Chip
    {
        public LogLevel Level { get; init; }
        public string Label { get; init; } = "";
        public ToggleButton Button { get; init; } = null!;
        public StackPanel Content { get; init; } = null!;
        public TextBlock CountText { get; init; } = null!;
    }

    private readonly List<Chip> _chips = new();
    private readonly StyledElement? _anchor;

    /// <summary>勾选状态变了（用户点芯片才会触发）。</summary>
    public event Action? SelectionChanged;

    /// <summary>把四个芯片建进 <paramref name="host"/>（它得是横向排列的 Panel）。默认全开。</summary>
    public LevelChipBar(Panel host)
    {
        _anchor = host;

        Add(host, LogLevel.Debug, "调试", "\uE721");    // 放大镜（诊断）
        Add(host, LogLevel.Info, "信息", "\uE946");     // 信息圆标
        Add(host, LogLevel.Warning, "警告", "\uE7BA");  // 警告三角
        Add(host, LogLevel.Error, "错误", "\uE783");    // 错误圆标

        // 出厂全开 = 显示全部。⛔ 不要在 XAML 里写 IsChecked="True" —— 抢跑坑。
        foreach (var c in _chips) c.Button.IsChecked = true;
    }

    /// <summary>当前勾着的级别（可能是空列表）。</summary>
    public List<LogLevel> SelectedLevels
    {
        get
        {
            var list = new List<LogLevel>(_chips.Count);
            foreach (var c in _chips)
                if (c.Button.IsChecked == true) list.Add(c.Level);
            return list;
        }
    }

    /// <summary>四个都勾着（= 不按级别筛）。</summary>
    public bool AllOn
    {
        get
        {
            foreach (var c in _chips) if (c.Button.IsChecked != true) return false;
            return true;
        }
    }

    /// <summary>一个都没勾。</summary>
    public bool AllOff
    {
        get
        {
            foreach (var c in _chips) if (c.Button.IsChecked == true) return false;
            return true;
        }
    }

    /// <summary>
    /// 交给 LogReader.Filter 的级别集合。全开时返回 null = 不按级别筛。
    /// </summary>
    public IReadOnlyCollection<LogLevel>? FilterLevels => AllOn ? null : SelectedLevels;

    /// <summary>刷新各芯片右侧的计数（只统计当前文件的全量，不随筛选变化）。</summary>
    public void SetCounts(LogStats stats)
    {
        foreach (var c in _chips)
        {
            var n = c.Level switch
            {
                LogLevel.Debug => stats.Debug,
                LogLevel.Warning => stats.Warning,
                LogLevel.Error => stats.Error,
                _ => stats.Info,
            };

            c.CountText.Text = n.ToString("N0");

            // 这一档本次没有内容 ⇒ 整颗压暗。⛔ 但不禁用：禁用了点不动，
            // 反而像"按钮坏了"；压暗只是提示"这次没这一档"。
            c.Button.Opacity = n == 0 ? 0.7 : 1.0;
            ToolTip.SetTip(c.Button, n == 0 ? $"{c.Label}：本次没有" : $"{c.Label}：{n:N0} 条");
        }
    }

    private void Add(Panel host, LogLevel level, string label, string glyph)
    {
        var icon = new FluentAvalonia.UI.Controls.FontIcon
        {
            Glyph = glyph,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = LogReader.BrushOf(level, host),
        };

        var name = new TextBlock { Text = label, FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center };

        var count = new TextBlock
        {
            Text = "0",
            FontSize = 12,
            Opacity = 0.6,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
        content.Children.Add(icon);
        content.Children.Add(name);
        content.Children.Add(count);

        var button = new ToggleButton
        {
            Content = content,
            MinWidth = 0,
            // 比默认 Button 收紧一圈：这排是"筛选器"，不该有按钮那种体量
            Padding = new Thickness(9, 3, 9, 3),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Quiet(button);

        var chip = new Chip { Level = level, Label = label, Button = button, Content = content, CountText = count };

        button.Checked += (_, _) => Sync(chip);
        button.Unchecked += (_, _) => Sync(chip);
        button.Click += (_, _) => SelectionChanged?.Invoke();

        _chips.Add(chip);
        host.Children.Add(button);
    }

    private static void Sync(Chip chip) =>
        chip.Content.Opacity = chip.Button.IsChecked == true ? 1.0 : 0.45;

    /// <summary>
    /// 把 ToggleButton 的 on/off 画刷换成安静版本（实例级资源覆盖）。
    ///   · off = 无底无边 + 内容压淡（读起来就是一枚普通标签）
    ///   · on  = 一层浅填充 + 一圈细边 + 主文字（"开着"，但不用 Accent 喊）
    ///
    /// ⚠️ "无"那一侧一律用 Brushes.Transparent，**不要**去查 …TransparentBrush 主题键。
    /// 透明本身与主题无关，不算"写死颜色"。
    /// </summary>
    private void Quiet(ToggleButton button)
    {
        void Set(string key, IBrush? brush)
        {
            if (brush is null) return;
            try { button.Resources[key] = brush; } catch { }
        }

        var none = Brushes.Transparent;
        var hover = ThemeBrush.Get(_anchor, "SubtleFillColorSecondaryBrush");
        var press = ThemeBrush.Get(_anchor, "SubtleFillColorTertiaryBrush");
        var onFill = ThemeBrush.Get(_anchor, "ControlFillColorSecondaryBrush");
        var onEdge = ThemeBrush.Get(_anchor, "CardStrokeColorDefaultBrush");
        var onText = ThemeBrush.Get(_anchor, "TextFillColorPrimaryBrush");
        var onTextDim = ThemeBrush.Get(_anchor, "TextFillColorSecondaryBrush");

        Set("ToggleButtonBackground", none);
        Set("ToggleButtonBackgroundPointerOver", hover);
        Set("ToggleButtonBackgroundPressed", press);
        Set("ToggleButtonBorderBrush", none);
        Set("ToggleButtonBorderBrushPointerOver", none);
        Set("ToggleButtonBorderBrushPressed", none);

        Set("ToggleButtonBackgroundChecked", onFill);
        Set("ToggleButtonBackgroundCheckedPointerOver", press);
        Set("ToggleButtonBackgroundCheckedPressed", press);
        Set("ToggleButtonForegroundChecked", onText);
        Set("ToggleButtonForegroundCheckedPointerOver", onText);
        Set("ToggleButtonForegroundCheckedPressed", onTextDim);
        Set("ToggleButtonBorderBrushChecked", onEdge);
        Set("ToggleButtonBorderBrushCheckedPointerOver", onEdge);
        Set("ToggleButtonBorderBrushCheckedPressed", onEdge);
    }
}
