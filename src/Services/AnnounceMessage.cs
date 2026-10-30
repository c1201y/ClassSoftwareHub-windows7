using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClassSoftwareHub.Desktop.Services;

/// <summary>一条教室喊话（服务端 /api/announce/poll 200 响应体）。</summary>
public sealed class AnnounceMessage
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("text")] public string Text { get; set; } = "";
    [JsonPropertyName("from")] public string From { get; set; } = "";
    [JsonPropertyName("duration")] public int Duration { get; set; }
    [JsonPropertyName("ts")] public long Ts { get; set; }

    /// <summary>从 poll 的 JSON 响应解出一条喊话；结构对不上返回 null（不当空喊话播）。</summary>
    public static AnnounceMessage? Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            // ⚠️ GetString() 只在 ValueKind=String 时合法，字段类型不对会抛 InvalidOperationException
            //    （不在 JsonException 的 catch 范围内）—— 先看类型再取值。
            var text = StringOf(root, "text");
            if (string.IsNullOrWhiteSpace(text)) return null;

            return new AnnounceMessage
            {
                Id = StringOf(root, "id") ?? "",
                Text = text,
                From = StringOf(root, "from") ?? "",
                Duration = root.TryGetProperty("duration", out var d) && d.TryGetInt32(out var dur) ? dur : 30,
                Ts = root.TryGetProperty("ts", out var ts) && ts.TryGetInt64(out var l) ? l : 0,
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? StringOf(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
