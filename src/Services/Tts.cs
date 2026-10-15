using System;
using ClassSoftwareHub.Desktop.Core;

namespace ClassSoftwareHub.Desktop.Services;

/// <summary>
/// 语音播报：SAPI5（Win7 自带）的 COM 动态调用。
///
/// <para>
/// 为什么不走 <c>System.Speech</c> NuGet 包：那要多带一份程序集（便携版对体积敏感），
/// 而它底层同样是 SAPI —— 直接用 <c>ProgID "SAPI.SpVoice"</c> 建实例，
/// 零新依赖拿到一模一样的能力，Win7 / Win10 都有引擎。
/// </para>
///
/// <para>
/// 降级口径（与《教室喊话功能设计》4.4 节一致）：
///   · 本机没有 SAPI 引擎 / COM 激活失败 → <see cref="Speak"/> 直接放弃并置 <see cref="Unavailable"/>，
///     本次运行内不再尝试（不弹窗不重试，调用方只管"没声音"这一种结果）；
///   · 没有中文语音（zh-CN，Language=804）→ 照样用默认声音读（可能读成怪音），但在日志里 Warn 一笔。
/// </para>
///
/// ⚠️ SpVoice 实例**在 UI 线程创建**（喊话窗构造时第一次 Speak），此后也只在同一线程调用 ——
///    COM 单元对象跨线程调用要封送，这里刻意不做，用"谁创建谁使用"绕开整个问题。
/// </summary>
internal static class Tts
{
    private static object? _voice;
    private static bool _probed;
    private static bool _failed;

    /// <summary>本机是否找到了中文语音（只用于设置页提示，读不到就当有，别拿它堵功能）。</summary>
    public static bool ChineseVoiceAvailable { get; private set; } = true;

    /// <summary>异步朗读一段话（会把上一段没读完的清掉）。失败静默 —— 语音只是加分项。</summary>
    public static void Speak(string text)
    {
        if (_failed || string.IsNullOrWhiteSpace(text)) return;
        try
        {
            dynamic voice = _voice ??= CreateVoice();
            voice.Speak(string.Empty, 1 + 2);   // SVSFlagsAsync + SVSFPurgeBeforeSpeak：清掉排队再读
            voice.Speak(text, 1);               // SVSFlagsAsync：不卡 UI 线程
        }
        catch (Exception ex)
        {
            // 建好实例之后才炸（声卡拔了 / 引擎被卸了）也按不可用处理，本次运行不再试
            _failed = true;
            AppLog.Warn("announce", "语音播报不可用，后续只出提示音（" + ex.Message + "）");
        }
    }

    /// <summary>停掉正在读 / 排队中的内容（喊话窗关闭时调，防句柄与声音残留）。</summary>
    public static void Cancel()
    {
        try
        {
            if (_voice is not null) ((dynamic)_voice).Speak(string.Empty, 1 + 2);
        }
        catch { /* 已经坏了就不用再清 */ }
    }

    private static dynamic CreateVoice()
    {
        var type = Type.GetTypeFromProgID("SAPI.SpVoice")
            ?? throw new InvalidOperationException("本机没有 SAPI 语音引擎");
        dynamic voice = Activator.CreateInstance(type)!
            ?? throw new InvalidOperationException("SAPI 语音引擎创建失败");

        // 选中文声（0x0804 = zh-CN）。找不到中文声就按系统默认读 —— 读出来可能是怪音，
        // 但比没有强；有没有中文声只用于设置页提示，不据此拒播。
        try
        {
            dynamic voices = voice.GetVoices("Language=804");
            if (voices.Count > 0) voice.Voice = voices.Item(0);
            else ChineseVoiceAvailable = false;
        }
        catch (Exception ex)
        {
            ChineseVoiceAvailable = false;
            AppLog.Warn("announce", "枚举中文语音失败：" + ex.Message);
        }

        voice.Rate = 0;      // 正常语速（教室里播通知，快了听不清）
        voice.Volume = 100;
        return voice;
    }
}
