using ClassSoftwareHub.Desktop.Services;

namespace ClassSoftwareHub.Desktop.Controls;

/// <summary>
/// 分类在**界面上**长什么样（显示名 / 占位字形 / 下拉里的顺序）。
///
/// ⚠️ 只有这一份 —— 分类下拉、行首的占位图标都用它，⛔ 不要在页面里再抄一遍，两处一改就对不上。
/// （从 <c>CategoryChipBar</c> 拆出：分类筛选改为"一个下拉单选"后，
///   这两个查表函数两边都要用，故单独留在这里。）
/// </summary>
public static class AppCategoryLook
{
    /// <summary>下拉里的显示顺序（也是界面上分类的固定次序）。</summary>
    public static readonly AppCategory[] Order =
    {
        AppCategory.Desktop,
        AppCategory.Runtime,
        AppCategory.Store,
        AppCategory.System,
    };

    /// <summary>分类的界面用名。</summary>
    public static string LabelOf(AppCategory category) => category switch
    {
        AppCategory.Runtime => "运行库",
        AppCategory.Store => "微软应用商店",
        AppCategory.System => "系统组件",
        _ => "桌面应用",
    };

    /// <summary>
    /// 分类的占位字形（掏不到软件图标时显示的那个）。
    /// ⚠️ 全是项目里已经在用的老码位（E7/E8/E9 段）；⛔ 别换到 ED 段 —— 那是新版图标字体，Win10 上是空框。
    /// ⚠️ 已逐颗渲染成图确认过字形，不是照着码位表抄的。
    /// </summary>
    public static string GlyphOf(AppCategory category) => category switch
    {
        AppCategory.Runtime => "\uE8F1",   // 书架（Library）—— "库"这件事一眼就懂
        AppCategory.Store => "\uE719",     // 购物袋 —— Microsoft Store 自己的图标语义
        AppCategory.System => "\uE713",    // 齿轮
        _ => "\uE71D",                     // 清单（AllApps）
    };
}
