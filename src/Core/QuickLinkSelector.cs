using Avalonia.Controls;
using Avalonia.Controls.Templates;

namespace ClassSoftwareHub.Desktop.Core;

/// <summary>
/// 快捷入口的两套模板：普通一颗 / 要突出的那颗（「赞助作者」）。
///
/// ⚠️ 原版是 WinUI 的 <c>DataTemplateSelector</c>；**Avalonia 没有这个类型**，
///    对应机制是 <see cref="IDataTemplate"/>（<c>Match</c> 判定 + <c>Build</c> 出控件）。
///    这里实现 <see cref="IDataTemplate"/> 保留完全相同的分流语义：
///    <see cref="QuickLink.Accent"/> 为真走 <see cref="Accent"/>，否则走 <see cref="Normal"/>。
///
/// ⚠️ 为什么不在数据里塞 Brush：**颜色必须在 XAML 里用 `{DynamicResource ...}`** ——
/// 那玩意儿认的是"元素所在那棵树"的主题，会自己跟着深浅色变；
/// 而代码里 `Application.Current.Resources[...]` 查的是应用级（跟系统走）那一套，
/// 浅色界面 + 深色系统时就会取到白字（被 Nick 抓到过）。
/// </summary>
public sealed class QuickLinkSelector : IDataTemplate
{
    public IDataTemplate? Normal { get; set; }
    public IDataTemplate? Accent { get; set; }

    public bool Match(object? data) => data is QuickLink;

    public Control? Build(object? param)
    {
        var template = param is QuickLink link && link.Accent ? Accent : Normal;
        return template?.Build(param);
    }
}
