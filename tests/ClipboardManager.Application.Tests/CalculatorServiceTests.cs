using ClipboardManager.Application.Services;

namespace ClipboardManager.Application.Tests;

public sealed class CalculatorServiceTests
{
    private static CalculationResult Ok(string input, bool requireBinaryOperator = false)
    {
        var outcome = CalculatorService.Evaluate(input, requireBinaryOperator);
        Assert.True(outcome.IsSuccess, $"{input} => {outcome.Status}: {outcome.Message}");
        return outcome.Result!;
    }

    private static CalculationOutcome Fail(string input, CalculationStatus expected, bool requireBinaryOperator = false)
    {
        var outcome = CalculatorService.Evaluate(input, requireBinaryOperator);
        Assert.Equal(expected, outcome.Status);
        Assert.Null(outcome.Result);
        Assert.NotEmpty(outcome.Message);
        return outcome;
    }

    [Theory]
    [InlineData("1+1", "2")]
    [InlineData("0.1+0.2", "0.3")]
    [InlineData("2+3*4", "14")]
    [InlineData("-(2+3)×4÷2", "-10")]
    [InlineData(" 1 + -2 ", "-1")]
    [InlineData(".5 + .25", "0.75")]
    [InlineData("1−2", "-1")]
    [InlineData("12", "12")]
    [InlineData("1.50+1.50", "3")]
    public void Calculate_DecimalArithmeticAndPrecedence(string input, string expected)
    {
        var result = Ok(input);
        Assert.Equal(expected, result.ResultText);
        Assert.Equal($"{input.Trim()}={expected}", result.Output);
    }

    // ---- normalization ----

    [Theory]
    [InlineData("３＋４", "7")]                  // full-width digits and operator
    [InlineData("（１＋２）＊３", "9")]           // full-width parentheses and *
    [InlineData("3x4", "12")]
    [InlineData("3X4", "12")]
    [InlineData("3*4", "12")]
    [InlineData("12÷4", "3")]
    [InlineData("12／4", "3")]
    [InlineData("1,250+380", "1630")]            // thousands separators
    [InlineData("1,250,000.5+0.5", "1250001")]
    [InlineData("¥1,250×2", "2500")]             // leading currency symbol
    [InlineData("$ 3.5+1", "4.5")]
    [InlineData("￥100-1", "99")]
    [InlineData("1+1=", "2")]                    // trailing '='
    [InlineData("1+1＝", "2")]
    [InlineData("1+1 = ", "2")]
    public void Normalization_AcceptsEverydaySpellings(string input, string expected) =>
        Assert.Equal(expected, Ok(input).ResultText);

    [Fact]
    public void Normalization_ExpressionKeepsUserSpellingWithoutTrailingEquals()
    {
        Assert.Equal("1+1", Ok("1+1=").Expression);
        Assert.Equal("３＋４", Ok("３＋４＝").Expression);
        Assert.Equal("7", Ok("3+4").ResultText);
    }

    [Theory]
    [InlineData("1,2+3")]      // not a thousands grouping
    [InlineData("1,25+3")]
    [InlineData("3+$4")]       // a currency symbol is only accepted in front
    [InlineData("1+1=2")]
    [InlineData("=1+1")]
    public void Normalization_DoesNotOverReach(string input) =>
        Assert.False(CalculatorService.Evaluate(input).IsSuccess);

    // ---- percent ----

    [Theory]
    [InlineData("200+10%", "220")]         // x + y% = x + x·y/100
    [InlineData("200-10%", "180")]         // x - y% = x - x·y/100
    [InlineData("200×10%", "20")]          // otherwise y% = y/100
    [InlineData("10%×200", "20")]
    [InlineData("200÷50%", "400")]
    [InlineData("50%", "0.5")]
    [InlineData("(200+100)×10%", "30")]
    [InlineData("100+10%+10%", "121")]     // each + y% applies to the running total
    [InlineData("200+(10%)", "200.1")]     // a parenthesised percent is just 0.1
    [InlineData("1,000×5%", "50")]
    public void Percent_FollowsTheFourSemantics(string input, string expected) =>
        Assert.Equal(expected, Ok(input).ResultText);

    [Fact]
    public void Percent_AdditiveFormShowsTheExpandedExpression()
    {
        var result = Ok("200+10%");
        Assert.Equal("200 + 20", result.ExpandedExpression);
        Assert.Null(Ok("1+1").ExpandedExpression);
    }

    [Theory]
    [InlineData("5%%")]
    [InlineData("%5")]
    [InlineData("5%5")]
    public void Percent_MisuseIsInvalid(string input) => Fail(input, CalculationStatus.Invalid);

    // ---- implicit multiplication ----

