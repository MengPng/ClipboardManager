namespace ClipboardManager.Domain.Models;

public enum ClipboardPayloadType
{
    None = 0,
    Text = 1,
    Image = 2,
    Files = 3
}

public sealed record ClipboardPayload(
    ClipboardPayloadType Type,
    string? Text = null,
    byte[]? ImageBytes = null,
    string? ImageMimeType = null,
    IReadOnlyList<string>? Files = null);
