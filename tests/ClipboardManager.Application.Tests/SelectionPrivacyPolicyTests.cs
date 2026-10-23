using ClipboardManager.Application.Services;

namespace ClipboardManager.Application.Tests;

public sealed class SelectionPrivacyPolicyTests
{
    [Fact]
    public void EmptyListMeansGlobalFeatureStaysActive()
    {
        Assert.False(SelectionPrivacyPolicy.IsProcessExcluded([], "Code"));
        Assert.False(SelectionPrivacyPolicy.IsProcessExcluded(Array.Empty<string>(), null));
        Assert.False(SelectionPrivacyPolicy.IsProcessExcluded([], ""));
        Assert.False(SelectionPrivacyPolicy.IsProcessExcluded([], "   "));
    }

    [Theory]
    [InlineData("Code", "code")]
    [InlineData("WeChat", "WECHAT")]
    [InlineData("Code", " Code ")]
    [InlineData(" Code ", "Code")]
    public void MatchingIgnoresCaseAndSurroundingWhitespace(string entry, string processName) =>
        Assert.True(SelectionPrivacyPolicy.IsProcessExcluded([entry], processName));

    [Fact]
    public void PartialOrDifferentNamesDoNotMatch()
    {
        var list = new[] { "Code", "WeChat" };
        Assert.False(SelectionPrivacyPolicy.IsProcessExcluded(list, "CodeInsiders"));   // 精确进程名，不做包含匹配
        Assert.False(SelectionPrivacyPolicy.IsProcessExcluded(list, "WeCha"));
        Assert.False(SelectionPrivacyPolicy.IsProcessExcluded(list, "Notion"));
    }

    [Fact]
    public void BlankEntriesInListAreSkipped()
    {
        Assert.True(SelectionPrivacyPolicy.IsProcessExcluded(["", "  ", "Code"], "code"));
        Assert.False(SelectionPrivacyPolicy.IsProcessExcluded(["", "  "], "Code"));
    }
}
