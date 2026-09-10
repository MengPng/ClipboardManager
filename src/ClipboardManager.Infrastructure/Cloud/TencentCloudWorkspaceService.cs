using ClipboardManager.Domain.Contracts;
using ClipboardManager.Domain.Models;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClipboardManager.Infrastructure.Cloud;

public sealed class TencentCloudWorkspaceService(HttpClient http, TencentCloudHttpAuthorizer authorizer) : ICloudWorkspaceService
{
    private static readonly JsonSerializerOptions Json = CreateJson();

    private static JsonSerializerOptions CreateJson()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    public Task<IReadOnlyList<CloudWorkspace>> GetAvailableAsync(CancellationToken cancellationToken = default) =>
        GetAsync<IReadOnlyList<CloudWorkspace>>("v1/workspaces", cancellationToken);

    public Task<IReadOnlyList<TeamMember>> GetMembersAsync(Guid workspaceId, CancellationToken cancellationToken = default) =>
        GetAsync<IReadOnlyList<TeamMember>>($"v1/workspaces/{workspaceId:D}/members", cancellationToken);

    public Task<IReadOnlyList<MemberGroup>> GetGroupsAsync(Guid workspaceId, CancellationToken cancellationToken = default) =>
        GetAsync<IReadOnlyList<MemberGroup>>($"v1/workspaces/{workspaceId:D}/groups", cancellationToken);

    public async Task<TeamInvitation> CreateInvitationAsync(Guid workspaceId, TimeSpan lifetime, int maxUses, CancellationToken cancellationToken = default)
    {
        if (lifetime <= TimeSpan.Zero || lifetime > TimeSpan.FromDays(30)) throw new ArgumentOutOfRangeException(nameof(lifetime));
        if (maxUses is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(maxUses));
        await authorizer.ConfigureAsync(http, cancellationToken);
        using var response = await http.PostAsJsonAsync($"v1/workspaces/{workspaceId:D}/invitations", new { lifetimeSeconds = (int)lifetime.TotalSeconds, maxUses }, cancellationToken);
        return await ReadAsync<TeamInvitation>(response, cancellationToken);
    }

    public async Task JoinAsync(string invitationCode, CancellationToken cancellationToken = default)
    {
        await authorizer.ConfigureAsync(http, cancellationToken);
        using var response = await http.PostAsJsonAsync("v1/invitations/join", new { code = invitationCode }, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    private async Task<T> GetAsync<T>(string route, CancellationToken cancellationToken)
    {
        await authorizer.ConfigureAsync(http, cancellationToken);
        using var response = await http.GetAsync(route, cancellationToken);
        return await ReadAsync<T>(response, cancellationToken);
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<T>(Json, cancellationToken) ?? throw new InvalidDataException("云端返回无效数据。");
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode) return;
        throw new HttpRequestException(await response.Content.ReadAsStringAsync(cancellationToken), null, response.StatusCode);
    }
}
