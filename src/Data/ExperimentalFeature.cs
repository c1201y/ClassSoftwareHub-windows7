using System;
using System.Collections.Generic;

namespace ClassSoftwareHub.Desktop.Data;

/// <summary>
/// 一条「实验性功能」的定义（实验性功能总览页用）。
///
/// 为什么是 <see cref="Tag"/> 而不是 <c>Type Page</c>（对比 <see cref="ToolDef"/>）：
///    实验性功能挂在左侧导航的「实验性功能」分组下，点卡片必须走
///    <c>ShellPage.NavigateTo(tag)</c> 才能把左侧高亮一起带走；直接 <c>Frame.Navigate</c> 会跳页但高亮留在原地。
/// </summary>
public sealed class ExperimentalFeature
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";

    /// <summary>一句话说明（卡片第二行，别写长）。</summary>
    public string Desc { get; init; } = "";

    /// <summary>SEGOEICONS 字形码。</summary>
    public string Glyph { get; init; } = "";

    /// <summary>导航 tag（<c>ShellPage.NavigateTagCore</c> 里要有对应的 case）。</summary>
    public string Tag { get; init; } = "";

    /// <summary>无障碍 / 调试用。</summary>
    public override string ToString() => Name;
}

/// <summary>
/// 「实验性功能」这一组的结算清单 —— 页面标题旁那枚「实验性功能」标记靠它判定显示与否。
///
/// <para>
/// 上游 1.10 把这份清单从 <c>ExperimentalPage</c> 里提到 <c>Data/ExperimentalFeatureCatalog.cs</c>，
/// 让「导航子项 / 总览页卡片 / 页面标题旁的标记」三处同源。本移植版把清单放在本文件
/// （文件名不同：目标是 <c>Data/ExperimentalFeature.cs</c>，见移植任务说明）。
/// </para>
///
/// <para>
/// ⚠️ 本移植版的「实验性功能」导航子项仍然是 <c>ShellPage.axaml</c> 里 XAML 硬写的
/// （FA 2.4.1 的选中竖线只认 XAML 子项，原因见那里的长注释），所以加一项要改三处：
/// ① 这里；② <c>ShellPage.axaml</c> 的子项；③ <c>NavigateTagCore</c> 的 case。
/// </para>
///
/// <para>
/// ⛔ 判断标准：**成熟度不够**才放这儿，不是"系统类"就放这儿。
/// 系统工具（磁盘、关机、校验、取色）在 <see cref="ToolCatalog"/> 的 <c>System</c> 组里，
/// 那一组已经是正式功能 —— 两边不要互相搬来搬去（转正时就该从这张表删掉）。
/// </para>
/// </summary>
public static class ExperimentalFeatureCatalog
{
    /// <summary>
    /// 清单。顺序与 <c>ExperimentalPage</c> 的卡片一致（新的追加在对应位置）。
    /// ⚠️ 本移植版比上游多两项：<c>virtualkeyboard</c>（虚拟键盘）与 <c>autoshutdown</c>（自动关机）——
    /// 上游 dv1.1.3 把虚拟键盘换成了自动关机，本移植版按用户 2026-10-11 的决定两个都留。
    /// </summary>
    public static readonly IReadOnlyList<ExperimentalFeature> All = new List<ExperimentalFeature>
    {
        // 本机核实：拿本机已装软件跟清单对一遍。
        // ⚠️ 为啥塞实验性：结果**明显不准**（只读「程序和功能」注册表，匹配靠名称模糊比对，
        //    Store 版 / 免安装版 / 改了名的全认不出来）。放一级导航容易被当成装机验收依据。
        new ExperimentalFeature
        {
            Id = "machinecheck", Name = "本机核实",
            Desc = "对照本机的已安装程序记录，看清单里的软件装没装",
            Glyph = "\uE7F4", Tag = "machinecheck"
        },

        // 程序专杀：用户自己指定进程名，每个程序单独配结束时间点。
        // ⚠️ 与白板专杀各存各的配置、各跑各的定时器（Nick 明确要求白板独立）。
        new ExperimentalFeature
        {
            Id = "procguard", Name = "程序专杀",
            Desc = "为指定程序设定结束时间点，到点结束其后驻留进程",
            Glyph = "\uE7E8", Tag = "procguard"
        },

        // 虚拟键盘（本移植版保留，上游已在 dv1.1.3 下线）。
        new ExperimentalFeature
        {
            Id = "virtualkeyboard", Name = "虚拟键盘",
            Desc = "自绘触屏键盘：手指点在输入框上自动弹出，外观全部使用系统控件与主题色",
            Glyph = "\uE765", Tag = "virtualkeyboard"
        },

        // 自动关机（本移植版与虚拟键盘并存，上游是替换）。
        new ExperimentalFeature
        {
            Id = "autoshutdown", Name = "自动关机",
            Desc = "按星期设定关机时间点，到点自动关机（可直接关，也可用系统的滑动关机）",
            Glyph = "\uE787", Tag = "autoshutdown"
        },

        // 应用卸载（2026-10-12 加）。⚠️ 为啥塞实验性：卸载不可逆，而扫描口径有已知盲区。
        // ⚠️ tag 在本移植版是 uninstall（上游 1.10 叫 uninstaller）—— 与 ShellPage / 命令行参数一字不差。
        new ExperimentalFeature
        {
            Id = "uninstall", Name = "应用卸载",
            Desc = "按分类列出本机已安装的软件，调用其自带卸载程序；卸载后可扫描并清理残留（进隔离区，可还原）",
            Glyph = "\uE74D", Tag = "uninstall"
        },

        // 开机启动项（2026-10-16 加）。⚠️ 为啥塞实验性：只覆盖注册表 Run 与启动文件夹，
        //    服务 / 计划任务 / 组策略启动脚本都不在范围内，容易被当成"全部自启项"。
        new ExperimentalFeature
        {
            Id = "startup", Name = "开机启动项",
            Desc = "列出开机自启项（注册表 Run + 启动文件夹），逐个启用 / 禁用；启用状态与任务管理器同源",
            Glyph = "\uE945", Tag = "startup"
        },

    };

    /// <summary>
    /// 按导航 tag 找一条定义；找不到返回 null。
    ///
    /// <para>
    /// 页面标题旁那枚「实验性功能」标记（<c>Controls/ExperimentalBadge</c>）就靠它决定显示与否：
    /// **功能转正时只要从这里删掉这一条，标志会自动消失**，不用回头去改页面
    /// （2026-11-03 Nick 定的规矩，见 MustRead）。
    /// </para>
    /// </summary>
    public static ExperimentalFeature? Find(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;

        foreach (var def in All)
        {
            if (string.Equals(def.Tag, tag, StringComparison.OrdinalIgnoreCase)) return def;
        }

        return null;
    }
}
