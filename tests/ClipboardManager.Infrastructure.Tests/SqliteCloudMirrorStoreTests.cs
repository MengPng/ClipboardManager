using ClipboardManager.Domain.Models;
using ClipboardManager.Infrastructure.Data;
using ClipboardManager.Infrastructure.Paths;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClipboardManager.Infrastructure.Tests;

public sealed class SqliteCloudMirrorStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ClipboardManager.Mirror.Tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task PullBatch_ProjectsIntoLocalTablesAndAdvancesCursorAtomically()
    {
        var factory = new SqliteConnectionFactory(new AppPaths(_root));
        await new SqliteDatabaseInitializer(factory, NullLogger<SqliteDatabaseInitializer>.Instance).InitializeAsync();
        var store = new SqliteCloudMirrorStore(factory);
        var workspaceId = Guid.NewGuid(); var folderId = Guid.NewGuid(); var itemId = Guid.NewGuid();
        await store.EnsureMappingAsync(new CloudWorkspace(workspaceId, WorkspaceType.Personal, "我的云配置", WorkspaceRole.Owner, 1024, 0));

        await store.ApplyPullBatchAsync(workspaceId,
        [
            new("collection_folder", folderId, 1, "{\"name\":\"常用\",\"sortKey\":1,\"isSystem\":false}", false),
            new("collection_item", itemId, 1, $"{{\"folderId\":\"{folderId:D}\",\"itemType\":0,\"textContent\":\"本地镜像\",\"sortKey\":1}}", false)
        ], "42", DateTimeOffset.UtcNow);

        var mapping = await store.GetMappingAsync(workspaceId);
        Assert.NotNull(mapping); Assert.Equal("42", mapping.RemoteCursor); Assert.Equal(CloudCacheState.Current, mapping.CacheState);
        await using var connection = factory.Create(); await connection.OpenAsync();
        await using var command = connection.CreateCommand(); command.CommandText = "SELECT text_content FROM collection_items WHERE id=$id";
        command.Parameters.AddWithValue("$id", itemId.ToString("D")); Assert.Equal("本地镜像", await command.ExecuteScalarAsync());
    }

    [Fact]
    public async Task InvalidBatch_DoesNotAdvanceCursor()
    {
        var factory = new SqliteConnectionFactory(new AppPaths(_root));
        await new SqliteDatabaseInitializer(factory, NullLogger<SqliteDatabaseInitializer>.Instance).InitializeAsync();
        var store = new SqliteCloudMirrorStore(factory); var workspaceId = Guid.NewGuid();
        await store.EnsureMappingAsync(new CloudWorkspace(workspaceId, WorkspaceType.Team, "团队", WorkspaceRole.Member, 1024, 0));

        await Assert.ThrowsAsync<InvalidDataException>(() => store.ApplyPullBatchAsync(workspaceId,
            [new("unknown", Guid.NewGuid(), 1, "{}", false)], "99", DateTimeOffset.UtcNow));

        Assert.Null((await store.GetMappingAsync(workspaceId))!.RemoteCursor);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
