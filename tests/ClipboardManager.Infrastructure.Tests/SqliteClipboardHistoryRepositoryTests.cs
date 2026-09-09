using ClipboardManager.Domain.Models;
using ClipboardManager.Infrastructure.Data;
using ClipboardManager.Infrastructure.Paths;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClipboardManager.Infrastructure.Tests;

public sealed class SqliteClipboardHistoryRepositoryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ClipboardManager.Tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task CaptureText_MergesWithinWorkspaceAndPreservesFlags()
    {
        var (repository, workspaceId, deviceId) = await CreateRepositoryAsync();
        var first = await repository.CaptureTextAsync(Capture(workspaceId, deviceId, "hello", "hash", DateTimeOffset.UtcNow.AddMinutes(-1)));
        await repository.SetFavoriteAsync(workspaceId, first.Item!.Id, true);
        await repository.SetPinnedAsync(workspaceId, first.Item.Id, true);

        var second = await repository.CaptureTextAsync(Capture(workspaceId, deviceId, "hello", "hash", DateTimeOffset.UtcNow));
        var items = await repository.SearchAsync(new ClipboardQuery(workspaceId));

        Assert.Equal(CaptureDisposition.Merged, second.Disposition);
        Assert.Single(items);
        Assert.True(items[0].IsFavorite);
        Assert.True(items[0].IsPinned);
        Assert.Equal(4, items[0].LocalVersion);
    }

    [Fact]
    public async Task SearchAndDelete_AreWorkspaceIsolated()
    {
        var (repository, workspaceId, deviceId) = await CreateRepositoryAsync();
        var otherWorkspace = Guid.NewGuid();
        await RegisterWorkspaceAsync(otherWorkspace);
        var own = await repository.CaptureTextAsync(Capture(workspaceId, deviceId, "own", "own-hash", DateTimeOffset.UtcNow));
        await repository.CaptureTextAsync(Capture(otherWorkspace, deviceId, "other", "other-hash", DateTimeOffset.UtcNow));

        await repository.SoftDeleteAsync(otherWorkspace, own.Item!.Id, DateTimeOffset.UtcNow);
        Assert.Single(await repository.SearchAsync(new ClipboardQuery(workspaceId)));
        Assert.Single(await repository.SearchAsync(new ClipboardQuery(otherWorkspace)));

        await repository.SoftDeleteAsync(workspaceId, own.Item.Id, DateTimeOffset.UtcNow);
        Assert.Empty(await repository.SearchAsync(new ClipboardQuery(workspaceId)));
    }

    [Fact]
    public async Task ImageFilter_IncludesBitmapAndImageFile_ButFileFilterExcludesThem()
    {
        var (repository, workspaceId, deviceId) = await CreateRepositoryAsync();
        var now = DateTimeOffset.UtcNow;
        ClipboardItem Item(ClipboardItemType type, string hash) => new()
        {
            Id = Guid.NewGuid(), WorkspaceId = workspaceId, DeviceId = deviceId,
            ItemType = type, ContentHash = hash, FileManifest = type == ClipboardItemType.ImageFile ? "[\"C:\\\\photo.png\"]" : type == ClipboardItemType.Files ? "[\"C:\\\\report.xlsx\"]" : null,
            ImagePath = type == ClipboardItemType.Image ? "managed.png" : null,
            CreatedAt = now, UpdatedAt = now, CapturedAt = now
        };
        await repository.UpsertAsync(Item(ClipboardItemType.Image, "bitmap"));
        await repository.UpsertAsync(Item(ClipboardItemType.ImageFile, "image-file"));
        await repository.UpsertAsync(Item(ClipboardItemType.Files, "document"));

        var images = await repository.SearchAsync(new ClipboardQuery(workspaceId, ItemType: ClipboardItemType.Image));
        var files = await repository.SearchAsync(new ClipboardQuery(workspaceId, ItemType: ClipboardItemType.Files));

        Assert.Equal(2, images.Count);
        Assert.All(images, item => Assert.Contains(item.ItemType, new[] { ClipboardItemType.Image, ClipboardItemType.ImageFile }));
        Assert.Single(files);
        Assert.Equal(ClipboardItemType.Files, files[0].ItemType);
    }

    [Fact]
    public async Task Cleanup_PreservesFavoriteAndPinnedItems()
    {
        var (repository, workspaceId, deviceId) = await CreateRepositoryAsync();
        var old = DateTimeOffset.UtcNow.AddDays(-60);
        var normal = await repository.CaptureTextAsync(Capture(workspaceId, deviceId, "normal", "n", old));
        var favorite = await repository.CaptureTextAsync(Capture(workspaceId, deviceId, "favorite", "f", old));
        var pinned = await repository.CaptureTextAsync(Capture(workspaceId, deviceId, "pinned", "p", old));
        await repository.SetFavoriteAsync(workspaceId, favorite.Item!.Id, true);
        await repository.SetPinnedAsync(workspaceId, pinned.Item!.Id, true);

        await repository.CleanupAsync(workspaceId, DateTimeOffset.UtcNow.AddDays(-30), 1000, DateTimeOffset.UtcNow);
        var items = await repository.SearchAsync(new ClipboardQuery(workspaceId));

        Assert.DoesNotContain(items, item => item.Id == normal.Item!.Id);
        Assert.Contains(items, item => item.Id == favorite.Item.Id);
        Assert.Contains(items, item => item.Id == pinned.Item.Id);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private async Task<(SqliteClipboardHistoryRepository Repository, Guid WorkspaceId, Guid DeviceId)> CreateRepositoryAsync()
    {
        var paths = new AppPaths(_root);
        var factory = new SqliteConnectionFactory(paths);
        await new SqliteDatabaseInitializer(factory, NullLogger<SqliteDatabaseInitializer>.Instance).InitializeAsync();
        var workspaceId = Guid.NewGuid();
        await RegisterWorkspaceAsync(workspaceId, factory);
        return (new SqliteClipboardHistoryRepository(factory), workspaceId, Guid.NewGuid());
    }

    private async Task RegisterWorkspaceAsync(Guid workspaceId, SqliteConnectionFactory? suppliedFactory = null)
    {
        var factory = suppliedFactory ?? new SqliteConnectionFactory(new AppPaths(_root));
        await using var connection = factory.Create();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT OR IGNORE INTO workspaces (id, workspace_type, name, created_at, updated_at) VALUES ($id, 0, 'test', $now, $now);";
        command.Parameters.AddWithValue("$id", workspaceId.ToString("D"));
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync();
    }

    private static TextCapture Capture(Guid workspaceId, Guid deviceId, string text, string hash, DateTimeOffset time) =>
        new(workspaceId, null, deviceId, text, text, hash, "Test", "test", time, text.Length);
}
