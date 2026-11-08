using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using ClassSoftwareHub.Desktop.Core;
using ClassSoftwareHub.Desktop.Data.Tweaks;
using Microsoft.Win32;

namespace ClassSoftwareHub.Desktop.Services;

/// <summary>
/// 一次「应用更改」的结果。
/// <para>
/// <see cref="Failed"/> 是<b>改完之后重读、状态仍与目标不一致</b>的那几项的 Id ——
/// 界面靠它把没成功的项留在待应用队列里，而不是一股脑清空（那样用户就再也找不到是哪几项没成）。
/// </para>
/// </summary>
public sealed record TweakApplyResult(
    int Requested,
    int Matched,
    string? Error,
    IReadOnlyList<string> Failed)
{
    public bool Ok => Error is null;
}

/// <summary>
/// 系统优化项的读写（自上游移植）。
///
/// <para>
/// ⭐ 核心策略：<b>当前用户直写、机器级才提权</b>。界面类项都在 <c>HKCU</c>，切换只是排队，
/// 点「应用更改」时逐项直接写入，<b>不弹 UAC</b>；只有 <c>Scope = System</c> 的机器级项才合成一份
/// <c>.reg</c> 用 <c>reg.exe import</c> 提权执行<b>一次</b>（本页目前没有这类项，它们在「系统功能」页）。
/// </para>
/// <para>
/// ⚠️ 成败判定看<b>改完之后重新读到的值</b>，不看 <c>reg.exe</c> 的返回值 ——
/// 经 ShellExecute 提权启动的进程拿不到可靠退出码，用户点「否」和执行失败在代码里长得一样。
/// </para>
/// <para>
/// ⛔ 临时 .reg 文件写在数据目录下、用完即删：里面只有键值，没有用户数据，但别留在桌面或临时目录
/// 让用户看见一个来路不明的文件。
/// </para>
/// </summary>
public static class TweakStore
{
    /// <summary>
    /// 读当前值；读不到 / 没写过 / 类型不认识都返回 <c>null</c>。
    /// <para>⚠️ 判定"开了没有"请用 <see cref="EffectiveValue"/>，别拿 <c>null</c> 直接和数字比。</para>
    /// </summary>
    public static int? ReadValue(TweakDef def)
    {
        try
        {
            using var root = RegistryKey.OpenBaseKey(
                def.Scope == TweakScope.System ? RegistryHive.LocalMachine : RegistryHive.CurrentUser,
                RegistryView.Default);

            using var key = root.OpenSubKey(def.KeyPath, writable: false);
            var raw = key?.GetValue(def.ValueName);

            // 值缺失 = 系统还没写过这一项，按关态（OffValue）理解
            if (raw is null) return null;

            // ⚠️ 类型宽松一点：DWORD 正常是 int，但导入的 .reg 或旧软件可能留成 long / string。
            //    解析规则抽在 Core/Tweaks/TweakRules.cs。
            var value = TweakRules.ParseDword(raw);

            if (value is null)
                AppLog.Warn("tweaks", $"优化项值类型不支持｜项目={def.Id}｜位置={def.FullPath}"
                + $"｜实际类型={raw.GetType().Name}｜处置=按关闭处理");

            return value;
        }
        catch (Exception ex)
        {
            AppLog.Catch("tweaks", $"读取优化项状态｜项目={def.Id}｜位置={def.FullPath}", ex);
            return null;
        }
    }

    /// <summary>
    /// 界面上应当显示成哪个值：值缺失时按 <see cref="TweakDef.OffValue"/> 解释（与 Dism++ 的
    /// <c>&lt;Applicable&gt;</c> 兜底同一口径）。下拉框与开关都按它摆初始状态。
    /// </summary>
    public static int EffectiveValue(TweakDef def) => ReadValue(def) ?? def.OffValue;

    /// <summary>复选框语义：当前值等于 <see cref="TweakDef.OnValue"/> 视为"开"。</summary>
    public static bool IsOn(TweakDef def) => EffectiveValue(def) == def.OnValue;

