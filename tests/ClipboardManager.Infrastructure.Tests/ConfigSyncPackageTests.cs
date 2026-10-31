using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ClipboardManager.Domain.Contracts;
using ClipboardManager.Domain.Models;
using ClipboardManager.Infrastructure.ConfigSync;
using ClipboardManager.Infrastructure.Data;
using ClipboardManager.Infrastructure.Paths;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClipboardManager.Infrastructure.Tests;

/// <summary>网络配置包的校验：手工构造恶意/损坏的包，必须被整体拒收且不留下任何已写入内容。</summary>
public sealed class ConfigSyncPackageTests : IAsyncLifetime
{
    private static readonly Guid PublishedId = Guid.NewGuid();
    private static readonly Guid FolderId = Guid.NewGuid();
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private ConfigSyncTestHost _host = null!;

    public async Task InitializeAsync() => _host = await ConfigSyncTestHost.CreateAsync("package");
    public async Task DisposeAsync() => await _host.DisposeAsync();

    [Fact]
    public async Task PackedFolder_FlagsRiskyFilesAndRoundTripsThroughManagedImport()
    {
        var folder = await _host.CreateFolderAsync("包");
        await _host.AddTextAsync(folder.Id, "文本", "hello");
        await _host.AddLinkAsync(folder.Id, "链接", "https://example.com/a");
        await _host.AddImageAsync(folder.Id, "图", ConfigSyncTestHost.Png(7));
        await _host.AddFileAsync(folder.Id, "脚本", "run.ps1", "Write-Host hi"u8.ToArray());
        await _host.AddFileAsync(folder.Id, "说明", "notes.txt", "notes"u8.ToArray());

        var destination = Path.Combine(_host.Root, "out.zip");
        var packed = await _host.Packages.PackFoldersAsync(_host.Workspace, [folder.Id], PublishedId, "包", 1, destination);

        Assert.True(packed.Manifest.HasRiskyFiles);
        Assert.Equal(["run.ps1"], packed.Manifest.Items.SelectMany(item => item.RiskyFiles));
        Assert.Equal(5, packed.Manifest.Items.Count);
        Assert.True(Encoding.UTF8.GetByteCount(packed.ManifestJson) < ConfigSyncPolicy.MaxManifestBytes);

        var hash = await _host.Packages.ComputeFileHashAsync(destination);
        Assert.Equal(packed.Manifest.Hash, hash);
        var imported = await _host.Packages.ImportManagedAsync(destination, PublishedId, 1, "key", hash);
        Assert.Equal(5, imported.Items.Count);
        Assert.Equal(packed.Manifest.Items.Select(i => i.Digest).Order(), imported.Manifest.Items.Select(i => i.Digest).Order());
        Assert.Equal(["run.ps1"], imported.Manifest.Items.SelectMany(item => item.RiskyFiles));
        Assert.All(imported.Items.Where(i => i.LocalPath is not null),
            item => Assert.StartsWith(Path.GetFullPath(imported.MaterializedRoot), Path.GetFullPath(item.LocalPath!), StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Pack_RejectsUnsafeLinkAndOversizedFile()
    {
        var folder = await _host.CreateFolderAsync("坏");
        await _host.AddFileAsync(folder.Id, "大文件", "big.bin", new byte[(int)ConfigSyncPolicy.MaxFileBytes + 1]);
        var error = await Assert.ThrowsAsync<ConfigSyncException>(() =>
            _host.Packages.PackFoldersAsync(_host.Workspace, [folder.Id], PublishedId, "坏", 1, Path.Combine(_host.Root, "big.zip")));
        Assert.Equal(ConfigSyncError.TooLarge, error.Error);
        Assert.Contains("50 MB", error.Message);
        Assert.False(File.Exists(Path.Combine(_host.Root, "big.zip")));
    }

    [Fact]
    public async Task ValidCraftedPackage_Imports()
    {
        var path = await BuildAsync(Items(Text("t")), []);
        var package = await _host.Packages.ImportManagedAsync(path, PublishedId, 3, "k", "h");
        Assert.Equal("t", Assert.Single(package.Items).Title);
        Assert.Equal(3, package.Manifest.Version);
    }

    [Fact]
    public async Task TamperedAsset_FailsHashCheck_AndLeavesNothingBehind()
    {
        var content = Encoding.UTF8.GetBytes("original");
        var declared = Convert.ToHexString(SHA256.HashData(content));
        var path = await BuildAsync(Items(FileItem("f", "assets/f.txt")),
            [new Asset("assets/f.txt", "f.txt", Encoding.UTF8.GetBytes("tampered"), content.Length, declared)]);
        var error = await Assert.ThrowsAsync<ConfigSyncException>(() => _host.Packages.ImportManagedAsync(path, PublishedId, 3, "k", "h"));
        Assert.Equal(ConfigSyncError.InvalidPackage, error.Error);
        AssertNoMaterializedFiles();
    }

    [Theory]
    [InlineData("../evil.txt")]
    [InlineData("..\\evil.txt")]
    [InlineData("/abs/evil.txt")]
    [InlineData("C:/evil.txt")]
    [InlineData("assets/../../evil.txt")]
    public async Task PathTraversalEntries_AreRejected(string entry)
    {
        var content = "x"u8.ToArray();
        var path = await BuildAsync(Items(FileItem("f", entry)),
            [new Asset(entry, "evil.txt", content, content.Length, Convert.ToHexString(SHA256.HashData(content)))]);
        var error = await Assert.ThrowsAsync<ConfigSyncException>(() => _host.Packages.ImportManagedAsync(path, PublishedId, 3, "k", "h"));
        Assert.Equal(ConfigSyncError.InvalidPackage, error.Error);
        Assert.False(System.IO.File.Exists(Path.Combine(_host.Root, "evil.txt")));
        AssertNoMaterializedFiles();
    }

    [Fact]
    public async Task TraversalInOriginalName_IsSanitizedNotFollowed()
    {
        var content = "x"u8.ToArray();
        var path = await BuildAsync(Items(FileItem("f", "assets/f.txt")),
            [new Asset("assets/f.txt", "..\\..\\escape.txt", content, content.Length, Convert.ToHexString(SHA256.HashData(content)))]);
        var package = await _host.Packages.ImportManagedAsync(path, PublishedId, 3, "k", "h");
        var written = Assert.Single(package.Items).LocalPath!;
        Assert.Equal("escape.txt", Path.GetFileName(written));
        Assert.StartsWith(Path.GetFullPath(package.MaterializedRoot), Path.GetFullPath(written), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WrongPublicationIdOrVersion_IsRejected()
    {
        var path = await BuildAsync(Items(Text("t")), []);
        await Assert.ThrowsAsync<ConfigSyncException>(() => _host.Packages.ImportManagedAsync(path, Guid.NewGuid(), 3, "k", "h"));
        await Assert.ThrowsAsync<ConfigSyncException>(() => _host.Packages.ImportManagedAsync(path, PublishedId, 4, "k", "h"));
        AssertNoMaterializedFiles();
    }

    [Theory]
    [InlineData("file:///C:/Windows/system32")]
    [InlineData("javascript:alert(1)")]
    [InlineData("ftp://example.com/x")]
    [InlineData("https://user:pass@example.com")]
    [InlineData("example.com/no-scheme")]
    public async Task NonHttpLinks_AreRejected(string url)
    {
        var path = await BuildAsync(Items(new
        {
            id = Guid.NewGuid(), folderId = FolderId, itemType = (int)CollectionItemType.Link, order = 0, title = "l",
            url, fileEntries = Array.Empty<string>(), contentSize = 0
        }), []);
        var error = await Assert.ThrowsAsync<ConfigSyncException>(() => _host.Packages.ImportManagedAsync(path, PublishedId, 3, "k", "h"));
        Assert.Equal(ConfigSyncError.InvalidPackage, error.Error);
    }

    [Fact]
    public async Task DeclaredSingleFileOverFiftyMegabytes_IsRejected()
    {
        var content = "x"u8.ToArray();
        var path = await BuildAsync(Items(FileItem("f", "assets/f.bin")),
            [new Asset("assets/f.bin", "f.bin", content, ConfigSyncPolicy.MaxFileBytes + 1, Convert.ToHexString(SHA256.HashData(content)))]);
        var error = await Assert.ThrowsAsync<ConfigSyncException>(() => _host.Packages.ImportManagedAsync(path, PublishedId, 3, "k", "h"));
        Assert.Equal(ConfigSyncError.InvalidPackage, error.Error);
    }

    [Fact]
    public async Task DeclaredTotalOverTwoHundredMegabytes_IsRejected()
    {
        var content = "x"u8.ToArray();
        var hash = Convert.ToHexString(SHA256.HashData(content));
        var assets = Enumerable.Range(0, 5).Select(index =>
            new Asset($"assets/{index}.bin", $"{index}.bin", content, ConfigSyncPolicy.MaxFileBytes, hash)).ToArray();
        var items = assets.Select((asset, index) => (object)FileItem($"f{index}", asset.EntryName)).ToArray();
        var path = await BuildAsync(Items(items), assets);
        var error = await Assert.ThrowsAsync<ConfigSyncException>(() => _host.Packages.ImportManagedAsync(path, PublishedId, 3, "k", "h"));
        Assert.Equal(ConfigSyncError.InvalidPackage, error.Error);
    }

    [Fact]
    public async Task ImageThatIsNotPngOrJpeg_IsRejected()
    {
        var content = Encoding.ASCII.GetBytes("<svg xmlns='http://www.w3.org/2000/svg'/>");
        var path = await BuildAsync(Items(new
        {
            id = Guid.NewGuid(), folderId = FolderId, itemType = (int)CollectionItemType.Image, order = 0, title = "i",
            imageEntry = "assets/i.png", fileEntries = Array.Empty<string>(), contentSize = content.Length
        }), [new Asset("assets/i.png", "i.png", content, content.Length, Convert.ToHexString(SHA256.HashData(content)))]);
        var error = await Assert.ThrowsAsync<ConfigSyncException>(() => _host.Packages.ImportManagedAsync(path, PublishedId, 3, "k", "h"));
        Assert.Equal(ConfigSyncError.InvalidPackage, error.Error);
    }

    [Fact]
    public async Task TextOverTwoMegabytes_IsRejected()
    {
        var path = await BuildAsync(Items(new
        {
            id = Guid.NewGuid(), folderId = FolderId, itemType = (int)CollectionItemType.Text, order = 0, title = "big",
            textContent = new string('a', (int)ConfigSyncPolicy.MaxTextBytes + 1), fileEntries = Array.Empty<string>(), contentSize = 1
        }), []);
        var error = await Assert.ThrowsAsync<ConfigSyncException>(() => _host.Packages.ImportManagedAsync(path, PublishedId, 3, "k", "h"));
        Assert.Equal(ConfigSyncError.InvalidPackage, error.Error);
    }

    [Fact]
    public async Task UndeclaredEntryInArchive_IsRejected()
    {
        var path = await BuildAsync(Items(Text("t")), [], extraEntries: [("assets/sneaky.exe", [1, 2, 3])]);
        var error = await Assert.ThrowsAsync<ConfigSyncException>(() => _host.Packages.ImportManagedAsync(path, PublishedId, 3, "k", "h"));
        Assert.Equal(ConfigSyncError.InvalidPackage, error.Error);
    }

    [Fact]
    public async Task SeveralFolders_AreAccepted_AndEveryItemKeepsItsFolder()
    {
        var second = Guid.NewGuid();
        var path = await BuildAsync(Items(Text("t"), new
        {
            id = Guid.NewGuid(), folderId = second, itemType = (int)CollectionItemType.Text, order = 1, title = "u",
            textContent = "body", fileEntries = Array.Empty<string>(), contentSize = 4
        }), [], folders:
        [
            new { id = FolderId, name = "a", isSystem = false, order = 0 },
            new { id = second, name = "b", isSystem = false, order = 1 }
        ]);
        var package = await _host.Packages.ImportManagedAsync(path, PublishedId, 3, "k", "h");
        Assert.Equal(["a", "b"], package.Folders.OrderBy(folder => folder.Order).Select(folder => folder.Name));
        Assert.Equal(FolderId, package.Items.Single(item => item.Title == "t").RemoteFolderId);
        Assert.Equal(second, package.Items.Single(item => item.Title == "u").RemoteFolderId);
        Assert.Equal([1, 1], package.Manifest.FolderList.Select(folder => folder.ItemCount));
    }

    [Fact]
    public async Task NoFolders_IsRejected()
    {
        var path = await BuildAsync(Items(), [], folders: []);
        var error = await Assert.ThrowsAsync<ConfigSyncException>(() => _host.Packages.ImportManagedAsync(path, PublishedId, 3, "k", "h"));
        Assert.Equal(ConfigSyncError.InvalidPackage, error.Error);
    }

    [Fact]
    public async Task MoreThanTheFolderLimit_IsRejected()
    {
        var folders = Enumerable.Range(0, ConfigSyncPolicy.MaxFolderCount + 1)
            .Select(index => (object)new { id = Guid.NewGuid(), name = $"f{index}", isSystem = false, order = index }).ToArray();
        var path = await BuildAsync(Items(), [], folders: folders);
        var error = await Assert.ThrowsAsync<ConfigSyncException>(() => _host.Packages.ImportManagedAsync(path, PublishedId, 3, "k", "h"));
        Assert.Equal(ConfigSyncError.InvalidPackage, error.Error);
    }

    [Fact]
    public async Task CorruptArchive_IsRejected()
    {
        var path = Path.Combine(_host.Root, "junk.zip");
        await System.IO.File.WriteAllBytesAsync(path, Encoding.UTF8.GetBytes("this is not a zip file"));
        var error = await Assert.ThrowsAsync<ConfigSyncException>(() => _host.Packages.ImportManagedAsync(path, PublishedId, 3, "k", "h"));
        Assert.Equal(ConfigSyncError.InvalidPackage, error.Error);
    }

    [Fact]
    public async Task ManagedFolders_AreReadOnlyForEveryUserWritePath()
    {
        var folder = await _host.CreateFolderAsync("来源");
        await _host.AddTextAsync(folder.Id, "t", "v1");
        var destination = Path.Combine(_host.Root, "managed.zip");
        var packed = await _host.Packages.PackFoldersAsync(_host.Workspace, [folder.Id], PublishedId, "来源", 1, destination);
        var package = await _host.Packages.ImportManagedAsync(destination, PublishedId, 1, "k", packed.Manifest.Hash);
        var peer = new PairedPeer(Guid.NewGuid(), PeerRole.Publisher, "peer", new string('A', 64), "127.0.0.1:1", DateTimeOffset.UtcNow, PeerStatus.Active);
        await _host.Repository.SavePeerAsync(peer);
        var subscription = await _host.Repository.CreateSubscriptionAsync(_host.Workspace, peer, package, "imports/sync/snap/k/v1.zip");

        var managed = (await _host.Collections.GetFoldersAsync(_host.Workspace)).Single(f => f.ManagedSubscriptionId == subscription.Id);
        var item = Assert.Single(await _host.Collections.GetItemsAsync(_host.Workspace, managed.Id));
        var uncategorized = await _host.Collections.EnsureUncategorizedAsync(_host.Workspace);

        await Assert.ThrowsAsync<InvalidOperationException>(() => _host.AddTextAsync(managed.Id, "x", "y"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _host.Collections.UpdateItemAsync(item with { TextContent = "z" }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _host.Collections.DeleteItemAsync(_host.Workspace, item.Id));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _host.Collections.MoveItemAsync(_host.Workspace, item.Id, uncategorized.Id, 0));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _host.Collections.RenameFolderAsync(_host.Workspace, managed.Id, "改名"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _host.Collections.DeleteFolderAsync(_host.Workspace, managed.Id, uncategorized.Id));
        // 用户不能把自己的条目移入受管文件夹。
        var mine = await _host.AddTextAsync(uncategorized.Id, "mine", "m");
        await Assert.ThrowsAsync<InvalidOperationException>(() => _host.Collections.MoveItemAsync(_host.Workspace, mine.Id, managed.Id, 0));

        // 受管文件夹也不能再被发布出去，且不会随个人配置导出。
        await Assert.ThrowsAsync<ConfigSyncException>(() =>
            _host.Packages.PackFoldersAsync(_host.Workspace, [managed.Id], Guid.NewGuid(), "x", 1, Path.Combine(_host.Root, "again.zip")));
        var export = Path.Combine(_host.Root, "export.clipconfig");
        await _host.Packages.ExportAsync(_host.Workspace, export);
        using var archive = ZipFile.OpenRead(export);
        using var manifest = JsonDocument.Parse(archive.GetEntry("manifest.json")!.Open());
        var names = manifest.RootElement.GetProperty("folders").EnumerateArray().Select(f => f.GetProperty("name").GetString()).ToList();
        // 用户自己的“来源”文件夹与“未分类”在导出里，订阅得到的同名受管文件夹不在。
        Assert.Equal(1, names.Count(name => name == "来源"));
        var titles = manifest.RootElement.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("title").GetString()).ToList();
        Assert.Equal(["mine", "t"], titles.Order());
    }

    private void AssertNoMaterializedFiles()
    {
        var root = Path.Combine(_host.Paths.ImportsPath, "sync", "m");
        Assert.False(Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories).Any(),
            "被拒收的包不应在受管目录留下任何文件。");
    }

    private static object Text(string title) => new
    {
        id = Guid.NewGuid(), folderId = FolderId, itemType = (int)CollectionItemType.Text, order = 0, title,
        textContent = "body", fileEntries = Array.Empty<string>(), contentSize = 4
    };

    private static object FileItem(string title, string entry) => new
    {
        id = Guid.NewGuid(), folderId = FolderId, itemType = (int)CollectionItemType.File, order = 0, title,
        fileEntries = new[] { entry }, contentSize = 1
    };

    private static object[] Items(params object[] items) => items;

    private sealed record Asset(string EntryName, string OriginalName, byte[] Content, long DeclaredSize, string DeclaredSha);

    private async Task<string> BuildAsync(object[] items, Asset[] assets, (string Name, byte[] Content)[]? extraEntries = null,
        object[]? folders = null, int version = 3)
    {
        var path = Path.Combine(_host.Root, $"crafted-{Guid.NewGuid():N}.zip");
        Directory.CreateDirectory(_host.Root);
        await using (var stream = new FileStream(path, FileMode.CreateNew))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            foreach (var asset in assets)
            {
                var entry = archive.CreateEntry(asset.EntryName.TrimStart('/'));
                await using var output = entry.Open();
                await output.WriteAsync(asset.Content);
            }
            foreach (var (name, bytes) in extraEntries ?? [])
            {
                var entry = archive.CreateEntry(name);
                await using var output = entry.Open();
                await output.WriteAsync(bytes);
            }
            var manifest = new
            {
                formatVersion = 1,
                packageId = Guid.NewGuid(),
                createdAtUtc = DateTimeOffset.UtcNow,
                folders = folders ?? [new { id = FolderId, name = "受管", isSystem = false, order = 0 }],
                items,
                assets = assets.Select(a => new { entryName = a.EntryName, originalName = a.OriginalName, byteSize = a.DeclaredSize, sha256 = a.DeclaredSha }),
                missingFileCount = 0,
                includedBytes = assets.Sum(a => a.DeclaredSize),
                sync = new { publishedId = PublishedId, name = "受管", version }
            };
            var manifestEntry = archive.CreateEntry("manifest.json");
            await using var manifestOutput = manifestEntry.Open();
            await JsonSerializer.SerializeAsync(manifestOutput, manifest, Web);
        }
        return path;
    }
}

public sealed class ConfigSyncSchemaTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ClipboardManager.ConfigSync.Schema", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task EmptyDatabase_InitializesToSchemaEightWithSyncTables()
    {
        var factory = new SqliteConnectionFactory(new AppPaths(_root));
        await Initialize(factory);

        Assert.Equal(8, SqliteDatabaseInitializer.CurrentVersion);
        Assert.Equal(8L, await Scalar(factory, "PRAGMA user_version;"));
        foreach (var table in new[] { "published_config", "paired_peer", "subscription", "config_snapshot" })
            Assert.Equal(1L, await Scalar(factory, $"SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='{table}';"));
        Assert.Equal(1L, await Scalar(factory, "SELECT COUNT(*) FROM pragma_table_info('collection_folders') WHERE name='managed_subscription_id';"));
    }

    [Fact]
    public async Task VersionSeven_UpgradesWithoutLosingDataAndKeepsABackup()
    {
        var factory = new SqliteConnectionFactory(new AppPaths(_root));
        await Initialize(factory);
        var workspace = Guid.NewGuid();
        var folder = Guid.NewGuid();
        var item = Guid.NewGuid();
        var history = Guid.NewGuid();
        await Execute(factory, $"INSERT INTO workspaces(id,workspace_type,name,created_at,updated_at) VALUES('{workspace}',0,'test','2026-01-01','2026-01-01');");
        await Execute(factory, $"INSERT INTO collection_folders(id,workspace_id,name,sort_key,is_system,created_at,updated_at,local_version,sync_status) VALUES('{folder}','{workspace}','保留',1,0,'2026-01-01','2026-01-01',1,0);");
        await Execute(factory, $"INSERT INTO collection_items(id,workspace_id,folder_id,item_type,title,text_content,sort_key,created_at,updated_at,local_version,sync_status) VALUES('{item}','{workspace}','{folder}',0,'标题','正文',1,'2026-01-01','2026-01-01',1,0);");
        await Execute(factory, $"INSERT INTO clipboard_items(id,workspace_id,device_id,item_type,content_hash,text_content,created_at,updated_at,captured_at) VALUES('{history}','{workspace}','{Guid.NewGuid()}',0,'hash','历史','2026-01-01','2026-01-01','2026-01-01');");

        // 还原成 v7 形态：没有同步表和受管列。
        await Execute(factory, """
            DROP TABLE config_snapshot; DROP TABLE subscription; DROP TABLE paired_peer; DROP TABLE published_config;
            ALTER TABLE collection_folders DROP COLUMN managed_subscription_id;
            PRAGMA user_version=7;
            """);
        SqliteConnection.ClearAllPools();

        await Initialize(factory);

        Assert.Equal(8L, await Scalar(factory, "PRAGMA user_version;"));
        Assert.Equal(1L, await Scalar(factory, $"SELECT COUNT(*) FROM collection_folders WHERE id='{folder}' AND managed_subscription_id IS NULL;"));
        Assert.Equal(1L, await Scalar(factory, $"SELECT COUNT(*) FROM collection_items WHERE id='{item}' AND text_content='正文';"));
        Assert.Equal(1L, await Scalar(factory, $"SELECT COUNT(*) FROM clipboard_items WHERE id='{history}' AND text_content='历史';"));
        foreach (var table in new[] { "published_config", "paired_peer", "subscription", "config_snapshot" })
            Assert.Equal(1L, await Scalar(factory, $"SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='{table}';"));
        Assert.Single(Directory.GetFiles(new AppPaths(_root).Root, "clipboard-v7-before-v8-*.db", SearchOption.AllDirectories));

        // 再次初始化是幂等的。
        await Initialize(factory);
        Assert.Equal(8L, await Scalar(factory, "PRAGMA user_version;"));
        Assert.Equal(1L, await Scalar(factory, $"SELECT COUNT(*) FROM collection_items WHERE id='{item}';"));
    }

    [Fact]
    public async Task Repository_StartsEmpty_AndRoundTripsPeersAndPublications()
    {
        var factory = new SqliteConnectionFactory(new AppPaths(_root));
        await Initialize(factory);
        var repository = new SqliteConfigSyncRepository(factory);
        Assert.Empty(await repository.GetPublishedAsync());
        Assert.Empty(await repository.GetPeersAsync(PeerRole.Publisher));
        Assert.Empty(await repository.GetSubscriptionsAsync());

        var peer = new PairedPeer(Guid.NewGuid(), PeerRole.Subscriber, "笔记本", new string('A', 64), null, DateTimeOffset.UtcNow, PeerStatus.Active);
        await repository.SavePeerAsync(peer);
        Assert.Equal(peer.Id, (await repository.FindActivePeerAsync(PeerRole.Subscriber, peer.Fingerprint.ToLowerInvariant()))?.Id);
        Assert.Null(await repository.FindActivePeerAsync(PeerRole.Publisher, peer.Fingerprint));
        await repository.RevokePeerAsync(peer.Id);
        Assert.Null(await repository.FindActivePeerAsync(PeerRole.Subscriber, peer.Fingerprint));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, true); } catch { }
    }

