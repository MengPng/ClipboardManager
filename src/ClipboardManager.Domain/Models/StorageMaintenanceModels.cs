namespace ClipboardManager.Domain.Models;

public sealed record StorageUsageStats(
    long DatabaseBytes,
    long BlobBytes,
    long ThumbnailBytes,
    int BlobFileCount,
    int ThumbnailFileCount)
{
    public long TotalBytes => DatabaseBytes + BlobBytes + ThumbnailBytes;
}

public sealed record BackupValidationResult(
    bool IsValid,
    string? Error,
    int? SchemaVersion,
    DateTimeOffset? CreatedAtUtc);

public sealed record AssetCleanupPreview(
    IReadOnlyList<string> RelativePaths,
    long ReclaimableBytes);

public sealed record AssetCleanupResult(
    int DeletedFileCount,
    long ReclaimedBytes,
    IReadOnlyList<string> FailedRelativePaths);

public sealed record RestoreResult(DateTimeOffset RestoredAtUtc, int SchemaVersion);
