using ClipboardManager.Domain.Models;

namespace ClipboardManager.Domain.Contracts;

/// <summary>接收方：53318 监听收到已匹配设备的传输请求时调用（ADR-0008）。</summary>
public interface IFileTransferInbox
{
    /// <summary>检查开关、文件名与磁盘空间，必要时请用户确认；同意后返回传输编号与续传偏移。</summary>
    Task<OfferDecision> OfferAsync(string peerFingerprint, FileOffer offer, CancellationToken cancellationToken = default);

    /// <summary>
    /// 从 <paramref name="offset"/> 起接收 <paramref name="length"/> 字节并校验整文件哈希，成功后返回保存的文件名。
    /// 每写入一块调用一次 <paramref name="keepAlive"/>，用于重置空闲超时。
    /// </summary>
    Task<string> ReceiveAsync(string peerFingerprint, Guid transferId, long offset, long length, Stream body, Action keepAlive,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// 已结束的传输记录（文件名、大小、方向、对方名称与指纹、结果、保存位置），重启后仍可查看。
/// 不含文件内容；不入 SQLite、不入同步 Outbox。读写失败只影响记录，不影响传输。
/// </summary>
public interface IFileTransferHistoryStore
{
    IReadOnlyList<FileTransferSnapshot> Load();
    void Save(IReadOnlyList<FileTransferSnapshot> finished);
}

/// <summary>由界面实现：弹出“是否接收”确认。返回 false 表示拒绝。</summary>
public interface IIncomingFilePrompt
{
    Task<bool> ConfirmAsync(IncomingFileRequest request, CancellationToken cancellationToken = default);
}