    private static Task Initialize(SqliteConnectionFactory factory) =>
        new SqliteDatabaseInitializer(factory, NullLogger<SqliteDatabaseInitializer>.Instance).InitializeAsync();

    private static async Task Execute(SqliteConnectionFactory factory, string sql)
    {
        await using var connection = factory.Create();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> Scalar(SqliteConnectionFactory factory, string sql)
    {
        await using var connection = factory.Create();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }
}

public sealed class ConfigDiscoveryMessageTests
{
    private static readonly string Fingerprint = "0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF";

    [Fact]
    public void Announcement_CarriesOnlyNameVersionAndFingerprintPrefix()
    {
        var bytes = ConfigDiscoveryMessage.Encode("办公室电脑", Fingerprint, 53318, [new PublicationAnnouncement("团队常用", 3)]);
        var text = Encoding.UTF8.GetString(bytes);

        Assert.DoesNotContain(Fingerprint, text);
        Assert.Contains(Fingerprint[..12], text);
        using var document = JsonDocument.Parse(bytes);
        Assert.Equal(["fp", "name", "port", "proto", "pubs"], document.RootElement.EnumerateObject().Select(p => p.Name).Order());
        var publication = document.RootElement.GetProperty("pubs")[0];
        Assert.Equal(["name", "version"], publication.EnumerateObject().Select(p => p.Name).Order());

        Assert.True(ConfigDiscoveryMessage.TryDecode(bytes, IPAddress.Parse("192.168.1.20"), DateTimeOffset.UtcNow, out var publisher));
        Assert.Equal("办公室电脑", publisher.DeviceName);
        Assert.Equal("192.168.1.20:53318", publisher.Address);
        Assert.Equal(Fingerprint[..12], publisher.FingerprintPrefix);
        Assert.Equal(new PublicationAnnouncement("团队常用", 3), Assert.Single(publisher.Publications));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{\"proto\":1,\"name\":\"x\",\"fp\":\"ZZZZZZ\",\"port\":53318}")]
    [InlineData("{\"proto\":1,\"name\":\"x\",\"fp\":\"0123456789AB\",\"port\":0}")]
    [InlineData("{\"proto\":1,\"name\":\"x\",\"fp\":\"0123456789AB\",\"port\":70000}")]
    [InlineData("{\"proto\":1,\"name\":\"x\",\"fp\":\"01\",\"port\":53318}")]
    public void GarbageDatagrams_AreIgnored(string text) =>
        Assert.False(ConfigDiscoveryMessage.TryDecode(Encoding.UTF8.GetBytes(text), IPAddress.Loopback, DateTimeOffset.UtcNow, out _));

