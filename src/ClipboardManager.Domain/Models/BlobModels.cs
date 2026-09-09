namespace ClipboardManager.Domain.Models;

public sealed record BlobDescriptor(
    string ContentHash,
    string MimeType,
    long ByteSize,
    string FileName);

public sealed record UploadTicket(
    string UploadId,
    Uri UploadUri,
    DateTimeOffset ExpiresAt,
    IReadOnlyDictionary<string, string> Headers);
