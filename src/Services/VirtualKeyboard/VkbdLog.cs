using System;
using System.IO;

namespace ClassSoftwareHub.Desktop.Services.VirtualKeyboard;

/// <summary>
/// 虚拟键盘自己的日志（<c>%LOCALAPPDATA%\ClassSoftwareHub\vkbd.log</c>）。
///
/// ⚠️ 这份日志**只追加、不轮转**（跟项目里其它日志一致）。所以：
///   · 平时只记"状态变化"和"出错了"；
///   · **按键级**的记录全部收在 <see cref="Verbose"/> 后面（环境变量 <c>CSH_KBD_DEBUG=1</c> 打开）。
/// 不节流的话日志会被功能空转刷到一分钟几百行。
/// </summary>
internal static class VkbdLog
{
    private static readonly object Gate = new();

    /// <summary>键帽级诊断开关（排障时才开）。</summary>
    public static bool Verbose { get; } =
        Environment.GetEnvironmentVariable("CSH_KBD_DEBUG") == "1";

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                Core.AppLog.Write("vkbd", Core.LogLevel.Info, message);
            }
        }
        catch
        {
            // 日志写不进去不该影响键盘本身
        }
    }

    /// <summary>只在 <c>CSH_KBD_DEBUG=1</c> 时记（键帽级）。</summary>
    public static void Detail(string message)
    {
        if (Verbose) Write(message);
    }
}
