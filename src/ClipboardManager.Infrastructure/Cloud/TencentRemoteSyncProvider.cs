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
        var wire = await ReadAsync<PushWireResponse>(response, cancellationToken);
        return new(wire.Succeeded, wire.ServerVersions, Conflicts: wire.Conflicts?.Select(x =>
            new RemoteConflict(x.OperationId,x.EntityId,x.ExpectedVersion,x.ActualVersion,
                x.RemotePayload.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null ? "{}" : x.RemotePayload.GetRawText())).ToArray());
    }

    public async Task<PullResult> PullAsync(Guid workspaceId, string? cursor, CancellationToken cancellationToken = default)
    {
        await authorizer.ConfigureAsync(http, cancellationToken);
        var route = $"v1/sync/pull?workspaceId={workspaceId:D}&cursor={Uri.EscapeDataString(cursor ?? string.Empty)}";
        using var response = await http.GetAsync(route, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(await response.Content.ReadAsStringAsync(cancellationToken), null, response.StatusCode);
        var wire = await response.Content.ReadFromJsonAsync<PullWireResponse>(Json, cancellationToken)
            ?? throw new InvalidDataException("云端返回无效数据。");
        return new(wire.Succeeded, wire.NextCursor,
            wire.Changes.Select(x => new RemoteChange(x.EntityType, x.EntityId, x.ServerVersion,
                x.Payload.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined ? "{}" : x.Payload.GetRawText(), x.IsDeleted)).ToArray());
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(await response.Content.ReadAsStringAsync(cancellationToken), null, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<T>(Json, cancellationToken) ?? throw new InvalidDataException("云端返回无效数据。");
    }

    private sealed record PullWireResponse(bool Succeeded, string? NextCursor, IReadOnlyList<PullWireChange> Changes);
    private sealed record PushWireResponse(bool Succeeded, Dictionary<Guid,long> ServerVersions, IReadOnlyList<ConflictWire>? Conflicts);
    private sealed record ConflictWire(Guid OperationId, Guid EntityId, long ExpectedVersion, long ActualVersion, JsonElement RemotePayload);
    private sealed record PullWireChange(string EntityType, Guid EntityId, long ServerVersion, JsonElement Payload, bool IsDeleted);
}
