using System;

namespace ClassSoftwareHub.Desktop.Platform;

/// <summary>
/// 启动/移植期「走到哪一步」打点。
///
/// <para>
/// 目标机（Win7 教室机）上「主窗口不显示、侧边栏却有」这类症状，光看结果判断不出死在初始化流程的哪一段 ——
/// 构造过程里任何一步抛异常，后面的代码（包括建窗口）就全都不执行了。
/// 这里按顺序打点，<b>最后一条停在哪，就是死在哪</b>。
/// </para>
///
/// <para>
/// ⛔ 历史上这些打点单独写一份 <c>port.log</c>；现已并入会话目录的 <c>app.log</c>（模块名 <c>port</c>）——
/// 一个会话一个目录，出问题时不必再让用户额外找第二个文件。
/// 级别固定为<b>调试</b>：这些是过程打点，日志页把「调试」滤掉后剩下的才是结论性条目。
/// </para>
/// </summary>
internal static class PortLog
{
    /// <summary>记一步。stage 建议写成「模块: 动作」，方便扫。</summary>
    public static void Step(string stage)
    {
        try { Core.AppLog.Debug("port", stage); } catch { }
    }

    /// <summary>「这一步炸了」——带异常类型、HResult、消息与完整堆栈。</summary>
    public static void Fail(string stage, Exception? ex)
    {
        try
        {
            if (ex is null) Core.AppLog.Warn("port", stage);
            else Core.AppLog.Exception("port", ex, stage);
        }
        catch { }
    }
}
