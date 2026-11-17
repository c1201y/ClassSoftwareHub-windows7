using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace ClassSoftwareHub.Desktop.Data;

// ═══════════════════════════════════════════════════════════════════════
//  CSES 课表的 YAML 读取
//
//  为什么单独写一个而不是引 NuGet 的 YAML 库：
//    · 本版是自包含发布，Win7 机器上体积敏感，为一个只读 4 个字段的格式
//      引一整个 YamlDotNet 不划算；
//    · 我们**只需要**读 CSES v1 这一个已知 schema 的子集，不需要通用 YAML
//      的锚点、标签、多文档、折叠标量等特性；
//    · 通用解析器一旦遇到本格式外的输入，行为难以预期，而这里的要求是
//      「读不懂就明确报错」，宁可拒绝也不要读出错的东西。
//
//  支持的 YAML 子集（覆盖 CSES 导出与手写的常见写法）：
//    · 块映射 `key: value`、块序列 `- item`、嵌套缩进；
//    · 行内序列 `[a, b]`、行内映射 `{a: b}`；
//    · 单引号 / 双引号标量（含转义），未加引号的裸标量；
//    · 注释（`#` 起，引号内的 `#` 不算）；
//    · 空行、行尾空白、CRLF / LF 混排；
//    · 可选的开头 `---` 文档标记。
//
//  明确**不支持**（遇到不会静默吞掉，按「格式不对」报错）：
//    · 锚点 `&a` / 别名 `*a`、标签 `!!str`；
//    · 多文档 `---` 分隔、折叠标量 `>` / 字面标量 `|`。
// ═══════════════════════════════════════════════════════════════════════

/// <summary>
/// 把 CSES 课表的 YAML 文本转成与 JSON 等价的 <see cref="CsesTimetable"/>。
/// 解析失败时抛出 <see cref="FormatException"/>，消息面向用户。
/// </summary>
internal static class CsesYaml
{
    /// <summary>
    /// 判断这段文本**看起来**是不是 YAML 而不是 JSON。
    /// 只做粗判，用于给用户一句准确的提示（"这是 YAML，已按 YAML 解析"或"请另存为 JSON"）。
    /// </summary>
    public static bool LooksLikeYaml(string text)
    {
        var head = (text ?? "").TrimStart('\uFEFF', ' ', '\t', '\r', '\n');
        if (head.Length == 0) return false;

        // JSON 一定以 { 或 [ 开头；YAML 的块映射以 key: 开头
        if (head[0] == '{' || head[0] == '[') return false;

        var firstLine = head.Split('\n')[0].Trim();
        if (firstLine.StartsWith("---", StringComparison.Ordinal)) return true;
        if (firstLine.StartsWith("#", StringComparison.Ordinal)) return true;

        var colon = firstLine.IndexOf(':');
        return colon > 0;
    }

    /// <summary>解析 CSES YAML。失败抛 <see cref="FormatException"/>。</summary>
    public static CsesTimetable Parse(string text)
    {
        var root = ParseToTree(text);
        if (root is not Dictionary<string, object?> map)
            throw new FormatException("YAML 顶层不是映射（应形如 version: 1 / subjects: ...）。");

        var table = new CsesTimetable();

        if (map.TryGetValue("version", out var versionValue))
        {
            var versionText = AsScalar(versionValue);
            if (int.TryParse(versionText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v))
                table.Version = v;
        }

        if (map.TryGetValue("subjects", out var subjectsValue))
        {
            foreach (var item in AsSequence(subjectsValue, "subjects"))
            {
                if (item is not Dictionary<string, object?> s) continue;
                table.Subjects.Add(new CsesSubject
                {
                    Name = Scalar(s, "name"),
                    SimplifiedName = NullableScalar(s, "simplified_name"),
                    Teacher = NullableScalar(s, "teacher"),
                    Room = NullableScalar(s, "room"),
                });
            }
        }

        if (map.TryGetValue("schedules", out var schedulesValue))
        {
            foreach (var item in AsSequence(schedulesValue, "schedules"))
            {
                if (item is not Dictionary<string, object?> d) continue;

                var day = new CsesDaySchedule
                {
                    Name = Scalar(d, "name"),
                    EnableDay = IntScalar(d, "enable_day"),
                    Weeks = Scalar(d, "weeks"),
                };
                if (day.Weeks.Length == 0) day.Weeks = "all";

                if (d.TryGetValue("classes", out var classesValue))
                {
                    foreach (var c in AsSequence(classesValue, "classes"))
                    {
                        if (c is not Dictionary<string, object?> cm) continue;
                        day.Classes.Add(new CsesClass
                        {
                            Subject = Scalar(cm, "subject"),
                            StartTime = Scalar(cm, "start_time"),
                            EndTime = Scalar(cm, "end_time"),
                        });
                    }
                }

                table.Schedules.Add(day);
            }
        }

        return table;
    }

