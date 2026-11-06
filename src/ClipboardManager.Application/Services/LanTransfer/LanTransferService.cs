using System.Security.Cryptography;
using ClipboardManager.Domain.Contracts;
using ClipboardManager.Domain.Models;
using Microsoft.Extensions.Logging;

namespace ClipboardManager.Application.Services.LanTransfer;

/// <summary>
/// 把本机文件发给一台已匹配设备（ADR-0008）：逐个文件计算 SHA-256 → 请对方确认 → 从对方已有位置续传上传。
/// 网络中断、超时或续传位置不一致时自动重新请求并续传，最多 <see cref="FileTransferPolicy.MaxSendRetries"/> 次。
/// 只发送用户显式选择的文件；不读取、不上传剪切板历史。
/// </summary>
public sealed class LanTransferService(
    IConfigSyncRepository repository,
    IConfigSyncClient client,
    FileTransferTracker tracker,
    TimeProvider time,
    ILogger<LanTransferService> logger)
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(2);

    public FileTransferTracker Tracker => tracker;

    /// <summary>发送多个文件；目录与不存在的路径会被跳过。单个文件失败不影响其他文件。</summary>
    public async Task<FileSendSummary> SendFilesAsync(string fingerprint, IReadOnlyList<string> paths,
        CancellationToken cancellationToken = default)
    {
        var peer = await FindPeerAsync(fingerprint, cancellationToken)
                   ?? throw new FileTransferException(FileTransferError.NotMatched, "这台设备已不在“已匹配的设备”中。");
        var files = paths.Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var skipped = paths.Count - files.Count;
        int sent = 0, failed = 0;
        string? lastError = null;
        foreach (var path in files)
        {
            if (cancellationToken.IsCancellationRequested) break;
            var result = await SendOneAsync(peer, path, cancellationToken);
            if (result is null) sent++;
            else
            {
                failed++;
                lastError = result;
            }
        }
        return new FileSendSummary(sent, failed, skipped, lastError);
    }

    /// <summary>发送一个文件；成功返回 null，否则返回给用户看的原因。</summary>
    private async Task<string?> SendOneAsync(PairedPeer peer, string path, CancellationToken cancellationToken)
    {
        var info = new FileInfo(path);
        var name = ConfigSyncPolicy.SanitizeFileName(info.Name);
        var tracked = tracker.Begin(FileTransferDirection.Send, peer.Name, peer.Fingerprint, name, info.Length, FileTransferState.Preparing,
            linked: cancellationToken);
        var id = tracked.Id;
        var token = tracked.Token;
        long transferred = 0;
        try
        {
            tracker.SetState(id, FileTransferState.Preparing, "正在计算校验值…", 0);
            var hash = await HashAsync(path, value => tracker.Progress(id, value), token);
            var size = new FileInfo(path).Length;
            var offer = new FileOffer(name, size, hash);

            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    var address = await AddressAsync(peer, token);
                    tracker.SetState(id, FileTransferState.WaitingForAccept, "等待对方确认…", 0);
                    var decision = await client.OfferFileAsync(address, peer.Fingerprint, offer, token);
                    transferred = decision.Offset;
                    tracker.SetState(id, FileTransferState.Transferring,
                        decision.Offset > 0 ? "从上次中断处继续…" : null, decision.Offset);
                    var progress = new Progress(value =>
                    {
                        transferred = value;
                        tracker.Progress(id, value);
                    });
                    await client.UploadFileAsync(address, peer.Fingerprint, decision.TransferId, path, decision.Offset,
                        progress, token);
                    tracker.SetState(id, FileTransferState.Completed, null, size);
                    return null;
                }
                catch (Exception ex) when (attempt < FileTransferPolicy.MaxSendRetries && IsRetryable(ex) && !token.IsCancellationRequested)
                {
                    logger.LogInformation("发送文件 {TransferId} 第 {Attempt} 次中断（{Type}），稍后续传", id, attempt + 1, ex.GetType().Name);
                    tracker.SetState(id, FileTransferState.Transferring, "连接中断，正在重试…", transferred);
                    await Task.Delay(RetryDelay, time, token);
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            const string message = "已取消。对方已收到的部分会保留，重新发送同一文件可以续传。";
            tracker.SetState(id, FileTransferState.Canceled, message, transferred);
            return message;
        }
        catch (FileTransferException ex) when (ex.Error == FileTransferError.Declined)
        {
            tracker.SetState(id, FileTransferState.Declined, ex.Message, transferred);
            return ex.Message;
        }
        catch (Exception ex) when (ex is FileTransferException or ConfigSyncException)
        {
            tracker.SetState(id, FileTransferState.Failed, ex.Message, transferred);
            return ex.Message;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning("读取待发送文件失败：{Type}", ex.GetType().Name);
            const string message = "无法读取这个文件，可能已被移动、删除或正被其他程序独占。";
            tracker.SetState(id, FileTransferState.Failed, message, transferred);
            return message;
        }
    }

    private static bool IsRetryable(Exception exception) => exception switch
    {
        ConfigSyncException sync => sync.Error is ConfigSyncError.Unreachable or ConfigSyncError.Timeout,
        FileTransferException transfer => transfer.Error is FileTransferError.BadOffset or FileTransferError.Busy
            or FileTransferError.UnknownTransfer or FileTransferError.Unreachable,
        IOException => true,
        _ => false
    };

    private async Task<PairedPeer?> FindPeerAsync(string fingerprint, CancellationToken cancellationToken)
    {
        var normalized = fingerprint.ToUpperInvariant();
        return await repository.FindActivePeerAsync(PeerRole.Publisher, normalized, cancellationToken)
               ?? await repository.FindActivePeerAsync(PeerRole.Subscriber, normalized, cancellationToken);
    }

    /// <summary>每次重试都重新读取地址：常驻发现可能已经根据广播更新了对方的新 IP。</summary>
    private async Task<string> AddressAsync(PairedPeer peer, CancellationToken cancellationToken)
    {
        var publisher = await repository.FindActivePeerAsync(PeerRole.Publisher, peer.Fingerprint, cancellationToken);
        var subscriber = await repository.FindActivePeerAsync(PeerRole.Subscriber, peer.Fingerprint, cancellationToken);
        return publisher?.LastAddress ?? subscriber?.LastAddress ?? peer.LastAddress
               ?? throw new FileTransferException(FileTransferError.Unreachable,
                   "还不知道这台设备的地址。请确认对方开着本软件并在同一局域网，或重新匹配一次。");
    }

    private static async Task<string> HashAsync(string path, Action<long> progress, CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, true);
        var buffer = new byte[1 << 20];
        long total = 0;
        int read;
        while ((read = await stream.ReadAsync(buffer, cancellationToken)) > 0)
        {
            hash.AppendData(buffer, 0, read);
            total += read;
            progress(total);
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    /// <summary>同步回调的进度（System.Progress 会切到捕获的上下文，这里不需要）。</summary>
    private sealed class Progress(Action<long> report) : IProgress<long>
    {
        public void Report(long value) => report(value);
    }
}
