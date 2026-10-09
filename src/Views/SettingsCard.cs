using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace ClassSoftwareHub.Desktop.Views;

/// <summary>
/// 设置项卡片。**对应 WinUI CommunityToolkit 的 <c>SettingsCard</c>** —— 原版设置页 / 虚拟键盘页大量使用它。
///
/// ⛔ 为什么必须自建：FluentAvalonia **只有 <c>SettingsExpander</c>，没有 <c>SettingsCard</c>**
///    （见 PORTING.md 4.2）。全工程共用这一个实现，**不要各页各写一份**。
///
/// 版式（照 WinUI Toolkit 的样子）：
///   ┌───────────────────────────────────────────────────────────────┐
///   │ [图标]   Header（BodyStrong）                    右侧内容区     │
///   │          Description（Caption + 次要色）          （开关/下拉…） │
///   └───────────────────────────────────────────────────────────────┘
///
/// ⚠️ 偏离原版之处（见交付总结）：
///   · 原版 `HeaderIcon` 是 WinUI 的 `IconElement`；Avalonia 里图标控件就是普通控件（<c>FontIcon</c>），
///     所以这里的 <see cref="HeaderIcon"/> 是 <see cref="object"/>，**XAML 写法与原版一模一样**：
///     <c>&lt;views:SettingsCard.HeaderIcon&gt;&lt;FontIcon Glyph="…"/&gt;&lt;/views:SettingsCard.HeaderIcon&gt;</c>。
///   · 另给一个 <see cref="IconSource"/>（同样是 object）作别名，方便别处按 PORTING.md 的措辞写；
///     两者只渲染其一（<see cref="HeaderIcon"/> 优先）。
///   · Header / Description 都是 object：Description 在原版里常被塞进一个 TextBlock/StackPanel
///     （动态文案、带按钮的说明），必须支持任意内容，不能只收 string。
/// </summary>
public class SettingsCard : ContentControl
{
    /// <summary>标题（通常是字符串；也允许放别的控件）。</summary>
    public static readonly StyledProperty<object?> HeaderProperty =
        AvaloniaProperty.Register<SettingsCard, object?>(nameof(Header));

    /// <summary>说明文字（string 或任意控件，原版里两种都有）。</summary>
    public static readonly StyledProperty<object?> DescriptionProperty =
        AvaloniaProperty.Register<SettingsCard, object?>(nameof(Description));

    /// <summary>左侧图标（一般是 <c>FluentAvalonia.UI.Controls.FontIcon</c>）。</summary>
    public static readonly StyledProperty<object?> HeaderIconProperty =
        AvaloniaProperty.Register<SettingsCard, object?>(nameof(HeaderIcon));

    /// <summary>左侧图标的别名（<see cref="HeaderIcon"/> 优先；给"按 IconSource 写"的调用方用）。</summary>
    public static readonly StyledProperty<object?> IconSourceProperty =
        AvaloniaProperty.Register<SettingsCard, object?>(nameof(IconSource));

    public object? Header
    {
        get => GetValue(HeaderProperty);
        set => SetValue(HeaderProperty, value);
    }

    public object? Description
    {
        get => GetValue(DescriptionProperty);
        set => SetValue(DescriptionProperty, value);
    }

    public object? HeaderIcon
    {
        get => GetValue(HeaderIconProperty);
        set => SetValue(HeaderIconProperty, value);
    }

    public object? IconSource
    {
        get => GetValue(IconSourceProperty);
        set => SetValue(IconSourceProperty, value);
    }

    // ════════════════════════════════════════════════════════════════
    // 整卡可点（对应 WinUI Toolkit SettingsCard 的 IsClickEnabled / Click / IsActionIconVisible）
    //   原版「设置 → 诊断 → 日志查看」「设置 → 回声洞」都是整卡可点（悬停底色铺满整行），
    //   ToolTipService.ToolTip 也照原样接（XAML 侧换成 ToolTip.Tip）。
    // ════════════════════════════════════════════════════════════════

    /// <summary>整卡是否可点。为真时右侧显示一颗 Chevron，并接管指针点击。</summary>
    public static readonly StyledProperty<bool> IsClickEnabledProperty =
        AvaloniaProperty.Register<SettingsCard, bool>(nameof(IsClickEnabled));

    /// <summary>右侧 Chevron 是否显示（默认 true；仅当 <see cref="IsClickEnabled"/> 为真时才有意义）。</summary>
    public static readonly StyledProperty<bool> IsActionIconVisibleProperty =
        AvaloniaProperty.Register<SettingsCard, bool>(nameof(IsActionIconVisible), defaultValue: true);

    /// <summary>
    /// 模板里那颗 Chevron 的实际可见性（= IsClickEnabled &amp;&amp; IsActionIconVisible）。
    ///
    /// ⛔⛔ <b>字段必须是 public</b>：<c>SettingsCard.axaml</c> 的模板用
    ///    <c>IsVisible="{TemplateBinding ShowActionIcon}"</c> 引用它，而 Avalonia 的**编译 XAML**
    ///    会把 TemplateBinding 直接编译成对 <c>ShowActionIconProperty</c> **字段**的 IL 访问。
    ///    写成 private 时编译期不报错，**运行期**才抛 <c>FieldAccessException</c>：
    ///     <c>Attempt by method 'CompiledAvaloniaXaml...Build_2' to access field
    ///     'SettingsCard.ShowActionIconProperty' failed</c> —— 而且它发生在模板构建时，
    ///     <c>SettingsCard</c> 出现在任何页面都会让**整个进程**被未处理异常带走
    ///     （2026-10-06 实测：进「实验性功能 → 虚拟键盘」直接闪退，crash.log 还是空的）。
    ///    这份程序集里**只有 DirectProperty 这一个**曾写成 private，其余 StyledProperty 都是 public。
    /// </summary>
    public static readonly DirectProperty<SettingsCard, bool> ShowActionIconProperty =
        AvaloniaProperty.RegisterDirect<SettingsCard, bool>(
            nameof(ShowActionIcon), o => o.ShowActionIcon);

    /// <summary>整卡被点击（仅当 <see cref="IsClickEnabled"/> 为真时触发）。</summary>
    public event EventHandler<RoutedEventArgs>? Click;

    static SettingsCard()
    {
        IsClickEnabledProperty.Changed.AddClassHandler<SettingsCard>((card, _) => card.UpdateClickable());
        IsActionIconVisibleProperty.Changed.AddClassHandler<SettingsCard>((card, _) => card.UpdateClickable());
    }

    public bool IsClickEnabled
    {
        get => GetValue(IsClickEnabledProperty);
        set => SetValue(IsClickEnabledProperty, value);
    }

    public bool IsActionIconVisible
    {
        get => GetValue(IsActionIconVisibleProperty);
        set => SetValue(IsActionIconVisibleProperty, value);
    }

    private bool _showActionIcon;

    /// <summary>模板里那颗 Chevron 的实际可见性（= IsClickEnabled &amp;&amp; IsActionIconVisible）。</summary>
    public bool ShowActionIcon
    {
        get => _showActionIcon;
        private set => SetAndRaise(ShowActionIconProperty, ref _showActionIcon, value);
    }

    private void UpdateClickable()
    {
        ShowActionIcon = IsClickEnabled && IsActionIconVisible;
        Cursor = IsClickEnabled ? new Cursor(StandardCursorType.Hand) : Cursor.Default;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!IsClickEnabled) return;
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        Click?.Invoke(this, new RoutedEventArgs());
        e.Handled = true;
    }
}
