using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using ClassSoftwareHub.Desktop.Core;
using Microsoft.Win32;

namespace ClassSoftwareHub.Desktop.Services;

/// <summary>局域网通道的防火墙状态。</summary>
public enum FirewallState
{
    /// <summary>还没查过。调用方按「未知」处理：不显示卡片、不提示。</summary>
    Unknown,

    /// <summary>两条入站规则都在、已启用、覆盖公用网络，且本程序没有入站拦截规则。</summary>
    Ok,

    /// <summary>端口规则缺失 / 被停用 / 不含公用网络。校园网与机房网通常判为「公用」。</summary>
    PortRulesMissing,

    /// <summary>本程序自己有入站 Block 规则 —— Block 永远优先于 Allow，端口规则再对也收不到包。</summary>
    ProgramBlocked,
}

/// <summary>
/// Windows 防火墙的查询与修复（对齐上游第二次重写）。
///
/// <para>
/// **查询只读注册表，不起任何子进程**。上一版用 <c>Get-NetFirewallRule</c> 查询，
/// 每次启动都会拉起 <c>powershell.exe</c>，安全软件按启发式规则直接拦截弹窗。
/// 规则本来就躺在
/// <c>HKLM\SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters\FirewallPolicy\FirewallRules</c>，
/// 该键对 <c>BUILTIN\Users</c> 授予了 <c>ReadKey</c>，普通权限即可读取；一次枚举微秒级。
/// </para>
///
/// <para>
/// 修复动作仍需要提权，但改成「提权跑 netsh」而不是 PowerShell 脚本：规则名全 ASCII，
/// 不涉及编码转换，安全软件告警等级更低；而且只在用户主动点「修复」时发生。
/// </para>
///
/// <para>
/// ⛔ 规则名一律用 ASCII。旧版本的中文名规则在修复时一并删掉，并重建覆盖「任何网络」的规则。
/// </para>
/// </summary>
public static class FirewallGateway
{
    public const string RuleTcpName = "ClassSoftwareHub-Announce-TCP47821";
    public const string RuleUdpName = "ClassSoftwareHub-Announce-UDP47820";

    /// <summary>规则分组名，方便用户在防火墙高级设置里一眼找到并整体删除。</summary>
    private const string RuleGroup = "ClassSoftwareHub";

    /// <summary>旧版本用过的中文规则名（可能只覆盖域/专用，修复时清掉）。</summary>
    private static readonly string[] LegacyRuleNames =
    {
        "ClassSoftwareHub 教室喊话（收件）",
        "ClassSoftwareHub 教室喊话（发现）",
    };

    /// <summary>Windows 自动为程序生成的规则，DisplayName 就是程序文件名。</summary>
    private const string ProgramRuleName = "classsoftwarehub.exe";

    /// <summary>防火墙本地规则库。和「高级安全」界面里看到的同一份数据。</summary>
    private const string RulesKeyPath =
        @"SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters\FirewallPolicy\FirewallRules";

    private static readonly object Gate = new();
    private static FirewallState _cached = FirewallState.Unknown;
    private static DateTime _cachedAt = DateTime.MinValue;

    /// <summary>
    /// 缓存时长。读注册表是微秒级，这层只为「一次页面刷新里连调多次」去重。
    /// ⛔ 刻意**不做磁盘缓存**：读一次比读一个文件还快。
    /// </summary>
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(30);

    /// <summary>上一次查到的状态（可能来自缓存）。</summary>
    public static FirewallState LastKnown
    {
        get { lock (Gate) return _cached; }
    }

    /// <summary>清掉缓存，下一次查询一定重新读。</summary>
    public static void Invalidate()
    {
        lock (Gate)
        {
            _cached = FirewallState.Unknown;
            _cachedAt = DateTime.MinValue;
        }
    }

    /// <summary>
    /// 查当前状态（带 30 秒缓存）。**同步、进程内、永不抛异常**：
    /// 读不到或格式不认识就返回 <see cref="FirewallState.Unknown"/>，调用方按「未知」处理。
    /// </summary>
    public static FirewallState Check(bool force = false)
    {
        lock (Gate)
        {
            if (!force && _cached != FirewallState.Unknown && DateTime.UtcNow - _cachedAt < CacheTtl)
                return _cached;
        }

        var state = QueryCore();

        lock (Gate)
        {
            _cached = state;
            _cachedAt = DateTime.UtcNow;
        }

        return state;
    }

    /// <summary>异步包装：读注册表本来就是微秒级，这里只为不阻塞界面调用点。</summary>
    public static Task<FirewallState> CheckAsync(bool force = false) => Task.FromResult(Check(force));

