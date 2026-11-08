using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace ClassSoftwareHub.Desktop.Services;

/// <summary>
/// 方程求解：一元方程（数值求根，支持 π / 三角函数 / 对数）与一次方程组（精确分数消元）。
///
/// <para>
/// 判定顺序很重要：**先按一次式解析**，全部成功就走进精确消元 —— 这样 `2x+1=5` 得到的是
/// 精确的 `x = 2` 而不是 `1.9999999997`。只有解析不成一次式（含平方、sin、ln…）才退到数值求根。
/// </para>
/// <para>
/// ⛔ 多元高次方程组（如圆与直线的交点）这一版不做：那是非线性方程组的数值解，
/// 多解、漏解、收敛域都得另做一套，混进来只会给出看着对、其实缺解的结果。
/// </para>
/// </summary>
public static class EquationSolver
{
    public sealed class Result
    {
        public bool Ok => Error is null;
        public string? Error { get; init; }
        /// <summary>「一元方程」/「一次方程组」，给界面当小标题。</summary>
        public string Kind { get; init; } = "";
        /// <summary>每行一条结论，例如 `x = 2`、`y = 3/2`。</summary>
        public List<string> Solutions { get; init; } = new();
        /// <summary>补充说明（近似、无解、无穷多解等）。</summary>
        public string? Note { get; init; }
    }

    /// <summary>
    /// 校验**一条**方程能不能被理解（界面边输边用，决定「确定」能不能点）。
    ///
    /// <para>
    /// 判据和 <see cref="Solve"/> 完全一致：先试一次式（多未知数都行），
    /// 不行再试一元数值式子（只认 x）—— 界面上的"合法"必须等于求解器真的算得出来，
    /// 否则会出现「按钮能点、点了报错」这种最烦人的情况。
    /// </para>
    /// </summary>
    public static bool Validate(string? equation, out string? error)
    {
        var text = (equation ?? "").Trim();

        if (text.Length == 0)
        {
            error = "方程不能为空。";
            return false;
        }

        var sides = text.Split('=');
        if (sides.Length != 2)
        {
            error = sides.Length < 2 ? "还差一个等号，写成「左边 = 右边」。" : "等号只能有一个。";
            return false;
        }

        var left = sides[0].Trim();
        var right = sides[1].Trim();

        if (left.Length == 0 || right.Length == 0)
        {
            error = "等号两侧都要有内容。";
            return false;
        }

        string? linearError = null;
        try
        {
            LinearFormParser.Parse(left);
            LinearFormParser.Parse(right);
            error = null;
            return true;
        }
        catch (LinearParseException ex)
        {
            linearError = ex.Message;
        }
        catch (Exception ex)
        {
            // ⛔ 兜住其它意外异常（FormatException / OverflowException / ArgumentOutOfRange…）：
            //    Validate 是**边输边调**的，任何漏网异常都会冒到 UI 线程把页面打白。
            //    解析器的两个 catch 只认 LinearParseException，这类异常以前是直接穿出去的。
            linearError = ex.Message;
        }

        // 一元方程走数值求根这条路，变量只能是 x
        if (ExpressionParser.TryCompile($"({left}) - ({right})", AngleMode.Radians, out var fn, out var numericError)
            && fn is not null)
        {
            error = null;
            return true;
        }

        error = string.IsNullOrWhiteSpace(numericError)
            ? $"式子看不懂：{linearError}"
            : $"式子看不懂：{numericError}（一元方程的未知数请用 x）";

        return false;
    }

    public static Result Solve(string input)
    {
        var lines = (input ?? "")
            .Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith('#'))
            .ToList();

        if (lines.Count == 0) return Fail("请输入方程，每行一个，例如 x^2 - 4 = 0");

        // ── ① 先按一次式解析 ──
        var equations = new List<(LinearForm Left, LinearForm Right)>();
        string? linearError = null;

