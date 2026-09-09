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
        var now=DateTimeOffset.UtcNow;
        var history=new ClipboardItem{Id=Guid.NewGuid(),WorkspaceId=WorkspaceService.Id,DeviceId=Guid.NewGuid(),ItemType=ClipboardItemType.Text,ContentHash="h",TextContent="hello",PreviewText="hello",CreatedAt=now,UpdatedAt=now,CapturedAt=now};
        var saved=await service.AddSnapshotAsync(Guid.NewGuid(),history);
        Assert.Equal("hello",saved.TextContent);
        Assert.Equal(history.Id,saved.SourceClipboardItemId);
    }

    [Fact]
    public async Task UpdateContent_ValidatesWorkspaceAndLinkAndPreservesText()
    {
        var repository = new FakeCollectionRepository();
        var service = new CollectionService(repository, new WorkspaceService(), new AssetStore());
        var now = DateTimeOffset.UtcNow;
        var item = new CollectionItem { Id = Guid.NewGuid(), WorkspaceId = WorkspaceService.Id,
            FolderId = Guid.NewGuid(), ItemType = CollectionItemType.Text, SortKey = 0, CreatedAt = now, UpdatedAt = now };
        await service.UpdateContentAsync(item, " 标题 ", "第一行\r\n第二行");
        Assert.Equal("第一行\r\n第二行", repository.Updated!.TextContent);
        Assert.Equal("标题", repository.Updated.Title);
        Assert.Equal(TextContentService.ComputeHash("第一行\n第二行"), repository.Updated.ContentHash);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.UpdateContentAsync(item with { WorkspaceId = Guid.NewGuid() }, "", "text"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.UpdateContentAsync(item with { DeletedAt = now }, "", "text"));
        await Assert.ThrowsAsync<ArgumentException>(() => service.UpdateContentAsync(item with { ItemType = CollectionItemType.Link }, "", "file:///C:/test"));
        await service.UpdateContentAsync(item with { ItemType = CollectionItemType.Link }, "链接", "https://example.com");
        Assert.Equal("https://example.com/", repository.Updated!.Url);
        Assert.Null(repository.Updated.TextContent);
    }

    [Fact]
    public async Task SnapshotFromDifferentWorkspaceOrDeletedHistory_IsRejected()
    {
        var service = new CollectionService(new FakeCollectionRepository(), new WorkspaceService(), new AssetStore());
        var now = DateTimeOffset.UtcNow;
        var item = new ClipboardItem { Id = Guid.NewGuid(), WorkspaceId = Guid.NewGuid(), DeviceId = Guid.NewGuid(),
            ItemType = ClipboardItemType.Text, ContentHash = "h", TextContent = "x", CreatedAt = now, UpdatedAt = now, CapturedAt = now };
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.AddSnapshotAsync(Guid.NewGuid(), item));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.AddSnapshotAsync(Guid.NewGuid(), item with { WorkspaceId = WorkspaceService.Id, DeletedAt = now }));
    }

    private sealed class WorkspaceService:IWorkspaceService{public static readonly Guid Id=Guid.NewGuid();private readonly Workspace _w=new(Id,WorkspaceType.Local,"L",null,DateTimeOffset.UtcNow,DateTimeOffset.UtcNow);public Task<IReadOnlyList<Workspace>> GetAvailableAsync(CancellationToken c=default)=>Task.FromResult<IReadOnlyList<Workspace>>([_w]);public Task<Workspace> GetCurrentAsync(CancellationToken c=default)=>Task.FromResult(_w);public Task SwitchAsync(Guid id,CancellationToken c=default)=>Task.CompletedTask;}
    private sealed class AssetStore:ILocalAssetStore{public Task<StoredAsset> StoreImageAsync(byte[] b,string m,CancellationToken c=default)=>throw new NotSupportedException();public Task<StoredAsset> ImportImageAsync(string p,CancellationToken c=default)=>throw new NotSupportedException();}
    private sealed class FakeCollectionRepository:ICollectionRepository
    {
        public Task<CollectionItem> CreateItemAsync(Guid w,CreateCollectionItem i,CancellationToken c=default){var now=DateTimeOffset.UtcNow;return Task.FromResult(new CollectionItem{Id=Guid.NewGuid(),WorkspaceId=w,FolderId=i.FolderId,SourceClipboardItemId=i.SourceClipboardItemId,ItemType=i.ItemType,Title=i.Title,TextContent=i.TextContent,Url=i.Url,Description=i.Description,LocalPath=i.LocalPath,ThumbnailPath=i.ThumbnailPath,FileManifest=i.FileManifest,ContentHash=i.ContentHash,ContentSize=i.ContentSize,SortKey=0,CreatedAt=now,UpdatedAt=now});}
        public Task<CollectionFolder> EnsureUncategorizedAsync(Guid w,CancellationToken c=default)=>throw new NotSupportedException();public Task<IReadOnlyList<CollectionFolder>> GetFoldersAsync(Guid w,CancellationToken c=default)=>throw new NotSupportedException();public Task<CollectionFolder> CreateFolderAsync(Guid w,string n,CancellationToken c=default)=>throw new NotSupportedException();public Task RenameFolderAsync(Guid w,Guid f,string n,CancellationToken c=default)=>throw new NotSupportedException();public Task DeleteFolderAsync(Guid w,Guid f,Guid b,CancellationToken c=default)=>throw new NotSupportedException();public Task ReorderFolderAsync(Guid w,Guid f,int i,CancellationToken c=default)=>throw new NotSupportedException();public Task<IReadOnlyList<CollectionItem>> GetItemsAsync(Guid w,Guid f,string? s=null,CancellationToken c=default)=>throw new NotSupportedException();public CollectionItem? Updated { get; private set; } public Task UpdateItemAsync(CollectionItem i,CancellationToken c=default){Updated=i;return Task.CompletedTask;}public Task MoveItemAsync(Guid w,Guid i,Guid f,int t,CancellationToken c=default)=>throw new NotSupportedException();public Task DeleteItemAsync(Guid w,Guid i,CancellationToken c=default)=>throw new NotSupportedException();
    }
}
