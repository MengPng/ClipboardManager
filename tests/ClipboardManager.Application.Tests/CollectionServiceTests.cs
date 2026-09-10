using ClipboardManager.Application.Services;
using ClipboardManager.Domain.Contracts;
using ClipboardManager.Domain.Models;

namespace ClipboardManager.Application.Tests;

public sealed class CollectionServiceTests
{
    [Fact]
    public async Task AddSnapshot_CopiesTextAndDoesNotDependOnHistoryLifetime()
    {
        var repository=new FakeCollectionRepository();
        var service=new CollectionService(repository,new WorkspaceService(),new AssetStore());
        var folder=await repository.EnsureUncategorizedAsync(WorkspaceService.LocalId);
        var history=repository.AddHistory(WorkspaceService.LocalId,"hello");
        var saved=await service.AddSnapshotAsync(WorkspaceService.LocalId,folder.Id,history);
        Assert.Equal("hello",saved.TextContent);
        Assert.Equal(history.Id,saved.SourceClipboardItemId);
    }

    [Fact]
    public async Task UpdateContent_ValidatesWorkspaceAndLinkAndPreservesText()
    {
        var repository = new FakeCollectionRepository();
        var service = new CollectionService(repository, new WorkspaceService(), new AssetStore());
        var now = DateTimeOffset.UtcNow;
        var item = new CollectionItem { Id = Guid.NewGuid(), WorkspaceId = WorkspaceService.LocalId,
            FolderId = Guid.NewGuid(), ItemType = CollectionItemType.Text, SortKey = 0, CreatedAt = now, UpdatedAt = now };
        await service.UpdateContentAsync(WorkspaceService.LocalId, item, " 标题 ", "第一行\r\n第二行");
        Assert.Equal("第一行\r\n第二行", repository.Updated!.TextContent);
        Assert.Equal("标题", repository.Updated.Title);
        Assert.Equal(TextContentService.ComputeHash("第一行\n第二行"), repository.Updated.ContentHash);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.UpdateContentAsync(WorkspaceService.LocalId, item with { WorkspaceId = Guid.NewGuid() }, "", "text"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.UpdateContentAsync(WorkspaceService.LocalId, item with { DeletedAt = now }, "", "text"));
        await Assert.ThrowsAsync<ArgumentException>(() => service.UpdateContentAsync(WorkspaceService.LocalId, item with { ItemType = CollectionItemType.Link }, "", "file:///C:/test"));
        await service.UpdateContentAsync(WorkspaceService.LocalId, item with { ItemType = CollectionItemType.Link }, "链接", "https://example.com");
        Assert.Equal("https://example.com/", repository.Updated!.Url);
        Assert.Null(repository.Updated.TextContent);
    }

