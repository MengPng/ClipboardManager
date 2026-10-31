using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace ClipboardManager.Application.Services;

/// <summary>
/// A successful calculation. <see cref="Value"/> is exact; the text properties are display/copy forms:
/// <see cref="ResultText"/> is a plain number (no thousands separators) for copying, <see cref="DisplayText"/> is
/// grouped for reading and marked "≈" when it was rounded, <see cref="FullValueText"/> is the unrounded value.
/// </summary>
public sealed record CalculationResult(string Expression, decimal Value, string? ExpandedExpression = null)
{
    public const int MaxDisplayDecimals = 12;
    private const string PlainFormat = "0.############";
    private const string GroupedFormat = "#,##0.############";

    private decimal Rounded => Round(Value, MaxDisplayDecimals);

    public bool IsApproximate => Rounded != Value;
    public string ResultText => Rounded.ToString(PlainFormat, CultureInfo.InvariantCulture);
    public string DisplayText => Rounded.ToString(GroupedFormat, CultureInfo.InvariantCulture) + (IsApproximate ? "≈" : "");
    public string FullValueText => Value.ToString("G29", CultureInfo.InvariantCulture);
    /// <summary>Short grouped form (≤ 2 decimals) for the history-row hint.</summary>
    public string CompactText => Round(Value, 2).ToString("#,##0.##", CultureInfo.InvariantCulture);
    public string Output => $"{Expression}={ResultText}";

    internal static string FormatPlain(decimal value) =>
        Round(value, MaxDisplayDecimals).ToString(PlainFormat, CultureInfo.InvariantCulture);

    private static decimal Round(decimal value, int decimals)
    {
        var rounded = Math.Round(value, decimals, MidpointRounding.AwayFromZero);
        return rounded == 0m ? 0m : rounded; // never print "-0"
    }
}

public enum CalculationStatus { Success, Incomplete, Invalid, MathError }

/// <param name="Message">Short reason; empty on success.</param>
public sealed record CalculationOutcome(CalculationStatus Status, CalculationResult? Result, string Message)
{
    public bool IsSuccess => Status == CalculationStatus.Success;
}

/// <summary>
/// A bounded decimal arithmetic parser. Never evaluates code or accesses external data.
/// Input is normalized once at the boundary; the parser then only sees the canonical alphabet
/// <c>0-9 . + - × ÷ ^ % ( )</c> and spaces.
/// </summary>
public static partial class CalculatorService
{
    public const int MaxExpressionLength = 512;
    private const int MaxNesting = 32;
    private const int MaxOperations = 128;
    private const int MaxExponent = 64;

    public static bool TryCalculate(string? text, out CalculationResult? result, out string error,
        bool requireBinaryOperator = false)
    {
        var outcome = Evaluate(text, requireBinaryOperator);
        result = outcome.Result;
        error = outcome.Message;
        return outcome.IsSuccess;
    }

    public static CalculationOutcome Evaluate(string? text, bool requireBinaryOperator = false)
    {
        if (string.IsNullOrWhiteSpace(text)) return Fail(CalculationStatus.Incomplete, "请输入算式。");
        if (text.Length > MaxExpressionLength) return Fail(CalculationStatus.Invalid, "算式不能超过 512 个字符。");
        if (text.Contains('\n') || text.Contains('\r')) return Fail(CalculationStatus.Invalid, "请使用单行算式。");
        if (HexLiteral().IsMatch(text)) return Fail(CalculationStatus.Invalid, "不支持十六进制数。");

        var normalized = Normalize(text);
        if (normalized.Length == 0) return Fail(CalculationStatus.Incomplete, "请输入算式。");
        // Dates are common copied text. Never treat a date-shaped value as arithmetic automatically.
        if (requireBinaryOperator && IsoDate().IsMatch(normalized))
            return Fail(CalculationStatus.Invalid, "日期不进行自动计算。");
        try
        {
            var parser = new Parser(normalized);
            var value = parser.Parse();
            if (requireBinaryOperator && !parser.HasBinaryOperator)
                return Fail(CalculationStatus.Invalid, "自动计算需要完整的运算表达式。");
            var expression = text.Trim();
            if (expression.EndsWith('=') || expression.EndsWith('＝')) expression = expression[..^1].TrimEnd();
            var expanded = parser.UsedPercent ? value.Text : null;
            return new CalculationOutcome(CalculationStatus.Success,
                new CalculationResult(expression, value.Value, expanded), string.Empty);
        }
        catch (CalculatorException ex) { return Fail(ex.Status, ex.Message); }
        catch (DivideByZeroException) { return Fail(CalculationStatus.MathError, "除数不能为 0。"); }
        catch (OverflowException) { return Fail(CalculationStatus.MathError, "数值超出十进制计算范围。"); }
    }

