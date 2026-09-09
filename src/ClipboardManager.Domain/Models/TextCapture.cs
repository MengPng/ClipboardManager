namespace ClipboardManager.Domain.Models;

public sealed record TextCapture(
    Guid WorkspaceId,
    Guid? OwnerId,
    Guid DeviceId,
    string Text,
    string Preview,
    string ContentHash,
    string? SourceApp,
    string? SourceProcess,
    DateTimeOffset CapturedAt,
    long ContentSize);

public enum CaptureDisposition
{
    Created = 0,
    Merged = 1,
    Restored = 2,
    IgnoredEmpty = 3,
    IgnoredTooLarge = 4,
    IgnoredExcludedApplication = 5,
    IgnoredPaused = 6,
    IgnoredSelfWrite = 7
}

public sealed record CaptureResult(CaptureDisposition Disposition, ClipboardItem? Item = null);

public sealed record ClipboardSource(string? ApplicationName, string? ProcessName);
