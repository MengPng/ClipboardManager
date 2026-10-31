using ClipboardManager.Domain.Contracts;
using ClipboardManager.Domain.Models;
using Microsoft.Extensions.Logging;

namespace ClipboardManager.Application.Services.ConfigSync;

/// <summary>后台节奏：默认每 30 分钟检查订阅更新；自动发布每 10 秒巡检，改动后 60 秒内无新改动才发布。</summary>
public sealed record ConfigSyncSchedule(
    TimeSpan CheckInterval,
    TimeSpan FirstCheckDelay,
    TimeSpan AutoPublishPoll,
    TimeSpan AutoPublishQuiet)
{
    public static ConfigSyncSchedule Default { get; } = new(
        TimeSpan.FromMinutes(30), TimeSpan.FromSeconds(45), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(60));
}

/// <summary>
/// 配置同步的后台协调：按需启动监听与组播广播、定时检查订阅更新、自动发布。
/// 全部在后台线程运行，任何失败只记录，不影响剪切板监听主链路与本地功能。
/// </summary>
public sealed class ConfigSyncCoordinator(
    IConfigSyncServer server,
    IConfigDiscovery discovery,
    IConfigSyncRepository repository,
    ConfigPublishService publishing,
    ConfigSubscriptionService subscriptions,
    ConfigSyncEndpoint endpoint,
    PairingSessions pairing,
    TimeProvider time,
    ILogger<ConfigSyncCoordinator> logger,
    ConfigSyncSchedule? schedule = null) : IDisposable
{
    private readonly ConfigSyncSchedule timing = schedule ?? ConfigSyncSchedule.Default;
    private readonly SemaphoreSlim refreshLock = new(1, 1);
    private readonly Dictionary<Guid, AutoPublishState> autoPublish = [];
    private readonly HashSet<string> notified = [];
    private readonly object notifiedLock = new();
    private CancellationTokenSource? cts;
    private bool started;

    /// <summary>发布端、订阅端或后台状态变化，界面据此刷新。</summary>
    public event EventHandler? Changed;

    /// <summary>发现某订阅有需要用户确认的新版本（仅对每个版本通知一次）。</summary>
    public event EventHandler<ConfigUpdateNotice>? UpdateAvailable;

    /// <summary>某订阅已被自动更新。</summary>
    public event EventHandler<ConfigUpdateNotice>? AutoUpdated;

    public SyncServerStatus ServerStatus => server.Status;

    public string FirewallHint => ConfigSyncPolicy.FirewallHint;

    public void Start()
    {
        if (started) return;
        started = true;
        cts = new CancellationTokenSource();
        var token = cts.Token;
        publishing.Changed += OnInnerChanged;
        subscriptions.Changed += OnInnerChanged;
        endpoint.PairingCompleted += OnInnerChanged;
        _ = Task.Run(() => RunAsync(token));
    }

    /// <summary>生成配对码前确保监听已启动（没有发布内容时也需要接受配对）。</summary>
    public async Task<PairingCodeInfo> IssuePairingCodeAsync(CancellationToken cancellationToken = default)
    {
        await server.StartAsync(cancellationToken);
        if (!server.Status.IsRunning)
            throw new ConfigSyncException(ConfigSyncError.Local,
                "无法启动本机监听：" + (server.Status.LastError ?? "未知原因") + "。" + ConfigSyncPolicy.FirewallHint);
        var info = publishing.IssuePairingCode();
        Changed?.Invoke(this, EventArgs.Empty);
        return info;
    }

    /// <summary>刷新监听与广播：有发布内容或配对码有效时监听，否则停止。</summary>
    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        await refreshLock.WaitAsync(cancellationToken);
        try
        {
            var published = await repository.GetPublishedAsync(cancellationToken);
            var needServer = published.Count > 0 || pairing.Current is not null;
            if (needServer) await server.StartAsync(cancellationToken);
            else await server.StopAsync();
            discovery.SetAnnouncement(server.Status.IsRunning
                ? published.Select(config => new PublicationAnnouncement(config.Name, config.CurrentVersion)).ToList()
                : []);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "刷新配置同步监听失败");
        }
        finally
        {
            refreshLock.Release();
        }
    }

    /// <summary>检查全部订阅；可自动更新且无高风险文件的订阅直接更新，其余只通知。</summary>
    public async Task CheckAllAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<ConfigSubscription> list;
        try { list = await repository.GetSubscriptionsAsync(cancellationToken); }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "读取订阅列表失败");
            return;
        }

        foreach (var subscription in list)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var check = await subscriptions.CheckAsync(subscription.Id, cancellationToken);
                if (check.State != ConfigUpdateState.UpdateAvailable || check.Remote is null) continue;
                var notice = new ConfigUpdateNotice(subscription, check);
                if (subscription.AutoUpdate && !check.Remote.HasRiskyFiles)
                {
                    try
                    {
                        var updated = await subscriptions.ApplyUpdateAsync(subscription.Id, false, cancellationToken);
                        AutoUpdated?.Invoke(this, new ConfigUpdateNotice(updated, check));
                        continue;
                    }
                    catch (ConfigSyncException exception)
                    {
                        // 自动更新失败：保留原版本，作为普通更新提示交给用户。
                        logger.LogWarning("订阅 {SubscriptionId} 自动更新失败：{Error}", subscription.Id, exception.Error);
                    }
                }
                NotifyOnce(notice);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "检查订阅 {SubscriptionId} 的更新失败", subscription.Id);
            }
        }
    }

    /// <summary>自动发布巡检一次（公开以便测试）。返回本次触发的发布数。</summary>
    public async Task<int> AutoPublishTickAsync(CancellationToken cancellationToken = default)
    {
        var published = await repository.GetPublishedAsync(cancellationToken);
        var now = time.GetUtcNow();
        var count = 0;
        var live = published.Where(config => config.AutoPublish).Select(config => config.Id).ToHashSet();
        foreach (var stale in autoPublish.Keys.Where(id => !live.Contains(id)).ToList()) autoPublish.Remove(stale);

        foreach (var config in published.Where(config => config.AutoPublish))
        {
            var fingerprint = await publishing.GetFingerprintAsync(config, cancellationToken);
            if (fingerprint is null) continue;
            if (!autoPublish.TryGetValue(config.Id, out var state))
            {
                autoPublish[config.Id] = new AutoPublishState(fingerprint, now, null);
                continue;
            }
            if (state.Seen != fingerprint)
            {
                autoPublish[config.Id] = state with { Seen = fingerprint, ChangedAt = now };
                continue;
            }
            if (state.Published == fingerprint || now - state.ChangedAt < timing.AutoPublishQuiet) continue;
            try
            {
                var result = await publishing.PublishAsync(config.WorkspaceId, config.AllFolders ? null : config.FolderIds, null, cancellationToken);
                if (result.Changed) count++;
            }
            catch (ConfigSyncException exception)
            {
                logger.LogWarning("自动发布 {PublishedId} 失败：{Error}", config.Id, exception.Error);
            }
            autoPublish[config.Id] = state with { Published = fingerprint };
        }
        return count;
    }

    public void Dispose()
    {
        cts?.Cancel();
        cts?.Dispose();
        cts = null;
        if (started)
        {
            publishing.Changed -= OnInnerChanged;
            subscriptions.Changed -= OnInnerChanged;
            endpoint.PairingCompleted -= OnInnerChanged;
        }
    }

    private async Task RunAsync(CancellationToken token)
    {
        try
        {
            await RefreshAsync(token);
            await Task.WhenAll(CheckLoopAsync(token), AutoPublishLoopAsync(token));
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            logger.LogError(exception, "配置同步后台任务异常退出");
        }
    }

    private async Task CheckLoopAsync(CancellationToken token)
    {
        await Task.Delay(timing.FirstCheckDelay, time, token);
        using var timer = new PeriodicTimer(timing.CheckInterval, time);
        do
        {
            try { await CheckAllAsync(token); }
            catch (OperationCanceledException) { throw; }
            catch (Exception exception) { logger.LogWarning(exception, "定时检查订阅更新失败"); }
        }
        while (await timer.WaitForNextTickAsync(token));
    }

    private async Task AutoPublishLoopAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(timing.AutoPublishPoll, time);
        while (await timer.WaitForNextTickAsync(token))
        {
            try { await AutoPublishTickAsync(token); }
            catch (OperationCanceledException) { throw; }
            catch (Exception exception) { logger.LogWarning(exception, "自动发布巡检失败"); }
        }
    }

    private void OnInnerChanged(object? sender, EventArgs e)
    {
        if (ReferenceEquals(sender, publishing)) _ = Task.Run(() => RefreshAsync());
        // 配对完成的回调发生在响应写回之前；稍后再决定是否停止监听，避免截断配对响应。
        else if (ReferenceEquals(sender, endpoint))
            _ = Task.Run(async () =>
            {
                await Task.Delay(TimeSpan.FromSeconds(3), time);
                await RefreshAsync();
            });
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void NotifyOnce(ConfigUpdateNotice notice)
    {
        var key = $"{notice.Subscription.Id:N}:{notice.Check.Remote!.Version}";
        lock (notifiedLock)
            if (!notified.Add(key)) return;
        UpdateAvailable?.Invoke(this, notice);
    }

    private sealed record AutoPublishState(string Seen, DateTimeOffset ChangedAt, string? Published);
}

public sealed record ConfigUpdateNotice(ConfigSubscription Subscription, ConfigUpdateCheck Check);
