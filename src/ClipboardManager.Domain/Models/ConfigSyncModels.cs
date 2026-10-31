namespace ClipboardManager.Domain.Models;

public enum PeerRole
{
    /// <summary>对方订阅了我发布的配置（我是发布者）。</summary>
    Subscriber = 0,
    /// <summary>我订阅了对方发布的配置（对方是发布者）。</summary>
    Publisher = 1
}

public enum PeerStatus
{
    Active = 0,
    Revoked = 1
}

public sealed record PublishedConfig(
    Guid Id,
    Guid WorkspaceId,
    IReadOnlyList<Guid> FolderIds,
    bool AllFolders,
    string Name,
    int CurrentVersion,
    string ContentHash,
    string PackagePath,
    long PackageBytes,
    string ManifestJson,
    bool AutoPublish,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record PairedPeer(
    Guid Id,
    PeerRole Role,
    string Name,
    string Fingerprint,
    string? LastAddress,
    DateTimeOffset PairedAt,
    PeerStatus Status);

public sealed record ConfigSubscription(
    Guid Id,
    Guid WorkspaceId,
    Guid PeerId,
    Guid PublishedId,
    string Name,
    int LocalVersion,
    Guid ManagedFolderId,
    bool AutoUpdate,
    bool StayOnCurrent,
    int? SkippedVersion,
    DateTimeOffset? LastCheckedAt,
    int? LatestKnownVersion,
    string? LastError,
    string ManifestJson,
    string ContentHash,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record ConfigSnapshot(Guid SubscriptionId, int Version, string SnapshotPath, DateTimeOffset CreatedAt);

/// <summary>清单里的单个条目摘要；<see cref="Digest"/> 用于按条目 ID 判断“修改”。</summary>
public sealed record ConfigManifestItem(
    Guid Id,
    int Type,
    string? Title,
    string Digest,
    long Size,
    IReadOnlyList<string> RiskyFiles,
    Guid FolderId = default);

/// <summary>清单里的文件夹摘要（只含至少有一个条目的文件夹）。</summary>
public sealed record ConfigManifestFolder(Guid Id, string Name, int ItemCount);

public sealed record ConfigManifest(
    int ProtocolVersion,
    Guid PublishedId,
    string Name,
    int Version,
    string Hash,
    long PackageBytes,
    DateTimeOffset PublishedAt,
    IReadOnlyList<ConfigManifestItem> Items,
    IReadOnlyList<ConfigManifestFolder>? Folders = null)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public IReadOnlyList<ConfigManifestFolder> FolderList => Folders ?? [];

    public bool HasRiskyFiles => Items.Any(item => item.RiskyFiles.Count > 0);
}

/// <summary>发布者可订阅配置的索引项（GET /v1/manifest 不带 id 时返回）。</summary>
public sealed record PublicationInfo(Guid PublishedId, string Name, int Version, string Hash);

public sealed record DiscoveredPublisher(
    string DeviceName,
    string Address,
    string FingerprintPrefix,
    int ProtocolVersion,
    IReadOnlyList<PublicationAnnouncement> Publications,
    DateTimeOffset LastSeen);

/// <summary>组播广播里的发布条目：只有名称与版本号。</summary>
public sealed record PublicationAnnouncement(string Name, int Version);

public sealed record ConfigChangeSummary(
    int Added,
    int Modified,
    int Removed,
    IReadOnlyList<string> AddedTitles,
    IReadOnlyList<string> ModifiedTitles,
    IReadOnlyList<string> RemovedTitles,
    int FoldersAdded = 0,
    int FoldersRemoved = 0);

public enum ConfigUpdateState
{
    UpToDate,
    UpdateAvailable,
    Skipped,
    Stayed,
    PublisherOffline,
    Failed
}

public sealed record ConfigUpdateCheck(
    ConfigUpdateState State,
    string Message,
    ConfigManifest? Remote = null,
    ConfigChangeSummary? Changes = null);

public sealed record PublishResult(PublishedConfig Config, bool Changed, int FileCount, int SkippedMissingFiles);

public sealed record PairResult(string ServerFingerprint, string ServerName);

public sealed record HelloInfo(int ProtocolVersion, string DeviceName, string Fingerprint);

public sealed record PairingCodeInfo(string Code, DateTimeOffset ExpiresAt);

public sealed record SyncServerStatus(bool IsRunning, int Port, string Fingerprint, string? LastError);

/// <summary>已校验网络包中的一个受管条目，尚未写入数据库。</summary>
public sealed record ManagedItemDraft(
    Guid RemoteId,
    int Order,
    CollectionItemType ItemType,
    string? Title,
    string? TextContent,
    string? Url,
    string? Description,
    string? LocalPath,
    string? ThumbnailPath,
    string? FileManifest,
    string? ContentHash,
    long ContentSize,
    Guid RemoteFolderId = default);

/// <summary>网络包里的一个文件夹（<see cref="RemoteId"/> 是发布方的文件夹 ID，用于更新时对应本机受管文件夹）。</summary>
public sealed record ManagedFolderDraft(Guid RemoteId, string Name, int Order);

public sealed record ManagedPackage(
    ConfigManifest Manifest,
    IReadOnlyList<ManagedFolderDraft> Folders,
    IReadOnlyList<ManagedItemDraft> Items,
    string MaterializedRoot);

public sealed record PackedConfig(ConfigManifest Manifest, string ManifestJson, int FileCount, int MissingFiles);
