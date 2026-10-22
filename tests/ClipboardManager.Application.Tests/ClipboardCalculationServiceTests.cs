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
        var service = new ClipboardCalculationService(clipboard, new Store(new AppSettings { EnableClipboardCalculator = true }), guard);
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
    public async Task DisabledPausedTimedPausedAndExcluded_NeverAttemptRewrite()
    {
        var settings = new[]
        {
            new AppSettings(),
            new AppSettings { EnableClipboardCalculator = true, IsCapturePaused = true },
            new AppSettings { EnableClipboardCalculator = true, CapturePausedUntil = DateTimeOffset.UtcNow.AddHours(1) },
            new AppSettings { EnableClipboardCalculator = true, ExcludedProcesses = ["NOTEPAD"] }
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
    public async Task NonExpressionAndInvalidInput_LeaveClipboardUntouched(string text)
    {
        var clipboard = new Rewriter();
        var service = new ClipboardCalculationService(clipboard, new Store(new AppSettings { EnableClipboardCalculator = true }), new SelfWriteGuard());
        Assert.False(await service.TryCalculateAsync(text, 17, Source));
        Assert.Equal(0, clipboard.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StaleOrFailedWrite_ClearsGuardAndDoesNotPublishResult(bool throws)
    {
        var clipboard = new Rewriter { Accepted = false, Throws = throws };
        var guard = new SelfWriteGuard();
        var service = new ClipboardCalculationService(clipboard, new Store(new AppSettings { EnableClipboardCalculator = true }), guard);
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
