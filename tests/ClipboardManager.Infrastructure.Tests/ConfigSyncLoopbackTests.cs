using ClipboardManager.Domain.Models;

namespace ClipboardManager.Infrastructure.Tests;

/// <summary>
/// 两个进程内对等端经真实自签 TLS 回环完成：配对 → 发布 → 订阅 → 更新 → 回退 → 取消订阅。
/// 未覆盖：组播发现、真实防火墙、跨机器网络。
/// </summary>
public sealed class ConfigSyncLoopbackTests : IAsyncLifetime
{
    private ConfigSyncTestHost _publisher = null!;
    private ConfigSyncTestHost _subscriber = null!;

    public async Task InitializeAsync()
    {
        _publisher = await ConfigSyncTestHost.CreateAsync("publisher");
        _subscriber = await ConfigSyncTestHost.CreateAsync("subscriber");
        await _publisher.StartServerAsync();
    }

    public async Task DisposeAsync()
    {
        await _publisher.DisposeAsync();
        await _subscriber.DisposeAsync();
    }

    [Fact]
    public async Task Hello_ReportsProtocolAndFingerprintWithoutPairing()
    {
        var hello = await _subscriber.Subscriptions.HelloAsync(_publisher.Address);
        Assert.Equal(ConfigSyncPolicy.ProtocolVersion, hello.ProtocolVersion);
        Assert.Equal(_publisher.Fingerprint, hello.Fingerprint, ignoreCase: true);
    }

    [Fact]
    public async Task Pairing_WrongCodeCountsDown_ThenCorrectCodePinsBothSides()
    {
        var code = _publisher.Publishing.IssuePairingCode().Code;
        var wrong = code == "000000" ? "111111" : "000000";

        var failure = await Assert.ThrowsAsync<PairingException>(() => _subscriber.Subscriptions.PairAsync(_publisher.Address, wrong));
        Assert.Equal(ConfigSyncError.PairingCodeInvalid, failure.Error);
        Assert.Equal(4, failure.RemainingAttempts);
        Assert.Empty(await _subscriber.Repository.GetPeersAsync(PeerRole.Publisher));

        var peer = await _subscriber.Subscriptions.PairAsync(_publisher.Address, code);
        Assert.Equal(_publisher.Fingerprint, peer.Fingerprint, ignoreCase: true);
        var subscriberPeer = Assert.Single(await _publisher.Repository.GetPeersAsync(PeerRole.Subscriber));
        Assert.Equal(_subscriber.Fingerprint, subscriberPeer.Fingerprint, ignoreCase: true);

        // 码已一次性消耗：再次使用同一个码必须失败。
        var again = await Assert.ThrowsAsync<PairingException>(() => _subscriber.Subscriptions.PairAsync(_publisher.Address, code));
        Assert.Equal(ConfigSyncError.PairingCodeExpired, again.Error);
    }

    [Fact]
    public async Task Pairing_FiveWrongCodesRevokeTheCode()
    {
        var code = _publisher.Publishing.IssuePairingCode().Code;
        var wrong = code == "000000" ? "111111" : "000000";
        for (var attempt = 1; attempt <= 4; attempt++)
            await Assert.ThrowsAsync<PairingException>(() => _subscriber.Subscriptions.PairAsync(_publisher.Address, wrong));
        var last = await Assert.ThrowsAsync<PairingException>(() => _subscriber.Subscriptions.PairAsync(_publisher.Address, wrong));
        Assert.Equal(ConfigSyncError.PairingRevoked, last.Error);
        // 即使随后输入正确码也不再有效。
        await Assert.ThrowsAsync<PairingException>(() => _subscriber.Subscriptions.PairAsync(_publisher.Address, code));
        Assert.Empty(await _publisher.Repository.GetPeersAsync(PeerRole.Subscriber));
    }

    [Fact]
    public async Task Pairing_ExpiredCodeIsRefused()
    {
        var code = _publisher.Publishing.IssuePairingCode().Code;
        _publisher.Clock.Advance(TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(1));
        var error = await Assert.ThrowsAsync<PairingException>(() => _subscriber.Subscriptions.PairAsync(_publisher.Address, code));
        Assert.Equal(ConfigSyncError.PairingCodeExpired, error.Error);
    }

