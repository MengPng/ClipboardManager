using ClipboardManager.Application.Services.Updates;
using ClipboardManager.Domain.Contracts;
using ClipboardManager.Domain.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClipboardManager.Application.Tests;

public sealed class AppUpdateManifestReaderTests
{
    private const string Hash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    internal static string Manifest(
        string version = "0.9.4",
        int schema = 1,
        string product = "ClipboardManager",
        string name = "ClipboardManager-0.9.4-win-x64.exe",
        string url = "https://clipboard.netkaize.com/download/files/0.9.4/ClipboardManager-0.9.4-win-x64.exe",
        string sha256 = Hash,
        long size = 1234) => $$"""
        {
          "schema": {{schema}},
          "product": "{{product}}",
          "pageUrl": "https://clipboard.netkaize.com/download/",
          "version": "{{version}}",
          "releasedAt": "2026-11-10T09:30:00Z",
          "title": "检查更新",
          "notes": ["应用内检查更新", "下载后校验 SHA-256"],
          "files": [
            { "kind": "zip", "name": "a.zip", "url": "https://clipboard.netkaize.com/a.zip", "size": 1, "sha256": "{{Hash}}" },
            { "kind": "exe", "name": "{{name}}", "url": "{{url}}", "size": {{size}}, "sha256": "{{sha256}}" }
          ]
        }
        """;

    [Fact]
    public void ReadsValidManifestAndPicksExecutable()
    {
        var result = AppUpdateManifestReader.Read(Manifest());

        var manifest = Assert.IsType<AppUpdateManifest>(result.Manifest);
        Assert.Equal(new Version(0, 9, 4), manifest.Version);
        Assert.Equal("检查更新", manifest.Title);
        Assert.Equal(2, manifest.Notes.Count);
        Assert.Equal("ClipboardManager-0.9.4-win-x64.exe", manifest.Executable?.Name);
        Assert.Equal(1234, manifest.Executable?.Size);
    }

    [Fact]
    public void UnknownSchemaMeansNoUpdate()
    {
        var result = AppUpdateManifestReader.Read(Manifest(schema: 2));
        Assert.Null(result.Manifest);
        Assert.Contains("schema", result.Reason);
    }

    [Theory]
    [InlineData("0.9")]
    [InlineData("0.9.4.1")]
    [InlineData("v0.9.4")]
    [InlineData("0.9.x")]
    public void RejectsVersionsThatAreNotMajorMinorPatch(string version) =>
        Assert.Null(AppUpdateManifestReader.Read(Manifest(version: version)).Manifest);

    [Fact]
    public void RejectsOtherProductAndBrokenJson()
    {
        Assert.Null(AppUpdateManifestReader.Read(Manifest(product: "Other")).Manifest);
        Assert.Null(AppUpdateManifestReader.Read("{not json").Manifest);
        Assert.Null(AppUpdateManifestReader.Read("[]").Manifest);
    }

    [Theory]
    [InlineData("http://clipboard.netkaize.com/a.exe", "a.exe", Hash, 10)]
    [InlineData("https://clipboard.netkaize.com/a.exe", "../a.exe", Hash, 10)]
    [InlineData("https://clipboard.netkaize.com/a.exe", "..\\\\a.exe", Hash, 10)]
    [InlineData("https://clipboard.netkaize.com/a.exe", "a.msi", Hash, 10)]
    [InlineData("https://clipboard.netkaize.com/a.exe", "a.exe", "abc", 10)]
    [InlineData("https://clipboard.netkaize.com/a.exe", "a.exe", Hash, 0)]
    [InlineData("https://clipboard.netkaize.com/a.exe", "a.exe", Hash, 600L * 1024 * 1024)]
    public void UnsafeExecutableEntryIsDroppedButManifestStillShowsUpdate(string url, string name, string sha256, long size)
    {
        var manifest = AppUpdateManifestReader.Read(Manifest(url: url, name: name, sha256: sha256, size: size)).Manifest;
        Assert.NotNull(manifest);
        Assert.Null(manifest!.Executable);
    }

    [Fact]
    public void NormalizesAssemblyVersionToThreeParts() =>
        Assert.Equal(new Version(0, 9, 3), AppUpdateManifestReader.Normalize(new Version(0, 9, 3, 0)));
}

public sealed class AppUpdateServiceTests
{
    private readonly FakeUpdateClient client = new();
    private readonly MemorySettingsStore settings = new();

    private AppUpdateService Create(string current = "0.9.3") =>
        new(client, settings, AppUpdateOptions.For(Version.Parse(current)), TimeProvider.System, NullLogger<AppUpdateService>.Instance);

    [Theory]
    [InlineData("0.9.3")]
    [InlineData("0.9.2")]
    public async Task SameOrOlderVersionIsUpToDate(string remote)
    {
        client.Json = AppUpdateManifestReaderTests.Manifest(version: remote);
        var service = Create();

        var result = await service.CheckAsync(manual: false);

        Assert.Equal(AppUpdateStatus.UpToDate, result.Status);
        Assert.Null(service.Pending);
    }