    /// <summary>
    /// 整批应用。<paramref name="changes"/> 是 (项, 目标状态)。
    /// <para>返回里 <c>Requested</c> 是要改几项、<c>Matched</c> 是改完之后<b>实际读到的状态与目标一致</b>的项数。</para>
    /// </summary>
    public static async Task<TweakApplyResult> ApplyAsync(IReadOnlyList<(TweakDef Def, int Value)> changes)
    {
        if (changes.Count == 0) return new TweakApplyResult(0, 0, null, Array.Empty<string>());

        // 界面类设置全在 HKCU，直接写、不弹 UAC；只有真正机器级的项才需要提权。
        var userChanges = changes.Where(c => c.Def.Scope == TweakScope.User).ToList();
        var systemChanges = changes.Where(c => c.Def.Scope == TweakScope.System).ToList();

        string? error = null;

        // ── ① 当前用户：直接写 ──
        foreach (var (def, value) in userChanges)
        {
            try
            {
                WriteUser(def, value);
            }
            catch (Exception ex)
            {
                AppLog.Catch("tweaks", $"写入优化项｜项目={def.Id}｜位置={def.FullPath}", ex);
                error ??= ex.Message;
            }
        }

        AppLog.Info("tweaks",
            $"应用优化项｜数量={changes.Count}｜当前用户={userChanges.Count}｜机器级={systemChanges.Count}"
            + $"｜项目={string.Join("、", changes.Select(c => c.Def.Id))}");

        // ── ② 机器级：整批一次提权 ──
        if (systemChanges.Count > 0)
        {
            try
            {
                // ⚠️ 2026-11-07：原来的 tweaks-HHmmss.reg 文件名可预测，且直接 WriteAllText
                //    到数据目录，写完到点 UAC 之间可被同用户进程覆写 —— 覆写后就是
                //    「以管理员身份导入任意注册表」。现在改成随机路径 + 原子落盘 +
                //    FileShare.Read 持有句柄到提权进程退出 + 回读校验 + .reg 行级白名单。
                //    .reg 必须是带 BOM 的 UTF-16LE，reg.exe 才认得。
                using var artifact = ElevatedArtifact.Create(
                    "csh-tweaks", "reg", new UnicodeEncoding(false, true),
                    BuildRegFile(systemChanges), IsAllowedRegLine);

                var psi = new ProcessStartInfo("reg.exe", $"import \"{artifact.Path}\"")
                {
                    Verb = "runas",              // 只有这一路会弹 UAC
                    UseShellExecute = true,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                };

                using var proc = Process.Start(psi);
                if (proc is not null) await proc.WaitForExitAsync();
            }
            catch (Exception ex)
            {
                // 用户点了「否」也走这里（Win32Exception：操作已被用户取消）
                AppLog.Catch("tweaks", "应用机器级优化项", ex);
                error ??= ex.Message;
            }
        }

        // ── ③ 通知外壳重读：任务栏 / 桌面各发一条，同一类只发一次 ──
        // 不再靠杀 explorer 让改动"显形"（照 Dism++ 的 ExplorerNotify 做法）。
        // 放到后台线程：SendMessageTimeout 最长能等 1 秒，别卡住界面。
        var refreshes = TweakRules.RefreshesFor(changes);

        if (refreshes.Count > 0)
        {
            await Task.Run(() =>
            {
                foreach (var kind in refreshes)
                {
                    if (kind == TweakRefresh.Tray) ShellNotify.TraySettings();
                    else if (kind == TweakRefresh.Desktop) ShellNotify.RefreshDesktop();
                }
            });
        }

        // 成败一律按改完之后<b>重新读到的值</b>判定，不看命令返回值
        var failed = new List<string>();
        foreach (var (def, value) in changes)
            if (EffectiveValue(def) != value) failed.Add(def.Id);

        var matched = changes.Count - failed.Count;

        AppLog.Info("tweaks",
            matched == changes.Count
                ? $"优化项应用｜结果=全部生效｜{matched}/{changes.Count}"
                : $"优化项应用｜结果=部分未生效｜已生效={matched}/{changes.Count}"
                  + $"｜未生效项={string.Join("、", failed)}");

        return new TweakApplyResult(changes.Count, matched, error, failed);
    }

