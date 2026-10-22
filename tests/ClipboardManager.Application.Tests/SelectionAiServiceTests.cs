using ClipboardManager.Application.Services;
using ClipboardManager.Domain.Contracts;
using ClipboardManager.Domain.Models;

namespace ClipboardManager.Application.Tests;

public sealed class SelectionAiServiceTests
{
    [Fact]
    public async Task TemplateExpansionDoesNotSendAndActionSendsExactPrompt()
    {
        var provider = new Provider();
        var template = new PromptTemplate(Guid.NewGuid(), "摘要", "处理：{text}\n原文：{text}");
        var preview = SelectionAiService.Expand(template, "原文{text}\r\n下一行");
        Assert.Equal("处理：原文{text}\r\n下一行\n原文：原文{text}\r\n下一行", preview);
        Assert.Null(provider.Request);
        await new SelectionAiService(provider).SendAsync(new(), preview);
        Assert.Equal(preview, provider.Request!.Prompt);
    }

    [Fact]
    public async Task CancelledActionDoesNotCallProvider()
    {
        var provider = new Provider();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new SelectionAiService(provider).SendAsync(new(), "expanded prompt", cancellation.Token));
        Assert.Null(provider.Request);
    }

    [Fact]
    public void RejectsEmptyOversizedSelectionAndMissingPlaceholder()
    {
        var template = new PromptTemplate(Guid.NewGuid(), "test", "{text}");
        Assert.Throws<ArgumentException>(() => SelectionAiService.Expand(template, "  "));
        Assert.Throws<ArgumentException>(() => SelectionAiService.Expand(template, new string('a', 32001)));
        Assert.Throws<ArgumentException>(() => SelectionAiService.Expand(template with { Body = "no variable" }, "text"));
        Assert.Throws<ArgumentException>(() => SelectionAiService.Expand(template with { Body = "{text}{text}{text}{text}" }, new string('a', 32000)));
    }

    [Fact]
    public async Task ModelResponseIsPlainTextBeforeItReachesTheWindow()
    {
        var response = await new SelectionAiService(new Provider("# 标题\n\n**正文**")).SendAsync(new(), "expanded prompt");
        Assert.DoesNotContain("#", response.Text);
        Assert.DoesNotContain("*", response.Text);
        Assert.Contains("标题", response.Text);
        Assert.Contains("正文", response.Text);
    }

    [Fact]
    public async Task FormattingWithoutVisibleContentIsRecoverableError()
    {
        await Assert.ThrowsAsync<AiRequestException>(() => new SelectionAiService(new Provider("---")).SendAsync(new(), "expanded prompt"));
    }

    private sealed class Provider(string response = "result") : ITextAiProvider
    {
        public AiTextRequest? Request;
        public Task<AiTextResponse> CompleteAsync(AiTextRequest request, CancellationToken cancellationToken = default)
        { Request = request; return Task.FromResult(new AiTextResponse(response)); }
    }
}
