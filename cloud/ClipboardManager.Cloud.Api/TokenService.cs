using System.Security.Cryptography;

namespace ClipboardManager.Cloud.Api;

public sealed class TokenService
{
    public IssuedTokens Issue() => new(
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(48)),
        DateTimeOffset.UtcNow.AddMinutes(15),
        DateTimeOffset.UtcNow.AddDays(30));

    public static byte[] Hash(string token) => SHA256.HashData(Convert.FromBase64String(token));
}
