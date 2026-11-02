using System;
using System.IO;
using System.Text.Json;
using ClassSoftwareHub.Desktop.Core;

namespace ClassSoftwareHub.Desktop.Services.License;

/// <summary>激活文件的内容。<c>ExpiresAt</c> 为 Unix 秒，<c>0</c> 表示长期有效。</summary>
internal sealed class LicenseRecord
{
    public string Token { get; set; } = "";
    public long ExpiresAt { get; set; }
    public string Device { get; set; } = "";
    public string CodeHint { get; set; } = "";
    public string ActivatedAt { get; set; } = "";
}

/// <summary>
/// 激活凭证的本地存储（<c>%LOCALAPPDATA%\ClassSoftwareHub\license.json</c>）。
///
/// <para>
/// 存的是服务端签发的令牌，不是密钥：令牌本身包含签名与设备绑定，改本地文件没有意义 ——
/// 服务端会重新验签并核对设备码。所以这里不做加密，只保证读写容错（坏文件按未激活处理）。
/// </para>
/// </summary>
internal static class LicenseStore
{
    private static readonly object Gate = new();
    private static LicenseRecord? _current;
    private static bool _loaded;

    internal static string FilePath => Path.Combine(AppPaths.DataDir, "license.json");

    /// <summary>当前设备码（发给开发者用于签发激活码）。</summary>
    internal static string DeviceCode => DeviceFingerprint.Value.ToUpperInvariant();

    /// <summary>可用的令牌；未激活或已过期/换设备时返回空串。</summary>
    internal static string Token
    {
        get
        {
            var record = Current;
            if (record.Token.Length == 0) return "";
            if (record.Device.Length > 0 && record.Device != DeviceFingerprint.Value) return "";
            if (record.ExpiresAt > 0 && DateTimeOffset.UtcNow.ToUnixTimeSeconds() >= record.ExpiresAt) return "";
            return record.Token;
        }
    }

    internal static bool IsActivated => Token.Length > 0;

    /// <summary>已激活时的到期时间（本地时间文本）；长期有效或未激活返回 null。</summary>
    internal static string? ExpiryText
    {
        get
        {
            var record = Current;
            if (!IsActivated || record.ExpiresAt <= 0) return null;
            return DateTimeOffset.FromUnixTimeSeconds(record.ExpiresAt).ToLocalTime().ToString("yyyy-MM-dd");
        }
    }

    /// <summary>激活码后四位，用于在界面/日志里标识这份授权（不保留完整激活码）。</summary>
    internal static string CodeHint => Current.CodeHint;

    private static LicenseRecord Current
    {
        get
        {
            lock (Gate)
            {
                if (!_loaded)
                {
                    _loaded = true;
                    _current = Read();
                }
                return _current!;
            }
        }
    }

    internal static void Save(string token, long expiresAt, string? code = null)
    {
        if (string.IsNullOrWhiteSpace(token)) return;

        var record = new LicenseRecord
        {
            Token = token.Trim(),
            ExpiresAt = expiresAt,
            Device = DeviceFingerprint.Value,
            // 服务端自动续签时不带激活码，沿用上一次的后四位标识
            CodeHint = string.IsNullOrWhiteSpace(code) ? Current.CodeHint : Tail(code),
            ActivatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
        };

        lock (Gate)
        {
            _loaded = true;
            _current = record;

            try
            {
                Directory.CreateDirectory(AppPaths.DataDir);
                File.WriteAllText(FilePath, JsonSerializer.Serialize(record, new JsonSerializerOptions
                {
                    WriteIndented = true,
                }));
            }
            catch (Exception ex)
            {
                // 写不进去也能用完本次运行，只是下次启动要重新激活
                AppLog.Warn("license", $"激活凭证保存失败：{ex.Message}");
            }
        }

        AppLog.Info("license", $"已激活（凭证 …{record.CodeHint}，设备 {DeviceCode}）");
    }

    internal static void Clear()
    {
        lock (Gate)
        {
            _loaded = true;
            _current = new LicenseRecord();
            try
            {
                if (File.Exists(FilePath)) File.Delete(FilePath);
            }
            catch { /* 删不掉就留着，反正下次会覆盖 */ }
        }
    }

    private static LicenseRecord Read()
    {
        try
        {
            if (!File.Exists(FilePath)) return new LicenseRecord();
            var text = File.ReadAllText(FilePath);
            if (text.Length == 0) return new LicenseRecord();
            return JsonSerializer.Deserialize<LicenseRecord>(text) ?? new LicenseRecord();
        }
        catch
        {
            return new LicenseRecord();
        }
    }

    private static string Tail(string code)
    {
        var clean = (code ?? "").Replace("-", "").Trim();
        return clean.Length <= 4 ? clean : clean[^4..];
    }
}
