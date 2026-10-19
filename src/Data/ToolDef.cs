using System;
using System.Collections.Generic;
using ClassSoftwareHub.Desktop.Pages.Tools;

namespace ClassSoftwareHub.Desktop.Data;

/// <summary>内置工具卡片定义（工具索引页用）。</summary>
public sealed class ToolDef
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Desc { get; init; } = "";
    /// <summary>SEGOEICONS 字形码。</summary>
    public string Glyph { get; init; } = "";
    /// <summary>点开要导航到的页面。</summary>
    public Type? Page { get; init; }

    /// <summary>无障碍 / 调试用：列表项名字就是工具名。</summary>
    public override string ToString() => Name;
}

/// <summary>一个系统镜像下载入口（对应内容包 text/mirror-sites.json）。</summary>
public sealed class MirrorSite
{
    public string Name { get; set; } = "";
    public string Desc { get; set; } = "";
    public string Url { get; set; } = "";
    public string Color { get; set; } = "";
    public string Icon { get; set; } = "";

    /// <summary>显示域名（去掉 https:// 与路径）。</summary>
    public string Host
    {
        get
        {
            try { return new Uri(Url).Host; }
            catch { return ""; }
        }
    }

    /// <summary>没有图标时兜底：名字首字。</summary>
    public string Badge => Name.Length > 0 ? Name.Substring(0, 1).ToUpperInvariant() : "?";

    /// <summary>站点有没有自带图标（内容包里的 icon 字段）。
    /// ⚠️ Avalonia 没有 <c>Visibility</c> 枚举，控件显隐用 <c>bool IsVisible</c>，
    ///    故这里返回值由 <c>Visibility</c> 改为 <c>bool</c>（界面绑 <c>IsVisible</c>）。</summary>
    public bool IconVisibility => Icon.Length > 0;

    public bool BadgeVisibility => Icon.Length == 0;

    /// <summary>首字方块底色（站点色的淡染）；站点没写 color 就用主题蓝。</summary>
    public Avalonia.Media.IBrush BadgeBackground => Tint(0.12);

    public Avalonia.Media.IBrush BadgeBorder => Tint(0.40);

    private Avalonia.Media.IBrush Tint(double alpha)
    {
        var hex = Color.TrimStart('#');
        if (hex.Length != 6)
        {
            return new Avalonia.Media.SolidColorBrush(
                Avalonia.Media.Color.FromArgb(0, 0, 0, 0));
        }
        try
        {
            var r = Convert.ToByte(hex.Substring(0, 2), 16);
            var g = Convert.ToByte(hex.Substring(2, 2), 16);
            var b = Convert.ToByte(hex.Substring(4, 2), 16);
            return new Avalonia.Media.SolidColorBrush(
                Avalonia.Media.Color.FromArgb((byte)(Math.Clamp(alpha, 0, 1) * 255), r, g, b));
        }
        catch
        {
            return new Avalonia.Media.SolidColorBrush(
                Avalonia.Media.Color.FromArgb(0, 0, 0, 0));
        }
    }
}

/// <summary>系统镜像下载页的整页文字 + 站点清单。</summary>
public sealed class MirrorInfo
{
    public string Title { get; set; } = "系统镜像下载";
    public string Subtitle { get; set; } = "";
    public string Disclaimer { get; set; } = "";
    public string Note { get; set; } = "";
    public List<MirrorSite> Sites { get; } = new();
}

