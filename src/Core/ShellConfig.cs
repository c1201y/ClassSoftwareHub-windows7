namespace ClassSoftwareHub.Desktop.Core;

/// <summary>外壳常量集中处（改这里就行）。</summary>
public static class ShellConfig
{
    /// <summary>
    /// 站点地址 —— ⚠️ 当前是「测试版」：直接写死本机 dev server。
    /// 正式发版时**只改这一行**，换成 "https://classsoftwarehub.us.ci" 就行（不要再引入开关）。
    /// </summary>
    public const string SiteUrl = "https://classsoftwarehub.us.ci";

    public const string AppName = "ClassSoftwareHub";
    public const string WindowTitle = "ClassSoftwareHub";

    /// <summary>
    /// 原生外壳版本（跟站点版本无关）。
    ///
    /// 命名规则（Nick 指定）：<c>dv</c> + <c>主.功能.补丁</c> + <c>-insider架构.迭代</c>
    /// <list type="bullet">
    ///   <item>第一位「架构」——只有整个应用的架构/技术路线发生重大变动才会动；</item>
    ///   <item>第二位「功能」——每叠加一块新功能涨一次（例如「下载后台化」落地时会被它抬一位）；</item>
    ///   <item>第三位「补丁」——小功能推送 / 小更新 / 小 bug 修复；</item>
    ///   <item><c>insider</c> 第一位——预览线自己的架构/思路基线，性质同主版本第一位；</item>
    ///   <item><c>insider</c> 第二位——这条预览线上的具体更改次数。</item>
    /// </list>
    /// 注：<c>dv</c> 前缀由 <see cref="VersionPrefix"/> 单独拼，**不要**写进这个字符串里。
    ///
    /// 递增流程：
    /// 做出一个能用的版本 → 发 <c>1.0.0-insider1.0</c>
    /// → 用户反馈还有问题 → 继续改成 <c>1.0.0-insider1.1</c>
    /// → 一直改到没问题 → **整个 <c>-insider</c> 后缀删掉** → 上线正式版 <c>1.0.0</c>。
    ///
    /// ⚠️ 基数不随便抬（否则旧包会被强制顶掉）。
    /// </summary>
    // 2026-10-04：1.1.0-insider1.2 → **首个正式版 1.0.0**（按上面那套流程删掉 -insider 后缀；
    // 同时把基数从「预览线跑到的 1.1.0」落回干净的首发 1.0.0）。
    // 2026-10-06：**1.0.0 → 1.1.0**（对齐 WinUI 桌面版 1.1 正式版，把 1.1 相对 1.0 新增的功能
    // 全数补进本版：日志查看 / 回声洞 / 联系方式本地加密 / 全屏秒表与全屏计时 / 到点铃声 /
    // 安装包管理等；基数按"叠加一块新功能涨一位"的规则抬到功能位）。
    // 2026-10-10：对齐 WinUI 桌面版 dv1.1.x 功能集：提交软件 OSS 直传
    // （OssUpload / SubmitEndpoint / DeviceFingerprint / IconResize）＋ 内容"一切从网络取"
    // （取消安装包内置内容包，GithubContentSync 整理版本 v2）＋ 浮窗抽号结果字号整体上调 ＋
    // 悬浮窗打开工具页动画只播一次。版本号经用户确认**留回 1.1.0**（功能并入 1.1 正式线，
    // 不再单独抬到 1.1.1；基数不动，旧包不会被强制顶掉）。
    // 客户端据此判定 IsInsider=false → 走 stable 通道，只认非预发布的 Release。
    public const string ShellVersion = "1.1.0";

    /// <summary>当前是不是预览（内测）构建 —— 版本号里带 <c>insider</c> 即为真。</summary>
    public static bool IsInsider =>
        ShellVersion.Contains("insider", System.StringComparison.OrdinalIgnoreCase);

    /// <summary>桌面版的版本号前缀（Nick 指定：dv）。</summary>
    public const string VersionPrefix = "dv";

    // ════════════════════════════════════════════════════════════════
    // 更新（双通道：正式版 / 预览版）= GitHub Releases（公开仓库，匿名读，不用令牌）
    //   · 正式版（stable）→ 只看【非预发布】的 Release
    //   · 预览版（insider）→ 预发布 Release ＋ 非预发布（预览用户也能跟上正式版）
    //
    // 发版时按这套约定起名，才能被自动识别（别乱起）：
    //   tag  ：正式版 `dv1.0.0`      预发布 `dv1.0.0-insider1.4`（发布时勾 Pre-release）
    //   资产 ：`ClassSoftwareHub-Setup-<tag>.exe`（名字带 setup 才认）＋ 同名 `.md5`
    //   正文 ：会原样显示在更新对话框里 → 写本次更新内容
    // ════════════════════════════════════════════════════════════════

    /// <summary>更新仓库 owner（发布 Release 的那个仓库）。</summary>
    public const string UpdateRepoOwner = "c1201y";

