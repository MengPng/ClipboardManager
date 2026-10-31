using ClipboardManager.Application.Services;
using ClipboardManager.Domain.Contracts;
using ClipboardManager.Domain.Models;

namespace ClipboardManager.Application.Tests;

public sealed class ClipboardCalculationServiceTests
{
    private static readonly ClipboardSource Source = new("Notepad", "notepad");

    [Fact]
    public async Task Success_PassesOriginalAndSequence_AndArmsOneShotGuard()
    {
        var clipboard = new Rewriter();
        var guard = new SelfWriteGuard();
        var service = new ClipboardCalculationService(clipboard, new Store(new AppSettings { ClipboardCalculatorMode = ClipboardCalculatorMode.Rewrite }), guard);
        CalculationResult? observed = null;
        service.CalculationCompleted += (_, result) => observed = result;
        Assert.True(await service.TryCalculateAsync(" 0.1+0.2 ", 17, Source));
        Assert.Equal(" 0.1+0.2 ", clipboard.Original);
        Assert.Equal(17u, clipboard.Sequence);
        Assert.Equal("0.1+0.2=0.3", clipboard.Replacement);
        Assert.Equal("0.3", observed!.ResultText);
        Assert.True(guard.TryConsume(TextContentService.ComputeHash(clipboard.Replacement!)));
        Assert.False(guard.TryConsume(TextContentService.ComputeHash(clipboard.Replacement!)));
    }

    [Fact]
    public async Task OffHintPausedTimedPausedAndExcluded_NeverAttemptRewrite()
    {
        var settings = new[]
        {
            new AppSettings { ClipboardCalculatorMode = ClipboardCalculatorMode.Off },
            new AppSettings(),   // the default is Hint: show a result in the history, never rewrite the clipboard
            new AppSettings { ClipboardCalculatorMode = ClipboardCalculatorMode.Hint },
            new AppSettings { ClipboardCalculatorMode = ClipboardCalculatorMode.Rewrite, IsCapturePaused = true },
            new AppSettings { ClipboardCalculatorMode = ClipboardCalculatorMode.Rewrite, CapturePausedUntil = DateTimeOffset.UtcNow.AddHours(1) },
            new AppSettings { ClipboardCalculatorMode = ClipboardCalculatorMode.Rewrite, ExcludedProcesses = ["NOTEPAD"] }
        };
        foreach (var config in settings)
        {
            var clipboard = new Rewriter();
            var service = new ClipboardCalculationService(clipboard, new Store(config), new SelfWriteGuard());
            Assert.False(await service.TryCalculateAsync("1+1", 17, Source));
            Assert.Equal(0, clipboard.Calls);
        }
    }

    [Theory]
    [InlineData("hello")]
    [InlineData("1+1=2")]
    [InlineData("1/0")]
    [InlineData("12")]
    [InlineData("2026-10-08")]
    [InlineData("10-12")]          // page range / room number
    [InlineData("2026-10")]        // year-month
    [InlineData("10 - 12")]
    [InlineData("50%")]
    public async Task NonExpressionAndInvalidInput_LeaveClipboardUntouched(string text)
    {
        var clipboard = new Rewriter();
        var service = new ClipboardCalculationService(clipboard, new Store(new AppSettings { ClipboardCalculatorMode = ClipboardCalculatorMode.Rewrite }), new SelfWriteGuard());
        Assert.False(await service.TryCalculateAsync(text, 17, Source));
        Assert.Equal(0, clipboard.Calls);
    }

    [Theory]
    [InlineData("200+10%", "200+10%=220")]
    [InlineData("(1250+380)/3", "(1250+380)/3=543.333333333333")]
    [InlineData("10-12+1", "10-12+1=-1")]
    [InlineData("１＋２", "１＋２=3")]
    [InlineData("1,250×2", "1,250×2=2500")]
    public async Task Rewrite_StillRewritesRealExpressions_WithTheSameOutputShape(string text, string expected)
    {
        var clipboard = new Rewriter();
        var service = new ClipboardCalculationService(clipboard, new Store(new AppSettings { ClipboardCalculatorMode = ClipboardCalculatorMode.Rewrite }), new SelfWriteGuard());
        Assert.True(await service.TryCalculateAsync(text, 17, Source));
        Assert.Equal(expected, clipboard.Replacement);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StaleOrFailedWrite_ClearsGuardAndDoesNotPublishResult(bool throws)
    {
        var clipboard = new Rewriter { Accepted = false, Throws = throws };
        var guard = new SelfWriteGuard();
        var service = new ClipboardCalculationService(clipboard, new Store(new AppSettings { ClipboardCalculatorMode = ClipboardCalculatorMode.Rewrite }), guard);
        var events = 0;
        service.CalculationCompleted += (_, _) => events++;
        if (throws) await Assert.ThrowsAsync<InvalidOperationException>(() => service.TryCalculateAsync("1+1", 17, Source));
        else Assert.False(await service.TryCalculateAsync("1+1", 17, Source));
        Assert.Equal(0, events);
        Assert.False(guard.TryConsume(TextContentService.ComputeHash("1+1=2")));
    }

    private sealed class Store(AppSettings settings) : IAppSettingsStore
    {
        public Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(settings);
        public Task SaveAsync(AppSettings value, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
    private sealed class Rewriter : IClipboardTextRewriter
    {
        public bool Accepted = true, Throws;
        public int Calls;
        public string? Original, Replacement;
        public uint Sequence;
        public Task<bool> TryReplaceTextAsync(string expectedText, uint expectedSequence, string replacement, CancellationToken cancellationToken = default)
        {
            Calls++;
            Original = expectedText; Sequence = expectedSequence; Replacement = replacement;
            if (Throws) throw new InvalidOperationException("busy");
            return Task.FromResult(Accepted);
        }
    }
}
