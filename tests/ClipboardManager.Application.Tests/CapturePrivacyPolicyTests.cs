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

    [Fact]
    public void UnknownOwner_IsRejectedOnlyWhenExclusionsAreConfigured()
    {
        var now = DateTimeOffset.UtcNow;
        var unknown = new ClipboardSource(null, null);

        Assert.False(CapturePrivacyPolicy.CanRead(
            new AppSettings { ExcludedProcesses = ["KeePass"] },
            unknown,
            now));
        Assert.True(CapturePrivacyPolicy.CanRead(
            new AppSettings { ExcludedProcesses = [] },
            unknown,
            now));
    }

    [Fact]
    public void HoldForRestore_AllowsOwnerlessClipboardEvenWithExclusions()
    {
        var now = DateTimeOffset.UtcNow;
        var settings = new AppSettings { ExcludedProcesses = ["KeePass"] };
        var unknown = new ClipboardSource(null, null);

        Assert.True(CapturePrivacyPolicy.CanHoldForRestore(settings, unknown, hasOwnerWindow: false, sourceIsKnown: false, now));
        Assert.False(CapturePrivacyPolicy.CanHoldForRestore(settings, unknown, hasOwnerWindow: true, sourceIsKnown: false, now));
        Assert.True(CapturePrivacyPolicy.CanHoldForRestore(settings, new ClipboardSource("Weixin", "Weixin"), true, true, now));
        Assert.False(CapturePrivacyPolicy.CanHoldForRestore(settings, new ClipboardSource("KeePass", "KeePass"), true, true, now));
        Assert.False(CapturePrivacyPolicy.CanHoldForRestore(settings with { IsCapturePaused = true }, unknown, false, false, now));
    }
}
