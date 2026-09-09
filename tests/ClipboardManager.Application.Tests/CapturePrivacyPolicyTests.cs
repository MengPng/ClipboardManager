using ClipboardManager.Application.Services;
using ClipboardManager.Domain.Models;

namespace ClipboardManager.Application.Tests;

public sealed class CapturePrivacyPolicyTests
{
    [Fact]
    public void ExcludedProcess_IsRejectedBeforeRead()
    {
        var settings = new AppSettings { ExcludedProcesses = ["  KeePass.exe  "] };
        Assert.False(CapturePrivacyPolicy.CanRead(settings, new ClipboardSource("Password manager", "keepass"), DateTimeOffset.UtcNow));
        Assert.True(CapturePrivacyPolicy.CanRead(settings, new ClipboardSource("Editor", "notepad.exe"), DateTimeOffset.UtcNow));
    }

    [Fact]
    public void PauseExpiry_UsesExactBoundaryAndDoesNotOverrideManualPause()
    {
        var now = DateTimeOffset.UtcNow;
        var settings = new AppSettings { CapturePausedUntil = now.AddMinutes(5) };
        Assert.True(CapturePrivacyPolicy.IsPaused(settings, now));
        Assert.False(CapturePrivacyPolicy.IsPaused(settings, now.AddMinutes(5)));
        Assert.True(CapturePrivacyPolicy.IsPaused(settings with { IsCapturePaused = true }, now.AddDays(1)));
    }
}
