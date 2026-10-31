using System.Globalization;
using System.Text.Json;
using ClipboardManager.Domain.Contracts;
using ClipboardManager.Domain.Models;
using Microsoft.Data.Sqlite;

namespace ClipboardManager.Infrastructure.Data;

public sealed class SqliteConfigSyncRepository(SqliteConnectionFactory connectionFactory) : IConfigSyncRepository
{
    private const string PublishedColumns = "id,workspace_id,folder_id,name,current_version,content_hash,package_path,package_bytes,manifest_json,auto_publish,created_at,updated_at,folder_scope";
    private const string SubscriptionColumns = "id,workspace_id,peer_id,published_id,name,local_version,managed_folder_id,auto_update,stay_on_current,skipped_version,last_checked_at,latest_known_version,last_error,manifest_json,content_hash,created_at,updated_at";

    public async Task<IReadOnlyList<PublishedConfig>> GetPublishedAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {PublishedColumns} FROM published_config ORDER BY created_at;";
        var result = new List<PublishedConfig>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) result.Add(ReadPublished(reader));
        return result;
    }

    public async Task<PublishedConfig?> GetPublishedAsync(Guid publishedId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {PublishedColumns} FROM published_config WHERE id=$id;";
        command.Parameters.AddWithValue("$id", publishedId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadPublished(reader) : null;
    }

    public async Task SavePublishedAsync(PublishedConfig config, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            INSERT INTO published_config ({PublishedColumns})
            VALUES ($id,$workspace,$folder,$name,$version,$hash,$path,$bytes,$manifest,$auto,$created,$updated,$scope)
            ON CONFLICT(id) DO UPDATE SET name=excluded.name,current_version=excluded.current_version,
                content_hash=excluded.content_hash,package_path=excluded.package_path,package_bytes=excluded.package_bytes,
                manifest_json=excluded.manifest_json,updated_at=excluded.updated_at;
            """;
        command.Parameters.AddWithValue("$id", config.Id.ToString("D"));
        command.Parameters.AddWithValue("$workspace", config.WorkspaceId.ToString("D"));
        // folder_id 是 0.9.0 初版的单文件夹列（NOT NULL），保留为“首个文件夹”；真实范围写在 folder_scope。
        command.Parameters.AddWithValue("$folder", (config.AllFolders ? Guid.Empty : config.FolderIds.FirstOrDefault()).ToString("D"));
        command.Parameters.AddWithValue("$scope", config.AllFolders
            ? "all"
            : JsonSerializer.Serialize(config.FolderIds.Select(id => id.ToString("D")).ToList()));
        command.Parameters.AddWithValue("$name", config.Name);
        command.Parameters.AddWithValue("$version", config.CurrentVersion);
        command.Parameters.AddWithValue("$hash", config.ContentHash);
        command.Parameters.AddWithValue("$path", config.PackagePath);
        command.Parameters.AddWithValue("$bytes", config.PackageBytes);
        command.Parameters.AddWithValue("$manifest", config.ManifestJson);
        command.Parameters.AddWithValue("$auto", config.AutoPublish ? 1 : 0);
        command.Parameters.AddWithValue("$created", Format(config.CreatedAt));
        command.Parameters.AddWithValue("$updated", Format(config.UpdatedAt));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task SetAutoPublishAsync(Guid publishedId, bool enabled, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await ExecuteAsync(connection, null, "UPDATE published_config SET auto_publish=$auto WHERE id=$id;", cancellationToken,
            ("$auto", enabled ? 1 : 0), ("$id", publishedId.ToString("D")));
    }

    public async Task<string?> DeletePublishedAsync(Guid publishedId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();
        await using var find = connection.CreateCommand();
        find.Transaction = transaction;
        find.CommandText = "SELECT package_path FROM published_config WHERE id=$id;";
        find.Parameters.AddWithValue("$id", publishedId.ToString("D"));
        var path = await find.ExecuteScalarAsync(cancellationToken) as string;
        await ExecuteAsync(connection, transaction, "DELETE FROM published_config WHERE id=$id;", cancellationToken,
            ("$id", publishedId.ToString("D")));
        await transaction.CommitAsync(cancellationToken);
        return path;
    }

    public async Task<IReadOnlyList<PairedPeer>> GetPeersAsync(PeerRole role, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id,role,name,fingerprint,last_address,paired_at,status FROM paired_peer WHERE role=$role AND status=0 ORDER BY paired_at;";
        command.Parameters.AddWithValue("$role", (int)role);
        var result = new List<PairedPeer>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) result.Add(ReadPeer(reader));
        return result;
    }

    public async Task<PairedPeer?> FindActivePeerAsync(PeerRole role, string fingerprint, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id,role,name,fingerprint,last_address,paired_at,status FROM paired_peer WHERE role=$role AND fingerprint=$fp AND status=0;";
        command.Parameters.AddWithValue("$role", (int)role);
        command.Parameters.AddWithValue("$fp", fingerprint.ToUpperInvariant());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadPeer(reader) : null;
    }

    public async Task SavePeerAsync(PairedPeer peer, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();
        // 同一指纹重新配对时替换旧记录（保留订阅关系需要稳定的 peer id，因此沿用原 id）。
        await using var find = connection.CreateCommand();
        find.Transaction = transaction;
        find.CommandText = "SELECT id FROM paired_peer WHERE role=$role AND fingerprint=$fp AND status=0;";
        find.Parameters.AddWithValue("$role", (int)peer.Role);
        find.Parameters.AddWithValue("$fp", peer.Fingerprint.ToUpperInvariant());
        if (await find.ExecuteScalarAsync(cancellationToken) is string existing)
        {
            await ExecuteAsync(connection, transaction, "UPDATE paired_peer SET name=$name,last_address=COALESCE($address,last_address) WHERE id=$id;",
                cancellationToken, ("$name", peer.Name), ("$address", peer.LastAddress), ("$id", existing));
        }
        else
        {
            await ExecuteAsync(connection, transaction,
                "INSERT INTO paired_peer(id,role,name,fingerprint,last_address,paired_at,status) VALUES($id,$role,$name,$fp,$address,$paired,0);",
                cancellationToken, ("$id", peer.Id.ToString("D")), ("$role", (int)peer.Role), ("$name", peer.Name),
                ("$fp", peer.Fingerprint.ToUpperInvariant()), ("$address", peer.LastAddress), ("$paired", Format(peer.PairedAt)));
        }
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task UpdatePeerAddressAsync(Guid peerId, string address, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await ExecuteAsync(connection, null, "UPDATE paired_peer SET last_address=$address WHERE id=$id;", cancellationToken,
            ("$address", address), ("$id", peerId.ToString("D")));
    }

    public async Task RevokePeerAsync(Guid peerId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await ExecuteAsync(connection, null, "UPDATE paired_peer SET status=1 WHERE id=$id;", cancellationToken,
            ("$id", peerId.ToString("D")));
    }

    public async Task<IReadOnlyList<ConfigSubscription>> GetSubscriptionsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {SubscriptionColumns} FROM subscription ORDER BY created_at;";
        var result = new List<ConfigSubscription>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) result.Add(ReadSubscription(reader));
        return result;
    }

    public async Task<ConfigSubscription?> GetSubscriptionAsync(Guid subscriptionId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        return await ReadSubscriptionAsync(connection, null, subscriptionId, cancellationToken);
    }

    public async Task UpdateSubscriptionOptionsAsync(Guid subscriptionId, bool autoUpdate, bool stayOnCurrent, int? skippedVersion,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await ExecuteAsync(connection, null,
            "UPDATE subscription SET auto_update=$auto,stay_on_current=$stay,skipped_version=$skipped,updated_at=$now WHERE id=$id;",
            cancellationToken, ("$auto", autoUpdate ? 1 : 0), ("$stay", stayOnCurrent ? 1 : 0), ("$skipped", skippedVersion),
            ("$now", Format(DateTimeOffset.UtcNow)), ("$id", subscriptionId.ToString("D")));
    }

    public async Task RecordCheckAsync(Guid subscriptionId, DateTimeOffset checkedAt, int? latestKnownVersion, string? lastError,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await ExecuteAsync(connection, null,
            "UPDATE subscription SET last_checked_at=$checked,latest_known_version=COALESCE($latest,latest_known_version),last_error=$error WHERE id=$id;",
            cancellationToken, ("$checked", Format(checkedAt)), ("$latest", latestKnownVersion), ("$error", lastError),
            ("$id", subscriptionId.ToString("D")));
    }

    public async Task<IReadOnlyList<ConfigSnapshot>> GetSnapshotsAsync(Guid subscriptionId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        return await ReadSnapshotsAsync(connection, null, subscriptionId, cancellationToken);
    }

    public async Task<ConfigSubscription> CreateSubscriptionAsync(Guid workspaceId, PairedPeer peer, ManagedPackage package,
        string snapshotPath, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();
        var now = DateTimeOffset.UtcNow;
        var subscriptionId = Guid.NewGuid();
        var folderMap = new Dictionary<Guid, Guid>();
        foreach (var draft in package.Folders.OrderBy(folder => folder.Order))
        {
            folderMap[draft.RemoteId] = await InsertManagedFolderAsync(connection, transaction, workspaceId, subscriptionId, draft, now, cancellationToken);
        }
        if (folderMap.Count == 0) throw new InvalidOperationException("配置包不包含任何文件夹。");
        var folderId = folderMap[package.Folders.OrderBy(folder => folder.Order).First().RemoteId];

        await InsertManagedItemsAsync(connection, transaction, workspaceId, folderMap, package.Items, now, cancellationToken);
        var manifestJson = ConfigSyncPolicy.SerializeManifest(package.Manifest);
        await ExecuteAsync(connection, transaction,
            $"""
            INSERT INTO subscription({SubscriptionColumns})
            VALUES($id,$workspace,$peer,$published,$name,$version,$folder,0,0,NULL,$now,$version,NULL,$manifest,$hash,$now,$now);
            """,
            cancellationToken, ("$id", subscriptionId.ToString("D")), ("$workspace", workspaceId.ToString("D")),
            ("$peer", peer.Id.ToString("D")), ("$published", package.Manifest.PublishedId.ToString("D")),
            ("$name", package.Manifest.Name), ("$version", package.Manifest.Version), ("$folder", folderId.ToString("D")),
            ("$now", Format(now)), ("$manifest", manifestJson), ("$hash", package.Manifest.Hash));
        await ExecuteAsync(connection, transaction,
            "INSERT INTO config_snapshot(subscription_id,version,snapshot_path,created_at) VALUES($id,$version,$path,$now);",
            cancellationToken, ("$id", subscriptionId.ToString("D")), ("$version", package.Manifest.Version),
            ("$path", snapshotPath), ("$now", Format(now)));
        var created = await ReadSubscriptionAsync(connection, transaction, subscriptionId, cancellationToken)
            ?? throw new InvalidOperationException("订阅创建后无法读取。");
        await transaction.CommitAsync(cancellationToken);
        return created;
    }

    public async Task<IReadOnlyList<string>> ReplaceManagedContentAsync(Guid subscriptionId, ManagedPackage package, string snapshotPath,
        bool isRollback, int? skippedVersion, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();
        var subscription = await ReadSubscriptionAsync(connection, transaction, subscriptionId, cancellationToken)
            ?? throw new InvalidOperationException("订阅不存在。");
        var now = DateTimeOffset.UtcNow;

        // 受管内容整体替换：同一事务内对齐文件夹、删旧条目、写新条目、更新订阅，失败即整体回滚，原版本保持不变。
        var folderMap = await ReconcileManagedFoldersAsync(connection, transaction, subscription, package, now, cancellationToken);
        var primaryFolder = folderMap[package.Folders.OrderBy(folder => folder.Order).First().RemoteId];
        foreach (var folder in folderMap.Values)
            await ExecuteAsync(connection, transaction, "DELETE FROM collection_items WHERE workspace_id=$workspace AND folder_id=$folder;",
                cancellationToken, ("$workspace", subscription.WorkspaceId.ToString("D")), ("$folder", folder.ToString("D")));
        await InsertManagedItemsAsync(connection, transaction, subscription.WorkspaceId, folderMap, package.Items, now, cancellationToken);
        await ExecuteAsync(connection, transaction,
            """
            UPDATE subscription SET local_version=$version,manifest_json=$manifest,content_hash=$hash,skipped_version=$skipped,
                last_error=NULL,name=$name,updated_at=$now,managed_folder_id=$primary,
                latest_known_version=MAX(COALESCE(latest_known_version,0),$version)
            WHERE id=$id;
            """,
            cancellationToken, ("$version", package.Manifest.Version), ("$manifest", ConfigSyncPolicy.SerializeManifest(package.Manifest)),
            ("$hash", package.Manifest.Hash), ("$skipped", skippedVersion), ("$primary", primaryFolder.ToString("D")),
            ("$name", package.Manifest.Name), ("$now", Format(now)), ("$id", subscriptionId.ToString("D")));

        var removed = new List<string>();
        foreach (var snapshot in await ReadSnapshotsAsync(connection, transaction, subscriptionId, cancellationToken))
        {
            // 回退会丢弃比目标版本新的快照；其路径可能与目标版本相同，调用方清理时需排除。
            if (isRollback && snapshot.Version > package.Manifest.Version && snapshot.Version != package.Manifest.Version)
                removed.Add(snapshot.SnapshotPath);
        }
        if (isRollback)
            await ExecuteAsync(connection, transaction, "DELETE FROM config_snapshot WHERE subscription_id=$id AND version>$version;",
                cancellationToken, ("$id", subscriptionId.ToString("D")), ("$version", package.Manifest.Version));
        await ExecuteAsync(connection, transaction,
            """
            INSERT INTO config_snapshot(subscription_id,version,snapshot_path,created_at) VALUES($id,$version,$path,$now)
            ON CONFLICT(subscription_id,version) DO UPDATE SET snapshot_path=excluded.snapshot_path,created_at=excluded.created_at;
            """,
            cancellationToken, ("$id", subscriptionId.ToString("D")), ("$version", package.Manifest.Version),
            ("$path", snapshotPath), ("$now", Format(now)));

        // 只保留最近 3 个版本（含当前）。
        var all = await ReadSnapshotsAsync(connection, transaction, subscriptionId, cancellationToken);
        foreach (var stale in all.OrderByDescending(snapshot => snapshot.Version).Skip(ConfigSyncPolicy.MaxSnapshots))
        {
            removed.Add(stale.SnapshotPath);
            await ExecuteAsync(connection, transaction, "DELETE FROM config_snapshot WHERE subscription_id=$id AND version=$version;",
                cancellationToken, ("$id", subscriptionId.ToString("D")), ("$version", stale.Version));
        }
        await transaction.CommitAsync(cancellationToken);
        return removed.Where(path => path != snapshotPath).Distinct().ToList();
    }

    public async Task<IReadOnlyList<string>> DeleteSubscriptionAsync(Guid subscriptionId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();
        var subscription = await ReadSubscriptionAsync(connection, transaction, subscriptionId, cancellationToken)
            ?? throw new InvalidOperationException("订阅不存在。");
        var leftovers = new List<string>();
        leftovers.AddRange((await ReadSnapshotsAsync(connection, transaction, subscriptionId, cancellationToken)).Select(snapshot => snapshot.SnapshotPath));
        await ExecuteAsync(connection, transaction,
            "DELETE FROM collection_items WHERE workspace_id=$workspace AND folder_id IN (SELECT id FROM collection_folders WHERE workspace_id=$workspace AND managed_subscription_id=$id);",
            cancellationToken, ("$workspace", subscription.WorkspaceId.ToString("D")), ("$id", subscriptionId.ToString("D")));
        await ExecuteAsync(connection, transaction, "DELETE FROM collection_folders WHERE workspace_id=$workspace AND managed_subscription_id=$id;",
            cancellationToken, ("$workspace", subscription.WorkspaceId.ToString("D")), ("$id", subscriptionId.ToString("D")));
        await ExecuteAsync(connection, transaction, "DELETE FROM config_snapshot WHERE subscription_id=$id;", cancellationToken,
            ("$id", subscriptionId.ToString("D")));
        await ExecuteAsync(connection, transaction, "DELETE FROM subscription WHERE id=$id;", cancellationToken,
            ("$id", subscriptionId.ToString("D")));
        await transaction.CommitAsync(cancellationToken);
        return leftovers.Where(path => !string.IsNullOrWhiteSpace(path)).ToList();
    }

    // 受管文件夹只在本机存在：不入云同步队列，也不参与个人导出。managed_remote_id 记录发布方的文件夹 id，用于更新时对齐。
    private static async Task<Guid> InsertManagedFolderAsync(SqliteConnection connection, SqliteTransaction transaction, Guid workspaceId,
        Guid subscriptionId, ManagedFolderDraft draft, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var folderId = Guid.NewGuid();
        var folderName = await UniqueFolderNameAsync(connection, transaction, workspaceId, draft.Name, null, cancellationToken);
        var sortKey = await NextFolderSortKeyAsync(connection, transaction, workspaceId, cancellationToken);
        await ExecuteAsync(connection, transaction,
            """
            INSERT INTO collection_folders(id,workspace_id,parent_folder_id,name,sort_key,is_system,created_at,updated_at,deleted_at,local_version,server_version,sync_status,managed_subscription_id,managed_remote_id)
            VALUES($id,$workspace,NULL,$name,$sort,0,$now,$now,NULL,1,NULL,0,$managed,$remote);
            """,
            cancellationToken, ("$id", folderId.ToString("D")), ("$workspace", workspaceId.ToString("D")), ("$name", folderName),
            ("$sort", sortKey), ("$now", Format(now)), ("$managed", subscriptionId.ToString("D")), ("$remote", draft.RemoteId.ToString("D")));
        return folderId;
    }

    private static async Task<double> NextFolderSortKeyAsync(SqliteConnection connection, SqliteTransaction transaction, Guid workspaceId,
        CancellationToken cancellationToken)
    {
        await using var last = connection.CreateCommand();
        last.Transaction = transaction;
        last.CommandText = "SELECT COALESCE(MAX(sort_key),0) FROM collection_folders WHERE workspace_id=$workspace AND deleted_at IS NULL;";
        last.Parameters.AddWithValue("$workspace", workspaceId.ToString("D"));
        return Convert.ToDouble(await last.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) + 1024d;
    }

    /// <summary>按发布方文件夹 id 对齐本机受管文件夹：复用、改名、新增，发布方已删除的整个移除。返回 远端 id → 本机 id。</summary>
    private static async Task<Dictionary<Guid, Guid>> ReconcileManagedFoldersAsync(SqliteConnection connection, SqliteTransaction transaction,
        ConfigSubscription subscription, ManagedPackage package, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var existing = new List<(Guid Id, Guid? Remote)>();
        await using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText = "SELECT id,managed_remote_id FROM collection_folders WHERE workspace_id=$workspace AND managed_subscription_id=$id ORDER BY sort_key;";
            read.Parameters.AddWithValue("$workspace", subscription.WorkspaceId.ToString("D"));
            read.Parameters.AddWithValue("$id", subscription.Id.ToString("D"));
            await using var reader = await read.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                existing.Add((Guid.Parse(reader.GetString(0)), reader.IsDBNull(1) ? null : Guid.Parse(reader.GetString(1))));
        }

        var map = new Dictionary<Guid, Guid>();
        var used = new HashSet<Guid>();
        foreach (var draft in package.Folders.OrderBy(folder => folder.Order))
        {
            // 0.9.0 初版建立的订阅没有 remote id：把那个唯一的旧文件夹认领给包里的第一个文件夹。
            Guid? matchId = null;
            foreach (var candidate in existing)
            {
                if (!used.Contains(candidate.Id) && candidate.Remote == draft.RemoteId) { matchId = candidate.Id; break; }
            }
            if (matchId is null && map.Count == 0)
            {
                foreach (var candidate in existing)
                {
                    if (!used.Contains(candidate.Id) && candidate.Remote is null && candidate.Id == subscription.ManagedFolderId)
                    {
                        matchId = candidate.Id;
                        break;
                    }
                }
            }
            if (matchId is { } found)
            {
                used.Add(found);
                var name = await UniqueFolderNameAsync(connection, transaction, subscription.WorkspaceId, draft.Name, found, cancellationToken);
                await ExecuteAsync(connection, transaction,
                    "UPDATE collection_folders SET name=$name,managed_remote_id=$remote,updated_at=$now WHERE workspace_id=$workspace AND id=$id;",
                    cancellationToken, ("$name", name), ("$remote", draft.RemoteId.ToString("D")), ("$now", Format(now)),
                    ("$workspace", subscription.WorkspaceId.ToString("D")), ("$id", found.ToString("D")));
                map[draft.RemoteId] = found;
            }
            else
            {
                map[draft.RemoteId] = await InsertManagedFolderAsync(connection, transaction, subscription.WorkspaceId, subscription.Id, draft, now, cancellationToken);
            }
        }

        foreach (var stale in existing.Where(candidate => !used.Contains(candidate.Id)))
        {
            await ExecuteAsync(connection, transaction, "DELETE FROM collection_items WHERE workspace_id=$workspace AND folder_id=$id;",
                cancellationToken, ("$workspace", subscription.WorkspaceId.ToString("D")), ("$id", stale.Id.ToString("D")));
            await ExecuteAsync(connection, transaction, "DELETE FROM collection_folders WHERE workspace_id=$workspace AND id=$id AND managed_subscription_id=$sub;",
                cancellationToken, ("$workspace", subscription.WorkspaceId.ToString("D")), ("$id", stale.Id.ToString("D")),
                ("$sub", subscription.Id.ToString("D")));
        }
        return map;
    }

    private async Task InsertManagedItemsAsync(SqliteConnection connection, SqliteTransaction transaction, Guid workspaceId,
        IReadOnlyDictionary<Guid, Guid> folderMap, IReadOnlyList<ManagedItemDraft> items, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var order = 0;
        var fallback = folderMap.Values.First();
        foreach (var item in items.OrderBy(item => item.Order))
        {
            order++;
            var folderId = folderMap.TryGetValue(item.RemoteFolderId, out var mapped) ? mapped : fallback;
            await ExecuteAsync(connection, transaction,
                """
                INSERT INTO collection_items(id,workspace_id,folder_id,source_clipboard_item_id,item_type,title,text_content,url,description,local_path,thumbnail_path,file_manifest,content_hash,content_size,sort_key,created_at,updated_at,deleted_at,local_version,server_version,sync_status)
                VALUES($id,$workspace,$folder,NULL,$type,$title,$text,$url,$description,$path,$thumb,$manifest,$hash,$size,$sort,$now,$now,NULL,1,NULL,0);
                """,
                cancellationToken, ("$id", Guid.NewGuid().ToString("D")), ("$workspace", workspaceId.ToString("D")),
                ("$folder", folderId.ToString("D")), ("$type", (int)item.ItemType), ("$title", item.Title), ("$text", item.TextContent),
                ("$url", item.Url), ("$description", item.Description), ("$path", item.LocalPath), ("$thumb", item.ThumbnailPath),
                ("$manifest", item.FileManifest), ("$hash", item.ContentHash), ("$size", item.ContentSize),
                ("$sort", order * 1024d), ("$now", Format(now)));
        }
    }

    private static async Task<string> UniqueFolderNameAsync(SqliteConnection connection, SqliteTransaction transaction,
        Guid workspaceId, string name, Guid? excludeFolderId, CancellationToken cancellationToken)
    {
        var baseName = name.Trim();
        if (baseName.Length == 0) baseName = "订阅配置";
        if (baseName.Length > ConfigSyncPolicy.MaxFolderNameLength - 8) baseName = baseName[..(ConfigSyncPolicy.MaxFolderNameLength - 8)];
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var candidate = attempt == 0 ? baseName : attempt == 1 ? baseName + "（订阅）" : $"{baseName}（订阅 {attempt}）";
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "SELECT 1 FROM collection_folders WHERE workspace_id=$workspace AND parent_folder_id IS NULL AND name=$name COLLATE NOCASE AND deleted_at IS NULL AND ($exclude IS NULL OR id<>$exclude);";
            command.Parameters.AddWithValue("$workspace", workspaceId.ToString("D"));
            command.Parameters.AddWithValue("$name", candidate);
            command.Parameters.AddWithValue("$exclude", excludeFolderId?.ToString("D") ?? (object)DBNull.Value);
            if (await command.ExecuteScalarAsync(cancellationToken) is null) return candidate;
        }
        throw new InvalidOperationException("无法为订阅生成不重名的文件夹名称。");
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = connectionFactory.Create();
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private static async Task<ConfigSubscription?> ReadSubscriptionAsync(SqliteConnection connection, SqliteTransaction? transaction,
        Guid id, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT {SubscriptionColumns} FROM subscription WHERE id=$id;";
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadSubscription(reader) : null;
    }

    private static async Task<IReadOnlyList<ConfigSnapshot>> ReadSnapshotsAsync(SqliteConnection connection, SqliteTransaction? transaction,
        Guid subscriptionId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT subscription_id,version,snapshot_path,created_at FROM config_snapshot WHERE subscription_id=$id ORDER BY version DESC;";
        command.Parameters.AddWithValue("$id", subscriptionId.ToString("D"));
        var result = new List<ConfigSnapshot>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            result.Add(new ConfigSnapshot(Guid.Parse(reader.GetString(0)), reader.GetInt32(1), reader.GetString(2), Parse(reader.GetString(3))));
        return result;
    }

    private static async Task ExecuteAsync(SqliteConnection connection, SqliteTransaction? transaction, string sql,
        CancellationToken cancellationToken, params (string Name, object? Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static PublishedConfig ReadPublished(SqliteDataReader r)
    {
        // folder_scope：NULL = 0.9.0 初版的单文件夹发布（用 folder_id）；"all" = 全部个人配置；否则是文件夹 id 的 JSON 数组。
        var scope = r.IsDBNull(12) ? null : r.GetString(12);
        var all = scope == "all";
        IReadOnlyList<Guid> folders;
        if (all) folders = [];
        else if (string.IsNullOrWhiteSpace(scope)) folders = [Guid.Parse(r.GetString(2))];
        else folders = (JsonSerializer.Deserialize<List<string>>(scope) ?? []).Select(Guid.Parse).ToList();
        return new PublishedConfig(Guid.Parse(r.GetString(0)), Guid.Parse(r.GetString(1)), folders, all, r.GetString(3), r.GetInt32(4),
            r.GetString(5), r.GetString(6), r.GetInt64(7), r.GetString(8), r.GetInt32(9) != 0, Parse(r.GetString(10)), Parse(r.GetString(11)));
    }

    private static PairedPeer ReadPeer(SqliteDataReader r) => new(
        Guid.Parse(r.GetString(0)), (PeerRole)r.GetInt32(1), r.GetString(2), r.GetString(3),
        r.IsDBNull(4) ? null : r.GetString(4), Parse(r.GetString(5)), (PeerStatus)r.GetInt32(6));

    private static ConfigSubscription ReadSubscription(SqliteDataReader r) => new(
        Guid.Parse(r.GetString(0)), Guid.Parse(r.GetString(1)), Guid.Parse(r.GetString(2)), Guid.Parse(r.GetString(3)), r.GetString(4),
        r.GetInt32(5), Guid.Parse(r.GetString(6)), r.GetInt32(7) != 0, r.GetInt32(8) != 0,
        r.IsDBNull(9) ? null : r.GetInt32(9), r.IsDBNull(10) ? null : Parse(r.GetString(10)),
        r.IsDBNull(11) ? null : r.GetInt32(11), r.IsDBNull(12) ? null : r.GetString(12), r.GetString(13), r.GetString(14),
        Parse(r.GetString(15)), Parse(r.GetString(16)));

    private static string Format(DateTimeOffset value) => value.ToString("O", CultureInfo.InvariantCulture);
    private static DateTimeOffset Parse(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
