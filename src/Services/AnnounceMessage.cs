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

            var text = root.TryGetProperty("text", out var t) ? t.GetString() : null;
            if (string.IsNullOrWhiteSpace(text)) return null;

            return new AnnounceMessage
            {
                Id = root.TryGetProperty("id", out var i) ? (i.GetString() ?? "") : "",
                Text = text,
                From = root.TryGetProperty("from", out var f) ? (f.GetString() ?? "") : "",
                Duration = root.TryGetProperty("duration", out var d) && d.TryGetInt32(out var dur) ? dur : 30,
                Ts = root.TryGetProperty("ts", out var ts) && ts.TryGetInt64(out var l) ? l : 0,
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
