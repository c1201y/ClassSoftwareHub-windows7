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
                if (File.Exists(flat))
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

            Process.Start(psi);
            return 0;
        }
        catch (Exception ex)
        {
            Fail("启动失败：" + ex.Message);
            return 1;
        }
    }

    /// <summary>弹一个系统级错误框（不依赖任何 UI 框架）。</summary>
    private static void Fail(string message)
    {
        try { MessageBoxW(IntPtr.Zero, message, "ClassSoftwareHub", MB_OK | MB_ICONERROR); }
        catch { /* 连对话框都弹不出来就只能静默失败，没有更好的办法 */ }
    }
}
