using System.Security.Cryptography;
using ClipboardManager.Domain.Models;

namespace ClipboardManager.Infrastructure.Tests;

/// <summary>
/// 局域网文件传输（ADR-0008）经真实自签 TLS 回环：匹配 → 请求 → 确认 → 上传 → 校验 → 落盘。
/// 未覆盖：组播发现、真实防火墙、跨机器网络与 2–3 GB 级别的真实大文件。
/// </summary>
public sealed class LanTransferLoopbackTests : IAsyncLifetime
{
    private ConfigSyncTestHost _receiver = null!;
    private ConfigSyncTestHost _sender = null!;

    public async Task InitializeAsync()
    {
        _receiver = await ConfigSyncTestHost.CreateAsync("receiver");
        _sender = await ConfigSyncTestHost.CreateAsync("sender");
        await _receiver.StartServerAsync();
    }

    public async Task DisposeAsync()
    {
        await _receiver.DisposeAsync();
        await _sender.DisposeAsync();
    }

    [Fact]
    public async Task MatchedDevice_ReceivesTheFileIntact_AfterConfirmation()
    {
        await MatchAsync();
        var (path, content) = await SourceFileAsync("报告 final.bin", 3 * 1024 * 1024 + 17);

        var summary = await _sender.Transfers.SendFilesAsync(_receiver.Fingerprint, [path]);

        Assert.Equal(new FileSendSummary(1, 0, 0, null), summary);
        var saved = Path.Combine(_receiver.ReceiveFolder, "报告 final.bin");
        Assert.Equal(content, await File.ReadAllBytesAsync(saved));
        var request = Assert.Single(_receiver.Prompt.Requests);
        Assert.Equal("报告 final.bin", request.FileName);
        Assert.Equal(content.Length, request.Size);
        Assert.False(request.IsHighRisk);
        Assert.Empty(PartFiles());

        var sent = Assert.Single(_sender.Tracker.GetAll());
        Assert.Equal(FileTransferState.Completed, sent.State);
        Assert.Equal(FileTransferDirection.Send, sent.Direction);
        Assert.Equal(_receiver.Fingerprint.ToUpperInvariant(), sent.PeerFingerprint);
        var received = Assert.Single(_receiver.Tracker.GetAll());
        Assert.Equal(FileTransferState.Completed, received.State);
        Assert.Equal(saved, received.SavedPath);
        Assert.Equal(_sender.Fingerprint.ToUpperInvariant(), received.PeerFingerprint);
    }

    [Fact]
    public async Task SameNameTwice_IsSavedUnderANewName()
    {
        await MatchAsync();
        var (first, _) = await SourceFileAsync("a.txt", 100);
        var (second, secondContent) = await SourceFileAsync("a.txt", 200);

        await _sender.Transfers.SendFilesAsync(_receiver.Fingerprint, [first]);
        await _sender.Transfers.SendFilesAsync(_receiver.Fingerprint, [second]);

        Assert.Equal(secondContent, await File.ReadAllBytesAsync(Path.Combine(_receiver.ReceiveFolder, "a (1).txt")));
    }

    [Fact]
    public async Task ExistingPart_IsResumedInsteadOfResent()
    {
        await MatchAsync();
        var (path, content) = await SourceFileAsync("movie.bin", 2 * 1024 * 1024);
        // 上一次只收到前一半（内容正确）。
        await SeedPartAsync(content, content.AsSpan(0, content.Length / 2).ToArray());

        var summary = await _sender.Transfers.SendFilesAsync(_receiver.Fingerprint, [path]);

        Assert.Equal(1, summary.Sent);
        Assert.Equal(content, await File.ReadAllBytesAsync(Path.Combine(_receiver.ReceiveFolder, "movie.bin")));
        Assert.Empty(PartFiles());
    }

