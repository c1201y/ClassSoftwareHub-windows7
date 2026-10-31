using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using ClassSoftwareHub.Desktop.Core;

namespace ClassSoftwareHub.Desktop.Services;

/// <summary>
/// 倒计时到点的铃声。
///
/// 换它之前是 <c>kernel32!Beep(880, 250)</c> 响三下 —— 主板蜂鸣器那种又尖又干的音色，
/// 音量还挂在"系统声音"那个通道上，投影到教室后排基本听不见。
/// 2026-10-04（Nick）：改成用一段真录音当默认铃声，并且允许再挑一个自己的文件。
///
/// 三条规矩（别顺手改）：
///  ① **只有一个播放器实例**。闹铃语义是"到点响一次"，页面版和浮窗版各持一个播放器
///     只会互相打断、把上一次的声音掐掉。所以这里全局单例，<see cref="Play"/> 前先停。
///  ② **默认铃声是内嵌资源**，运行时释放到 <c>cache\</c> 再把路径交给播放器 ——
///     单文件发布下旁边没有散落的 wav 可读，只能走这条路（同 <see cref="EmbeddedAssets"/>）。
///  ③ **用户文件失效要能退回默认**：挑完的铃声可能被删 / 被移走 / 换了机器，
///     所以每次都是"文件真在才用它"，否则退回内嵌那份，绝不静默哑掉。
///
/// ⚠️ 移植说明（WinUI/WinRT → Win7/Avalonia）：
///   · 原版用 <c>Windows.Media.Playback.MediaPlayer</c> + <c>MediaSource.CreateFromUri</c>
///     播铃声；**Win7 + Avalonia 上没有 WinRT MediaPlayer**。
///   · 新写法：<c>winmm.dll</c> 的 MCI（<c>mciSendStringW</c>）—— Win7 自带，能放 wav / mp3。
///     打开用固定 alias，<see cref="Play"/> 前先 close 再从头 open+play（= 原版"先 Pause 再换 Source
///     再 Play"的等价语义，连点不会叠成两遍）；<see cref="Stop"/> 发 pause。
///   · 文件扩展名不在 wav/mp3 内时（.m4a/.wma/... 这些 Win7 版 MCI 不保证有解码器），
///     MCI 打开多半会失败 —— 这里是"尽力打开 + 记日志"，不会崩。
///
/// ⚠️ 2026-10-31（用户实机反馈：进程崩溃过一次之后倒计时/喊话提示音都不响了）：
///   默认铃声是 wav —— 这类文件改走 <c>winmm!PlaySoundW</c>（SND_FILENAME | SND_ASYNC），
///   MCI 只在非 wav 或 PlaySound 失败时兜底。PlaySound 是 winmm 里最小的一条路，
///   不占设备句柄、不依赖 MCI 的 waveaudio 设备映射，Win7 上更不容易哑火；
///   先前的 MCI 实现保留，作为 mp3 和异常环境的退路。
///
/// TODO(win7): 按移植契约「所有 P/Invoke 集中到 Platform/」的规矩，本文件的 <c>mciSendStringW</c>
///   与 <c>PlaySoundW</c> 本应放进 Platform.NativeMethods；但本轮任务**禁止改动 Platform/**，
///   故暂置于本文件内部。主控如要规整，可把它们搬到 Platform.NativeMethodsUtil，
///   本文件改为转调即可，公开契约不变。
/// </summary>
public static class TimerAlarm
{
    /// <summary>内嵌默认铃声的文件名。<c>EmbeddedAssets</c> 是按"资源名结尾匹配"找的。</summary>
    private const string DefaultAsset = "timer-alarm.wav";

    /// <summary>挑铃声时过滤的扩展名。都交给系统解码器，多列几个也不怕。</summary>
    public static readonly string[] Extensions =
        { ".wav", ".mp3", ".m4a", ".wma", ".aac", ".flac", ".ogg" };

    // ══════════════════════════════════════════════════════════════════
    //  Win7 播放替换点：winmm.dll 的 MCI
    // ══════════════════════════════════════════════════════════════════

    /// <summary>MCI 设备别名。全局单例 → 固定别名即可（<see cref="Play"/> 前会先 close 掉它）。</summary>
    private const string Alias = "csh_timer_alarm";

