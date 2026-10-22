using ClipboardManager.Application.Services;
using ClipboardManager.Domain.Contracts;
using ClipboardManager.Domain.Models;

namespace ClipboardManager.Application.Tests;

public sealed class TodoServiceTests
{
    [Fact]
    public async Task CreateCompleteAndRestore_UpdatesCompletionTime()
    {
        var (service, repository) = Create();
        var created = await service.CreateAsync("写周报", []);
        Assert.False(created.IsCompleted);
        Assert.Null(created.CompletedAt);

        var completed = await service.SetCompletedAsync(created.Id, true);
        Assert.True(completed.IsCompleted);
        Assert.NotNull(completed.CompletedAt);

        var restored = await service.SetCompletedAsync(created.Id, false);
        Assert.False(restored.IsCompleted);
        Assert.Null(restored.CompletedAt);
        Assert.Equal("写周报", repository.Items.Single().Title);
    }

    [Fact]
    public async Task Snapshot_ComputesTodayStatsAndFilter()
    {
        var (service, _) = Create();
        var now = DateTimeOffset.Now;
        var work = await service.CreateTagAsync("项目", "#2563EB");
        await service.CreateAsync("进行中", [work.Id]);
        var done = await service.CreateAsync("已完成", [work.Id]);
        await service.SetCompletedAsync(done.Id, true);

        var all = await service.GetSnapshotAsync(TodoListFilter.All, null, now);
        Assert.Equal(1, all.Summary.Pending);
        Assert.Equal(1, all.Summary.TodayCompleted);
        Assert.Equal(2, all.Summary.TodayCreated);
        Assert.Equal(50, all.Summary.CompletionRate);
        Assert.Equal(2, all.Items.Count);
        Assert.Contains(all.Items, item => item.IsCompleted && TodoService.IsCompletedToday(item, now));

        var active = await service.GetSnapshotAsync(TodoListFilter.Active, null, now);
        Assert.Single(active.Items);
        Assert.Equal("进行中", active.Items[0].Title);

        var tagged = await service.GetSnapshotAsync(TodoListFilter.All, work.Id, now);
        Assert.Equal(2, tagged.Items.Count);
        var workStat = tagged.TagStats.Single(stat => stat.Tag.Id == work.Id);
        Assert.Equal(1, workStat.TodayCompleted);
        Assert.Equal(1, workStat.Pending);
        Assert.Equal(50, workStat.CompletionRate);
    }

    [Fact]
    public async Task UpdateTodo_ChangesTitleAndTags()
    {
        var (service, _) = Create();
        var first = await service.CreateTagAsync("甲", "#2563EB");
        var second = await service.CreateTagAsync("乙", "#E11D48");
        var item = await service.CreateAsync("原内容", [first.Id]);

        var updated = await service.UpdateAsync(item.Id, "新内容", [second.Id]);

        Assert.Equal("新内容", updated.Title);
        Assert.Equal(second.Id, Assert.Single(updated.TagIds));
    }

    [Fact]
    public async Task UpdateTag_ChangesNameAndColor()
    {
        var (service, _) = Create();
        var tag = await service.CreateTagAsync("旧名称", "#2563EB");

        var updated = await service.UpdateTagAsync(tag.Id, "新名称", "#E11D48");

        Assert.Equal("新名称", updated.Name);
        Assert.Equal("#E11D48", updated.Color);
        var snapshot = await service.GetSnapshotAsync(TodoListFilter.All, null);
        Assert.Contains(snapshot.Tags, value => value.Id == tag.Id && value.Name == "新名称" && value.Color == "#E11D48");
    }

    [Fact]
    public async Task DeleteTag_RemovesItFromTodos()
    {
        var (service, repository) = Create();
        var tag = await service.CreateTagAsync("紧急", "#E11D48");
        var item = await service.CreateAsync("处理发票", [tag.Id]);
        Assert.Equal(tag.Id, Assert.Single(item.TagIds));

        await service.DeleteTagAsync(tag.Id);
        var snapshot = await service.GetSnapshotAsync(TodoListFilter.All, null);
        Assert.DoesNotContain(snapshot.Tags, x => x.Id == tag.Id);
        Assert.Empty(snapshot.Items.Single(x => x.Id == item.Id).TagIds);
        Assert.DoesNotContain(repository.Links, link => link.TagId == tag.Id);
    }

