using System.Globalization;
using System.Text.RegularExpressions;

namespace ClipboardManager.Application.Services;

public sealed record CalculationResult(string Expression, decimal Value)
{
    public string ResultText => Value.ToString("G29", CultureInfo.InvariantCulture);
    public string Output => $"{Expression}={ResultText}";
}

/// <summary>A bounded decimal arithmetic parser. Never evaluates code or accesses external data.</summary>
public static class CalculatorService
{
    public const int MaxExpressionLength = 512;

    public static bool TryCalculate(string? text, out CalculationResult? result, out string error,
        bool requireBinaryOperator = false)
    {
        result = null;
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(text)) { error = "请输入算式。"; return false; }
        if (text.Length > MaxExpressionLength) { error = "算式不能超过 512 个字符。"; return false; }
        if (text.Contains('\n') || text.Contains('\r')) { error = "请使用单行算式。"; return false; }
        var expression = text.Trim();
        // Dates are common copied text. Never treat a date-shaped value as arithmetic automatically.
        if (requireBinaryOperator && Regex.IsMatch(expression, @"^\d{4}-\d{1,2}-\d{1,2}$"))
        { error = "日期不进行自动计算。"; return false; }
        try
        {
            var parser = new Parser(expression);
            var value = parser.Parse();
            if (requireBinaryOperator && !parser.HasBinaryOperator)
            { error = "自动计算需要完整的运算表达式。"; return false; }
            result = new CalculationResult(expression, value);
            return true;
        }
        catch (DivideByZeroException) { error = "除数不能为 0。"; }
        catch (OverflowException) { error = "数值超出十进制计算范围。"; }
        catch (FormatException ex) { error = ex.Message; }
        return false;
    }

    private sealed class Parser(string text)
    {
        private int _position, _depth, _operations;
        public bool HasBinaryOperator { get; private set; }

        public decimal Parse()
        {
            var value = Expression();
            SkipSpaces();
            if (_position != text.Length) throw new FormatException("只支持数字、加减乘除和括号；请检查算式。 ");
            return value;
        }

        private decimal Expression()
        {
            var value = Term();
            while (true)
            {
                if (Take('+')) { CountOperation(); HasBinaryOperator = true; value = checked(value + Term()); }
                else if (Take('-') || Take('−')) { CountOperation(); HasBinaryOperator = true; value = checked(value - Term()); }
                else return value;
            }
        }

        private decimal Term()
        {
            var value = Unary();
            while (true)
            {
                if (Take('*') || Take('×')) { CountOperation(); HasBinaryOperator = true; value = checked(value * Unary()); }
                else if (Take('/') || Take('÷')) { CountOperation(); HasBinaryOperator = true; value /= Unary(); }
                else return value;
            }
        }

        private decimal Unary()
        {
            if (++_depth > 32) throw new FormatException("括号或正负号嵌套不能超过 32 层。 ");
            try
            {
                if (Take('+')) return Unary();
                if (Take('-') || Take('−')) return checked(-Unary());
                if (Take('('))
                {
                    var value = Expression();
                    if (!Take(')')) throw new FormatException("括号不匹配。 ");
                    return value;
                }
                SkipSpaces();
                var start = _position;
                while (_position < text.Length && (text[_position] is >= '0' and <= '9' || text[_position] == '.'))
                    _position++;
                var token = text.AsSpan(start, _position - start);
                var dot = token.IndexOf('.');
                if (dot >= 0 && token.Length - dot - 1 > 28)
                    throw new FormatException("小数最多支持 28 位，请缩短后计算。");
                if (token.ToString().Replace(".", "").TrimStart('0').Length > 29)
                    throw new FormatException("数字最多支持 29 位有效数字。");
                if (start == _position || !decimal.TryParse(text.AsSpan(start, _position - start),
                        NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var number))
                    throw new FormatException("数字无效、超出范围或算式不完整。 ");
                return number;
            }
            finally { _depth--; }
        }

        private bool Take(char character)
        {
            SkipSpaces();
            if (_position >= text.Length || text[_position] != character) return false;
            _position++;
            return true;
        }

        private void SkipSpaces()
        {
            while (_position < text.Length && text[_position] is ' ' or '\t') _position++;
        }

        private void CountOperation()
        {
            if (++_operations > 128) throw new FormatException("算式最多支持 128 次运算。 ");
        }
    }
}
