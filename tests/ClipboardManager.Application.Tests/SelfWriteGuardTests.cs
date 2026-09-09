using ClipboardManager.Application.Services;

namespace ClipboardManager.Application.Tests;

public sealed class SelfWriteGuardTests
{
    [Fact]
    public void TryConsume_OnlyConsumesMatchingHashOnce()
    {
        var guard = new SelfWriteGuard();
        guard.Expect("expected");

        Assert.False(guard.TryConsume("other"));
        Assert.True(guard.TryConsume("expected"));
        Assert.False(guard.TryConsume("expected"));
    }
}