    [Fact]
    public async Task CorruptPart_FailsVerificationAndIsDiscarded_ThenTheNextSendSucceeds()
    {
        await MatchAsync();
        var (path, content) = await SourceFileAsync("disk.img", 1024 * 1024);
        // 已有部分被篡改：续传后整文件哈希不一致，必须丢弃而不是落盘。
        await SeedPartAsync(content, Enumerable.Repeat((byte)0xEE, content.Length / 2).ToArray());

        var summary = await _sender.Transfers.SendFilesAsync(_receiver.Fingerprint, [path]);

        Assert.Equal(0, summary.Sent);
        Assert.Equal(1, summary.Failed);
        Assert.Contains("校验", summary.LastError);
        Assert.False(File.Exists(Path.Combine(_receiver.ReceiveFolder, "disk.img")));
        Assert.Empty(PartFiles());
        Assert.Equal(FileTransferState.Failed, Assert.Single(_sender.Tracker.GetAll()).State);

        var retry = await _sender.Transfers.SendFilesAsync(_receiver.Fingerprint, [path]);
        Assert.Equal(1, retry.Sent);
        Assert.Equal(content, await File.ReadAllBytesAsync(Path.Combine(_receiver.ReceiveFolder, "disk.img")));
    }

    [Fact]
    public async Task DeclinedOffer_SavesNothing()
    {
        await MatchAsync();
        _receiver.Prompt.Answer = false;
        var (path, _) = await SourceFileAsync("x.zip", 4096);

        var summary = await _sender.Transfers.SendFilesAsync(_receiver.Fingerprint, [path]);

        Assert.Equal(1, summary.Failed);
        Assert.Contains("拒绝", summary.LastError);
        Assert.Equal(FileTransferState.Declined, Assert.Single(_sender.Tracker.GetAll()).State);
        Assert.False(Directory.Exists(_receiver.ReceiveFolder) && Directory.EnumerateFileSystemEntries(_receiver.ReceiveFolder).Any());
    }

    [Fact]
    public async Task ReceiveDisabled_IsRefusedWithoutAsking()
    {
        await MatchAsync();
        _receiver.Settings.Current = _receiver.Settings.Current with { LanReceiveEnabled = false };
        var (path, _) = await SourceFileAsync("x.zip", 4096);

        var summary = await _sender.Transfers.SendFilesAsync(_receiver.Fingerprint, [path]);

        Assert.Equal(1, summary.Failed);
        Assert.Contains("接收文件", summary.LastError);
        Assert.Empty(_receiver.Prompt.Requests);
    }

    [Fact]
    public async Task AutoAccept_SkipsTheConfirmation_AndExecutablesAreFlaggedWhenAsked()
    {
        await MatchAsync();
        var (setup, _) = await SourceFileAsync("setup.exe", 2048);

        await _sender.Transfers.SendFilesAsync(_receiver.Fingerprint, [setup]);
        Assert.True(Assert.Single(_receiver.Prompt.Requests).IsHighRisk);

        _receiver.Settings.Current = _receiver.Settings.Current with { LanAutoAccept = true };
        var (notes, notesContent) = await SourceFileAsync("notes.txt", 512);
        var summary = await _sender.Transfers.SendFilesAsync(_receiver.Fingerprint, [notes]);

        Assert.Equal(1, summary.Sent);
        Assert.Single(_receiver.Prompt.Requests);
        Assert.Equal(notesContent, await File.ReadAllBytesAsync(Path.Combine(_receiver.ReceiveFolder, "notes.txt")));
    }

    [Fact]
    public async Task RemovedMatch_IsRefusedByTheReceiver()
    {
        await MatchAsync();
        foreach (var role in new[] { PeerRole.Publisher, PeerRole.Subscriber })
        foreach (var peer in await _receiver.Repository.GetPeersAsync(role))
            await _receiver.Repository.RevokePeerAsync(peer.Id);
        var (path, _) = await SourceFileAsync("x.bin", 4096);

        var summary = await _sender.Transfers.SendFilesAsync(_receiver.Fingerprint, [path]);

        Assert.Equal(1, summary.Failed);
        Assert.Empty(_receiver.Prompt.Requests);
        Assert.False(Directory.Exists(_receiver.ReceiveFolder) && Directory.EnumerateFiles(_receiver.ReceiveFolder).Any());
    }

    [Fact]
    public async Task UnknownDevice_IsRefusedLocally()
    {
        var (path, _) = await SourceFileAsync("x.bin", 16);
        var error = await Assert.ThrowsAsync<FileTransferException>(() =>
            _sender.Transfers.SendFilesAsync(_receiver.Fingerprint, [path]));
        Assert.Equal(FileTransferError.NotMatched, error.Error);
    }

