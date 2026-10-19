using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using ClassSoftwareHub.Desktop.Services;

namespace ClassSoftwareHub.Desktop.Pages.Tools;

/// <summary>
/// 化学式编辑器（2026-10-18 自上游 1.2.0 移植；WinUI → Avalonia）。
///
/// <para>
/// 站在**学生 / 老师**的角度排的（Nick 2026-10-17）：
/// 左边先用**常用元素**的大按钮（带中文名，一眼能找到"铁、氧、钠"），
/// 需要别的元素再展开完整周期表；右边是括号/数字与常用物质、离子根。
/// </para>
/// <para>
/// ⛔ 一上来就摆 118 个 24px 的小格子是开发者思维：中学生配平翻来覆去就那十几种元素，
/// 满屏小方块只会让人找不着。
/// ⛔ 不让用户手敲 "H2O" —— 键盘上打出来的 2 是正文，看着像 H 乘以 2。
/// </para>
/// <para>
/// ⚠️ WinUI → Avalonia：<c>HyperlinkButton</c> → 透明底 Button；
/// <c>RowSpacing/ColumnSpacing</c> → 子元素 Margin；<c>Visibility</c> → <c>IsVisible</c>；
/// <c>ToolTipService</c> → <c>ToolTip</c>；自动化名字用 Avalonia 的 <c>AutomationProperties</c>。
/// </para>
/// </summary>
internal sealed class FormulaEditor
{
    private List<string> _substances;
    private List<string> _ions;

    private readonly StringBuilder _raw;
    private readonly IBrush _brush;
    private readonly StackPanel _preview;
    private readonly TextBlock _error;
    private readonly StackPanel _fullTableHost;
    private readonly Border _fullTableWrap;

    public Grid View { get; }

    /// <summary>内容变了，参数 = 当前是否合法（用来控制「确定」能不能点）。</summary>
    public event Action<bool>? Changed;

    public bool IsValid { get; private set; }

    public FormulaEditor(string initial, IBrush brush)
    {
        _raw = new StringBuilder(initial ?? "");
        _brush = brush;

        var favorites = ChemicalFavorites.Load();
        _substances = favorites.Substances;
        _ions = favorites.Ions;

        _preview = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            MinHeight = 46,
            VerticalAlignment = VerticalAlignment.Center,
        };

        _error = new TextBlock
        {
            FontSize = 12,
            Foreground = ThemeBrush.Get(null, "SystemFillColorCriticalBrush"),
            TextWrapping = TextWrapping.Wrap,
            IsVisible = false,
        };

        _fullTableHost = new StackPanel();
        _fullTableWrap = new Border
        {
            IsVisible = false,
            Margin = new Thickness(0, 8, 0, 0),
            Child = _fullTableHost,
        };

        var root = new Grid { MinWidth = 620 };
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(340) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var preview = BuildPreviewCard();
        Grid.SetColumnSpan(preview, 2);
        root.Children.Add(preview);

        var left = BuildLeftPanel();
        Grid.SetRow(left, 1);
        left.Margin = new Thickness(0, 12, 14, 0);
        root.Children.Add(left);

        var right = BuildRightPanel();
        Grid.SetRow(right, 1);
        Grid.SetColumn(right, 1);
        right.Margin = new Thickness(0, 12, 0, 0);
        root.Children.Add(right);

