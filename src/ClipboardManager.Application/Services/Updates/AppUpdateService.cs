using ClipboardManager.Domain.Contracts;
using ClipboardManager.Domain.Models;
using Microsoft.Extensions.Logging;

namespace ClipboardManager.Application.Services.Updates;

/// <summary>检查更新的节奏与当前版本；由 Windows 宿主按程序集版本注册。</summary>
public sealed record AppUpdateOptions(Version CurrentVersion, TimeSpan FirstCheckDelay, TimeSpan CheckInterval)
{
    public static AppUpdateOptions For(Version currentVersion) =>
        new(AppUpdateManifestReader.Normalize(currentVersion), TimeSpan.FromSeconds(30), TimeSpan.FromHours(1));
}

/// <summary>
/// 应用内检查更新（ADR-0005）：后台定时读取版本清单，发现更高版本时提示一次；
/// 失败只记录，不弹窗，不影响剪切板监听主链路与本地功能。这里只读取公开清单，
/// 不上传任何剪切板内容。
/// </summary>
public sealed class AppUpdateService(
    IAppUpdateClient client,
    IAppSettingsStore settingsStore,
    AppUpdateOptions options,
    TimeProvider time,
    ILogger<AppUpdateService> logger) : IDisposable
{
    private readonly SemaphoreSlim checkGate = new(1, 1);
    private readonly object stateLock = new();
    private CancellationTokenSource? cts;
    private Version? notifiedVersion;
    private AppUpdateManifest? pending;

    public Version CurrentVersion => options.CurrentVersion;

    /// <summary>已发现、未被跳过的新版本；界面页脚据此显示"有新版本"。</summary>
    public AppUpdateManifest? Pending
    {
        get { lock (stateLock) return pending; }
    }

    /// <summary>自动检查发现新版本（每个版本只通知一次）。</summary>
    public event EventHandler<AppUpdateManifest>? UpdateAvailable;

    /// <summary><see cref="Pending"/> 变化。</summary>
    public event EventHandler? PendingChanged;

    public void Start()
    {
        if (cts is not null) return;
        cts = new CancellationTokenSource();
        var token = cts.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(options.FirstCheckDelay, time, token);
                while (!token.IsCancellationRequested)
                {
                    await RunAutomaticCheckAsync(token);
                    await Task.Delay(options.CheckInterval, time, token);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                logger.LogError("Update scheduler stopped: {ExceptionType}", exception.GetType().Name);
            }
        }, token);
    }

    private async Task RunAutomaticCheckAsync(CancellationToken cancellationToken)
    {
        try
        {
            var settings = await settingsStore.LoadAsync(cancellationToken);
            if (!settings.AutoCheckForUpdates) return;
            await CheckAsync(manual: false, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning("Automatic update check failed: {ExceptionType}", exception.GetType().Name);
        }
    }

    /// <summary>
    /// 读取清单并与当前版本比较。<paramref name="manual"/> 为 true 时（设置里点"检查更新"）
    /// 即使该版本被跳过也返回 <see cref="AppUpdateStatus.Available"/>，且不触发 <see cref="UpdateAvailable"/>。
    /// </summary>
    public async Task<AppUpdateCheck> CheckAsync(bool manual, CancellationToken cancellationToken = default)
    {
        await checkGate.WaitAsync(cancellationToken);
        try
        {
            string json;
            try
            {
                json = await client.FetchManifestAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                logger.LogInformation("Update manifest unavailable: {ExceptionType}", exception.GetType().Name);
                return new AppUpdateCheck(AppUpdateStatus.Failed, Error: DescribeNetworkError(exception));
            }

            var parsed = AppUpdateManifestReader.Read(json);
            if (parsed.Manifest is not { } manifest)
            {
                logger.LogWarning("Update manifest ignored: {Reason}", parsed.Reason);
                return new AppUpdateCheck(AppUpdateStatus.Unsupported, Error: parsed.Reason);
            }

            if (manifest.Version <= options.CurrentVersion)
            {
                SetPending(null);
                return new AppUpdateCheck(AppUpdateStatus.UpToDate, manifest);
            }

            var settings = await settingsStore.LoadAsync(cancellationToken);
            var skipped = AppUpdateManifestReader.ParseVersion(settings.SkippedUpdateVersion) == manifest.Version;
            if (skipped && !manual)
            {
                SetPending(null);
                return new AppUpdateCheck(AppUpdateStatus.Skipped, manifest);
            }

            SetPending(manifest);
            if (!manual)
            {
                bool first;
                lock (stateLock)
                {
                    first = notifiedVersion != manifest.Version;
                    notifiedVersion = manifest.Version;
                }
                if (first)
                {
                    logger.LogInformation("Update {Version} available (current {Current}).", manifest.Version, options.CurrentVersion);
                    UpdateAvailable?.Invoke(this, manifest);
                }
            }
            return new AppUpdateCheck(AppUpdateStatus.Available, manifest);
        }
        finally
        {
            checkGate.Release();
        }
    }

    /// <summary>跳过此版本：写入设置，自动检查不再提示它；更高的版本仍会提示。</summary>
    public async Task SkipAsync(Version version, CancellationToken cancellationToken = default)
    {
        var settings = await settingsStore.LoadAsync(cancellationToken);
        await settingsStore.SaveAsync(settings with { SkippedUpdateVersion = version.ToString(3) }, cancellationToken);
        lock (stateLock)
        {
            notifiedVersion = version;
        }
        SetPending(null);
    }

    /// <summary>下载并校验安装包；不提供单文件 EXE 的版本直接报错，由界面改为打开下载页。</summary>
    public Task<string> DownloadAsync(AppUpdateManifest manifest, IProgress<double>? progress, CancellationToken cancellationToken = default)
    {
        if (manifest.Version <= options.CurrentVersion) throw new InvalidOperationException("该版本不比当前版本新。");
        if (manifest.Executable is not { } file) throw new InvalidOperationException("该版本没有可自动安装的程序文件，请从下载页获取。");
        return client.DownloadAsync(file, manifest.Version, progress, cancellationToken);
    }

    /// <summary>更新完成或启动时清理已安装及更旧版本的下载缓存。</summary>
    public void CleanupDownloads()
    {
        try
        {
            client.CleanupDownloads(options.CurrentVersion);
        }
        catch (Exception exception)
        {
            logger.LogWarning("Update download cleanup failed: {ExceptionType}", exception.GetType().Name);
        }
    }

    private void SetPending(AppUpdateManifest? manifest)
    {
        bool changed;
        lock (stateLock)
        {
            changed = pending?.Version != manifest?.Version;
            pending = manifest;
        }
        if (changed) PendingChanged?.Invoke(this, EventArgs.Empty);
    }

    private static string DescribeNetworkError(Exception exception) => exception switch
    {
        TaskCanceledException or TimeoutException => "连接更新服务器超时，请稍后再试。",
        HttpRequestException { StatusCode: { } status } => $"更新服务器返回 {(int)status}，请稍后再试。",
        HttpRequestException => "无法连接更新服务器，请检查网络。",
        InvalidDataException data => data.Message,
        _ => "检查更新失败：" + exception.Message,
    };

    public void Dispose()
    {
        cts?.Cancel();
        cts?.Dispose();
        cts = null;
    }
}
