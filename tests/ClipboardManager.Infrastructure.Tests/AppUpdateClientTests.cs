using System.Net;
using System.Security.Cryptography;
using System.Text;
using ClipboardManager.Domain.Models;
using ClipboardManager.Infrastructure.Paths;
using ClipboardManager.Infrastructure.Updates;

namespace ClipboardManager.Infrastructure.Tests;

public sealed class AppUpdateClientTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ClipboardManager.Tests", Guid.NewGuid().ToString("N"));
    private static readonly Uri FileUrl = new("https://clipboard.netkaize.com/download/files/0.9.4/ClipboardManager-0.9.4-win-x64.exe");

    private (HttpAppUpdateClient Client, StubHandler Handler) Create(Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        var handler = new StubHandler(respond);
        return (new HttpAppUpdateClient(new HttpClient(handler), new AppPaths(_root)), handler);
    }

    private static AppUpdateFile File(byte[] content, string? sha = null, long? size = null) =>
        new("exe", "ClipboardManager-0.9.4-win-x64.exe", FileUrl, size ?? content.Length,
            sha ?? Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant());

    private static HttpResponseMessage Bytes(byte[] content) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(content) };

    [Fact]
    public async Task ManifestRequestBypassesCachesAndCarriesNoBody()
    {
        var (client, handler) = Create(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"schema\":1}") });

        var json = await client.FetchManifestAsync();

        Assert.Equal("{\"schema\":1}", json);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal(HttpAppUpdateClient.DefaultManifestUrl, request.RequestUri);
        Assert.True(request.Headers.CacheControl?.NoCache);
        Assert.Null(request.Content);
        Assert.Null(request.Headers.Authorization);
    }

    [Fact]
    public async Task OversizedManifestIsRejected()
    {
        var huge = new string(' ', HttpAppUpdateClient.MaxManifestBytes + 10);
        var (client, _) = Create(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(huge) });
        await Assert.ThrowsAsync<InvalidDataException>(() => client.FetchManifestAsync());
    }

    [Fact]
    public async Task ServerErrorSurfacesAsHttpException()
    {
        var (client, _) = Create(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        await Assert.ThrowsAsync<HttpRequestException>(() => client.FetchManifestAsync());
    }

    [Fact]
    public async Task DownloadVerifiesHashAndReportsProgress()
    {
        var content = Encoding.UTF8.GetBytes(new string('x', 300_000));
        var (client, _) = Create(_ => Bytes(content));
        var reported = new List<double>();

        var path = await client.DownloadAsync(File(content), new Version(0, 9, 4), new SyncProgress(reported.Add));

        Assert.Equal(Path.Combine(_root, "updates", "0.9.4", "ClipboardManager-0.9.4-win-x64.exe"), path);
        Assert.Equal(content, await System.IO.File.ReadAllBytesAsync(path));
        Assert.Equal(1, reported[^1]);
        Assert.False(System.IO.File.Exists(path + ".partial"));
    }

    [Fact]
    public async Task HashMismatchDeletesTheDownload()
    {
        var content = Encoding.UTF8.GetBytes("tampered");
        var (client, _) = Create(_ => Bytes(content));
        var expected = File(content, sha: new string('0', 64));

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => client.DownloadAsync(expected, new Version(0, 9, 4), null));

        Assert.Contains("SHA-256", error.Message);
        var directory = Path.Combine(_root, "updates", "0.9.4");
        Assert.Empty(Directory.EnumerateFiles(directory));
    }

    [Fact]
    public async Task LongerOrShorterBodyThanDeclaredIsRejected()
    {
        var content = Encoding.UTF8.GetBytes("0123456789");
        var (longer, _) = Create(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new MemoryStream(content)) });
        await Assert.ThrowsAsync<InvalidDataException>(() => longer.DownloadAsync(File(content[..5], size: 5), new Version(0, 9, 4), null));

        var (shorter, _) = Create(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new MemoryStream(content)) });
        await Assert.ThrowsAsync<InvalidDataException>(() => shorter.DownloadAsync(File(content, size: 20), new Version(0, 9, 4), null));
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(_root, "updates", "0.9.4")));
    }

    [Fact]
    public async Task InterruptedDownloadLeavesNoPartialFile()
    {
        var content = new byte[200_000];
        var (client, _) = Create(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new FailingStream(content, 100_000)) });

        await Assert.ThrowsAsync<IOException>(() => client.DownloadAsync(File(content), new Version(0, 9, 4), null));

        Assert.Empty(Directory.EnumerateFiles(Path.Combine(_root, "updates", "0.9.4")));
    }

    [Fact]
    public async Task VerifiedDownloadIsReusedAndOtherVersionsAreCleaned()
    {
        var content = Encoding.UTF8.GetBytes("new build");
        var (client, handler) = Create(_ => Bytes(content));
        var stale = Path.Combine(_root, "updates", "0.9.3");
        Directory.CreateDirectory(stale);
        await System.IO.File.WriteAllTextAsync(Path.Combine(stale, "old.exe"), "old");

        await client.DownloadAsync(File(content), new Version(0, 9, 4), null);
        await client.DownloadAsync(File(content), new Version(0, 9, 4), null);

        Assert.Single(handler.Requests);
        Assert.False(Directory.Exists(stale));
    }

    [Fact]
    public async Task CleanupKeepsOnlyVersionsNewerThanInstalled()
    {
        var content = Encoding.UTF8.GetBytes("new build");
        var (client, _) = Create(_ => Bytes(content));
        await client.DownloadAsync(File(content), new Version(0, 9, 4), null);

        client.CleanupDownloads(new Version(0, 9, 3));
        Assert.True(Directory.Exists(Path.Combine(_root, "updates", "0.9.4")));

        client.CleanupDownloads(new Version(0, 9, 4));
        Assert.False(Directory.Exists(Path.Combine(_root, "updates", "0.9.4")));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(respond(request));
        }
    }

    private sealed class SyncProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }

    private sealed class FailingStream(byte[] content, int failAfter) : MemoryStream(content)
    {
        public override int Read(byte[] buffer, int offset, int count) =>
            Position >= failAfter ? throw new IOException("connection reset") : base.Read(buffer, offset, Math.Min(count, failAfter - (int)Position));

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Position >= failAfter) throw new IOException("connection reset");
            return base.ReadAsync(buffer[..Math.Min(buffer.Length, failAfter - (int)Position)], cancellationToken);
        }
    }
}

