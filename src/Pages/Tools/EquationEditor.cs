using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using ClassSoftwareHub.Desktop.Services;

namespace ClassSoftwareHub.Desktop.Pages.Tools;

/// <summary>
/// 方程编辑器（浮层里那块；2026-10-18 自上游 1.2.0 移植，WinUI → Avalonia）。
///
/// <para>
/// 与化学式编辑器同一套思路（Nick 2026-10-17）：
/// **不让用户对着空白框回忆语法**，而是把数字、变量、运算符、函数做成大按钮点出来；
/// 预览按数学排版显示、边输边校验，「确定」只在式子真的能被求解器理解时才亮。
/// </para>
/// <para>
/// ⛔ 但比化学式多留一个口子：方程式子长、种类多，老师常常是从题目里**复制粘贴**过来的，
/// 所以顶栏给「键盘输入」开关，随时能切成直接打字。
/// </para>
/// </summary>
internal sealed class EquationEditor
{
    private readonly StringBuilder _raw = new();
    private readonly IBrush _brush;

    private readonly StackPanel _previewHost = new()
    {
        Orientation = Orientation.Horizontal,
        MinHeight = 44,
        VerticalAlignment = VerticalAlignment.Center,
    };

    private readonly TextBox _textBox;

    private readonly TextBlock _error = new()
    {
        FontSize = 12,
        Foreground = ThemeBrush.Get(null, "SystemFillColorCriticalBrush"),
        TextWrapping = TextWrapping.Wrap,
        IsVisible = false,
    };

    private readonly ToggleButton _typingToggle;

    /// <summary>打字模式下 TextChanged 与预览刷新会互相触发，用它挡住回环。</summary>
    private bool _syncing;

    public Grid View { get; }

    /// <summary>内容变了，参数 = 当前是否合法（控制「确定」能不能点）。</summary>
    public event Action<bool>? Changed;

    public bool IsValid { get; private set; }

    public string Value => _raw.ToString();

    public EquationEditor(string? initial, IBrush brush)
    {
        _raw.Append(initial ?? "");
        _brush = brush;

        _textBox = new TextBox
        {
            FontSize = 20,
            Padding = new Thickness(0),
            BorderThickness = new Thickness(0),
            MinHeight = 44,
            VerticalAlignment = VerticalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Background = new SolidColorBrush(Colors.Transparent),
            IsVisible = false,
            Watermark = "例如 x^2 - 5x + 6 = 0",
        };
        _textBox.TextChanged += (_, _) =>
        {
            if (_syncing) return;

            _syncing = true;
            _raw.Clear();
            _raw.Append(_textBox.Text ?? "");
            _syncing = false;

            Refresh();
        };

        _typingToggle = new ToggleButton
        {
            Content = new FluentAvalonia.UI.Controls.FontIcon { Glyph = "\uE765", FontSize = 14 },
            Width = 44,
            Height = 44,
            Padding = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        ToolTip.SetTip(_typingToggle, "改用键盘直接输入（可粘贴）");
        ButtonFeel.ApplyPressGradient(_typingToggle);
        AutomationProperties.SetAutomationId(_typingToggle, "eq-typing");
        _typingToggle.IsCheckedChanged += (_, _) => SetTyping(_typingToggle.IsChecked == true);

        var root = new Grid();
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.05, GridUnitType.Star) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var preview = BuildPreviewCard();
        Grid.SetColumnSpan(preview, 2);
        root.Children.Add(preview);

        // ⛔ 两列都进滚动区：浮层高度受窗口限制，装不下时只能滚 ——
        //    直接塞进 StackPanel 的话最后一行会被压成一条缝（实测未知数那排只剩 11px）。
        var left = WrapScroll(BuildNumberPad());
        Grid.SetRow(left, 1);
        left.Margin = new Thickness(0, 12, 8, 0);
        root.Children.Add(left);

        var right = WrapScroll(BuildOperatorPanel());
        Grid.SetRow(right, 1);
        Grid.SetColumn(right, 1);
        right.Margin = new Thickness(8, 12, 0, 0);
        root.Children.Add(right);

        View = root;
        Refresh();
    }

