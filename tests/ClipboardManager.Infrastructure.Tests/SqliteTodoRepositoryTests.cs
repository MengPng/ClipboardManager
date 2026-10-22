using ClipboardManager.Domain.Models;
using ClipboardManager.Infrastructure.Data;
using ClipboardManager.Infrastructure.Paths;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClipboardManager.Infrastructure.Tests;

public sealed class SqliteTodoRepositoryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ClipboardManager.Tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task NewWorkspace_StartsWithoutTodoTags()
    {
        var (repository, workspace) = await CreateAsync();

        Assert.Empty(await repository.GetTagsAsync(workspace));
    }

    [Fact]
    public async Task UpdateTag_PersistsNameAndColorAndRejectsDuplicates()
    {
        var (repository, workspace) = await CreateAsync();
        var first = await repository.CreateTagAsync(workspace, "旧名称", "#2563EB");
        await repository.CreateTagAsync(workspace, "已存在", "#059669");

        var updated = await repository.UpdateTagAsync(workspace, first.Id, "新名称", "#E11D48");

        Assert.Equal("新名称", updated.Name);
        Assert.Equal("#E11D48", updated.Color);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            repository.UpdateTagAsync(workspace, first.Id, "已存在", "#E11D48"));
    }

    [Fact]
    public async Task DeleteTag_DetachesFromItemsWithoutDeletingTheTodo()
    {
        var (repository, workspace) = await CreateAsync();
        var tag = await repository.CreateTagAsync(workspace, "合同", "#2563EB");
        var item = await repository.CreateItemAsync(workspace, "跟进合同", [tag.Id]);
        await repository.DeleteTagAsync(workspace, tag.Id);
        Assert.DoesNotContain(await repository.GetTagsAsync(workspace), x => x.Id == tag.Id);
        var remaining = Assert.Single(await repository.GetItemsAsync(workspace));
        Assert.Equal(item.Id, remaining.Id);
        Assert.Empty(remaining.TagIds);
    }

    [Fact]
    public async Task Queries_RejectCrossWorkspaceWrites()
    {
        var (repository, workspace) = await CreateAsync();
        var other = Guid.NewGuid();
        await RegisterWorkspaceAsync(other);
        var tag = await repository.CreateTagAsync(workspace, "本机", "#2563EB");
        var item = await repository.CreateItemAsync(workspace, "本机待办", [tag.Id]);
        await Assert.ThrowsAsync<InvalidOperationException>(() => repository.UpdateItemAsync(other, item.Id, "越界", []));
        await Assert.ThrowsAsync<InvalidOperationException>(() => repository.SetCompletedAsync(other, item.Id, true, DateTimeOffset.UtcNow));
        await Assert.ThrowsAsync<InvalidOperationException>(() => repository.DeleteItemAsync(other, item.Id));
        await Assert.ThrowsAsync<InvalidOperationException>(() => repository.UpdateTagAsync(other, tag.Id, "越界", "#E11D48"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => repository.DeleteTagAsync(other, tag.Id));
        Assert.False((await repository.GetItemsAsync(workspace)).Single().IsCompleted);
    }

    [Fact]
    public async Task UpdateItem_PersistsTitleAndReplacesTags()
    {
        var (repository, workspace) = await CreateAsync();
        var first = await repository.CreateTagAsync(workspace, "甲", "#2563EB");
        var second = await repository.CreateTagAsync(workspace, "乙", "#E11D48");
        var item = await repository.CreateItemAsync(workspace, "原内容", [first.Id]);

        var updated = await repository.UpdateItemAsync(workspace, item.Id, "新内容", [second.Id]);

        Assert.Equal("新内容", updated.Title);
        Assert.Equal(second.Id, Assert.Single(updated.TagIds));
        var persisted = Assert.Single(await repository.GetItemsAsync(workspace));
        Assert.Equal("新内容", persisted.Title);
        Assert.Equal(second.Id, Assert.Single(persisted.TagIds));
    }

    [Fact]
    public async Task CompletedItems_AreOrderedAfterActiveItems()
    {
        var (repository, workspace) = await CreateAsync();
        var completed = await repository.CreateItemAsync(workspace, "先创建后完成", []);
        await repository.SetCompletedAsync(workspace, completed.Id, true, DateTimeOffset.UtcNow);
        var active = await repository.CreateItemAsync(workspace, "进行中", []);

        var items = await repository.GetItemsAsync(workspace);

        Assert.Equal(active.Id, items[0].Id);
        Assert.Equal(completed.Id, items[^1].Id);
    }

    [Fact]
    public async Task ClearCompleted_SoftDeletesOnlyFinishedItems()
    {
        var (repository, workspace) = await CreateAsync();
        var active = await repository.CreateItemAsync(workspace, "未完成", []);
        var done = await repository.CreateItemAsync(workspace, "已完成", []);
        await repository.SetCompletedAsync(workspace, done.Id, true, DateTimeOffset.UtcNow);
        Assert.Equal(1, await repository.ClearCompletedAsync(workspace, DateTimeOffset.UtcNow));
        Assert.Equal(active.Id, Assert.Single(await repository.GetItemsAsync(workspace)).Id);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private async Task<(SqliteTodoRepository Repository, Guid WorkspaceId)> CreateAsync()
    {
        var paths = new AppPaths(_root);
        var factory = new SqliteConnectionFactory(paths);
        await new SqliteDatabaseInitializer(factory, NullLogger<SqliteDatabaseInitializer>.Instance).InitializeAsync();
        var workspace = Guid.NewGuid();
        await RegisterWorkspaceAsync(workspace, factory);
        return (new SqliteTodoRepository(factory), workspace);
    }

    private async Task RegisterWorkspaceAsync(Guid id, SqliteConnectionFactory? factory = null)
    {
        factory ??= new SqliteConnectionFactory(new AppPaths(_root));
        await using var connection = factory.Create();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT OR IGNORE INTO workspaces(id,workspace_type,name,created_at,updated_at) VALUES($id,0,'test',$now,$now);";
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync();
    }
}
