using System;
using System.IO;
using System.Linq;
using System.Text;
using ClassSoftwareHub.Desktop.Services;

namespace ClassSoftwareHub.SelfCheck;

/// <summary>
/// 纯计算逻辑回归自检。断言失败返回非零退出码，供本地与 CI 直接判定。
/// </summary>
internal static class Program
{
    private static int _failed;

    private static void Check(string name, bool ok, string detail = "")
    {
        Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {name}{(detail.Length > 0 ? "  -> " + detail : "")}");
        if (!ok) _failed++;
    }

    private static int Main()
    {
        Console.WriteLine("ClassSoftwareHub · 纯计算逻辑回归自检");
        Console.WriteLine(new string('-', 60));

        // ── 审计 P0-1（第一轮）：剥电荷不得吞掉化学式下标数字 ────────
        // 二轮修正：旧断言写成 `!r.Ok || ...`，**只要报错就算通过**，
        // 于是「一价离子全被拒」这个回归被完美放过。现在改成两条独立断言：
        //   ① 歧义写法（Cr2O7- 这种）允许报错，但绝不能输出被截肢的式子；
        //   ② 出厂预置的 8 个离子根**必须全部可用**（这才是用户真实路径）。
        foreach (var (input, token) in new[]
        {
            ("Cr2O7- + Fe2+ + H+ = Cr3+ + Fe3+ + H2O", "Cr2O7"),
            ("NO3- + H+ = NO + H2O", "NO3"),
            ("SO4- + H+ = SO2 + H2O", "SO4"),
            ("MnO4- + Fe2+ + H+ = Mn2+ + Fe3+ + H2O", "MnO4"),
        })
        {
            var r = ChemistryBalancer.Balance(input);
            var normalized = r.Equation.Replace(" ", "");
            var safe = !r.Ok || normalized.Contains(token, StringComparison.Ordinal);
            Check($"化学歧义电荷不吞下标 [{token}]", safe,
                $"Ok={r.Ok} Eq={r.Equation} Err={r.Error}");
        }

        // ② 出厂预置的离子根必须逐个通过校验（ChemicalFavorites.DefaultIons）
        //    这 8 个是编辑器「常用离子根」面板的默认按钮，用户会直接点。
        foreach (var ion in new[] { "H^+", "OH^-", "SO4^2-", "CO3^2-", "NO3^-", "NH4^+", "Cl^-", "Fe^3+" })
        {
            Check($"出厂离子根可用 [{ion}]", ChemistryBalancer.ValidateFormula(ion, out var ionErr),
                ionErr ?? "");
        }

        // ③ 一价离子 / 多价离子的离子方程式必须真的配平出正确系数
        foreach (var (input, expected) in new[]
        {
            ("H^+ + OH^- = H2O", "H^+ + OH^- = H2O"),
            ("Cr2O7^2- + Fe^2+ + H^+ = Cr^3+ + Fe^3+ + H2O",
             "Cr2O7^2- + 6Fe^2+ + 14H^+ = 2Cr^3+ + 6Fe^3+ + 7H2O"),
            ("Fe^3+ + OH^- = Fe(OH)3", "Fe^3+ + 3OH^- = Fe(OH)3"),
        })
        {
            var r = ChemistryBalancer.Balance(input);
            Check($"离子方程式配平 [{input[..Math.Min(input.Length, 22)]}]",
                r.Ok && r.Equation == expected, $"实际={r.Equation} 期望={expected} Err={r.Error}");
        }

        // ④ 电荷守恒必须参与判定：Fe^2+ = Fe^3+ 电荷不平，绝不能报"配平成功"
        foreach (var bad in new[] { "Fe^2+ = Fe^3+", "Cu^2+ = Cu^+", "Cr^3+ = Cr^6+" })
        {
            var r = ChemistryBalancer.Balance(bad);
            Check($"电荷不守恒必须拒绝 [{bad}]", !r.Ok, r.Ok ? "错误地判定为配平成功：" + r.Equation : "");
        }

        // ⑤ 离子方程式的电荷核对表必须相等
        var chargeEq = ChemistryBalancer.Balance("MnO4^- + Fe^2+ + H^+ = Mn^2+ + Fe^3+ + H2O");
        Check("离子方程式电荷核对表左右相等",
            chargeEq.Ok && chargeEq.ChargeCheck is { } c && c.Left == c.Right,
            chargeEq.ChargeCheck?.ToString() ?? "null");

        // 常规方程式仍须正确配平，且守恒表逐元素相等。
        var basic = ChemistryBalancer.Balance("Fe + O2 = Fe2O3");
        Check("基础配平 Fe + O2 = Fe2O3",
            basic.Ok && basic.ElementCheck.Count > 0 && basic.ElementCheck.All(c => c.Left == c.Right),
            basic.Equation);

        // ── 审计 P0-2：二分收尾的相对量级判据 ────────────────────────
        // 大根残差天然大于 1e-6，用绝对阈值会把整类大根误判为「无实根」。
        // 二轮修正：旧断言只查 `Count > 0`，于是「只找到 1 个根」这个漏解被完美放过。
        // 现在必须断言**根的个数与数值都正确**。
        foreach (var (input, want) in new[]
        {
            ("x^2 = 10000000000", "x = −100000|x = 100000"),   // 根恰在扫描窗左端点
            ("x^2 = 1e10", "x = −100000|x = 100000"),
            ("x^2 = 1e12", "x = −1000000|x = 1000000"),        // 根在旧扫描窗之外
            ("x^2 = 100", "x = −10|x = 10"),
            ("x^2 - 5x + 6 = 0", "x = 2|x = 3"),
        })
        {
            var r = EquationSolver.Solve(input);
            var got = string.Join("|", r.Solutions);
            Check($"大根/常规根个数与数值 [{input}]", got == want, $"实际={got} 期望={want}");
        }

        // 极平坦函数不应刷出一屏假根。
        var flat = EquationSolver.Solve("sin(x)*0.000000000000001 = 0");
        Check("极平坦函数不刷假根", flat.Solutions.Count <= 12,
            $"共 {flat.Solutions.Count} 个根 / {flat.Note}");

        // ── 二轮 P0：科学计数法不得被当成变量名 ────────────────────
        // 旧实现把 `1e10` 读成 `1 × e10`（e10 是未知数），答"无穷多组解"且不报错。
        foreach (var (input, want) in new[]
        {
            ("x = 1e10", "x = 10000000000"),
            ("x = 1.5e3", "x = 1500"),
            ("x + 1e2 = 300", "x = 200"),
        })
        {
            var r = EquationSolver.Solve(input);
            var got = string.Join("|", r.Solutions);
            Check($"科学计数法正确求值 [{input}]", r.Ok && got == want, $"实际={got} 期望={want}");
        }

        // ── 二轮 P0：Validate / Solve 绝不能被解析异常打穿 ──────────
        // 旧实现 `x ^ 2` 会截出 " 2" 抛 FormatException、`x^999…` 抛 OverflowException，
        // 而两个 catch 只认 LinearParseException → 异常直穿到 UI 线程（Validate 边输边调）。
        foreach (var input in new[] { "x ^ a = 4", "x ^ (2) = 4", "x^999999999999999999999 = 4", "x ^ 2 = 4" })
        {
            var escaped = "";
            try
            {
                EquationSolver.Validate(input, out _);
                EquationSolver.Solve(input);
            }
            catch (Exception ex) { escaped = ex.GetType().Name + ": " + ex.Message; }
            Check($"解析异常不得穿出 Validate/Solve [{input}]", escaped.Length == 0, escaped);
        }

        // 恒等式（x^0 = 1）应报"任意实数"，不能只返回 x = 0 一个点。
        var identity = EquationSolver.Solve("x^0 = 1");
        Check("恒等式报任意实数", identity.Solutions.Count == 1 && identity.Solutions[0].Contains("任意"),
            string.Join("; ", identity.Solutions));

        // ── 审计 P1：提权脚本 TOCTOU 防护（独占共享句柄 + 回读校验 + 白名单）──
        CheckElevatedArtifact();

        Console.WriteLine(new string('-', 60));
        Console.WriteLine(_failed == 0 ? "全部通过。" : $"失败 {_failed} 项。");
        return _failed == 0 ? 0 : 1;
    }

