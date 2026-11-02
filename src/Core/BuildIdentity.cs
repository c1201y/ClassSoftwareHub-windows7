using System;
using System.IO;
using System.Reflection;
using System.Text;

namespace ClassSoftwareHub.Desktop.Core;

/// <summary>
/// 构建身份：区分「官方发布版」与「自行编译版」。
///
/// <para>
/// 仓库是公开的，任何人 clone 下来编译出的二进制都能连上自建后端。官方包在**编译期**把
/// 一份签名写进程序集元数据（<see cref="StampMetaKey"/>，内容是 <c>v1|构建号|签名|签发时间</c>
/// 的 base64），签名由发布密钥对「构建号 + 版本号」算出 —— **那把钥匙不在仓库里**，所以自行
/// 编译的版本拿不到合法签名。客户端把构建号与签名随每个后端请求发出，服务端据此判定放行还是
/// 要求激活。
/// </para>
/// <para>
/// 签名内嵌而非放 exe 同目录的明文 txt：明文文件谁都能顺手拷走，塞进程序集至少挡住复制粘贴
/// 这一层（防不住有意反编译，这一点不必自欺）。
/// </para>
/// <para>
/// 文件缺失或内容不合法一律视为社区版（本地构建本来就该是这样，不算错误）。
/// 该标记只影响在线服务，本地工具不受任何影响。
/// </para>
/// </summary>
public static class BuildIdentity
{
    /// <summary>旧版打包方式留在应用目录的签名文件名（仅作兼容回落）。</summary>
    public const string StampFileName = "build-stamp.txt";

    /// <summary>编译期注入的签名在程序集元数据里的键名（见 csproj 的 AssemblyMetadata）。</summary>
    public const string StampMetaKey = "CshBuildStamp";

    /// <summary>优先读取的环境变量（值是签名文件路径，或整行内容本身）。便于 CI / 调试覆盖。</summary>
    public const string StampEnvVar = "CSH_BUILD_STAMP";

    /// <summary>当前文件的格式版本，与签名一并参与计算。</summary>
    private const string StampFormat = "v1";

    /// <summary>构建号，形如 <c>dv1.2.0-insider1.5+20261102T0730Z</c>。社区版为 <c>&lt;版本&gt;+local</c>。</summary>
    public static string BuildId { get; }

    /// <summary>构建签名（十六进制）。社区版为空串。</summary>
    public static string Signature { get; }

    /// <summary>签名签发时间（签名文件里的原样文本，社区版为空串）。仅用于日志与排障。</summary>
    public static string IssuedAt { get; }

    /// <summary>是否为官方发布的构建（签名非空）。</summary>
    public static bool IsOfficial => Signature.Length > 0;

    static BuildIdentity()
    {
        var (id, sig, issued) = Read();
        BuildId = string.IsNullOrWhiteSpace(id) ? ShellConfig.ShellVersion + "+local" : id;
        Signature = sig;
        IssuedAt = issued;

        AppLog.Info("license", IsOfficial
            ? $"构建身份: 官方（{BuildId}，签发 {IssuedAt}）"
            : $"构建身份: 社区（{BuildId}）—— 在线服务需激活后使用");
    }

    private static (string Id, string Sig, string Issued) Read()
    {
        foreach (var raw in Sources())
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var parts = raw.Trim().Split('|');
            if (parts.Length < 3) continue;
            if (!string.Equals(parts[0], StampFormat, StringComparison.OrdinalIgnoreCase)) continue;

            var id = parts[1].Trim();
            var sig = parts[2].Trim();
            var issued = parts.Length > 3 ? parts[3].Trim() : "";
            if (id.Length == 0 || !IsHex(sig)) continue;
            return (id, sig.ToLowerInvariant(), issued);
        }

        return ("", "", "");
    }

    private static string[] Sources()
    {
        var result = new System.Collections.Generic.List<string>(3);

        try
        {
            var env = Environment.GetEnvironmentVariable(StampEnvVar);
            if (!string.IsNullOrWhiteSpace(env))
            {
                // 环境变量里既可以是路径，也可以直接是整行内容
                result.Add(env.Contains('|') ? env : TryRead(env) ?? "");
            }
        }
        catch { /* 读环境变量失败按没有处理 */ }

        result.Add(Embedded() ?? "");
        result.Add(TryRead(Path.Combine(AppContext.BaseDirectory, StampFileName)) ?? "");
        return result.ToArray();
    }

    /// <summary>读编译期注入的签名（AssemblyMetadata，值是 base64）。没有注入时返回 null。</summary>
    private static string? Embedded()
    {
        try
        {
            foreach (var attr in typeof(BuildIdentity).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>())
            {
                if (!string.Equals(attr.Key, StampMetaKey, StringComparison.Ordinal)) continue;
                if (string.IsNullOrWhiteSpace(attr.Value)) continue;

                var raw = attr.Value!.Trim();
                try
                {
                    return Encoding.UTF8.GetString(Convert.FromBase64String(raw));
                }
                catch (FormatException)
                {
                    // 兼容直接写原文的构建（手工塞进来的调试图）
                    return raw;
                }
            }
        }
        catch { /* 元数据读不到按没有处理 */ }

        return null;
    }

    private static string? TryRead(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch
        {
            return null;
        }
    }

    private static bool IsHex(string text)
    {
        if (text.Length is < 32 or > 128) return false;
        foreach (var c in text)
        {
            if (!Uri.IsHexDigit(c)) return false;
        }
        return true;
    }
}