    // ── 映射取值的小工具 ──────────────────────────────────────────────

    private static string Scalar(Dictionary<string, object?> map, string key)
        => map.TryGetValue(key, out var v) ? AsScalar(v) : "";

    private static string? NullableScalar(Dictionary<string, object?> map, string key)
    {
        if (!map.TryGetValue(key, out var v)) return null;
        var text = AsScalar(v);
        return text.Length == 0 ? null : text;
    }

    private static int IntScalar(Dictionary<string, object?> map, string key)
    {
        if (!map.TryGetValue(key, out var v)) return 0;
        var text = AsScalar(v);
        return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0;
    }

    private static string AsScalar(object? value) => value switch
    {
        null => "",
        string s => s.Trim(),
        bool b => b ? "true" : "false",
        double d => d.ToString(CultureInfo.InvariantCulture),
        int i => i.ToString(CultureInfo.InvariantCulture),
        Dictionary<string, object?> => "",
        List<object?> => "",
        _ => Convert.ToString(value, CultureInfo.InvariantCulture)?.Trim() ?? "",
    };

    private static IEnumerable<object?> AsSequence(object? value, string field)
    {
        if (value is List<object?> list) return list;
        if (value is null) return Array.Empty<object?>();
        throw new FormatException($"字段 {field} 应该是列表（以 - 开头的多项），实际不是。");
    }

    // ══════════════════════════════════════════════════════════════════
    //  极简 YAML 解析：缩进驱动，产出 Dictionary / List / string 的树
    // ══════════════════════════════════════════════════════════════════

    private sealed class Line
    {
        public int Indent;
        public string Content = "";
        public int Number;
    }

    private static object? ParseToTree(string text)
    {
        var lines = Preprocess(text);
        if (lines.Count == 0) return null;

        var index = 0;
        var value = ParseBlock(lines, ref index, lines[0].Indent);
        return value;
    }

    /// <summary>切行、去注释、丢空行，记录每行的缩进与原始行号。</summary>
    /// <remarks>
    /// ⚠️ Tab 缩进：YAML 规范本身**禁止**用 Tab 做缩进，但现实里手写的课表文件常混用。
    ///    这里在解析前把行首的 Tab 统一**展开成 4 个空格**，之后只按空格算缩进 ——
    ///    否则「tab 算 2」会和序列项 `- ` 的两位叠加产生歧义（tab 层与空格层算出来一样深，
    ///    子块就判断不出来，整份课表会被解析成空的）。
    ///    展开只作用于**行首缩进区**，值里的 Tab 原样保留。
    /// </remarks>
    private static List<Line> Preprocess(string text)
    {
        const int TabWidth = 4;

        var result = new List<Line>();
        var raw = (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

        for (var i = 0; i < raw.Length; i++)
        {
            var line = StripComment(raw[i]);

            if (i == 0) line = line.TrimStart('\uFEFF');

            var trimmedEnd = line.TrimEnd();
            if (trimmedEnd.Trim().Length == 0) continue;

            var trimmed = trimmedEnd.TrimStart();
            if (trimmed.StartsWith("---", StringComparison.Ordinal)) continue;   // 文档标记，忽略
            if (trimmed.StartsWith("...", StringComparison.Ordinal)) break;      // 文档结束

            // 只展开行首缩进区的 Tab，行内值里的 Tab 不动
            var sb = new StringBuilder(trimmedEnd.Length + 8);
            var indent = 0;
            var k = 0;
            for (; k < trimmedEnd.Length; k++)
            {
                if (trimmedEnd[k] == ' ') { sb.Append(' '); indent += 1; }
                else if (trimmedEnd[k] == '\t') { sb.Append(' ', TabWidth); indent += TabWidth; }
                else break;
            }
            sb.Append(trimmedEnd, k, trimmedEnd.Length - k);

            var expanded = sb.ToString();
            result.Add(new Line
            {
                Indent = indent,
                Content = expanded.Substring(indent).TrimEnd(),
                Number = i + 1,
            });
        }

        return result;
    }

    /// <summary>去掉行尾注释：引号外的 ` #` 之后全部丢弃。</summary>
    private static string StripComment(string line)
    {
        var inSingle = false;
        var inDouble = false;

        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];

            if (inSingle)
            {
                if (c == '\'') inSingle = false;
                continue;
            }

            if (inDouble)
            {
                if (c == '\\') { i++; continue; }
                if (c == '"') inDouble = false;
                continue;
            }

            if (c == '\'') { inSingle = true; continue; }
            if (c == '"') { inDouble = true; continue; }

            if (c == '#' && (i == 0 || char.IsWhiteSpace(line[i - 1])))
                return line.Substring(0, i);
        }