public sealed class ExecutableSwapTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ClipboardManager.Tests", Guid.NewGuid().ToString("N"));

    public ExecutableSwapTests() => Directory.CreateDirectory(_root);

    private string Write(string name, string content)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void ReplaceKeepsOldCopyUntilCleanup()
    {
        var current = Write("ClipboardManager.exe", "v0.9.3");
        var replacement = Write("new.exe", "v0.9.4");

        ExecutableSwap.Replace(current, replacement);

        Assert.Equal("v0.9.4", File.ReadAllText(current));
        Assert.Equal("v0.9.3", File.ReadAllText(ExecutableSwap.OldPath(current)));
        Assert.True(ExecutableSwap.TryDeleteOld(current));
        Assert.False(File.Exists(ExecutableSwap.OldPath(current)));
    }

    [Fact]
    public void RollbackRestoresOriginal()
    {
        var current = Write("ClipboardManager.exe", "v0.9.3");
        ExecutableSwap.Replace(current, Write("new.exe", "v0.9.4"));

        Assert.True(ExecutableSwap.Rollback(current));

        Assert.Equal("v0.9.3", File.ReadAllText(current));
        Assert.False(File.Exists(ExecutableSwap.OldPath(current)));
        Assert.False(ExecutableSwap.Rollback(current));
    }

    [Fact]
    public void FailedCopyPutsOriginalBack()
    {
        var current = Write("ClipboardManager.exe", "v0.9.3");
        var replacement = Write("new.exe", "v0.9.4");
        using (File.Open(replacement, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.ThrowsAny<IOException>(() => ExecutableSwap.Replace(current, replacement));
        }

        Assert.Equal("v0.9.3", File.ReadAllText(current));
        Assert.False(File.Exists(ExecutableSwap.OldPath(current)));
    }

    [Fact]
    public void LeftoverOldFileFromEarlierUpdateIsReplaced()
    {
        var current = Write("ClipboardManager.exe", "v0.9.3");
        Write("ClipboardManager.exe.old", "v0.9.2");

        ExecutableSwap.Replace(current, Write("new.exe", "v0.9.4"));

        Assert.Equal("v0.9.3", File.ReadAllText(ExecutableSwap.OldPath(current)));
    }

    [Fact]
    public void LockedOldFileIsKeptForNextStart()
    {
        var current = Write("ClipboardManager.exe", "v0.9.4");
        var old = Write("ClipboardManager.exe.old", "v0.9.3");
        using (File.Open(old, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.False(ExecutableSwap.TryDeleteOld(current));
        }
        Assert.True(ExecutableSwap.TryDeleteOld(current));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }
}

public sealed class AppUpdateSettingsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ClipboardManager.Tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task SettingsFromBeforeUpdateChecksDefaultToAutomaticChecks()
    {
        var paths = new AppPaths(_root);
        paths.EnsureCreated();
        await File.WriteAllTextAsync(paths.SettingsPath, """{ "retentionDays": 12 }""");

        var loaded = await new JsonAppSettingsStore(paths).LoadAsync();

        Assert.True(loaded.AutoCheckForUpdates);
        Assert.Null(loaded.SkippedUpdateVersion);
    }

    [Fact]
    public async Task OlderSettingsHaveNotConfirmedTheDeviceNameSoTransferSettingsAsksOnce()
    {
        var paths = new AppPaths(_root);
        paths.EnsureCreated();
        await File.WriteAllTextAsync(paths.SettingsPath, """{ "lanDeviceAlias": "DESKTOP-1" }""");

        var store = new JsonAppSettingsStore(paths);
        var loaded = await store.LoadAsync();
        Assert.Equal("DESKTOP-1", loaded.LanDeviceAlias);
        Assert.False(loaded.LanDeviceNameConfirmed);

        await store.SaveAsync(loaded with { LanDeviceAlias = "财务部-张三", LanDeviceNameConfirmed = true });
        var reloaded = await new JsonAppSettingsStore(paths).LoadAsync();
        Assert.Equal("财务部-张三", reloaded.LanDeviceAlias);
        Assert.True(reloaded.LanDeviceNameConfirmed);
    }

    [Fact]
    public async Task SkippedVersionPersistsAndInvalidValueIsRejected()
    {
        var store = new JsonAppSettingsStore(new AppPaths(_root));
        await store.SaveAsync(new AppSettings { AutoCheckForUpdates = false, SkippedUpdateVersion = "0.9.4" });

        var reloaded = await new JsonAppSettingsStore(new AppPaths(_root)).LoadAsync();
        Assert.False(reloaded.AutoCheckForUpdates);
        Assert.Equal("0.9.4", reloaded.SkippedUpdateVersion);
        await Assert.ThrowsAsync<ArgumentException>(() => store.SaveAsync(reloaded with { SkippedUpdateVersion = "latest" }));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }
}
