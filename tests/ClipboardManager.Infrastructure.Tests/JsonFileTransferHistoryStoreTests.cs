using ClipboardManager.Domain.Models;
using ClipboardManager.Infrastructure.Paths;

namespace ClipboardManager.Infrastructure.Tests;

/// <summary>传输记录文件：写入后能原样读回；文件损坏时返回空列表而不是抛出。</summary>
public sealed class JsonFileTransferHistoryStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cm-transfers-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    [Fact]
    public void SavedRecords_RoundTrip()
    {
        var store = new JsonFileTransferHistoryStore(new AppPaths(_root));
        var record = new FileTransferSnapshot(Guid.NewGuid(), FileTransferDirection.Receive, "办公室电脑", "A1B2C3", "报告.pdf",
            1234, 1234, FileTransferState.Completed, null, @"C:\Downloads\报告.pdf", DateTimeOffset.Parse("2026-11-10T08:00:00Z"));

        store.Save([record]);

        Assert.Equal(record, Assert.Single(new JsonFileTransferHistoryStore(new AppPaths(_root)).Load()));
    }

    [Fact]
    public void CorruptFile_LoadsAsEmpty()
    {
        var store = new JsonFileTransferHistoryStore(new AppPaths(_root));
        Directory.CreateDirectory(_root);
        File.WriteAllText(store.FilePath, "{ not json");

        Assert.Empty(store.Load());
    }
}
