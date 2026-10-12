using System;
using System.IO;
using System.Linq;
using System.Reflection;

namespace ClassSoftwareHub.Desktop.Services;

/// <summary>读取内嵌的注入脚本 / 图标资源（单文件发布下也能用）。</summary>
public static class EmbeddedAssets
{
    public static string ReadText(string fileNameEndingWith)
    {
        var asm = Assembly.GetExecutingAssembly();
        var name = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith(fileNameEndingWith, StringComparison.OrdinalIgnoreCase));
        if (name is null) return string.Empty;

        using var stream = asm.GetManifestResourceStream(name);
        if (stream is null) return string.Empty;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>
    /// 把内嵌资源**原样**写到指定路径（「下载示例文件」用）。
    /// 与 <see cref="ExtractToCache"/> 的区别：这个不比较新旧、直接覆盖，因为用户是自己点了"保存到这儿"。
    /// </summary>
    public static bool ExtractTo(string fileNameEndingWith, string outPath)
    {
        try
        {
            var asm = Assembly.GetExecutingAssembly();
            var name = asm.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith(fileNameEndingWith, StringComparison.OrdinalIgnoreCase));
            if (name is null) return false;

            using var stream = asm.GetManifestResourceStream(name);
            if (stream is null) return false;

            var dir = Path.GetDirectoryName(outPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            using var file = File.Create(outPath);
            stream.CopyTo(file);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>把内嵌图片释放到本地缓存目录，返回文件路径（Bitmap 用）。</summary>
    public static string? ExtractToCache(string fileNameEndingWith, string outFileName)
    {
        try
        {
            var asm = Assembly.GetExecutingAssembly();
            var name = asm.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith(fileNameEndingWith, StringComparison.OrdinalIgnoreCase));
            if (name is null) return null;

            // ⚠️ 缓存统一进「按版本分的子目录」：cache\dv{版本}-{yyyyMMddHHmm}\（见 CacheHome）。
            //    以前全堆在 cache\ 根下、靠文件名区分版本，越攒越乱、也没法按版本回收。
            var cacheDir = CacheHome.InstanceDir;
            Directory.CreateDirectory(cacheDir);
            var outPath = Path.Combine(cacheDir, outFileName);

            // 缓存里的跟内嵌的对不上（换了图）就重写一份 —— 不然换了 banner 还显示旧图
            using var stream = asm.GetManifestResourceStream(name)!;

            byte[] payload;
            using (var ms = new MemoryStream())
            {
                stream.CopyTo(ms);
                payload = ms.ToArray();
            }

            // ⚠️ 这里原来只比**长度**：内嵌资源换成等长文件时（换一张尺寸相同的 PNG/ICO 极常见，
            //    timer-alarm.wav 重新编码后等长也有可能）旧缓存永远不会被覆盖 ——
            //    与上面那句"换了 banner 还显示旧图"的意图正好相反。改成整段比内容。
            var stale = true;
            try { stale = !File.Exists(outPath) || !File.ReadAllBytes(outPath).AsSpan().SequenceEqual(payload); }
            catch { stale = true; }

            if (stale)
            {
                // ⚠️ 先写临时文件再 Move：原来用 File.Create(outPath) **截断**目标，
                //    而"判断过期"与"截断"之间没有锁 —— 两个调用者竞争时，一个拿到**被截断的**
                //    缓存文件，另一个拿到 IOException 被外层 catch 吞成 null，
                //    调用方（如 TimerAlarm.ResolvePath）拿到 null 就**静默不响铃**。
                var tmp = outPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    File.WriteAllBytes(tmp, payload);
                    File.Move(tmp, outPath, overwrite: true);
                }
                finally
                {
                    try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
                }
            }
            return outPath;
        }
        catch
        {
            return null;
        }
    }
}
