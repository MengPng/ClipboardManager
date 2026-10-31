using ClipboardManager.Application.Services.ConfigSync;
using ClipboardManager.Domain.Contracts;
using ClipboardManager.Domain.Models;
using ClipboardManager.Infrastructure.ConfigSync;
using ClipboardManager.Infrastructure.Data;
using ClipboardManager.Infrastructure.Paths;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClipboardManager.Infrastructure.Tests;

/// <summary>
/// 一个完整的“对等端”：独立数据目录、真实 SQLite、真实打包服务、真实自签证书和 TLS 监听（端口 0）。
/// 两个实例在同一进程内通过回环地址互相配对、发布与订阅，覆盖除组播发现和真实防火墙以外的整条链路。
/// </summary>
internal sealed class ConfigSyncTestHost : IAsyncDisposable
{
    public string Root { get; }
    public AppPaths Paths { get; }
    public Guid Workspace { get; } = Guid.NewGuid();
    public SqliteConnectionFactory Factory { get; }
    public SqliteCollectionRepository Collections { get; }
    public SqliteConfigSyncRepository Repository { get; }
    public PersonalConfigTransferService Packages { get; }
    public ConfigSyncIdentity Identity { get; }
    public PairingSessions Pairing { get; }
    public ConfigSyncEndpoint Endpoint { get; }
    public ConfigSyncServer Server { get; }
    public ConfigSyncClient Client { get; }
    public ConfigPublishService Publishing { get; }
    public ConfigSubscriptionService Subscriptions { get; }
    public ManualClock Clock { get; }

    public string Address => $"127.0.0.1:{Server.Status.Port}";
    public string Fingerprint => Identity.Fingerprint;

    private ConfigSyncTestHost(string root, ManualClock clock)
    {
        Root = root;
        Clock = clock;
        Paths = new AppPaths(root);
        Factory = new SqliteConnectionFactory(Paths);
        Collections = new SqliteCollectionRepository(Factory);
        Repository = new SqliteConfigSyncRepository(Factory);
        Packages = new PersonalConfigTransferService(Paths, Collections, new UnusedStorageMaintenance());
        Identity = new ConfigSyncIdentity(Paths, new MemoryVault(), NullLogger<ConfigSyncIdentity>.Instance);
        Pairing = new PairingSessions(clock);
        Endpoint = new ConfigSyncEndpoint(Repository, Packages, Identity, Pairing, clock);
        Server = new ConfigSyncServer(Identity, Endpoint, NullLogger<ConfigSyncServer>.Instance, new ConfigSyncServerOptions(0));
        Client = new ConfigSyncClient(Identity, NullLogger<ConfigSyncClient>.Instance);
        Publishing = new ConfigPublishService(Repository, Packages, Collections, Pairing, clock,
            NullLogger<ConfigPublishService>.Instance);
        Subscriptions = new ConfigSubscriptionService(Repository, Client, Packages, Collections, new UnusedAssetStore(), clock,
            NullLogger<ConfigSubscriptionService>.Instance);
    }

    public static async Task<ConfigSyncTestHost> CreateAsync(string name, ManualClock? clock = null)
    {
        var root = Path.Combine(Path.GetTempPath(), "ClipboardManager.ConfigSync.Tests", Guid.NewGuid().ToString("N"), name);
        var host = new ConfigSyncTestHost(root, clock ?? new ManualClock());
        await new SqliteDatabaseInitializer(host.Factory, NullLogger<SqliteDatabaseInitializer>.Instance).InitializeAsync();
        await host.RegisterWorkspaceAsync();
        return host;
    }

    public async Task StartServerAsync()
    {
        await Server.StartAsync();
        Assert.True(Server.Status.IsRunning, Server.Status.LastError);
        Assert.NotEqual(0, Server.Status.Port);
    }

    public async Task<CollectionFolder> CreateFolderAsync(string name) => await Collections.CreateFolderAsync(Workspace, name);

    public Task<CollectionItem> AddTextAsync(Guid folderId, string title, string text) =>
        Collections.CreateItemAsync(Workspace, new CreateCollectionItem(folderId, CollectionItemType.Text, title, text));

    public Task<CollectionItem> AddLinkAsync(Guid folderId, string title, string url) =>
        Collections.CreateItemAsync(Workspace, new CreateCollectionItem(folderId, CollectionItemType.Link, title, Url: url));

    public async Task<CollectionItem> AddFileAsync(Guid folderId, string title, string fileName, byte[] content)
    {
        var path = Path.Combine(Root, "source-files", Guid.NewGuid().ToString("N"), fileName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, content);
        return await Collections.CreateItemAsync(Workspace, new CreateCollectionItem(folderId, CollectionItemType.File, title,
            FileManifest: System.Text.Json.JsonSerializer.Serialize(new[] { path }), ContentSize: content.Length));
    }

    public async Task<CollectionItem> AddImageAsync(Guid folderId, string title, byte[] png)
    {
        var path = Path.Combine(Root, "source-files", Guid.NewGuid().ToString("N"), "picture.png");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, png);
        return await Collections.CreateItemAsync(Workspace, new CreateCollectionItem(folderId, CollectionItemType.Image, title,
            LocalPath: path, ContentSize: png.Length));
    }

    private async Task RegisterWorkspaceAsync()
    {
        await using var connection = Factory.Create();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT OR IGNORE INTO workspaces(id,workspace_type,name,created_at,updated_at)VALUES($id,0,'test',$now,$now);";
        command.Parameters.AddWithValue("$id", Workspace.ToString("D"));
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await Server.DisposeAsync();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(Path.GetDirectoryName(Root)!, true); } catch { }
    }

    public static byte[] Png(int seed) =>
        [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0x0D, (byte)seed, (byte)(seed >> 8), 3, 4, 5, 6, 7, 8];

    public sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset now = new(2026, 10, 31, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan by) => now += by;
    }

    private sealed class MemoryVault : ICredentialVault
    {
        private readonly Dictionary<string, string> values = [];
        public Task<string?> ReadAsync(string key, CancellationToken cancellationToken = default) =>
            Task.FromResult(values.TryGetValue(key, out var value) ? value : null);
        public Task WriteAsync(string key, string secret, CancellationToken cancellationToken = default)
        {
            values[key] = secret;
            return Task.CompletedTask;
        }
        public Task DeleteAsync(string key, CancellationToken cancellationToken = default)
        {
            values.Remove(key);
            return Task.CompletedTask;
        }
    }

    private sealed class UnusedStorageMaintenance : IStorageMaintenanceService
    {
        public Task<StorageUsageStats> GetUsageStatsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task CreateBackupAsync(string backupPath, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<BackupValidationResult> ValidateBackupAsync(string backupPath, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<RestoreResult> RestoreAsync(string backupPath, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AssetCleanupPreview> PreviewOrphanedAssetsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AssetCleanupResult> CleanupOrphanedAssetsAsync(AssetCleanupPreview preview, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class UnusedAssetStore : ILocalAssetStore
    {
        public Task<StoredAsset> StoreImageAsync(byte[] imageBytes, string mimeType, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<StoredAsset> ImportImageAsync(string sourcePath, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
