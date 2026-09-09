namespace ClipboardManager.Domain.Models;

public sealed record PersonalConfigExportResult(
    int FolderCount,
    int ItemCount,
    int IncludedFileCount,
    int MissingFileCount,
    long IncludedBytes);

public sealed record PersonalConfigImportPreview(
    int FormatVersion,
    DateTimeOffset CreatedAtUtc,
    int FolderCount,
    int ItemCount,
    int IncludedFileCount,
    int MissingFileCount,
    long IncludedBytes);

public sealed record PersonalConfigImportResult(
    int CreatedFolderCount,
    int ImportedItemCount,
    int SkippedItemCount,
    string SafetyBackupPath);
