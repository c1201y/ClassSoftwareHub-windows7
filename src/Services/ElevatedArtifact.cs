using System;
using System.IO;
using System.Linq;
using System.Text;

namespace ClassSoftwareHub.Desktop.Services;

/// <summary>
/// 提权执行用的临时脚本 / 注册表导入文件。
///
/// <para>
/// ⚠️ 解决的问题：普通权限进程把 <c>.cmd</c> / <c>.reg</c> 写进用户可写目录，再让
///    <c>runas</c> 起来的管理员进程去读。同用户下的任何进程都能在「写完 → 用户点 UAC
///    『是』」之间覆写、替换或删掉这个文件，等于把管理员权限做成公用工具（TOCTOU → 本地提权）。
///    <c>SystemFeatures</c> / <c>FirewallGateway</c> / <c>TweakStore</c> 三条链路都中招。
/// </para>
///
/// <para>
/// 做法（对齐审计报告 P1 建议的第二条，并修正其中一处）：
/// <list type="number">
///   <item>目录名与文件名都带 Guid，用 <c>FileMode.CreateNew</c> +
///         <c>File.Move(overwrite:false)</c> 落盘，绝不接受一个已经存在的路径；</item>
///   <item>写完后以 <b>只读</b>的 <c>FileShare.Read</c> 持有句柄，直到提权进程退出才释放：
///         其它进程仍能读（cmd.exe / reg.exe 必须能读），但写、删、改名都会被共享冲突挡住。
///         ⛔ 不能用 <c>FileShare.None</c>（提权进程自己也被挡在外面）；
///         ⛔ 也不能用 <c>FileAccess.ReadWrite</c> 持有 —— Windows 要求后续句柄必须
///            共享写权限，只以 FILE_SHARE_READ 打开的读者会当场失败（自检实测）；</item>
///   <item>「Move → 打开保护句柄」之间的空窗被下一道闸封住：若有人抢先拿到写句柄，
///         我们这个只读 guard 的打开必然因共享冲突失败，此时直接放弃，绝不启动 UAC；</item>
///   <item>写完回读文件字节并与预期逐字节比对；</item>
///   <item>调用方传入行级白名单，拒绝命令拼接注入（控制字符、白名单外的命令行等）。</item>
/// </list>
/// </para>
///
/// <para>
/// ⚠️ 残留风险：同用户进程仍能枚举到这个目录，理论上可以在 <c>CreateNew</c> 之前抢先
///     创建同名目录造成失败（失败即放弃执行，不会被利用）。更彻底的零信任方案是改成
///     「提权助手读结构化清单、自己按内置规则重新生成命令、根本不落脚本」，
///     那需要改动 UAC 进程拓扑（由本程序 runas 自己而非 cmd.exe / reg.exe），留作后续改造项。
/// </para>
/// </summary>
internal sealed class ElevatedArtifact : IDisposable
{
    /// <summary>脚本内容上限，防止调用方误把整个日志拼进来。</summary>
    private const int MaxChars = 64 * 1024;

    /// <summary>提权进程要读的文件路径。</summary>
    public string Path { get; }

    private readonly string _directory;
    private readonly FileStream _guard;
    private bool _disposed;

    private ElevatedArtifact(string directory, string path, FileStream guard)
    {
        _directory = directory;
        Path = path;
        _guard = guard;
    }

    /// <summary>
    /// 生成并锁定一份提权脚本。任何一步校验失败都会抛异常，调用方应为「放弃执行」。
    /// </summary>
    /// <param name="prefix">文件名前缀，仅用于排障时辨认来源。</param>
    /// <param name="extension">扩展名（cmd / reg）。</param>
    /// <param name="encoding">脚本编码；会连同 <c>GetPreamble()</c> 一起写入。</param>
    /// <param name="content">脚本正文，由调用方从内置规则生成。</param>
    /// <param name="lineAllowed">
    /// 行级白名单。传入时对每一行（含头部行）调用，返回 false 即整份拒绝。
    /// 传 null 时只做通用控制字符检查。
    /// </param>
    public static ElevatedArtifact Create(string prefix, string extension, Encoding encoding,
                                          string content, Func<string, bool>? lineAllowed = null)
    {
        if (content is null) throw new ArgumentNullException(nameof(content));
        if (content.Length == 0 || content.Length > MaxChars)
            throw new InvalidOperationException("提权脚本内容为空或超出长度上限，已放弃执行。");

        // ① 行级校验：先看有没有控制字符，再交给调用方的白名单。
        var lines = content.Replace("\r\n", "\n").Split('\n');
        foreach (var line in lines)
        {
            if (line.Length == 0) continue;
            foreach (var ch in line)
            {
                // cmd / reg 脚本里出现控制字符基本都是注入；\t 也不放行，规则生成的命令不含它。
                if (ch < 0x20 || ch == 0x7F)
                    throw new InvalidOperationException("提权脚本含控制字符，已放弃执行。");
            }
            if (lineAllowed is not null && !lineAllowed(line))
                throw new InvalidOperationException("提权脚本含白名单之外的命令行，已放弃执行。");
        }

        var expected = encoding.GetPreamble().Concat(encoding.GetBytes(content)).ToArray();

        // ② 随机目录 + 随机文件名：不接受任何预先存在的路径。
        var directory = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "csh-elev-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var finalPath = System.IO.Path.Combine(directory, $"{prefix}-{Guid.NewGuid():N}.{extension}");

        try
        {
            // ③ CreateNew + FileShare.Read 一次到位：从文件诞生起就没有写/删/改名的窗口。
            //    不用「写 .tmp 再 Move」——两步之间恰好会露出一个可竞态的空档。
            var guard = new FileStream(finalPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read);
            try
            {
                guard.Write(expected, 0, expected.Length);
                guard.Flush(flushToDisk: true);

                // ④ 回读校验：落盘字节必须与预期完全一致。
                //    FileStream.Read 不保证一次读满，必须循环到 EOF 再比，否则会误报「被改动」。
                guard.Position = 0;
                var actual = new byte[expected.Length + 1];
                var total = 0;
                while (total < actual.Length)
                {
                    var read = guard.Read(actual, total, actual.Length - total);
                    if (read == 0) break;
                    total += read;
                }
                if (total != expected.Length || !actual.AsSpan(0, total).SequenceEqual(expected))
                    throw new IOException("提权脚本回读校验失败（内容被改动），已放弃执行。");

                guard.Position = 0;
                return new ElevatedArtifact(directory, finalPath, guard);
            }
            catch
            {
                guard.Dispose();
                throw;
            }
        }
        catch
        {
            TryDelete(finalPath);
            TryDeleteDirectory(directory);
            throw;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // ⛔ 顺序不能反：先放行句柄再删，否则 Windows 上删除会失败并留下垃圾文件。
        try { _guard.Dispose(); } catch { }
        TryDelete(Path);
        TryDeleteDirectory(_directory);
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: false); } catch { }
    }
}
