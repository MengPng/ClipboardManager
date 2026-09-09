using ClipboardManager.Application.Services;

namespace ClipboardManager.Application.Tests;

public sealed class TextContentServiceTests
{
    [Fact]
    public void ComputeHash_NormalizesLineEndingsOnly()
    {
        Assert.Equal(
            TextContentService.ComputeHash("第一行\r\n第二行\r第三行"),
            TextContentService.ComputeHash("第一行\n第二行\n第三行"));
        Assert.NotEqual(
            TextContentService.ComputeHash(" text "),
            TextContentService.ComputeHash("text"));
    }

    [Fact]
    public void CreatePreview_CollapsesWhitespaceWithoutChangingSource()
    {
        const string source = "  第一行\r\n\t第二行  ";
        Assert.Equal("第一行 第二行", TextContentService.CreatePreview(source));
        Assert.Equal("  第一行\r\n\t第二行  ", source);
    }
}