/// <summary>
/// 内置工具清单 —— 导航栏「内置工具」分组的子项、工具索引页的卡片，共用这一份。
/// <para>⚠️ 往这儿加工具：① 下面 <see cref="All"/> 加一条（Id/Name/Desc/Glyph/Page）；
/// ② 导航子项是 <c>ShellPage</c> 按 <see cref="ToolDef.Id"/> 动态生成的，<c>NavigateTagCore</c>
///    也是按 Id 反查 <see cref="All"/>，所以**不用再改导航**；③ 若首页卡片 / 浮窗有直达入口，另加。</para>
/// </summary>
public static class ToolCatalog
{
    public static readonly List<ToolDef> All = new()
    {
        new ToolDef
        {
            Id = "image-color", Name = "图片取色",
            Desc = "导入图片后自动提取一组柔和的配色（种子色 + 7 档明暗变体），单击色块即可复制。",
            Glyph = "\uE790", Page = typeof(ImageColorToolPage)
        },
        new ToolDef
        {
            Id = "pick-number", Name = "随机抽号",
            Desc = "按学号范围或班级名单随机抽取，可一次抽多个、抽过不重复，也能一键随机分组。",
            Glyph = "\uE716", Page = typeof(PickNumberToolPage)
        },
        new ToolDef
        {
            Id = "timer", Name = "课堂计时器",
            Desc = "倒计时 / 秒表，大字号便于投影，到达设定时间响铃。",
            Glyph = "\uE916", Page = typeof(TimerToolPage)
        },
        new ToolDef
        {
            Id = "clock", Name = "全屏时钟",
            Desc = "将屏幕变为大型时钟：可设置背景图与蒙版，全屏显示查看时间。",
            Glyph = "\uE740", Page = typeof(ClockToolPage)
        },
        new ToolDef
        {
            Id = "message", Name = "桌面留言",
            Desc = "把要提醒的话显示出来：可调字号与颜色，既能做成置顶悬浮窗钉在桌面上，也能全屏铺满屏幕展示。",
            Glyph = "\uE8BD", Page = typeof(MessageToolPage)
        },
        new ToolDef
        {
            Id = "announce", Name = "教室喊话",
            Desc = "接收远端喊话（全屏大字 + 可选朗读），也能从本机给同配对码下的教室机群发一句话。",
            Glyph = "\uE720", Page = typeof(AnnouncePage)
        },
        new ToolDef
        {
            Id = "encoding", Name = "编码 / 哈希工具",
            Desc = "校验文件：拖入即得 MD5 / SHA-1 / SHA-256 / SHA-512，可粘贴官方值自动核对；也可做 Base64、URL 编解码。",
            Glyph = "\uE943", Page = typeof(EncodingToolPage)
        },
        new ToolDef
        {
            Id = "autoshutdown", Name = "自动关机",
            Desc = "按星期设定关机时间点，到达时间后自动关机；可直接关机（可设延迟），也可用系统原生的滑动关机。",
            Glyph = "\uE787", Page = typeof(ClassSoftwareHub.Desktop.Pages.AutoShutdownPage)
        },
        // ⚠️ 上游 2026-10-09 把「系统镜像下载」整体下线（工具定义、页面、侧边栏模块、内容解析）。
        //    本移植版按用户决定**保留**这个功能（页面 Pages/Tools/MirrorToolPage 与侧边栏模块都还在），
        //    所以这条定义必须留着 —— 2026-10-19 修：之前跟着上游删了定义，但导航项
        //    （ShellPage.axaml 里 Tag="mirror-download"）和侧边栏模块还在，
        //    点导航项时 ToolCatalog 反查不到 → 落进 default → 打开成首页（用户报的 bug）。
        new ToolDef
        {
            Id = "mirror-download", Name = "系统镜像下载",
            Desc = "从多个镜像站下载 Windows / Office 等系统镜像，可复制校验值核对；镜像地址来自站点内容包。",
            // E896 = 云下载
            Glyph = "\uE896", Page = typeof(ClassSoftwareHub.Desktop.Pages.Tools.MirrorToolPage)
        },
        // ⚠️「自动关机」2026-10-12 从「实验性功能」转正搬来这里（Nick 定：这玩意可以退出实验性了）——
        //    导航子项与工具索引页卡片都会自动跟着出现，不用另改导航（见本类注释）。
        new ToolDef
        {
            Id = "function-plot", Name = "函数图像",
            Desc = "输入含 x 的表达式即可绘制函数曲线，可同时绘制多条以便比较；支持滚轮缩放与拖动查看。",
            Glyph = "\uE9D2", Page = typeof(ClassSoftwareHub.Desktop.Pages.FunctionPlotPage)
        },
        new ToolDef
        {
            Id = "disk-map", Name = "磁盘空间",
            Desc = "按占用面积画出目录结构，一眼看出空间被谁吃掉了；单击方块可逐层下钻。",
            // E7F1 = 硬盘
            Glyph = "\uE7F1", Page = typeof(ClassSoftwareHub.Desktop.Pages.Tools.DiskMapPage)
        },
        new ToolDef
        {
            Id = "equation", Name = "方程求解",
            Desc = "一次方程组给出精确分数解；一元方程按方程本身求根，支持平方、三角函数、对数与指数。",
            // E8EF = 计算器
            Glyph = "\uE8EF", Page = typeof(ClassSoftwareHub.Desktop.Pages.Tools.EquationPage)
        },
        new ToolDef
        {
            Id = "chemistry", Name = "化学方程式配平",
            Desc = "输入 A + B = C 形式的方程式即可算出最小正整数系数，并逐种元素核对左右原子数。支持括号与结晶水。",
            // E80A = 九宫格（像元素周期表）。⛔ 只用 E7/E8/E9 老码位，ED 段在 Win10 是空框
            Glyph = "\uE80A", Page = typeof(ClassSoftwareHub.Desktop.Pages.Tools.ChemistryPage)
        },
    };
}
