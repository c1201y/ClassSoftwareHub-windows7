using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using ClassSoftwareHub.Desktop.Core;

namespace ClassSoftwareHub.Desktop.Services;

/// <summary>
/// 喊话提示音（2026-10-25）。
/// <para>
/// 默认音是内嵌的 <c>announce-alert.wav</c>（Nick 给的那份），运行时释放到 <c>cache\</c> 再交给播放器 ——
/// 单文件发布下旁边没有散落的 wav 可读，只能走内嵌这条路（与 <see cref="TimerAlarm"/> 同一套做法）。
/// 用户可以在接收端自选音效，路径存 <c>AnnounceSoundPath</c>；文件不在了自动退回内嵌那份。
/// </para>
///
/// ⚠️ 移植说明（WinUI/WinRT → Win7/Avalonia）：
///   · 原版用 <c>Windows.Media.Playback.MediaPlayer</c> + <c>MediaSource.CreateFromUri</c>
///     播提示音；**Win7 + Avalonia 上没有 WinRT MediaPlayer**。
///   · 新写法：<c>winmm.dll</c> 的 MCI（<c>mciSendStringW</c>）—— Win7 自带，能放 wav / mp3。
///     播放写法与 <see cref="TimerAlarm"/> 完全同一套：打开用固定 alias，
///     <see cref="Play"/> 前先 close 再从头 open+play（= 原版「先 Pause 再换 Source
///     再 Play」的等价语义，连点不会叠成两遍）；<see cref="Stop"/> 发 pause。
///   · 文件扩展名不在 wav/mp3 内时（.m4a/.wma/... 这些 Win7 版 MCI 不保证有解码器），
///     MCI 打开多半会失败 —— 这里是「尽力打开 + 记日志」，不会崩。
///
/// TODO(win7): 按移植契约「所有 P/Invoke 集中到 Platform/」的规矩，本文件的 <c>mciSendStringW</c>
///   本应放进 Platform.NativeMethods；但本轮任务**禁止改动 Platform/**，故暂置于本文件内部
///   （与 <see cref="TimerAlarm"/> 同一处理）。主控如要规整，把它搬到 Platform 一并收拢即可，
///   公开契约不变。
/// </summary>
public static class AnnounceSound
{
    /// <summary>内嵌默认提示音的文件名。<c>EmbeddedAssets</c> 是按「资源名结尾匹配」找的。</summary>
    private const string DefaultAsset = "announce-alert.wav";

    /// <summary>选音效时允许的扩展名（都交给系统解码器）。</summary>
    public static readonly string[] Extensions =
        { ".wav", ".mp3", ".m4a", ".wma", ".aac", ".flac", ".ogg" };

    // ══════════════════════════════════════════════════════════════════
    //  Win7 播放替换点：winmm.dll 的 MCI（原版为 WinRT MediaPlayer）
    // ══════════════════════════════════════════════════════════════════

    /// <summary>MCI 设备别名。全局单例 → 固定别名即可（<see cref="Play"/> 前会先 close 掉它）。</summary>
    private const string Alias = "csh_announce_sound";

    // ⚠️ 已显式写死导出名 W 版 + ExactSpelling=true：否则默认的字符串后缀规则会去找
    //    "mciSendStringWW"（先给名字再加 W）而找不到导出。
    [DllImport("winmm.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern int mciSendStringW(string command, StringBuilder? returnString,
        int returnLength, IntPtr hwndCallback);

    private static bool _opened;                // 当前 alias 是否已 open（Stop 时用）
    private static string? _defaultPath;        // 内嵌那份释放出来的路径（缓存一次）

    /// <summary>用户自定义音效的完整路径；空 = 用默认。直接读写 settings.json。</summary>
    public static string CustomPath
    {
        get => App.Settings.Current.AnnounceSoundPath ?? string.Empty;
        set
        {
            App.Settings.Current.AnnounceSoundPath = value ?? string.Empty;
            App.Settings.Save();
        }
    }

    /// <summary>界面上显示的名字。</summary>
    public static string DisplayName
    {
        get
        {
            var path = CustomPath;
            if (string.IsNullOrWhiteSpace(path)) return "默认提示音";
            try
            {
                var name = Path.GetFileName(path);
                return string.IsNullOrWhiteSpace(name) ? "自定义提示音" : name;
            }
            catch
            {
                return "自定义提示音";
            }
        }
    }

    /// <summary>当前该播哪个文件；自定义那份不在了就退回内嵌默认。</summary>
    public static string? ResolvePath()
    {
        var custom = CustomPath;
        if (!string.IsNullOrWhiteSpace(custom) && File.Exists(custom)) return custom;

        if (_defaultPath is not null && File.Exists(_defaultPath)) return _defaultPath;
        _defaultPath = EmbeddedAssets.ExtractToCache(DefaultAsset, DefaultAsset);
        return _defaultPath;
    }

    /// <summary>响一次。已经在响就先停再从头放，连点不会叠成两遍。</summary>
    public static void Play()
    {
        try
        {
            var path = ResolvePath();
            if (string.IsNullOrEmpty(path))
            {
                AppLog.Warn("announce", "提示音取不到（内嵌资源释放失败）");
                return;
            }

            var ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext != ".wav" && ext != ".mp3")
            {
                // MCI 在 Win7 上稳的只有 wav（waveaudio）/ mp3（mpegvideo），其余解码器不保证有
                AppLog.Warn("announce", $"提示音格式不在 wav/mp3 内，MCI 可能无法播放：{ext}");
            }

            // 原版「先 Pause 再换 Source 再 Play」= 每次都从头放。MCI 这边等价做法：先 close 再重新 open。
            Mci("close " + Alias);
            _opened = false;

            // mp3 显式走 mpegvideo 设备；wav 让 MCI 自己选（一般是 waveaudio）。
            var command = ext == ".mp3"
                ? $"open \"{path}\" type mpegvideo alias {Alias}"
                : $"open \"{path}\" alias {Alias}";

            var open = Mci(command);
            if (open != 0)
            {
                // 兜一次：不带 type 再试（少数机器上设备枚举不同）
                open = Mci($"open \"{path}\" alias {Alias}");
            }

            if (open != 0)
            {
                AppLog.Warn("announce", $"提示音播放失败：MCI open 返回 {open}（文件：{path}）");
                return;
            }

            _opened = true;

            var play = Mci("play " + Alias);
            if (play != 0) AppLog.Warn("announce", $"提示音播放失败：MCI play 返回 {play}");
        }
        catch (Exception ex)
        {
            AppLog.Warn("announce", "播放提示音出错：" + ex.Message);
        }
    }

    /// <summary>停（原版是 <c>_player?.Pause()</c>，MCI 对应发 pause 并归位，下次 play 从头放）。</summary>
    public static void Stop()
    {
        try
        {
            if (!_opened) return;
            Mci("pause " + Alias);
            Mci("seek " + Alias + " to start");
        }
        catch { }
    }

    /// <summary>
    /// 发一条 MCI 命令。返回 0 = 成功，非 0 = MCI 错误码（失败只记日志，不抛）。
    /// </summary>
    private static int Mci(string command)
    {
        try
        {
            return mciSendStringW(command, null, 0, IntPtr.Zero);
        }
        catch (Exception ex)
        {
            AppLog.Warn("announce", "MCI 命令失败：" + ex.Message);
            return -1;
        }
    }
}
