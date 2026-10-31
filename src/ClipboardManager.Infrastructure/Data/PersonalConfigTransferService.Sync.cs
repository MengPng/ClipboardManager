using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ClipboardManager.Domain.Models;

namespace ClipboardManager.Infrastructure.Data;

// 配置订阅同步（0.9.0）：在个人配置迁移包格式上增加“按文件夹发布”和“网络包受管导入”。
public sealed partial class PersonalConfigTransferService
{
    public string ResolvePath(string relativeOrAbsolutePath) => Path.IsPathRooted(relativeOrAbsolutePath)
        ? Path.GetFullPath(relativeOrAbsolutePath)
        : Path.GetFullPath(Path.Combine(paths.Root, relativeOrAbsolutePath));

    public string ToRelativePath(string absolutePath) =>
        Path.GetRelativePath(paths.Root, absolutePath).Replace('\\', '/');

    public string NewTempPath()
    {
        paths.EnsureCreated();
        var directory = Path.Combine(paths.ImportsPath, "sync", "tmp");
        Directory.CreateDirectory(directory);
        // 上次异常退出遗留的半截下载不会被续传，超过一天即清理。
        foreach (var stale in Directory.EnumerateFiles(directory))
        {
            try { if (File.GetLastWriteTimeUtc(stale) < DateTime.UtcNow.AddDays(-1)) File.Delete(stale); } catch { }
        }
        return Path.Combine(directory, Guid.NewGuid().ToString("N") + ".part");
    }

    public Task<string> ComputeFileHashAsync(string path, CancellationToken cancellationToken = default) =>
        ComputeHashAsync(path, cancellationToken);

