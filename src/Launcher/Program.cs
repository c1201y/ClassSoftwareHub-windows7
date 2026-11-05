using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace ClassSoftwareHub.Launcher;

/// <summary>
/// 便携版 / 安装版的启动器。
///
/// 职责只有一条：把 <c>app\ClassSoftwareHub.exe</c> 拉起来，然后把这次命令行参数原样转发过去。
/// 启动器自己**不常驻**、不做单实例判断（那是应用本体的事）、不等待子进程退出 ——
/// 拉起就走，任务栏上留下的那个图标始终是应用自己的。
/// </summary>
internal static class Program
{
    /// <summary>应用本体所在的子目录名。便携版与安装版都是这个结构。</summary>
    private const string AppFolderName = "app";

    /// <summary>应用本体的文件名（与启动器同名，但不同目录）。</summary>
    private const string AppExeName = "ClassSoftwareHub.exe";

    // 启动器没有 UI 框架（不带 Avalonia / WinForms），出错时只能借系统的对话框。
    private const uint MB_OK = 0x00000000;
    private const uint MB_ICONERROR = 0x00000010;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);

    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            // ⚠️ 单文件发布时 AppContext.BaseDirectory 给的是**这个 exe 所在目录**（不是解压临时目录），
            //    正是我们要的"启动器在哪儿，app\ 就在哪儿找"。
            var root = AppContext.BaseDirectory;

            var appDir = Path.Combine(root, AppFolderName);
            var appExe = Path.Combine(appDir, AppExeName);

            // 兜底：万一有人手工把应用本体铺在了启动器旁边（没有 app\ 子目录），也照常启动 ——
            // 免得用户只是因为解压姿势不同就得到一个"点了没反应"。
            if (!File.Exists(appExe))
            {
                var flat = Path.Combine(root, AppExeName);
                // ⛔ flat 必须不是启动器自己：在 dist\win7-launcher 这种「只有启动器、没有 app\」的
                //    目录里原地双击时，flat 就是本 exe —— 不挡住的话它会拉起自己，一代一代永不停
                //    （2026-10-27 实测：进程列表里 launcher 链自己续命，怎么杀都杀不完）。
                var ownPath = Environment.ProcessPath;
                if (File.Exists(flat)
                    && !string.Equals(ownPath, flat, StringComparison.OrdinalIgnoreCase))
                {
                    appDir = root;
                    appExe = flat;
                }
            }

            if (!File.Exists(appExe))
            {
                Fail(
                    "找不到应用程序本体，无法启动。" + Environment.NewLine + Environment.NewLine +
                    "期望的位置：" + Environment.NewLine + appExe + Environment.NewLine + Environment.NewLine +
                    "便携版是「启动器 + app 文件夹」两层结构，" +
                    "请确认解压时把 app 文件夹一并保留了下来（不要只单独拿走 exe）。");
                return 2;
            }

            var psi = new ProcessStartInfo(appExe)
            {
                // 工作目录设到 app\：应用自己是按 AppContext.BaseDirectory 定位资源的，
                // 但把工作目录也摆正，能顺带兜住任何"相对路径"的写法。
                WorkingDirectory = appDir,
                UseShellExecute = false,
            };

            // 用 ArgumentList 而不是拼字符串 —— 引号/空格的转义交给运行时，省得自己写错。
            foreach (var a in args)
                psi.ArgumentList.Add(a);

            // ⛔ 崩溃转储开关必须由启动器来设：托管层的访问冲突在 .NET 6 上由 CLR 自己接管
            //    （FailFast），绕过 WER 的 LocalDumps —— 只有 CLR 自带的 createdump 能留下现场。
            //    而这些开关是 CLR 启动时读的，子进程起来之后再设置就晚了，所以只能在拉起之前注入。
            //
            //    应用本体里也有一份同样的设置（见 Platform/CrashDump.ConfigureRuntimeDump），
            //    那份是给「直接双击 app\ 里的 exe」兜底的；这里设置的值优先级更高。
            ConfigureCrashDump(psi);

            Process.Start(psi);
            return 0;
        }
        catch (Exception ex)
        {
            Fail("启动失败：" + ex.Message);
            return 1;
        }
    }

    /// <summary>
    /// 给子进程注入 .NET 崩溃转储开关。
    ///
    /// <para>
    /// 这条链路解决的是「程序有时候直接没了、日志里什么都没有」：托管代码里的访问冲突、
    /// 栈溢出、<c>FailFast</c> 都不会经过 CLR 的托管异常钩子，进程当场结束。
    /// 没有转储就只剩一句「崩了」，无从查起。
    /// </para>
    ///
    /// <para>
    /// ⚠️ 必须在 <see cref="Process.Start(ProcessStartInfo)"/> 之前写进
    /// <c>psi.EnvironmentVariables</c>：CLR 是在启动阶段读这些变量的，进程起来之后再设无效。
    /// 任何一步失败都不影响启动 —— 转储是排障手段，不是运行前提。
    /// </para>
    /// </summary>
    private static void ConfigureCrashDump(ProcessStartInfo psi)
    {
        try
        {
            var dumpDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ClassSoftwareHub", "crashes");

            try { Directory.CreateDirectory(dumpDir); } catch { /* 建不出来就让 CLR 自己处理失败 */ }

            psi.EnvironmentVariables["DOTNET_DbgEnableMiniDump"] = "1";
            // 1 = MiniDumpNormal：含线程栈与模块列表，足以定位崩在哪一行；
            // 教室机磁盘紧张，默认不抓完整堆（完整转储动辄数百兆）。
            psi.EnvironmentVariables["DOTNET_DbgMiniDumpType"] = "1";
            // %p 会被 CLR 替换成进程号，多开时不互相覆盖。
            psi.EnvironmentVariables["DOTNET_DbgMiniDumpName"] = Path.Combine(dumpDir, "csh.%p.dmp");
        }
        catch
        {
            // 转储配置失败绝不能让程序起不来。
        }
    }

    /// <summary>弹一个系统级错误框（不依赖任何 UI 框架）。</summary>
    private static void Fail(string message)
    {
        try { MessageBoxW(IntPtr.Zero, message, "ClassSoftwareHub", MB_OK | MB_ICONERROR); }
        catch { /* 连对话框都弹不出来就只能静默失败，没有更好的办法 */ }
    }
}
