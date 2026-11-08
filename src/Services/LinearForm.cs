using System;
using System.Collections.Generic;
using System.Linq;

namespace ClassSoftwareHub.Desktop.Services;

/// <summary>
/// 一次式：常数项 + 各变量的一次项之和。
///
/// <para>
/// 为什么不用通用表达式树：方程求解要的是**精确**系数，一次式用字典存"变量 → 分数"最直接，
/// 而且能在解析阶段就判定"这式子是不是一次的"—— 二次、乘法、变量相除当场就能报错，
/// 不会出现"看起来算出来了、其实系数早就不对"的情况。
/// </para>
/// </summary>
public sealed class LinearForm
{
    public Dictionary<string, Rational> Coeffs { get; } = new(StringComparer.Ordinal);
    public Rational Constant { get; set; } = Rational.Zero;

    public bool IsConstant => Coeffs.Count == 0;

    public Rational this[string name] =>
        Coeffs.TryGetValue(name, out var v) ? v : Rational.Zero;

    public void AddVariable(string name, Rational coeff)
    {
        if (coeff.IsZero) return;
        Coeffs[name] = this[name] + coeff;
        if (Coeffs[name].IsZero) Coeffs.Remove(name);
    }

    public static LinearForm FromConstant(Rational c) => new() { Constant = c };

    public static LinearForm FromVariable(string name) => new() { Coeffs = { [name] = Rational.One } };

    public LinearForm Add(LinearForm other)
    {
        var result = new LinearForm { Constant = Constant + other.Constant };
        foreach (var (k, v) in Coeffs) result.AddVariable(k, v);
        foreach (var (k, v) in other.Coeffs) result.AddVariable(k, v);
        return result;
    }

    public LinearForm Subtract(LinearForm other)
    {
        var result = new LinearForm { Constant = Constant - other.Constant };
        foreach (var (k, v) in Coeffs) result.AddVariable(k, v);
        foreach (var (k, v) in other.Coeffs) result.AddVariable(k, -v);
        return result;
    }

    public LinearForm Negate()
    {
        var result = new LinearForm { Constant = -Constant };
        foreach (var (k, v) in Coeffs) result.AddVariable(k, -v);
        return result;
    }

    public LinearForm Scale(Rational factor)
    {
        var result = new LinearForm { Constant = Constant * factor };
        foreach (var (k, v) in Coeffs) result.AddVariable(k, v * factor);
        return result;
    }
}

/// <summary>一次式解析失败（含"这不是一次式"）。</summary>
public sealed class LinearParseException : Exception
{
    public LinearParseException(string message) : base(message) { }
}

/// <summary>
/// 一次式解析器。<see cref="Parse"/> 只接受：常数、变量、+ − * /、括号、整数指数 0/1。
/// 其余一律报错 —— 报错信息是给用户看的，要说清"哪里不行"。
/// </summary>
public sealed class LinearFormParser
{
    private readonly string _s;
    private int _i;

    private LinearFormParser(string text) => _s = text;

    public static LinearForm Parse(string text) => new LinearFormParser(text).ParseExpression();

    private void SkipSpace()
    {
        while (_i < _s.Length && char.IsWhiteSpace(_s[_i])) _i++;
    }

    private char Peek()
    {
        SkipSpace();
        return _i < _s.Length ? _s[_i] : '\0';
    }

    private LinearForm ParseExpression()
    {
        var left = ParseTerm();

        while (true)
        {
            var c = Peek();
            if (c != '+' && c != '-') return left;

            _i++;
            var right = ParseTerm();
            left = c == '+' ? left.Add(right) : left.Subtract(right);
        }
    }

    private LinearForm ParseTerm()
    {
        var left = ParseUnary();

        while (true)
        {
            var c = Peek();

            if (c == '*')
            {
                _i++;
                left = Multiply(left, ParseUnary());
                continue;
            }

            if (c == '/')
            {
                _i++;
                var divisor = ParseUnary();
                if (!divisor.IsConstant)
                    throw new LinearParseException("除数里出现了未知数，这不是一次式。");
                if (divisor.Constant.IsZero)
                    throw new LinearParseException("除数不能为 0。");

                left = left.Scale(Rational.One / divisor.Constant);
                continue;
            }

            // 隐式乘法：2x、3(x+1)、2π
            if (c == '(' || char.IsLetter(c))
            {
                left = Multiply(left, ParseUnary());
                continue;
            }

            return left;
        }
    }

    /// <summary>乘：两边都含未知数 ⇒ 二次项，直接拒绝。</summary>
    private static LinearForm Multiply(LinearForm a, LinearForm b)
    {
        if (a.IsConstant && b.IsConstant) return LinearForm.FromConstant(a.Constant * b.Constant);
        if (a.IsConstant) return b.Scale(a.Constant);
        if (b.IsConstant) return a.Scale(b.Constant);

        throw new LinearParseException("出现了未知数相乘（二次或更高次），这里只解一次方程组。");
    }

    private LinearForm ParseUnary()
    {
        var c = Peek();

        if (c == '+') { _i++; return ParseUnary(); }
        if (c == '-') { _i++; return ParseUnary().Negate(); }

        return ParsePower();
    }