    [Fact]
    public void OversizedDatagram_IsIgnored() =>
        Assert.False(ConfigDiscoveryMessage.TryDecode(new byte[ConfigDiscoveryMessage.MaxDatagramBytes + 1], IPAddress.Loopback, DateTimeOffset.UtcNow, out _));

    [Fact]
    public void ManyPublications_NeverExceedOneDatagram()
    {
        var many = Enumerable.Range(0, 200).Select(i => new PublicationAnnouncement(new string('名', 80) + i, i + 1)).ToList();
        Assert.True(ConfigDiscoveryMessage.Encode("设备", Fingerprint, 53318, many).Length <= ConfigDiscoveryMessage.MaxDatagramBytes);
    }
}

public sealed class ConfigSyncIdentityTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ClipboardManager.ConfigSync.Identity", Guid.NewGuid().ToString("N"));

    [Fact]
    public void Fingerprint_IsStableAcrossRestarts_AndChangesWhenTheKeyIsLost()
    {
        var vault = new MemoryVault();
        var first = new ConfigSyncIdentity(new AppPaths(_root), vault, NullLogger<ConfigSyncIdentity>.Instance).Fingerprint;
        var second = new ConfigSyncIdentity(new AppPaths(_root), vault, NullLogger<ConfigSyncIdentity>.Instance).Fingerprint;
        Assert.Equal(first, second);
        Assert.Equal(64, first.Length);

        var lostKey = new ConfigSyncIdentity(new AppPaths(_root), new MemoryVault(), NullLogger<ConfigSyncIdentity>.Instance).Fingerprint;
        Assert.NotEqual(first, lostKey);
    }