    /// <summary>写当前用户的一个 DWORD。界面类设置都走这里，不需要管理员。</summary>
    private static void WriteUser(TweakDef def, int value)
    {
        using var root = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Default);

        // 关掉时要"恢复成没配置过"的项：直接删值，而不是留一个 0。
        // ⚠️ 这里刻意<b>不建键</b>：键本来就不存在时，创建一个空键只是多留垃圾，
        //    而"没有这个键"本身已经等于关态了。所以先按可写方式打开，打不开就直接收工。
        if (def.DeleteWhenOff && value == def.OffValue)
        {
            using var existing = root.OpenSubKey(def.KeyPath, writable: true);
            existing?.DeleteValue(def.ValueName, throwOnMissingValue: false);
            return;
        }

        using var key = root.CreateSubKey(def.KeyPath, writable: true)
                        ?? throw new InvalidOperationException($"打不开注册表键：{def.KeyPath}");

        key.SetValue(def.ValueName, value, RegistryValueKind.DWord);
    }

    /// <summary>
    /// 生成 .reg 文本。<c>UnicodeEncoding(false, true)</c> 写出来是带 BOM 的 UTF-16LE ——
    /// <c>reg.exe import</c> 对编码敏感，ANSI/UTF-8 遇到中文键值会乱码甚至导入失败。
    /// <para>生成规则本体在 <see cref="TweakRules.BuildRegText"/>（纯函数）。</para>
    /// </summary>
    public static string BuildRegFile(IReadOnlyList<(TweakDef Def, int Value)> changes) =>
        TweakRules.BuildRegText(changes);

    /// <summary>
    /// 提权导入前的 .reg 行级白名单：只认 <c>TweakRules</c> 会生成的四种行。
    /// 机器级改动一律落在 HKEY_LOCAL_MACHINE；<c>[-HKEY...]</c> 删除节、注释、
    /// 任意根键、字符串值等都不放行 —— 防止以后有人把用户输入的键路径拼进 TweakDef。
    /// </summary>
    private static bool IsAllowedRegLine(string line)
    {
        if (line == "Windows Registry Editor Version 5.00") return true;

        if (line.StartsWith("[", StringComparison.Ordinal))
            return line.StartsWith("[HKEY_LOCAL_MACHINE\\", StringComparison.OrdinalIgnoreCase)
                   && line.EndsWith("]", StringComparison.Ordinal)
                   && line.IndexOf(']') == line.Length - 1
                   && line.IndexOfAny(new[] { '"', '=', ';' }) < 0;

        // 值行："值名"=dword:8 位十六进制 或 "值名"=-（DeleteWhenOff）
        if (!line.StartsWith("\"", StringComparison.Ordinal)) return false;
        var eq = line.LastIndexOf("\"=", StringComparison.Ordinal);
        if (eq < 1) return false;

        var rhs = line[(eq + 2)..];
        if (rhs == "-") return true;
        return rhs.StartsWith("dword:", StringComparison.OrdinalIgnoreCase)
               && rhs.Length == 14
               && rhs.Skip(6).All(Uri.IsHexDigit);
    }

    /// <summary>
    /// 重启资源管理器（任务栏）。⚠️ 会关掉当前打开的所有资源管理器窗口。
    /// 先结束再拉起：只结束不拉起的话，桌面上只剩壁纸，用户会以为系统坏了。
    /// </summary>
    public static void RestartExplorer()
    {
        try
        {
            AppLog.Info("tweaks", "重启资源管理器｜目的=使优化项生效");

            using (var kill = Process.Start(new ProcessStartInfo("taskkill.exe", "/f /im explorer.exe")
                   {
                       UseShellExecute = false,
                       CreateNoWindow = true,
                   }))
            {
                kill?.WaitForExit(5000);
            }

            // ⚠️ 等它真的退干净再拉，否则新实例会立刻被判为"已在运行"而直接退出
            for (var i = 0; i < 20 && Process.GetProcessesByName("explorer").Length > 0; i++)
                System.Threading.Thread.Sleep(100);

            Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppLog.Exception("tweaks", ex, "重启资源管理器");
        }
    }
}
