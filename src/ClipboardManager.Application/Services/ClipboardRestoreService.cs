using ClipboardManager.Domain.Contracts;
using ClipboardManager.Domain.Models;
using System.Text.Json;

namespace ClipboardManager.Application.Services;

public sealed class ClipboardRestoreService(
    ISystemClipboard systemClipboard,
    ClipboardHistoryService historyService,
    SelfWriteGuard selfWriteGuard)
{
    public async Task CopyTextAsync(string text, CancellationToken cancellationToken = default)
    {
        selfWriteGuard.Expect(TextContentService.ComputeHash(text));
        try { await systemClipboard.WriteTextAsync(text, cancellationToken); }
        catch { selfWriteGuard.Clear(); throw; }
    }

    public async Task RestoreAsync(ClipboardItem item, CancellationToken cancellationToken = default)
    {
        switch (item.ItemType)
        {
            case ClipboardItemType.Text when item.TextContent is not null:
                await CopyTextAsync(item.TextContent, cancellationToken);
                break;
            case ClipboardItemType.Image when item.ImagePath is not null:
                selfWriteGuard.Expect(ClipboardPayloadType.Image, item.ContentHash);
                try { await systemClipboard.WriteImageAsync(item.ImagePath, cancellationToken); }
                catch { selfWriteGuard.Clear(); throw; }
                break;
            case ClipboardItemType.Files or ClipboardItemType.ImageFile when item.FileManifest is not null:
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