    [Fact]
    public async Task NewTodoWorkspace_StartsWithoutTags()
    {
        var (service, _) = Create();

        var snapshot = await service.GetSnapshotAsync(TodoListFilter.All, null);

        Assert.Empty(snapshot.Tags);
    }

    [Fact]
    public async Task ClearCompleted_LeavesActiveItems()
    {
        var (service, _) = Create();
        await service.CreateAsync("还在做", []);
        var done = await service.CreateAsync("做完了", []);
        await service.SetCompletedAsync(done.Id, true);
        Assert.Equal(1, await service.ClearCompletedAsync());
        var snapshot = await service.GetSnapshotAsync(TodoListFilter.All, null);
        Assert.Equal("还在做", Assert.Single(snapshot.Items).Title);
    }

    [Fact]
    public async Task Create_RejectsBlankTitleAndUnknownTags()
    {
        var (service, _) = Create();
        await Assert.ThrowsAsync<ArgumentException>(() => service.CreateAsync("   ", []));
        var created = await service.CreateAsync("有效", [Guid.NewGuid()]);
        Assert.Empty(created.TagIds);
    }

    private static (TodoService Service, MemoryTodoRepository Repository) Create()
    {
        var repository = new MemoryTodoRepository();
        var service = new TodoService(repository, new LocalWorkspace());
        return (service, repository);
    }