    [Fact]
    public void PrivateKey_IsNotWrittenToTheDataDirectory()
    {
        var vault = new MemoryVault();
        _ = new ConfigSyncIdentity(new AppPaths(_root), vault, NullLogger<ConfigSyncIdentity>.Instance).Fingerprint;
        var certificate = new System.Security.Cryptography.X509Certificates.X509Certificate2(Path.Combine(_root, "sync", "identity.cer"));
        Assert.False(certificate.HasPrivateKey);
        Assert.Single(vault.Values);
    }

    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    private sealed class MemoryVault : ICredentialVault
    {
        public Dictionary<string, string> Values { get; } = [];
        public Task<string?> ReadAsync(string key, CancellationToken cancellationToken = default) =>
            Task.FromResult(Values.TryGetValue(key, out var value) ? value : null);
        public Task WriteAsync(string key, string secret, CancellationToken cancellationToken = default)
        {
            Values[key] = secret;
            return Task.CompletedTask;
        }
        public Task DeleteAsync(string key, CancellationToken cancellationToken = default)
        {
            Values.Remove(key);
            return Task.CompletedTask;
        }
    }
}

public sealed class ConfigAutoPublishTests : IAsyncLifetime
{
    private ConfigSyncTestHost _host = null!;
    private ClipboardManager.Application.Services.ConfigSync.ConfigSyncCoordinator _coordinator = null!;

