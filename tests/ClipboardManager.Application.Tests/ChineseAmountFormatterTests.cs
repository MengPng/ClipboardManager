using ClipboardManager.Application.Services;

namespace ClipboardManager.Application.Tests;

public sealed class ChineseAmountFormatterTests
{
    [Theory]
    [InlineData("0", "零元整")]
    [InlineData("1", "壹元整")]
    [InlineData("10", "壹拾元整")]
    [InlineData("105", "壹佰零伍元整")]
    [InlineData("1000", "壹仟元整")]
    [InlineData("1234.5", "壹仟贰佰叁拾肆元伍角整")]
    [InlineData("1234.56", "壹仟贰佰叁拾肆元伍角陆分")]
    [InlineData("1234.05", "壹仟贰佰叁拾肆元零伍分")]
    [InlineData("0.5", "伍角整")]
    [InlineData("0.05", "伍分")]
    [InlineData("0.56", "伍角陆分")]
    [InlineData("10000", "壹万元整")]
    [InlineData("10005", "壹万零伍元整")]
    [InlineData("100000", "壹拾万元整")]
    [InlineData("110000", "壹拾壹万元整")]
    [InlineData("10000000", "壹仟万元整")]
    [InlineData("100000000", "壹亿元整")]
    [InlineData("100010000", "壹亿零壹万元整")]
    [InlineData("100000001", "壹亿零壹元整")]
    [InlineData("1000000000000", "壹万亿元整")]
    [InlineData("1203004005.06", "壹拾贰亿零叁佰万肆仟零伍元零陆分")]
    [InlineData("9999999999999999.99", "玖仟玖佰玖拾玖万亿玖仟玖佰玖拾玖亿玖仟玖佰玖拾玖万玖仟玖佰玖拾玖元玖角玖分")]
    [InlineData("-1234.5", "负壹仟贰佰叁拾肆元伍角整")]
    [InlineData("-0.05", "负伍分")]
    public void Format_FollowsReimbursementConvention(string amount, string expected)
    {
        Assert.True(ChineseAmountFormatter.TryFormat(decimal.Parse(amount, System.Globalization.CultureInfo.InvariantCulture), out var text, out var rounded));
        Assert.Equal(expected, text);
        Assert.False(rounded);
    }

    [Theory]
    [InlineData("1.005", "壹元零壹分")]
    [InlineData("0.004", "零元整")]
    [InlineData("2.999", "叁元整")]
    [InlineData("-0.001", "零元整")]
    public void MoreThanTwoDecimals_RoundToTheNearestCentAndSaySo(string amount, string expected)
    {
        Assert.True(ChineseAmountFormatter.TryFormat(decimal.Parse(amount, System.Globalization.CultureInfo.InvariantCulture), out var text, out var rounded));
        Assert.Equal(expected, text);
        Assert.True(rounded);
    }

    [Fact]
    public void BeyondTheUnitNames_IsRefusedInsteadOfGuessing()
    {
        Assert.False(ChineseAmountFormatter.TryFormat(10_000_000_000_000_000m, out var text, out _));
        Assert.Empty(text);
        Assert.False(ChineseAmountFormatter.TryFormat(-10_000_000_000_000_000m, out _, out _));
    }

    [Fact]
    public void CalculatorResult_FeedsTheFormatter()
    {
        var outcome = CalculatorService.Evaluate("(1250+380)/3");
        Assert.True(outcome.IsSuccess);
        Assert.True(ChineseAmountFormatter.TryFormat(outcome.Result!.Value, out var text, out var rounded));
        Assert.Equal("伍佰肆拾叁元叁角叁分", text);
        Assert.True(rounded);
    }
}