    private sealed class LocalWorkspace : IWorkspaceService
    {
        public static readonly Guid Id = Guid.Parse("11111111-1111-1111-1111-111111111111");
        private readonly Workspace _workspace = new(Id, WorkspaceType.Local, "本机", null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        public Task<IReadOnlyList<Workspace>> GetAvailableAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Workspace>>([_workspace]);
        public Task<Workspace> GetCurrentAsync(CancellationToken cancellationToken = default) => Task.FromResult(_workspace);
        public Task SwitchAsync(Guid workspaceId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class MemoryTodoRepository : ITodoRepository
    {
        public List<TodoTag> Tags { get; } = [];
        public List<TodoItem> Items { get; } = [];
        public List<(Guid TodoId, Guid TagId)> Links { get; } = [];

        public Task<IReadOnlyList<TodoTag>> GetTagsAsync(Guid workspaceId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<TodoTag>>(Tags.Where(tag => tag.WorkspaceId == workspaceId && tag.DeletedAt is null).ToList());

        public Task<TodoTag> CreateTagAsync(Guid workspaceId, string name, string color, CancellationToken cancellationToken = default)
        {
            if (Tags.Any(tag => tag.WorkspaceId == workspaceId && tag.DeletedAt is null &&
                                string.Equals(tag.Name, name, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException("标签名称不能重复。");
            }

            var tag = new TodoTag
            {
                Id = Guid.NewGuid(), WorkspaceId = workspaceId, Name = name, Color = color,
                SortKey = Tags.Count, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow
            };
            Tags.Add(tag);
            return Task.FromResult(tag);
        }

        public Task<TodoTag> UpdateTagAsync(Guid workspaceId, Guid tagId, string name, string color, CancellationToken cancellationToken = default)
        {
            var index = Tags.FindIndex(tag => tag.WorkspaceId == workspaceId && tag.Id == tagId && tag.DeletedAt is null);
            if (index < 0) throw new InvalidOperationException("标签不存在或不属于当前空间。");
            if (Tags.Any(tag => tag.WorkspaceId == workspaceId && tag.Id != tagId && tag.DeletedAt is null &&
                                string.Equals(tag.Name, name, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("标签名称不能重复。");
            Tags[index] = Tags[index] with { Name = name, Color = color, UpdatedAt = DateTimeOffset.UtcNow };
            return Task.FromResult(Tags[index]);
        }

        public Task DeleteTagAsync(Guid workspaceId, Guid tagId, CancellationToken cancellationToken = default)
        {
            var index = Tags.FindIndex(tag => tag.WorkspaceId == workspaceId && tag.Id == tagId && tag.DeletedAt is null);
            if (index < 0) throw new InvalidOperationException("标签不存在或不属于当前空间。");
            Tags[index] = Tags[index] with { DeletedAt = DateTimeOffset.UtcNow };
            Links.RemoveAll(link => link.TagId == tagId);
            for (var i = 0; i < Items.Count; i++)
                Items[i] = Items[i] with { TagIds = Items[i].TagIds.Where(id => id != tagId).ToArray() };
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<TodoItem>> GetItemsAsync(Guid workspaceId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<TodoItem>>(Items.Where(item => item.WorkspaceId == workspaceId && item.DeletedAt is null).ToList());

        public Task<TodoItem> CreateItemAsync(Guid workspaceId, string title, IReadOnlyList<Guid> tagIds, CancellationToken cancellationToken = default)
        {
            var item = new TodoItem
            {
                Id = Guid.NewGuid(), WorkspaceId = workspaceId, Title = title,
                CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow, TagIds = tagIds.ToArray()
            };
            Items.Add(item);
            foreach (var tagId in tagIds) Links.Add((item.Id, tagId));
            return Task.FromResult(item);
        }

        public Task<TodoItem> UpdateItemAsync(Guid workspaceId, Guid itemId, string title, IReadOnlyList<Guid> tagIds, CancellationToken cancellationToken = default)
        {
            var index = Items.FindIndex(item => item.WorkspaceId == workspaceId && item.Id == itemId && item.DeletedAt is null);
            if (index < 0) throw new InvalidOperationException("待办不存在或不属于当前空间。");
            Items[index] = Items[index] with { Title = title, TagIds = tagIds.ToArray(), UpdatedAt = DateTimeOffset.UtcNow };
            Links.RemoveAll(link => link.TodoId == itemId);
            foreach (var tagId in tagIds) Links.Add((itemId, tagId));
            return Task.FromResult(Items[index]);
        }

        public Task<TodoItem> SetCompletedAsync(Guid workspaceId, Guid itemId, bool completed, DateTimeOffset now, CancellationToken cancellationToken = default)
        {
            var index = Items.FindIndex(item => item.WorkspaceId == workspaceId && item.Id == itemId && item.DeletedAt is null);
            if (index < 0) throw new InvalidOperationException("待办不存在或不属于当前空间。");
            Items[index] = Items[index] with
            {
                IsCompleted = completed,
                CompletedAt = completed ? now : null,
                UpdatedAt = now
            };
            return Task.FromResult(Items[index]);
        }

        public Task DeleteItemAsync(Guid workspaceId, Guid itemId, CancellationToken cancellationToken = default)
        {
            var index = Items.FindIndex(item => item.WorkspaceId == workspaceId && item.Id == itemId && item.DeletedAt is null);
            if (index < 0) throw new InvalidOperationException("待办不存在或不属于当前空间。");
            Items[index] = Items[index] with { DeletedAt = DateTimeOffset.UtcNow };
            Links.RemoveAll(link => link.TodoId == itemId);
            return Task.CompletedTask;
        }

        public Task<int> ClearCompletedAsync(Guid workspaceId, DateTimeOffset deletedAt, CancellationToken cancellationToken = default)
        {
            var count = 0;
            for (var i = 0; i < Items.Count; i++)
            {
                if (Items[i].WorkspaceId != workspaceId || Items[i].DeletedAt is not null || !Items[i].IsCompleted) continue;
                Items[i] = Items[i] with { DeletedAt = deletedAt };
                Links.RemoveAll(link => link.TodoId == Items[i].Id);
                count++;
            }

            return Task.FromResult(count);
        }
    }
}
