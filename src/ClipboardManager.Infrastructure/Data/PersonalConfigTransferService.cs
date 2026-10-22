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
    // The manifest is metadata, not an asset container. Bound it separately so a
    // small zip entry cannot expand into an unbounded JSON document.
    private const long MaxManifestCompressedBytes = 4L * 1024 * 1024;
    private const long MaxManifestBytes = 16L * 1024 * 1024;
    private const int MaxFolderCount = 10_000;
    private const int MaxItemCount = 100_000;
    private const int MaxAssetCount = 100_000;
    private const int MaxAssetReferences = 200_000;
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
            await stream.DisposeAsync();
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

            var importRoot = Path.Combine(paths.ImportsPath,
                $"{manifest.PackageId:N}-{Guid.NewGuid():N}");
            var createdItems = new List<(Guid WorkspaceId, Guid ItemId)>();
            var createdFolders = new List<Guid>();
            var skipped = 0;
            CollectionFolder? uncategorized = null;
            var committed = false;
            try
            {
                Directory.CreateDirectory(importRoot);
                var materializedAssets = new Dictionary<string, string>(StringComparer.Ordinal);
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

                    var localPath = await MaterializeAsync(archive, manifest, item.ImageEntry, importRoot, materializedAssets, cancellationToken);
                    var thumbnailPath = await MaterializeAsync(archive, manifest, item.ThumbnailEntry, importRoot, materializedAssets, cancellationToken);
                    var filePaths = new List<string>();
                    foreach (var entry in item.FileEntries)
                    {
                        var file = await MaterializeAsync(archive, manifest, entry, importRoot, materializedAssets, cancellationToken);
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
                        item.ItemType == (int)CollectionItemType.Link ? CollectionLink.Normalize(item.Url) : item.Url, item.Description, localPath, thumbnailPath,
                        filePaths.Count == 0 ? null : JsonSerializer.Serialize(filePaths), item.ContentHash,
                        item.ContentSize), cancellationToken);
                    createdItems.Add((personalWorkspaceId, created.Id));
                }

                committed = true;
                return new PersonalConfigImportResult(createdFolders.Count, createdItems.Count, skipped, safetyBackup);
            }
            catch
            {
                foreach (var item in createdItems.AsEnumerable().Reverse())
                {
                    try { await repository.DeleteItemAsync(item.WorkspaceId, item.ItemId, CancellationToken.None); }
                    catch { }
                }
                if (uncategorized is not null)
                    foreach (var folder in createdFolders.AsEnumerable().Reverse())
                    {
                        try { await repository.DeleteFolderAsync(personalWorkspaceId, folder, uncategorized.Id, CancellationToken.None); }
                        catch { }
                    }
                throw;
            }
            finally
            {
                if (!committed) TryDeleteDirectory(importRoot);
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
            if (manifestEntry.CompressedLength > MaxManifestCompressedBytes || manifestEntry.Length > MaxManifestBytes)
                throw new InvalidDataException("迁移包清单超出限制。");
            PackageManifest? manifest;
            await using (var input = manifestEntry.Open())
                manifest = await JsonSerializer.DeserializeAsync<PackageManifest>(input, JsonOptions, cancellationToken);
            if (manifest is null || manifest.FormatVersion != FormatVersion)
                throw new InvalidDataException("不支持的个人配置迁移包版本。");
            ValidateManifestShape(manifest);
            var assetsByName = manifest.Assets.ToDictionary(asset => asset.EntryName, StringComparer.Ordinal);
            if (assetsByName.Count != manifest.Assets.Count) throw new InvalidDataException("迁移包资源名称重复。");
            if (archive.Entries.Count != manifest.Assets.Count + 1 ||
                archive.Entries.Any(entry => entry.FullName != ManifestEntryName && !assetsByName.ContainsKey(entry.FullName)))
                throw new InvalidDataException("迁移包包含未声明资源。");
            long total = 0;
            var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.Ordinal);
            foreach (var asset in manifest.Assets)
            {
                if (!IsSafeEntryName(asset.EntryName) || asset.ByteSize < 0) throw new InvalidDataException("迁移包资源描述无效。");
                var entry = archive.GetEntry(asset.EntryName) ?? throw new InvalidDataException($"迁移包缺少资源：{asset.OriginalName}");
                if (entry.Length != asset.ByteSize) throw new InvalidDataException($"资源大小校验失败：{asset.OriginalName}");
                total = checked(total + entry.Length);
                if (total > MaxPackageBytes) throw new InvalidDataException("迁移包资源总大小校验失败。");
                entries.Add(asset.EntryName, entry);
            }
            if (total != manifest.IncludedBytes) throw new InvalidDataException("迁移包资源总大小校验失败。");
            foreach (var asset in manifest.Assets)
            {
                var entry = entries[asset.EntryName];
                await using var content = entry.Open();
                var hash = Convert.ToHexString(await SHA256.HashDataAsync(content, cancellationToken));
                if (!hash.Equals(asset.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"资源校验失败：{asset.OriginalName}");
            }
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
        string importRoot, Dictionary<string, string> materializedAssets, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(entryName)) return null;
        if (materializedAssets.TryGetValue(entryName, out var existing)) return existing;
        var asset = manifest.Assets.SingleOrDefault(candidate => candidate.EntryName == entryName)
            ?? throw new InvalidDataException("迁移包项目引用了未声明资源。");
        var entry = archive.GetEntry(entryName) ?? throw new InvalidDataException("迁移包资源缺失。");
        var target = Path.Combine(importRoot, Guid.NewGuid().ToString("N") + Path.GetExtension(asset.OriginalName));
        await using var input = entry.Open();
        await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
        await input.CopyToAsync(output, cancellationToken);
        materializedAssets.Add(entryName, target);
        return target;
    }

    private static void ValidateManifestShape(PackageManifest manifest)
    {
        if (manifest.PackageId == Guid.Empty || manifest.Folders is null || manifest.Items is null || manifest.Assets is null ||
            manifest.MissingFileCount < 0 || manifest.IncludedBytes < 0 || manifest.IncludedBytes > MaxPackageBytes ||
            manifest.Folders.Count > MaxFolderCount || manifest.Items.Count > MaxItemCount || manifest.Assets.Count > MaxAssetCount)
            throw new InvalidDataException("迁移包清单结构或数量无效。");

        var folders = new HashSet<Guid>();
        foreach (var folder in manifest.Folders)
        {
            if (folder is null || folder.Id == Guid.Empty || string.IsNullOrWhiteSpace(folder.Name) || !folders.Add(folder.Id))
                throw new InvalidDataException("迁移包文件夹无效。");
        }

        var items = new HashSet<Guid>();
        var references = 0;
        foreach (var item in manifest.Items)
        {
            if (item is null || item.Id == Guid.Empty || !items.Add(item.Id) || !folders.Contains(item.FolderId) ||
                !Enum.IsDefined(typeof(CollectionItemType), item.ItemType) || item.ContentSize < 0 || item.FileEntries is null)
                throw new InvalidDataException("迁移包项目无效。");
            var type = (CollectionItemType)item.ItemType;
            if (type == CollectionItemType.Link)
            {
                try { CollectionLink.Normalize(item.Url); }
                catch (ArgumentException exception) { throw new InvalidDataException("迁移包链接无效。", exception); }
            }
            if (type is not CollectionItemType.Image && (item.ImageEntry is not null || item.ThumbnailEntry is not null))
                throw new InvalidDataException("迁移包项目资源类型不匹配。");
            if (type != CollectionItemType.File && item.FileEntries.Count != 0)
                throw new InvalidDataException("迁移包项目资源类型不匹配。");
            if (item.ImageEntry is not null) references = checked(references + 1);
            if (item.ThumbnailEntry is not null) references = checked(references + 1);
            if (item.FileEntries.Any(string.IsNullOrWhiteSpace) ||
                item.FileEntries.Distinct(StringComparer.Ordinal).Count() != item.FileEntries.Count)
                throw new InvalidDataException("迁移包文件资源引用重复或无效。");
            references = checked(references + item.FileEntries.Count);
        }
        if (references > MaxAssetReferences)
            throw new InvalidDataException("迁移包资源引用过多。");

        var assetNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var asset in manifest.Assets)
        {
            if (asset is null || !IsSafeEntryName(asset.EntryName) || string.IsNullOrWhiteSpace(asset.OriginalName) || string.IsNullOrWhiteSpace(asset.Sha256) ||
                asset.Sha256.Length != 64 || !asset.Sha256.All(Uri.IsHexDigit) || !assetNames.Add(asset.EntryName))
                throw new InvalidDataException("迁移包资源描述无效。");
        }
        foreach (var item in manifest.Items)
        {
            foreach (var reference in EnumerateAssetReferences(item))
                if (!assetNames.Contains(reference))
                    throw new InvalidDataException("迁移包项目引用了未声明资源。");
        }
    }

    private static IEnumerable<string> EnumerateAssetReferences(PackageItem item)
    {
        if (item.ImageEntry is not null) yield return item.ImageEntry;
        if (item.ThumbnailEntry is not null) yield return item.ThumbnailEntry;
        foreach (var entry in item.FileEntries) yield return entry;
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
