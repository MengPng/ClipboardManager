using ClipboardManager.Domain.Contracts;
using ClipboardManager.Domain.Models;
using System.Net.Http.Json;
using System.Text.Json;

namespace ClipboardManager.Infrastructure.Cloud;

public sealed class TencentRemoteSyncProvider(HttpClient http, TencentCloudHttpAuthorizer authorizer) : IRemoteSyncProvider
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<PushResult> PushAsync(IReadOnlyList<SyncOperation> operations, CancellationToken cancellationToken = default)
    {
        if (operations.Count == 0) return new(true, new Dictionary<Guid, long>());
        if (operations.Select(x => x.WorkspaceId).Distinct().Count() != 1) throw new ArgumentException("一次推送只能属于同一 Workspace。", nameof(operations));
        await authorizer.ConfigureAsync(http, cancellationToken);
        var wireOperations = operations.Select(operation => new
        {
            operation.Id,
            operation.EntityType,
            operation.EntityId,
            operation.OperationType,
            operation.BaseVersion,
            Payload = JsonSerializer.Deserialize<JsonElement>(operation.Payload),
            operation.CreatedAt
        }).ToArray();
        using var response = await http.PostAsJsonAsync("v1/sync/push", new { workspaceId = operations[0].WorkspaceId, operations = wireOperations }, cancellationToken);
        return await ReadAsync<PushResult>(response, cancellationToken);
    }

    public async Task<PullResult> PullAsync(Guid workspaceId, string? cursor, CancellationToken cancellationToken = default)
    {
        await authorizer.ConfigureAsync(http, cancellationToken);
        var route = $"v1/sync/pull?workspaceId={workspaceId:D}&cursor={Uri.EscapeDataString(cursor ?? string.Empty)}";
        using var response = await http.GetAsync(route, cancellationToken);
        return await ReadAsync<PullResult>(response, cancellationToken);
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(await response.Content.ReadAsStringAsync(cancellationToken), null, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<T>(Json, cancellationToken) ?? throw new InvalidDataException("云端返回无效数据。");
    }
}