    /// <summary>
    /// 审计 P1：提权脚本必须在「写完 → 点 UAC → 管理员进程读」整段里保持不可写、不可删。
    /// 这里直接验证 FileShare.Read 独占语义、回读内容与白名单拒绝。
    /// </summary>
    private static void CheckElevatedArtifact()
    {
        const string line = "reg add \"HKLM\\SOFTWARE\\CSH\" /v X /t REG_DWORD /d 1 /f";
        var script = "@echo off\r\n" + line + "\r\n";

        var artifact = ElevatedArtifact.Create("selftest", "cmd", Encoding.ASCII, script,
            l => l == "@echo off" || l == line);
        var path = artifact.Path;
        try
        {
            // 读脚本必须用「允许其他进程可写」的共享模式（FileShare.ReadWrite），
            // 与 cmd.exe / reg.exe 读取 .cmd/.reg 的真实共享模式一致；否则会与
            // ElevatedArtifact 持有写访问的 guard 句柄触发共享违例。
            string onDisk;
            using (var r = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var sr = new StreamReader(r, Encoding.ASCII))
                onDisk = sr.ReadToEnd();
            Check("提权脚本已落盘且内容逐字一致",
                File.Exists(path) && onDisk == script);

            var writeBlocked = false;
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
            }
            catch (IOException) { writeBlocked = true; }
            Check("持有期间其它进程无法覆写脚本", writeBlocked);

            var deleteBlocked = false;
            try { File.Delete(path); }
            catch (IOException) { deleteBlocked = true; }
            Check("持有期间其它进程无法删除脚本", deleteBlocked && File.Exists(path));
        }
        finally
        {
            artifact.Dispose();
        }

        Check("释放句柄后临时脚本已清理", !File.Exists(path));

        var rejected = false;
        try
        {
            ElevatedArtifact.Create("selftest", "cmd", Encoding.ASCII,
                "@echo off\r\n" + line + " & format C:\r\n",
                l => l == "@echo off" || l == line);
        }
        catch (InvalidOperationException) { rejected = true; }
        Check("行级白名单拒绝注入命令", rejected);

        var ctrlRejected = false;
        try
        {
            ElevatedArtifact.Create("selftest", "cmd", Encoding.ASCII,
                "@echo off\r" + line + "\r\n", null);
        }
        catch (InvalidOperationException) { ctrlRejected = true; }
        Check("拒绝脚本内的控制字符", ctrlRejected);
    }
}