    /// <summary>补规则 / 删拦截规则（一次 UAC）。返回修复后的实际状态：只有 <see cref="FirewallState.Ok"/> 才算成功。</summary>
    public static FirewallState Repair()
    {
        try
        {
            // 只有真的被程序级规则拦着才去删 —— 那条命令按名字删「所有同名入站规则」，
            // 会把 Windows 自动生成的 Allow 条目一并带走。没被拦就不必付这个代价。
            var needsUnblock = Check(force: true) == FirewallState.ProgramBlocked;

            RunElevatedBatch(RepairCommands(needsUnblock));
        }
        catch (Exception ex)
        {
            // 用户点了「否」也走这里（Win32Exception：操作已被用户取消）
            AppLog.Catch("announce-lan", "修复防火墙入站规则", ex);
        }

        Invalidate();
        var state = QueryCore();

        // 记录**实际结果**而不是「命令跑过了」：否则规则没建/建了但判不出来时，日志毫无线索。
        AppLog.Info("announce-lan", state == FirewallState.Ok
            ? "防火墙入站规则已就绪｜覆盖=任意网络类型｜程序级拦截=无"
            : "防火墙修复后仍未达标｜当前状态=" + Describe(state));

        return state;
    }

    // ════════════════════════ 注册表查询 ════════════════════════

    /// <summary>
    /// 读一遍规则库，判断三件事：TCP 规则在不在、UDP 规则在不在、本程序有没有被入站拦截。
    /// 任何读不到 / 格式不对的情况都返回 <see cref="FirewallState.Unknown"/>。
    /// </summary>
    private static FirewallState QueryCore()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(RulesKeyPath, writable: false);
            if (key is null) return FirewallState.Unknown;

            var names = key.GetValueNames();
            if (names.Length == 0) return FirewallState.Unknown;

            var exe = NormalizePath(Environment.ProcessPath);

            var tcpOk = false;
            var udpOk = false;
            var blocked = false;
            var sawV2 = false;

            foreach (var valueName in names)
            {
                if (key.GetValue(valueName) is not string raw) continue;

                var rule = Core.FirewallRule.Parse(raw);   // 非 v2 值（PolicyVersion 等）在这里被挡掉
                if (rule is null) continue;

                sawV2 = true;
                if (!rule.ActiveInbound) continue;

                if (string.Equals(rule.Action, "Allow", StringComparison.OrdinalIgnoreCase))
                {
                    if (string.Equals(rule.Name, RuleTcpName, StringComparison.OrdinalIgnoreCase) && rule.CoversPublic)
                        tcpOk = true;
                    else if (string.Equals(rule.Name, RuleUdpName, StringComparison.OrdinalIgnoreCase) && rule.CoversPublic)
                        udpOk = true;
                }
                else if (string.Equals(rule.Action, "Block", StringComparison.OrdinalIgnoreCase))
                {
                    var app = NormalizePath(rule.App);
                    if (app is not null && exe is not null && app.Equals(exe, StringComparison.OrdinalIgnoreCase))
                        blocked = true;
                }
            }

            // 一条 v2 规则都没解析出来 = 格式变了或环境特殊，别硬给结论
            if (!sawV2) return FirewallState.Unknown;

