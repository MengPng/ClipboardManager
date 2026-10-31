using System.Security.Cryptography;
using System.Text;
using ClipboardManager.Domain.Contracts;
using ClipboardManager.Domain.Models;
using Microsoft.Extensions.Logging;

namespace ClipboardManager.Application.Services.ConfigSync;

/// <summary>发布方：把一个配置文件夹打包为版本化的只读配置包，并管理配对设备。</summary>
public sealed class ConfigPublishService(
    IConfigSyncRepository repository,
    IConfigPackageService packages,
    ICollectionRepository collections,
    PairingSessions pairing,
    TimeProvider time,
    ILogger<ConfigPublishService> logger)
{
    private readonly SemaphoreSlim publishLock = new(1, 1);

    /// <summary>发布内容、配对设备或发布设置发生变化（用于刷新界面与广播）。</summary>
    public event EventHandler? Changed;

    public Task<IReadOnlyList<PublishedConfig>> GetPublishedAsync(CancellationToken cancellationToken = default) =>
        repository.GetPublishedAsync(cancellationToken);

    public Task<IReadOnlyList<PairedPeer>> GetSubscribersAsync(CancellationToken cancellationToken = default) =>
        repository.GetPeersAsync(PeerRole.Subscriber, cancellationToken);

    public PairingCodeInfo IssuePairingCode() => pairing.Issue();

    public PairingCodeInfo? CurrentPairingCode => pairing.Current;

    public void CancelPairingCode() => pairing.Cancel();

    public async Task RemoveSubscriberAsync(Guid peerId, CancellationToken cancellationToken = default)
    {
        await repository.RevokePeerAsync(peerId, cancellationToken);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task SetAutoPublishAsync(Guid publishedId, bool enabled, CancellationToken cancellationToken = default)
    {
        await repository.SetAutoPublishAsync(publishedId, enabled, cancellationToken);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task StopPublishingAsync(Guid publishedId, CancellationToken cancellationToken = default)
    {
        await publishLock.WaitAsync(cancellationToken);
        try
        {
            var path = await repository.DeletePublishedAsync(publishedId, cancellationToken);
            if (path is not null) TryDelete(packages.ResolvePath(path));
        }
        finally
        {
            publishLock.Release();
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public Task<PublishResult> PublishAsync(Guid workspaceId, Guid folderId, string? name = null,
        CancellationToken cancellationToken = default) =>
        PublishAsync(workspaceId, [folderId], name, cancellationToken);

    /// <summary>
    /// 把所选文件夹打成<b>一个</b>包发布；<paramref name="folderIds"/> 为空表示一次发布全部个人配置（默认）。
    /// 首次发布为 v1；同一范围再次发布则版本号 +1；内容没有变化时不产生新版本。
    /// </summary>
    public async Task<PublishResult> PublishAsync(Guid workspaceId, IReadOnlyList<Guid>? folderIds, string? name = null,
        CancellationToken cancellationToken = default)
    {
        await publishLock.WaitAsync(cancellationToken);
        try
        {
            var all = folderIds is not { Count: > 0 };
            var selection = all ? [] : folderIds!.Distinct().ToList();
            var workspaceFolders = await collections.GetFoldersAsync(workspaceId, cancellationToken);
            var chosen = all
                ? workspaceFolders.Where(candidate => candidate.ManagedSubscriptionId is null).ToList()
                : selection.Select(id => workspaceFolders.FirstOrDefault(candidate => candidate.Id == id)
                    ?? throw new ConfigSyncException(ConfigSyncError.Local, "要发布的配置文件夹已不存在，请重新选择。")).ToList();
            if (chosen.Any(candidate => candidate.ManagedSubscriptionId is not null))
                throw new ConfigSyncException(ConfigSyncError.Local, "订阅得到的文件夹不能再发布，请先“复制到我的配置”。");
            if (chosen.Count == 0)
                throw new ConfigSyncException(ConfigSyncError.Local, "没有可发布的配置文件夹。");

            var existing = (await repository.GetPublishedAsync(cancellationToken))
                .FirstOrDefault(config => config.WorkspaceId == workspaceId && SameScope(config, all, selection));
            var id = existing?.Id ?? Guid.NewGuid();
            var version = (existing?.CurrentVersion ?? 0) + 1;
            var publishName = existing?.Name ?? CleanName(string.IsNullOrWhiteSpace(name) ? DefaultName(all, chosen) : name);

            var temp = packages.NewTempPath();
            PackedConfig packed;
            try
            {
                packed = await packages.PackFoldersAsync(workspaceId, all ? null : selection, id, publishName, version, temp, cancellationToken);
                if (packed.Manifest.Items.Count == 0)
                    throw new ConfigSyncException(ConfigSyncError.Local, "没有可发布的内容。");
                if (Encoding.UTF8.GetByteCount(packed.ManifestJson) > ConfigSyncPolicy.MaxManifestBytes)
                    throw new ConfigSyncException(ConfigSyncError.TooLarge, "配置清单超过 4 MB 上限，请减少条目数量后再发布。");
                if (existing is not null && SameContent(ConfigSyncPolicy.ParseManifest(existing.ManifestJson), packed.Manifest))
                {
                    TryDelete(temp);
                    return new PublishResult(existing, false, packed.FileCount, packed.MissingFiles);
                }

                var relative = $"imports/sync/pub/{id:N}/v{version}.zip";
                var destination = packages.ResolvePath(relative);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Move(temp, destination, overwrite: true);

                var now = time.GetUtcNow();
                var config = new PublishedConfig(id, workspaceId, selection, all, publishName, version, packed.Manifest.Hash, relative,
                    packed.Manifest.PackageBytes, packed.ManifestJson, existing?.AutoPublish ?? false, existing?.CreatedAt ?? now, now);
                await repository.SavePublishedAsync(config, cancellationToken);
                if (existing is not null && existing.PackagePath != relative) TryDelete(packages.ResolvePath(existing.PackagePath));
                logger.LogInformation("已发布配置 {PublishedId} v{Version}（{Items} 项）", id, version, packed.Manifest.Items.Count);
                Changed?.Invoke(this, EventArgs.Empty);
                return new PublishResult(config, true, packed.FileCount, packed.MissingFiles);
            }
            finally
            {
                TryDelete(temp);
            }
        }
        finally
        {
            publishLock.Release();
        }
    }

    /// <summary>用单个文件夹的内容算指纹（保留给单文件夹场景与测试）。</summary>
    public Task<string?> GetFolderFingerprintAsync(Guid workspaceId, Guid folderId, CancellationToken cancellationToken = default) =>
        GetFingerprintAsync(workspaceId, false, [folderId], cancellationToken);

    /// <summary>用于自动发布判断：该发布范围内文件夹（名称、顺序）与条目（更新时间、内容哈希）变化后指纹随之变化。</summary>
    public Task<string?> GetFingerprintAsync(PublishedConfig config, CancellationToken cancellationToken = default) =>
        GetFingerprintAsync(config.WorkspaceId, config.AllFolders, config.FolderIds, cancellationToken);

    private async Task<string?> GetFingerprintAsync(Guid workspaceId, bool all, IReadOnlyList<Guid> selection,
        CancellationToken cancellationToken)
    {
        var folders = await collections.GetFoldersAsync(workspaceId, cancellationToken);
        var chosen = new List<CollectionFolder>();
        if (all) chosen.AddRange(folders.Where(folder => folder.ManagedSubscriptionId is null));
        else
        {
            foreach (var id in selection)
            {
                var found = folders.FirstOrDefault(folder => folder.Id == id);
                if (found is null) return null;
                chosen.Add(found);
            }
        }
        if (chosen.Count == 0) return null;
        var builder = new StringBuilder();
        foreach (var folder in chosen)
        {
            builder.Append(folder.Id.ToString("N")).Append(':').Append(folder.Name).Append(';');
            var items = await collections.GetItemsAsync(workspaceId, folder.Id, cancellationToken: cancellationToken);
            foreach (var item in items) builder.Append($"{item.Id:N}:{item.UpdatedAt.UtcTicks}:{item.LocalVersion}:{item.ContentHash}|");
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }

    private static bool SameScope(PublishedConfig config, bool all, IReadOnlyList<Guid> selection) =>
        config.AllFolders == all &&
        (all || (config.FolderIds.Count == selection.Count && !config.FolderIds.Except(selection).Any()));

    private static string DefaultName(bool all, IReadOnlyList<CollectionFolder> chosen) =>
        all ? "我的配置" : chosen.Count == 1 ? chosen[0].Name : $"{chosen[0].Name} 等 {chosen.Count} 个文件夹";

    private static bool SameContent(ConfigManifest previous, ConfigManifest next) =>
        previous.Items.Count == next.Items.Count &&
        previous.Items.Zip(next.Items).All(pair => pair.First.Id == pair.Second.Id && pair.First.Digest == pair.Second.Digest &&
                                                   pair.First.FolderId == pair.Second.FolderId) &&
        previous.FolderList.Select(folder => (folder.Id, folder.Name)).SequenceEqual(next.FolderList.Select(folder => (folder.Id, folder.Name)));

    private static string CleanName(string value)
    {
        var text = new string(value.Where(ch => !char.IsControl(ch)).ToArray()).Trim();
        if (text.Length == 0) text = "我的配置";
        return text.Length > ConfigSyncPolicy.MaxFolderNameLength ? text[..ConfigSyncPolicy.MaxFolderNameLength] : text;
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