    public async Task InitializeAsync()
    {
        _host = await ConfigSyncTestHost.CreateAsync("autopublish");
        _coordinator = new ClipboardManager.Application.Services.ConfigSync.ConfigSyncCoordinator(
            _host.Server, new NoDiscovery(), _host.Repository, _host.Publishing, _host.Subscriptions, _host.Endpoint,
            _host.Pairing, _host.Clock, NullLogger<ClipboardManager.Application.Services.ConfigSync.ConfigSyncCoordinator>.Instance);
    }

    public async Task DisposeAsync()
    {
        _coordinator.Dispose();
        await _host.DisposeAsync();
    }

    [Fact]
    public async Task PublishesOnlyAfterSixtySecondsWithoutFurtherChanges()
    {
        var folder = await _host.CreateFolderAsync("自动");
        var item = await _host.AddTextAsync(folder.Id, "t", "v1");
        var published = await _host.Publishing.PublishAsync(_host.Workspace, folder.Id);
        await _host.Publishing.SetAutoPublishAsync(published.Config.Id, true);

        // 首次巡检只记录基线；没有改动就不会产生新版本。
        Assert.Equal(0, await _coordinator.AutoPublishTickAsync());
        _host.Clock.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(0, await _coordinator.AutoPublishTickAsync());
        Assert.Equal(1, (await _host.Repository.GetPublishedAsync(published.Config.Id))!.CurrentVersion);

        // 改动后的静默期内不发布，期间再次改动会重新计时。
        await Edit(item.Id, folder.Id, "v2");
        Assert.Equal(0, await _coordinator.AutoPublishTickAsync());
        _host.Clock.Advance(TimeSpan.FromSeconds(59));
        Assert.Equal(0, await _coordinator.AutoPublishTickAsync());
        await Edit(item.Id, folder.Id, "v3");
        Assert.Equal(0, await _coordinator.AutoPublishTickAsync());
        _host.Clock.Advance(TimeSpan.FromSeconds(59));
        Assert.Equal(0, await _coordinator.AutoPublishTickAsync());
        Assert.Equal(1, (await _host.Repository.GetPublishedAsync(published.Config.Id))!.CurrentVersion);

        // 满 60 秒无新改动才发布，且只发布一次。
        _host.Clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(1, await _coordinator.AutoPublishTickAsync());
        Assert.Equal(2, (await _host.Repository.GetPublishedAsync(published.Config.Id))!.CurrentVersion);
        _host.Clock.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(0, await _coordinator.AutoPublishTickAsync());
        Assert.Equal(2, (await _host.Repository.GetPublishedAsync(published.Config.Id))!.CurrentVersion);
    }