    public async Task<string> CopyToUserStorageAsync(string sourcePath, CancellationToken cancellationToken = default)
    {
        var source = Path.GetFullPath(sourcePath);
        var managedRoot = Path.GetFullPath(Path.Combine(paths.ImportsPath, "sync")) + Path.DirectorySeparatorChar;
        if (!source.StartsWith(managedRoot, StringComparison.OrdinalIgnoreCase) || !File.Exists(source))
            throw new ConfigSyncException(ConfigSyncError.Local, "要复制的文件已不存在或不属于订阅内容。");
        var directory = Path.Combine(paths.ImportsPath, "copies", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var target = Path.Combine(directory, ConfigSyncPolicy.SanitizeFileName(Path.GetFileName(source)));
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
        await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
        await input.CopyToAsync(output, cancellationToken);
        return target;
    }

    public async Task<PackedConfig> PackFoldersAsync(Guid workspaceId, IReadOnlyList<Guid>? folderIds, Guid publishedId, string name,
        int version, string destinationPath, CancellationToken cancellationToken = default)
    {
        var all = await repository.GetFoldersAsync(workspaceId, cancellationToken);
        // folderIds 为空表示“全部个人配置”：所有非受管文件夹；受管（订阅得到的）文件夹永远不参与发布。
        // 无论勾选顺序如何，包内文件夹一律按配置页里的顺序排列，同一范围重复发布才能识别为“内容没变”。
        var wanted = folderIds is { Count: > 0 } ? folderIds.ToHashSet() : null;
        if (wanted is not null && wanted.Any(id => all.All(candidate => candidate.Id != id)))
            throw new ConfigSyncException(ConfigSyncError.Local, "要发布的配置文件夹已不存在，请重新选择。");
        var selected = wanted is not null
            ? all.Where(candidate => wanted.Contains(candidate.Id)).ToList()
            : all.Where(candidate => candidate.ManagedSubscriptionId is null).ToList();
        if (selected.Any(folder => folder.ManagedSubscriptionId is not null))
            throw new ConfigSyncException(ConfigSyncError.Local, "订阅得到的受管文件夹不能再发布，请先“复制到我的配置”。");
        if (selected.Count > ConfigSyncPolicy.MaxFolderCount)
            throw new ConfigSyncException(ConfigSyncError.TooLarge, $"文件夹数量超过 {ConfigSyncPolicy.MaxFolderCount} 个，无法发布。");
        var entries = new List<(CollectionFolder Folder, CollectionItem Item, int Index)>();
        foreach (var candidate in selected)
        {
            var items = await repository.GetItemsAsync(workspaceId, candidate.Id, cancellationToken: cancellationToken);
            for (var position = 0; position < items.Count; position++) entries.Add((candidate, items[position], position));
        }
        if (entries.Count > ConfigSyncPolicy.MaxItemCount)
            throw new ConfigSyncException(ConfigSyncError.TooLarge, $"条目总数超过 {ConfigSyncPolicy.MaxItemCount} 条，无法发布。");

        var fullPath = Path.GetFullPath(destinationPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var temporary = fullPath + ".tmp-" + Guid.NewGuid().ToString("N");
        var packageItems = new List<PackageItem>();
        var manifestItems = new List<ConfigManifestItem>();
        var packagedFolders = new List<ConfigManifestFolder>();
        var assets = new List<PackageAsset>();
        var missing = 0;
        long total = 0;
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81920, true))
            {
                using var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true);
                foreach (var (folder, item, index) in entries)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var label = string.IsNullOrWhiteSpace(item.Title) ? item.ItemType.ToString() : item.Title!;
                    if (item.TextContent is not null && Encoding.UTF8.GetByteCount(item.TextContent) > ConfigSyncPolicy.MaxTextBytes)
                        throw new ConfigSyncException(ConfigSyncError.TooLarge, $"条目“{label}”的文本超过 2 MB，无法发布。");
                    var url = item.Url;
                    if (item.ItemType == CollectionItemType.Link)
                    {
                        try { url = CollectionLink.Normalize(item.Url); }
                        catch (ArgumentException)
                        {
                            throw new ConfigSyncException(ConfigSyncError.InvalidPackage, $"条目“{label}”的链接不是 http/https 地址，无法发布。");
                        }
                    }

                    PackageAsset? image = null, thumbnail = null;
                    var files = new List<PackageAsset>();
                    var risky = new List<string>();
                    var sources = new List<(string? Path, string Prefix, int Kind)>();
                    if (item.ItemType == CollectionItemType.Image)
                    {
                        sources.Add((item.LocalPath, $"assets/{item.Id:N}/image", 0));
                        sources.Add((item.ThumbnailPath, $"assets/{item.Id:N}/thumbnail", 1));
                    }
                    else if (item.ItemType == CollectionItemType.File)
                    {
                        var fileIndex = 0;
                        foreach (var file in ParseFiles(item)) sources.Add((file, $"assets/{item.Id:N}/files/{fileIndex++:D4}", 2));
                    }
                    foreach (var source in sources)
                    {
                        if (string.IsNullOrWhiteSpace(source.Path) || !File.Exists(source.Path))
                        {
                            if (source.Kind != 1) missing++;
                            continue;
                        }
                        var length = new FileInfo(source.Path).Length;
                        if (length > ConfigSyncPolicy.MaxFileBytes)
                            throw new ConfigSyncException(ConfigSyncError.TooLarge,
                                $"“{System.IO.Path.GetFileName(source.Path)}”超过 50 MB（单个文件上限），无法发布。");
                        total = checked(total + length);
                        if (total > ConfigSyncPolicy.MaxPackageBytes)
                            throw new ConfigSyncException(ConfigSyncError.TooLarge, "配置内容总大小超过 200 MB，无法发布。");
                        var asset = await AddAssetAsync(archive, source.Path, source.Prefix, assets, cancellationToken);
                        if (asset is null) continue;
                        if (source.Kind == 0) image = asset;
                        else if (source.Kind == 1) thumbnail = asset;
                        else
                        {
                            files.Add(asset);
                            if (ConfigSyncPolicy.IsHighRiskFile(asset.OriginalName))
                                risky.Add(ConfigSyncPolicy.SanitizeFileName(asset.OriginalName));
                        }
                    }
                    // 资源已丢失的条目无法在对方机器恢复，不进入发布内容。
                    if ((item.ItemType == CollectionItemType.Image && image is null) ||
                        (item.ItemType == CollectionItemType.File && files.Count == 0)) continue;

                    var digest = ComputeDigest((int)item.ItemType, item.Title, item.TextContent, url, item.Description,
                        new[] { image, thumbnail }.Concat(files).Where(asset => asset is not null).Select(asset => asset!.Sha256));
                    packageItems.Add(new PackageItem(item.Id, folder.Id, (int)item.ItemType, index, item.Title, item.TextContent,
                        url, item.Description, item.ContentHash, item.ContentSize, image?.EntryName, thumbnail?.EntryName,
                        files.Select(asset => asset.EntryName).ToArray(), digest));
                    manifestItems.Add(new ConfigManifestItem(item.Id, (int)item.ItemType, item.Title, digest,
                        Math.Max(item.ContentSize, files.Sum(asset => asset.ByteSize) + (image?.ByteSize ?? 0)), risky, folder.Id));
                }

