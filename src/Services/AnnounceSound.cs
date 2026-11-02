using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using ClassSoftwareHub.Desktop.Core;

namespace ClassSoftwareHub.Desktop.Services;

/// <summary>
/// 喊话提示音。
/// <para>
/// 默认音是内嵌的 <c>announce-alert.wav</c>，运行时释放到 <c>cache\</c> 再交给播放器
/// （单文件发布下旁边没有散落的 wav 可读；与 <see cref="TimerAlarm"/> 同一套做法）。
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
/// ⚠️ wav 优先走 <c>winmm!PlaySoundW</c>（SND_FILENAME | SND_ASYNC），MCI 只在非 wav 或
/// PlaySound 失败时兜底 —— 与 <see cref="TimerAlarm"/> 同一处理。
/// <c>announce-alert.wav</c> 必须包含在 csproj 的 EmbeddedResource 中，否则内嵌资源取不到、播放永远静默。
///
/// TODO(win7): 按移植契约「所有 P/Invoke 集中到 Platform/」的规矩，本文件的 <c>mciSendStringW</c>
///   与 <c>PlaySoundW</c> 本应放进 Platform.NativeMethods；但本轮任务**禁止改动 Platform/**，
///   故暂置于本文件内部（与 <see cref="TimerAlarm"/> 同一处理）。主控如要规整，把它们搬到
///   Platform 一并收拢即可，公开契约不变。
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

    // ⚠️ 同样写死 W 版导出名（默认后缀规则会去找 PlaySoundWW）。
    [DllImport("winmm.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = false)]
    private static extern bool PlaySoundW(string? pszSound, IntPtr hMod, uint fdwSound);

    // MCI 的命令解析器对含空格 / 中文的长路径不稳（PlaySound 没这个问题），
    // 喂给 MCI 前先转 8.3 短路径；卷未启用 8.3 名时转不出，退回原路径。
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int GetShortPathNameW(string longPath, StringBuilder? buffer, int length);

    // MCI 错误码 → 可读文本（open / play 失败时写日志用，只有数字码无法定位）。
    [DllImport("winmm.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern bool mciGetErrorStringW(int errorCode, StringBuilder buffer, int capacity);

    private const uint SndAsync = 0x0001;
    private const uint SndNoDefault = 0x1000;
    private const uint SndFileName = 0x00020000;

    private static bool _opened;                // 当前 alias 是否已 open（Stop 时用）
    private static bool _usingPlaySound;        // 本次是否走的 PlaySound（Stop 时用）
    private static string? _defaultPath;        // 内嵌那份释放出来的路径（缓存一次）
    private static int _playSeq;                // 播放代数：静音检测只认自己这一代

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
            AppLog.Info("announce", "提示音播放请求：" + path);

            // ① wav 优先 PlaySound：最小的一条播放路，winmm 直接按文件名播，不占 MCI 设备
            if (ext == ".wav" && PlayViaSound(path)) return;

            // ② MCI：mp3（wav 走 PlaySound 失败时也在这里再兜一次）
            if (PlayViaMci(path, ext)) return;

            // ③ 最后一搏：不管扩展名都交给 PlaySound；还不行就只记日志
            if (!PlayViaSound(path))
                AppLog.Warn("announce", $"提示音播放失败（MCI 与 PlaySound 都没成功）：{path}");
        }
        catch (Exception ex)
        {
            AppLog.Warn("announce", "播放提示音出错：" + ex.Message);
        }
    }

    /// <summary>用 winmm!PlaySound 播（异步，不阻塞 UI）。成功返回 true。</summary>
    private static bool PlayViaSound(string path)
    {
        try
        {
            MciClose();     // 上一次 MCI 的声音先掐掉，避免叠音
            if (!PlaySoundW(path, IntPtr.Zero, SndFileName | SndAsync | SndNoDefault))
            {
                AppLog.Info("announce", "PlaySound 没接住（无音频设备/解码失败），改走 MCI");
                return false;
            }
            _usingPlaySound = true;
            // 成功路径也记一行：排查「没声音」问题时可直接区分是播放器未接住还是音量/设备问题
            AppLog.Info("announce", "提示音已交给 PlaySound 播放：" + path);
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Warn("announce", "PlaySound 出错：" + ex.Message);
            return false;
        }
    }

    /// <summary>MCI 路径：先 close 再从头 open+play。成功返回 true。</summary>
    private static bool PlayViaMci(string path, string ext)
    {
        var seq = ++_playSeq;

        if (_usingPlaySound)
        {
            try { PlaySoundW(null, IntPtr.Zero, 0); } catch { }
            _usingPlaySound = false;
        }

        if (ext != ".wav" && ext != ".mp3")
        {
            // MCI 在 Win7 上稳的只有 wav（waveaudio）/ mp3（mpegvideo），其余解码器不保证有
            AppLog.Warn("announce", $"提示音格式不在 wav/mp3 内，MCI 可能无法播放：{ext}");
        }

        MciClose();

        // mp3 显式走 mpegvideo 设备；wav 让 MCI 自己选（一般是 waveaudio）。
        // 路径先转 8.3 短路径：MCI 解析含空格 / 中文的长路径经常 open 失败。
        var mciPath = ToMciPath(path);
        var command = ext == ".mp3"
            ? $"open \"{mciPath}\" type mpegvideo alias {Alias}"
            : $"open \"{mciPath}\" alias {Alias}";

        var open = Mci(command);
        if (open != 0)
        {
            // 兜一次：不带 type 再试（少数机器上设备枚举不同）
            open = Mci($"open \"{mciPath}\" alias {Alias}");
        }

        if (open != 0)
        {
            AppLog.Warn("announce", $"提示音播放失败：MCI open 返回 {MciErrorText(open)}（文件：{path}）");
            return false;
        }

        _opened = true;

        var play = Mci("play " + Alias);
        if (play != 0)
        {
            AppLog.Warn("announce", $"提示音播放失败：MCI play 返回 {MciErrorText(play)}");
            return false;
        }

        // MCI 的 play 返回 0 只代表命令被接受，不代表真的在出声
        // （设备/解码器有问题时可能立刻停住）。后台对比 position，不前进记警告，
        // 便于区分「解码器缺失但 open 成功」的机器与音量/设备问题。
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(400);
                var p1 = MciPosition();
                await Task.Delay(400);
                if (_playSeq != seq || !_opened || p1 < 0) return;
                var p2 = MciPosition();
                if (p2 >= 0 && p2 == p1)
                    AppLog.Warn("announce", "MCI play 后 position 未前进，疑似静音播放（解码器/设备异常）：" + path);
            }
            catch { }
        });

        AppLog.Info("announce", $"提示音已交给 MCI 播放：{path}（mode={MciStatus()}）");
        return true;
    }

    /// <summary>MCI 命令用的路径：转 8.3 短路径（MCI 对长路径解析不稳）；失败退回原路径。</summary>
    private static string ToMciPath(string path)
    {
        try
        {
            var sb = new StringBuilder(520);
            var len = GetShortPathNameW(path, sb, sb.Capacity);
            return len > 0 && len < sb.Capacity ? sb.ToString() : path;
        }
        catch { return path; }
    }

    /// <summary>MCI 错误码转可读文本。</summary>
    private static string MciErrorText(int code)
    {
        try
        {
            if (code <= 0) return code.ToString();
            var sb = new StringBuilder(256);
            return mciGetErrorStringW(code, sb, sb.Capacity) ? sb.ToString() : "0x" + code.ToString("X8");
        }
        catch { return code.ToString(); }
    }

    /// <summary>查 MCI alias 当前播放位置（毫秒）；查不到返回 -1。</summary>
    private static int MciPosition()
    {
        var sb = new StringBuilder(32);
        return mciSendStringW("status " + Alias + " position", sb, sb.Capacity, IntPtr.Zero) == 0
               && int.TryParse(sb.ToString(), out var v) ? v : -1;
    }

    /// <summary>查 MCI alias 当前状态（playing/stopped/paused），查不到返回说明文字。</summary>
    private static string MciStatus()
    {
        try
        {
            var sb = new StringBuilder(64);
            var hr = mciSendStringW("status " + Alias + " mode", sb, sb.Capacity, IntPtr.Zero);
            return hr == 0 ? sb.ToString() : "查询失败 0x" + hr.ToString("X8");
        }
        catch (Exception ex)
        {
            return "查询出错：" + ex.Message;
        }
    }

    /// <summary>关掉 MCI 的 alias（把上一次的声音一起停掉）。</summary>
    private static void MciClose()
    {
        if (!_opened) return;
        Mci("close " + Alias);
        _opened = false;
    }

    /// <summary>停（MCI 发 pause 并归位，下次 play 从头放；对应原版 <c>_player?.Pause()</c>）。</summary>
    public static void Stop()
    {
        try
        {
            if (_usingPlaySound)
            {
                PlaySoundW(null, IntPtr.Zero, 0);
                _usingPlaySound = false;
            }

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