    private LinearForm ParsePower()
    {
        var basis = ParsePrimary();
        if (Peek() != '^') return basis;

        _i++;
        // ⛔ 必须**先跳过空格再取起点**：旧实现把 start 定在 '^' 之后、
        //    然后才 SkipSpace()，于是 `x ^ 2` 会截出 " 2" 这种带空格的串，
        //    int.Parse 直接抛 FormatException —— 而 LinearFormParser 的两个
        //    catch 只认 LinearParseException，异常会一路穿出 Solve / Validate
        //    （Validate 是边输边调的，用户打字过程中就白屏）。
        SkipSpace();
        var start = _i;
        while (_i < _s.Length && char.IsDigit(_s[_i])) _i++;
        if (start == _i) throw new LinearParseException("指数写法有误。");

        // ⛔ 必须用 TryParse：`x^999999999999999999999` 会让 int.Parse 抛
        //    OverflowException，同样是穿出去的类型。
        if (!int.TryParse(_s[start.._i], out var exponent))
            throw new LinearParseException("指数太大，无法计算。");

        if (exponent == 0)
        {
            if (!basis.IsConstant)
                throw new LinearParseException("未知数的 0 次方按 1 处理，请直接写 1。");
            return LinearForm.FromConstant(Rational.One);
        }

        if (exponent == 1) return basis;

        throw new LinearParseException("出现了平方及更高次项，这里只解一次方程组。");
    }

    private LinearForm ParsePrimary()
    {
        var c = Peek();

        if (c == '(')
        {
            _i++;
            var inner = ParseExpression();
            if (Peek() != ')') throw new LinearParseException("括号没有闭合。");
            _i++;
            return inner;
        }

        if (char.IsDigit(c) || c == '.')
        {
            var start = _i;
            while (_i < _s.Length && (char.IsDigit(_s[_i]) || _s[_i] == '.')) _i++;

            // ⛔ 科学计数法必须在这里整体吃掉：`1e10` 若只读成 `1`，后面那个 `e10`
            //    会被 ParsePrimary 当成**变量名**（字母开头），于是 `x = 1e10` 变成
            //    「x = 1 × e10」这种两个未知数的方程，答"无穷多组解"且不报错。
            //    这是最隐蔽的一类错误：答案结构上看着对，实际完全错。
            if (_i < _s.Length && (_s[_i] == 'e' || _s[_i] == 'E'))
            {
                var expStart = _i;
                var probe = _i + 1;
                if (probe < _s.Length && (_s[probe] == '+' || _s[probe] == '-')) probe++;
                var digitsStart = probe;
                while (probe < _s.Length && char.IsDigit(_s[probe])) probe++;

                // 只有「e 后面确实跟了指数数字」才算科学计数法；
                // 否则 `2e` 这种仍应按"系数 2 × 变量 e"处理，保持原语义。
                if (probe > digitsStart) _i = probe;
                else _i = expStart;
            }

            var text = _s[start.._i];
            if (!decimal.TryParse(text, System.Globalization.NumberStyles.Float,
                                  System.Globalization.CultureInfo.InvariantCulture, out var value))
                throw new LinearParseException($"看不清数字「{text}」。");

            // 小数转精确分数：0.5 ⇒ 1/2，0.25 ⇒ 1/4，不做浮点近似
            return LinearForm.FromConstant(FromDecimal(value));
        }

        if (char.IsLetter(c))
        {
            var start = _i;
            while (_i < _s.Length && (char.IsLetter(_s[_i]) || char.IsDigit(_s[_i]))) _i++;
            var name = _s[start.._i];

            // 函数调用（sin、ln…）不是一次式，交给一元方程的数值求解
            if (Peek() == '(')
                throw new LinearParseException($"「{name}」是函数，请写成只含 x 的一元方程。");

            return LinearForm.FromVariable(name);
        }

        if (c == '\0') throw new LinearParseException("式子没写完。");
        throw new LinearParseException($"认不出字符「{c}」。");
    }

    /// <summary>
    /// decimal → 精确分数。
    ///
    /// ⛔ 不能走 <c>decimal.ToInt64</c>：科学计数法（1e10、1e20）很容易超出 long 范围，
    ///    那样一输入大数就抛 OverflowException。直接按字符串拼 BigInteger：
    ///    整数部分 × 10^小数位数 + 小数部分，分母 10^小数位数，再交给 Rational 约分。
    /// </summary>
    private static Rational FromDecimal(decimal value)
    {
        if (value == 0) return Rational.Zero;

        var text = value.ToString(System.Globalization.CultureInfo.InvariantCulture);

        // decimal.ToString 可能给出 "1E+10" 这种（小数值极大或极小时）。先归一成普通写法。
        if (text.Contains('E') || text.Contains('e'))
        {
            text = value.ToString("0.############################", System.Globalization.CultureInfo.InvariantCulture);
        }

        var negative = text.StartsWith("-", StringComparison.Ordinal);
        if (negative) text = text[1..];

        var dot = text.IndexOf('.');
        var intPart = dot < 0 ? text : text[..dot];
        var fracPart = dot < 0 ? "" : text[(dot + 1)..];

        if (intPart.Length == 0) intPart = "0";

        var numerator = System.Numerics.BigInteger.Parse(intPart + fracPart,
                            System.Globalization.CultureInfo.InvariantCulture);
        if (negative) numerator = -numerator;

        var denominator = System.Numerics.BigInteger.Pow(10, fracPart.Length);
        return new Rational(numerator, denominator);
    }
}
