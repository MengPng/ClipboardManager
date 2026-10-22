using ClipboardManager.Domain.Contracts;
using ClipboardManager.Domain.Models;
using System.Diagnostics;
using System.Text.Json;

namespace ClipboardManager.Application.Services;

public sealed class CollectionRestoreService(ISystemClipboard clipboard)
{
    public async Task UseAsync(CollectionItem item, CancellationToken cancellationToken = default)
    {
        switch (item.ItemType)
        {
            case CollectionItemType.Text when item.TextContent is not null:
                await clipboard.WriteTextAsync(item.TextContent, cancellationToken);
                break;
            case CollectionItemType.Link when item.Url is not null:
                await clipboard.WriteTextAsync(item.Url, cancellationToken);
                break;
            case CollectionItemType.Image when item.LocalPath is not null:
                await clipboard.WriteImageAsync(item.LocalPath, cancellationToken);
                break;
            case CollectionItemType.File:
                var files = item.FileManifest is null ? [item.LocalPath ?? string.Empty] : JsonSerializer.Deserialize<string[]>(item.FileManifest) ?? [];
                await clipboard.WriteFilesAsync(files.Where(path => !string.IsNullOrWhiteSpace(path)).ToArray(), cancellationToken);
                break;
            default:
                throw new InvalidOperationException("收藏内容不可用或本地文件已失效。");
        }
    }

    public void OpenLink(CollectionItem item)
    {
        if (item.ItemType != CollectionItemType.Link || item.Url is null) throw new InvalidOperationException("该收藏不是链接。");
        var address = CollectionLink.Normalize(item.Url);
        Process.Start(new ProcessStartInfo(address) { UseShellExecute = true });
    }
}