    [Theory]
    [InlineData("2(3+4)", "14")]
    [InlineData("(1+2)(3+4)", "21")]
    [InlineData("2 (3)", "6")]
    [InlineData("3(2)(2)", "12")]
    [InlineData("-2(3)", "-6")]
    public void ImplicitMultiplication_OnlyBeforeAParenthesis(string input, string expected) =>
        Assert.Equal(expected, Ok(input).ResultText);

    [Theory]
    [InlineData("2 3")]
    [InlineData("(2)3")]
    [InlineData("2.5 .5")]
    public void ImplicitMultiplication_BetweenNumbersIsRejected(string input) =>
        Assert.Contains("缺少运算符", Fail(input, CalculationStatus.Invalid).Message);

    // ---- power ----

    [Theory]
    [InlineData("2^10", "1024")]
    [InlineData("2^3^2", "512")]           // right associative
    [InlineData("2^-2", "0.25")]
    [InlineData("-2^2", "-4")]
    [InlineData("(-2)^2", "4")]
    [InlineData("2^(1+1)", "4")]
    [InlineData("2^1.0", "2")]
    [InlineData("5^0", "1")]
    [InlineData("2^64", "18446744073709551616")]
    [InlineData("10^28", "10000000000000000000000000000")]
    [InlineData("2×3^2", "18")]
    public void Power_IntegerExponents(string input, string expected) =>
        Assert.Equal(expected, Ok(input).ResultText);

    [Theory]
    [InlineData("2^65")]
    [InlineData("2^-65")]
    [InlineData("2^0.5")]
    [InlineData("2^(1/2)")]
    public void Power_ExponentMustBeAnIntegerWithinSixtyFour(string input) => Fail(input, CalculationStatus.Invalid);

    [Theory]
    [InlineData("10^29")]
    [InlineData("0^-1")]
    public void Power_OverflowAndDivisionByZeroAreMathErrors(string input) => Fail(input, CalculationStatus.MathError);

    [Fact]
    public void Power_TinyResultIsMarkedApproximate()
    {
        var result = Ok("2^-64");
        Assert.True(result.IsApproximate);
        Assert.Equal("0", result.ResultText);
    }

    // ---- display, rounding and copy form ----

    [Fact]
    public void Display_RoundsToTwelveDecimalsAndMarksApproximate()
    {
        var result = Ok("(1250+380)/3");
        Assert.True(result.IsApproximate);
        Assert.Equal("543.333333333333", result.ResultText);
        Assert.Equal("543.333333333333≈", result.DisplayText);
        Assert.Equal("543.33", result.CompactText);
        Assert.StartsWith("543.3333333333333333", result.FullValueText);
        Assert.Equal("2/3=0.666666666667", Ok("2/3").Output);   // half away from zero
    }

    [Theory]
    [InlineData("0.1+0.2", "0.3")]
    [InlineData("1/8", "0.125")]
    [InlineData("0.123456789012", "0.123456789012")]   // exactly 12 decimals: not approximate
    public void Display_ExactResultsAreNotMarked(string input, string expected)
    {
        var result = Ok(input);
        Assert.False(result.IsApproximate);
        Assert.Equal(expected, result.ResultText);
        Assert.Equal(expected, result.DisplayText);
    }

    [Fact]
    public void Display_GroupsThousandsButCopiesPlainDigits()
    {
        var result = Ok("1000000×1000+0.5");
        Assert.Equal("1,000,000,000.5", result.DisplayText);
        Assert.Equal("1000000000.5", result.ResultText);
        Assert.DoesNotContain(",", result.Output);
        Assert.Equal("1,000,000,000.5", result.CompactText);
        Assert.Equal("0", Ok("0.0000000000004-0.0000000000004").ResultText);
        Assert.Equal("0", Ok("0-0.0000000000001").ResultText);   // never "-0"
    }

    [Fact]
    public void Display_NegativeAndFraction()
    {
        Assert.Equal("-1,234.5", Ok("-1234.5").DisplayText);
        Assert.Equal("-1234.5", Ok("-1234.5").ResultText);
        Assert.Equal("0.5", Ok("50%").CompactText);
    }

