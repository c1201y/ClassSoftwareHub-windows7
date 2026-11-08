using Avalonia;
using Avalonia.Controls;
using ClassSoftwareHub.Desktop.Data;

namespace ClassSoftwareHub.Desktop.Controls;

/// <summary>
/// 页面标题旁的「实验性功能」标记。
///
/// <para>
/// 显示与否**不写死在页面里**：页面只声明自己是谁（<see cref="FeatureId"/>），
/// 控件去 <see cref="ExperimentalFeatureCatalog"/> 里查 —— 查得到就显示，查不到就整块收起。
/// 这样功能转正时**只要从清单里删掉那一条**，标志自动消失，不会留下"已经转正了还挂着实验性"
/// （2026-11-03 之前就是这么乱的：四个页面各抄一份 Border，后来新加的五个页面全忘了加）。
/// </para>
///
/// <para>
/// ⛔ 加新的实验性页面时：标题横向 StackPanel 里放一个
/// <c>&lt;controls:ExperimentalBadge FeatureId="导航 tag" /&gt;</c>，FeatureId 必须和
/// <see cref="ExperimentalFeatureCatalog"/> 里的 Tag 一字不差。
/// </para>
///
/// ⚠️ 移植说明（WinUI → Avalonia）：
///   · <c>UserControl</c> 同名（Avalonia.Controls）；<c>DependencyProperty.Register</c> →
///     <c>AvaloniaProperty.Register</c>，<c>PropertyMetadata</c> 的回调改成
///     <c>AddClassHandler</c>（Avalonia 没有「带变更回调的 PropertyMetadata」这种写法）。
///   · <c>Visibility.Visible/Collapsed</c> → <c>IsVisible</c>（Avalonia 用 <c>bool</c>）。
///   · <c>Loaded</c> 事件两边同名，但 Avalonia 里它每次进出可视树都会触发 —— 先判空再赋值，幂等。
/// </summary>
public sealed partial class ExperimentalBadge : UserControl
{
    public static readonly StyledProperty<string> FeatureIdProperty =
        AvaloniaProperty.Register<ExperimentalBadge, string>(nameof(FeatureId), defaultValue: "");

    static ExperimentalBadge()
    {
        // XAML 里设属性时控件还没进视觉树，但改 IsVisible 本身是安全的
        FeatureIdProperty.Changed.AddClassHandler<ExperimentalBadge>((badge, _) => badge.Update());
    }

    /// <summary>这一页在 <see cref="ExperimentalFeatureCatalog"/> 里的导航 tag。</summary>
    public string FeatureId
    {
        get => GetValue(FeatureIdProperty);
        set => SetValue(FeatureIdProperty, value);
    }

    public ExperimentalBadge()
    {
        InitializeComponent();
        Loaded += (_, _) => Update();
    }

    private void Update()
    {
        IsVisible = ExperimentalFeatureCatalog.Find(FeatureId) is not null;
    }
}
