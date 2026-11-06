namespace ClipboardManager.Domain.Models;

/// <summary>固定匹配码的防暴力规则（ADR-0008）：连续错 5 次锁定，锁定时间翻倍；累计错 20 次停用，直到更换。</summary>
public static class LanMatchPolicy
{
    public const int FailuresPerLock = 5;
    public const int MaxTotalFailures = 20;
    public static readonly TimeSpan FirstLock = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan MaxLock = TimeSpan.FromMinutes(60);

    /// <summary>超过该时间没有收到广播即视为离线。</summary>
    public static readonly TimeSpan OnlineWindow = TimeSpan.FromSeconds(20);

    public const string VaultKey = "ConfigSync/MatchCode";
    public const string DisabledSuffix = ":disabled";
    public const int MaxDeviceNameLength = 40;

    /// <summary>第 <paramref name="lockCount"/> 次锁定的时长：1、2、4…分钟，最长 60 分钟。</summary>
    public static TimeSpan LockDuration(int lockCount)
    {
        var minutes = Math.Min(LanMatchPolicy.MaxLock.TotalMinutes, Math.Pow(2, Math.Max(0, lockCount - 1)) * FirstLock.TotalMinutes);
        return TimeSpan.FromMinutes(minutes);
    }

    public static string CleanDeviceName(string? name)
    {
        var text = new string((name ?? string.Empty).Where(ch => !char.IsControl(ch)).ToArray()).Trim();
        return text.Length > MaxDeviceNameLength ? text[..MaxDeviceNameLength] : text;
    }
}

/// <summary>本机匹配码的当前状态；Code 只在本机界面显示，不上网。</summary>
public sealed record MatchCodeStatus(string? Code, bool Disabled, DateTimeOffset? LockedUntil, int TotalFailures);

/// <summary>一台已匹配设备（同一证书指纹的全部有效配对记录合并为一项）。</summary>
public sealed record MatchedDevice(
    string Fingerprint,
    string Name,
    string? Address,
    DateTimeOffset PairedAt,
    bool CanBrowse,
    bool IsOnline,
    IReadOnlyList<PairedPeer> Peers);

public static class FileTransferPolicy
{
    public const string DefaultFolderName = "剪切板传输";
    public const string PartPrefix = ".cm-transfer-";
    public const string PartExtension = ".part";
    public const long FreeSpaceReserve = 64L * 1024 * 1024;
    public const int MaxConcurrentReceives = 2;
    public const int MaxSendRetries = 3;
    public static readonly TimeSpan DecisionTimeout = TimeSpan.FromSeconds(120);
    public static readonly TimeSpan AcceptedReuse = TimeSpan.FromMinutes(30);

    public static bool IsWellFormedHash(string? value) =>
        value is { Length: 64 } && value.All(Uri.IsHexDigit);

    /// <summary>未完成文件的名字只由发送方指纹、内容哈希与大小决定，同一文件重发即可续传。</summary>
    public static string PartFileName(string peerFingerprint, string sha256, long size) =>
        $"{PartPrefix}{peerFingerprint[..Math.Min(12, peerFingerprint.Length)]}-{sha256[..16]}-{size}{PartExtension}".ToLowerInvariant();

    /// <summary>在目录中给文件名找一个不冲突的名字：name.ext、name (1).ext、name (2).ext…</summary>
    public static string UniquePath(string folder, string fileName)
    {
        var candidate = Path.Combine(folder, fileName);
        if (!File.Exists(candidate) && !Directory.Exists(candidate)) return candidate;
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        for (var index = 1; index < 10_000; index++)
        {
            candidate = Path.Combine(folder, $"{stem} ({index}){extension}");
            if (!File.Exists(candidate) && !Directory.Exists(candidate)) return candidate;
        }
        return Path.Combine(folder, $"{stem} ({Guid.NewGuid():N}){extension}");
    }
}

public enum FileTransferDirection
{
    Send,
    Receive
}

public enum FileTransferState
{
    Preparing,
    WaitingForAccept,
    Transferring,
    Verifying,
    Completed,
    Failed,
    Declined,
    Canceled
}

public enum FileTransferError
{
    NotMatched,
    ReceiveDisabled,
    Declined,
    Busy,
    InsufficientSpace,
    BadOffset,
    HashMismatch,
    UnknownTransfer,
    Protocol,
    Unreachable,
    Local
}

/// <summary>文件传输失败；Message 是可以直接展示的中文原因。</summary>
public sealed class FileTransferException(FileTransferError error, string message, Exception? inner = null)
    : Exception(message, inner)
{
    public FileTransferError Error { get; } = error;

    /// <summary>接收方实际已有的字节数（偏移不一致时由接收方返回）。</summary>
    public long? ExpectedOffset { get; init; }
}

/// <summary>发送方的文件说明；Sha256 为整文件的十六进制哈希。</summary>
public sealed record FileOffer(string Name, long Size, string Sha256);

/// <summary>接收方同意接收：后续上传使用 TransferId，从 Offset 开始（续传）。</summary>
public sealed record OfferDecision(Guid TransferId, long Offset);

/// <summary>需要用户确认的接收请求。</summary>
public sealed record IncomingFileRequest(string PeerName, string FileName, long Size, bool IsHighRisk, string SaveFolder);

/// <summary>界面展示用的传输快照（只在内存中，不入库）。</summary>
public sealed record FileTransferSnapshot(
    Guid Id,
    FileTransferDirection Direction,
    string PeerName,
    string PeerFingerprint,
    string FileName,
    long Size,
    long Transferred,
    FileTransferState State,
    string? Message,
    string? SavedPath,
    DateTimeOffset UpdatedAt)
{
    public bool IsFinished => State is FileTransferState.Completed or FileTransferState.Failed
        or FileTransferState.Declined or FileTransferState.Canceled;
}

public sealed record FileSendSummary(int Sent, int Failed, int Skipped, string? LastError);