    /// <summary>
    /// A pure "number-number" such as a page range, room number or year-month (<c>10-12</c>, <c>2026-10</c>).
    /// These compute fine but are far more likely to be identifiers than arithmetic, so automatic rewriting skips them.
    /// </summary>
    public static bool IsDashSeparatedIdentifier(string? text) =>
        text is not null && DashPair().IsMatch(Normalize(text));

    private static CalculationOutcome Fail(CalculationStatus status, string message) => new(status, null, message);

    // ---- normalization: the only place input spelling is interpreted ----

    internal static string Normalize(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var character in text) builder.Append(Map(character));
        var normalized = builder.ToString().Trim();
        if (normalized.EndsWith('=')) normalized = normalized[..^1].TrimEnd();
        normalized = LeadingCurrency().Replace(normalized, string.Empty);
        return Thousands().Replace(normalized, match => match.Value.Replace(",", string.Empty));
    }

    private static char Map(char character) => character switch
    {
        >= '０' and <= '９' => (char)('0' + (character - '０')),
        '＋' => '+',
        '－' or '–' or '—' or '−' => '-',
        '＊' or '*' or 'x' or 'X' => '×',
        '／' or '/' => '÷',
        '（' => '(',
        '）' => ')',
        '．' => '.',
        '％' => '%',
        '＾' => '^',
        '＝' => '=',
        '＄' => '$',
        ' ' or '　' => ' ',
        _ => character,
    };

    [GeneratedRegex(@"(?<![\w.])0[xX][0-9A-Fa-f]")]
    private static partial Regex HexLiteral();

    [GeneratedRegex(@"^\d{4}-\d{1,2}-\d{1,2}$")]
    private static partial Regex IsoDate();

    [GeneratedRegex(@"^\d+\s*-\s*\d+$")]
    private static partial Regex DashPair();

    [GeneratedRegex(@"^[¥￥$€£]\s*(?=\.?\d)")]
    private static partial Regex LeadingCurrency();

    [GeneratedRegex(@"(?<![\d.,])\d{1,3}(?:,\d{3})+(?:\.\d+)?(?![\d,])")]
    private static partial Regex Thousands();

    // ---- parsing over the canonical alphabet ----

    private sealed class CalculatorException(CalculationStatus status, string message) : Exception(message)
    {
        public CalculationStatus Status { get; } = status;
    }

    private static CalculatorException Invalid(string message) => new(CalculationStatus.Invalid, message);
    private static CalculatorException Incomplete() => new(CalculationStatus.Incomplete, "算式还没写完。");

    /// <param name="Percent">True only for a bare <c>y%</c> operand (value already divided by 100).</param>
    private readonly record struct Node(decimal Value, string Text, bool Percent);

    private sealed class Parser(string text)
    {
        private int _position, _depth, _operations;

        /// <summary>True when an explicit + - × ÷ ^ was applied (implicit multiplication and % do not count).</summary>
        public bool HasBinaryOperator { get; private set; }
        public bool UsedPercent { get; private set; }

        public Node Parse()
        {
            var node = Additive();
            SkipSpaces();
            if (_position < text.Length)
            {
                var next = text[_position];
                if (next == ')') throw Invalid("括号不匹配。");
                if (next is >= '0' and <= '9' or '.') throw Invalid("数字之间缺少运算符。");
                throw Invalid("只支持数字、加减乘除、百分号、乘方和括号；请检查算式。");
            }
            return node;
        }

        // x + y% and x - y% mean x ± x·y/100; every other use of y% is just y/100.
        private Node Additive()
        {
            var left = Term();
            while (true)
            {
                var op = PeekAny('+', '-');
                if (op == '\0') return left;
                _position++;
                CountOperation();
                HasBinaryOperator = true;
                var right = Term();
                var rightValue = right.Value;
                var rightText = right.Text;
                if (right.Percent)
                {
                    rightValue = checked(left.Value * right.Value);
                    rightText = CalculationResult.FormatPlain(rightValue);
                }
                var value = op == '+' ? checked(left.Value + rightValue) : checked(left.Value - rightValue);
                left = new Node(value, $"{left.Text} {op} {rightText}", false);
            }
        }

        private Node Term()
        {
            var left = Unary();
            while (true)
            {
                var op = PeekAny('×', '÷', '(');
                if (op == '\0') return left;
                if (op != '(') _position++; // '(' starts the next factor: implicit multiplication
                CountOperation();
                if (op != '(') HasBinaryOperator = true;
                var right = Unary();
                var value = op == '÷' ? left.Value / right.Value : checked(left.Value * right.Value);
                left = new Node(value, $"{left.Text} {(op == '÷' ? '÷' : '×')} {right.Text}", false);
            }
        }

        private Node Unary()
        {
            // The outermost operand is depth 1, so N parentheses or signs reach depth N + 1.
            if (++_depth > MaxNesting + 1) throw Invalid("括号或正负号嵌套不能超过 32 层。");
            try
            {
                var sign = PeekAny('+', '-');
                if (sign == '\0') return Power();
                _position++;
                var operand = Unary();
                return sign == '+' ? operand : operand with { Value = -operand.Value, Text = "-" + operand.Text };
            }
            finally { _depth--; }
        }

        // Right associative: 2^3^2 = 2^(3^2). The exponent may carry a sign: 2^-2.
        private Node Power()
        {
            var baseNode = Postfix();
            if (PeekAny('^') == '\0') return baseNode;
            _position++;
            CountOperation();
            HasBinaryOperator = true;
            var exponent = Unary();
            return new Node(Raise(baseNode.Value, exponent.Value), $"{baseNode.Text} ^ {exponent.Text}", false);
        }

        private Node Postfix()
        {
            var node = Primary();
            if (PeekAny('%') == '\0') return node;
            _position++;
            UsedPercent = true;
            var fraction = node.Value / 100m;
            if (PeekAny('%') != '\0') throw Invalid("百分号不能连用。");
            return new Node(fraction, CalculationResult.FormatPlain(fraction), true);
        }

        private Node Primary()
        {
            SkipSpaces();
            if (_position >= text.Length) throw Incomplete();
            var character = text[_position];
            if (character == '(')
            {
                _position++;
                var inner = Additive();
                SkipSpaces();
                if (_position >= text.Length) throw Incomplete();
                if (text[_position] != ')')
                    throw Invalid("只支持数字、加减乘除、百分号、乘方和括号；请检查算式。");
                _position++;
                return inner with { Text = $"({inner.Text})", Percent = false };
            }
            if (character is >= '0' and <= '9' or '.') return Number();
            if (character is ')' or '×' or '÷' or '^' or '%') throw Invalid("这里缺少数字。");
            throw Invalid("只支持数字、加减乘除、百分号、乘方和括号；请检查算式。");
        }

        private Node Number()
        {
            var start = _position;
            while (_position < text.Length && (text[_position] is >= '0' and <= '9' || text[_position] == '.'))
                _position++;
            var token = text.AsSpan(start, _position - start);
            var dot = token.IndexOf('.');
            if (dot >= 0 && token[(dot + 1)..].IndexOf('.') >= 0) throw Invalid("数字里有多个小数点。");
            if (token.Length == 1 && dot == 0) throw Incomplete();
            if (dot >= 0 && token.Length - dot - 1 > 28) throw Invalid("小数最多支持 28 位，请缩短后计算。");
            if (token.ToString().Replace(".", "").TrimStart('0').Length > 29)
                throw Invalid("数字最多支持 29 位有效数字。");
            if (!decimal.TryParse(token, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var number))
                throw new OverflowException();
            return new Node(number, token.ToString(), false);
        }

        private static decimal Raise(decimal value, decimal exponent)
        {
            if (exponent != decimal.Truncate(exponent)) throw Invalid("指数必须是整数。");
            if (Math.Abs(exponent) > MaxExponent) throw Invalid("指数的绝对值不能超过 64。");
            var count = (int)Math.Abs(exponent);
            var result = 1m;
            for (var i = 0; i < count; i++) result = checked(result * value);
            return exponent < 0 ? 1m / result : result;
        }

        /// <summary>Returns the next non-space character if it is one of the arguments, else '\0'.</summary>
        private char PeekAny(char first, char second = '\0', char third = '\0')
        {
            SkipSpaces();
            if (_position >= text.Length) return '\0';
            var character = text[_position];
            return character == first || character == second || character == third ? character : '\0';
        }

        private void SkipSpaces()
        {
            while (_position < text.Length && text[_position] is ' ' or '\t') _position++;
        }

        private void CountOperation()
        {
            if (++_operations > MaxOperations) throw Invalid("算式最多支持 128 次运算。");
        }
    }
}
