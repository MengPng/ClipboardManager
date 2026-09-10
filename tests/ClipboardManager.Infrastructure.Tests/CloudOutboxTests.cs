using ClipboardManager.Domain.Contracts;
using ClipboardManager.Domain.Models;
using ClipboardManager.Infrastructure.Data;
using ClipboardManager.Infrastructure.Paths;
using ClipboardManager.Infrastructure.Sync;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;

namespace ClipboardManager.Infrastructure.Tests;

public sealed class CloudOutboxTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "ClipboardManager.Outbox.Tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task RepeatedOfflineEdits_RebaseOnlyAfterAcknowledgement_AndDrainInOrder()
    {
        var factory = new SqliteConnectionFactory(new AppPaths(root));
        await new SqliteDatabaseInitializer(factory, NullLogger<SqliteDatabaseInitializer>.Instance).InitializeAsync();
        var mirror = new SqliteCloudMirrorStore(factory); var id = Guid.NewGuid();
        await mirror.EnsureMappingAsync(new(id, WorkspaceType.Personal, "cloud", WorkspaceRole.Owner, 1024, 0));
        var repo = new SqliteCollectionRepository(factory);
        var folder = await repo.CreateFolderAsync(id, "first");
        await repo.RenameFolderAsync(id, folder.Id, "second");
        await repo.RenameFolderAsync(id, folder.Id, "third");
        var remote = new RecordingRemote();
        var engine = new SqliteCloudSyncEngine(factory, remote, mirror);
        var result = await engine.SynchronizeAsync(id, SyncReason.UserRequested);
        Assert.True(result.Succeeded);
        Assert.Equal(new long[] { 0, 1, 2 }, remote.Operations.Select(x => x.BaseVersion));
        Assert.Equal(3, result.PushedCount);
        Assert.Equal("third", (await repo.GetFoldersAsync(id)).Single().Name);
    }

    [Fact]
    public async Task PullCannotOverwriteAnUnsentEditOrAdvanceItsCursor()
    {
        var factory = new SqliteConnectionFactory(new AppPaths(root));
        await new SqliteDatabaseInitializer(factory, NullLogger<SqliteDatabaseInitializer>.Instance).InitializeAsync();
        var mirror = new SqliteCloudMirrorStore(factory); var id = Guid.NewGuid();
        await mirror.EnsureMappingAsync(new(id, WorkspaceType.Personal, "cloud", WorkspaceRole.Owner, 1024, 0));
        var repo = new SqliteCollectionRepository(factory); var folder = await repo.CreateFolderAsync(id, "unsent");
        await Assert.ThrowsAsync<InvalidOperationException>(() => mirror.ApplyPullBatchAsync(id,
            [new("collection_folder", folder.Id, 2, "{\"name\":\"remote\",\"sortKey\":0}", false)], "20", DateTimeOffset.UtcNow));
        Assert.Equal("unsent", (await repo.GetFoldersAsync(id)).Single().Name);
        Assert.Null((await mirror.GetMappingAsync(id))!.RemoteCursor);
    }

    private sealed class RecordingRemote : IRemoteSyncProvider
    {
        public List<SyncOperation> Operations { get; } = [];
        public Task<PushResult> PushAsync(IReadOnlyList<SyncOperation> operations, CancellationToken cancellationToken = default)
        {
            Operations.AddRange(operations);
            return Task.FromResult(new PushResult(true, operations.ToDictionary(x => x.Id, x => x.BaseVersion + 1)));
        }
        public Task<PullResult> PullAsync(Guid workspaceId, string? cursor, CancellationToken cancellationToken = default) =>
            Task.FromResult(new PullResult(true, "3", []));
    }
    public void Dispose() { SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
}
