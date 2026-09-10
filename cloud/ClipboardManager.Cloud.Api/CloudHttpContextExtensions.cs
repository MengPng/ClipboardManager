namespace ClipboardManager.Cloud.Api;

public static class CloudHttpContextExtensions
{
    public static async Task<CloudPrincipal> RequireCloudUserAsync(this HttpContext context, CloudRepository repository, CancellationToken ct)
    {
        var header = context.Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) throw new UnauthorizedAccessException("需要登录。");
        var principal = await repository.ValidateAccessTokenAsync(header[7..].Trim(), ct);
        return principal ?? throw new UnauthorizedAccessException("会话无效或已撤销。");
    }
}
