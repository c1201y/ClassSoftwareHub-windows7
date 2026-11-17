using System;
using System.IO;
using System.Linq;

namespace ClassSoftwareHub.Desktop.Services;

/// <summary>
/// 自检工程的最小替身：<c>PinyinTable</c> 按「资源名结尾匹配」读内嵌的 pinyin.tsv。
/// 这里给出与主工程同语义的读取实现，让自检能真正跑通拼音转换（不是空实现）。
/// </summary>
internal static class EmbeddedAssets
{
    public static string ReadText(string fileNameEndingWith)
    {
        var asm = typeof(EmbeddedAssets).Assembly;

        var name = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith(fileNameEndingWith, StringComparison.OrdinalIgnoreCase));

        if (name is null)
        {
            throw new InvalidOperationException(
                "资源未找到：" + fileNameEndingWith
                + "；实际有：" + string.Join(", ", asm.GetManifestResourceNames()));
        }

        using var stream = asm.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
