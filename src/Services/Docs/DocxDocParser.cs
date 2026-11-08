using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace ClassSoftwareHub.Desktop.Services.Docs;

/// <summary>
/// .docx → <see cref="DocBlock"/>（自己解析 OOXML，不引第三方库）。
///
/// <para>
/// 读的是 <c>word/document.xml</c> 及其配套的 <c>styles.xml</c>（标题级别）、
/// <c>numbering.xml</c>（有序 / 无序列表）、<c>document.xml.rels</c>（超链接与图片）。
/// 图片直接从压缩包里取字节交给渲染器，不落盘。
/// </para>
/// <para>
/// ⛔ 不处理：页眉页脚、脚注尾注、批注、文本框、域代码（除 HYPERLINK）、修订痕迹、嵌套表格。
/// 这些在教程文章里极少出现，跳过比渲染错安全。
/// </para>
/// </summary>
public static class DocxDocParser
{
    private static readonly XNamespace W =
        "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private static readonly XNamespace R =
        "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace A =
        "http://schemas.openxmlformats.org/drawingml/2006/main";
    private static readonly XNamespace WP =
        "http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing";
    private static readonly XNamespace V =
        "urn:schemas-microsoft-com:vml";

    private static readonly Regex HeadingName = new(
        @"^(?:heading|标题)\s*(\d)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>压缩包条目数上限：正常 docx 远达不到，超了基本可判为压缩炸弹。</summary>
    private const int MaxZipEntries = 512;

    /// <summary>累计解压字节上限（32 MiB）：含正文 / 样式 / 编号 / 关系 / 图片，超过直接拒绝解析。</summary>
    private const long MaxUncompressedBytes = 32L * 1024 * 1024;

    private sealed record Relationship(string Target, bool External);

    public static IReadOnlyList<DocBlock> Parse(byte[] bytes)
    {
        using var zip = new ZipArchive(new MemoryStream(bytes, writable: false), ZipArchiveMode.Read);

        // 压缩炸弹防护①：先按中心目录预检（条目数 + 累计声明解压体积），超限直接拒绝解析。
        if (zip.Entries.Count > MaxZipEntries)
            throw new InvalidDataException($"docx 条目过多（{zip.Entries.Count} 个），疑似压缩炸弹，已跳过解析");

        long declaredTotal = 0;
        foreach (var e in zip.Entries)
        {
            declaredTotal += e.Length;
            if (declaredTotal > MaxUncompressedBytes)
                throw new InvalidDataException("docx 解压后体积超过 32 MiB，疑似压缩炸弹，已跳过解析");
        }

        // 压缩炸弹防护②：实际解压时再按累计字节卡一道，防中心目录里的体积被伪造。
        long totalRead = 0;
        byte[] ReadPartBytes(ZipArchiveEntry entry)
        {
            using var stream = entry.Open();
            using var buffer = new MemoryStream();
            var chunk = new byte[8192];
            int n;
            while ((n = stream.Read(chunk, 0, chunk.Length)) > 0)
            {
                totalRead += n;
                if (totalRead > MaxUncompressedBytes)
                    throw new InvalidDataException("docx 解压后体积超过 32 MiB，疑似压缩炸弹，已跳过解析");
                buffer.Write(chunk, 0, n);
            }
            return buffer.ToArray();
        }

        byte[]? ReadOptionalPart(string name) =>
            zip.GetEntry(name) is { } entry ? ReadPartBytes(entry) : null;

        var documentEntry = zip.GetEntry("word/document.xml")
            ?? throw new InvalidDataException("这个 docx 里没有正文（word/document.xml）");

        var document = XDocument.Load(new MemoryStream(ReadPartBytes(documentEntry)));

        var body = document.Root?.Element(W + "body")
            ?? throw new InvalidDataException("这个 docx 的正文结构无法识别");

        var styles = ReadHeadingStyles(ReadOptionalPart("word/styles.xml"));
        var numbering = ReadNumbering(ReadOptionalPart("word/numbering.xml"));
        var rels = ReadRelationships(ReadOptionalPart("word/_rels/document.xml.rels"));

        var blocks = new List<DocBlock>();
        var counters = new Dictionary<string, int>(StringComparer.Ordinal);
        List<DocListItem>? list = null;
        var listOrdered = false;

        void FlushList()
        {
            if (list is null) return;
            if (list.Count > 0) blocks.Add(new DocListBlock(listOrdered, list));
            list = null;
        }

        void Walk(IEnumerable<XElement> nodes)
        {
            foreach (var node in nodes)
            {
                if (node.Name == W + "p")
                {
                    ParseParagraph(node);
                }
                else if (node.Name == W + "tbl")
                {
                    FlushList();
                    AppendTable(node);
                }
                else if (node.Name == W + "sdt")
                {
                    var content = node.Element(W + "sdtContent");
                    if (content is not null) Walk(content.Elements());
                }
            }
        }

        void ParseParagraph(XElement paragraph)
        {
            var props = paragraph.Element(W + "pPr");
            var styleId = (string?)props?.Element(W + "pStyle")?.Attribute(W + "val") ?? "";

            var level = HeadingLevel(styleId, props, styles);

            var numId = "";
            var ilvl = 0;
            var numPr = props?.Element(W + "numPr");
            if (numPr is not null)
            {
                numId = (string?)numPr.Element(W + "numId")?.Attribute(W + "val") ?? "";
                var levelAttr = (string?)numPr.Element(W + "ilvl")?.Attribute(W + "val");
                if (int.TryParse(levelAttr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
                    ilvl = Math.Clamp(parsed, 0, 4);
            }

            var runs = new List<DocRun>();
            var images = new List<DocImageBlock>();

            foreach (var child in paragraph.Elements())
            {
                if (child.Name == W + "pPr") continue;
                WalkInlines(child, null);
            }

            void WalkInlines(XElement element, string? link)
            {
                if (element.Name == W + "r")
                {
                    AppendRun(element, link);
                    return;
                }

                if (element.Name == W + "hyperlink")
                {
                    var id = (string?)element.Attribute(R + "id");
                    var target = id is not null && rels.TryGetValue(id, out var rel) && rel.External
                        ? rel.Target
                        : null;

                    foreach (var child in element.Elements())
                        WalkInlines(child, target ?? link);
                    return;
                }

                if (element.Name == W + "fldSimple")
                {
                    var instruction = (string?)element.Attribute(W + "instr") ?? "";
                    var match = Regex.Match(instruction, "HYPERLINK\\s+\"([^\"]+)\"", RegexOptions.IgnoreCase);
                    var target = match.Success ? match.Groups[1].Value : link;

                    foreach (var child in element.Elements())
                        WalkInlines(child, target);
                    return;
                }

                if (element.Name == W + "ins" || element.Name == W + "smartTag" || element.Name == W + "sdt")
                {
                    foreach (var child in element.Elements())
                        WalkInlines(child, link);
                    return;
                }

                // bookmarkStart / bookmarkEnd / proofErr / del 之类不产出文字
            }

            void AppendRun(XElement run, string? link)
            {
                var rPr = run.Element(W + "rPr");
                var bold = IsOn(rPr?.Element(W + "b"));
                var italic = IsOn(rPr?.Element(W + "i"));
                var runStyle = (string?)rPr?.Element(W + "rStyle")?.Attribute(W + "val") ?? "";
                if (runStyle is "Strong") bold = true;
                if (runStyle is "Emphasis") italic = true;

                foreach (var node in run.Elements())
                {
                    if (node.Name == W + "t")
                    {
                        var text = node.Value;
                        if (text.Length > 0) runs.Add(new DocRun(text, link, bold, italic));
                    }
                    else if (node.Name == W + "tab")
                    {
                        runs.Add(new DocRun("\t", link, bold, italic));
                    }
                    else if (node.Name == W + "br" || node.Name == W + "cr")
                    {
                        runs.Add(new DocRun("\n", link, bold, italic));
                    }
                    else if (node.Name == W + "noBreakHyphen")
                    {
                        runs.Add(new DocRun("-", link, bold, italic));
                    }
                    else if (node.Name == W + "drawing" || node.Name == W + "pict")
                    {
                        if (TryReadImage(node, out var image)) images.Add(image);
                    }
                }
            }

            if (level > 0)
            {
                FlushList();
                if (runs.Count > 0) blocks.Add(new DocHeadingBlock(level, runs));
            }
            else if (numId is { Length: > 0 } and not "0")
            {
                var ordered = IsOrdered(numbering.Ordered, numId, ilvl);
                if (list is not null && listOrdered != ordered) FlushList();

                list ??= new List<DocListItem>();
                listOrdered = ordered;

                var marker = "•";
                if (ordered)
                {
                    var key = numId + ":" + ilvl;
                    if (counters.TryGetValue(key, out var value))
                    {
                        counters[key] = value + 1;
                    }
                    else if (numbering.StartOverrides.TryGetValue(numId, out var perLevel)
                             && perLevel.TryGetValue(ilvl, out var start))
                    {
                        // 命中 w:startOverride：该编号实例这一级从覆盖值起编，之后照常累加
                        counters[key] = start;
                    }
                    else
                    {
                        counters[key] = 1;
                    }

                    foreach (var stale in counters.Keys.Where(k => k.StartsWith(numId + ":", StringComparison.Ordinal)
                                                                   && !k.Equals(key, StringComparison.Ordinal)).ToList())
                    {
                        var staleLevel = stale[(numId.Length + 1)..];
                        if (int.TryParse(staleLevel, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)
                            && n > ilvl)
                            counters.Remove(stale);
                    }

                    marker = counters[key].ToString(CultureInfo.InvariantCulture) + ".";
                }

                list.Add(new DocListItem(ilvl, marker, runs));
            }
            else if (runs.Count > 0)
            {
                FlushList();
                blocks.Add(new DocParagraphBlock(runs));
            }

            foreach (var image in images) blocks.Add(image);
        }

        void AppendTable(XElement table)
        {
            foreach (var row in table.Elements(W + "tr"))
            {
                var cells = new List<string>();
                foreach (var cell in row.Elements(W + "tc"))
                {
                    var text = string.Join("\n", cell.Elements(W + "p").Select(PlainText).Where(t => t.Length > 0));
                    cells.Add(text);
                }

                if (cells.Count == 0) continue;
                blocks.Add(new DocParagraphBlock(DocInline.Parse(string.Join("　｜　", cells))));
            }
        }

        bool TryReadImage(XElement node, out DocImageBlock image)
        {
            image = null!;

            var alt = node.Descendants(WP + "docPr").FirstOrDefault() is { } docPr
                ? (string?)docPr.Attribute("descr") ?? (string?)docPr.Attribute("name") ?? ""
                : "";

            var blip = node.Descendants(A + "blip").FirstOrDefault();
            var embed = (string?)blip?.Attribute(R + "embed") ?? (string?)blip?.Attribute(R + "link");

            var vml = node.Descendants(V + "imagedata").FirstOrDefault();
            if (embed is null && vml is not null) embed = (string?)vml.Attribute(R + "id");

            if (embed is null || !rels.TryGetValue(embed, out var rel) || rel.External) return false;

            var part = ResolvePart("word", rel.Target);
            var entry = zip.GetEntry(part);
            if (entry is null) return false;

            try
            {
                // 图片同样走累计字节上限，避免超大内嵌图片绕过防护
                image = new DocImageBlock("", alt, ReadPartBytes(entry));
                return true;
            }
            catch (InvalidDataException)
            {
                throw;   // 压缩炸弹异常不能被当成"这张图读不到"吞掉，要终止整篇解析
            }
            catch
            {
                return false;
            }
        }

        Walk(body.Elements());
        FlushList();
        return blocks;
    }

    // ── 元数据 ───────────────────────────────────────────────────

    private static int HeadingLevel(
        string styleId, XElement? props, IReadOnlyDictionary<string, int> styles)
    {
        if (styleId.Length > 0 && styles.TryGetValue(styleId, out var mapped)) return mapped;

        var direct = HeadingName.Match(styleId);
        if (direct.Success
            && int.TryParse(direct.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
            return Math.Clamp(n, 1, 6);

        var outline = (string?)props?.Element(W + "outlineLvl")?.Attribute(W + "val");
        if (int.TryParse(outline, NumberStyles.Integer, CultureInfo.InvariantCulture, out var outlineLevel)
            && outlineLevel is >= 0 and <= 5)
            return outlineLevel + 1;

        return 0;
    }

    private static Dictionary<string, int> ReadHeadingStyles(byte[]? bytes)
    {
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (bytes is null) return result;

        try
        {
            var doc = XDocument.Load(new MemoryStream(bytes));

            foreach (var style in doc.Root?.Elements(W + "style") ?? Enumerable.Empty<XElement>())
            {
                var id = (string?)style.Attribute(W + "styleId");
                if (string.IsNullOrEmpty(id)) continue;

                var name = (string?)style.Element(W + "name")?.Attribute(W + "val") ?? "";
                var outline = (string?)style.Element(W + "pPr")?.Element(W + "outlineLvl")?.Attribute(W + "val");

                var level = 0;
                if (int.TryParse(outline, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
                    level = n + 1;
                else if (HeadingName.Match(name) is { Success: true } match
                         && int.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var h))
                    level = h;
                else if (name.Equals("Title", StringComparison.OrdinalIgnoreCase) || name == "标题")
                    level = 1;
                else if (name.StartsWith("Subtitle", StringComparison.OrdinalIgnoreCase))
                    level = 2;

                if (level > 0) result[id] = Math.Clamp(level, 1, 6);
            }
        }
        catch
        {
            // styles.xml 坏掉不影响正文，按默认样式读
        }

        return result;
    }

    /// <summary>
    /// 解析 numbering.xml：
    /// · Ordered —— numId → 每一级是否有序（原有编号逻辑，保持不变）；
    /// · StartOverrides —— numId → (ilvl → w:startOverride 覆盖的起始编号)。
    /// </summary>
    private static (Dictionary<string, Dictionary<int, bool>> Ordered,
                    Dictionary<string, Dictionary<int, int>> StartOverrides)
        ReadNumbering(byte[]? bytes)
    {
        var ordered = new Dictionary<string, Dictionary<int, bool>>(StringComparer.Ordinal);
        var startOverrides = new Dictionary<string, Dictionary<int, int>>(StringComparer.Ordinal);
        if (bytes is null) return (ordered, startOverrides);

        try
        {
            var doc = XDocument.Load(new MemoryStream(bytes));
            var root = doc.Root;
            if (root is null) return (ordered, startOverrides);

            var abstracts = new Dictionary<string, Dictionary<int, bool>>(StringComparer.Ordinal);
            foreach (var abstractNum in root.Elements(W + "abstractNum"))
            {
                var id = (string?)abstractNum.Attribute(W + "abstractNumId");
                if (id is null) continue;
                abstracts[id] = ReadLevels(abstractNum);
            }

            foreach (var num in root.Elements(W + "num"))
            {
                var id = (string?)num.Attribute(W + "numId");
                var abstractId = (string?)num.Element(W + "abstractNumId")?.Attribute(W + "val");
                if (id is null) continue;

                if (abstractId is not null && abstracts.TryGetValue(abstractId, out var levels))
                    ordered[id] = levels;

                // w:num 上的 lvlOverride / startOverride：该编号实例某一级从指定值重新起编
                Dictionary<int, int>? perLevel = null;
                foreach (var lvlOverride in num.Elements(W + "lvlOverride"))
                {
                    var ilvlAttr = (string?)lvlOverride.Attribute(W + "ilvl");
                    var startAttr = (string?)lvlOverride.Element(W + "startOverride")?.Attribute(W + "val");
                    if (!int.TryParse(ilvlAttr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ilvl)) continue;
                    if (!int.TryParse(startAttr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var start)) continue;
                    (perLevel ??= new Dictionary<int, int>())[ilvl] = start;
                }
                if (perLevel is not null) startOverrides[id] = perLevel;
            }
        }
        catch
        {
            // 编号表读不出来就统一按无序处理
        }

        return (ordered, startOverrides);
    }

    private static Dictionary<int, bool> ReadLevels(XElement abstractNum)
    {
        var levels = new Dictionary<int, bool>();
        foreach (var level in abstractNum.Elements(W + "lvl"))
        {
            var ilvl = (string?)level.Attribute(W + "ilvl");
            if (!int.TryParse(ilvl, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index)) continue;

            var format = (string?)level.Element(W + "numFmt")?.Attribute(W + "val") ?? "";
            levels[index] = !format.Equals("bullet", StringComparison.OrdinalIgnoreCase);
        }

        return levels;
    }

    private static bool IsOrdered(
        IReadOnlyDictionary<string, Dictionary<int, bool>> numbering, string numId, int ilvl)
    {
        if (!numbering.TryGetValue(numId, out var levels)) return false;

        // 没声明的那一级沿用最近的一级
        for (var i = ilvl; i >= 0; i--)
            if (levels.TryGetValue(i, out var ordered)) return ordered;

        return false;
    }

    private static Dictionary<string, Relationship> ReadRelationships(byte[]? bytes)
    {
        var result = new Dictionary<string, Relationship>(StringComparer.Ordinal);
        if (bytes is null) return result;

        try
        {
            var doc = XDocument.Load(new MemoryStream(bytes));

            foreach (var rel in doc.Root?.Elements() ?? Enumerable.Empty<XElement>())
            {
                var id = (string?)rel.Attribute("Id");
                var target = (string?)rel.Attribute("Target");
                if (id is null || target is null) continue;

                var external = string.Equals(
                    (string?)rel.Attribute("TargetMode"), "External", StringComparison.OrdinalIgnoreCase);

                result[id] = new Relationship(target, external);
            }
        }
        catch
        {
            // 关系表坏掉 → 链接和图片都读不出来，正文照常
        }

        return result;
    }

    /// <summary><c>w:b</c> 这类开关：元素存在且没有 <c>w:val="0"/"false"</c> 即为开。</summary>
    private static bool IsOn(XElement? element)
    {
        if (element is null) return false;
        var value = (string?)element.Attribute(W + "val");
        return value is null || (value is not ("0" or "false" or "off"));
    }

    private static string PlainText(XElement paragraph) =>
        string.Concat(paragraph.Descendants(W + "t").Select(t => t.Value)).Trim();

    /// <summary>把 rels 里的相对地址（如 <c>media/image1.png</c>）拼成压缩包内的完整路径。</summary>
    private static string ResolvePart(string baseDir, string target)
    {
        var combined = target.StartsWith('/')
            ? target.TrimStart('/')
            : baseDir + "/" + target;

        var parts = new List<string>();
        foreach (var segment in combined.Split('/'))
        {
            if (segment is "" or ".") continue;
            if (segment == "..")
            {
                if (parts.Count > 0) parts.RemoveAt(parts.Count - 1);
                continue;
            }

            parts.Add(segment);
        }

        return string.Join("/", parts);
    }
}
