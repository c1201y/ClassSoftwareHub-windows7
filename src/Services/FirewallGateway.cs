using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using ClassSoftwareHub.Desktop.Core;

namespace ClassSoftwareHub.Desktop.Services;

/// <summary>局域网通道的防火墙状态。</summary>
public enum FirewallState
{
    /// <summary>还没查过（页面刚打开、后台查询未回）。</summary>
    Unknown,

    /// <summary>两条入站规则都在、已启用、覆盖公用网络，且本程序没有入站拦截规则。</summary>
    Ok,

    /// <summary>端口规则缺失 / 被停用 / 不含公用网络。校园网与机房网通常判为「公用」。</summary>
    PortRulesMissing,

    /// <summary>本程序自己有入站 Block 规则 —— Block 永远优先于 Allow，端口规则再对也收不到包。</summary>
    ProgramBlocked,
}

/// <summary>
/// Windows 防火墙的查询与修复（对齐上游 2026-10-27 重写，Win7 版双引擎适配）。
///
/// <para>
/// 为什么单独成文件：旧实现（<c>AnnounceLanService.FirewallRulesPresent</c>）只查
/// 「名字叫这个的规则存在 + 输出里像是有『已启用/公用』」，判据太窄 —— 规则只覆盖
/// 域/专用时，校园网/机房网（通常被判成「公用」）下等于没加，页面就会一直提示要放行。
/// </para>
///
/// <para>
/// 查询引擎按系统能力选：
/// <list type="number">
///   <item><b>PowerShell NetSecurity 模块</b>（Win8+ / PS3+）：脚本经 <c>-EncodedCommand</c>
///     以 UTF-16 传递、输出强制 UTF-8，与系统代码页完全无关；判据是三个必要条件：
///     规则存在 + 已启用 + <c>Profile</c> 位掩码含 <c>Public</c>，并额外检查本程序有没有
///     入站 Block 规则（Block 永远优先于 Allow）。</item>
///   <item><b>netsh 兜底</b>（Win7 默认只有 PS 2.0，没有 NetSecurity 模块）：
///     沿用已验证的输出启发式（已启用 + 覆盖公用）；这一路查不出 Block 规则，
///     只区分 Ok / PortRulesMissing。</item>
/// </list>
/// </para>
///
/// <para>
/// ⛔ 规则名一律用 ASCII（netsh / PowerShell 任何一层编码没对齐，中文名就会变成
/// 「查不到、也删不掉」）。旧版本的中文名规则会在修复时一并删掉，并重建覆盖「任何网络」的规则。
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

    private static readonly object Gate = new();
    private static FirewallState _cached = FirewallState.Unknown;
    private static DateTime _cachedAt = DateTime.MinValue;

    /// <summary>缓存时长：查询要起一个子进程（约 0.5~2 秒），不能每次刷界面都跑一遍。</summary>
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(60);

    /// <summary>上一次查到的状态（可能来自缓存）。</summary>
    public static FirewallState LastKnown
    {
        get { lock (Gate) return _cached; }
    }

    /// <summary>清掉缓存，下一次查询一定重新跑。</summary>
    public static void Invalidate()
    {
        lock (Gate)
        {
            _cached = FirewallState.Unknown;
            _cachedAt = DateTime.MinValue;
        }
    }

    /// <summary>
    /// 查询当前状态（带缓存）。永远不抛异常：查不出来就返回 <see cref="FirewallState.Unknown"/>，
    /// 调用方按「未知」处理，不提示、不打扰。
    /// </summary>
    public static async Task<FirewallState> CheckAsync(bool force = false)
    {
        lock (Gate)
        {
            if (!force && _cached != FirewallState.Unknown && DateTime.UtcNow - _cachedAt < CacheTtl)
                return _cached;
        }

        var state = await Task.Run(QueryCore);

        lock (Gate)
        {
            _cached = state;
            _cachedAt = DateTime.UtcNow;
        }

        return state;
    }

    private static FirewallState QueryCore()
    {
        // 先走 PowerShell（代码页无关、判据完整）；引擎不可用（Win7 PS 2.0）时回落 netsh。
        var output = RunPowerShell(QueryScript(), elevate: false);
        var parsed = ParseQueryOutput(output);
        if (parsed.HasValue) return parsed.Value;

        return QueryViaNetsh();
    }

    /// <summary>解析查询脚本的输出（形如 <c>tcp=1;udp=1;block=0</c>）。解析不出返回 null。</summary>
    private static FirewallState? ParseQueryOutput(string? output)
    {
        if (string.IsNullOrWhiteSpace(output)) return null;
        if (output.Contains("unsupported", StringComparison.Ordinal)) return null;

        var tcp = false;
        var udp = false;
        var block = false;
        var sawAny = false;

        foreach (var part in output.Trim().Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var idx = part.IndexOf('=');
            if (idx <= 0) continue;

            var key = part[..idx].Trim();
            var value = part[(idx + 1)..].Trim() == "1";
            sawAny = true;

            switch (key)
            {
                case "tcp": tcp = value; break;
                case "udp": udp = value; break;
                case "block": block = value; break;
            }
        }

        if (!sawAny) return null;   // 脚本没跑起来 / 被策略拦了 / 引擎不支持

        // Block 优先报：它比端口规则更致命，提示语也不一样
        if (block) return FirewallState.ProgramBlocked;
        if (!tcp || !udp) return FirewallState.PortRulesMissing;
        return FirewallState.Ok;
    }

    /// <summary>
    /// netsh 兜底查询（Win7 / 无 NetSecurity 模块时）。
    /// <para>
    /// 沿用已验证的启发式：规则存在 + 输出含「已启用/Enabled」+ 覆盖「任何/公用/Any/Public」。
    /// 只查两条 ASCII 端口规则；这一路分辨不出 Block 规则，查不出就按端口规则下结论。
    /// </para>
    /// </summary>
    private static FirewallState QueryViaNetsh()
    {
        foreach (var name in new[] { RuleTcpName, RuleUdpName })
        {
            var output = RunNetsh($"advfirewall firewall show rule name=\"{name}\"");
            if (string.IsNullOrEmpty(output)) return FirewallState.Unknown;

            // ⛔ 按「值」判断，不能只看标签在不在（2026-10-28）：
            //    netsh 的输出恒含「已启用:」和「本地 IP: 任何」两行标签，
            //    所以旧写法 Contains("已启用") / Contains("任何") 对任何存在的规则都成立 ——
            //    规则被停用、或只覆盖域/专用时照样判 Ok，页面不再提示，局域网静默不通。
            var enabled = ReadFlagField(output, "已启用", "Enabled");
            var coversPublic = ReadPublicField(output, "配置文件", "Profiles");

            if (enabled is null || coversPublic is null)
            {
                // 输出格式不认识（语言或系统版本差异）：退回旧启发式，保住「能查出来」
                enabled ??= output.Contains("已启用", StringComparison.Ordinal)
                            || output.Contains("Enabled", StringComparison.Ordinal);
                coversPublic ??= output.Contains("任何", StringComparison.Ordinal)
                                 || output.Contains("Any", StringComparison.Ordinal);
            }

            if (enabled != true || coversPublic != true) return FirewallState.PortRulesMissing;
        }

        return FirewallState.Ok;
    }

    /// <summary>取「已启用」这一行的布尔值；标签不存在返回 null（交给调用方兜底）。</summary>
    private static bool? ReadFlagField(string output, params string[] labels)
    {
        var value = ReadField(output, labels);
        if (value is null) return null;

        return value.StartsWith("是", StringComparison.Ordinal)
               || value.StartsWith("启用", StringComparison.Ordinal)
               || value.StartsWith("Yes", StringComparison.OrdinalIgnoreCase)
               || value.StartsWith("Enable", StringComparison.OrdinalIgnoreCase)
               || value == "True";
    }

    /// <summary>取「配置文件」这一行是否含公用网络；标签不存在返回 null。</summary>
    private static bool? ReadPublicField(string output, params string[] labels)
    {
        var value = ReadField(output, labels);
        if (value is null) return null;

        return value.Contains("公用", StringComparison.Ordinal)
               || value.Contains("任何", StringComparison.Ordinal)
               || value.Contains("全部", StringComparison.Ordinal)
               || value.Contains("Public", StringComparison.OrdinalIgnoreCase)
               || value.Contains("Any", StringComparison.OrdinalIgnoreCase);
    }

    private static string? ReadField(string output, string[] labels)
    {
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            var idx = line.IndexOfAny(new[] { ':', '：' });
            if (idx <= 0) continue;

            var key = line[..idx].Trim();
            if (!labels.Any(l => key.Equals(l, StringComparison.OrdinalIgnoreCase))) continue;

            return line[(idx + 1)..].Trim();
        }

        return null;
    }

    /// <summary>
    /// 补规则 / 删拦截规则（一次 UAC）。返回修复后的实际状态：只有 <see cref="FirewallState.Ok"/> 才算成功。
    /// <para>
    /// 修复动作走 netsh（Win7 上也能用，中文规则名经 CreateProcessW 传递不受控制台代码页影响）；
    /// 修完用当前引擎重新查一遍 —— PowerShell 可用时连 Block 规则的清除结果也能验证。
    /// </para>
    /// </summary>
    public static FirewallState Repair()
    {
        try
        {
            RunElevatedBatch(RepairCommands());
        }
        catch (Exception ex)
        {
            // 用户点了「否」也走这里（Win32Exception：操作已被用户取消）
            AppLog.Warn("announce-lan", "修复防火墙未完成：" + ex.Message);
        }

        Invalidate();
        return QueryCore();
    }

    // ════════════════════════ 脚本与进程 ════════════════════════

    /// <summary>
    /// 起一个 PowerShell 执行脚本。
    /// <para>
    /// ⚠️ 脚本一律走 <c>-EncodedCommand</c>（Base64 的 UTF-16LE）：直接塞进命令行会被系统代码页
    /// 重新解释一次，中文用户名路径都会变形。
    /// </para>
    /// </summary>
    private static string? RunPowerShell(string script, bool elevate)
    {
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));

        var psi = new ProcessStartInfo("powershell.exe",
            "-NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -EncodedCommand " + encoded)
        {
            CreateNoWindow = true,
            UseShellExecute = elevate,        // runas 必须走 shell
            RedirectStandardOutput = !elevate,
            RedirectStandardError = !elevate,
            StandardOutputEncoding = elevate ? null : Encoding.UTF8,
            StandardErrorEncoding = elevate ? null : Encoding.UTF8,
        };

        if (elevate) psi.Verb = "runas";

        using var proc = Process.Start(psi);
        if (proc is null) return null;

        if (elevate)
        {
            proc.WaitForExit(45_000);   // 用户在 UAC 框上犹豫是常态，别太急
            return null;
        }

        var output = proc.StandardOutput.ReadToEnd();
        proc.WaitForExit(15_000);
        return output;
    }

    /// <summary>同步跑一次 netsh 并取输出（查询用；不做任何写操作）。</summary>
    private static string? RunNetsh(string arguments)
    {
        try
        {
            var psi = new ProcessStartInfo("netsh", arguments)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                // netsh 按控制台 OEM 代码页输出；.NET Core 默认 UTF-8 解出来是乱码，
                // 中文标签（「已启用」「配置文件」）就一个也匹配不上。
                StandardOutputEncoding = OemEncoding(),
            };

            using var proc = Process.Start(psi);
            if (proc is null) return null;

            var output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(4000);
            return output;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 以管理员身份执行一组 netsh 命令（修复用；只弹一次 UAC）。
    /// <para>
    /// ⛔ 不能把多条命令用 <c>&amp;</c> 拼成一个字符串交给 netsh：那一路是 CreateProcess，
    ///    没有 shell 去解释 <c>&amp;</c>，netsh 会把它当成参数的一部分，一条规则也加不上
    ///    （用户看到的是 UAC 确认了、页面仍说没放行）。要串联必须交给 cmd.exe。
    /// </para>
    /// <para>
    /// 用临时 .cmd 而不是 <c>cmd /c "…&amp; …"</c>：脚本里带引号的规则名和可能含中文的程序路径，
    /// 直接在命令行里转义极易被 cmd 的引号规则吃掉；写文件后 cmd 只需要解析一个路径。
    /// </para>
    /// </summary>
    private static void RunElevatedBatch(IReadOnlyList<string> commands)
    {
        var file = Path.Combine(Path.GetTempPath(), "csh-fw-" + Guid.NewGuid().ToString("N") + ".cmd");

        // 批处理按 OEM 代码页读取；程序路径可能含中文，必须用同一个代码页写文件
        File.WriteAllText(file, "@echo off\r\n" + string.Join("\r\n", commands) + "\r\n", OemEncoding());

        try
        {
            var psi = new ProcessStartInfo("cmd.exe", "/d /c \"" + file + "\"")
            {
                Verb = "runas",
                UseShellExecute = true,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            };

            using var proc = Process.Start(psi);
            proc?.WaitForExit(45_000);
        }
        finally
        {
            try { File.Delete(file); } catch { /* 文件被占用就留给系统清理 */ }
        }
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

    /// <summary>
    /// 查询脚本。判据写死在脚本里，返回三态：tcp / udp / block。
    /// <para>
    /// ⚠️ <c>-PolicyStore ActiveStore</c>：只看当前**生效**的规则集，本机策略与组策略合并后的结果。
    /// 默认的 PersistentStore 在域控下发的规则上看不到，会误判成「没有规则」。
    /// </para>
    /// <para>
    /// ⚠️ 开头先探测 <c>Get-NetFirewallRule</c> 是否存在：Win7 默认只有 PS 2.0、没有 NetSecurity
    /// 模块，直接跑会把「查不到规则」误报成「规则缺失」—— 那种情况输出 <c>unsupported</c>，
    /// 调用方回落 netsh。
    /// </para>
    /// </summary>
    private static string QueryScript()
    {
        var exe = (Environment.ProcessPath ?? "").Replace("'", "''");

        return $@"
$ErrorActionPreference = 'SilentlyContinue'
[Console]::OutputEncoding = [Text.Encoding]::UTF8

if (-not (Get-Command Get-NetFirewallRule -ErrorAction SilentlyContinue)) {{
  Write-Output 'unsupported'
  exit
}}

function Test-PortRule([string]$name) {{
  $rules = @(Get-NetFirewallRule -DisplayName $name -PolicyStore ActiveStore)
  foreach ($r in $rules) {{
    if ($r.Direction -ne 'Inbound') {{ continue }}
    if ($r.Action -ne 'Allow') {{ continue }}
    if ($r.Enabled -ne 'True') {{ continue }}
    # Profile 是位掩码：Domain=1, Private=2, Public=4
    if (($r.Profile -band 4) -ne 0) {{ return 1 }}
  }}
  return 0
}}

$tcp = Test-PortRule '{RuleTcpName}'
$udp = Test-PortRule '{RuleUdpName}'

# 程序级拦截：Block 规则永远优先于 Allow，端口规则再对也收不到包
$block = 0
$exe = '{exe}'
foreach ($r in @(Get-NetFirewallRule -DisplayName '{ProgramRuleName}' -PolicyStore ActiveStore)) {{
  if ($r.Direction -ne 'Inbound') {{ continue }}
  if ($r.Action -ne 'Block') {{ continue }}
  if ($r.Enabled -ne 'True') {{ continue }}
  $app = $r | Get-NetFirewallApplicationFilter
  if ($app -and $app.Program -ieq $exe) {{ $block = 1; break }}
}}

Write-Output ('tcp=' + $tcp + ';udp=' + $udp + ';block=' + $block)
";
    }

    /// <summary>
    /// 修复用的 netsh 命令（整段交给提权进程执行）：
    /// 删掉新旧端口规则 → 重建两条覆盖「任何网络」的入站规则 → 删掉本程序作用域的全部入站规则
    /// （含 Windows 首次弹「允许访问」时点了取消留下的 Block 规则）。
    /// ⚠️ 只动名字 / 程序路径是本程序的那几条，不碰任何其它规则。
    /// </summary>
    private static IReadOnlyList<string> RepairCommands()
    {
        var exe = Environment.ProcessPath ?? "";
        var cmds = new List<string>();

        foreach (var name in new[] { RuleTcpName, RuleUdpName }.Concat(LegacyRuleNames))
            cmds.Add($"netsh advfirewall firewall delete rule name=\"{name}\"");

        // program= 过滤只删「以本程序为作用域」的规则（自动放行 / Block），端口规则不受影响
        cmds.Add($"netsh advfirewall firewall delete rule name=all dir=in program=\"{exe}\"");

        cmds.Add($"netsh advfirewall firewall add rule name=\"{RuleTcpName}\" group=\"{RuleGroup}\" "
                 + $"dir=in action=allow protocol=TCP localport={AnnounceLanService.ReceivePort} profile=any");
        cmds.Add($"netsh advfirewall firewall add rule name=\"{RuleUdpName}\" group=\"{RuleGroup}\" "
                 + $"dir=in action=allow protocol=UDP localport={AnnounceLanService.DiscoveryPort} profile=any");

        return cmds;
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
