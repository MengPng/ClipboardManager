using ClipboardManager.Domain.Models;

namespace ClipboardManager.Domain.Contracts;

public interface IStorageMaintenanceService
{
    Task<StorageUsageStats> GetUsageStatsAsync(CancellationToken cancellationToken = default);
    Task CreateBackupAsync(string backupPath, CancellationToken cancellationToken = default);
    Task<BackupValidationResult> ValidateBackupAsync(string backupPath, CancellationToken cancellationToken = default);

    // The caller must quiesce database connections and managed-asset writers before restore.
    Task<RestoreResult> RestoreAsync(string backupPath, CancellationToken cancellationToken = default);
    Task<AssetCleanupPreview> PreviewOrphanedAssetsAsync(CancellationToken cancellationToken = default);
    Task<AssetCleanupResult> CleanupOrphanedAssetsAsync(
        AssetCleanupPreview preview,
        CancellationToken cancellationToken = default);
}
