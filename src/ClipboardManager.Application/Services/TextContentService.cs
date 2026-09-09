using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace ClipboardManager.Application.Services;

public static partial class TextContentService
{
    public static string ComputeHash(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized))).ToLowerInvariant();
    }

    public static string CreatePreview(string text, int maxLength = 240)
    {
        ArgumentNullException.ThrowIfNull(text);
        var collapsed = WhitespaceRegex().Replace(text, " ").Trim();
        return collapsed.Length <= maxLength ? collapsed : collapsed[..maxLength] + "…";
    }

    public static long GetUtf8ByteCount(string text) => Encoding.UTF8.GetByteCount(text);

    [GeneratedRegex("\\s+", RegexOptions.CultureInvariant)]
    private static partial Regex WhitespaceRegex();
}