    /// <summary>
    /// 更新仓库名 —— **Windows 7 移植版有自己独立的仓库**（2026-10-04 起独立成库）。
    ///
    /// 与 WinUI 版（ClassSoftwareHub-Desktop）彻底分家：Release 列表、更新日志各看各的，
    /// 不存在"收到另一个版本的更新"这种事。正式版 = Release 的 Latest；预览版 = Pre-release。
    /// </summary>
    public const string UpdateRepoName = "ClassSoftwareHub-windows7";

    /// <summary>
    /// 本版处于哪条发布线（也写在界面上，让用户知道自己在哪条线上）。纯信息性。
    /// </summary>
    public const string ReleaseBranch = "main";

    /// <summary>
    /// Release tag 前缀 —— 独立仓库之后**留空**（不再需要）。
    ///
    /// 历史：2026-10-04 之前 Win7 版与 WinUI 版共用 <c>ClassSoftwareHub-Desktop</c>，
    /// 而 GitHub 的 Release 列表是**仓库级**的、不分分支，只能靠前缀
    /// （<c>win7-dv1.0.0</c>）分家。现在 Win7 版独立成库，天然隔离，
    /// 于是清空本项 —— tag 回到干净的 <c>dv1.0.0</c> 形状。
    /// 机制本身保留：将来若又需要合库，填上前缀即可（发版脚本要同步改）。
    /// </summary>
    public const string UpdateTagPrefix = "";

    /// <summary>可选：GitHub 令牌（留空走匿名，60 次/小时，够用）。⚠️ 永远不要写死在公开仓库里。</summary>
    public const string UpdateToken = "";

    /// <summary>
    /// 本版本默认走哪条通道：版本号里带 insider 的内部构建默认预览版，否则正式版。
    /// 用户可在设置页改；改过之后按 settings.json 里存的值来。
    /// </summary>
    public static string DefaultUpdateChannel => IsInsider ? "insider" : "stable";

    /// <summary>与站点 v2.3.4 对齐的适配版本号（短号，传给网页做核对用的那个值）。</summary>
    public const string SiteVersionTarget = "v2.3.4";

    /// <summary>
    /// 设置页「站点版本」那一行显示的全文。
    ///
    /// ⛔⛔ **不要再改回去读内容包的 <c>text/ui.json → app.version</c>**（2026-10-04 踩实了）：
    ///   内容包（联网同步下来的缓存）里 <c>text/</c> 这类文件不在 GitHub 仓库的「软件数据/」维护范围内，
    ///   同步不会更新它 —— 缓存里一旦有旧文案就**冻结**在装机/上次同步那版：
    ///   该字段会一直停在旧值（上游实测老机器上读到的是 2.3.2），怎么升级客户端都不会变，
    ///   用户就会看到「客户端 1.6 / 站点版本 2.3.2」这种自相矛盾的搭配。
    ///
    /// 站点的真实版本只有客户端自己知道（发版时人工对齐），所以这里以**编译进程序的常量**为准，
    /// 每次发版跟着 <see cref="SiteVersionTarget"/> 一起改。
    /// </summary>
    public const string SiteVersionDisplay = "v2.3.4 - Tangram (20260927PR01)";

    public const string WebView2DownloadUrl = "https://developer.microsoft.com/microsoft-edge/webview2/";

    public const string NetworkErrorMessage = "网络出现错误，请稍后重试";

    /// <summary>标题栏高度（与 AppWindow PreferredHeightOption.Tall 对齐）。</summary>
    public const int TitleBarHeight = 48;

    /// <summary>导航超时（秒），超时视为网络错误。</summary>
    public const int LoadTimeoutSeconds = 25;

    /// <summary>单实例互斥名。</summary>
    public const string MutexName = "ClassSoftwareHub.Desktop.SingleInstance";

    /// <summary>
    /// 「把主窗口叫出来」的命名事件名。
    ///
    /// ⛔ 为什么必须有这个：默认关闭窗口是**收进托盘**（<c>CloseToTray</c> 默认 true），进程并没有退出。
    /// 此时用户再点桌面图标 / 开始菜单 / 双击 exe，新起来的第二个实例会被 <see cref="MutexName"/>
    /// 挡在门外 —— 2026-09-30 之前那里是直接 <c>Environment.Exit(0)</c>，**什么反应都没有**，
    /// 用户只能去任务管理器结束进程（实测反馈就是这么来的）。
    /// 现在第二个实例改为先敲这个事件，让第一个实例把主窗口亮出来，然后自己才退。
    /// </summary>
    public const string ActivateEventName = "ClassSoftwareHub.Desktop.ActivateMainWindow";

    // ════════════════════════════════════════════════════════════════
    // 内容（软件数据）—— 原生界面用，跟站点 dist/content/ 那套对应
    // ════════════════════════════════════════════════════════════════