                var includedBytes = assets.Sum(asset => asset.ByteSize);
                // 没有可发布条目的文件夹不进入包，订阅方不会得到空文件夹。
                var usedFolders = selected.Where(candidate => packageItems.Any(item => item.FolderId == candidate.Id)).ToList();
                if (usedFolders.Count == 0)
                    throw new ConfigSyncException(ConfigSyncError.Local, "没有可发布的内容。");
                packagedFolders.AddRange(usedFolders.Select(candidate => new ConfigManifestFolder(candidate.Id, candidate.Name,
                    packageItems.Count(item => item.FolderId == candidate.Id))));
                var manifest = new PackageManifest(FormatVersion, Guid.NewGuid(), DateTimeOffset.UtcNow,
                    usedFolders.Select((candidate, order) => new PackageFolder(candidate.Id, candidate.Name, false, order)).ToList(),
                    packageItems, assets, missing, includedBytes, new PackageSyncInfo(publishedId, name, version));
                var manifestEntry = archive.CreateEntry(ManifestEntryName, CompressionLevel.Optimal);
                await using var output = manifestEntry.Open();
                await JsonSerializer.SerializeAsync(output, manifest, JsonOptions, cancellationToken);
            }

            var hash = await ComputeHashAsync(temporary, cancellationToken);
            var bytes = new FileInfo(temporary).Length;
            if (bytes > ConfigSyncPolicy.MaxPackageBytes)
                throw new ConfigSyncException(ConfigSyncError.TooLarge, "配置包大小超过 200 MB，无法发布。");
            File.Move(temporary, fullPath, true);
            var published = new ConfigManifest(ConfigSyncPolicy.ProtocolVersion, publishedId, name, version, hash, bytes,
                DateTimeOffset.UtcNow, manifestItems, packagedFolders);
            var fileCount = assets.Count(asset => !asset.EntryName.EndsWith("/thumbnail", StringComparison.Ordinal));
            return new PackedConfig(published, ConfigSyncPolicy.SerializeManifest(published), fileCount, missing);
        }
        finally
        {
            TryDeleteFile(temporary);
        }
    }

    public async Task<ManagedPackage> ImportManagedAsync(string packagePath, Guid expectedPublishedId, int expectedVersion,
        string subscriptionKey, string packageHash, CancellationToken cancellationToken = default)
    {
        paths.EnsureCreated();
        var root = Path.Combine(paths.ImportsPath, "sync", "m", $"{subscriptionKey}-v{expectedVersion}-{Guid.NewGuid():N}");
        try
        {
            var (archive, manifest) = await OpenAndValidateAsync(packagePath, NetworkLimits, cancellationToken);
            using (archive)
            {
                if (manifest.Sync is null || manifest.Sync.PublishedId != expectedPublishedId || manifest.Sync.Version != expectedVersion)
                    throw new InvalidDataException("包内的发布标识或版本号与清单不一致，已拒收。");
                Directory.CreateDirectory(root);
                var drafts = new List<ManagedItemDraft>();
                var manifestItems = new List<ConfigManifestItem>();
                var materialized = new Dictionary<string, string>(StringComparer.Ordinal);
                var assetsByName = manifest.Assets.ToDictionary(asset => asset.EntryName, StringComparer.Ordinal);
                foreach (var item in manifest.Items.OrderBy(item => item.Order))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var type = (CollectionItemType)item.ItemType;
                    var imagePath = await MaterializeNamedAsync(archive, assetsByName, item.ImageEntry, root, materialized, cancellationToken);
                    var thumbnailPath = await MaterializeNamedAsync(archive, assetsByName, item.ThumbnailEntry, root, materialized, cancellationToken);
                    var filePaths = new List<string>();
                    var risky = new List<string>();
                    foreach (var entry in item.FileEntries)
                    {
                        var path = await MaterializeNamedAsync(archive, assetsByName, entry, root, materialized, cancellationToken);
                        if (path is null) continue;
                        filePaths.Add(path);
                        if (ConfigSyncPolicy.IsHighRiskFile(Path.GetFileName(path))) risky.Add(Path.GetFileName(path));
                    }
                    if ((type == CollectionItemType.Image && imagePath is null) || (type == CollectionItemType.File && filePaths.Count == 0))
                        throw new InvalidDataException("配置包中的图片或文件条目缺少资源。");
                    var assetHashes = new[] { item.ImageEntry, item.ThumbnailEntry }.Concat(item.FileEntries)
                        .OfType<string>().Select(entry => assetsByName[entry].Sha256);
                    var digest = ComputeDigest(item.ItemType, item.Title, item.TextContent, item.Url, item.Description, assetHashes);
                    drafts.Add(new ManagedItemDraft(item.Id, item.Order, type, item.Title, item.TextContent,
                        type == CollectionItemType.Link ? CollectionLink.Normalize(item.Url) : item.Url, item.Description,
                        imagePath ?? (filePaths.Count > 0 ? filePaths[0] : null), thumbnailPath,
                        filePaths.Count == 0 ? null : JsonSerializer.Serialize(filePaths), item.ContentHash, item.ContentSize, item.FolderId));
                    manifestItems.Add(new ConfigManifestItem(item.Id, item.ItemType, item.Title, digest, item.ContentSize, risky, item.FolderId));
                }
                var info = new FileInfo(packagePath);
                var folders = manifest.Folders.OrderBy(folder => folder.Order)
                    .Select(folder => new ManagedFolderDraft(folder.Id, folder.Name.Trim(), folder.Order)).ToList();
                var folderSummary = folders.Select(folder => new ConfigManifestFolder(folder.RemoteId, folder.Name,
                    drafts.Count(draft => draft.RemoteFolderId == folder.RemoteId))).ToList();
                return new ManagedPackage(new ConfigManifest(ConfigSyncPolicy.ProtocolVersion, expectedPublishedId,
                        manifest.Sync.Name, expectedVersion, packageHash, info.Length, manifest.CreatedAtUtc, manifestItems, folderSummary),
                    folders, drafts, root);
            }
        }
        catch (InvalidDataException exception)
        {
            TryDeleteDirectory(root);
            throw new ConfigSyncException(ConfigSyncError.InvalidPackage, "配置包校验失败：" + exception.Message, exception);
        }
        catch (ArgumentException exception)
        {
            TryDeleteDirectory(root);
            throw new ConfigSyncException(ConfigSyncError.InvalidPackage, "配置包校验失败：链接必须是 http/https 地址。", exception);
        }
        catch
        {
            TryDeleteDirectory(root);
            throw;
        }
    }

    private static string ComputeDigest(int type, string? title, string? text, string? url, string? description,
        IEnumerable<string> assetHashes)
    {
        var joined = string.Join('\n', type, title, text, url, description, string.Join(',', assetHashes));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(joined)));
    }

    // ZipArchiveEntry.Length 来自压缩包头，不可信：读取时按声明大小截断，超出即判为损坏。
    private static async Task<string> HashEntryAsync(ZipArchiveEntry entry, long declaredSize, string name,
        CancellationToken cancellationToken)
    {
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using var content = entry.Open();
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = await content.ReadAsync(buffer, cancellationToken)) > 0)
        {
            total += read;
            if (total > declaredSize) throw new InvalidDataException($"资源实际大小超过声明：{name}");
            hasher.AppendData(buffer, 0, read);
        }
        if (total != declaredSize) throw new InvalidDataException($"资源实际大小与声明不符：{name}");
        return Convert.ToHexString(hasher.GetHashAndReset());
    }

    private static async Task<string?> MaterializeNamedAsync(ZipArchive archive, IReadOnlyDictionary<string, PackageAsset> assets,
        string? entryName, string root, Dictionary<string, string> materialized, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(entryName)) return null;
        if (materialized.TryGetValue(entryName, out var existing)) return existing;
        var asset = assets[entryName];
        var entry = archive.GetEntry(entryName) ?? throw new InvalidDataException("配置包资源缺失。");
        // 每个资源独占一个目录并保留规范化后的原文件名（含扩展名），避免重名与路径穿越。
        var directory = Path.Combine(root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var target = Path.Combine(directory, ConfigSyncPolicy.SanitizeFileName(asset.OriginalName));
        await using (var input = entry.Open())
        await using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
        {
            var buffer = new byte[81920];
            long total = 0;
            int read;
            while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
            {
                total += read;
                if (total > asset.ByteSize) throw new InvalidDataException($"资源实际大小超过声明：{asset.OriginalName}");
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }
        }
        materialized.Add(entryName, target);
        return target;
    }
}