        return line;
    }

    /// <summary>解析一个块（映射或序列），返回 Dictionary / List / 标量。</summary>
    private static object? ParseBlock(List<Line> lines, ref int index, int indent)
    {
        if (index >= lines.Count) return null;

        return IsSequenceItem(lines[index].Content)
            ? ParseSequence(lines, ref index, indent)
            : ParseMapping(lines, ref index, indent);
    }

    private static bool IsSequenceItem(string content)
        => content == "-" || content.StartsWith("- ", StringComparison.Ordinal);

    private static List<object?> ParseSequence(List<Line> lines, ref int index, int indent)
    {
        var list = new List<object?>();

        while (index < lines.Count)
        {
            var line = lines[index];
            if (line.Indent < indent) break;
            if (line.Indent > indent) break;                  // 交给上层（异常缩进按结束处理）
            if (!IsSequenceItem(line.Content)) break;

            var rest = line.Content.Length > 1 ? line.Content.Substring(1).TrimStart() : "";
            index++;

            if (rest.Length == 0)
            {
                // `-` 单独一行，值在下一层的缩进块里
                if (index < lines.Count && lines[index].Indent > indent)
                    list.Add(ParseBlock(lines, ref index, lines[index].Indent));
                else
                    list.Add(null);
                continue;
            }

            // `- key: value` → 这是序列里的一个映射，且后续同层的 `key: value` 属于同一个映射
            if (TrySplitKey(rest, out var key, out var inlineValue))
            {
                var map = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                AddKeyValue(map, key, inlineValue, lines, ref index, indent + 2);   // "- " 占两位

                // 继续吸收后续属于这个映射的键（缩进大于 `-` 所在层）
                while (index < lines.Count && lines[index].Indent > indent && !IsSequenceItem(lines[index].Content))
                {
                    var child = lines[index];
                    if (!TrySplitKey(child.Content, out var k2, out var v2)) break;
                    index++;
                    AddKeyValue(map, k2, v2, lines, ref index, child.Indent);
                }

                list.Add(map);
                continue;
            }

            list.Add(Unquote(rest));
        }

        return list;
    }

    private static Dictionary<string, object?> ParseMapping(List<Line> lines, ref int index, int indent)
    {
        var map = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        while (index < lines.Count)
        {
            var line = lines[index];
            if (line.Indent < indent) break;
            if (line.Indent > indent) break;
            if (IsSequenceItem(line.Content)) break;

            if (!TrySplitKey(line.Content, out var key, out var inlineValue))
                throw new FormatException($"第 {line.Number} 行不是有效的 `键: 值` 形式：{line.Content}");

            index++;
            AddKeyValue(map, key, inlineValue, lines, ref index, indent);
        }

        return map;
    }

    /// <summary>
    /// 把 `key: value` 或 `key:`（值在下一层块）写进映射。
    /// <paramref name="keyIndent"/> 是这个键**自己所在行**的缩进宽度；
    /// 下一行缩进比它大，就说明值是一个嵌套块，否则值为空。
    /// </summary>
    private static void AddKeyValue(
        Dictionary<string, object?> map, string key, string inlineValue,
        List<Line> lines, ref int index, int keyIndent)
    {
        if (inlineValue.Length > 0)
        {
            map[key] = ParseInlineValue(inlineValue);
            return;
        }

        // 值为空 → 看下一行是不是更深一层的块（序列或映射）
        if (index < lines.Count && lines[index].Indent > keyIndent)
        {
            map[key] = ParseBlock(lines, ref index, lines[index].Indent);
            return;
        }

        map[key] = null;
    }

    /// <summary>拆 `key: value`。引号内的冒号不算分隔符。</summary>
    private static bool TrySplitKey(string content, out string key, out string value)
    {
        key = "";
        value = "";

        var inSingle = false;
        var inDouble = false;

        for (var i = 0; i < content.Length; i++)
        {
            var c = content[i];

            if (inSingle)
            {
                if (c == '\'') inSingle = false;
                continue;
            }

            if (inDouble)
            {
                if (c == '\\') { i++; continue; }
                if (c == '"') inDouble = false;
                continue;
            }

            if (c == '\'') { inSingle = true; continue; }
            if (c == '"') { inDouble = true; continue; }

            if (c != ':') continue;

            // 冒号后面必须是空白或行尾，才算键分隔（避免把 http:// 当成键）
            var after = i + 1;
            if (after < content.Length && !char.IsWhiteSpace(content[after])) continue;

            key = Unquote(content.Substring(0, i).Trim());
            value = content.Substring(after).Trim();
            return key.Length > 0;
        }

        return false;
    }

    /// <summary>解析行内值：`[...]`、`{...}`、引号标量、裸标量。</summary>
    private static object? ParseInlineValue(string text)
    {
        var value = text.Trim();
        if (value.Length == 0) return null;

        if (value[0] == '[') return ParseInlineSequence(value);
        if (value[0] == '{') return ParseInlineMapping(value);

        return Unquote(value);
    }

    private static List<object?> ParseInlineSequence(string text)
    {
        var list = new List<object?>();
        var body = StripBrackets(text, '[', ']');
        if (body.Trim().Length == 0) return list;

        foreach (var part in SplitTopLevel(body, ','))
        {
            var item = part.Trim();
            if (item.Length == 0) continue;
            list.Add(item[0] is '[' or '{' ? ParseInlineValue(item) : Unquote(item));
        }

        return list;
    }

    private static Dictionary<string, object?> ParseInlineMapping(string text)
    {
        var map = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        var body = StripBrackets(text, '{', '}');
        if (body.Trim().Length == 0) return map;

        foreach (var part in SplitTopLevel(body, ','))
        {
            if (!TrySplitKey(part.Trim(), out var k, out var v)) continue;
            map[k] = v.Length == 0 ? null : ParseInlineValue(v);
        }

        return map;
    }

    private static string StripBrackets(string text, char open, char close)
    {
        var s = text.Trim();
        if (s.Length > 0 && s[0] == open) s = s.Substring(1);
        if (s.Length > 0 && s[^1] == close) s = s.Substring(0, s.Length - 1);
        return s;
    }

    /// <summary>按顶层分隔符切分，跳过引号与嵌套括号里的分隔符。</summary>
    private static IEnumerable<string> SplitTopLevel(string text, char separator)
    {
        var depth = 0;
        var inSingle = false;
        var inDouble = false;
        var start = 0;

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];

            if (inSingle) { if (c == '\'') inSingle = false; continue; }
            if (inDouble) { if (c == '\\') { i++; continue; } if (c == '"') inDouble = false; continue; }

            switch (c)
            {
                case '\'': inSingle = true; break;
                case '"': inDouble = true; break;
                case '[' or '{': depth++; break;
                case ']' or '}': depth--; break;
                default:
                    if (c == separator && depth == 0)
                    {
                        yield return text.Substring(start, i - start);
                        start = i + 1;
                    }
                    break;
            }
        }

        if (start <= text.Length) yield return text.Substring(start);
    }

    /// <summary>去掉成对的引号并处理转义；不是引号标量就原样返回。</summary>
    private static string Unquote(string text)
    {
        var s = text.Trim();
        if (s.Length < 2) return s;

        if (s[0] == '\'' && s[^1] == '\'')
        {
            var inner = s.Substring(1, s.Length - 2);
            return inner.Replace("''", "'");
        }

        if (s[0] == '"' && s[^1] == '"')
        {
            var inner = s.Substring(1, s.Length - 2);
            var sb = new StringBuilder(inner.Length);

            for (var i = 0; i < inner.Length; i++)
            {
                if (inner[i] != '\\' || i == inner.Length - 1)
                {
                    sb.Append(inner[i]);
                    continue;
                }

                i++;
                sb.Append(inner[i] switch
                {
                    'n' => '\n',
                    't' => '\t',
                    'r' => '\r',
                    '0' => '\0',
                    '\\' => '\\',
                    '"' => '"',
                    '/' => '/',
                    _ => inner[i],
                });
            }

            return sb.ToString();
        }

        return s;
    }
}
