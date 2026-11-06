using System.Security.Cryptography;
using ClipboardManager.Application.Services.LanTransfer;
using ClipboardManager.Domain.Contracts;
using ClipboardManager.Domain.Models;
using Microsoft.Extensions.Logging;

namespace ClipboardManager.Infrastructure.ConfigSync;

/// <summary>
/// 接收已匹配设备发来的文件（ADR-0008）：先确认，再写入保存目录下隐藏的 .part 文件，
/// 收完计算整文件 SHA-256，一致才改名为最终文件。中断时保留 .part，同一文件重发可以续传。
/// 收到的文件不进入剪切板历史、数据库或同步 Outbox，也从不自动打开。
/// </summary>
public sealed class LanFileInbox(
    IConfigSyncRepository repository,
    IAppSettingsStore settingsStore,
    IIncomingFilePrompt prompt,
    FileTransferTracker tracker,
    TimeProvider time,
    ILogger<LanFileInbox> logger) : IFileTransferInbox
{
    private readonly object sync = new();
    private readonly Dictionary<Guid, Accepted> accepted = [];
    private readonly SemaphoreSlim receiving = new(FileTransferPolicy.MaxConcurrentReceives, FileTransferPolicy.MaxConcurrentReceives);

    public async Task<OfferDecision> OfferAsync(string peerFingerprint, FileOffer offer, CancellationToken cancellationToken = default)
    {
        var fingerprint = peerFingerprint.ToUpperInvariant();
        var peer = await FindPeerAsync(fingerprint, cancellationToken)
                   ?? throw new FileTransferException(FileTransferError.NotMatched, "对方没有和这台电脑匹配，或匹配已被移除。");
        var settings = await settingsStore.LoadAsync(cancellationToken);
        if (!settings.LanReceiveEnabled)
            throw new FileTransferException(FileTransferError.ReceiveDisabled, "对方关闭了“接收文件”。");
        if (offer.Size < 0 || !FileTransferPolicy.IsWellFormedHash(offer.Sha256) || string.IsNullOrWhiteSpace(offer.Name))
            throw new FileTransferException(FileTransferError.Protocol, "文件信息不完整，无法接收。");

        var name = ConfigSyncPolicy.SanitizeFileName(offer.Name);
        var hash = offer.Sha256.ToUpperInvariant();
        var folder = LanTransferPaths.ResolveReceiveFolder(settings);
        try { Directory.CreateDirectory(folder); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new FileTransferException(FileTransferError.Local, "对方的保存位置不可用，请对方在“共享 → 传输”更换保存位置。", ex);
        }
        var partPath = Path.Combine(folder, FileTransferPolicy.PartFileName(fingerprint, hash, offer.Size));

        // 30 分钟内同意过的同一文件：直接续传，不再询问。
        lock (sync)
        {
            PruneExpired();
            foreach (var canceled in accepted.Values.Where(item => item.Tracked.Token.IsCancellationRequested).ToList())
                accepted.Remove(canceled.TransferId);
            var reuse = accepted.Values.FirstOrDefault(item =>
                item.Fingerprint == fingerprint && item.Hash == hash && item.Size == offer.Size && item.PartPath == partPath);
            if (reuse is not null)
                return new OfferDecision(reuse.TransferId, PartLength(partPath, offer.Size));
        }

        var existing = PartLength(partPath, offer.Size);
        EnsureFreeSpace(folder, offer.Size - existing);

        if (!settings.LanAutoAccept)
        {
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            wait.CancelAfter(FileTransferPolicy.DecisionTimeout);
            bool approved;
            try
            {
                approved = await prompt.ConfirmAsync(
                    new IncomingFileRequest(peer.Name, name, offer.Size, ConfigSyncPolicy.IsHighRiskFile(name), folder), wait.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new FileTransferException(FileTransferError.Declined, "对方没有在 2 分钟内确认，已取消发送。");
            }
            if (!approved) throw new FileTransferException(FileTransferError.Declined, "对方拒绝接收这个文件。");
        }

        var tracked = tracker.Begin(FileTransferDirection.Receive, peer.Name, fingerprint, name, offer.Size, FileTransferState.WaitingForAccept, existing);
        var item = new Accepted(Guid.NewGuid(), fingerprint, hash, offer.Size, name, folder, partPath, time.GetUtcNow(), tracked);
        lock (sync) accepted[item.TransferId] = item;
        logger.LogInformation("已同意接收文件 {TransferId}（{Size} 字节）", item.TransferId, offer.Size);
        return new OfferDecision(item.TransferId, existing);
    }

    public async Task<string> ReceiveAsync(string peerFingerprint, Guid transferId, long offset, long length, Stream body, Action keepAlive,
        CancellationToken cancellationToken = default)
    {
        Accepted? item;
        lock (sync) accepted.TryGetValue(transferId, out item);
        if (item is null || item.Fingerprint != peerFingerprint.ToUpperInvariant())
            throw new FileTransferException(FileTransferError.UnknownTransfer, "这次传输已失效，请重新发送。");
        if (offset + length != item.Size)
            throw new FileTransferException(FileTransferError.Protocol, "上传的范围与文件大小不一致。");
        if (!await receiving.WaitAsync(0, cancellationToken))
            throw new FileTransferException(FileTransferError.Busy, "对方正在接收其他文件，请稍后再发。");

        var id = item.Tracked.Id;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, item.Tracked.Token);
        var token = linked.Token;
        long written = offset;
        try
        {
            var have = PartLength(item.PartPath, item.Size);
            if (have != offset)
                throw new FileTransferException(FileTransferError.BadOffset, "续传位置与对方已有的部分不一致，将自动重试。")
                {
                    ExpectedOffset = have
                };

            tracker.SetState(id, FileTransferState.Transferring, null, offset);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[1 << 16];
            await using (var part = new FileStream(item.PartPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1 << 16, true))
            {
                if (offset == 0) TryHide(item.PartPath);
                // 续传：先把已有部分算进哈希。
                long hashed = 0;
                while (hashed < offset)
                {
                    var read = await part.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, offset - hashed)), token);
                    if (read == 0) throw new FileTransferException(FileTransferError.BadOffset, "已收到的部分不完整。") { ExpectedOffset = 0 };
                    hash.AppendData(buffer, 0, read);
                    hashed += read;
                    keepAlive();
                }
                part.SetLength(offset);
                part.Seek(offset, SeekOrigin.Begin);
                while (written < item.Size)
                {
                    var read = await body.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, item.Size - written)), token);
                    if (read == 0) throw new IOException("对方在文件发送完成前断开了连接。");
                    await part.WriteAsync(buffer.AsMemory(0, read), token);
                    hash.AppendData(buffer, 0, read);
                    written += read;
                    keepAlive();
                    tracker.Progress(id, written);
                }
                await part.FlushAsync(token);
            }

            tracker.SetState(id, FileTransferState.Verifying, "正在校验…", written);
            var actual = Convert.ToHexString(hash.GetHashAndReset());
            if (!string.Equals(actual, item.Hash, StringComparison.OrdinalIgnoreCase))
            {
                TryDelete(item.PartPath);
                Forget(item);
                tracker.SetState(id, FileTransferState.Failed, "校验不一致，已丢弃收到的内容。");
                throw new FileTransferException(FileTransferError.HashMismatch, "对方收到的内容与原文件校验不一致，已丢弃，请重新发送。");
            }

            string final;
            lock (sync)
            {
                final = FileTransferPolicy.UniquePath(item.Folder, item.FileName);
                File.SetAttributes(item.PartPath, FileAttributes.Normal);
                File.Move(item.PartPath, final);
            }
            Forget(item);
            tracker.SetState(id, FileTransferState.Completed, null, item.Size, final);
            logger.LogInformation("文件 {TransferId} 接收完成（{Size} 字节）", transferId, item.Size);
            return Path.GetFileName(final);
        }
        catch (FileTransferException)
        {
            throw;
        }
        catch (OperationCanceledException) when (item.Tracked.Token.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            // 本机用户取消：忘掉这次同意，对方再发时会重新询问（已收到的部分仍可续传）。
            Forget(item);
            tracker.SetState(id, FileTransferState.Canceled, "已取消。已收到的部分会保留，对方重新发送即可续传。", written);
            throw new FileTransferException(FileTransferError.Declined, "对方取消了接收。");
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or UnauthorizedAccessException)
        {
            logger.LogInformation("文件 {TransferId} 接收中断：{Type}", transferId, ex.GetType().Name);
            tracker.SetState(id, FileTransferState.Failed, "传输中断。已收到的部分会保留，重新发送即可续传。", written);
            throw;
        }
        finally
        {
            receiving.Release();
        }
    }

    private async Task<PairedPeer?> FindPeerAsync(string fingerprint, CancellationToken cancellationToken) =>
        await repository.FindActivePeerAsync(PeerRole.Subscriber, fingerprint, cancellationToken)
        ?? await repository.FindActivePeerAsync(PeerRole.Publisher, fingerprint, cancellationToken);

    private static long PartLength(string path, long size)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return 0;
            if (info.Length <= size) return info.Length;
            info.Delete();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return 0;
    }

    private static void EnsureFreeSpace(string folder, long needed)
    {
        long available;
        try { available = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(folder))!).AvailableFreeSpace; }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException) { return; }
        if (available < needed + FileTransferPolicy.FreeSpaceReserve)
            throw new FileTransferException(FileTransferError.InsufficientSpace,
                $"对方磁盘空间不足：需要 {FormatSize(needed)}，剩余 {FormatSize(available)}。");
    }

    internal static string FormatSize(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.##} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0.#} MB",
        >= 1L << 10 => $"{bytes / 1024.0:0} KB",
        _ => $"{bytes} 字节"
    };

    private void PruneExpired()
    {
        var now = time.GetUtcNow();
        foreach (var stale in accepted.Values.Where(item => now - item.AcceptedAt > FileTransferPolicy.AcceptedReuse).ToList())
            accepted.Remove(stale.TransferId);
    }

    private void Forget(Accepted item)
    {
        lock (sync) accepted.Remove(item.TransferId);
    }

    private static void TryHide(string path)
    {
        try { File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.Hidden); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (!File.Exists(path)) return;
            File.SetAttributes(path, FileAttributes.Normal);
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private sealed record Accepted(
        Guid TransferId,
        string Fingerprint,
        string Hash,
        long Size,
        string FileName,
        string Folder,
        string PartPath,
        DateTimeOffset AcceptedAt,
        TrackedTransfer Tracked);
}
