using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using ClipboardManager.Domain.Contracts;
using ClipboardManager.Domain.Models;
using ClipboardManager.Infrastructure.Data;
using ClipboardManager.Infrastructure.Paths;

namespace ClipboardManager.Infrastructure.Tests;

public sealed class PersonalConfigTransferSecurityTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ClipboardManager.Transfer.Tests", Guid.NewGuid().ToString("N"));
    private readonly Guid _workspace = Guid.NewGuid();

    [Fact]
    public async Task FailedReimport_WithSamePackageId_DoesNotDeletePriorImportedAttachment()
    {
        var packageId = Guid.NewGuid();
        var good = Path.Combine(_root, "good.clipconfig");
        var bad = Path.Combine(_root, "bad.clipconfig");
        await WritePackageAsync(good, packageId, [ImageItem("first", "assets/photo.bin")],
            [Asset("assets/photo.bin", "photo.bin", "image")]);
        var repository = new InMemoryCollectionRepository();
        var service = CreateService(repository);

        await service.ImportAsync(_workspace, good);
        var imported = Assert.Single(repository.Items);
        Assert.NotNull(imported.LocalPath);
        Assert.True(File.Exists(imported.LocalPath));

        await WritePackageAsync(bad, packageId,
            [ImageItem("second", "assets/photo.bin"), FileItem("broken", "assets/not-declared.bin")],
            [Asset("assets/photo.bin", "photo.bin", "image")]);

        await Assert.ThrowsAsync<InvalidDataException>(() => service.ImportAsync(_workspace, bad));
        Assert.True(File.Exists(imported.LocalPath));
    }

    [Fact]
    public async Task RepeatedDeclaredReference_IsMaterializedOnce()
    {
        var package = Path.Combine(_root, "shared.clipconfig");
        await WritePackageAsync(package, Guid.NewGuid(),
            [ImageItem("one", "assets/shared.bin"), ImageItem("two", "assets/shared.bin")],
            [Asset("assets/shared.bin", "shared.bin", "same bytes")]);
        var repository = new InMemoryCollectionRepository();

        await CreateService(repository).ImportAsync(_workspace, package);

        Assert.Equal(2, repository.Items.Count);
        Assert.Single(repository.Items.Select(item => item.LocalPath).Distinct(StringComparer.Ordinal));
        var imports = Directory.GetDirectories(new AppPaths(_root).ImportsPath);
        Assert.Single(Directory.GetFiles(imports[0]));
    }

    [Theory]
    [InlineData("file:///C:/secret")]
    [InlineData("https://user@example.com")]
    [InlineData("relative/path")]
    public async Task UnsafeLinks_AreRejectedBeforeWrites(string url)
    {
        var package = Path.Combine(_root, "unsafe-link.clipconfig");
        await WritePackageAsync(package, Guid.NewGuid(), [LinkItem(url)], []);
        var repository = new InMemoryCollectionRepository();

        await Assert.ThrowsAsync<InvalidDataException>(() => CreateService(repository).ImportAsync(_workspace, package));

        Assert.Empty(repository.Items);
        Assert.False(Directory.Exists(new AppPaths(_root).ImportsPath) &&
                     Directory.EnumerateFileSystemEntries(new AppPaths(_root).ImportsPath).Any());
    }

    [Fact]
    public async Task ExportThenImport_RoundTripsTextLinkImageAndMultipleFiles()
    {
        var source = new InMemoryCollectionRepository();
        var folder = await source.CreateFolderAsync(_workspace, "常用");
        var image = await WriteFileAsync("image.png", "image");
        var fileA = await WriteFileAsync("a.txt", "a");
        var fileB = await WriteFileAsync("b.txt", "b");
        await source.CreateItemAsync(_workspace, new CreateCollectionItem(folder.Id, CollectionItemType.Text, "文本", "hello"));
        await source.CreateItemAsync(_workspace, new CreateCollectionItem(folder.Id, CollectionItemType.Link, "链接", Url: "https://example.com/path"));
        await source.CreateItemAsync(_workspace, new CreateCollectionItem(folder.Id, CollectionItemType.Image, "图片", LocalPath: image));
        await source.CreateItemAsync(_workspace, new CreateCollectionItem(folder.Id, CollectionItemType.File, "文件", FileManifest: JsonSerializer.Serialize(new[] { fileA, fileB })));
        var package = Path.Combine(_root, "roundtrip.clipconfig");

        await CreateService(source).ExportAsync(_workspace, package);
        var target = new InMemoryCollectionRepository();
        var result = await CreateService(target).ImportAsync(_workspace, package);

        Assert.Equal(4, result.ImportedItemCount);
        Assert.Collection(target.Items.OrderBy(item => item.ItemType),
            item => Assert.Equal("hello", item.TextContent),
            item => Assert.Equal("https://example.com/path", item.Url),
            item => Assert.True(File.Exists(item.LocalPath)),
            item =>
            {
                var paths = JsonSerializer.Deserialize<string[]>(item.FileManifest!)!;
                Assert.Equal(2, paths.Length);
                Assert.All(paths, path => Assert.True(File.Exists(path)));
            });
    }

    private PersonalConfigTransferService CreateService(InMemoryCollectionRepository repository) =>
        new(new AppPaths(_root), repository, new NoOpStorageMaintenanceService());

    private async Task<string> WriteFileAsync(string name, string contents)
    {
        var path = Path.Combine(_root, "source", name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, contents);
        return path;
    }

    private static PackageItem ImageItem(string title, string entry) =>
        new(Guid.NewGuid(), FolderId, (int)CollectionItemType.Image, 0, title, null, null, null, title, 1, entry, null, []);

    private static PackageItem FileItem(string title, string entry) =>
        new(Guid.NewGuid(), FolderId, (int)CollectionItemType.File, 1, title, null, null, null, title, 1, null, null, [entry]);

    private static PackageItem LinkItem(string url) =>
        new(Guid.NewGuid(), FolderId, (int)CollectionItemType.Link, 0, "link", null, url, null, "link", 0, null, null, []);

    private static PackageAsset Asset(string entry, string name, string contents) =>
        new(entry, name, contents.Length, Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(contents))));

    private static readonly Guid FolderId = Guid.NewGuid();

    private static async Task WritePackageAsync(string path, Guid packageId, IReadOnlyList<PackageItem> items, IReadOnlyList<PackageAsset> assets)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var asset in assets)
        {
            var entry = archive.CreateEntry(asset.EntryName);
            await using var writer = new StreamWriter(entry.Open());
            await writer.WriteAsync(asset.OriginalName == "photo.bin" ? "image" : "same bytes");
        }
        var manifest = new PackageManifest(1, packageId, DateTimeOffset.UtcNow,
            [new PackageFolder(FolderId, "导入", false, 0)], items, assets, 0, assets.Sum(asset => asset.ByteSize));
        var manifestEntry = archive.CreateEntry("manifest.json");
        await using var output = manifestEntry.Open();
        await JsonSerializer.SerializeAsync(output, manifest, new JsonSerializerOptions(JsonSerializerDefaults.Web));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private sealed class InMemoryCollectionRepository : ICollectionRepository
    {
        private readonly List<CollectionFolder> _folders = [];
        public List<CollectionItem> Items { get; } = [];

        public Task<CollectionFolder> EnsureUncategorizedAsync(Guid workspaceId, CancellationToken cancellationToken = default)
        {
            var folder = _folders.FirstOrDefault(folder => folder.WorkspaceId == workspaceId && folder.IsSystem)
                ?? NewFolder(workspaceId, "未分类", true);
            return Task.FromResult(folder);
        }

        public Task<IReadOnlyList<CollectionFolder>> GetFoldersAsync(Guid workspaceId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<CollectionFolder>>(_folders.Where(folder => folder.WorkspaceId == workspaceId).ToList());

        public Task<CollectionFolder> CreateFolderAsync(Guid workspaceId, string name, CancellationToken cancellationToken = default) =>
            Task.FromResult(NewFolder(workspaceId, name, false));

        public Task RenameFolderAsync(Guid workspaceId, Guid folderId, string name, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ReorderFolderAsync(Guid workspaceId, Guid folderId, int targetIndex, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DeleteFolderAsync(Guid workspaceId, Guid folderId, Guid fallbackFolderId, CancellationToken cancellationToken = default)
        {
            _folders.RemoveAll(folder => folder.WorkspaceId == workspaceId && folder.Id == folderId);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<CollectionItem>> GetItemsAsync(Guid workspaceId, Guid folderId, string? searchText = null, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<CollectionItem>>(Items.Where(item => item.WorkspaceId == workspaceId && item.FolderId == folderId).ToList());

        public Task<CollectionItem> CreateItemAsync(Guid workspaceId, CreateCollectionItem input, CancellationToken cancellationToken = default)
        {
            var now = DateTimeOffset.UtcNow;
            var item = new CollectionItem
            {
                Id = Guid.NewGuid(), WorkspaceId = workspaceId, FolderId = input.FolderId, ItemType = input.ItemType,
                Title = input.Title, TextContent = input.TextContent, Url = input.Url, Description = input.Description,
                LocalPath = input.LocalPath, ThumbnailPath = input.ThumbnailPath, FileManifest = input.FileManifest,
                ContentHash = input.ContentHash, ContentSize = input.ContentSize, SortKey = Items.Count,
                CreatedAt = now, UpdatedAt = now
            };
            Items.Add(item);
            return Task.FromResult(item);
        }

        public Task UpdateItemAsync(CollectionItem item, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task MoveItemAsync(Guid workspaceId, Guid itemId, Guid targetFolderId, int targetIndex, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DeleteItemAsync(Guid workspaceId, Guid itemId, CancellationToken cancellationToken = default)
        {
            Items.RemoveAll(item => item.WorkspaceId == workspaceId && item.Id == itemId);
            return Task.CompletedTask;
        }

        private CollectionFolder NewFolder(Guid workspaceId, string name, bool system)
        {
            var now = DateTimeOffset.UtcNow;
            var folder = new CollectionFolder
            {
                Id = Guid.NewGuid(), WorkspaceId = workspaceId, Name = name, IsSystem = system,
                SortKey = _folders.Count, CreatedAt = now, UpdatedAt = now
            };
            _folders.Add(folder);
            return folder;
        }
    }

    private sealed class NoOpStorageMaintenanceService : IStorageMaintenanceService
    {
        public Task<StorageUsageStats> GetUsageStatsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new StorageUsageStats(0, 0, 0, 0, 0));
        public Task CreateBackupAsync(string backupPath, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<BackupValidationResult> ValidateBackupAsync(string backupPath, CancellationToken cancellationToken = default) =>
            Task.FromResult(new BackupValidationResult(true, null, null, null));
        public Task<RestoreResult> RestoreAsync(string backupPath, CancellationToken cancellationToken = default) =>
            Task.FromResult(new RestoreResult(DateTimeOffset.UtcNow, 0));
        public Task<AssetCleanupPreview> PreviewOrphanedAssetsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new AssetCleanupPreview([], 0));
        public Task<AssetCleanupResult> CleanupOrphanedAssetsAsync(AssetCleanupPreview preview, CancellationToken cancellationToken = default) =>
            Task.FromResult(new AssetCleanupResult(0, 0, []));
    }

    private sealed record PackageManifest(int FormatVersion, Guid PackageId, DateTimeOffset CreatedAtUtc,
        IReadOnlyList<PackageFolder> Folders, IReadOnlyList<PackageItem> Items, IReadOnlyList<PackageAsset> Assets,
        int MissingFileCount, long IncludedBytes);
    private sealed record PackageFolder(Guid Id, string Name, bool IsSystem, int Order);
    private sealed record PackageItem(Guid Id, Guid FolderId, int ItemType, int Order, string? Title, string? TextContent,
        string? Url, string? Description, string? ContentHash, long ContentSize, string? ImageEntry, string? ThumbnailEntry,
        IReadOnlyList<string> FileEntries);
    private sealed record PackageAsset(string EntryName, string OriginalName, long ByteSize, string Sha256);
}
