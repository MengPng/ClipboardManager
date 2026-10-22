namespace ClipboardManager.Domain.Models;

public static class CollectionLink
{
    public static string Normalize(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
            string.IsNullOrWhiteSpace(uri.Host) || !string.IsNullOrEmpty(uri.UserInfo))
            throw new ArgumentException("Link must be an absolute HTTP or HTTPS address without credentials.", nameof(value));
        return uri.AbsoluteUri;
    }
}
