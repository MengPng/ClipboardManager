using ClipboardManager.Domain.Models;
using ClipboardManager.Infrastructure.Data;
using ClipboardManager.Infrastructure.Paths;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClipboardManager.Infrastructure.Tests;

public sealed class SqliteCollectionRepositoryTests : IDisposable
{
    private readonly string _root=Path.Combine(Path.GetTempPath(),"ClipboardManager.Tests",Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Uncategorized_IsUniqueAndSystemProtected()
    {
        var (repository,workspace)=await CreateAsync();
        var first=await repository.EnsureUncategorizedAsync(workspace);
        var second=await repository.EnsureUncategorizedAsync(workspace);
        Assert.Equal(first.Id,second.Id);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>repository.DeleteFolderAsync(workspace,first.Id,first.Id));
    }

    [Fact]
    public async Task DeleteFolder_MovesItemsToUncategorized()
    {
        var (repository,workspace)=await CreateAsync();
        var uncategorized=await repository.EnsureUncategorizedAsync(workspace);
        var folder=await repository.CreateFolderAsync(workspace,"常用回复");
        var item=await repository.CreateItemAsync(workspace,new CreateCollectionItem(folder.Id,CollectionItemType.Text,"问候","你好"));
        await repository.DeleteFolderAsync(workspace,folder.Id,uncategorized.Id);
        Assert.Empty(await repository.GetItemsAsync(workspace,folder.Id));
        Assert.Contains(await repository.GetItemsAsync(workspace,uncategorized.Id),x=>x.Id==item.Id);
    }

    [Fact]
    public async Task FolderAndMixedItemOrder_Persists()
    {
        var (repository,workspace)=await CreateAsync();
        var uncategorized=await repository.EnsureUncategorizedAsync(workspace);
        var a=await repository.CreateFolderAsync(workspace,"A");
        var b=await repository.CreateFolderAsync(workspace,"B");
        await repository.ReorderFolderAsync(workspace,b.Id,0);
        Assert.Equal(b.Id,(await repository.GetFoldersAsync(workspace))[0].Id);

        var text=await repository.CreateItemAsync(workspace,new CreateCollectionItem(uncategorized.Id,CollectionItemType.Text,"T","text"));
        var link=await repository.CreateItemAsync(workspace,new CreateCollectionItem(uncategorized.Id,CollectionItemType.Link,"L",Url:"https://example.com"));
        await repository.MoveItemAsync(workspace,link.Id,uncategorized.Id,0);
        var items=await repository.GetItemsAsync(workspace,uncategorized.Id);
        Assert.Equal(link.Id,items[0].Id);
        Assert.Equal(text.Id,items[1].Id);
    }

    [Fact]
    public async Task Queries_AreWorkspaceIsolated()
    {
        var (repository,workspace)=await CreateAsync();
        var other=Guid.NewGuid(); await RegisterWorkspaceAsync(other);
        var own=await repository.EnsureUncategorizedAsync(workspace);
        var foreign=await repository.EnsureUncategorizedAsync(other);
        await repository.CreateItemAsync(workspace,new CreateCollectionItem(own.Id,CollectionItemType.Text,TextContent:"own"));
        await repository.CreateItemAsync(other,new CreateCollectionItem(foreign.Id,CollectionItemType.Text,TextContent:"other"));
        Assert.Single(await repository.GetItemsAsync(workspace,own.Id));
        Assert.Single(await repository.GetItemsAsync(other,foreign.Id));
    }