    /// <summary>
    /// **首选来源**：软件数据直接在站点仓库里，从 GitHub 读就是最新最全的（仓库是公开的，不用令牌）。
    /// 站点的 content/manifest.json 一直没发布，所以这里才是主力，manifest / 自带内容包是备用。
    /// </summary>
    public const string SiteRepoOwner = "c1201y";
    public const string SiteRepoName = "ClassSoftwareHub";
    public const string SiteRepoBranch = "main";

    /// <summary>仓库里软件数据所在目录（子目录 apps/ 一个软件一个 json，根上还有 categories.json）。</summary>
    public const string SiteRepoDataDir = "软件数据";

    // ════════════════════════════════════════════════════════════════
    // 自建提交服务 —— 「提交软件」与「回声洞投稿」共用
    // ════════════════════════════════════════════════════════════════

    /// <summary>
    /// 提交服务入口（自建 Worker <c>classhub</c>，令牌在服务端，客户端只发内容）。
    /// 提交软件走 <c>{入口}/api/submit</c>；回声洞投稿走 <c>{入口}/api/echocave</c>；
    /// 反馈中心走 <c>{入口}/api/feedback</c>（见 <see cref="Services.FeedbackSubmit"/>）。
    /// 两个域名指向同一套服务，按顺序试；成功的那个记进 <see cref="SubmitEndpointFile"/>。
    /// </summary>
    public static readonly string[] SubmitEndpoints =
    {
        "https://cshapi.132614.xyz",
        "https://submit.132614.xyz",
    };

    /// <summary>「上次可用的提交入口」记忆文件（与提交软件共用同一个）。</summary>
    public const string SubmitEndpointFile = "submit-endpoint.txt";

    /// <summary>提交请求超时（毫秒）。</summary>
    public const int SubmitTimeoutMs = 10000;

    /// <summary>提交入口的完整地址（按"上次成功优先"排好序）。</summary>
    public static System.Collections.Generic.List<string> OrderedSubmitEndpoints()
    {
        var list = new System.Collections.Generic.List<string>();
        try
        {
            var file = System.IO.Path.Combine(AppPaths.DataDir, SubmitEndpointFile);
            if (System.IO.File.Exists(file))
            {
                var remembered = System.IO.File.ReadAllText(file).Trim();
                if (System.Array.IndexOf(SubmitEndpoints, remembered) >= 0) list.Add(remembered);
            }
        }
        catch { /* 读不到就用默认顺序 */ }

        foreach (var endpoint in SubmitEndpoints)
            if (!list.Contains(endpoint)) list.Add(endpoint);

        return list;
    }

    /// <summary>记下这次成功的入口，下次先试它。</summary>
    public static void RememberSubmitEndpoint(string baseUrl)
    {
        try
        {
            System.IO.Directory.CreateDirectory(AppPaths.DataDir);
            System.IO.File.WriteAllText(
                System.IO.Path.Combine(AppPaths.DataDir, SubmitEndpointFile), baseUrl);
        }
        catch { /* 记不住不影响功能 */ }
    }

    /// <summary>
    /// 正式来源：站点上的内容清单（route 2 的产物，跟站点一起发布）。
    /// 桌面版只轮询这一个文件，按 sha256 增量拉变化的文件。
    /// </summary>
    public const string ContentManifestUrl = "https://classsoftwarehub.us.ci/content/manifest.json";

    /// <summary>备用来源（主站挂了可以从网盘拿）。</summary>
    public const string ContentManifestUrlFallback = "https://pan.132614.xyz/dav/%E7%BD%91%E7%AB%99/content/manifest.json";

    /// <summary>增量同步下来的内容缓存目录。</summary>
    public static string CachedContentDir => System.IO.Path.Combine(AppPaths.DataDir, "content");

    // ⚠️ 「安装包自带内容包」（BundledContentDir）已随上游 2026-10-05 定案删除：
    //    装机残留会让已下架的软件永远"删了又复活"，且自带 text/ 写进缓存后再也不更新。
    //    现在内容**只**来自网络同步（GithubContentSync → CachedContentDir），
    //    新装机器首次启动必须联网，拿不到就是空清单 + 重试入口。

    /// <summary>
    /// 开发用：直接读站点工程的内容包（跑过 scripts/build-content.mjs 就有）。
    /// 只有缓存目录里没数据时才会用到它 —— 正式用户机器上这个路径不存在，自动跳过。
    /// </summary>
    public const string DevContentDir =
        @"C:\Users\Programmer_Nick\OneDrive\文档\Visual Studio 18 项目文件\ClassSoftwareHub\dist\content";

    /// <summary>应用内网页浮层（WebSheet，比如详情页里「顺手看一眼官网」）—— 全程序唯一还会用到 WebView2 的地方。⚠️「提交软件」早已是原生页（Pages/SubmitPage.xaml），别再看这条注释。</summary>
    public const string SubmitPageUrl = "https://classsoftwarehub.us.ci/#/submit";
}

/// <summary>应用数据目录（设置、缓存、内容）。</summary>
public static class AppPaths
{
    public static string DataDir { get; } = System.IO.Path.Combine(
        System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
        "ClassSoftwareHub");
}