        foreach (var line in lines)
        {
            var sides = line.Split('=');
            if (sides.Length != 2)
            {
                linearError = "每行要写成「左边 = 右边」的形式。";
                break;
            }

            var left = sides[0].Trim();
            var right = sides[1].Trim();
            if (left.Length == 0 || right.Length == 0)
            {
                linearError = "等号两侧都要有内容。";
                break;
            }

            try
            {
                equations.Add((LinearFormParser.Parse(left), LinearFormParser.Parse(right)));
            }
            catch (LinearParseException ex)
            {
                linearError = ex.Message;
                break;
            }
            catch (Exception ex)
            {
                // 同 Validate：任何漏网异常都不能穿出 Solve。
                linearError = ex.Message;
                break;
            }
        }

        if (linearError is null) return SolveLinear(equations);

        // ── ② 一次式解析失败 → 只有当它是**单个未知数的一元方程**才走数值求根 ──
        if (lines.Count == 1) return SolveNumeric(lines[0], linearError);

        return Fail("多元的非一次方程组这一版还不支持。一次方程组可以每行写一个方程，" +
                    "一元方程请用 x 作未知数（例如 x^2 - 5x + 6 = 0）。");
    }

    private static Result Fail(string message) => new() { Error = message };

    // ══════════════════════════════════════════════════════
    //  一次方程组：精确分数高斯消元
    // ══════════════════════════════════════════════════════

    private static Result SolveLinear(List<(LinearForm Left, LinearForm Right)> equations)
    {
        var variables = equations
            .SelectMany(e => e.Left.Coeffs.Keys.Concat(e.Right.Coeffs.Keys))
            .Distinct()
            .OrderBy(v => v, StringComparer.Ordinal)
            .ToList();

        if (variables.Count == 0)
            return Fail("方程里没有未知数。");

        var rows = equations.Count;
        var matrix = new Rational[rows][];
        var rhs = new Rational[rows];

        for (var r = 0; r < rows; r++)
        {
            var (left, right) = equations[r];
            matrix[r] = new Rational[variables.Count];

            for (var c = 0; c < variables.Count; c++)
            {
                var name = variables[c];
                matrix[r][c] = left[name] - right[name];
            }

            // 常数项移到右边：左边常数 − 右边常数，取反
            rhs[r] = right.Constant - left.Constant;
        }

        var solution = LinearSolver.Solve(matrix, rhs);

        if (!solution.Consistent)
            return new Result
            {
                Kind = "一次方程组",
                Note = "把各方程对照一遍：它们互相矛盾（例如同时要求 x = 1 和 x = 2），因此**无解**。",
            };

        if (solution.Unique)
        {
            var lines = new List<string>();
            for (var i = 0; i < variables.Count; i++)
                lines.Add($"{variables[i]} = {Format(solution.Particular[i])}");

            return new Result
            {
                Kind = variables.Count == 1 ? "一元一次方程" : "一次方程组",
                Solutions = lines,
                Note = "结果为精确值（分数形式表示未除尽的有理数）。",
            };
        }

        return DescribeInfinite(variables, solution);
    }

    /// <summary>无穷多解：把自由变量取成参数 t1、t2…，其余变量用它表示。</summary>
    private static Result DescribeInfinite(List<string> variables, SolveResult solution)
    {
        var pivotSet = new HashSet<int>(solution.PivotColumns);
        var free = new List<int>();
        var isPivot = new bool[variables.Count];

        for (var i = 0; i < variables.Count; i++)
        {
            isPivot[i] = pivotSet.Contains(i);
            if (!isPivot[i]) free.Add(i);
        }

        var parameters = free.Select((_, i) => $"t{i + 1}").ToList();
        var lines = new List<string>();

        for (var i = 0; i < variables.Count; i++)
        {
            if (!isPivot[i])
            {
                lines.Add($"{variables[i]} = {parameters[free.IndexOf(i)]}");
                continue;
            }

            var sb = new StringBuilder($"{variables[i]} = {Format(solution.Particular[i])}");

            for (var k = 0; k < free.Count; k++)
            {
                var coeff = solution.NullBasis[k][i];
                if (coeff.IsZero) continue;

                var term = coeff == Rational.One
                    ? parameters[k]
                    : coeff == -Rational.One
                        ? "−" + parameters[k]
                        : Format(coeff) + parameters[k];

                sb.Append(coeff.Sign > 0 ? " + " : " − ");
                sb.Append(coeff.Sign > 0 ? term : term.TrimStart('−'));
            }

            lines.Add(sb.ToString());
        }

        return new Result
        {
            Kind = "一次方程组",
            Solutions = lines,
            Note = $"未知数比独立方程多，有**无穷多组解**。上面是通解，{string.Join("、", parameters)} 可任意取值。",
        };
    }

    // ══════════════════════════════════════════════════════
    //  一元方程：数值求根
    // ══════════════════════════════════════════════════════

    private static Result SolveNumeric(string line, string linearError)
    {
        var sides = line.Split('=');
        if (sides.Length != 2) return Fail("每行要写成「左边 = 右边」的形式。");

        // 与一次式解析共用一条式子，但不能被它挡下来：含 sin / ln 的式子只能数值算
        var text = $"({sides[0].Trim()}) - ({sides[1].Trim()})";

        if (!ExpressionParser.TryCompile(text, AngleMode.Radians, out var fn, out var error) || fn is null)
        {
            return Fail(string.IsNullOrWhiteSpace(error)
                ? linearError
                : $"式子看不懂：{error}（一元方程请用 x 作未知数）");
        }

        var (roots, capped) = FindRoots(fn);

        if (roots.Count == 0)
        {
            // ⛔ 区分"真的无解"和"整条曲线都算不出来"（例如 x^999999999 这类
            //    指数巨大、几乎处处溢出为 ∞ 的式子）。后者必须明确报错，
            //    否则用户看到的是 `Ok=true` + 空白解列表，以为程序正常但没解。
            if (!HasFiniteSample(fn))
            {
                return Fail("这个方程在实数范围内几乎处处无定义（数值溢出），无法求根。" +
                            "请检查指数是否过大。");
            }

            return new Result
            {
                Kind = "一元方程",
                Note = "在常用范围内没有找到实根（可能是无解，或根非常稀疏）。",
            };
        }

        // 周期函数有无穷多个解，全列出来没意义：只给靠近原点的那几个，并说明白
        var shown = roots;
        string note;

        if (capped && roots.Count == 1 && roots[0] == 0)
        {
            // Scan 里 magnitude == 0 的分支（f 在采样点上恒等于 0，如 x^0 = 1）：
            // 解集是**全体实数**，不该显示成 "x = 0" 这一个点。
            return new Result
            {
                Kind = "一元方程",
                Solutions = new List<string> { "x = 任意实数" },
                Note = "这是一个恒等式：等号两边在实数范围内完全相等，因此**任意实数都是解**。",
            };
        }

        if (capped || roots.Count > 12)
        {
            // 先挑**离原点最近**的 6 个，再按大小排 —— 直接取前 6 个会全是负数侧的根
            var near = roots.Where(r => Math.Abs(r) <= 10)
                            .OrderBy(r => Math.Abs(r))
                            .Take(6)
                            .OrderBy(r => r)
                            .ToList();
            shown = near.Count > 0 ? near : roots.OrderBy(r => Math.Abs(r)).Take(6).OrderBy(r => r).ToList();
            note = "该方程在实数范围内有大量解（周期函数通常有无穷多组），上表只列出靠近原点的若干个。" +
                   "通解需按函数的周期写出。";
        }
        else
        {
            // ⛔ 旧文案写「已按方程本身的性质求根，与窗口大小无关」—— 这句话是假的：
            //    能不能找到根、找到几个，完全取决于 FindRoots 的几个固定扫描窗
            //    （±10 / ±100 / ±1000 / ±1e5 / ±1e6）。跟用户说实话，才不会让人
            //    以为"只有这一个根"。
            note = roots.Count == 1
                ? "在常用范围内找到 1 个实根（已扫描 ±1e6；根非常稀疏或落在更大范围时可能还有其它解）。"
                : $"共 {roots.Count} 个实根。";
        }

        var labels = shown.Select(r => $"x = {FormatRoot(r)}").ToList();

        return new Result
        {
            Kind = "一元方程",
            Solutions = labels,
            Note = note,
        };
    }

    private static double Eval(Func<double, double> f, double x)
    {
        try { return f(x); }
        catch { return double.NaN; }
    }

    /// <summary>
    /// 这个函数在常用范围内是否至少有一个有限值。
    /// 用来区分「真的无实根」和「整条式子数值溢出、处处算不出来」——
    /// 后者（如 x^999999999）必须明确报错，而不是安静地返回空解列表。
    /// </summary>
    private static bool HasFiniteSample(Func<double, double> f)
    {
        foreach (var x in new[] { 0.0, 1.0, -1.0, 0.5, 2.0, 10.0, -10.0, 100.0, -100.0 })
        {
            var y = Eval(f, x);
            if (double.IsFinite(y)) return true;
        }
        return false;
    }

    /// <summary>
    /// 由近及远多尺度扫描 + 二分细化。
    ///
    /// ⚠️ 顺序必须是"先扫原点附近"：周期函数（sin(x)=0.5）会有无穷多个根，
    /// 从 −1e5 开始扫会把解全找在西边几百个单位外，列表里看不到人真正关心的那几个。
    /// ⚠️ 抓到 64 个就停：再扫下去只是浪费时间，界面也放不下。
    /// </summary>
    private static (List<double> Roots, bool Capped) FindRoots(Func<double, double> f)
    {
        var roots = new List<double>();
        const int cap = 64;
        var capped = false;

        void Push(double v)
        {
            if (double.IsNaN(v) || double.IsInfinity(v)) return;
            foreach (var existing in roots)
            {
                var tol = 1e-6 * Math.Max(1, Math.Abs(existing));
                if (Math.Abs(existing - v) < tol) return;
            }

            roots.Add(v);
        }

        void Scan(double from, double to, int steps)
        {
            var step = (to - from) / steps;

            // 先把这一段的采样值全部算出来：判定阈值要参考**本段**的量级，
            // 不能拿相邻两点的绝对值硬比 1e-12（见下）。
            var ys = new double[steps + 1];
            var magnitude = 0.0;
            var finite = 0;

            for (var i = 0; i <= steps; i++)
            {
                var y = Eval(f, from + step * i);
                ys[i] = y;
                if (double.IsNaN(y) || double.IsInfinity(y)) continue;

                finite++;
                var a = Math.Abs(y);
                if (a < 1e300 && a > magnitude) magnitude = a;
            }

            if (finite == 0) return;

            if (magnitude == 0)
            {
                // 采样点上恒等于 0（f ≡ 0 之类）：解集是全体实数，交给上面的"无穷多解"文案
                Push(0);
                capped = true;
                return;
            }

            // ⛔ 左端点（i = 0）必须单独判一次"是不是零点"。
            //    主循环从 i = 1 起（需要 ys[i-1]），因此 ys[0] 永远只作为 prevY 参与
            //    变号判断，**从不作为 y 被 `y == 0` 检查**；而偶重根分支又要求 i >= 2。
            //    于是「根恰好落在某一窗的左端点」时会被完整漏掉 ——
            //    x^2 = 1e10 的负根 -100000 正是最后一个窗 Scan(-100000, 100000) 的左端点，
            //    结果只返回正根、并谎称"与窗口大小无关"。这里补上端点判定。
            if (ys[0] == 0) Push(from);

            for (var i = 1; i <= steps; i++)
            {
                var x = from + step * i;
                var y = ys[i];
                var prevX = x - step;
                var prevY = ys[i - 1];

                if (double.IsNaN(y) || double.IsInfinity(y)) continue;

                if (y == 0)
                {
                    Push(x);
                }
                else if (!double.IsNaN(prevY) && !double.IsInfinity(prevY) && prevY != 0 &&
                         Math.Sign(y) != Math.Sign(prevY))
                {
                    var root = Bisect(f, prevX, x, prevY);
                    if (root is { } r) Push(r);
                }
                else if (i >= 2)
                {
                    // 偶重根（x²、x⁴、(x−1)²…）两侧不变号，只靠变号是永远抓不到的。
                    // 这里找 |f| 的**严格**局部极小，再用黄金分割细化到谷底。
                    // ⚠️ 收敛值必须和**谷两侧的量级**比，不能只和绝对值比：
                    //    x² − 1e-9 在 0 处 |f| = 1e-9，它只是"浅坑"，不是根。
                    var prevPrevY = ys[i - 2];
                    if (double.IsNaN(prevPrevY) || double.IsInfinity(prevPrevY)) continue;
                    if (Math.Sign(prevY) != Math.Sign(prevPrevY) || Math.Sign(prevY) != Math.Sign(y)) continue;

                    var ay = Math.Abs(prevY);
                    var a0 = Math.Abs(prevPrevY);
                    var a2 = Math.Abs(y);

                    // ⛔ 必须是**谷底**：两侧都不比它小。只判"有一侧更大"会把单调坡上的
                    //    点也当成候选，黄金分割随后滑到区间端点，把 f = x² + 1e-9 的
                    //    浅坑端点误判成根（冒烟测试曾抓到此问题）。
                    if (!(ay <= a0 && ay <= a2 && (ay < a0 || ay < a2))) continue;

                    var valley = MinimizeAbs(f, prevX - step, x);
                    if (valley is not { } v) continue;

                    var fv = Math.Abs(Eval(f, v));
                    var localScale = Math.Max(a0, a2);
                    if (!double.IsNaN(fv) && fv <= 1e-10 * localScale) Push(v);
                }

                if (roots.Count > cap) { capped = true; return; }
            }
        }

        Scan(-10, 10, 40_000);            // 原点附近，最细
        if (capped) { roots.Sort(); return (roots, true); }

        Scan(-100, 100, 120_000);
        if (capped) { roots.Sort(); return (roots, true); }

        Scan(-1000, 1000, 120_000);
        if (capped) { roots.Sort(); return (roots, true); }

        Scan(-100_000, 100_000, 120_000);   // 大根（x² = 1e10 这种）
        if (capped) { roots.Sort(); return (roots, true); }

        // 最后一窗放到 ±1e6：x² = 1e12 的根是 ±1e6，正好落在上一窗（±1e5）之外，
        // 不补这一窗会直接报"没有找到实根"。步长仍是 1e6/1.2e5 ≈ 8.3，
        // 对连续函数足够（变号检测只看符号，不需要细步长；偶重根那种靠 |f| 谷底
        // 细化，8 个单位内也够用）。
        Scan(-1_000_000, 1_000_000, 120_000);

        roots.Sort();
        return (roots, capped);
    }

    /// <summary>
    /// 黄金分割找 |f| 在 [a, b] 上的谷底（用于偶重根：两侧同号、只有 |f| 下探到 0）。
    /// ⚠️ 返回的只是候选点，是否算根由调用方按谷两侧的量级再判一次。
    /// </summary>
    private static double? MinimizeAbs(Func<double, double> f, double a, double b)
    {
        const double phi = 0.6180339887498949;
        var c = b - (b - a) * phi;
        var d = a + (b - a) * phi;
        var fc = Math.Abs(Eval(f, c));
        var fd = Math.Abs(Eval(f, d));

        for (var i = 0; i < 80; i++)
        {
            if (double.IsNaN(fc) || double.IsNaN(fd)) return null;

            if (fc < fd)
            {
                b = d; d = c; fd = fc;
                c = b - (b - a) * phi;
                fc = Math.Abs(Eval(f, c));
            }
            else
            {
                a = c; c = d; fc = fd;
                d = a + (b - a) * phi;
                fd = Math.Abs(Eval(f, d));
            }
        }

        var mid = (a + b) / 2;
        var m = Eval(f, mid);
        return double.IsNaN(m) || double.IsInfinity(m) ? null : mid;
    }

    /// <summary>
    /// 二分。⛔ 必须核对"收敛点真的是零点"：tan(x) 的极点也会造成变号，那是假根。
    /// 判据改用相对量级：以变号区间两端的 |f| 为基准，要求收敛点残差比它小若干数量级。
    /// 旧的绝对阈值 1e-6 会误杀大根（x²=1e10 收敛后残差约 1e-4），也会把整体极平坦、
    /// 仅靠浮点舍入抖动"变号"的函数刷成假根；相对判据同时挡住这两类。
    /// </summary>
    private static double? Bisect(Func<double, double> f, double a, double b, double fa)
    {
        // 量级基准取**初始**变号端点的 |f|；收敛后再取会被压小，判据将失去意义。
        var fb = Eval(f, b);
        var endpointScale = Math.Max(Math.Abs(fa), Math.Abs(fb));
        if (double.IsNaN(fb) || double.IsInfinity(fb) || !double.IsFinite(endpointScale) || endpointScale <= 0)
            return null;

        for (var i = 0; i < 200; i++)
        {
            var mid = (a + b) / 2;
            var fm = Eval(f, mid);
            if (double.IsNaN(fm)) return null;

            if (Math.Sign(fm) == Math.Sign(fa)) { a = mid; fa = fm; }
            else b = mid;

            if (b - a < 1e-14 * Math.Max(1, Math.Abs(b))) break;
        }

        var root = (a + b) / 2;
        var value = Eval(f, root);

        // 真根：|f| 相对端点量级足够小；极点与平坦伪根在 root 处仍与端点同量级，会被挡下。
        if (!double.IsFinite(value) || Math.Abs(value) > 1e-7 * endpointScale) return null;
        return root;
    }

    // ══════════════════════════════════════════════════════
    //  显示
    // ══════════════════════════════════════════════════════

    /// <summary>精确值显示：整数、简单分数，负数用数学负号。</summary>
    private static string Format(Rational value)
    {
        var text = value.ToString();
        return text.Replace("-", "−");
    }

    /// <summary>
    /// 数值根显示：能认出来的就写成数学式（1/2、√2、π/6），认不出才落到小数。
    /// 这样按课本做题时看到的是 `x = −1/2`，而不是 `−0.5`。
    /// </summary>
    private static string FormatRoot(double v)
    {
        if (Math.Abs(v) < 1e-12) return "0";

        // ⛔ 容差必须**随量级放大**（相对容差）。固定 1e-9 在 1e6 这种大根上等于要求
        //    12 位有效数字，二分根本收敛不到那么准 —— x²=1e12 会显示成
        //    `999999.999999996` 而不是 `1000000`，老师会以为算错了。
        //    这里用 **相对误差** 判断"像不像整数"：
        //      · x²=1e12 → 收敛值 999999.999999996，相对误差 4e-15 → 认作 1000000 ✅
        //      · x²=9999999999 → 99999.999995，相对误差 5e-11 > 1e-11 → 保持小数 ✅
        var scale = Math.Max(1.0, Math.Abs(v));
        var eps = 1e-11 * scale;

        // 整数 / 简单分数
        for (var q = 1; q <= 64; q++)
        {
            var p = v * q;
            var rounded = Math.Round(p);
            if (Math.Abs(p - rounded) > eps * q) continue;

            var sign = rounded < 0 ? "−" : "";
            var num = Math.Abs(rounded);
            return q == 1 ? sign + num.ToString("0", CultureInfo.InvariantCulture)
                          : $"{sign}{num.ToString("0", CultureInfo.InvariantCulture)}/{q}";
        }

        // π 的倍数：1/6 π、2π …
        var k = v / Math.PI;
        for (var q = 1; q <= 12; q++)
        {
            var p = k * q;
            var rounded = Math.Round(p);
            if (Math.Abs(p - rounded) > 1e-9 * Math.Max(1.0, Math.Abs(p)) || rounded == 0) continue;

            var sign = rounded < 0 ? "−" : "";
            var num = Math.Abs(rounded);
            var head = num == 1 ? "π" : $"{num.ToString("0", CultureInfo.InvariantCulture)}π";
            return q == 1 ? sign + head : $"{sign}{head}/{q}";
        }

        // 平方根：√2、−√3
        var square = v * v;
        var n = Math.Round(square);
        if (n > 0 && Math.Abs(square - n) < 1e-9 * Math.Max(1.0, Math.Abs(square))
            && Math.Abs(Math.Sqrt(n) - Math.Abs(v)) < 1e-9 * Math.Max(1.0, Math.Abs(v)))
        {
            var sign = v < 0 ? "−" : "";
            return n == 1 ? sign + "1" : $"{sign}√{n.ToString("0", CultureInfo.InvariantCulture)}";
        }

        // 大数的普通小数会拖一串 9：按有效位数格式化，丢掉浮点噪声尾巴。
        if (Math.Abs(v) >= 1e6)
            return v.ToString("0.######", CultureInfo.InvariantCulture).Replace("-", "−");

        return v.ToString("0.############", CultureInfo.InvariantCulture).Replace("-", "−");
    }
}