    [Fact]
    public async Task UnpairedDevice_CannotReadPublications()
    {
        var folder = await _publisher.CreateFolderAsync("公开");
        await _publisher.AddTextAsync(folder.Id, "t", "hello");
        await _publisher.Publishing.PublishAsync(_publisher.Workspace, folder.Id);

        var stranger = await Assert.ThrowsAsync<ConfigSyncException>(() =>
            _subscriber.Client.ListPublicationsAsync(_publisher.Address, _publisher.Fingerprint));
        Assert.Equal(ConfigSyncError.NotPaired, stranger.Error);
    }

    [Fact]
    public async Task WrongPinnedFingerprint_IsRejectedBeforeAnyData()
    {
        var peer = await PairAsync();
        var error = await Assert.ThrowsAsync<ConfigSyncException>(() =>
            _subscriber.Client.ListPublicationsAsync(_publisher.Address, new string('A', 64)));
        Assert.Equal(ConfigSyncError.PinMismatch, error.Error);
        Assert.NotNull(peer);
    }

    [Fact]
    public async Task RevokedSubscriber_IsNoLongerServed()
    {
        var peer = await PairAsync();
        var subscriber = Assert.Single(await _publisher.Publishing.GetSubscribersAsync());
        await _publisher.Publishing.RemoveSubscriberAsync(subscriber.Id);
        var error = await Assert.ThrowsAsync<ConfigSyncException>(() =>
            _subscriber.Subscriptions.ListPublicationsAsync(peer.Id));
        Assert.Equal(ConfigSyncError.NotPaired, error.Error);
    }

    [Fact]
    public async Task FullLifecycle_PublishSubscribeUpdateRollbackUnsubscribe()
    {
        var peer = await PairAsync();

        // 发布 v1
        var folder = await _publisher.CreateFolderAsync("团队常用");
        var text = await _publisher.AddTextAsync(folder.Id, "问候", "你好 v1");
        await _publisher.AddLinkAsync(folder.Id, "文档", "https://example.com/doc");
        await _publisher.AddImageAsync(folder.Id, "图", ConfigSyncTestHost.Png(1));
        await _publisher.AddFileAsync(folder.Id, "说明", "readme.txt", "readme"u8.ToArray());
        var v1 = await _publisher.Publishing.PublishAsync(_publisher.Workspace, folder.Id);
        Assert.True(v1.Changed);
        Assert.Equal(1, v1.Config.CurrentVersion);

        // 未变化不产生新版本
        var unchanged = await _publisher.Publishing.PublishAsync(_publisher.Workspace, folder.Id);
        Assert.False(unchanged.Changed);
        Assert.Equal(1, unchanged.Config.CurrentVersion);

        // 订阅 v1
        var listed = Assert.Single(await _subscriber.Subscriptions.ListPublicationsAsync(peer.Id));
        Assert.Equal(1, listed.Version);
        var subscription = await _subscriber.Subscriptions.SubscribeAsync(_subscriber.Workspace, peer.Id, listed.PublishedId, false);
        Assert.Equal(1, subscription.LocalVersion);
        var managed = (await _subscriber.Collections.GetFoldersAsync(_subscriber.Workspace))
            .Single(f => f.ManagedSubscriptionId == subscription.Id);
        var installed = await _subscriber.Collections.GetItemsAsync(_subscriber.Workspace, managed.Id);
        Assert.Equal(4, installed.Count);
        Assert.All(installed.Where(i => i.LocalPath is not null), i => Assert.True(File.Exists(i.LocalPath)));

        // 受管文件夹只读
        await Assert.ThrowsAsync<InvalidOperationException>(() => _subscriber.AddTextAsync(managed.Id, "x", "y"));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _subscriber.Collections.UpdateItemAsync(installed.First(i => i.ItemType == CollectionItemType.Text) with { TextContent = "改" }));

        // 重复订阅被拒
        await Assert.ThrowsAsync<ConfigSyncException>(() =>
            _subscriber.Subscriptions.SubscribeAsync(_subscriber.Workspace, peer.Id, listed.PublishedId, false));

        // 无新版本
        Assert.Equal(ConfigUpdateState.UpToDate, (await _subscriber.Subscriptions.CheckAsync(subscription.Id)).State);