            if (blocked) return FirewallState.ProgramBlocked;
            if (!tcpOk || !udpOk) return FirewallState.PortRulesMissing;
            return FirewallState.Ok;
        }
        catch (Exception ex)
        {
            AppLog.Catch("announce-lan", "读取防火墙规则", ex);
            return FirewallState.Unknown;
        }
    }

    /// <summary>路径规范化后再比。注册表里的路径可能大小写不一、也可能带尾部反斜杠。</summary>
    private static string? NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;

        try
        {
            return Path.GetFullPath(path.Trim().TrimEnd('\\', '/'));
        }
        catch
        {
            return path.Trim();
        }
    }

    // ════════════════════════ 修复 ════════════════════════

    /// <summary>
    /// 修复用的 netsh 命令（整段交给提权进程执行）：
    /// 删掉新旧端口规则 → 重建两条覆盖「任何网络」的入站规则；
    /// 只有确实被程序级规则拦截时才额外删掉同名入站规则。
    /// ⚠️ 只动名字是本程序的那几条，不碰任何其它规则。
    /// </summary>
    private static IReadOnlyList<string> RepairCommands(bool needsUnblock)
    {
        var cmds = new List<string>();

        // 1) 清掉同名的旧端口规则（早期版本建的可能只覆盖域/专用，叠加会留下一条无效规则）
        foreach (var name in new[] { RuleTcpName, RuleUdpName }.Concat(LegacyRuleNames))
            cmds.Add($"netsh advfirewall firewall delete rule name=\"{name}\" >nul 2>&1");

        // 2) 重建：profile=any 才是「任何网络」—— 校园网、机房网判为「公用」时才放行
        cmds.Add($"netsh advfirewall firewall add rule name=\"{RuleTcpName}\" group=\"{RuleGroup}\" "
                 + $"dir=in action=allow protocol=TCP localport={AnnounceLanService.ReceivePort} profile=any");
        cmds.Add($"netsh advfirewall firewall add rule name=\"{RuleUdpName}\" group=\"{RuleGroup}\" "
                 + $"dir=in action=allow protocol=UDP localport={AnnounceLanService.DiscoveryPort} profile=any");

        if (needsUnblock)
        {
            // 3) 删掉本程序的入站拦截规则（Windows 第一次弹「允许访问」时点取消留下的）。
            //    ⚠️ netsh 的 delete **没有 action= 参数**，只能按「名字 + 方向」删，
            //       会连带删掉同名的 Allow 规则 —— 无害：端口规则已覆盖 47820/47821。
            //    ⚠️ 不带 program=：路径可能含中文用户名，命令行编码会出问题。
            cmds.Add($"netsh advfirewall firewall delete rule name=\"{ProgramRuleName}\" dir=in >nul 2>&1");
        }

        return cmds;
    }

    /// <summary>
    /// 以管理员身份执行一组 netsh 命令（只弹一次 UAC）。
    /// <para>
    /// ⛔ 不能把多条命令用 <c>&amp;</c> 拼成一个字符串交给 netsh：那一路是 CreateProcess，
    ///    没有 shell 去解释 <c>&amp;</c>，netsh 会把它当成参数的一部分，一条规则也加不上。
    ///    要串联必须交给 cmd.exe。
    /// </para>
    /// <para>
    /// 用临时 .cmd 而不是 <c>cmd /c "…&amp; …"</c>：脚本里带引号的规则名和可能含中文的程序路径，
    /// 直接在命令行里转义极易被 cmd 的引号规则吃掉；写文件后 cmd 只需要解析一个路径。
    /// </para>
    /// </summary>
    private static void RunElevatedBatch(IReadOnlyList<string> commands)
    {
        // 行级白名单：脚本的每一行都必须逐字等于本次由 RepairCommands() 生成的命令。
        // 这样即使以后有人把用户输入拼进命令列表，也只会被整体拒绝，不会变成提权注入。
        var allowed = new HashSet<string>(commands, StringComparer.Ordinal);
        var content = "@echo off\r\n" + string.Join("\r\n", commands) + "\r\n";

        // ⚠️ 2026-11-07：以前直接 WriteAllText 到 %TEMP% 的 .cmd，写完到点 UAC 之间
        //    可被同用户进程覆写。现在原子落盘 + FileShare.Read 持有句柄到提权进程退出，
        //    并回读校验；句柄随 using 覆盖整个 Process.Start / WaitForExit 区间。
        //    批处理按 OEM 代码页读取，必须用同一个编码写。
        using var artifact = ElevatedArtifact.Create(
            "csh-fw", "cmd", OemEncoding(), content,
            line => line == "@echo off" || allowed.Contains(line));

        var psi = new ProcessStartInfo("cmd.exe", "/d /c \"" + artifact.Path + "\"")
        {
            Verb = "runas",
            UseShellExecute = true,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };

        using var proc = Process.Start(psi);
        proc?.WaitForExit(45_000);
    }

    /// <summary>控制台 / 批处理使用的 OEM 代码页（中文系统 936）；取不到就退回 UTF-8。</summary>
    private static Encoding OemEncoding()
    {
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(System.Globalization.CultureInfo.CurrentCulture.TextInfo.OEMCodePage);
        }
        catch
        {
            return Encoding.UTF8;
        }
    }

    /// <summary>给人看的说明（页面用）。</summary>
    public static string Describe(FirewallState state) => state switch
    {
        FirewallState.Ok => "防火墙已放行，同网段的电脑可以发进来。",
        FirewallState.PortRulesMissing => "防火墙尚未放行（或规则只覆盖了域/专用网络）。校园网、机房网通常被判为「公用」，在那种网络下收不到消息。",
        FirewallState.ProgramBlocked => "本程序在 Windows 防火墙里有一条入站拦截规则，所有入站连接会被直接丢弃 —— 端口规则正确也无效。",
        _ => "正在检查防火墙状态……",
    };
}
