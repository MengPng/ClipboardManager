using ClipboardManager.Domain.Models;

namespace ClipboardManager.Domain.Tests;

public sealed class CollectionLinkTests
{
    [Theory]
    [InlineData(@"C:\Windows\notepad.exe")]
    [InlineData(@"\\server\share\tool.exe")]
    [InlineData("file:///C:/Windows/notepad.exe")]
    [InlineData("ms-settings:privacy")]
    [InlineData("javascript:alert(1)")]
    [InlineData("https://user:password@example.com")]
    public void UnsafeTargetsAreRejected(string target) =>
        Assert.Throws<ArgumentException>(() => CollectionLink.Normalize(target));

    [Theory]
    [InlineData("https://example.com", "https://example.com/")]
    [InlineData("HTTP://example.com/a?q=b#c", "http://example.com/a?q=b#c")]
    public void WebTargetsRemainUsable(string target, string expected) =>
        Assert.Equal(expected, CollectionLink.Normalize(target));
}