        // 发布 v2：修改一条、删除一条、新增一条
        await _publisher.Collections.UpdateItemAsync(text with { TextContent = "你好 v2" });
        var items = await _publisher.Collections.GetItemsAsync(_publisher.Workspace, folder.Id);
        await _publisher.Collections.DeleteItemAsync(_publisher.Workspace, items.First(i => i.ItemType == CollectionItemType.Link).Id);
        await _publisher.AddTextAsync(folder.Id, "新增", "new");
        var v2 = await _publisher.Publishing.PublishAsync(_publisher.Workspace, folder.Id);
        Assert.Equal(2, v2.Config.CurrentVersion);

        var check = await _subscriber.Subscriptions.CheckAsync(subscription.Id);
        Assert.Equal(ConfigUpdateState.UpdateAvailable, check.State);
        Assert.Equal((1, 1, 1), (check.Changes!.Added, check.Changes.Modified, check.Changes.Removed));
        Assert.Equal(1, (await _subscriber.Subscriptions.GetSubscriptionsAsync()).Single().LocalVersion);

        // 更新
        var updated = await _subscriber.Subscriptions.ApplyUpdateAsync(subscription.Id, false);
        Assert.Equal(2, updated.LocalVersion);
        var after = await _subscriber.Collections.GetItemsAsync(_subscriber.Workspace, managed.Id);
        Assert.Contains(after, i => i.TextContent == "你好 v2");
        Assert.Contains(after, i => i.Title == "新增");
        Assert.DoesNotContain(after, i => i.ItemType == CollectionItemType.Link);
        Assert.Equal([1, 2], (await _subscriber.Subscriptions.GetSnapshotsAsync(subscription.Id)).Select(s => s.Version).Order());

        // 回退到 v1，并自动跳过 v2
        var rolled = await _subscriber.Subscriptions.RollbackAsync(subscription.Id);
        Assert.Equal(1, rolled.LocalVersion);
        Assert.Equal(2, rolled.SkippedVersion);
        var back = await _subscriber.Collections.GetItemsAsync(_subscriber.Workspace, managed.Id);
        Assert.Contains(back, i => i.TextContent == "你好 v1");
        Assert.Equal(ConfigUpdateState.Skipped, (await _subscriber.Subscriptions.CheckAsync(subscription.Id)).State);
        await Assert.ThrowsAsync<ConfigSyncException>(() => _subscriber.Subscriptions.RollbackAsync(subscription.Id));

