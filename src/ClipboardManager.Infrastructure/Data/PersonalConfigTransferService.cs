using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using ClipboardManager.Domain.Contracts;
using ClipboardManager.Domain.Models;
using ClipboardManager.Infrastructure.Paths;

namespace ClipboardManager.Infrastructure.Data;

public sealed class PersonalConfigTransferService(
    AppPaths paths,
    ICollectionRepository repository,
    IStorageMaintenanceService storageMaintenance) : IPersonalConfigTransferService
{
    private const int FormatVersion = 1;
    private const string ManifestEntryName = "manifest.json";
    private const long MaxPackageBytes = 20L * 1024 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public async Task<PersonalConfigExportResult> ExportAsync(Guid personalWorkspaceId, string packagePath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packagePath);
        var fullPath = Path.GetFullPath(packagePath);
        var parent = Path.GetDirectoryName(fullPath) ?? throw new ArgumentException("导出路径无效。", nameof(packagePath));
        Directory.CreateDirectory(parent);
        var temporary = Path.Combine(parent, "." + Path.GetFileName(fullPath) + ".tmp-" + Guid.NewGuid().ToString("N"));
        var packageId = Guid.NewGuid();
        var packageFolders = new List<PackageFolder>();
        var packageItems = new List<PackageItem>();
        var assets = new List<PackageAsset>();
        long includedBytes = 0;
        var missingFiles = 0;

        try
        {
            await using var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81920, true);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true);
            var folders = await repository.GetFoldersAsync(personalWorkspaceId, cancellationToken);
            for (var folderIndex = 0; folderIndex < folders.Count; folderIndex++)
            {
                var folder = folders[folderIndex];
                packageFolders.Add(new PackageFolder(folder.Id, folder.Name, folder.IsSystem, folderIndex));
                var items = await repository.GetItemsAsync(personalWorkspaceId, folder.Id, cancellationToken: cancellationToken);
                for (var itemIndex = 0; itemIndex < items.Count; itemIndex++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var item = items[itemIndex];
                    PackageAsset? image = null;
                    PackageAsset? thumbnail = null;
                    var files = new List<PackageAsset>();
                    if (item.ItemType == CollectionItemType.Image)
                    {
                        image = await AddAssetAsync(archive, item.LocalPath, $"assets/{item.Id:N}/image", assets, cancellationToken);
                        thumbnail = await AddAssetAsync(archive, item.ThumbnailPath, $"assets/{item.Id:N}/thumbnail", assets, cancellationToken);
                        if (image is null) missingFiles++;
                    }
                    else if (item.ItemType == CollectionItemType.File)
                    {
                        foreach (var file in ParseFiles(item))
                        {
                            var asset = await AddAssetAsync(archive, file, $"assets/{item.Id:N}/files/{files.Count:D4}", assets, cancellationToken);
                            if (asset is null) missingFiles++; else files.Add(asset);
                        }
                    }

                    packageItems.Add(new PackageItem(item.Id, folder.Id, (int)item.ItemType, itemIndex,
                        item.Title, item.TextContent, item.Url, item.Description, item.ContentHash,
                        item.ContentSize, image?.EntryName, thumbnail?.EntryName,
                        files.Select(file => file.EntryName).ToArray()));
                }
            }

            includedBytes = assets.Sum(asset => asset.ByteSize);
            var manifest = new PackageManifest(FormatVersion, packageId, DateTimeOffset.UtcNow,
                packageFolders, packageItems, assets, missingFiles, includedBytes);
            var manifestEntry = archive.CreateEntry(ManifestEntryName, CompressionLevel.Optimal);
            await using (var output = manifestEntry.Open())
                await JsonSerializer.SerializeAsync(output, manifest, JsonOptions, cancellationToken);
            archive.Dispose();
            await stream.FlushAsync(cancellationToken);
            File.Move(temporary, fullPath, true);
            return new PersonalConfigExportResult(packageFolders.Count, packageItems.Count, assets.Count, missingFiles, includedBytes);
        }
        finally
        {
            TryDeleteFile(temporary);
        }
    }

    public async Task<PersonalConfigImportPreview> InspectAsync(string packagePath, CancellationToken cancellationToken = default)
    {
        var (archive, manifest) = await OpenAndValidateAsync(packagePath, cancellationToken);
        using (archive)
        {
        return new PersonalConfigImportPreview(manifest.FormatVersion, manifest.CreatedAtUtc,
            manifest.Folders.Count, manifest.Items.Count, manifest.Assets.Count,
            manifest.MissingFileCount, manifest.IncludedBytes);
        }
    }

    public async Task<PersonalConfigImportResult> ImportAsync(Guid personalWorkspaceId, string packagePath, CancellationToken cancellationToken = default)
    {
        var (archive, manifest) = await OpenAndValidateAsync(packagePath, cancellationToken);
        using (archive)
        {
            paths.EnsureCreated();
            var safetyDirectory = Path.Combine(paths.Root, "import-backups");
            Directory.CreateDirectory(safetyDirectory);
            var safetyBackup = Path.Combine(safetyDirectory, $"before-personal-import-{DateTime.Now:yyyyMMdd-HHmmss}.clipbackup");
            await storageMaintenance.CreateBackupAsync(safetyBackup, cancellationToken);

            var importRoot = Path.Combine(paths.Root, "imports", manifest.PackageId.ToString("N"));
            var createdItems = new List<(Guid WorkspaceId, Guid ItemId)>();
            var createdFolders = new List<Guid>();
            var skipped = 0;
            CollectionFolder? uncategorized = null;
            try
            {
                Directory.CreateDirectory(importRoot);
                var existingFolders = await repository.GetFoldersAsync(personalWorkspaceId, cancellationToken);
                uncategorized = await repository.EnsureUncategorizedAsync(personalWorkspaceId, cancellationToken);
                var folderMap = new Dictionary<Guid, Guid>();
                foreach (var folder in manifest.Folders.OrderBy(folder => folder.Order))
                {
                    if (folder.IsSystem)
                    {
                        folderMap[folder.Id] = uncategorized.Id;
                        continue;
                    }
                    var existing = existingFolders.FirstOrDefault(candidate =>
                        string.Equals(candidate.Name, folder.Name, StringComparison.OrdinalIgnoreCase));
                    if (existing is not null) folderMap[folder.Id] = existing.Id;
                    else
                    {
                        var created = await repository.CreateFolderAsync(personalWorkspaceId, folder.Name, cancellationToken);
                        createdFolders.Add(created.Id);
                        folderMap[folder.Id] = created.Id;
                    }
                }

                foreach (var item in manifest.Items.OrderBy(item => item.Order))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!folderMap.TryGetValue(item.FolderId, out var folderId)) { skipped++; continue; }
                    var existingItems = await repository.GetItemsAsync(personalWorkspaceId, folderId, cancellationToken: cancellationToken);
                    if (!string.IsNullOrWhiteSpace(item.ContentHash) && existingItems.Any(existing =>
                        existing.ItemType == (CollectionItemType)item.ItemType && existing.ContentHash == item.ContentHash))
                    {
                        skipped++;
                        continue;
                    }

                    var localPath = await MaterializeAsync(archive, manifest, item.ImageEntry, importRoot, cancellationToken);
                    var thumbnailPath = await MaterializeAsync(archive, manifest, item.ThumbnailEntry, importRoot, cancellationToken);
                    var filePaths = new List<string>();
                    foreach (var entry in item.FileEntries)
                    {
                        var file = await MaterializeAsync(archive, manifest, entry, importRoot, cancellationToken);
                        if (file is not null) filePaths.Add(file);
                    }
                    if (((CollectionItemType)item.ItemType == CollectionItemType.Image && localPath is null) ||
                        ((CollectionItemType)item.ItemType == CollectionItemType.File && filePaths.Count == 0))
                    {
                        skipped++;
                        continue;
                    }

                    var created = await repository.CreateItemAsync(personalWorkspaceId, new CreateCollectionItem(
                        folderId, (CollectionItemType)item.ItemType, item.Title, item.TextContent,
                        item.Url, item.Description, localPath, thumbnailPath,
                        filePaths.Count == 0 ? null : JsonSerializer.Serialize(filePaths), item.ContentHash,
                        item.ContentSize), cancellationToken);
                    createdItems.Add((personalWorkspaceId, created.Id));
                }

                return new PersonalConfigImportResult(createdFolders.Count, createdItems.Count, skipped, safetyBackup);
            }
            catch
            {
                foreach (var item in createdItems.AsEnumerable().Reverse())
                    await repository.DeleteItemAsync(item.WorkspaceId, item.ItemId, CancellationToken.None);
                if (uncategorized is not null)
                    foreach (var folder in createdFolders.AsEnumerable().Reverse())
                        await repository.DeleteFolderAsync(personalWorkspaceId, folder, uncategorized.Id, CancellationToken.None);
                TryDeleteDirectory(importRoot);
                throw;
            }
        }
    }

    private static async Task<PackageAsset?> AddAssetAsync(ZipArchive archive, string? sourcePath, string entryPrefix,
        List<PackageAsset> assets, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath)) return null;
        var extension = Path.GetExtension(sourcePath);
        var name = Path.GetFileName(sourcePath);
        var entryName = entryPrefix + extension;
        var hash = await ComputeHashAsync(sourcePath, cancellationToken);
        var size = new FileInfo(sourcePath).Length;
        var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
        await using var input = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
        await using var output = entry.Open();
        await input.CopyToAsync(output, cancellationToken);
        var asset = new PackageAsset(entryName, name, size, hash);
        assets.Add(asset);
        return asset;
    }

    private static async Task<(ZipArchive Archive, PackageManifest Manifest)> OpenAndValidateAsync(string packagePath, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packagePath);
        var stream = new FileStream(Path.GetFullPath(packagePath), FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
        ZipArchive? archive = null;
        try
        {
            archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
            if (archive.Entries.Any(entry => !IsSafeEntryName(entry.FullName)))
                throw new InvalidDataException("迁移包包含不安全路径。");
            var manifestEntry = archive.GetEntry(ManifestEntryName) ?? throw new InvalidDataException("迁移包缺少 manifest.json。");
            PackageManifest? manifest;
            await using (var input = manifestEntry.Open())
                manifest = await JsonSerializer.DeserializeAsync<PackageManifest>(input, JsonOptions, cancellationToken);
            if (manifest is null || manifest.FormatVersion != FormatVersion)
                throw new InvalidDataException("不支持的个人配置迁移包版本。");
            if (manifest.IncludedBytes < 0 || manifest.IncludedBytes > MaxPackageBytes)
                throw new InvalidDataException("迁移包解压大小超出限制。");
            var assetsByName = manifest.Assets.ToDictionary(asset => asset.EntryName, StringComparer.Ordinal);
            if (assetsByName.Count != manifest.Assets.Count) throw new InvalidDataException("迁移包资源名称重复。");
            long total = 0;
            foreach (var asset in manifest.Assets)
            {
                if (!IsSafeEntryName(asset.EntryName) || asset.ByteSize < 0) throw new InvalidDataException("迁移包资源描述无效。");
                var entry = archive.GetEntry(asset.EntryName) ?? throw new InvalidDataException($"迁移包缺少资源：{asset.OriginalName}");
                if (entry.Length != asset.ByteSize) throw new InvalidDataException($"资源大小校验失败：{asset.OriginalName}");
                total = checked(total + entry.Length);
                await using var content = entry.Open();
                var hash = Convert.ToHexString(await SHA256.HashDataAsync(content, cancellationToken));
                if (!hash.Equals(asset.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"资源校验失败：{asset.OriginalName}");
            }
            if (total != manifest.IncludedBytes || total > MaxPackageBytes) throw new InvalidDataException("迁移包资源总大小校验失败。");
            return (archive, manifest);
        }
        catch
        {
            archive?.Dispose();
            if (archive is null) stream.Dispose();
            throw;
        }
    }

    private static async Task<string?> MaterializeAsync(ZipArchive archive, PackageManifest manifest, string? entryName,
        string importRoot, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(entryName)) return null;
        var asset = manifest.Assets.SingleOrDefault(candidate => candidate.EntryName == entryName)
            ?? throw new InvalidDataException("迁移包项目引用了未声明资源。");
        var entry = archive.GetEntry(entryName) ?? throw new InvalidDataException("迁移包资源缺失。");
        var target = Path.Combine(importRoot, Guid.NewGuid().ToString("N") + Path.GetExtension(asset.OriginalName));
        await using var input = entry.Open();
        await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
        await input.CopyToAsync(output, cancellationToken);
        return target;
    }

    private static IReadOnlyList<string> ParseFiles(CollectionItem item)
    {
        if (!string.IsNullOrWhiteSpace(item.FileManifest))
        {
            try { return JsonSerializer.Deserialize<string[]>(item.FileManifest) ?? []; }
            catch (JsonException) { return []; }
        }
        return string.IsNullOrWhiteSpace(item.LocalPath) ? [] : [item.LocalPath];
    }

    private static async Task<string> ComputeHashAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
    }

    private static bool IsSafeEntryName(string name) => !string.IsNullOrWhiteSpace(name) &&
        !name.StartsWith('/') && !name.StartsWith('\\') && !name.Contains('\\') &&
        name.Split('/').All(part => part is not "" and not "." and not "..");

    private static void TryDeleteFile(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }
    private static void TryDeleteDirectory(string path) { try { if (Directory.Exists(path)) Directory.Delete(path, true); } catch { } }

    private sealed record PackageManifest(int FormatVersion, Guid PackageId, DateTimeOffset CreatedAtUtc,
        IReadOnlyList<PackageFolder> Folders, IReadOnlyList<PackageItem> Items,
        IReadOnlyList<PackageAsset> Assets, int MissingFileCount, long IncludedBytes);
    private sealed record PackageFolder(Guid Id, string Name, bool IsSystem, int Order);
    private sealed record PackageItem(Guid Id, Guid FolderId, int ItemType, int Order, string? Title,
        string? TextContent, string? Url, string? Description, string? ContentHash, long ContentSize,
        string? ImageEntry, string? ThumbnailEntry, IReadOnlyList<string> FileEntries);
    private sealed record PackageAsset(string EntryName, string OriginalName, long ByteSize, string Sha256);
}