    [Fact]
    public async Task NewerVersionIsAnnouncedOncePerVersion()
    {
        client.Json = AppUpdateManifestReaderTests.Manifest(version: "0.9.4");
        var service = Create();
        var announced = new List<Version>();
        service.UpdateAvailable += (_, manifest) => announced.Add(manifest.Version);

        Assert.Equal(AppUpdateStatus.Available, (await service.CheckAsync(manual: false)).Status);
        await service.CheckAsync(manual: false);
        Assert.Equal([new Version(0, 9, 4)], announced);
        Assert.Equal(new Version(0, 9, 4), service.Pending?.Version);

        client.Json = AppUpdateManifestReaderTests.Manifest(version: "0.9.5");
        await service.CheckAsync(manual: false);
        Assert.Equal([new Version(0, 9, 4), new Version(0, 9, 5)], announced);
    }

    [Fact]
    public async Task ManualCheckDoesNotRaiseBackgroundNotice()
    {
        client.Json = AppUpdateManifestReaderTests.Manifest(version: "0.9.4");
        var service = Create();
        var raised = 0;
        service.UpdateAvailable += (_, _) => raised++;

        Assert.Equal(AppUpdateStatus.Available, (await service.CheckAsync(manual: true)).Status);
        Assert.Equal(0, raised);
        Assert.NotNull(service.Pending);
    }

    [Fact]
    public async Task SkippedVersionIsQuietAutomaticallyButShownOnManualCheck()
    {
        client.Json = AppUpdateManifestReaderTests.Manifest(version: "0.9.4");
        var service = Create();
        await service.CheckAsync(manual: false);

        await service.SkipAsync(new Version(0, 9, 4));

        Assert.Equal("0.9.4", (await settings.LoadAsync()).SkippedUpdateVersion);
        Assert.Null(service.Pending);
        Assert.Equal(AppUpdateStatus.Skipped, (await Create().CheckAsync(manual: false)).Status);
        Assert.Equal(AppUpdateStatus.Available, (await Create().CheckAsync(manual: true)).Status);

        client.Json = AppUpdateManifestReaderTests.Manifest(version: "0.9.5");
        Assert.Equal(AppUpdateStatus.Available, (await Create().CheckAsync(manual: false)).Status);
    }

    [Fact]
    public async Task NetworkFailureIsReportedWithoutThrowing()
    {
        client.Failure = new HttpRequestException("offline");
        var result = await Create().CheckAsync(manual: false);
        Assert.Equal(AppUpdateStatus.Failed, result.Status);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public async Task UnsupportedManifestIsNotAnUpdate()
    {
        client.Json = AppUpdateManifestReaderTests.Manifest(version: "1.0.0", schema: 2);
        var service = Create();
        var raised = 0;
        service.UpdateAvailable += (_, _) => raised++;

        Assert.Equal(AppUpdateStatus.Unsupported, (await service.CheckAsync(manual: false)).Status);
        Assert.Equal(0, raised);
        Assert.Null(service.Pending);
    }

    [Fact]
    public async Task DownloadRefusesManifestWithoutExecutableOrNotNewer()
    {
        var service = Create();
        var noExe = AppUpdateManifestReader.Read(AppUpdateManifestReaderTests.Manifest(version: "0.9.4", sha256: "bad")).Manifest!;
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.DownloadAsync(noExe, null));

        var same = AppUpdateManifestReader.Read(AppUpdateManifestReaderTests.Manifest(version: "0.9.3")).Manifest!;
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.DownloadAsync(same, null));
        Assert.Equal(0, client.Downloads);
    }

    [Fact]
    public void DefaultScheduleChecksAfterThirtySecondsThenHourly()
    {
        var options = AppUpdateOptions.For(new Version(0, 9, 3, 0));
        Assert.Equal(TimeSpan.FromSeconds(30), options.FirstCheckDelay);
        Assert.Equal(TimeSpan.FromHours(1), options.CheckInterval);
        Assert.Equal(new Version(0, 9, 3), options.CurrentVersion);
    }

    [Fact]
    public async Task AutomaticScheduleHonoursTheSetting()
    {
        client.Json = AppUpdateManifestReaderTests.Manifest(version: "0.9.4");
        await settings.SaveAsync(new AppSettings { AutoCheckForUpdates = false });
        using var service = new AppUpdateService(client, settings,
            new AppUpdateOptions(new Version(0, 9, 3), TimeSpan.FromMilliseconds(1), TimeSpan.FromHours(1)),
            TimeProvider.System, NullLogger<AppUpdateService>.Instance);

        service.Start();
        await Task.Delay(200);

        Assert.Equal(0, client.Fetches);
    }

    private sealed class FakeUpdateClient : IAppUpdateClient
    {
        public string Json { get; set; } = "{}";
        public Exception? Failure { get; set; }
        public int Fetches { get; private set; }
        public int Downloads { get; private set; }

        public Task<string> FetchManifestAsync(CancellationToken cancellationToken = default)
        {
            Fetches++;
            return Failure is null ? Task.FromResult(Json) : Task.FromException<string>(Failure);
        }

        public Task<string> DownloadAsync(AppUpdateFile file, Version version, IProgress<double>? progress, CancellationToken cancellationToken = default)
        {
            Downloads++;
            return Task.FromResult("downloaded.exe");
        }

        public void CleanupDownloads(Version installed) { }
    }

    private sealed class MemorySettingsStore : IAppSettingsStore
    {
        private AppSettings current = new();
        public Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(current);
        public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
        {
            current = settings;
            return Task.CompletedTask;
        }
    }
}