        // 取消订阅：删除受管文件夹，用户其他配置不受影响
        var mine = await _subscriber.CreateFolderAsync("我的");
        await _subscriber.AddTextAsync(mine.Id, "保留", "keep");
        await _subscriber.Subscriptions.UnsubscribeAsync(subscription.Id);
        Assert.DoesNotContain(await _subscriber.Collections.GetFoldersAsync(_subscriber.Workspace), f => f.Id == managed.Id);
        Assert.Empty(await _subscriber.Subscriptions.GetSubscriptionsAsync());
        Assert.Single(await _subscriber.Collections.GetItemsAsync(_subscriber.Workspace, mine.Id));
        Assert.All(installed.Where(i => i.LocalPath is not null), i => Assert.False(File.Exists(i.LocalPath)));
    }

    [Fact]
    public async Task Snapshots_AreCappedAtThree()
    {
        var peer = await PairAsync();
        var folder = await _publisher.CreateFolderAsync("多版本");
        var item = await _publisher.AddTextAsync(folder.Id, "t", "v1");
        await _publisher.Publishing.PublishAsync(_publisher.Workspace, folder.Id);
        var id = Assert.Single(await _subscriber.Subscriptions.ListPublicationsAsync(peer.Id)).PublishedId;
        var subscription = await _subscriber.Subscriptions.SubscribeAsync(_subscriber.Workspace, peer.Id, id, false);

        for (var version = 2; version <= 5; version++)
        {
            var current = (await _publisher.Collections.GetItemsAsync(_publisher.Workspace, folder.Id)).Single(i => i.Id == item.Id);
            await _publisher.Collections.UpdateItemAsync(current with { TextContent = $"v{version}" });
            await _publisher.Publishing.PublishAsync(_publisher.Workspace, folder.Id);
            await _subscriber.Subscriptions.ApplyUpdateAsync(subscription.Id, false);
        }

        var snapshots = (await _subscriber.Subscriptions.GetSnapshotsAsync(subscription.Id)).Select(s => s.Version).Order().ToArray();
        Assert.Equal([3, 4, 5], snapshots);
        Assert.Equal(3, Directory.EnumerateFiles(_subscriber.Packages.ResolvePath("imports/sync/snap"), "*.zip", SearchOption.AllDirectories).Count());
    }

    [Fact]
    public async Task RiskyFiles_NeedExplicitConfirmation_AndAreNeverOpened()
    {
        var peer = await PairAsync();
        var folder = await _publisher.CreateFolderAsync("工具");
        await _publisher.AddFileAsync(folder.Id, "安装器", "setup.exe", [1, 2, 3, 4]);
        await _publisher.Publishing.PublishAsync(_publisher.Workspace, folder.Id);
        var id = Assert.Single(await _subscriber.Subscriptions.ListPublicationsAsync(peer.Id)).PublishedId;

        var manifest = await _subscriber.Subscriptions.GetRemoteManifestAsync(peer.Id, id);
        Assert.True(manifest.HasRiskyFiles);
        Assert.Contains("setup.exe", manifest.Items.SelectMany(i => i.RiskyFiles));

        var refused = await Assert.ThrowsAsync<ConfigSyncException>(() =>
            _subscriber.Subscriptions.SubscribeAsync(_subscriber.Workspace, peer.Id, id, false));
        Assert.Contains("高风险", refused.Message);
        Assert.Empty(await _subscriber.Subscriptions.GetSubscriptionsAsync());

        var subscription = await _subscriber.Subscriptions.SubscribeAsync(_subscriber.Workspace, peer.Id, id, true);
        Assert.Equal(1, subscription.LocalVersion);
    }

    [Fact]
    public async Task PublishedAndRemoteVersionBelowInstalled_IsRejected()
    {
        var peer = await PairAsync();
        var folder = await _publisher.CreateFolderAsync("单调");
        var item = await _publisher.AddTextAsync(folder.Id, "t", "v1");
        await _publisher.Publishing.PublishAsync(_publisher.Workspace, folder.Id);
        var id = Assert.Single(await _subscriber.Subscriptions.ListPublicationsAsync(peer.Id)).PublishedId;
        var subscription = await _subscriber.Subscriptions.SubscribeAsync(_subscriber.Workspace, peer.Id, id, false);
        await _publisher.Collections.UpdateItemAsync(item with { TextContent = "v2" });
        await _publisher.Publishing.PublishAsync(_publisher.Workspace, folder.Id);
        await _subscriber.Subscriptions.ApplyUpdateAsync(subscription.Id, false);

        // 发布方“回滚”版本号（模拟被篡改或误操作）：订阅方必须拒收。
        await using (var connection = _publisher.Factory.Create())
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE published_config SET current_version=1;";
            await command.ExecuteNonQueryAsync();
        }

        var check = await _subscriber.Subscriptions.CheckAsync(subscription.Id);
        Assert.Equal(ConfigUpdateState.UpToDate, check.State);
        var error = await Assert.ThrowsAsync<ConfigSyncException>(() => _subscriber.Subscriptions.ApplyUpdateAsync(subscription.Id, false));
        Assert.Equal(ConfigSyncError.VersionRejected, error.Error);
        Assert.Equal(2, (await _subscriber.Subscriptions.GetSubscriptionsAsync()).Single().LocalVersion);
    }

    [Fact]
    public async Task OfflinePublisher_IsReportedAndKeepsInstalledVersion()
    {
        var peer = await PairAsync();
        var folder = await _publisher.CreateFolderAsync("离线");
        await _publisher.AddTextAsync(folder.Id, "t", "v1");
        await _publisher.Publishing.PublishAsync(_publisher.Workspace, folder.Id);
        var id = Assert.Single(await _subscriber.Subscriptions.ListPublicationsAsync(peer.Id)).PublishedId;
        var subscription = await _subscriber.Subscriptions.SubscribeAsync(_subscriber.Workspace, peer.Id, id, false);

        await _publisher.Server.StopAsync();
        var check = await _subscriber.Subscriptions.CheckAsync(subscription.Id);

        Assert.Equal(ConfigUpdateState.PublisherOffline, check.State);
        var stored = (await _subscriber.Subscriptions.GetSubscriptionsAsync()).Single();
        Assert.Equal(1, stored.LocalVersion);
        Assert.False(string.IsNullOrWhiteSpace(stored.LastError));
        Assert.Single(await _subscriber.Collections.GetItemsAsync(_subscriber.Workspace, stored.ManagedFolderId));
    }

    [Fact]
    public async Task FailedUpdate_KeepsTheInstalledVersion()
    {
        var peer = await PairAsync();
        var folder = await _publisher.CreateFolderAsync("失败");
        var item = await _publisher.AddTextAsync(folder.Id, "t", "v1");
        await _publisher.Publishing.PublishAsync(_publisher.Workspace, folder.Id);
        var id = Assert.Single(await _subscriber.Subscriptions.ListPublicationsAsync(peer.Id)).PublishedId;
        var subscription = await _subscriber.Subscriptions.SubscribeAsync(_subscriber.Workspace, peer.Id, id, false);

        await _publisher.Collections.UpdateItemAsync(item with { TextContent = "v2" });
        var v2 = await _publisher.Publishing.PublishAsync(_publisher.Workspace, folder.Id);
        // 发布方的包文件被损坏：哈希或结构校验必须失败，原版本不变。
        var packagePath = _publisher.Packages.ResolvePath(v2.Config.PackagePath);
        var bytes = await File.ReadAllBytesAsync(packagePath);
        bytes[bytes.Length / 2] ^= 0xFF;
        await File.WriteAllBytesAsync(packagePath, bytes);

        var error = await Assert.ThrowsAsync<ConfigSyncException>(() => _subscriber.Subscriptions.ApplyUpdateAsync(subscription.Id, false));
        Assert.True(error.Error is ConfigSyncError.HashMismatch or ConfigSyncError.InvalidPackage or ConfigSyncError.Protocol);
        var stored = (await _subscriber.Subscriptions.GetSubscriptionsAsync()).Single();
        Assert.Equal(1, stored.LocalVersion);
        Assert.Equal("v1", Assert.Single(await _subscriber.Collections.GetItemsAsync(_subscriber.Workspace, stored.ManagedFolderId)).TextContent);
    }

    [Fact]
    public async Task OptionsPersist_AndStayOnCurrentSuppressesPrompt()
    {
        var peer = await PairAsync();
        var folder = await _publisher.CreateFolderAsync("选项");
        var item = await _publisher.AddTextAsync(folder.Id, "t", "v1");
        await _publisher.Publishing.PublishAsync(_publisher.Workspace, folder.Id);
        var id = Assert.Single(await _subscriber.Subscriptions.ListPublicationsAsync(peer.Id)).PublishedId;
        var subscription = await _subscriber.Subscriptions.SubscribeAsync(_subscriber.Workspace, peer.Id, id, false);
        await _publisher.Collections.UpdateItemAsync(item with { TextContent = "v2" });
        await _publisher.Publishing.PublishAsync(_publisher.Workspace, folder.Id);

        await _subscriber.Subscriptions.UpdateOptionsAsync(subscription.Id, true, true, null);
        var stored = (await _subscriber.Subscriptions.GetSubscriptionsAsync()).Single();
        Assert.True(stored.AutoUpdate);
        Assert.True(stored.StayOnCurrent);
        Assert.Equal(ConfigUpdateState.Stayed, (await _subscriber.Subscriptions.CheckAsync(subscription.Id)).State);

        await _subscriber.Subscriptions.UpdateOptionsAsync(subscription.Id, false, false, 2);
        Assert.Equal(ConfigUpdateState.Skipped, (await _subscriber.Subscriptions.CheckAsync(subscription.Id)).State);
        await _subscriber.Subscriptions.UpdateOptionsAsync(subscription.Id, false, false, null);
        Assert.Equal(ConfigUpdateState.UpdateAvailable, (await _subscriber.Subscriptions.CheckAsync(subscription.Id)).State);
    }

    private async Task<PairedPeer> PairAsync()
    {
        var code = _publisher.Publishing.IssuePairingCode().Code;
        return await _subscriber.Subscriptions.PairAsync(_publisher.Address, code);
    }
}