    [Fact]
    public async Task UpdateTitle_AllowsImageAndFileNamesWithoutChangingTheirPayload()
    {
        var repository = new FakeCollectionRepository();
        var service = new CollectionService(repository, new WorkspaceService(), new AssetStore());
        var now = DateTimeOffset.UtcNow;
        var image = new CollectionItem
        {
            Id = Guid.NewGuid(), WorkspaceId = WorkspaceService.LocalId, FolderId = Guid.NewGuid(),
            ItemType = CollectionItemType.Image, Title = "old", LocalPath = "managed.png",
            SortKey = 0, CreatedAt = now, UpdatedAt = now
        };

        await service.UpdateTitleAsync(WorkspaceService.LocalId, image, " 新名称 ");

        Assert.Equal("新名称", repository.Updated!.Title);
        Assert.Equal("managed.png", repository.Updated.LocalPath);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.UpdateTitleAsync(Guid.NewGuid(), image, "越界"));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.UpdateTitleAsync(WorkspaceService.LocalId, image, "  "));
    }

    [Fact]
    public async Task AddFile_AcceptsDirectoryAsLocalFolderMapping()
    {
        var repository = new FakeCollectionRepository();
        var service = new CollectionService(repository, new WorkspaceService(), new AssetStore());
        var folder = await repository.EnsureUncategorizedAsync(WorkspaceService.LocalId);
        var path = Path.Combine(Path.GetTempPath(), $"ClipboardManager-folder-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        try
        {
            var saved = await service.AddFileAsync(WorkspaceService.LocalId, folder.Id, path);
            Assert.Equal(CollectionItemType.File, saved.ItemType);
            Assert.Equal(Path.GetFileName(path), saved.Title);
            Assert.Equal(path, saved.LocalPath);
        }
        finally { Directory.Delete(path); }
    }

    /// <summary>
    /// The target space is passed explicitly, but a config item still has to belong to the
    /// space it is edited in: editing a 团队配置 item while claiming 个人配置 must fail.
    /// </summary>
    [Fact]
    public async Task UpdateContent_ComparesAgainstTheTargetSpaceNotTheCaptureSpace()
    {
        var repository = new FakeCollectionRepository();
        var service = new CollectionService(repository, new WorkspaceService(), new AssetStore());
        var now = DateTimeOffset.UtcNow;
        var teamItem = new CollectionItem { Id = Guid.NewGuid(), WorkspaceId = WorkspaceService.TeamId,
            FolderId = Guid.NewGuid(), ItemType = CollectionItemType.Text, SortKey = 0, CreatedAt = now, UpdatedAt = now };
        await service.UpdateContentAsync(WorkspaceService.TeamId, teamItem, "团队文本", "内容");
        Assert.Equal("内容", repository.Updated!.TextContent);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.UpdateContentAsync(WorkspaceService.LocalId, teamItem, "团队文本", "内容"));
    }

    [Fact]
    public async Task SnapshotFromDifferentWorkspaceOrDeletedHistory_IsRejected()
    {
        var repository = new FakeCollectionRepository();
        var service = new CollectionService(repository, new WorkspaceService(), new AssetStore());
        var folder = await repository.EnsureUncategorizedAsync(WorkspaceService.LocalId);
        var now = DateTimeOffset.UtcNow;
        var item = new ClipboardItem { Id = Guid.NewGuid(), WorkspaceId = Guid.NewGuid(), DeviceId = Guid.NewGuid(),
            ItemType = ClipboardItemType.Text, ContentHash = "h", TextContent = "x", CreatedAt = now, UpdatedAt = now, CapturedAt = now };
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.AddSnapshotAsync(WorkspaceService.LocalId, folder.Id, item));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.AddSnapshotAsync(WorkspaceService.LocalId, folder.Id, item with { WorkspaceId = WorkspaceService.LocalId, DeletedAt = now }));
    }

    /// <summary>
    /// 个人配置 and 团队配置 are separate workspaces, so neither can see or write the
    /// other's folders and items even though they share one set of UI controls.
    /// </summary>
    [Fact]
    public async Task ConfigSpaces_AreIsolatedFromEachOther()
    {
        var repository = new FakeCollectionRepository();
        var service = new CollectionService(repository, new WorkspaceService(), new AssetStore());
        var personal = await service.GetFoldersAsync(WorkspaceService.LocalId);
        var team = await service.GetFoldersAsync(WorkspaceService.TeamId);
        Assert.NotEqual(personal.Uncategorized.Id, team.Uncategorized.Id);

        var personalFolder = await service.CreateFolderAsync(WorkspaceService.LocalId, "个人目录");
        var teamFolder = await service.CreateFolderAsync(WorkspaceService.TeamId, "团队目录");
        await service.AddTextAsync(WorkspaceService.LocalId, personalFolder.Id, "个人", "个人内容");
        await service.AddTextAsync(WorkspaceService.TeamId, teamFolder.Id, "团队", "团队内容");

        // Invisible: each space lists only its own folders and only its own items.
        Assert.DoesNotContain((await service.GetFoldersAsync(WorkspaceService.LocalId)).Folders, x => x.Id == teamFolder.Id);
        Assert.DoesNotContain((await service.GetFoldersAsync(WorkspaceService.TeamId)).Folders, x => x.Id == personalFolder.Id);
        Assert.Equal("个人内容", Assert.Single(await service.GetItemsAsync(WorkspaceService.LocalId, personalFolder.Id)).TextContent);
        Assert.Empty(await service.GetItemsAsync(WorkspaceService.TeamId, personalFolder.Id));

        // Non-writable: naming the other space's folder from this space is rejected.
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.AddTextAsync(WorkspaceService.LocalId, teamFolder.Id, "越界", "越界"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RenameFolderAsync(WorkspaceService.LocalId, teamFolder.Id, "改名"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.DeleteFolderAsync(WorkspaceService.TeamId, personalFolder.Id));
        var personalItem = Assert.Single(await service.GetItemsAsync(WorkspaceService.LocalId, personalFolder.Id));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.MoveItemAsync(WorkspaceService.TeamId, personalItem.Id, teamFolder.Id, 0));
        Assert.Equal("团队目录", (await service.GetFoldersAsync(WorkspaceService.TeamId)).Folders.Single(x => !x.IsSystem).Name);
    }

    /// <summary>
    /// AGENTS.md requires putting content into a team space to be an explicit user action,
    /// so the keyboard quick-collect path must resolve to the local capture space even
    /// though a team workspace exists and the user may be viewing it.
    /// </summary>
    [Fact]
    public async Task QuickCollect_TargetsLocalUncategorizedAndNeverTheTeamSpace()
    {
        var repository = new FakeCollectionRepository();
        var service = new CollectionService(repository, new WorkspaceService(), new AssetStore());
        var teamFolders = await service.GetFoldersAsync(WorkspaceService.TeamId);
        var history = repository.AddHistory(WorkspaceService.LocalId, "quick");

        var saved = await service.QuickCollectAsync(history);

        Assert.Equal(WorkspaceService.LocalId, saved.WorkspaceId);
        var localUncategorized = await repository.EnsureUncategorizedAsync(WorkspaceService.LocalId);
        Assert.Equal(localUncategorized.Id, saved.FolderId);
        Assert.Empty(await service.GetItemsAsync(WorkspaceService.TeamId, teamFolders.Uncategorized.Id));
    }

    [Fact]
    public async Task CollectToUncategorized_UsesTheExplicitTargetWorkspace()
    {
        var repository = new FakeCollectionRepository();
        var service = new CollectionService(repository, new WorkspaceService(), new AssetStore());
        var history = repository.AddHistory(WorkspaceService.LocalId, "explicit team copy");

        var saved = await service.CollectToUncategorizedAsync(WorkspaceService.TeamId, history);

        var team = await service.GetFoldersAsync(WorkspaceService.TeamId);
        Assert.Equal(WorkspaceService.TeamId, saved.WorkspaceId);
        Assert.Equal(team.Uncategorized.Id, saved.FolderId);
        Assert.Equal(history.Id, saved.SourceClipboardItemId);
        Assert.Empty(await service.GetItemsAsync(WorkspaceService.LocalId,
            (await service.GetFoldersAsync(WorkspaceService.LocalId)).Uncategorized.Id));
    }

    /// <summary>
    /// Sharing into the team space creates an independent copy that keeps
    /// <c>source_item_id</c>; the original history record is neither moved nor changed.
    /// </summary>
    [Fact]
    public async Task SnapshotIntoTeamSpace_CopiesLocalHistoryAndLeavesTheOriginalUntouched()
    {
        var repository = new FakeCollectionRepository();
        var service = new CollectionService(repository, new WorkspaceService(), new AssetStore());
        var team = await service.GetFoldersAsync(WorkspaceService.TeamId);
        var history = repository.AddHistory(WorkspaceService.LocalId, "shared");

        var copy = await service.AddSnapshotAsync(WorkspaceService.TeamId, team.Uncategorized.Id, history);

        Assert.Equal(WorkspaceService.TeamId, copy.WorkspaceId);
        Assert.Equal(history.Id, copy.SourceClipboardItemId);
        Assert.Equal("shared", copy.TextContent);
        Assert.NotEqual(history.Id, copy.Id);
        // The history record still belongs to the local space and is not soft deleted.
        var original = Assert.Single(repository.History);
        Assert.Equal(history, original);
        Assert.Equal(WorkspaceService.LocalId, original.WorkspaceId);
        Assert.Null(original.DeletedAt);
        // The same record can still be collected into 个人配置 afterwards.
        var personal = await service.GetFoldersAsync(WorkspaceService.LocalId);
        var localCopy = await service.AddSnapshotAsync(WorkspaceService.LocalId, personal.Uncategorized.Id, history);
        Assert.Equal(WorkspaceService.LocalId, localCopy.WorkspaceId);
    }

    /// <summary>
    /// 上移/下移 resolve their index against the folder's full contents and persist through
    /// the same sort key drag-sorting uses; the first and last positions are no-ops.
    /// </summary>
    [Fact]
    public async Task MoveItemByOffset_ReordersWithinTheFolderAndNoOpsAtTheBoundaries()
    {
        var repository = new FakeCollectionRepository();
        var service = new CollectionService(repository, new WorkspaceService(), new AssetStore());
        var folders = await service.GetFoldersAsync(WorkspaceService.TeamId);
        var folder = folders.Uncategorized.Id;
        var first = await service.AddTextAsync(WorkspaceService.TeamId, folder, "A", "A");
        var second = await service.AddTextAsync(WorkspaceService.TeamId, folder, "B", "B");
        var third = await service.AddTextAsync(WorkspaceService.TeamId, folder, "C", "C");

        Assert.True(await service.MoveItemByOffsetAsync(WorkspaceService.TeamId, third.Id, folder, -1));
        Assert.Equal(new[] { first.Id, third.Id, second.Id },
            (await service.GetItemsAsync(WorkspaceService.TeamId, folder)).Select(x => x.Id));

        Assert.True(await service.MoveItemByOffsetAsync(WorkspaceService.TeamId, first.Id, folder, 1));
        Assert.Equal(new[] { third.Id, first.Id, second.Id },
            (await service.GetItemsAsync(WorkspaceService.TeamId, folder)).Select(x => x.Id));

        Assert.False(await service.MoveItemByOffsetAsync(WorkspaceService.TeamId, third.Id, folder, -1));
        Assert.False(await service.MoveItemByOffsetAsync(WorkspaceService.TeamId, second.Id, folder, 1));
        Assert.Equal(new[] { third.Id, first.Id, second.Id },
            (await service.GetItemsAsync(WorkspaceService.TeamId, folder)).Select(x => x.Id));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.MoveItemByOffsetAsync(WorkspaceService.TeamId, first.Id, folder, 2));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.MoveItemByOffsetAsync(WorkspaceService.TeamId, Guid.NewGuid(), folder, 1));
    }

    /// <summary>
    /// Mirrors <c>LocalWorkspaceService</c>: the current workspace is the local capture
    /// anchor, and the team space is only ever reachable through the available list.
    /// </summary>
    private sealed class WorkspaceService : IWorkspaceService
    {
        public static readonly Guid LocalId = Guid.NewGuid();
        public static readonly Guid TeamId = Guid.NewGuid();
        private static readonly Workspace Local = new(LocalId, WorkspaceType.Local, "本机剪切板", null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        private static readonly Workspace Team = new(TeamId, WorkspaceType.Team, "团队配置（仅本机）", null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        public Task<IReadOnlyList<Workspace>> GetAvailableAsync(CancellationToken c = default) => Task.FromResult<IReadOnlyList<Workspace>>([Local, Team]);
        public Task<Workspace> GetCurrentAsync(CancellationToken c = default) => Task.FromResult(Local);
        public Task SwitchAsync(Guid id, CancellationToken c = default) => Task.CompletedTask;
    }

    private sealed class AssetStore:ILocalAssetStore{public Task<StoredAsset> StoreImageAsync(byte[] b,string m,CancellationToken c=default)=>throw new NotSupportedException();public Task<StoredAsset> ImportImageAsync(string p,CancellationToken c=default)=>throw new NotSupportedException();}

    /// <summary>
    /// Stores folders, items and history in memory with the same workspace scoping and
    /// sort-key ordering the SQLite repository enforces, so the isolation and reorder
    /// assertions exercise real behaviour instead of a pass-through stub.
    /// </summary>
    private sealed class FakeCollectionRepository : ICollectionRepository
    {
        private readonly List<CollectionFolder> _folders = [];
        private readonly List<CollectionItem> _items = [];
        private readonly List<ClipboardItem> _history = [];
        private decimal _nextFolderKey = 1;

        public CollectionItem? Updated { get; private set; }
        public IReadOnlyList<ClipboardItem> History => _history;

        public ClipboardItem AddHistory(Guid workspaceId, string text)
        {
            var now = DateTimeOffset.UtcNow;
            var item = new ClipboardItem { Id = Guid.NewGuid(), WorkspaceId = workspaceId, DeviceId = Guid.NewGuid(),
                ItemType = ClipboardItemType.Text, ContentHash = "h", TextContent = text, PreviewText = text,
                CreatedAt = now, UpdatedAt = now, CapturedAt = now };
            _history.Add(item);
            return item;
        }

        public Task<CollectionFolder> EnsureUncategorizedAsync(Guid workspaceId, CancellationToken c = default)
        {
            var existing = _folders.FirstOrDefault(x => x.WorkspaceId == workspaceId && x.IsSystem && x.DeletedAt is null);
            if (existing is not null) return Task.FromResult(existing);
            var now = DateTimeOffset.UtcNow;
            var folder = new CollectionFolder { Id = Guid.NewGuid(), WorkspaceId = workspaceId, Name = "未分类",
                SortKey = 0, IsSystem = true, CreatedAt = now, UpdatedAt = now };
            _folders.Add(folder);
            return Task.FromResult(folder);
        }

        public Task<IReadOnlyList<CollectionFolder>> GetFoldersAsync(Guid workspaceId, CancellationToken c = default) =>
            Task.FromResult<IReadOnlyList<CollectionFolder>>(_folders
                .Where(x => x.WorkspaceId == workspaceId && x.DeletedAt is null)
                .OrderBy(x => x.SortKey).ToList());

        public Task<CollectionFolder> CreateFolderAsync(Guid workspaceId, string name, CancellationToken c = default)
        {
            var now = DateTimeOffset.UtcNow;
            var folder = new CollectionFolder { Id = Guid.NewGuid(), WorkspaceId = workspaceId, Name = name,
                SortKey = _nextFolderKey++, CreatedAt = now, UpdatedAt = now };
            _folders.Add(folder);
            return Task.FromResult(folder);
        }

        public Task RenameFolderAsync(Guid workspaceId, Guid folderId, string name, CancellationToken c = default)
        {
            var index = RequireFolderIndex(workspaceId, folderId);
            _folders[index] = _folders[index] with { Name = name, UpdatedAt = DateTimeOffset.UtcNow };
            return Task.CompletedTask;
        }

        public Task DeleteFolderAsync(Guid workspaceId, Guid folderId, Guid fallbackFolderId, CancellationToken c = default)
        {
            var index = RequireFolderIndex(workspaceId, folderId);
            if (_folders[index].IsSystem) throw new InvalidOperationException("系统文件夹不能删除。");
            var fallback = RequireFolderIndex(workspaceId, fallbackFolderId);
            if (!_folders[fallback].IsSystem) throw new InvalidOperationException("回退目标必须是同一空间的系统文件夹。");
            for (var i = 0; i < _items.Count; i++)
                if (_items[i].WorkspaceId == workspaceId && _items[i].FolderId == folderId)
                    _items[i] = _items[i] with { FolderId = fallbackFolderId, SortKey = NextKey(workspaceId, fallbackFolderId) };
            _folders[index] = _folders[index] with { DeletedAt = DateTimeOffset.UtcNow };
            return Task.CompletedTask;
        }

        public Task ReorderFolderAsync(Guid workspaceId, Guid folderId, int targetIndex, CancellationToken c = default)
        {
            var index = RequireFolderIndex(workspaceId, folderId);
            var ordered = _folders.Where(x => x.WorkspaceId == workspaceId && x.DeletedAt is null && x.Id != folderId)
                .OrderBy(x => x.SortKey).ToList();
            _folders[index] = _folders[index] with { SortKey = KeyAt(ordered.Select(x => x.SortKey).ToList(), targetIndex) };
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<CollectionItem>> GetItemsAsync(Guid workspaceId, Guid folderId, string? search = null, CancellationToken c = default) =>
            Task.FromResult<IReadOnlyList<CollectionItem>>(_items
                .Where(x => x.WorkspaceId == workspaceId && x.FolderId == folderId && x.DeletedAt is null)
                .Where(x => string.IsNullOrWhiteSpace(search) || (x.Title ?? "").Contains(search) || (x.TextContent ?? "").Contains(search))
                .OrderBy(x => x.SortKey).ToList());

        public Task<CollectionItem> CreateItemAsync(Guid workspaceId, CreateCollectionItem input, CancellationToken c = default)
        {
            RequireFolderIndex(workspaceId, input.FolderId);
            if (input.SourceClipboardItemId is { } sourceId)
            {
                var sourceWorkspace = input.SourceWorkspaceId ?? workspaceId;
                if (!_history.Any(x => x.Id == sourceId && x.WorkspaceId == sourceWorkspace && x.DeletedAt is null))
                    throw new InvalidOperationException("来源历史不存在或不属于来源空间。");
            }
            var now = DateTimeOffset.UtcNow;
            var item = new CollectionItem { Id = Guid.NewGuid(), WorkspaceId = workspaceId, FolderId = input.FolderId,
                SourceClipboardItemId = input.SourceClipboardItemId, ItemType = input.ItemType, Title = input.Title,
                TextContent = input.TextContent, Url = input.Url, Description = input.Description,
                LocalPath = input.LocalPath, ThumbnailPath = input.ThumbnailPath, FileManifest = input.FileManifest,
                ContentHash = input.ContentHash, ContentSize = input.ContentSize,
                SortKey = NextKey(workspaceId, input.FolderId), CreatedAt = now, UpdatedAt = now };
            _items.Add(item);
            return Task.FromResult(item);
        }

        public Task UpdateItemAsync(CollectionItem item, CancellationToken c = default)
        {
            Updated = item;
            var index = _items.FindIndex(x => x.WorkspaceId == item.WorkspaceId && x.Id == item.Id && x.DeletedAt is null);
            if (index >= 0) _items[index] = item with { UpdatedAt = DateTimeOffset.UtcNow };
            return Task.CompletedTask;
        }

        public Task MoveItemAsync(Guid workspaceId, Guid itemId, Guid targetFolderId, int targetIndex, CancellationToken c = default)
        {
            RequireFolderIndex(workspaceId, targetFolderId);
            var index = _items.FindIndex(x => x.WorkspaceId == workspaceId && x.Id == itemId && x.DeletedAt is null);
            if (index < 0) throw new InvalidOperationException("配置项不存在或不属于当前空间。");
            var keys = _items.Where(x => x.WorkspaceId == workspaceId && x.FolderId == targetFolderId && x.DeletedAt is null && x.Id != itemId)
                .OrderBy(x => x.SortKey).Select(x => x.SortKey).ToList();
            _items[index] = _items[index] with { FolderId = targetFolderId, SortKey = KeyAt(keys, targetIndex) };
            return Task.CompletedTask;
        }

        public Task DeleteItemAsync(Guid workspaceId, Guid itemId, CancellationToken c = default)
        {
            var index = _items.FindIndex(x => x.WorkspaceId == workspaceId && x.Id == itemId && x.DeletedAt is null);
            if (index >= 0) _items[index] = _items[index] with { DeletedAt = DateTimeOffset.UtcNow };
            return Task.CompletedTask;
        }

        private int RequireFolderIndex(Guid workspaceId, Guid folderId)
        {
            var index = _folders.FindIndex(x => x.WorkspaceId == workspaceId && x.Id == folderId && x.DeletedAt is null);
            if (index < 0) throw new InvalidOperationException("文件夹不存在或不属于当前空间。");
            return index;
        }

        private decimal NextKey(Guid workspaceId, Guid folderId) =>
            _items.Where(x => x.WorkspaceId == workspaceId && x.FolderId == folderId && x.DeletedAt is null)
                .Select(x => x.SortKey).DefaultIfEmpty(0m).Max() + 1024m;

        // Same contract as the SQLite repository: the index excludes the moved row.
        private static decimal KeyAt(List<decimal> keys, int targetIndex)
        {
            targetIndex = Math.Clamp(targetIndex, 0, keys.Count);
            var left = targetIndex > 0 ? keys[targetIndex - 1] : (decimal?)null;
            var right = targetIndex < keys.Count ? keys[targetIndex] : (decimal?)null;
            return left is null ? (right is null ? 1024m : right.Value - 1024m)
                : right is null ? left.Value + 1024m : (left.Value + right.Value) / 2m;
        }
    }
}
