using ClipboardManager.Domain.Models;
using ClipboardManager.Infrastructure.Data;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClipboardManager.Infrastructure.Tests;

/// <summary>一次发布多个文件夹：默认全部个人配置、可多选；订阅方得到同样数量的只读受管文件夹并整体更新。</summary>
public sealed class ConfigSyncMultiFolderTests : IAsyncLifetime
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
    public async Task DefaultPublish_TakesEveryPersonalFolderAsOneVersion_AndSkipsEmptyOnes()
    {
        var a = await _publisher.CreateFolderAsync("甲");
        var b = await _publisher.CreateFolderAsync("乙");
        await _publisher.CreateFolderAsync("空");
        await _publisher.AddTextAsync(a.Id, "a1", "1");
        await _publisher.AddTextAsync(a.Id, "a2", "2");
        await _publisher.AddLinkAsync(b.Id, "b1", "https://example.com/b");

        var v1 = await _publisher.Publishing.PublishAsync(_publisher.Workspace, (IReadOnlyList<Guid>?)null);

        Assert.True(v1.Changed);
        Assert.True(v1.Config.AllFolders);
        Assert.Equal(1, v1.Config.CurrentVersion);
        var manifest = ConfigSyncPolicy.ParseManifest(v1.Config.ManifestJson);
        Assert.Equal(new[] { "乙", "甲" }.Order(StringComparer.Ordinal), manifest.FolderList.Select(folder => folder.Name).Order(StringComparer.Ordinal));
        Assert.Equal(3, manifest.Items.Count);
        Assert.Equal(2, manifest.FolderList.Single(folder => folder.Name == "甲").ItemCount);

        // 内容没变：不产生新版本，也不会另起一份发布。
        var unchanged = await _publisher.Publishing.PublishAsync(_publisher.Workspace, (IReadOnlyList<Guid>?)null);
        Assert.False(unchanged.Changed);
        Assert.Single(await _publisher.Publishing.GetPublishedAsync());

        // 之后新建的文件夹自动并入“全部”。
        var c = await _publisher.CreateFolderAsync("丙");
        await _publisher.AddTextAsync(c.Id, "c1", "3");
        var v2 = await _publisher.Publishing.PublishAsync(_publisher.Workspace, (IReadOnlyList<Guid>?)null);
        Assert.Equal(2, v2.Config.CurrentVersion);
        Assert.Equal(3, ConfigSyncPolicy.ParseManifest(v2.Config.ManifestJson).FolderList.Count);
    }

    [Fact]
    public async Task SelectedPublish_IsItsOwnPublication_AndSelectionOrderDoesNotMatter()
    {
        var a = await _publisher.CreateFolderAsync("甲");
        var b = await _publisher.CreateFolderAsync("乙");
        var c = await _publisher.CreateFolderAsync("丙");
        foreach (var folder in new[] { a, b, c }) await _publisher.AddTextAsync(folder.Id, folder.Name, "x");

        var all = await _publisher.Publishing.PublishAsync(_publisher.Workspace, (IReadOnlyList<Guid>?)null);
        var some = await _publisher.Publishing.PublishAsync(_publisher.Workspace, [a.Id, b.Id]);
        var sameSome = await _publisher.Publishing.PublishAsync(_publisher.Workspace, [b.Id, a.Id]);

        Assert.NotEqual(all.Config.Id, some.Config.Id);
        Assert.False(some.Config.AllFolders);
        Assert.Equal(2, ConfigSyncPolicy.ParseManifest(some.Config.ManifestJson).FolderList.Count);
        Assert.Equal(some.Config.Id, sameSome.Config.Id);
        Assert.False(sameSome.Changed);
        Assert.Equal(2, (await _publisher.Publishing.GetPublishedAsync()).Count);
    }

    [Fact]
    public async Task ManagedFolders_AreNeverPublishedAgain()
    {
        var folder = await _publisher.CreateFolderAsync("甲");
        await _publisher.AddTextAsync(folder.Id, "t", "x");
        var peer = await PairAsync();
        await _publisher.Publishing.PublishAsync(_publisher.Workspace, (IReadOnlyList<Guid>?)null);
        var id = Assert.Single(await _subscriber.Subscriptions.ListPublicationsAsync(peer.Id)).PublishedId;
        var subscription = await _subscriber.Subscriptions.SubscribeAsync(_subscriber.Workspace, peer.Id, id, false);
        var managed = (await _subscriber.Collections.GetFoldersAsync(_subscriber.Workspace)).Single(f => f.ManagedSubscriptionId == subscription.Id);

        await Assert.ThrowsAsync<ConfigSyncException>(() => _subscriber.Publishing.PublishAsync(_subscriber.Workspace, [managed.Id]));
        // “全部”只包含个人文件夹：订阅方除受管文件夹外没有内容，因此没有可发布的内容。
        await Assert.ThrowsAsync<ConfigSyncException>(() => _subscriber.Publishing.PublishAsync(_subscriber.Workspace, (IReadOnlyList<Guid>?)null));
    }

    [Fact]
    public async Task Subscribe_UpdateReconcileRollbackAndUnsubscribe_WorkOnAllFolders()
    {
        var peer = await PairAsync();
        var a = await _publisher.CreateFolderAsync("甲");
        var b = await _publisher.CreateFolderAsync("乙");
        var aItem = await _publisher.AddTextAsync(a.Id, "a1", "a-v1");
        await _publisher.AddTextAsync(b.Id, "b1", "b-v1");
        await _publisher.AddLinkAsync(b.Id, "b2", "https://example.com/b2");
        await _publisher.Publishing.PublishAsync(_publisher.Workspace, (IReadOnlyList<Guid>?)null);

        // 订阅 v1：一次得到两个只读受管文件夹。
        var id = Assert.Single(await _subscriber.Subscriptions.ListPublicationsAsync(peer.Id)).PublishedId;
        var subscription = await _subscriber.Subscriptions.SubscribeAsync(_subscriber.Workspace, peer.Id, id, false);
        var managedFolders = (await _subscriber.Collections.GetFoldersAsync(_subscriber.Workspace))
            .Where(folder => folder.ManagedSubscriptionId == subscription.Id).ToList();
        Assert.Equal(new[] { "乙", "甲" }.Order(StringComparer.Ordinal), managedFolders.Select(folder => folder.Name).Order(StringComparer.Ordinal));
        var managedA = managedFolders.Single(folder => folder.Name == "甲");
        var managedB = managedFolders.Single(folder => folder.Name == "乙");
        Assert.Equal("a-v1", Assert.Single(await _subscriber.Collections.GetItemsAsync(_subscriber.Workspace, managedA.Id)).TextContent);
        Assert.Equal(2, (await _subscriber.Collections.GetItemsAsync(_subscriber.Workspace, managedB.Id)).Count);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _subscriber.AddTextAsync(managedB.Id, "x", "y"));

        // v2：甲被清空（不再出现在包里）、乙改名、新增丙。
        await _publisher.Collections.DeleteItemAsync(_publisher.Workspace, aItem.Id);
        await _publisher.Collections.RenameFolderAsync(_publisher.Workspace, b.Id, "乙乙");
        var c = await _publisher.CreateFolderAsync("丙");
        await _publisher.AddTextAsync(c.Id, "c1", "c-v2");
        var v2 = await _publisher.Publishing.PublishAsync(_publisher.Workspace, (IReadOnlyList<Guid>?)null);
        Assert.Equal(2, v2.Config.CurrentVersion);

        var check = await _subscriber.Subscriptions.CheckAsync(subscription.Id);
        Assert.Equal(ConfigUpdateState.UpdateAvailable, check.State);
        Assert.Equal((1, 1), (check.Changes!.FoldersAdded, check.Changes.FoldersRemoved));
        await _subscriber.Subscriptions.ApplyUpdateAsync(subscription.Id, false);

        var afterFolders = (await _subscriber.Collections.GetFoldersAsync(_subscriber.Workspace))
            .Where(folder => folder.ManagedSubscriptionId == subscription.Id).ToList();
        Assert.Equal(new[] { "丙", "乙乙" }.Order(StringComparer.Ordinal), afterFolders.Select(folder => folder.Name).Order(StringComparer.Ordinal));
        // 乙沿用同一个本机文件夹（只是改名），甲整体移除。
        Assert.Equal(managedB.Id, afterFolders.Single(folder => folder.Name == "乙乙").Id);
        Assert.DoesNotContain(afterFolders, folder => folder.Id == managedA.Id);
        Assert.Equal(2, (await _subscriber.Collections.GetItemsAsync(_subscriber.Workspace, managedB.Id)).Count);
        Assert.Equal("c-v2", Assert.Single(await _subscriber.Collections.GetItemsAsync(_subscriber.Workspace,
            afterFolders.Single(folder => folder.Name == "丙").Id)).TextContent);

        // 回退到 v1：整体回到甲、乙。
        var rolled = await _subscriber.Subscriptions.RollbackAsync(subscription.Id);
        Assert.Equal(1, rolled.LocalVersion);
        var rolledFolders = (await _subscriber.Collections.GetFoldersAsync(_subscriber.Workspace))
            .Where(folder => folder.ManagedSubscriptionId == subscription.Id).ToList();
        Assert.Equal(2, rolledFolders.Count);
        Assert.Contains(rolledFolders, folder => folder.Name == "甲");
        Assert.Contains(rolledFolders, folder => folder.Name == "乙");
        Assert.Equal(managedB.Id, rolledFolders.Single(folder => folder.Name == "乙").Id);

        // 取消订阅：所有受管文件夹及其条目一并删除，本机自己的文件夹不受影响。
        var mine = await _subscriber.CreateFolderAsync("我的");
        await _subscriber.AddTextAsync(mine.Id, "keep", "keep");
        await _subscriber.Subscriptions.UnsubscribeAsync(subscription.Id);
        var remaining = await _subscriber.Collections.GetFoldersAsync(_subscriber.Workspace);
        Assert.DoesNotContain(remaining, folder => folder.ManagedSubscriptionId is not null);
        Assert.Single(await _subscriber.Collections.GetItemsAsync(_subscriber.Workspace, mine.Id));
    }

    [Fact]
    public async Task AlreadyV8Database_GainsTheNewColumns_AndLegacySinglePublicationStillReads()
    {
        // 0.9.0 初版建出的 v8 库没有这两列：模拟它，再让初始化器跑一遍。
        await using (var connection = _publisher.Factory.Create())
        {
            await connection.OpenAsync();
            await using var drop = connection.CreateCommand();
            drop.CommandText = "ALTER TABLE published_config DROP COLUMN folder_scope; ALTER TABLE collection_folders DROP COLUMN managed_remote_id;";
            await drop.ExecuteNonQueryAsync();
        }
        var initializer = new SqliteDatabaseInitializer(_publisher.Factory, NullLogger<SqliteDatabaseInitializer>.Instance);
        await initializer.InitializeAsync();
        await initializer.InitializeAsync();

        await using var check = _publisher.Factory.Create();
        await check.OpenAsync();
        await using var columns = check.CreateCommand();
        columns.CommandText = "SELECT (SELECT COUNT(*) FROM pragma_table_info('published_config') WHERE name='folder_scope')," +
                              "(SELECT COUNT(*) FROM pragma_table_info('collection_folders') WHERE name='managed_remote_id')," +
                              "(SELECT user_version FROM pragma_user_version);";
        await using var reader = await columns.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal((1L, 1L, 8L), (reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2)));
        await reader.DisposeAsync();

        // 初版写下的单文件夹发布（folder_scope 为 NULL）按“只含那个文件夹”读取。
        var folder = await _publisher.CreateFolderAsync("旧");
        await using var insert = check.CreateCommand();
        var legacyId = Guid.NewGuid();
        insert.CommandText = "INSERT INTO published_config(id,workspace_id,folder_id,name,current_version,content_hash,package_path,package_bytes,manifest_json,auto_publish,created_at,updated_at)" +
                             "VALUES($id,$ws,$folder,'旧',3,'h','p',0,'{}',0,$now,$now);";
        insert.Parameters.AddWithValue("$id", legacyId.ToString("D"));
        insert.Parameters.AddWithValue("$ws", _publisher.Workspace.ToString("D"));
        insert.Parameters.AddWithValue("$folder", folder.Id.ToString("D"));
        insert.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        await insert.ExecuteNonQueryAsync();

        var legacy = await _publisher.Repository.GetPublishedAsync(legacyId);
        Assert.NotNull(legacy);
        Assert.False(legacy!.AllFolders);
        Assert.Equal([folder.Id], legacy.FolderIds);
    }

    private async Task<PairedPeer> PairAsync()
    {
        var code = _publisher.Publishing.IssuePairingCode().Code;
        return await _subscriber.Subscriptions.PairAsync(_publisher.Address, code);
    }
}
