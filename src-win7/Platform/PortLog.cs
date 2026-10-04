using System;
using System.IO;
using System.Text;

namespace ClassSoftwareHub.Desktop.Platform;

/// <summary>
/// 移植期「走到哪一步」打点日志。
///
/// 为什么单独开一个：目标机（Win7 教室机）上「主窗口不显示、侧边栏却有」这类症状，
/// 光看结果根本判断不出是死在初始化流程的哪一段 —— 而构造过程里任何一步抛异常，
/// 后面的代码（包括建窗口）就全都不执行了。这里把启动流程按顺序落进 <c>port.log</c>，
/// 诊断控制台版会实时回显到黑窗上，**最后一条停在哪，就是死在哪**。
///
/// 代价极低（每次启动重写一份、之后单文件追加），所以正式版也保留 ——
/// 万一现场出问题，它就是第一手线索。
/// </summary>
internal static class PortLog
{
    private static readonly object Gate = new();
    private static string? _path;
    private static bool _reset;

    /// <summary>记一步。stage 建议写成「模块: 动作」的形状，方便扫。</summary>
    public static void Step(string stage)
    {
        try
        {
            lock (Gate)
            {
                EnsurePath();

                // 每次进程启动都从空文件重来 —— 免得上次那几百行把这次的顶出视野。
                if (!_reset)
                {
                    _reset = true;
                    try { File.WriteAllText(_path!, ""); } catch { }
                }

                File.AppendAllText(_path!,
                    $"[{DateTimeOffset.Now:HH:mm:ss.fff}] {stage}{Environment.NewLine}",
                    new UTF8Encoding(false));
            }
        }
        catch { }
    }

    /// <summary>「这一步炸了」——带异常类型/消息，并把完整堆栈也写进去。</summary>
    public static void Fail(string stage, Exception? ex)
    {
        Step(ex is null
            ? "✗ " + stage
            : $"✗ {stage} :: {ex.GetType().FullName} :: {ex.Message}");

        if (ex is null) return;
        try
        {
            lock (Gate)
            {
                EnsurePath();
                File.AppendAllText(_path!, ex + Environment.NewLine + Environment.NewLine,
                    new UTF8Encoding(false));
            }
        }
        catch { }
    }

    private static void EnsurePath()
    {
        if (_path is not null) return;
        var dir = Services.SettingsStore.Dir;
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "port.log");
    }
}
