using ClipboardManager.Application.Services;

namespace ClipboardManager.Application.Tests;

public sealed class CalculatorServiceTests
{
    [Theory]
    [InlineData("1+1", "2")]
    [InlineData("0.1+0.2", "0.3")]
    [InlineData("(1250+380)/3", "543.33333333333333333333333333")]
    [InlineData("2+3*4", "14")]
    [InlineData("-(2+3)×4÷2", "-10")]
    [InlineData(" 1 + -2 ", "-1")]
    [InlineData(".5 + .25", "0.75")]
    [InlineData("1−2", "-1")]
    [InlineData("12", "12")]
    public void Calculate_DecimalArithmeticAndPrecedence(string input, string expected)
    {
        Assert.True(CalculatorService.TryCalculate(input, out var result, out var error), error);
        Assert.Equal(expected, result!.ResultText);
        Assert.Equal($"{input.Trim()}={expected}", result.Output);
    }

    [Theory]
    [InlineData("1/0")]
    [InlineData("1+")]
    [InlineData("(1+2")]
    [InlineData("1+1=2")]
    [InlineData("请计算1+1")]
    [InlineData("1+1\n2+2")]
    [InlineData("200+10%")]
    [InlineData("1e3+2")]
    [InlineData("1..2+3")]
    [InlineData("2(3+4)")]
    [InlineData("79228162514264337593543950335+1")]
    [InlineData("999999999999999999999999999999+1")]
    [InlineData("0.00000000000000000000000000001+1")]
    [InlineData("0.12345678901234567890123456789+1")]
    public void InvalidOrUnsafeInput_IsRejectedWithoutThrowing(string input)
    {
        Assert.False(CalculatorService.TryCalculate(input, out var result, out var error));
        Assert.Null(result);
        Assert.NotEmpty(error);
    }

    [Theory]
    [InlineData("12")]
    [InlineData("-12")]
    [InlineData("2026-10-08")]
    public void AutomaticMode_DoesNotRewritePlainNumbersOrIsoDates(string input) =>
        Assert.False(CalculatorService.TryCalculate(input, out _, out _, requireBinaryOperator: true));

    [Fact]
    public void Parsing_HasLengthAndNestingBounds()
    {
        Assert.False(CalculatorService.TryCalculate(new string('1', 513), out _, out _));
        Assert.False(CalculatorService.TryCalculate(new string('(', 33) + "1+1" + new string(')', 33), out _, out _));
        Assert.False(CalculatorService.TryCalculate(new string('-', 33) + "1+1", out _, out _));
    }
}