    [Fact]
    public async Task TargetFolderWrites_RejectForeignMissingAndDeletedFolders()
    {
        var (repository, workspace) = await CreateAsync();
        var own = await repository.EnsureUncategorizedAsync(workspace);
        var other = Guid.NewGuid();
        await RegisterWorkspaceAsync(other);
        var foreign = await repository.EnsureUncategorizedAsync(other);
        var deleted = await repository.CreateFolderAsync(workspace, "deleted");
        await repository.DeleteFolderAsync(workspace, deleted.Id, own.Id);
        var item = await repository.CreateItemAsync(workspace, new CreateCollectionItem(own.Id, CollectionItemType.Text, TextContent: "original"));
        foreach (var target in new[] { foreign.Id, Guid.NewGuid(), deleted.Id })
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => repository.CreateItemAsync(workspace, new CreateCollectionItem(target, CollectionItemType.Text, TextContent: "bad")));
            await Assert.ThrowsAsync<InvalidOperationException>(() => repository.MoveItemAsync(workspace, item.Id, target, 0));
            await Assert.ThrowsAsync<InvalidOperationException>(() => repository.UpdateItemAsync(item with { FolderId = target, TextContent = "bad" }));
        }
        await Assert.ThrowsAsync<InvalidOperationException>(() => repository.MoveItemAsync(other, item.Id, foreign.Id, 0));
        await Assert.ThrowsAsync<InvalidOperationException>(() => repository.UpdateItemAsync(item with { WorkspaceId = other, FolderId = foreign.Id, TextContent = "bad" }));
        Assert.Equal(item, Assert.Single(await Reopen().GetItemsAsync(workspace, own.Id)));
        Assert.Empty(await repository.GetItemsAsync(other, foreign.Id));
    }

    [Fact]
    public async Task Update_DoesNotInsertResurrectOrTrustStaleMetadata()
    {
        var (repository, workspace) = await CreateAsync();
        var folder = await repository.EnsureUncategorizedAsync(workspace);
        var target = await repository.CreateFolderAsync(workspace, "target");
        var item = await repository.CreateItemAsync(workspace, new CreateCollectionItem(folder.Id, CollectionItemType.Link, "old", Url: "https://example.com"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => repository.UpdateItemAsync(item with { Id = Guid.NewGuid() }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => repository.UpdateItemAsync(item with { FolderId = target.Id }));
        await repository.UpdateItemAsync(item with { Title = "new", Url = "https://example.org", SortKey = -999, LocalVersion = 100, DeletedAt = DateTimeOffset.UtcNow });
        await repository.UpdateItemAsync(item with { Title = "newer", Url = "https://example.net" });
        var saved = Assert.Single(await Reopen().GetItemsAsync(workspace, folder.Id));
        Assert.Equal("newer", saved.Title);
        Assert.Equal("https://example.net", saved.Url);
        Assert.Equal(3, saved.LocalVersion);
        Assert.Equal(item.SortKey, saved.SortKey);
        Assert.Equal(item.CreatedAt, saved.CreatedAt);
        await repository.DeleteItemAsync(workspace, item.Id);
        await Assert.ThrowsAsync<InvalidOperationException>(() => repository.UpdateItemAsync(item));
        Assert.Empty(await Reopen().GetItemsAsync(workspace, folder.Id));
        Assert.Equal(1L, await ScalarAsync("SELECT COUNT(*) FROM collection_items WHERE deleted_at IS NOT NULL;"));
    }

    [Fact]
    public async Task DeleteFolder_RejectsMaliciousFallbackWithoutChangingAnyItems()
    {
        var (repository, workspace) = await CreateAsync();
        var system = await repository.EnsureUncategorizedAsync(workspace);
        var folder = await repository.CreateFolderAsync(workspace, "normal");
        var deleted = await repository.CreateFolderAsync(workspace, "deleted");
        await repository.DeleteFolderAsync(workspace, deleted.Id, system.Id);
        var other = Guid.NewGuid();
        await RegisterWorkspaceAsync(other);
        var foreign = await repository.EnsureUncategorizedAsync(other);
        var protectedItem = await repository.CreateItemAsync(workspace, new CreateCollectionItem(system.Id, CollectionItemType.Text, TextContent: "protected"));
        var normalItem = await repository.CreateItemAsync(workspace, new CreateCollectionItem(folder.Id, CollectionItemType.Text, TextContent: "normal"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => repository.DeleteFolderAsync(workspace, system.Id, folder.Id));
        foreach (var fallback in new[] { foreign.Id, folder.Id, Guid.NewGuid(), deleted.Id })
            await Assert.ThrowsAsync<InvalidOperationException>(() => repository.DeleteFolderAsync(workspace, folder.Id, fallback));
        await Assert.ThrowsAsync<InvalidOperationException>(() => repository.DeleteFolderAsync(other, folder.Id, foreign.Id));
        Assert.Equal(protectedItem, Assert.Single(await Reopen().GetItemsAsync(workspace, system.Id)));
        Assert.Equal(normalItem, Assert.Single(await repository.GetItemsAsync(workspace, folder.Id)));
        Assert.Equal(2, (await repository.GetFoldersAsync(workspace)).Count);
    }

    [Fact]
    public async Task ReorderAndMove_OnlyChangeMovedRowsAndPersistAfterReopen()
    {
        var (repository, workspace) = await CreateAsync();
        var system = await repository.EnsureUncategorizedAsync(workspace);
        var a = await repository.CreateFolderAsync(workspace, "A");
        var b = await repository.CreateFolderAsync(workspace, "B");
        await repository.ReorderFolderAsync(workspace, b.Id, 1);
        var folders = await Reopen().GetFoldersAsync(workspace);
        Assert.Equal(new[] { system.Id, b.Id, a.Id }, folders.Select(x => x.Id));
        Assert.Equal(system, folders[0]);
        Assert.Equal(a, folders[2]);
        Assert.Equal(2, folders[1].LocalVersion);
        await repository.ReorderFolderAsync(workspace, b.Id, 1);
        Assert.Equal(folders, await repository.GetFoldersAsync(workspace));
        var first = await repository.CreateItemAsync(workspace, new CreateCollectionItem(a.Id, CollectionItemType.Text, TextContent: "first"));
        var second = await repository.CreateItemAsync(workspace, new CreateCollectionItem(a.Id, CollectionItemType.Link, Url: "https://example.com"));
        var moved = await repository.CreateItemAsync(workspace, new CreateCollectionItem(b.Id, CollectionItemType.Text, TextContent: "moved"));
        await repository.MoveItemAsync(workspace, moved.Id, a.Id, 1);
        var items = await Reopen().GetItemsAsync(workspace, a.Id);
        Assert.Equal(new[] { first.Id, moved.Id, second.Id }, items.Select(x => x.Id));
        Assert.Equal(first, items[0]);
        Assert.Equal(second, items[2]);
        Assert.Equal(2, items[1].LocalVersion);
        await repository.MoveItemAsync(workspace, moved.Id, a.Id, 1);
        Assert.Equal(items, await repository.GetItemsAsync(workspace, a.Id));
        Assert.Empty(await repository.GetItemsAsync(workspace, b.Id));
    }

    [Fact]
    public async Task RepeatedInsertion_RebalancesExhaustedGapsAndKeepsStrictOrder()
    {
        var (repository, workspace) = await CreateAsync();
        var folder = await repository.EnsureUncategorizedAsync(workspace);
        var first = await repository.CreateItemAsync(workspace, new CreateCollectionItem(folder.Id, CollectionItemType.Text, TextContent: "anchor"));
        var expected = new List<Guid> { first.Id };
        for (var i = 0; i < 80; i++)
        {
            var item = await repository.CreateItemAsync(workspace, new CreateCollectionItem(folder.Id, CollectionItemType.Text, TextContent: i.ToString()));
            await repository.MoveItemAsync(workspace, item.Id, folder.Id, 1);
            expected.Insert(1, item.Id);
        }
        var items = await Reopen().GetItemsAsync(workspace, folder.Id);
        Assert.Equal(expected, items.Select(x => x.Id));
        Assert.True(items.Zip(items.Skip(1)).All(x => x.First.SortKey < x.Second.SortKey));
        Assert.Contains(items, x => x.LocalVersion > 2);
    }

    [Fact]
    public async Task DeleteFolder_AppendsInSourceOrderWithoutTouchingExistingFallbackRows()
    {
        var (repository, workspace) = await CreateAsync();
        var system = await repository.EnsureUncategorizedAsync(workspace);
        var folder = await repository.CreateFolderAsync(workspace, "source");
        var existing = await repository.CreateItemAsync(workspace, new CreateCollectionItem(system.Id, CollectionItemType.Text, TextContent: "existing"));
        var a = await repository.CreateItemAsync(workspace, new CreateCollectionItem(folder.Id, CollectionItemType.Text, TextContent: "A"));
        var b = await repository.CreateItemAsync(workspace, new CreateCollectionItem(folder.Id, CollectionItemType.Text, TextContent: "B"));
        await repository.DeleteFolderAsync(workspace, folder.Id, system.Id);
        var items = await Reopen().GetItemsAsync(workspace, system.Id);
        Assert.Equal(new[] { existing.Id, a.Id, b.Id }, items.Select(x => x.Id));
        Assert.Equal(existing, items[0]);
        Assert.Equal(2, items[1].LocalVersion);
        Assert.Equal(2, items[2].LocalVersion);
        Assert.True(items.Zip(items.Skip(1)).All(x => x.First.SortKey < x.Second.SortKey));
        Assert.DoesNotContain(await repository.GetFoldersAsync(workspace), x => x.Id == folder.Id);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MoveAndDelete_RollBackWhenFinalWriteFails(bool delete)
    {
        var (repository, workspace) = await CreateAsync();
        var system = await repository.EnsureUncategorizedAsync(workspace);
        var folder = await repository.CreateFolderAsync(workspace, "source");
        var existing = await repository.CreateItemAsync(workspace, new CreateCollectionItem(system.Id, CollectionItemType.Text, TextContent: "existing"));
        var moved = await repository.CreateItemAsync(workspace, new CreateCollectionItem(folder.Id, CollectionItemType.Text, TextContent: "moved"));
        // Force rebalance, then fail the final mutation: earlier key writes must roll back too.
        await ExecuteSqlAsync("UPDATE collection_items SET sort_key=4503599627370496 WHERE folder_id='" + system.Id + "';");
        var before = await repository.GetItemsAsync(workspace, system.Id);
        await ExecuteSqlAsync(delete
            ? "CREATE TRIGGER fail_delete BEFORE UPDATE OF deleted_at ON collection_folders BEGIN SELECT RAISE(ABORT,'injected'); END;"
            : "CREATE TRIGGER fail_move BEFORE UPDATE ON collection_items WHEN NEW.id='" + moved.Id + "' BEGIN SELECT RAISE(ABORT,'injected'); END;");
        await Assert.ThrowsAsync<SqliteException>(() => delete
            ? repository.DeleteFolderAsync(workspace, folder.Id, system.Id)
            : repository.MoveItemAsync(workspace, moved.Id, system.Id, 1));
        Assert.Equal(before, await Reopen().GetItemsAsync(workspace, system.Id));
        Assert.Equal(moved, Assert.Single(await repository.GetItemsAsync(workspace, folder.Id)));
        Assert.Contains(await repository.GetFoldersAsync(workspace), x => x.Id == folder.Id);
    }

    [Fact]
    public async Task FolderReorder_RebalancesAdjacentRealKeysAndRollsBackOnFailure()
    {
        var (repository, workspace) = await CreateAsync();
        var a = await repository.CreateFolderAsync(workspace, "A");
        var b = await repository.CreateFolderAsync(workspace, "B");
        var c = await repository.CreateFolderAsync(workspace, "C");
        await ExecuteSqlAsync("UPDATE collection_folders SET sort_key=CASE name WHEN 'A' THEN 1.0 WHEN 'B' THEN 1.0000000000000002 ELSE 10 END;");
        var before = await repository.GetFoldersAsync(workspace);
        await ExecuteSqlAsync("CREATE TRIGGER fail_reorder BEFORE UPDATE ON collection_folders WHEN NEW.id='" + c.Id + "' BEGIN SELECT RAISE(ABORT,'injected'); END;");
        await Assert.ThrowsAsync<SqliteException>(() => repository.ReorderFolderAsync(workspace, c.Id, 1));
        Assert.Equal(before, await Reopen().GetFoldersAsync(workspace));
        await ExecuteSqlAsync("DROP TRIGGER fail_reorder;");
        await repository.ReorderFolderAsync(workspace, c.Id, 1);
        var after = await Reopen().GetFoldersAsync(workspace);
        Assert.Equal(new[] { a.Id, c.Id, b.Id }, after.Select(x => x.Id));
        Assert.True(after.Zip(after.Skip(1)).All(x => x.First.SortKey < x.Second.SortKey));
    }

    [Fact]
    public async Task SourceHistory_MustBelongToWorkspaceAndSnapshotSurvivesSoftDelete()
    {
        var (repository, workspace) = await CreateAsync();
        var folder = await repository.EnsureUncategorizedAsync(workspace);
        var other = Guid.NewGuid();
        await RegisterWorkspaceAsync(other);
        var source = Guid.NewGuid();
        await ExecuteSqlAsync($"INSERT INTO clipboard_items(id,workspace_id,device_id,item_type,content_hash,created_at,updated_at,captured_at) VALUES('{source}','{other}','{Guid.NewGuid()}',0,'hash','2026-01-01','2026-01-01','2026-01-01');");
        await Assert.ThrowsAsync<InvalidOperationException>(() => repository.CreateItemAsync(workspace, new CreateCollectionItem(folder.Id, CollectionItemType.Text, TextContent: "copy", SourceClipboardItemId: source)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => repository.CreateItemAsync(workspace, new CreateCollectionItem(folder.Id, CollectionItemType.Text, TextContent: "copy", SourceClipboardItemId: Guid.NewGuid())));
        await ExecuteSqlAsync($"UPDATE clipboard_items SET workspace_id='{workspace}' WHERE id='{source}';");
        var snapshot = await repository.CreateItemAsync(workspace, new CreateCollectionItem(folder.Id, CollectionItemType.Text, TextContent: "copy", SourceClipboardItemId: source));
        await ExecuteSqlAsync($"UPDATE clipboard_items SET deleted_at='2026-01-02' WHERE id='{source}';");
        await repository.UpdateItemAsync(snapshot with { TextContent = "edited snapshot" });
        Assert.Equal("edited snapshot", Assert.Single(await Reopen().GetItemsAsync(workspace, folder.Id)).TextContent);
        await Assert.ThrowsAsync<InvalidOperationException>(() => repository.CreateItemAsync(workspace, new CreateCollectionItem(folder.Id, CollectionItemType.Text, TextContent: "copy", SourceClipboardItemId: source)));
    }

    /// <summary>
    /// Collecting a local history record into the second config space must keep the
    /// provenance id while creating the snapshot in the target space, and must not touch
    /// the history row. Provenance is still validated — against the source space.
    /// </summary>
    [Fact]
    public async Task Snapshot_MayCrossWorkspacesWhenTheSourceSpaceIsNamed()
    {
        var (repository, workspace) = await CreateAsync();
        var team = Guid.NewGuid();
        await RegisterWorkspaceAsync(team);
        var teamFolder = await repository.EnsureUncategorizedAsync(team);
        var source = Guid.NewGuid();
        await ExecuteSqlAsync($"INSERT INTO clipboard_items(id,workspace_id,device_id,item_type,content_hash,text_content,created_at,updated_at,captured_at) VALUES('{source}','{workspace}','{Guid.NewGuid()}',0,'hash','shared','2026-01-01','2026-01-01','2026-01-01');");

        // Without the source space the record looks foreign to the target space.
        await Assert.ThrowsAsync<InvalidOperationException>(() => repository.CreateItemAsync(team,
            new CreateCollectionItem(teamFolder.Id, CollectionItemType.Text, TextContent: "shared", SourceClipboardItemId: source)));

        var copy = await repository.CreateItemAsync(team, new CreateCollectionItem(teamFolder.Id,
            CollectionItemType.Text, TextContent: "shared", SourceClipboardItemId: source, SourceWorkspaceId: workspace));
        Assert.Equal(copy, Assert.Single(await Reopen().GetItemsAsync(team, teamFolder.Id)));
        Assert.Equal(source, copy.SourceClipboardItemId);
        Assert.Equal(team, copy.WorkspaceId);
        // The history row keeps its own workspace and is not soft deleted.
        Assert.Equal(1L, await ScalarAsync($"SELECT COUNT(*) FROM clipboard_items WHERE id='{source}' AND workspace_id='{workspace}' AND deleted_at IS NULL;"));
        // A source that exists in neither space is still rejected.
        await Assert.ThrowsAsync<InvalidOperationException>(() => repository.CreateItemAsync(team,
            new CreateCollectionItem(teamFolder.Id, CollectionItemType.Text, TextContent: "bad", SourceClipboardItemId: Guid.NewGuid(), SourceWorkspaceId: workspace)));
    }

    private SqliteCollectionRepository Reopen() => new(new SqliteConnectionFactory(new AppPaths(_root)));
    private async Task ExecuteSqlAsync(string sql)
    {
        await using var connection = new SqliteConnectionFactory(new AppPaths(_root)).Create();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }
    private async Task<long> ScalarAsync(string sql)
    {
        await using var connection = new SqliteConnectionFactory(new AppPaths(_root)).Create();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    public void Dispose(){SqliteConnection.ClearAllPools();if(Directory.Exists(_root))Directory.Delete(_root,true);}
    private async Task<(SqliteCollectionRepository,Guid)> CreateAsync(){var paths=new AppPaths(_root);var factory=new SqliteConnectionFactory(paths);await new SqliteDatabaseInitializer(factory,NullLogger<SqliteDatabaseInitializer>.Instance).InitializeAsync();var workspace=Guid.NewGuid();await RegisterWorkspaceAsync(workspace,factory);return(new SqliteCollectionRepository(factory),workspace);}
    private async Task RegisterWorkspaceAsync(Guid id,SqliteConnectionFactory? factory=null){factory??=new SqliteConnectionFactory(new AppPaths(_root));await using var c=factory.Create();await c.OpenAsync();await using var cmd=c.CreateCommand();cmd.CommandText="INSERT OR IGNORE INTO workspaces(id,workspace_type,name,created_at,updated_at)VALUES($id,0,'test',$now,$now);";cmd.Parameters.AddWithValue("$id",id.ToString("D"));cmd.Parameters.AddWithValue("$now",DateTimeOffset.UtcNow.ToString("O"));await cmd.ExecuteNonQueryAsync();}
}
