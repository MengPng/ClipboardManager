using ClipboardManager.Application.Services.LanTransfer;
using ClipboardManager.Domain.Models;

namespace ClipboardManager.Application.Tests;

/// <summary>传输记录按设备指纹归属：设备页只列这台设备的记录，“清除已完成”也只清这台设备的。</summary>
public sealed class FileTransferTrackerTests
{
    private const string Office = "a1b2c3d4e5f6";
    private const string Home = "0f9e8d7c6b5a";

    [Fact]
    public void Begin_RecordsThePeerFingerprintInUpperCase()
    {
        var tracker = new FileTransferTracker(TimeProvider.System);

        tracker.Begin(FileTransferDirection.Send, "办公室电脑", Office, "a.txt", 10, FileTransferState.Preparing);

        Assert.Equal(Office.ToUpperInvariant(), Assert.Single(tracker.GetAll()).PeerFingerprint);
    }

    [Fact]
    public void ClearFinished_WithAFingerprint_KeepsOtherDevicesAndRunningTransfers()
    {
        var tracker = new FileTransferTracker(TimeProvider.System);
        var officeDone = tracker.Begin(FileTransferDirection.Send, "办公室电脑", Office, "done.txt", 10, FileTransferState.Preparing);
        tracker.Begin(FileTransferDirection.Send, "办公室电脑", Office, "running.txt", 10, FileTransferState.Transferring);
        var homeDone = tracker.Begin(FileTransferDirection.Receive, "家里", Home, "home.txt", 10, FileTransferState.WaitingForAccept);
        tracker.SetState(officeDone.Id, FileTransferState.Completed);
        tracker.SetState(homeDone.Id, FileTransferState.Completed);

        tracker.ClearFinished(Office);

        var left = tracker.GetAll().Select(item => item.FileName).OrderBy(name => name).ToList();
        Assert.Equal(["home.txt", "running.txt"], left);

        tracker.ClearFinished();
        Assert.Equal("running.txt", Assert.Single(tracker.GetAll()).FileName);
    }
}
