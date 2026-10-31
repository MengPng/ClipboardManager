using ClipboardManager.Domain.Models;

namespace ClipboardManager.Domain.Contracts;

public interface IConfigSyncRepository
{
    Task<IReadOnlyList<PublishedConfig>> GetPublishedAsync(CancellationToken cancellationToken = default);
    Task<PublishedConfig?> GetPublishedAsync(Guid publishedId, CancellationToken cancellationToken = default);
    Task SavePublishedAsync(PublishedConfig config, CancellationToken cancellationToken = default);
    Task SetAutoPublishAsync(Guid publishedId, bool enabled, CancellationToken cancellationToken = default);
    /// <summary>停止发布：删除发布记录，返回需要清理的包文件路径；订阅方已安装的内容不受影响。</summary>
    Task<string?> DeletePublishedAsync(Guid publishedId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<PairedPeer>> GetPeersAsync(PeerRole role, CancellationToken cancellationToken = default);
    Task<PairedPeer?> FindActivePeerAsync(PeerRole role, string fingerprint, CancellationToken cancellationToken = default);
    Task SavePeerAsync(PairedPeer peer, CancellationToken cancellationToken = default);
    Task UpdatePeerAddressAsync(Guid peerId, string address, CancellationToken cancellationToken = default);
    Task RevokePeerAsync(Guid peerId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ConfigSubscription>> GetSubscriptionsAsync(CancellationToken cancellationToken = default);
    Task<ConfigSubscription?> GetSubscriptionAsync(Guid subscriptionId, CancellationToken cancellationToken = default);
    Task UpdateSubscriptionOptionsAsync(Guid subscriptionId, bool autoUpdate, bool stayOnCurrent, int? skippedVersion,
        CancellationToken cancellationToken = default);
    Task RecordCheckAsync(Guid subscriptionId, DateTimeOffset checkedAt, int? latestKnownVersion, string? lastError,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ConfigSnapshot>> GetSnapshotsAsync(Guid subscriptionId, CancellationToken cancellationToken = default);

    /// <summary>首次订阅：在一个事务内创建受管文件夹、条目、订阅记录与快照记录。</summary>
    Task<ConfigSubscription> CreateSubscriptionAsync(
        Guid workspaceId, PairedPeer peer, ManagedPackage package, string snapshotPath,
        CancellationToken cancellationToken = default);

    /// <summary>原子替换受管内容并更新订阅状态；任何一步失败都整体回滚。返回被淘汰的快照文件路径（只保留最近 3 个版本）。</summary>
    Task<IReadOnlyList<string>> ReplaceManagedContentAsync(
        Guid subscriptionId, ManagedPackage package, string snapshotPath, bool isRollback, int? skippedVersion,
        CancellationToken cancellationToken = default);

    /// <summary>删除订阅、受管文件夹与条目、快照记录；返回需要清理的快照文件路径。</summary>
    Task<IReadOnlyList<string>> DeleteSubscriptionAsync(Guid subscriptionId, CancellationToken cancellationToken = default);
}

/// <summary>在个人配置迁移包格式上增加“发布/受管导入”能力，网络路径使用 200 MB / 50 MB 上限。</summary>
public interface IConfigPackageService
{
    string ResolvePath(string relativeOrAbsolutePath);
    string ToRelativePath(string absolutePath);
    string NewTempPath();

    /// <summary>把所选文件夹打成一个包；<paramref name="folderIds"/> 为空表示全部个人（非受管）文件夹。</summary>
    Task<PackedConfig> PackFoldersAsync(Guid workspaceId, IReadOnlyList<Guid>? folderIds, Guid publishedId, string name, int version,
        string destinationPath, CancellationToken cancellationToken = default);

    /// <summary>校验网络包（路径安全、SHA-256、大小、链接、图片头）并物化为受管条目草稿。</summary>
    Task<ManagedPackage> ImportManagedAsync(string packagePath, Guid expectedPublishedId, int expectedVersion,
        string subscriptionKey, string packageHash, CancellationToken cancellationToken = default);

    Task<string> ComputeFileHashAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>把受管文件复制到用户自己的存储（imports/copies），供“复制到我的配置”使用，不随订阅更新或取消而消失。</summary>
    Task<string> CopyToUserStorageAsync(string sourcePath, CancellationToken cancellationToken = default);
}

public interface IConfigSyncEndpoint
{
    string DeviceName { get; }
    Task<(byte[] ServerNonce, string DeviceName)> PairBeginAsync(string clientFingerprint, string clientName,
        byte[] clientNonce, CancellationToken cancellationToken = default);
    Task<(byte[] ServerProof, string DeviceName)> PairFinishAsync(string clientFingerprint, string serverFingerprint,
        byte[] clientProof, CancellationToken cancellationToken = default);
    Task<bool> IsPinnedAsync(string clientFingerprint, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<PublicationInfo>> ListPublicationsAsync(CancellationToken cancellationToken = default);
    Task<ConfigManifest?> GetManifestAsync(Guid publishedId, CancellationToken cancellationToken = default);
    Task<string?> GetPackagePathAsync(Guid publishedId, int version, CancellationToken cancellationToken = default);
}

public interface IConfigSyncServer
{
    SyncServerStatus Status { get; }
    Task StartAsync(CancellationToken cancellationToken = default);
    Task StopAsync();
}

public interface IConfigSyncClient
{
    Task<HelloInfo> HelloAsync(string address, CancellationToken cancellationToken = default);
    Task<PairResult> PairAsync(string address, string code, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<PublicationInfo>> ListPublicationsAsync(string address, string pinnedFingerprint,
        CancellationToken cancellationToken = default);
    Task<ConfigManifest> GetManifestAsync(string address, string pinnedFingerprint, Guid publishedId,
        CancellationToken cancellationToken = default);
    Task DownloadPackageAsync(string address, string pinnedFingerprint, Guid publishedId, int version,
        string destinationPath, CancellationToken cancellationToken = default);
}

public interface IConfigDiscovery
{
    IReadOnlyList<DiscoveredPublisher> GetPublishers();
    event EventHandler? Changed;
    /// <summary>开始监听其他设备的组播广播（打开订阅页时启动）。</summary>
    void StartListening();
    void StopListening();
    /// <summary>开始/停止广播本机发布（仅名称与版本号、指纹前缀）。</summary>
    void SetAnnouncement(IReadOnlyList<PublicationAnnouncement> publications);
}

public interface IConfigSyncIdentity
{
    string DeviceName { get; }
    /// <summary>本机自签证书的 SHA-256 指纹（十六进制，首次访问时生成并持久化）。</summary>
    string Fingerprint { get; }
}
