using Avalonia;
using Avalonia.Controls;

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
}