    [Fact]
    public async Task MissingPaths_AreSkipped()
    {
        await MatchAsync();
        var (path, _) = await SourceFileAsync("ok.txt", 64);
        var folder = Path.GetDirectoryName(path)!;

        var summary = await _sender.Transfers.SendFilesAsync(_receiver.Fingerprint,
            [path, Path.Combine(folder, "gone.txt")]);

        Assert.Equal(new FileSendSummary(1, 0, 1, null), summary);
    }

    [Fact]
    public async Task Folder_IsSentAsOneZip_WithItsStructure_AndTheTemporaryZipIsRemoved()
    {
        await MatchAsync();
        var folder = Path.Combine(_sender.Root, "outbox", Guid.NewGuid().ToString("N"), "项目资料");
        Directory.CreateDirectory(Path.Combine(folder, "图片"));
        var top = RandomNumberGenerator.GetBytes(4096);
        var nested = RandomNumberGenerator.GetBytes(200_000);
        await File.WriteAllBytesAsync(Path.Combine(folder, "说明.txt"), top);
        await File.WriteAllBytesAsync(Path.Combine(folder, "图片", "a.png"), nested);

        var summary = await _sender.Transfers.SendFilesAsync(_receiver.Fingerprint, [folder]);

        Assert.Equal(new FileSendSummary(1, 0, 0, null), summary);
        var saved = Path.Combine(_receiver.ReceiveFolder, "项目资料.zip");
        using (var archive = System.IO.Compression.ZipFile.OpenRead(saved))
        {
            Assert.Equal(["图片/a.png", "说明.txt"], archive.Entries.Select(entry => entry.FullName).Order(StringComparer.Ordinal));
            await using var stream = archive.GetEntry("图片/a.png")!.Open();
            using var copy = new MemoryStream();
            await stream.CopyToAsync(copy);
            Assert.Equal(nested, copy.ToArray());
        }
        var sent = Assert.Single(_sender.Tracker.GetAll());
        Assert.Equal("项目资料.zip", sent.FileName);
        Assert.Equal(FileTransferState.Completed, sent.State);
        Assert.Equal(new FileInfo(saved).Length, sent.Size);
    }

    [Fact]
    public async Task MatchingFromTheSender_LetsTheReceiverSendBack()
    {
        // 发起匹配的一方也开着监听并报告端口：对方随即知道地址，可以反向发文件。
        await _sender.StartServerAsync();
        await _sender.Subscriptions.PairAsync(_receiver.Address, ConfigSyncTestHost.MatchCode, _sender.Server.Status.Port);
        var (path, content) = await SourceFileAsync("reply.txt", 1000, _receiver);

        var summary = await _receiver.Transfers.SendFilesAsync(_sender.Fingerprint, [path]);

        Assert.Equal(1, summary.Sent);
        Assert.Equal(content, await File.ReadAllBytesAsync(Path.Combine(_sender.ReceiveFolder, "reply.txt")));
    }

    private Task MatchAsync() => _sender.Subscriptions.PairAsync(_receiver.Address, ConfigSyncTestHost.MatchCode);

    private async Task<(string Path, byte[] Content)> SourceFileAsync(string name, int size, ConfigSyncTestHost? owner = null)
    {
        var content = RandomNumberGenerator.GetBytes(size);
        var path = Path.Combine((owner ?? _sender).Root, "outbox", Guid.NewGuid().ToString("N"), name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, content);
        return (path, content);
    }

    private async Task SeedPartAsync(byte[] whole, byte[] prefix)
    {
        Directory.CreateDirectory(_receiver.ReceiveFolder);
        var hash = Convert.ToHexString(SHA256.HashData(whole));
        var part = Path.Combine(_receiver.ReceiveFolder,
            FileTransferPolicy.PartFileName(_sender.Fingerprint.ToUpperInvariant(), hash, whole.Length));
        await File.WriteAllBytesAsync(part, prefix);
    }

    private IEnumerable<string> PartFiles() =>
        Directory.Exists(_receiver.ReceiveFolder)
            ? Directory.EnumerateFiles(_receiver.ReceiveFolder, FileTransferPolicy.PartPrefix + "*")
            : [];
}