    // ── 预览（数学排版）+ 删除 / 清空 / 键盘输入 ──
    private Border BuildPreviewCard()
    {
        var content = new StackPanel { Spacing = 3 };
        content.Children.Add(_previewHost);
        content.Children.Add(_textBox);
        content.Children.Add(_error);

        var backspace = IconButton("\uE750", "删除最后一个字符", 44);
        backspace.Click += (_, _) => Backspace();

        var clear = new Button
        {
            Content = "清空",
            Width = 56,
            Height = 44,
            FontSize = 13,
            Padding = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        ToolTip.SetTip(clear, "清空输入");
        clear.Click += (_, _) =>
        {
            _raw.Clear();
            _textBox.Text = "";
            Refresh();
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        grid.Children.Add(content);

        Grid.SetColumn(_typingToggle, 1);
        _typingToggle.Margin = new Thickness(8, 0, 0, 0);
        grid.Children.Add(_typingToggle);
        Grid.SetColumn(backspace, 2);
        backspace.Margin = new Thickness(8, 0, 0, 0);
        grid.Children.Add(backspace);
        Grid.SetColumn(clear, 3);
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

    private static Button IconButton(string glyph, string tip, double size)
    {
        var button = new Button
        {
            Content = new FluentAvalonia.UI.Controls.FontIcon { Glyph = glyph, FontSize = 15 },
            Width = size,
            Height = size,
            Padding = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
        };

        ToolTip.SetTip(button, tip);
        ButtonFeel.ApplyPressGradient(button);
        return button;
    }

    private void SetTyping(bool typing)
    {
        _previewHost.IsVisible = !typing;
        _textBox.IsVisible = typing;

        if (!typing) return;

        _syncing = true;
        _textBox.Text = _raw.ToString();
        _syncing = false;
        _textBox.Focus();
    }

    private void Refresh()
    {
        RenderPreview();

        IsValid = EquationSolver.Validate(_raw.ToString(), out var error);

        if (!IsValid && _raw.Length > 0 && error is { Length: > 0 })
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

    private void RenderPreview()
    {
        _previewHost.Children.Clear();

        if (_raw.Length == 0)
        {
            _previewHost.Children.Add(new TextBlock
            {
                Text = "点下面的按键开始输入",
                FontSize = 14,
                Opacity = 0.4,
                VerticalAlignment = VerticalAlignment.Center,
            });
            return;
        }

        var rendered = MathText.Render(Pretty(_raw.ToString()), 26, _brush);
        rendered.VerticalAlignment = VerticalAlignment.Center;
        _previewHost.Children.Add(rendered);
    }

    /// <summary>
    /// 把内部写法换成黑板上的写法：<c>pi → π</c>、<c>sqrt( → √(</c>、<c>* → ×</c>。
    /// ⛔ 只影响显示，<see cref="Value"/> 交出去的仍是求解器认的写法。
    /// </summary>
    private static string Pretty(string raw)
    {
        var text = Regex.Replace(raw, @"\bpi\b", "π");
        text = Regex.Replace(text, @"\bsqrt\s*\(", "√(");
        return text.Replace("*", "×");
    }

    // ── 左：数字与变量 ──
    private Control BuildNumberPad()
    {
        var panel = new StackPanel();
        panel.Children.Add(SectionTitle("数字"));

        var digits = new Grid();
        for (var c = 0; c < 4; c++)
            digits.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (var r = 0; r < 3; r++)
            digits.RowDefinitions.Add(new RowDefinition { Height = new GridLength(42) });

        // 1~9 之后是 0 与小数点；删除键在预览卡上，不在这里重复
        var keys = new[] { "1", "2", "3", "4", "5", "6", "7", "8", "9", "0", "." };
        for (var i = 0; i < keys.Length; i++)
        {
            var key = keys[i];
            var button = KeyButton(key, key, 20);
            Grid.SetRow(button, i / 4);
            Grid.SetColumn(button, i % 4);
            button.Margin = new Thickness(i % 4 == 0 ? 0 : 6, i / 4 == 0 ? 0 : 6, 0, 0);
            digits.Children.Add(button);
        }

        var paren = KeyButton("( )", "左括号 + 右括号", 18, () =>
        {
            var text = _raw.ToString();
            var open = text.Count(c => c == '(') - text.Count(c => c == ')');
            Append(open > 0 ? ")" : "(");
        });
        Grid.SetRow(paren, 2);
        Grid.SetColumn(paren, 3);
        paren.Margin = new Thickness(6, 6, 0, 0);
        digits.Children.Add(paren);

        digits.Margin = new Thickness(0, 6, 0, 0);
        panel.Children.Add(digits);

        panel.Children.Add(SectionTitle("未知数"));
        var vars = new Grid();
        for (var c = 0; c < 4; c++)
            vars.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        vars.RowDefinitions.Add(new RowDefinition { Height = new GridLength(42) });

        var names = new[] { "x", "y", "z", "n" };
        for (var i = 0; i < names.Length; i++)
        {
            var name = names[i];

            // ⛔ 这里必须用字符串 Content：用自定义 TextBlock 当内容时（斜体变量）实测整块空白，
            //    走 Control 自带的 FontSize/FontStyle 才稳。
            var button = new Button
            {
                Content = name,
                FontSize = 20,
                FontStyle = FontStyle.Italic,
                Padding = new Thickness(0),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                CornerRadius = new CornerRadius(4),
            };

            ToolTip.SetTip(button, name == "x" ? "x（一元方程只能用 x）" : $"{name}（一次方程组可用）");
            AutomationProperties.SetAutomationId(button, "eq-key:" + name);
            button.Click += (sender, _) => Append((sender as Button)?.Content as string ?? name);

            Grid.SetColumn(button, i);
            button.Margin = new Thickness(i == 0 ? 0 : 6, 6, 0, 0);
            vars.Children.Add(button);
        }

        vars.Margin = new Thickness(0, 6, 0, 0);
        panel.Children.Add(vars);

        // ⛔ 常用式子放**左**列：右列（运算 + 函数）本来就快占满一屏，
        //    再往下塞一块就只能滚动才看得见（Nick 视角：藏起来等于没有）。
        // 只留三类最常用的：幂、指数、三角各一个。⛔ 铺满两行会让浮层在默认窗口下就装不下，
        // 用户得滚动才看得见 —— 藏起来的按钮等于没有。
        panel.Children.Add(SectionTitle("常用"));
        var common = Grid3x2(new[]
        {
            ("x²", "x^2"), ("2ˣ", "2^x"), ("sin(x)", "sin(x)"),
        });
        common.Margin = new Thickness(0, 6, 0, 0);
        panel.Children.Add(common);

        return panel;
    }

    // ── 右：运算 / 函数 / 常用 ──
    private Control BuildOperatorPanel()
    {
        var panel = new StackPanel();

        panel.Children.Add(SectionTitle("运算"));
        var ops = Grid4x2(new[]
        {
            ("+", "+"), ("−", "-"), ("×", "*"), ("÷", "/"),
            ("^", "^"), ("=", "="), ("(", "("), (")", ")"),
        });
        ops.Margin = new Thickness(0, 6, 0, 0);
        panel.Children.Add(ops);

        panel.Children.Add(SectionTitle("函数"));
        var fns = Grid3x3(new[]
        {
            ("sin", "sin("), ("cos", "cos("), ("tan", "tan("),
            ("ln", "ln("), ("lg", "lg("), ("log", "log("),
            ("√", "sqrt("), ("abs", "abs("), ("π", "pi"),
        });
        fns.Margin = new Thickness(0, 6, 0, 0);
        panel.Children.Add(fns);

        return panel;
    }

    /// <summary>竖向可滚（横向禁掉，否则滚轮会和外层抢）。</summary>
    private static ScrollViewer WrapScroll(Control content) => new()
    {
        Content = content,
        VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
        VerticalAlignment = VerticalAlignment.Stretch,
    };

    private Control Grid4x2((string Label, string Insert)[] items) => GridOf(items, 4, 44);

    private Control Grid3x3((string Label, string Insert)[] items) => GridOf(items, 3, 42);

    private Control Grid3x2((string Label, string Insert)[] items) => GridOf(items, 3, 40);

    /// <summary>等宽按键网格：列用星号均分，窗口多宽都摆得正。</summary>
    private Control GridOf((string Label, string Insert)[] items, int columns, double height)
    {
        var rows = (items.Length + columns - 1) / columns;
        var grid = new Grid();

        for (var c = 0; c < columns; c++)
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (var r = 0; r < rows; r++)
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(height) });

        for (var i = 0; i < items.Length; i++)
        {
            var (label, insert) = items[i];

            // ⛔ 按键上显示的是黑板写法（x²、√），插进去的必须是求解器认的写法（x^2、sqrt(。
            var button = KeyButton(label, $"插入 {label}", label.Length > 1 ? 14 : 17,
                                   () => Append(insert));

            Grid.SetRow(button, i / columns);
            Grid.SetColumn(button, i % columns);
            button.Margin = new Thickness(i % columns == 0 ? 0 : 6, i / columns == 0 ? 0 : 6, 0, 0);
            grid.Children.Add(button);
        }

        return grid;
    }

    private Button KeyButton(string label, string tip, double fontSize, Action? onClick = null)
    {
        var button = new Button
        {
            Content = new TextBlock
            {
                Text = label,
                FontSize = fontSize,
                HorizontalAlignment = HorizontalAlignment.Center,
            },
            Padding = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Stretch,
            CornerRadius = new CornerRadius(4),
            Tag = label,
        };

        // 给自动化/读屏一个稳定标识（按键上显示的可能是 ×、√ 这类记号，名字不好匹配）
        AutomationProperties.SetAutomationId(button, "eq-key:" + label);
        AutomationProperties.SetName(button, tip);

        ToolTip.SetTip(button, tip);
        ButtonFeel.ApplyPressGradient(button);

        button.Click += (_, _) =>
        {
            if (onClick is not null) onClick();
            else Append(label);
        };

        return button;
    }

    private static TextBlock SectionTitle(string text) => new()
    {
        Text = text,
        FontSize = 12,
        Opacity = 0.55,
        Margin = new Thickness(0, 2, 0, 0),
    };

    private void Append(string text)
    {
        _raw.Append(text);
        if (_typingToggle.IsChecked == true)
        {
            _syncing = true;
            _textBox.Text = _raw.ToString();
            _syncing = false;
        }

        Refresh();
    }

    private void Backspace()
    {
        if (_raw.Length == 0) return;

        _raw.Remove(_raw.Length - 1, 1);
        if (_typingToggle.IsChecked == true)
        {
            _syncing = true;
            _textBox.Text = _raw.ToString();
            _syncing = false;
        }

        Refresh();
    }
}