    // ---- error classes ----

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("1+")]
    [InlineData("(1+2")]
    [InlineData("1+(")]
    [InlineData("2×")]
    [InlineData(".")]
    [InlineData("2^")]
    [InlineData("-")]
    [InlineData("1++")]
    public void Errors_UnfinishedInputIsIncomplete(string input) => Fail(input, CalculationStatus.Incomplete);

    [Theory]
    [InlineData("请计算1+1")]
    [InlineData("1+1=2")]
    [InlineData("1+1\n2+2")]
    [InlineData("1e3+2")]
    [InlineData("1..2+3")]
    [InlineData(".5.5+1")]
    [InlineData("0x1F+1")]
    [InlineData(")")]
    [InlineData("1+2)")]
    [InlineData("()")]
    [InlineData("×3")]
    public void Errors_MalformedInputIsInvalid(string input) => Fail(input, CalculationStatus.Invalid);

    [Theory]
    [InlineData("1/0")]
    [InlineData("1÷0")]
    [InlineData("0÷0")]
    [InlineData("(1+2)÷(3-3)")]
    [InlineData("79228162514264337593543950335+1")]
    [InlineData("79228162514264337593543950335×2")]
    public void Errors_DivisionByZeroAndOverflowAreMathErrors(string input) => Fail(input, CalculationStatus.MathError);

    [Theory]
    [InlineData("1/0")]
    [InlineData("1+")]
    [InlineData("(1+2")]
    [InlineData("请计算1+1")]
    [InlineData("1+1=2")]
    [InlineData("2^65")]
    [InlineData("2 3")]
    [InlineData("")]
    public void Errors_MessagesHaveNoTrailingWhitespace(string input)
    {
        var message = CalculatorService.Evaluate(input).Message;
        Assert.Equal(message.TrimEnd(), message);
    }

    [Fact]
    public void TryCalculate_MatchesEvaluate()
    {
        Assert.True(CalculatorService.TryCalculate("1+1", out var result, out var error));
        Assert.Equal("2", result!.ResultText);
        Assert.Empty(error);
        Assert.False(CalculatorService.TryCalculate("1+", out result, out error));
        Assert.Null(result);
        Assert.NotEmpty(error);
    }

    // ---- limits ----

    [Fact]
    public void Limits_ExpressionLengthIsFiveHundredTwelve()
    {
        Assert.Equal("1", Ok("1" + new string(' ', 511)).ResultText);
        Assert.Contains("512", Fail("1" + new string(' ', 512), CalculationStatus.Invalid).Message);
    }

    [Fact]
    public void Limits_NestingIsThirtyTwo()
    {
        Assert.Equal("2", Ok(new string('(', 32) + "1+1" + new string(')', 32)).ResultText);
        Fail(new string('(', 33) + "1+1" + new string(')', 33), CalculationStatus.Invalid);
        Assert.Equal("2", Ok(new string('-', 32) + "1+1").ResultText);
        Fail(new string('-', 33) + "1+1", CalculationStatus.Invalid);
    }

    [Fact]
    public void Limits_OperationsAreOneHundredTwentyEight()
    {
        Assert.Equal("129", Ok("1" + string.Concat(Enumerable.Repeat("+1", 128))).ResultText);
        Assert.Contains("128", Fail("1" + string.Concat(Enumerable.Repeat("+1", 129)), CalculationStatus.Invalid).Message);
    }

    [Fact]
    public void Limits_TwentyEightDecimalsAndTwentyNineSignificantDigits()
    {
        Assert.True(CalculatorService.Evaluate("0.1234567890123456789012345678+0").IsSuccess);
        Fail("0.12345678901234567890123456789+0", CalculationStatus.Invalid);
        Assert.True(CalculatorService.Evaluate("1234567890123456789012345678.9+0").IsSuccess);    // 29 significant digits
        Fail("100000000000000000000000000000+1", CalculationStatus.Invalid);                       // 30 digits
        Fail("0.00000000000000000000000000001+1", CalculationStatus.Invalid);                      // 29 decimals
    }

    // ---- automatic (clipboard) mode ----

    [Theory]
    [InlineData("12")]
    [InlineData("-12")]
    [InlineData("50%")]
    [InlineData("2(3)")]
    [InlineData("2026-10-08")]
    [InlineData("2026-1-8")]
    public void AutomaticMode_DoesNotTreatPlainNumbersPercentsOrIsoDatesAsExpressions(string input) =>
        Assert.False(CalculatorService.TryCalculate(input, out _, out _, requireBinaryOperator: true));

    [Theory]
    [InlineData("1+1")]
    [InlineData("2^3")]
    [InlineData("2(3)+1")]
    [InlineData("200+10%")]
    [InlineData("(1250+380)/3")]
    public void AutomaticMode_AcceptsRealOperations(string input) =>
        Assert.True(CalculatorService.TryCalculate(input, out _, out _, requireBinaryOperator: true));

    [Theory]
    [InlineData("10-12", true)]
    [InlineData("2026-10", true)]
    [InlineData("10 - 12", true)]
    [InlineData("１０－１２", true)]
    [InlineData("10-12+1", false)]
    [InlineData("1-2-3", false)]
    [InlineData("10×12", false)]
    [InlineData("a-b", false)]
    [InlineData(null, false)]
    public void DashSeparatedIdentifier_IsOnlyAPureNumberDashNumber(string? input, bool expected) =>
        Assert.Equal(expected, CalculatorService.IsDashSeparatedIdentifier(input));
}