    // ⚠️ 已显式写死导出名 W 版 + ExactSpelling=true：否则默认的字符串后缀规则会去找
    //    "mciSendStringWW"（先给名字再加 W）而找不到导出。
    [DllImport("winmm.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern int mciSendStringW(string command, StringBuilder? returnString,
        int returnLength, IntPtr hwndCallback);

    // ⚠️ 同样写死 W 版导出名（默认后缀规则会去找 PlaySoundWW）。
    [DllImport("winmm.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = false)]
    private static extern bool PlaySoundW(string? pszSound, IntPtr hMod, uint fdwSound);

    private const uint SndAsync = 0x0001;
    private const uint SndNoDefault = 0x1000;
    private const uint SndFileName = 0x00020000;

    private static bool _opened;                // 当前 alias 是否已 open（Stop 时用）
    private static bool _usingPlaySound;        // 本次是否走的 PlaySound（Stop 时用）
    private static string? _defaultPath;        // 内嵌那份释放出来的路径（缓存一次）

    /// <summary>用户自定义铃声的完整路径；空 = 用默认。直接读写 settings.json。</summary>
    public static string CustomPath
    {
        get => App.Settings.Current.TimerAlarmPath ?? string.Empty;
        set
        {
            App.Settings.Current.TimerAlarmPath = value ?? string.Empty;
            App.Settings.Save();
        }
    }

    /// <summary>界面上显示的名字（按钮文案 / 提示用）。</summary>
    public static string DisplayName
    {
        get
        {
            var path = CustomPath;
            if (string.IsNullOrWhiteSpace(path)) return "默认铃声";
            try
            {
                var name = Path.GetFileName(path);
                return string.IsNullOrWhiteSpace(name) ? "自定义铃声" : name;
            }
            catch
            {
                return "自定义铃声";
            }
        }
    }

    /// <summary>当前该播哪个文件。自定义那份不在了就退回内嵌默认。</summary>
    public static string? ResolvePath()
    {
        var custom = CustomPath;
        if (!string.IsNullOrWhiteSpace(custom) && File.Exists(custom)) return custom;

        if (_defaultPath is not null && File.Exists(_defaultPath)) return _defaultPath;
        _defaultPath = EmbeddedAssets.ExtractToCache(DefaultAsset, DefaultAsset);
        return _defaultPath;
    }

    /// <summary>响铃。已经响着就先停再从头放（连点不会叠成两遍）。</summary>
    public static void Play()
    {
        try
        {
            var path = ResolvePath();
            if (string.IsNullOrEmpty(path)) return;

            var ext = Path.GetExtension(path).ToLowerInvariant();
            AppLog.Info("ring", $"响铃请求：{path}");

            // ① wav 优先 PlaySound：最小的一条播放路，winmm 直接按文件名播，不占 MCI 设备
            if (ext == ".wav" && PlayViaSound(path)) return;

            // ② MCI：mp3（wav 走 PlaySound 失败时也在这里再兜一次）
            if (PlayViaMci(path, ext)) return;
            // ③ 最后一搏：不管扩展名都交给 PlaySound；还不行就只记日志
            if (!PlayViaSound(path))
                AppLog.Info("ring", $"铃声播放失败（MCI 与 PlaySound 都没成功）：{path}");
        }
        catch (Exception ex)
        {
            AppLog.Info("ring", "响铃出错：" + ex.Message);
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
                AppLog.Info("ring", "PlaySound 没接住（无音频设备/解码失败），改走 MCI");
                return false;
            }
            _usingPlaySound = true;
            // 成功路径也记一行：实机「没声音」时能直接看出是播放器没接住还是音量/设备的问题
            AppLog.Info("ring", "铃声已交给 PlaySound 播放：" + path);
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Info("ring", "PlaySound 出错：" + ex.Message);
            return false;
        }
    }

    /// <summary>MCI 路径：先 close 再从头 open+play（= 原版「先 Pause 再换 Source 再 Play」）。成功返回 true。</summary>
    private static bool PlayViaMci(string path, string ext)
    {
        if (_usingPlaySound)
        {
            try { PlaySoundW(null, IntPtr.Zero, 0); } catch { }
            _usingPlaySound = false;
        }

        if (ext != ".wav" && ext != ".mp3")
        {
            // MCI 在 Win7 上稳的只有 wav（waveaudio）/ mp3（mpegvideo），其余解码器不保证有
            AppLog.Info("ring", $"铃声格式不在 wav/mp3 内，MCI 可能无法播放：{ext}");
        }

        MciClose();

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
            AppLog.Info("ring", $"铃声播放失败：MCI open 返回 {open}（文件：{path}）");
            return false;
        }

        _opened = true;

        var play = Mci("play " + Alias);
        if (play != 0)
        {
            AppLog.Info("ring", $"铃声播放失败：MCI play 返回 {play}");
            return false;
        }

        AppLog.Info("ring", "铃声已交给 MCI 播放：" + path);
        return true;
    }

    /// <summary>关掉 MCI 的 alias（把上一次的声音一起停掉）。</summary>
    private static void MciClose()
    {
        if (!_opened) return;
        Mci("close " + Alias);
        _opened = false;
    }

    /// <summary>停铃（重新开始 / 重置 / 换模式 / 离开页面时调，别让上一轮的铃追着响）。</summary>
    public static void Stop()
    {
        try
        {
            if (_usingPlaySound)
            {
                PlaySoundW(null, IntPtr.Zero, 0);
                _usingPlaySound = false;
            }

            // 原版是 _player?.Pause()（暂停，不销毁）；MCI 对应发 pause。
            if (!_opened) return;
            Mci("pause " + Alias);
            Mci("seek " + Alias + " to start");   // 归位，下次 play 从头放
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
            AppLog.Info("ring", "MCI 命令失败：" + ex.Message);
            return -1;
        }
    }
}
