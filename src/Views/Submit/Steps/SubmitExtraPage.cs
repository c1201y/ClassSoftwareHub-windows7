using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace ClassSoftwareHub.Desktop.Views.Submit.Steps;

/// <summary>
/// 第 4 步：补充信息（自上游 dv1.2.2 移植，2026-11-14）。
/// 整页选填，不实现 <see cref="ISubmitStep"/> —— 没有可拦住前进的条件。
///
/// <para>
/// ⚠️ 移植说明：上游是 XAML + <c>x:Bind Draft.Website</c> 双向绑定；
/// Avalonia 这边改成**代码建控件 + 绑定到 <see cref="SubmitDraft"/>**（实现 INotifyPropertyChanged），
/// 双向绑定语义与上游一致：在页面上改，草稿立刻跟着变，翻页再回来时值还在。
/// </para>
/// </summary>
public sealed partial class SubmitExtraPage : UserControl
{
    private readonly SubmitDraft _draft;

    /// <summary>输入框的延迟绑定收集器（见 <see cref="DeferredBindings"/>）。</summary>
    private readonly DeferredBindings _bindings = new();

    public SubmitExtraPage()
    {
        _draft = SubmitSession.Current?.Draft ?? SubmitDraft.CreateEmpty();

        AttachedToVisualTree += (_, _) => _bindings.Flush();
        BuildUi();
    }

    protected override void OnDetachedFromVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _bindings.Clear();
    }

    private void BuildUi()
    {
        var panel = new StackPanel { Width = 600, Spacing = 14, HorizontalAlignment = HorizontalAlignment.Center };

        panel.Children.Add(Header("补充信息", "以下内容全部选填，可直接进入下一步。"));

        // 官网 / GitHub
        panel.Children.Add(TwoColumns(
            Field("官网地址（选填）", "https://…", _draft, nameof(SubmitDraft.Website), 0, bindings: _bindings),
            Field("GitHub 仓库（选填）", "https://github.com/…", _draft, nameof(SubmitDraft.Github), 1, bindings: _bindings)));

        // 开发者 / 开源协议（自上游 dv1.2.1 移植的两项）
        panel.Children.Add(TwoColumns(
            Field("开发者（选填）", "个人或团队名称", _draft, nameof(SubmitDraft.Author), 0, bindings: _bindings),
            Field("开源协议（选填）", "如 MIT、GPL-3.0", _draft, nameof(SubmitDraft.License), 1, bindings: _bindings)));

        panel.Children.Add(Field("提示条（选填）",
            "显示在下载区上方的提示，适合更新频繁、直链易失效的软件",
            _draft, nameof(SubmitDraft.Notice), -1, multiline: true, bindings: _bindings));

        panel.Children.Add(Field("应用商店地址（选填）",
            "填写后会额外显示一张「商店下载」卡片",
            _draft, nameof(SubmitDraft.Store), -1, bindings: _bindings));

        var sort = Field("排序值（选填）", "数字越小越靠前，留空排最后",
            _draft, nameof(SubmitDraft.Sort), -1, bindings: _bindings);
        sort.Width = 200;
        sort.HorizontalAlignment = HorizontalAlignment.Left;
        panel.Children.Add(sort);

        Content = new ScrollViewer
        {
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            Content = new Grid
            {
                Margin = new Thickness(24, 28, 24, 24),
                Children = { panel },
            },
        };
    }

    // ── 小工具（本向导内几个页面共用） ──────────────────────────────────

    /// <summary>标题 + 一行说明。</summary>
    internal static Control Header(string title, string? subtitle)
    {
        var stack = new StackPanel { Spacing = 4, Margin = new Thickness(0, 0, 0, 2) };
        stack.Children.Add(new TextBlock { Text = title, FontSize = 24, FontWeight = Avalonia.Media.FontWeight.SemiBold });

        if (!string.IsNullOrEmpty(subtitle))
            stack.Children.Add(new TextBlock
            {
                Text = subtitle,
                FontSize = 13,
                Opacity = 0.65,
                TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            });

        return stack;
    }

    /// <summary>
    /// 一个「标签 + 输入框」字段，双向绑定到草稿的同名属性。
    /// <paramref name="column"/> 为 -1 时表示占满一行。
    /// </summary>
    internal static StackPanel Field(
        string label, string? placeholder, SubmitDraft draft, string property, int column,
        bool multiline = false, DeferredBindings? bindings = null)
    {
        var stack = new StackPanel { Spacing = 4 };
        stack.Children.Add(new TextBlock { Text = label, FontSize = 12.5, Opacity = 0.75 });

        var box = new TextBox
        {
            Watermark = placeholder,
            AcceptsReturn = multiline,
            TextWrapping = multiline ? Avalonia.Media.TextWrapping.Wrap : Avalonia.Media.TextWrapping.NoWrap,
            MinHeight = multiline ? 72 : 0,
        };

        // 双向：草稿 → 控件（首次铺值），控件 → 草稿。
        // ⚠️ 订阅要等进树之后才生效，见 DeferredBindings 的说明。
        box.Text = draft.GetType().GetProperty(property)?.GetValue(draft) as string ?? "";

        void Apply(string? text)
        {
            try { draft.GetType().GetProperty(property)?.SetValue(draft, text ?? ""); }
            catch { /* 字段名写错不该带走界面 */ }
        }

        if (bindings is not null) TextBoxBinding.Bind(bindings, box, Apply);
        else box.TextChanged += (_, _) => Apply(box.Text);

        stack.Children.Add(box);

        if (column >= 0)
        {
            Grid.SetColumn(stack, column);
            stack.Margin = new Thickness(column == 0 ? 0 : 0, 0, 0, 0);
        }

        return stack;
    }

    /// <summary>两列布局（Avalonia 的 Grid 没有 ColumnSpacing，用右列外边距代替）。</summary>
    internal static Grid TwoColumns(Control left, Control right)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition());

        Grid.SetColumn(left, 0);
        Grid.SetColumn(right, 1);
        right.Margin = new Thickness(12, 0, 0, 0);

        grid.Children.Add(left);
        grid.Children.Add(right);
        return grid;
    }
}
