using Avalonia.Controls;
using Avalonia.Controls.Templates;

namespace ClassSoftwareHub.Desktop.Core;

/// <summary>
/// 快捷入口的模板：一颗入口卡（图标 + 名称）。
///
/// ⚠️ 上游 1.2.0 把「普通 / 突出」两套模板合并成了一套（Accent 去掉，Nick 2026-10-12 嫌主题色底
///    越看越丑），并把模板内联进了首页 / 设置页；本移植版保留这个 <see cref="IDataTemplate"/>
///    作为两页共用的模板容器（Avalonia 里 IDataTemplate 就是"模板选择器"的对应物），
///    但语义与上游一致：**只有一套外观**，图标优先用 <see cref="QuickLink.IconSource"/>（SVG），
///    没有 SVG 的项退回 <see cref="QuickLink.Glyph"/> 的 FontIcon。
/// </summary>
public sealed class QuickLinkSelector : IDataTemplate
{
    public IDataTemplate? Normal { get; set; }

    /// <summary>兼容旧 XAML：上游已无"突出"模板，这里保留属性但不使用。</summary>
    public IDataTemplate? Accent { get; set; }

    public bool Match(object? data) => data is QuickLink;

    public Control? Build(object? param) => Normal?.Build(param);
}
