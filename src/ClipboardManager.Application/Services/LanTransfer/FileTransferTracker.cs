using ClipboardManager.Domain.Models;

namespace ClipboardManager.Application.Services.LanTransfer;

/// <summary>
/// 发送与接收任务的内存状态（ADR-0008）：只供界面显示与取消，不入库、不入同步 Outbox。
/// 进度事件做了节流（同一任务 200 ms 内只通知一次，状态变化立即通知）。
/// </summary>
public sealed class FileTransferTracker(TimeProvider time)
{
    private const int MaxFinished = 30;
    private static readonly TimeSpan ProgressThrottle = TimeSpan.FromMilliseconds(200);

    private readonly object sync = new();
    private readonly Dictionary<Guid, Entry> entries = [];

    public event EventHandler? Changed;

    public IReadOnlyList<FileTransferSnapshot> GetAll()
    {
        lock (sync)
            return entries.Values.Select(entry => entry.Snapshot)
                .OrderByDescending(snapshot => !snapshot.IsFinished)
                .ThenByDescending(snapshot => snapshot.UpdatedAt)
                .ToList();
    }

    public TrackedTransfer Begin(FileTransferDirection direction, string peerName, string peerFingerprint, string fileName, long size,
        FileTransferState state, long transferred = 0, CancellationToken linked = default)
    {
        var id = Guid.NewGuid();
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(linked);
        lock (sync)
        {
            entries[id] = new Entry(
                new FileTransferSnapshot(id, direction, peerName, peerFingerprint.ToUpperInvariant(), fileName, size, transferred, state, null, null, time.GetUtcNow()),
                cancellation, DateTimeOffset.MinValue);
            TrimFinished();
        }
        Changed?.Invoke(this, EventArgs.Empty);
        return new TrackedTransfer(id, cancellation.Token);
    }

    public void Progress(Guid id, long transferred)
    {
        var notify = false;
        lock (sync)
        {
            if (!entries.TryGetValue(id, out var entry) || entry.Snapshot.IsFinished) return;
            var now = time.GetUtcNow();
            entry.Snapshot = entry.Snapshot with { Transferred = transferred, UpdatedAt = now };
            if (now - entry.LastNotified >= ProgressThrottle)
            {
                entry.LastNotified = now;
                notify = true;
            }
        }
        if (notify) Changed?.Invoke(this, EventArgs.Empty);
    }

    public void SetState(Guid id, FileTransferState state, string? message = null, long? transferred = null, string? savedPath = null)
    {
        lock (sync)
        {
            if (!entries.TryGetValue(id, out var entry)) return;
            entry.Snapshot = entry.Snapshot with
            {
                State = state,
                Message = message,
                Transferred = transferred ?? entry.Snapshot.Transferred,
                SavedPath = savedPath ?? entry.Snapshot.SavedPath,
                UpdatedAt = time.GetUtcNow()
            };
            entry.LastNotified = time.GetUtcNow();
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>取消一个进行中的任务；已收到的部分会保留，重新发送同一文件可以续传。</summary>
    public void Cancel(Guid id)
    {
        lock (sync)
        {
            if (!entries.TryGetValue(id, out var entry) || entry.Snapshot.IsFinished) return;
            entry.Cancellation.Cancel();
        }
    }

    /// <summary>清除已结束的记录；给出指纹时只清除与那台设备之间的。</summary>
    public void ClearFinished(string? peerFingerprint = null)
    {
        lock (sync)
            foreach (var id in entries.Where(pair => pair.Value.Snapshot.IsFinished &&
                         (peerFingerprint is null || pair.Value.Snapshot.PeerFingerprint.Equals(peerFingerprint, StringComparison.OrdinalIgnoreCase))).Select(pair => pair.Key).ToList())
                entries.Remove(id);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void TrimFinished()
    {
        var finished = entries.Values.Where(entry => entry.Snapshot.IsFinished)
            .OrderByDescending(entry => entry.Snapshot.UpdatedAt).Skip(MaxFinished).Select(entry => entry.Snapshot.Id).ToList();
        foreach (var id in finished) entries.Remove(id);
    }

    private sealed class Entry(FileTransferSnapshot snapshot, CancellationTokenSource cancellation, DateTimeOffset lastNotified)
    {
        public FileTransferSnapshot Snapshot { get; set; } = snapshot;
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public DateTimeOffset LastNotified { get; set; } = lastNotified;
    }
}

/// <summary>一个已登记的任务：Token 在用户点“取消”或调用方取消时触发。</summary>
public readonly record struct TrackedTransfer(Guid Id, CancellationToken Token);
