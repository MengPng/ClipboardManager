using System.IO.Compression;
using ClipboardManager.Domain.Models;
using ClipboardManager.Infrastructure.Data;
using ClipboardManager.Infrastructure.Paths;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClipboardManager.Infrastructure.Tests;

public sealed class StorageMaintenanceServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ClipboardManager.Storage.Tests", Guid.NewGuid().ToString("N"));
    private AppPaths Paths => new(_root);

    [Fact]
    public async Task BackupRestore_RoundTripsConsistentDatabaseAndManagedFiles()
    {
        var service = await CreateAsync();
        await InsertClipboardAsync("before", null, null);
        Directory.CreateDirectory(Paths.BlobsPath);
        await File.WriteAllTextAsync(Path.Combine(Paths.BlobsPath, "a.bin"), "asset-before");
        await File.WriteAllTextAsync(Paths.SettingsPath, "settings-before");
        var backup = Path.Combine(_root, "backup.zip");

        await service.CreateBackupAsync(backup);
        Assert.True((await service.ValidateBackupAsync(backup)).IsValid);

        await ExecuteAsync("UPDATE clipboard_items SET text_content='after';");
        await File.WriteAllTextAsync(Path.Combine(Paths.BlobsPath, "a.bin"), "asset-after");
        await File.WriteAllTextAsync(Paths.SettingsPath, "settings-after");
        await service.RestoreAsync(backup);

        Assert.Equal("before", await ScalarStringAsync("SELECT text_content FROM clipboard_items LIMIT 1;"));
        Assert.Equal("asset-before", await File.ReadAllTextAsync(Path.Combine(Paths.BlobsPath, "a.bin")));
        Assert.Equal("settings-before", await File.ReadAllTextAsync(Paths.SettingsPath));
    }

    [Fact]
    public async Task CorruptArchive_IsRejectedWithoutLiveChanges()
    {
        var service = await CreateAsync();
        await InsertClipboardAsync("live", null, null);
        var backup = Path.Combine(_root, "corrupt.zip");
        await File.WriteAllTextAsync(backup, "not a zip");

        Assert.False((await service.ValidateBackupAsync(backup)).IsValid);
        await Assert.ThrowsAnyAsync<Exception>(() => service.RestoreAsync(backup));
        Assert.Equal("live", await ScalarStringAsync("SELECT text_content FROM clipboard_items LIMIT 1;"));
    }

    [Fact]
    public async Task ZipTraversal_IsRejectedWithoutWritingOutsideExtractionRoot()
    {
        var service = await CreateAsync();
        var backup = Path.Combine(_root, "traversal.zip");
        using (var archive = ZipFile.Open(backup, ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry("../evil.txt");
            await using var writer = new StreamWriter(entry.Open());
            await writer.WriteAsync("evil");
        }
        var result = await service.ValidateBackupAsync(backup);
        Assert.False(result.IsValid);
        Assert.False(File.Exists(Path.Combine(_root, "evil.txt")));
    }

    [Fact]
    public async Task Cleanup_PreservesReferencedCollectionAssetsAndDeletesOrphans()
    {
        var service = await CreateAsync();
        var referenced = Path.Combine(Paths.BlobsPath, "kept.png");
        var thumb = Path.Combine(Paths.ThumbnailsPath, "kept.jpg");
        var orphan = Path.Combine(Paths.BlobsPath, "orphan.bin");
        Directory.CreateDirectory(Paths.BlobsPath);
        Directory.CreateDirectory(Paths.ThumbnailsPath);
        await File.WriteAllTextAsync(referenced, "keep");
        await File.WriteAllTextAsync(thumb, "thumb");
        await File.WriteAllTextAsync(orphan, "delete");
        await InsertCollectionAsync(referenced, thumb);

        var preview = await service.PreviewOrphanedAssetsAsync();
        Assert.Equal(new[] { "blobs/orphan.bin" }, preview.RelativePaths);
        Assert.True(File.Exists(orphan));
        var result = await service.CleanupOrphanedAssetsAsync(preview);

        Assert.Equal(1, result.DeletedFileCount);
        Assert.False(File.Exists(orphan));
        Assert.True(File.Exists(referenced));
        Assert.True(File.Exists(thumb));
    }

    [Fact]
    public async Task Cleanup_DeletedHistoryAndDanglingBlobBecomeOrphansUnlessActiveCollectionReferencesThem()
    {
        var service = await CreateAsync();
        var orphanImage = Path.Combine(Paths.BlobsPath, "deleted.png");
        var orphanThumb = Path.Combine(Paths.ThumbnailsPath, "deleted.jpg");
        var collectionImage = Path.Combine(Paths.BlobsPath, "collection.png");
        var collectionThumb = Path.Combine(Paths.ThumbnailsPath, "collection.jpg");
        Directory.CreateDirectory(Paths.BlobsPath);
        Directory.CreateDirectory(Paths.ThumbnailsPath);
        foreach (var file in new[] { orphanImage, orphanThumb, collectionImage, collectionThumb })
            await File.WriteAllTextAsync(file, "asset");

        var deletedItemId = await InsertClipboardAsync("deleted", orphanImage, orphanThumb);
        await ExecuteAsync("UPDATE clipboard_items SET deleted_at=$now WHERE id=$id;", ("$now", DateTimeOffset.UtcNow.ToString("O")), ("$id", deletedItemId));
        await ExecuteAsync("""
            INSERT INTO blobs(id,clipboard_item_id,content_hash,mime_type,byte_size,local_path,thumbnail_path,upload_status,created_at)
            VALUES($id,$clipboard,'hash','image/png',5,$local,$thumb,'local',$now);
            """, ("$id", Guid.NewGuid().ToString()), ("$clipboard", deletedItemId), ("$local", orphanImage), ("$thumb", orphanThumb), ("$now", DateTimeOffset.UtcNow.ToString("O")));
        await InsertCollectionAsync(collectionImage, collectionThumb);

        var preview = await service.PreviewOrphanedAssetsAsync();

        Assert.Contains("blobs/deleted.png", preview.RelativePaths);
        Assert.Contains("thumbnails/deleted.jpg", preview.RelativePaths);
        Assert.DoesNotContain("blobs/collection.png", preview.RelativePaths);
        Assert.DoesNotContain("thumbnails/collection.jpg", preview.RelativePaths);
    }

    [Fact]
    public async Task UsageStats_ReportsDatabaseBlobsAndThumbnails()
    {
        var service = await CreateAsync();
        Directory.CreateDirectory(Paths.BlobsPath);
        Directory.CreateDirectory(Paths.ThumbnailsPath);
        await File.WriteAllBytesAsync(Path.Combine(Paths.BlobsPath, "b"), new byte[7]);
        await File.WriteAllBytesAsync(Path.Combine(Paths.ThumbnailsPath, "t"), new byte[11]);

        var stats = await service.GetUsageStatsAsync();

        Assert.True(stats.DatabaseBytes > 0);
        Assert.Equal(7, stats.BlobBytes);
        Assert.Equal(11, stats.ThumbnailBytes);
        Assert.Equal(1, stats.BlobFileCount);
        Assert.Equal(1, stats.ThumbnailFileCount);
        Assert.Equal(stats.DatabaseBytes + 18, stats.TotalBytes);
    }

    private async Task<StorageMaintenanceService> CreateAsync()
    {
        var paths = Paths;
        var factory = new SqliteConnectionFactory(paths);
        await new SqliteDatabaseInitializer(factory, NullLogger<SqliteDatabaseInitializer>.Instance).InitializeAsync();
        return new StorageMaintenanceService(paths, factory, NullLogger<StorageMaintenanceService>.Instance);
    }

    private async Task<string> InsertClipboardAsync(string text, string? image, string? thumbnail)
    {
        var workspace = Guid.NewGuid().ToString();
        var id = Guid.NewGuid().ToString();
        await ExecuteAsync("INSERT INTO workspaces(id,workspace_type,name,created_at,updated_at) VALUES($w,0,'local',$now,$now);", ("$w", workspace), ("$now", DateTimeOffset.UtcNow.ToString("O")));
        await ExecuteAsync("""
            INSERT INTO clipboard_items(id,workspace_id,device_id,item_type,content_hash,text_content,created_at,updated_at,captured_at,image_path,thumbnail_path)
            VALUES($id,$w,'device',0,'hash',$text,$now,$now,$now,$image,$thumb);
            """, ("$id", id), ("$w", workspace), ("$text", text), ("$now", DateTimeOffset.UtcNow.ToString("O")), ("$image", image), ("$thumb", thumbnail));
        return id;
    }

    private async Task InsertCollectionAsync(string localPath, string thumbnailPath)
    {
        var workspace = Guid.NewGuid().ToString();
        var folder = Guid.NewGuid().ToString();
        var now = DateTimeOffset.UtcNow.ToString("O");
        await ExecuteAsync("INSERT INTO workspaces(id,workspace_type,name,created_at,updated_at) VALUES($w,0,'local',$now,$now);", ("$w", workspace), ("$now", now));
        await ExecuteAsync("INSERT INTO collection_folders(id,workspace_id,name,sort_key,created_at,updated_at) VALUES($f,$w,'folder',0,$now,$now);", ("$f", folder), ("$w", workspace), ("$now", now));
        await ExecuteAsync("""
            INSERT INTO collection_items(id,workspace_id,folder_id,item_type,local_path,thumbnail_path,sort_key,created_at,updated_at)
            VALUES($id,$w,$f,2,$local,$thumb,0,$now,$now);
            """, ("$id", Guid.NewGuid().ToString()), ("$w", workspace), ("$f", folder), ("$local", localPath), ("$thumb", thumbnailPath), ("$now", now));
    }

    private async Task ExecuteAsync(string sql, params (string Name, object? Value)[] parameters)
    {
        var factory = new SqliteConnectionFactory(Paths);
        await using var connection = factory.Create();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value);
        await command.ExecuteNonQueryAsync();
    }

    private async Task<string?> ScalarStringAsync(string sql)
    {
        var factory = new SqliteConnectionFactory(Paths);
        await using var connection = factory.Create();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(await command.ExecuteScalarAsync());
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
