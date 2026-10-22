using ClipboardManager.Application.Services;

namespace ClipboardManager.Application.Tests;

public sealed class AiPlainTextFormatterTests
{
    [Theory]
    [InlineData("# 标题\n\n**重点**和*说明*。", "标题\n重点和说明。")]
    [InlineData("> 引用\n\n- 第一项\n- 第二项", "引用\n第一项\n第二项")]
    [InlineData("```text\n普通代码内容\n```", "普通代码内容")]
    [InlineData("`内容`与~~删除线文字~~", "内容与删除线文字")]
    [InlineData("[示例](https://example.invalid)\n\n![图片说明](image.png)", "示例\n图片说明")]
    [InlineData("普通中文。\n第二行。", "普通中文。\n第二行。")]
    [InlineData("C# 和 2 * 3 = 6", "C# 和 2 * 3 = 6")]
    public void RemovesMarkdownFormattingAndRetainsText(string markdown, string expected)
    {
        var actual = AiPlainTextFormatter.Format(markdown).Replace("\r\n", "\n");
        var normalized = string.Join("\n", actual.Split('\n').Where(line => !string.IsNullOrWhiteSpace(line)));
        Assert.Equal(expected, normalized);
    }

    [Fact]
    public void TableTextIsPreservedWithoutMarkdownDelimiters()
    {
        var result = AiPlainTextFormatter.Format("| 名称 | 说明 |\n| --- | --- |\n| 项目 | 内容 |");
        Assert.Contains("名称", result); Assert.Contains("说明", result);
        Assert.Contains("项目", result); Assert.Contains("内容", result);
        Assert.DoesNotContain("|", result); Assert.DoesNotContain("---", result);
    }
}
