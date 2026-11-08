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
    private static bool _loggedDeviceMismatch;
    private static bool _loggedExpired;

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

            if (record.Device.Length > 0 && record.Device != DeviceFingerprint.Value)
            {
                // 令牌与设备码绑死：换机之后不能一直拿着废令牌去撞 403。
                // ⚠️ 这条 getter 每个受保护请求都会走一次，日志只记一遍，否则会刷满日志。
                if (!_loggedDeviceMismatch)
                {
                    _loggedDeviceMismatch = true;
                    AppLog.Info("license", "本机保存的令牌是别的设备签发的，已忽略");
                }
                return "";
            }

            if (record.ExpiresAt > 0 && DateTimeOffset.UtcNow.ToUnixTimeSeconds() >= record.ExpiresAt)
            {
                if (!_loggedExpired)
                {
                    _loggedExpired = true;
                    AppLog.Info("license", $"令牌已于 {DateTimeOffset.FromUnixTimeSeconds(record.ExpiresAt).ToLocalTime():yyyy-MM-dd HH:mm} 过期");
                }
                return "";
            }

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

    /// <summary>
    /// 激活时间（本地时间文本，形如 <c>2026-11-08 09:30</c>）。
    ///
    /// <para>
    /// 界面上显示**激活时间**而不是有效期（上游 dv1.2.0-insider1.11）：
    /// 有效期是服务端签发的令牌属性，对用户没有可操作含义，反而容易被误读成
    /// 「这软件快到期、到期就不能用了」。激活时间则是一句陈述事实的话。
    /// </para>
    /// <para>旧凭证里没有这个字段（历史文件 <c>ActivatedAt</c> 为空）→ 返回 null，界面退化为只报「已激活」。</para>
    /// </summary>
    internal static string? ActivatedAtText
    {
        get
        {
            var record = Current;
            if (!IsActivated) return null;
            var raw = (record.ActivatedAt ?? "").Trim();
            if (raw.Length == 0) return null;

            // 存的是 "yyyy-MM-dd HH:mm:ss"，界面只要到分钟
            return DateTime.TryParse(raw, out var at)
                ? at.ToString("yyyy-MM-dd HH:mm")
                : raw;
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
            _loggedDeviceMismatch = false;
            _loggedExpired = false;

            try
            {
                Directory.CreateDirectory(AppPaths.DataDir);

                // 写入要原子（上游后端文档的故障表：「官方包偶发 403，重启后恢复 —— 令牌被旧值覆盖」）：
                // 先写临时文件再整体替换，避免半个 JSON 落盘 / 两次并发保存互相截断。
                var temp = FilePath + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(record, new JsonSerializerOptions
                {
                    WriteIndented = true,
                }));
                File.Move(temp, FilePath, overwrite: true);
            }
            catch (Exception ex)
            {
                // 写不进去也能用完本次运行，只是下次启动要重新激活
                AppLog.Catch("license", "保存激活凭证", ex);
            }
        }

        AppLog.Info("license", $"激活状态｜结果=已激活｜凭证尾号={record.CodeHint}｜设备码={DeviceCode}");
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
        catch (Exception ex)
        {
            // 文件坏了不该让整个功能不可用：删掉重来（下次激活会重写），并按未激活处理
            AppLog.Catch("license", "读取激活凭证", ex);
            try { if (File.Exists(FilePath)) File.Delete(FilePath); } catch { /* 删不掉就留着，反正按未激活处理 */ }
            return new LicenseRecord();
        }
    }

    private static string Tail(string code)
    {
        var clean = (code ?? "").Replace("-", "").Trim();
        return clean.Length <= 4 ? clean : clean[^4..];
    }
}
