using ClipboardManager.Domain.Contracts;
using ClipboardManager.Domain.Models;
using System.Text.Json;

namespace ClipboardManager.Application.Services;

public sealed class ClipboardRestoreService(
    ISystemClipboard systemClipboard,
    ClipboardHistoryService historyService,
    SelfWriteGuard selfWriteGuard)
{
    public async Task RestoreAsync(ClipboardItem item, CancellationToken cancellationToken = default)
    {
        switch (item.ItemType)
        {
            case ClipboardItemType.Text when item.TextContent is not null:
                selfWriteGuard.Expect(TextContentService.ComputeHash(item.TextContent));
                try { await systemClipboard.WriteTextAsync(item.TextContent, cancellationToken); }
                catch { selfWriteGuard.Clear(); throw; }
                break;
            case ClipboardItemType.Image when item.ImagePath is not null:
                selfWriteGuard.Expect(ClipboardPayloadType.Image, item.ContentHash);
                try { await systemClipboard.WriteImageAsync(item.ImagePath, cancellationToken); }
                catch { selfWriteGuard.Clear(); throw; }
                break;
            case ClipboardItemType.Files when item.FileManifest is not null:
                var files = JsonSerializer.Deserialize<string[]>(item.FileManifest) ?? [];
                selfWriteGuard.Expect(ClipboardPayloadType.Files, item.ContentHash);
                try { await systemClipboard.WriteFilesAsync(files, cancellationToken); }
                catch { selfWriteGuard.Clear(); throw; }
                break;
            default:
                throw new InvalidOperationException("记录内容不可恢复或已失效。");
        }
        await historyService.MarkUsedAsync(item.Id, cancellationToken);
    }
}