        View = root;
        Refresh();
    }

    public string Value => _raw.ToString();

    // ── 预览 ──
    private Border BuildPreviewCard()
    {
        var stack = new StackPanel();
        stack.Children.Add(_preview);
        stack.Children.Add(_error);

        // ⛔ 删除键必须贴在输入内容旁边：放到下面的小键盘里，用户得满处找（Nick 点名）
        var backspace = new Button
        {
            Content = new FluentAvalonia.UI.Controls.FontIcon { Glyph = "\uE750", FontSize = 15 },
            Width = 48,
            Height = 44,
            Padding = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        ToolTip.SetTip(backspace, "删除最后一个字符");
        ButtonFeel.ApplyPressGradient(backspace);
        backspace.Click += (_, _) => Backspace();

        var clear = new Button
        {
            Content = "清空",
            Width = 64,
            Height = 44,
            FontSize = 13,
            Padding = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        ToolTip.SetTip(clear, "清空输入");
        ButtonFeel.ApplyPressGradient(clear);
        clear.Click += (_, _) => { _raw.Clear(); Refresh(); };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        grid.Children.Add(stack);
        Grid.SetColumn(backspace, 1);
        backspace.Margin = new Thickness(8, 0, 0, 0);
        grid.Children.Add(backspace);
        Grid.SetColumn(clear, 2);
        clear.Margin = new Thickness(8, 0, 0, 0);
        grid.Children.Add(clear);

        return new Border
        {
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(16, 8, 12, 8),
            Background = ThemeBrush.Get(null, "ControlFillColorSecondaryBrush"),
            Child = grid,
        };
    }

    private void Refresh()
    {
        _preview.Children.Clear();

        if (_raw.Length == 0)
        {
            _preview.Children.Add(new TextBlock
            {
                Text = "点左边的元素开始输入",
                FontSize = 13,
                Opacity = 0.4,
                VerticalAlignment = VerticalAlignment.Center,
            });

            _error.IsVisible = false;
            IsValid = false;
            Changed?.Invoke(false);
            return;
        }

        var rendered = ChemicalNotation.Formula(_raw.ToString(), 30, _brush);
        rendered.VerticalAlignment = VerticalAlignment.Center;
        _preview.Children.Add(rendered);

        IsValid = ChemistryBalancer.ValidateFormula(_raw.ToString(), out var error);

        if (!IsValid && error is { Length: > 0 })
        {
            _error.Text = error;
            _error.IsVisible = true;
        }
        else
        {
            _error.IsVisible = false;
        }

        Changed?.Invoke(IsValid);
    }

    // ── 左：常用元素（大）+ 完整周期表（折叠） ──
    private Control BuildLeftPanel()
    {
        var panel = new StackPanel();

        // 标题跟着内容走：展开周期表后还写「常用元素」会让人以为下面还是常用那几个
        var sectionTitle = SectionTitle("常用元素");
        panel.Children.Add(sectionTitle);

        var grid = new Grid();
        const int columns = 7;

        for (var c = 0; c < columns; c++)
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var rows = (PeriodicTable.Common.Count + columns - 1) / columns;
        for (var r = 0; r < rows; r++)
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(62) });

        for (var i = 0; i < PeriodicTable.Common.Count; i++)
        {
            var (symbol, name) = PeriodicTable.Common[i];
            var cell = BuildCommonElement(symbol, name);
            Grid.SetRow(cell, i / columns);
            Grid.SetColumn(cell, i % columns);
            cell.Margin = new Thickness(i % columns == 0 ? 0 : 3, i / columns == 0 ? 0 : 3, 0, 0);
            grid.Children.Add(cell);
        }

        grid.Margin = new Thickness(0, 8, 0, 0);
        panel.Children.Add(grid);

        // 完整周期表：默认收起，需要时再展开。
        // ⛔ 展开时把「常用元素」整块收起来：两块都摆着，左栏一定比可视区高，
        //    周期表得滚半天才看得见（Nick 2026-10-17）。
        ScrollViewer? scroller = null;

        var toggle = new Button
        {
            Content = "找不到？展开完整元素周期表",
            FontSize = 12,
            Padding = new Thickness(0, 2, 0, 2),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        ToolTip.SetTip(toggle, "展开 / 收起完整元素周期表");
        toggle.Click += (_, _) =>
        {
            if (_fullTableWrap.IsVisible)
            {
                _fullTableWrap.IsVisible = false;
                grid.IsVisible = true;
                sectionTitle.Text = "常用元素";
                toggle.Content = "找不到？展开完整元素周期表";
                return;
            }

            if (_fullTableHost.Children.Count == 0)
            {
                var width = scroller is { Bounds.Width: > 0 } s ? s.Bounds.Width : 800;
                _fullTableHost.Children.Add(BuildPeriodicTable(width));
            }

            grid.IsVisible = false;
            sectionTitle.Text = "元素周期表";
            _fullTableWrap.IsVisible = true;
            toggle.Content = "收起完整元素周期表";
        };

        toggle.Margin = new Thickness(0, 8, 0, 0);
        panel.Children.Add(toggle);
        panel.Children.Add(_fullTableWrap);

        // ⛔ 整条左栏也要能滚：展开完整周期表后，内容一定比可视区高，
        //    没有滚动区就只能被裁掉（Nick 2026-10-17：滑到底还是被截断）。
        scroller = new ScrollViewer
        {
            Content = panel,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            VerticalAlignment = VerticalAlignment.Stretch,
        };

        return scroller;
    }

    private static TextBlock SectionTitle(string text) => new()
    {
        Text = text,
        FontSize = 12,
        Opacity = 0.55,
    };

    /// <summary>常用元素：符号大、中文名在下，按钮本身就是"看得清、点得中"的尺寸。</summary>
    private Button BuildCommonElement(string symbol, string name)
    {
        var content = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };

        content.Children.Add(new TextBlock
        {
            Text = symbol,
            FontSize = 18,
            FontWeight = FontWeight.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
        });

        content.Children.Add(new TextBlock
        {
            Text = name,
            FontSize = 10,
            Opacity = 0.6,
            HorizontalAlignment = HorizontalAlignment.Center,
        });

        var button = new Button
        {
            Content = content,
            Tag = symbol,
            Padding = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            Background = ThemeBrush.Get(null, "ControlFillColorDefaultBrush"),
        };

        ToolTip.SetTip(button, $"{symbol}　{name}");
        ButtonFeel.ApplyPressGradient(button);
        AutomationProperties.SetName(button, $"{symbol} {name}");
        button.Click += (sender, _) => Append((sender as Button)?.Tag as string ?? symbol);

        return button;
    }

    /// <summary>
    /// 完整周期表（18 族 × 7 周期 + 镧系锕系两行）。
    ///
    /// <para>
    /// 格子尺寸按左栏的实际宽度算：窗口窄的时候缩到 30，宽的时候给到 44，
    /// 这样绝大多数情况下 18 列正好摆下、不用横向拖。
    /// </para>
    /// </summary>
    private Control BuildPeriodicTable(double availableWidth)
    {
        // 18 列 + 17 个 2px 间隙。⛔ 上限别贴着算：展开表格后纵向滚动条会占掉十几像素，
        // 按满宽算会让第 18 族（He/Ne/Ar…）正好被裁掉一截（Nick 2026-10-17 实测）。
        var cellWidth = Math.Clamp((availableWidth - 48) / 18, 30, 42);
        var cellHeight = Math.Round(cellWidth * 1.19);

        var grid = new Grid { HorizontalAlignment = HorizontalAlignment.Left };

        for (var c = 0; c < 18; c++)
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(cellWidth) });

        for (var r = 0; r < 10; r++)
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(cellHeight) });

        foreach (var element in PeriodicTable.All)
        {
            var cell = BuildElementCell(element, cellWidth, cellHeight);
            Grid.SetRow(cell, element.Row - 1);
            Grid.SetColumn(cell, element.Column - 1);
            cell.Margin = new Thickness(2, 2, 0, 0);
            grid.Children.Add(cell);
        }

        var lanth = GroupLabel("镧系", 8);
        grid.Children.Add(lanth);
        var actin = GroupLabel("锕系", 9);
        grid.Children.Add(actin);

        // ⛔ 只给横向滚动：竖向交给外层那条滚动区，
        //    两层都能竖向滚的话滚轮会互相抢，手感很怪。
        return new ScrollViewer
        {
            Content = grid,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
        };
    }

    private static TextBlock GroupLabel(string text, int row)
    {
        var label = new TextBlock
        {
            Text = text,
            FontSize = 9,
            Opacity = 0.4,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        Grid.SetRow(label, row - 1);
        Grid.SetColumn(label, 0);
        Grid.SetColumnSpan(label, 2);
        return label;
    }

    private Button BuildElementCell(Element element, double cellWidth, double cellHeight)
    {
        var name = PeriodicTable.ChineseName(element.Number);

        // 三行各占各的位置（⛔ 不能都叠在中间：符号和中文名会糊成一团）：
        // 上面留原子序数，中间符号，下面中文名。
        var content = new Grid();
        content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(Math.Round(cellHeight * 0.21)) });
        content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(Math.Round(cellHeight * 0.42)) });
        content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var number = new TextBlock
        {
            Text = element.Number.ToString(),
            FontSize = 7,
            Opacity = 0.38,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(4, 1, 0, 0),
        };
        content.Children.Add(number);

        var symbol = new TextBlock
        {
            Text = element.Symbol,
            FontSize = Math.Max(11, Math.Round(cellWidth * 0.31)),
            FontWeight = FontWeight.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetRow(symbol, 1);
        content.Children.Add(symbol);

        var chinese = new TextBlock
        {
            Text = name,
            FontSize = Math.Max(8.5, Math.Round(cellWidth * 0.23)),
            Opacity = 0.6,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetRow(chinese, 2);
        content.Children.Add(chinese);

        var button = new Button
        {
            Content = content,
            Tag = element.Symbol,
            Padding = new Thickness(0),
            Width = cellWidth,
            Height = cellHeight,
            CornerRadius = new CornerRadius(4),
            Background = ThemeBrush.Get(null, "ControlFillColorDefaultBrush"),
        };

        ToolTip.SetTip(button,
            name.Length > 0 ? $"{element.Symbol}　{name}　原子序数 {element.Number}"
                            : $"{element.Symbol}　原子序数 {element.Number}");

        AutomationProperties.SetName(button,
            name.Length > 0 ? $"{element.Symbol} {name}" : element.Symbol);

        button.Click += (sender, _) => Append((sender as Button)?.Tag as string ?? element.Symbol);

        return button;
    }

    // ── 右：括号 / 数字 + 常用物质 / 离子根 ──
    private Control BuildRightPanel()
    {
        // ⛔ 整条右栏都进滚动区：窗口矮的时候 数字键 / 常用物质 / 离子根 必须都够得着，
        //    固定住键盘只让下面滚，离子根必然被截断（Nick 两次点名）。
        var content = new StackPanel();
        content.Children.Add(BuildKeypad());
        content.Children.Add(BuildFavoriteSection("常用物质", false));
        content.Children.Add(BuildFavoriteSection("常用离子根", true));

        return new ScrollViewer
        {
            Content = content,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            VerticalAlignment = VerticalAlignment.Stretch,
        };
    }

    private Control BuildFavoriteSection(string title, bool isIons)
    {
        var section = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };
        var body = new StackPanel();

        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        header.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 12,
            Opacity = 0.55,
            VerticalAlignment = VerticalAlignment.Center,
        });

        var edit = new Button
        {
            Content = "编辑",
            FontSize = 11.5,
            Padding = new Thickness(10, 3, 10, 3),
        };
        Grid.SetColumn(edit, 1);
        header.Children.Add(edit);

        void ShowChips()
        {
            body.Children.Clear();

            var items = isIons ? _ions : _substances;
            if (items.Count == 0)
            {
                body.Children.Add(new TextBlock { Text = "（空）点「编辑」添加", FontSize = 11.5, Opacity = 0.4 });
                return;
            }

            body.Children.Add(BuildChips(items));
        }

        edit.Click += (_, _) =>
        {
            body.Children.Clear();

            var box = new TextBox
            {
                Text = string.Join("\n", isIons ? _ions : _substances),
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                FontSize = 13,
                Height = isIons ? 120 : 160,
                Watermark = "一行一个",
            };
            body.Children.Add(box);

            var hint = new TextBlock
            {
                Text = isIons
                    ? "一行一个。下标直接写数字，电荷写在 ^ 之后，例如 SO4^2-、NH4^+。"
                    : "一行一个。下标直接写数字，例如 H2O、Ca(OH)2。",
                FontSize = 11,
                Opacity = 0.45,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 4, 0, 0),
            };
            body.Children.Add(hint);

            var actions = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 6, 0, 0),
            };

            var save = new Button { Content = "保存", FontSize = 12, Padding = new Thickness(12, 4, 12, 4) };
            save.Click += (_, _) =>
            {
                var list = (box.Text ?? "")
                    .Split('\n')
                    .Select(l => l.Trim())
                    .Where(l => l.Length > 0)
                    .Distinct(StringComparer.Ordinal)
                    .Take(40)
                    .ToList();

                if (isIons) _ions = list; else _substances = list;
                ChemicalFavorites.Save(_substances, _ions);
                ShowChips();
            };
            actions.Children.Add(save);

            var reset = new Button { Content = "恢复默认", FontSize = 12, Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(6, 0, 0, 0) };
            reset.Click += (_, _) =>
            {
                if (isIons) _ions = ChemicalFavorites.DefaultIons.ToList();
                else _substances = ChemicalFavorites.DefaultSubstances.ToList();

                ChemicalFavorites.Save(_substances, _ions);
                ShowChips();
            };
            actions.Children.Add(reset);

            var cancel = new Button { Content = "取消", FontSize = 12, Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(6, 0, 0, 0) };
            cancel.Click += (_, _) => ShowChips();
            actions.Children.Add(cancel);

            body.Children.Add(actions);
        };

        section.Children.Add(header);
        body.Margin = new Thickness(0, 6, 0, 0);
        section.Children.Add(body);
        ShowChips();
        section.Margin = new Thickness(0, 10, 0, 0);

        return section;
    }

    /// <summary>常用项：两列，按钮 40 高 —— 鼠标好点、投影上也看得清。</summary>
    private Control BuildChips(IReadOnlyList<string> items)
    {
        var stack = new StackPanel();

        for (var i = 0; i < items.Count; i += 2)
        {
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            for (var c = 0; c < 2 && i + c < items.Count; c++)
            {
                var text = items[i + c];

                // ⛔ 按钮上不能出 "SO4^2-" 这种计算机写法：照化学式排成 SO₄²⁻（Nick 2026-10-17）
                var chip = new Button
                {
                    Content = ChemicalNotation.Formula(text, 14, _brush),
                    Tag = text,
                    Padding = new Thickness(2),
                    Height = 40,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Center,
                    VerticalContentAlignment = VerticalAlignment.Center,
                };

                ToolTip.SetTip(chip, $"追加 {ChemicalNotation.Plain(text)}");
        ButtonFeel.ApplyPressGradient(chip);
                AutomationProperties.SetName(chip, ChemicalNotation.Plain(text));

                chip.Click += (sender, _) => Append((sender as Button)?.Tag as string ?? text);
                Grid.SetColumn(chip, c);
                if (c == 1) chip.Margin = new Thickness(5, 0, 0, 0);
                if (i > 0) chip.Margin = new Thickness(c == 1 ? 5 : 0, 5, 0, 0);
                row.Children.Add(chip);
            }

            stack.Children.Add(row);
        }

        return stack;
    }

    // ── 括号 / 结晶水 / 数字 ──
    private Control BuildKeypad()
    {
        var stack = new StackPanel();

        var symbols = new StackPanel { Orientation = Orientation.Horizontal };
        var lp = Key("(", "左括号", width: 60);
        symbols.Children.Add(lp);
        var rp = Key(")", "右括号", width: 60);
        rp.Margin = new Thickness(6, 0, 0, 0);
        symbols.Children.Add(rp);
        var dot = Key("·", "结晶水（如 CuSO4·5H2O）", width: 60);
        dot.Margin = new Thickness(6, 0, 0, 0);
        symbols.Children.Add(dot);
        stack.Children.Add(symbols);

        var digits = new StackPanel { Orientation = Orientation.Horizontal };
        for (var d = 1; d <= 9; d++)
        {
            var text = d.ToString();
            var k = Key(text, "下标", () => Append(text), width: 30);
            k.Margin = new Thickness(4, 5, 0, 0);
            digits.Children.Add(k);
        }

        var zero = Key("0", "下标", () => Append("0"), width: 30);
        zero.Margin = new Thickness(4, 5, 0, 0);
        digits.Children.Add(zero);
        stack.Children.Add(digits);

        return stack;
    }

    private Button Key(string text, string tip, Action? onClick = null, double width = 26)
    {
        var button = new Button
        {
            Content = text,
            FontSize = 15,
            Width = width,
            Height = 40,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(4),
        };

        ToolTip.SetTip(button, tip);
        ButtonFeel.ApplyPressGradient(button);
        button.Click += (_, _) =>
        {
            if (onClick is not null) onClick();
            else Append(text);
        };

        return button;
    }

    private void Append(string text)
    {
        _raw.Append(text);
        Refresh();
    }

    private void Backspace()
    {
        if (_raw.Length > 0) _raw.Remove(_raw.Length - 1, 1);
        Refresh();
    }
}
