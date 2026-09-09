namespace ClipboardManager.Domain.Models;

public sealed record IdentitySession(
    Guid IdentityId,
    string Provider,
    string Subject,
    string DisplayName,
    bool IsLocalOnly);

public sealed record SignInRequest(string Provider, IReadOnlyDictionary<string, string> Parameters);

public sealed record DeviceIdentity(
    Guid DeviceId,
    string DeviceName,
    string Platform,
    DateTimeOffset CreatedAt);
