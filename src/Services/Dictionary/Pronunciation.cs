using System;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using ClassSoftwareHub.Desktop.Core;

namespace ClassSoftwareHub.Desktop.Services.Dictionary;

/// <summary>
/// 单词发音：直接播有道词典的语音文件（<c>dictvoice</c>），不需要本地音频包。
///
/// <para>
/// ⚠️ 这是**在线**功能：断网、或词库里查出来的生僻词没有音频时会静默失败。
///    所以界面上的喇叭按钮不做成"永远可点"，播放失败要回一句提示，别让用户以为按钮坏了。
/// </para>
///
/// ⚠️ 移植说明（WinUI/WinRT → Win7/Avalonia）：
///   · 原版用 <c>Windows.Media.Playback.MediaPlayer</c> + <c>MediaSource.CreateFromUri</c>
///     直接播远程地址；**Win7 + Avalonia 上没有 WinRT MediaPlayer**。
///   · 新写法：先把音频下载到 <c>%LOCALAPPDATA%\ClassSoftwareHub\dict\voice\</c> 缓存，
///     再交给 <c>winmm.dll</c> 的 MCI 播放（与 <see cref="AnnounceSound"/> 同一套）。
///     缓存还有个额外好处：同一个词连点两次，第二次不用再走网络。
///   · MCI 只建一条固定 alias，播之前先 close，连点不会叠成两遍 ——
///     等价于原版「先 Pause 再换 Source 再 Play」的语义。
/// </summary>
public static class Pronunciation
{
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(10)
    };

    private const string Alias = "csh_dict_voice";

    private static bool _opened;

    private static string Folder => Path.Combine(AppPaths.DataDir, "dict", "voice");

    /// <summary>念一个词。词为空就什么都不做；播放是异步的，调用方不必等。</summary>
    public static void Play(string word)
    {
        var text = (word ?? "").Trim();
        if (text.Length == 0) return;

        _ = PlayAsync(text);
    }

    private static async Task PlayAsync(string word)
    {
        try
        {
            var path = await EnsureCachedAsync(word);
            if (path is null)
            {
                AppLog.Warn("dict", "发音下载失败：" + word);
                return;
            }

            PlayFile(path);
        }
        catch (Exception ex)
        {
            AppLog.Warn("dict", "播放发音失败：" + ex.Message);
        }
    }

    /// <summary>取本地缓存；没有就下载一份。失败返回 null。</summary>
    private static async Task<string?> EnsureCachedAsync(string word)
    {
        var dir = Folder;
        Directory.CreateDirectory(dir);

        var path = Path.Combine(dir, Uri.EscapeDataString(word) + ".mp3");
        if (File.Exists(path) && new FileInfo(path).Length > 0) return path;

        // type=2 是美音，type=1 是英音。教室里念的多半是美音，跟词库里取的音标一致。
        var url = "https://dict.youdao.com/dictvoice?audio="
                  + Uri.EscapeDataString(word) + "&type=2";

        var bytes = await Http.GetByteArrayAsync(url);
        if (bytes.Length == 0) return null;

        // 先写临时文件再换名：中途断网不会留下半截 mp3，下次播放也不会拿到坏文件
        var temp = path + ".part";
        await File.WriteAllBytesAsync(temp, bytes);
        File.Move(temp, path, true);

        return path;
    }

    /// <summary>MCI 播一个本地 mp3。先 close 再从头 open+play。</summary>
    private static void PlayFile(string path)
    {
        lock (typeof(Pronunciation))
        {
            MciClose();

            var open = Mci($"open \"{path}\" type mpegvideo alias {Alias}");
            if (open != 0) open = Mci($"open \"{path}\" alias {Alias}");
            if (open != 0)
            {
                AppLog.Warn("dict", $"发音播放失败：MCI open 返回 {open}（文件：{path}）");
                return;
            }

            _opened = true;

            var play = Mci("play " + Alias);
            if (play != 0)
                AppLog.Warn("dict", $"发音播放失败：MCI play 返回 {play}");
        }
    }

    private static void MciClose()
    {
        if (!_opened) return;
        Mci("close " + Alias);
        _opened = false;
    }

    private static int Mci(string command)
    {
        try
        {
            return mciSendStringW(command, null, 0, IntPtr.Zero);
        }
        catch (Exception ex)
        {
            AppLog.Warn("dict", "MCI 命令失败：" + ex.Message);
            return -1;
        }
    }

    [DllImport("winmm.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern int mciSendStringW(string command, StringBuilder? returnString,
        int returnLength, IntPtr hwndCallback);
}
