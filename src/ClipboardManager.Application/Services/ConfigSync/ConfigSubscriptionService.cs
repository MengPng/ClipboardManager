using System.Text.Json;
using ClipboardManager.Domain.Contracts;
using ClipboardManager.Domain.Models;
using Microsoft.Extensions.Logging;

namespace ClipboardManager.Application.Services.ConfigSync;

/// <summary>
/// 订阅方：配对、预览、订阅、检查更新、更新（先校验后原子替换）、回退与取消订阅。
/// 任何失败都保留原版本；受管文件夹只读，内容只能通过本服务改变。
/// </summary>
public sealed class ConfigSubscriptionService(
    IConfigSyncRepository repository,
    IConfigSyncClient client,
    IConfigPackageService packages,
    ICollectionRepository collections,
    ILocalAssetStore assetStore,
    TimeProvider time,
    ILogger<ConfigSubscriptionService> logger)
{
    private readonly SemaphoreSlim operationLock = new(1, 1);

    public event EventHandler? Changed;

    public Task<IReadOnlyList<PairedPeer>> GetPublisherPeersAsync(CancellationToken cancellationToken = default) =>
        repository.GetPeersAsync(PeerRole.Publisher, cancellationToken);

    public Task<IReadOnlyList<ConfigSubscription>> GetSubscriptionsAsync(CancellationToken cancellationToken = default) =>
        repository.GetSubscriptionsAsync(cancellationToken);

    public Task<IReadOnlyList<ConfigSnapshot>> GetSnapshotsAsync(Guid subscriptionId, CancellationToken cancellationToken = default) =>
        repository.GetSnapshotsAsync(subscriptionId, cancellationToken);

    public Task<HelloInfo> HelloAsync(string address, CancellationToken cancellationToken = default) =>
        client.HelloAsync(address, cancellationToken);

    public async Task<PairedPeer> PairAsync(string address, string code, CancellationToken cancellationToken = default)
    {
        var normalized = NormalizeAddress(address);
        var result = await client.PairAsync(normalized, code, cancellationToken);
        await repository.SavePeerAsync(new PairedPeer(Guid.NewGuid(), PeerRole.Publisher, result.ServerName,
            result.ServerFingerprint.ToUpperInvariant(), normalized, time.GetUtcNow(), PeerStatus.Active), cancellationToken);
        Changed?.Invoke(this, EventArgs.Empty);
        return await repository.FindActivePeerAsync(PeerRole.Publisher, result.ServerFingerprint, cancellationToken)
            ?? throw new InvalidOperationException("配对完成后无法读取配对记录。");
    }

    public async Task RemovePublisherAsync(Guid peerId, CancellationToken cancellationToken = default)
    {
        if ((await repository.GetSubscriptionsAsync(cancellationToken)).Any(subscription => subscription.PeerId == peerId))
            throw new ConfigSyncException(ConfigSyncError.Local, "这台设备下还有订阅的配置。请先取消这些订阅，再移除配对。");
        await repository.RevokePeerAsync(peerId, cancellationToken);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>可订阅的发布列表；成功连通后记住对方当前地址（地址可能随 DHCP 变化）。</summary>
    public async Task<IReadOnlyList<PublicationInfo>> ListPublicationsAsync(Guid peerId, string? addressOverride = null,
        CancellationToken cancellationToken = default)
    {
        var peer = await RequirePeerAsync(peerId, cancellationToken);
        var address = addressOverride is null ? RequireAddress(peer) : NormalizeAddress(addressOverride);
        var list = await client.ListPublicationsAsync(address, peer.Fingerprint, cancellationToken);
        if (address != peer.LastAddress) await repository.UpdatePeerAddressAsync(peer.Id, address, cancellationToken);
        return list;
    }

    public async Task<ConfigManifest> GetRemoteManifestAsync(Guid peerId, Guid publishedId, string? addressOverride = null,
        CancellationToken cancellationToken = default)
    {
        var peer = await RequirePeerAsync(peerId, cancellationToken);
        var address = addressOverride is null ? RequireAddress(peer) : NormalizeAddress(addressOverride);
        var manifest = await client.GetManifestAsync(address, peer.Fingerprint, publishedId, cancellationToken);
        if (address != peer.LastAddress) await repository.UpdatePeerAddressAsync(peer.Id, address, cancellationToken);
        return manifest;
    }

    public async Task<ConfigSubscription> SubscribeAsync(Guid workspaceId, Guid peerId, Guid publishedId, bool confirmRisky,
        string? addressOverride = null, CancellationToken cancellationToken = default)
    {
        await operationLock.WaitAsync(cancellationToken);
        try
        {
            var peer = await RequirePeerAsync(peerId, cancellationToken);
            if ((await repository.GetSubscriptionsAsync(cancellationToken)).Any(subscription =>
                    subscription.WorkspaceId == workspaceId && subscription.PeerId == peerId && subscription.PublishedId == publishedId))
                throw new ConfigSyncException(ConfigSyncError.Local, "已经订阅过这份配置。");

            var address = addressOverride is null ? RequireAddress(peer) : NormalizeAddress(addressOverride);
            var manifest = await client.GetManifestAsync(address, peer.Fingerprint, publishedId, cancellationToken);
            EnsureRiskConfirmed(manifest, confirmRisky);

            var key = SubscriptionKey(peerId, publishedId);
            var (temp, package) = await DownloadAndImportAsync(address, peer, manifest, key, cancellationToken);
            string snapshot;
            try
            {
                EnsureRiskConfirmed(package.Manifest, confirmRisky);
                snapshot = MoveToSnapshot(temp, key, manifest.Version);
            }
            catch
            {
                TryDeleteFile(temp);
                TryDeleteDirectory(package.MaterializedRoot);
                throw;
            }

            try
            {
                var created = await repository.CreateSubscriptionAsync(workspaceId, peer, package, snapshot, cancellationToken);
                if (address != peer.LastAddress) await repository.UpdatePeerAddressAsync(peer.Id, address, cancellationToken);
                logger.LogInformation("已订阅配置 {PublishedId} v{Version}（{Items} 项）", publishedId, manifest.Version, package.Items.Count);
                Changed?.Invoke(this, EventArgs.Empty);
                return created;
            }
            catch
            {
                TryDeleteFile(packages.ResolvePath(snapshot));
                TryDeleteDirectory(package.MaterializedRoot);
                throw;
            }
        }
        finally
        {
            operationLock.Release();
        }
    }

    /// <summary>向发布者查询最新版本并与本机已安装版本比较；离线或出错只记录到订阅状态，不抛出。</summary>
    public async Task<ConfigUpdateCheck> CheckAsync(Guid subscriptionId, CancellationToken cancellationToken = default)
    {
        var subscription = await RequireSubscriptionAsync(subscriptionId, cancellationToken);
        var peer = await RequirePeerAsync(subscription.PeerId, cancellationToken);
        try
        {
            var address = RequireAddress(peer);
            var remote = await client.GetManifestAsync(address, peer.Fingerprint, subscription.PublishedId, cancellationToken);
            await repository.RecordCheckAsync(subscription.Id, time.GetUtcNow(), remote.Version, null, cancellationToken);
            Changed?.Invoke(this, EventArgs.Empty);
            return Evaluate(subscription, remote);
        }
        catch (ConfigSyncException exception) when (exception.Error is ConfigSyncError.Unreachable or ConfigSyncError.Timeout)
        {
            var message = "发布者不在线或无法连接：" + exception.Message;
            await repository.RecordCheckAsync(subscription.Id, time.GetUtcNow(), null, message, cancellationToken);
            Changed?.Invoke(this, EventArgs.Empty);
            return new ConfigUpdateCheck(ConfigUpdateState.PublisherOffline, message);
        }
        catch (ConfigSyncException exception)
        {
            await repository.RecordCheckAsync(subscription.Id, time.GetUtcNow(), null, exception.Message, cancellationToken);
            Changed?.Invoke(this, EventArgs.Empty);
            return new ConfigUpdateCheck(ConfigUpdateState.Failed, exception.Message);
        }
    }

    public static ConfigUpdateCheck Evaluate(ConfigSubscription subscription, ConfigManifest remote)
    {
        if (remote.Version <= subscription.LocalVersion)
            return new ConfigUpdateCheck(ConfigUpdateState.UpToDate, $"已是最新版本 v{subscription.LocalVersion}", remote);
        var changes = ConfigSyncPolicy.Compare(ConfigSyncPolicy.ParseManifest(subscription.ManifestJson), remote);
        if (subscription.StayOnCurrent)
            return new ConfigUpdateCheck(ConfigUpdateState.Stayed, $"已停留在 v{subscription.LocalVersion}，发布者有新版本 v{remote.Version}", remote, changes);
        if (subscription.SkippedVersion == remote.Version)
            return new ConfigUpdateCheck(ConfigUpdateState.Skipped, $"已跳过 v{remote.Version}", remote, changes);
        return new ConfigUpdateCheck(ConfigUpdateState.UpdateAvailable,
            $"有新版本 v{remote.Version}（当前 v{subscription.LocalVersion}）", remote, changes);
    }

    /// <summary>
    /// 更新到发布者最新版本：下载 → SHA-256 校验 → 包内逐项校验并物化 → 单事务替换受管内容。
    /// 当前版本的快照在安装时已保存，更新失败不会改动任何已安装内容。
    /// </summary>
    public async Task<ConfigSubscription> ApplyUpdateAsync(Guid subscriptionId, bool confirmRisky,
        CancellationToken cancellationToken = default)
    {
        await operationLock.WaitAsync(cancellationToken);
        try
        {
            var subscription = await RequireSubscriptionAsync(subscriptionId, cancellationToken);
            var peer = await RequirePeerAsync(subscription.PeerId, cancellationToken);
            var address = RequireAddress(peer);
            var remote = await client.GetManifestAsync(address, peer.Fingerprint, subscription.PublishedId, cancellationToken);
            if (remote.Version <= subscription.LocalVersion)
                throw new ConfigSyncException(ConfigSyncError.VersionRejected,
                    $"发布者的版本 v{remote.Version} 不高于本机已安装的 v{subscription.LocalVersion}，已拒收（版本号只能增加）。");
            EnsureRiskConfirmed(remote, confirmRisky);

            var current = (await repository.GetSnapshotsAsync(subscription.Id, cancellationToken))
                .FirstOrDefault(snapshot => snapshot.Version == subscription.LocalVersion);
            if (current is null || !File.Exists(packages.ResolvePath(current.SnapshotPath)))
                logger.LogWarning("订阅 {SubscriptionId} 的当前版本快照缺失，本次更新后将无法回退到 v{Version}", subscription.Id, subscription.LocalVersion);

            var key = SubscriptionKey(subscription.PeerId, subscription.PublishedId);
            var (temp, package) = await DownloadAndImportAsync(address, peer, remote, key, cancellationToken);
            string snapshot;
            try
            {
                EnsureRiskConfirmed(package.Manifest, confirmRisky);
                snapshot = MoveToSnapshot(temp, key, remote.Version);
            }
            catch
            {
                TryDeleteFile(temp);
                TryDeleteDirectory(package.MaterializedRoot);
                throw;
            }

            IReadOnlyList<string> removed;
            try
            {
                removed = await repository.ReplaceManagedContentAsync(subscription.Id, package, snapshot, false, null, cancellationToken);
            }
            catch
            {
                TryDeleteFile(packages.ResolvePath(snapshot));
                TryDeleteDirectory(package.MaterializedRoot);
                throw;
            }
            foreach (var path in removed) TryDeleteFile(packages.ResolvePath(path));
            CleanOldMaterialized(key, package.MaterializedRoot);
            logger.LogInformation("订阅 {SubscriptionId} 已更新到 v{Version}", subscription.Id, remote.Version);
            Changed?.Invoke(this, EventArgs.Empty);
            return await RequireSubscriptionAsync(subscription.Id, cancellationToken);
        }
        finally
        {
            operationLock.Release();
        }
    }

    /// <summary>回退到上一个保留的版本，并自动跳过被回退的版本（可在订阅设置里取消跳过）。</summary>
    public async Task<ConfigSubscription> RollbackAsync(Guid subscriptionId, CancellationToken cancellationToken = default)
    {
        await operationLock.WaitAsync(cancellationToken);
        try
        {
            var subscription = await RequireSubscriptionAsync(subscriptionId, cancellationToken);
            var target = (await repository.GetSnapshotsAsync(subscription.Id, cancellationToken))
                .Where(snapshot => snapshot.Version < subscription.LocalVersion)
                .OrderByDescending(snapshot => snapshot.Version)
                .FirstOrDefault() ?? throw new ConfigSyncException(ConfigSyncError.Local, "没有可回退的旧版本。");
            var path = packages.ResolvePath(target.SnapshotPath);
            if (!File.Exists(path))
                throw new ConfigSyncException(ConfigSyncError.Local, $"v{target.Version} 的快照文件已丢失，无法回退。");

            var key = SubscriptionKey(subscription.PeerId, subscription.PublishedId);
            var hash = await packages.ComputeFileHashAsync(path, cancellationToken);
            var package = await packages.ImportManagedAsync(path, subscription.PublishedId, target.Version, key, hash, cancellationToken);
            IReadOnlyList<string> removed;
            try
            {
                removed = await repository.ReplaceManagedContentAsync(subscription.Id, package, target.SnapshotPath, true,
                    subscription.LocalVersion, cancellationToken);
            }
            catch
            {
                TryDeleteDirectory(package.MaterializedRoot);
                throw;
            }
            foreach (var stale in removed) TryDeleteFile(packages.ResolvePath(stale));
            CleanOldMaterialized(key, package.MaterializedRoot);
            logger.LogInformation("订阅 {SubscriptionId} 已回退到 v{Version}", subscription.Id, target.Version);
            Changed?.Invoke(this, EventArgs.Empty);
            return await RequireSubscriptionAsync(subscription.Id, cancellationToken);
        }
        finally
        {
            operationLock.Release();
        }
    }

    public async Task UpdateOptionsAsync(Guid subscriptionId, bool autoUpdate, bool stayOnCurrent, int? skippedVersion,
        CancellationToken cancellationToken = default)
    {
        await repository.UpdateSubscriptionOptionsAsync(subscriptionId, autoUpdate, stayOnCurrent, skippedVersion, cancellationToken);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>取消订阅：删除受管文件夹、条目、快照与已落盘的受管文件；用户自己的配置不受影响。</summary>
    public async Task UnsubscribeAsync(Guid subscriptionId, CancellationToken cancellationToken = default)
    {
        await operationLock.WaitAsync(cancellationToken);
        try
        {
            var subscription = await RequireSubscriptionAsync(subscriptionId, cancellationToken);
            var removed = await repository.DeleteSubscriptionAsync(subscriptionId, cancellationToken);
            foreach (var path in removed) TryDeleteFile(packages.ResolvePath(path));
            var key = SubscriptionKey(subscription.PeerId, subscription.PublishedId);
            TryDeleteDirectory(packages.ResolvePath($"imports/sync/snap/{key}"));
            CleanOldMaterialized(key, null);
            logger.LogInformation("已取消订阅 {SubscriptionId}", subscriptionId);
            Changed?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            operationLock.Release();
        }
    }

    /// <summary>“复制到我的配置”：把受管文件夹整体复制成一个用户自己的可编辑文件夹。</summary>
    public async Task<CollectionFolder> CopyFolderToMineAsync(Guid workspaceId, Guid managedFolderId,
        CancellationToken cancellationToken = default)
    {
        var folders = await collections.GetFoldersAsync(workspaceId, cancellationToken);
        var source = folders.FirstOrDefault(folder => folder.Id == managedFolderId && folder.ManagedSubscriptionId is not null)
            ?? throw new ConfigSyncException(ConfigSyncError.Local, "只能复制订阅得到的文件夹。");
        var names = folders.Select(folder => folder.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var name = $"{source.Name}（副本）";
        for (var index = 2; names.Contains(name); index++) name = $"{source.Name}（副本 {index}）";
        var target = await collections.CreateFolderAsync(workspaceId, name, cancellationToken);
        foreach (var item in await collections.GetItemsAsync(workspaceId, managedFolderId, cancellationToken: cancellationToken))
            await CopyItemAsync(workspaceId, target.Id, item, cancellationToken);
        Changed?.Invoke(this, EventArgs.Empty);
        return target;
    }

    /// <summary>复制单个受管条目到“未分类”。</summary>
    public async Task<CollectionItem> CopyItemToMineAsync(Guid workspaceId, CollectionItem item, CancellationToken cancellationToken = default)
    {
        var uncategorized = await collections.EnsureUncategorizedAsync(workspaceId, cancellationToken);
        var copy = await CopyItemAsync(workspaceId, uncategorized.Id, item, cancellationToken);
        Changed?.Invoke(this, EventArgs.Empty);
        return copy;
    }

    private async Task<CollectionItem> CopyItemAsync(Guid workspaceId, Guid folderId, CollectionItem item, CancellationToken cancellationToken)
    {
        switch (item.ItemType)
        {
            case CollectionItemType.Text or CollectionItemType.Link:
                return await collections.CreateItemAsync(workspaceId, new CreateCollectionItem(folderId, item.ItemType, item.Title,
                    item.TextContent, item.Url, item.Description, ContentHash: item.ContentHash, ContentSize: item.ContentSize), cancellationToken);
            case CollectionItemType.Image:
            {
                if (string.IsNullOrWhiteSpace(item.LocalPath) || !File.Exists(item.LocalPath))
                    throw new FileNotFoundException("图片文件已不存在，无法复制。", item.LocalPath);
                var asset = await assetStore.ImportImageAsync(item.LocalPath, cancellationToken);
                return await collections.CreateItemAsync(workspaceId, new CreateCollectionItem(folderId, CollectionItemType.Image, item.Title,
                    Description: item.Description, LocalPath: asset.LocalPath, ThumbnailPath: asset.ThumbnailPath,
                    ContentHash: asset.ContentHash, ContentSize: asset.ByteSize), cancellationToken);
            }
            default:
            {
                var sources = string.IsNullOrWhiteSpace(item.FileManifest)
                    ? []
                    : JsonSerializer.Deserialize<string[]>(item.FileManifest) ?? [];
                if (sources.Length == 0) throw new FileNotFoundException("文件已不存在，无法复制。", item.LocalPath);
                var copies = new List<string>();
                foreach (var source in sources) copies.Add(await packages.CopyToUserStorageAsync(source, cancellationToken));
                return await collections.CreateItemAsync(workspaceId, new CreateCollectionItem(folderId, CollectionItemType.File, item.Title,
                    Description: item.Description, LocalPath: copies[0], FileManifest: JsonSerializer.Serialize(copies),
                    ContentHash: item.ContentHash, ContentSize: item.ContentSize), cancellationToken);
            }
        }
    }

    private async Task<(string Temp, ManagedPackage Package)> DownloadAndImportAsync(string address, PairedPeer peer,
        ConfigManifest manifest, string key, CancellationToken cancellationToken)
    {
        var temp = packages.NewTempPath();
        try
        {
            await client.DownloadPackageAsync(address, peer.Fingerprint, manifest.PublishedId, manifest.Version, temp, cancellationToken);
            var hash = await packages.ComputeFileHashAsync(temp, cancellationToken);
            if (!string.Equals(hash, manifest.Hash, StringComparison.OrdinalIgnoreCase))
                throw new ConfigSyncException(ConfigSyncError.HashMismatch, "下载的配置包与清单的 SHA-256 不一致，已丢弃，原内容未改动。");
            var package = await packages.ImportManagedAsync(temp, manifest.PublishedId, manifest.Version, key, hash, cancellationToken);
            return (temp, package);
        }
        catch
        {
            TryDeleteFile(temp);
            throw;
        }
    }

    private static void EnsureRiskConfirmed(ConfigManifest manifest, bool confirmed)
    {
        if (!manifest.HasRiskyFiles || confirmed) return;
        var names = manifest.Items.SelectMany(item => item.RiskyFiles).Distinct().Take(5);
        throw new ConfigSyncException(ConfigSyncError.Local,
            $"这份配置包含可执行类高风险文件（{string.Join("、", names)}），需要单独确认后才能导入。导入后不会自动打开。");
    }

    private string MoveToSnapshot(string temp, string key, int version)
    {
        var relative = $"imports/sync/snap/{key}/v{version}.zip";
        var destination = packages.ResolvePath(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.Move(temp, destination, overwrite: true);
        return relative;
    }

    private void CleanOldMaterialized(string key, string? keep)
    {
        var root = packages.ResolvePath("imports/sync/m");
        if (!Directory.Exists(root)) return;
        var keepFull = keep is null ? null : Path.GetFullPath(keep);
        foreach (var directory in Directory.EnumerateDirectories(root, key + "-v*"))
            if (keepFull is null || !string.Equals(Path.GetFullPath(directory), keepFull, StringComparison.OrdinalIgnoreCase))
                TryDeleteDirectory(directory);
    }

    private async Task<PairedPeer> RequirePeerAsync(Guid peerId, CancellationToken cancellationToken) =>
        (await repository.GetPeersAsync(PeerRole.Publisher, cancellationToken)).FirstOrDefault(peer => peer.Id == peerId)
        ?? throw new ConfigSyncException(ConfigSyncError.NotPaired, "没有找到这台发布设备的配对记录，请重新配对。");

    private async Task<ConfigSubscription> RequireSubscriptionAsync(Guid subscriptionId, CancellationToken cancellationToken) =>
        await repository.GetSubscriptionAsync(subscriptionId, cancellationToken)
        ?? throw new ConfigSyncException(ConfigSyncError.Local, "订阅已不存在。");

    private static string RequireAddress(PairedPeer peer) =>
        peer.LastAddress ?? throw new ConfigSyncException(ConfigSyncError.Local, "还不知道这台设备的地址，请手动输入 IP:端口。");

    private static string NormalizeAddress(string address)
    {
        if (!ConfigSyncPolicy.TryParseAddress(address, out var host, out var port))
            throw new ConfigSyncException(ConfigSyncError.Local, "地址格式不正确，请输入 IP:端口，例如 192.168.1.20:53318。");
        return host.Contains(':') ? $"[{host}]:{port}" : $"{host}:{port}";
    }

    public static string SubscriptionKey(Guid peerId, Guid publishedId) => $"{peerId:N}"[..8] + "-" + $"{publishedId:N}"[..8];

    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