    [Fact]
    public async Task SwitchedOff_NeverPublishes()
    {
        var folder = await _host.CreateFolderAsync("手动");
        var item = await _host.AddTextAsync(folder.Id, "t", "v1");
        var published = await _host.Publishing.PublishAsync(_host.Workspace, folder.Id);
        await _coordinator.AutoPublishTickAsync();
        await Edit(item.Id, folder.Id, "v2");
        _host.Clock.Advance(TimeSpan.FromMinutes(10));
        Assert.Equal(0, await _coordinator.AutoPublishTickAsync());
        Assert.Equal(1, (await _host.Repository.GetPublishedAsync(published.Config.Id))!.CurrentVersion);
    }

    private async Task Edit(Guid itemId, Guid folderId, string text)
    {
        var current = (await _host.Collections.GetItemsAsync(_host.Workspace, folderId)).Single(i => i.Id == itemId);
        await _host.Collections.UpdateItemAsync(current with { TextContent = text });
    }

    private sealed class NoDiscovery : IConfigDiscovery
    {
        public event EventHandler? Changed { add { } remove { } }
        public IReadOnlyList<DiscoveredPublisher> GetPublishers() => [];
        public void StartListening() { }
        public void StopListening() { }
        public void SetAnnouncement(IReadOnlyList<PublicationAnnouncement> publications) { }
    }
}
