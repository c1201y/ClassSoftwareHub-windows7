namespace ClassSoftwareHub.Desktop.Core;

/// <summary>
/// 自检工程的最小替身：被链接的纯计算源码在异常分支里会写应用日志，
/// 自检不加载 Avalonia 与日志宿主，因此这里提供空实现，仅用于满足编译。
/// 不参与任何断言逻辑。
/// </summary>
internal static class AppLog
{
    public static void Exception(string tag, Exception ex, string what) { }
    public static void Warn(string tag, string what) { }
    public static void Warning(string tag, string what) { }
    public static void Debug(string tag, string what) { }
    public static void Info(string tag, string what) { }

    public static void Catch(string tag, string what, Exception ex,
        ClassSoftwareHub.Desktop.Core.LogLevel level = ClassSoftwareHub.Desktop.Core.LogLevel.Debug)
    { }
}

/// <summary>日志级别（与主工程同名，供被链接源码编译通过）。</summary>
internal enum LogLevel
{
    Debug,
    Info,
    Warning,
    Error,
}
