using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using ClassSoftwareHub.Desktop.Core;

namespace ClassSoftwareHub.Desktop.Services;

/// <summary>
/// 与 PowerPoint / WPS 演示的 COM 对接（2026-10-31 PPT 续播）。
///
/// <para>
/// ⚠️ 为什么不用 Microsoft.Office.Interop.PowerPoint：那套 PIA 只随完整 Office 安装，
///    教室机上也常是 WPS，用一个 PIA 反而两头都绑死。这里全部走 **late binding**
///    （<c>Type.GetTypeFromProgID</c> + <c>dynamic</c>），Office 与 WPS 的演示对象模型
///    在 Presentation / Slides / SlideShowSettings 这几个用到的成员上是兼容的。
/// </para>
///
/// <para>
/// ⚠️ 拿「正在运行的」PowerPoint 只能走 ROT（Running Object Table）：
///    .NET 6 没有 <c>Marshal.GetActiveObject</c>（那是 .NET Framework 才有的），
///    而且 <c>Activator.CreateInstance</c> 只会再开一个实例、拿不到用户正开着的那个。
/// </para>
///
/// <para>⛔ 所有 COM 调用都必须在 <see cref="PptResumeService"/> 的 STA 后台线程上执行。</para>
/// </summary>
internal static class OfficeCom
{
    private static readonly string[] ProgIds =
    {
        "PowerPoint.Application",
        "KWPP.Application",     // WPS 演示
        "WPP.Application",
    };

    private static readonly string[] ProcessNames = { "POWERPNT", "wpp", "wps" };
    private static readonly string[] RotPatterns = { "POWERPNT", "KWPP", "WPP" };

    /// <summary>机器上有没有演示软件的进程（没有就不必去扫 ROT）。</summary>
    public static bool AnyRunning()
    {
        foreach (var name in ProcessNames)
        {
            try
            {
                var list = Process.GetProcessesByName(name);
                if (list.Length == 0) continue;

                foreach (var item in list) item.Dispose();
                return true;
            }
            catch { }
        }
        return false;
    }

    /// <summary>拿到用户正在用的那个演示程序；没有就返回 null。</summary>
    public static object? TryGetRunning() => RunningObject.Get(RotPatterns);

    /// <summary>启动一个演示程序（PowerPoint 优先，其次 WPS）；都装不上返回 null。</summary>
    public static object? Start()
    {
        foreach (var progId in ProgIds)
        {
            try
            {
                var type = Type.GetTypeFromProgID(progId);
                if (type is null) continue;

                var instance = Activator.CreateInstance(type);
                if (instance is null) continue;

                AppLog.Info("ppt-resume", "已启动演示程序：" + progId);
                return instance;
            }
            catch (Exception ex)
            {
                AppLog.Debug("ppt-resume", "启动 " + progId + " 失败: " + ex.Message);
            }
        }

        return null;
    }

    /// <summary>是不是演示程序的 Application 对象（ROT 里也可能有别的 Office 条目）。</summary>
    public static bool LooksLikePresentationApp(object comObject)
    {
        try
        {
            dynamic app = comObject;
            var name = (string)app.Name;
            return name.IndexOf("PowerPoint", StringComparison.OrdinalIgnoreCase) >= 0
                   || name.IndexOf("WPS", StringComparison.OrdinalIgnoreCase) >= 0
                   || name.IndexOf("演示", StringComparison.Ordinal) >= 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>释放一个自己拿到的 COM 对象（ROT 里返回的对象不要转手给别人）。</summary>
    public static void Release(object? comObject)
    {
        if (comObject is null) return;
        try
        {
            if (Marshal.IsComObject(comObject)) Marshal.ReleaseComObject(comObject);
        }
        catch { }
    }
}

/// <summary>跑表（Running Object Table）枚举 —— 找正在运行的 Office 实例。</summary>
internal static class RunningObject
{
    [DllImport("ole32.dll")]
    private static extern int GetRunningObjectTable(int reserved, out IRunningObjectTable table);

    [DllImport("ole32.dll")]
    private static extern int CreateBindCtx(int reserved, out IBindCtx ctx);

    public static object? Get(string[] patterns)
    {
        IRunningObjectTable? table = null;
        IEnumMoniker? enumerator = null;
        IBindCtx? ctx = null;
        var moniker = new IMoniker[1];

        try
        {
            if (GetRunningObjectTable(0, out table) != 0 || table is null) return null;
            if (CreateBindCtx(0, out ctx) != 0 || ctx is null) return null;

            table.EnumRunning(out enumerator);
            if (enumerator is null) return null;
            enumerator.Reset();

            while (enumerator.Next(1, moniker, IntPtr.Zero) == 0)
            {
                var current = moniker[0];
                moniker[0] = null!;
                if (current is null) continue;

                try
                {
                    string? display = null;
                    try { current.GetDisplayName(ctx, null, out display); } catch { }
                    if (string.IsNullOrEmpty(display) || !Matches(display, patterns)) continue;

                    object? found = null;
                    try { table.GetObject(current, out found); } catch { }
                    if (found is null) continue;

                    // ⚠️ ROT 里也可能有文档等别的条目：确认拿到的是演示程序的 Application
                    if (OfficeCom.LooksLikePresentationApp(found)) return found;

                    OfficeCom.Release(found);
                }
                finally
                {
                    OfficeCom.Release(current);
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Debug("ppt-resume", "枚举运行中的 Office 失败: " + ex.Message);
        }
        finally
        {
            OfficeCom.Release(enumerator);
            OfficeCom.Release(ctx);
            OfficeCom.Release(table);
        }

        return null;
    }

    private static bool Matches(string display, string[] patterns)
    {
        foreach (var pattern in patterns)
        {
            if (display.IndexOf(pattern, StringComparison.OrdinalIgnoreCase) >= 0) return true;
        }
        return false;
    }
}
