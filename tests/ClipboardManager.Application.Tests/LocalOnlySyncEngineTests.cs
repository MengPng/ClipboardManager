using ClipboardManager.Application.Services;
using ClipboardManager.Domain.Models;

namespace ClipboardManager.Application.Tests;

public sealed class LocalOnlySyncEngineTests
{
    [Fact]
    public async Task SynchronizeAsync_ReportsLocalOnlyWithoutPretendingSuccess()
    {
        var engine = new LocalOnlySyncEngine();
        var workspaceId = Guid.NewGuid();

        var result = await engine.SynchronizeAsync(workspaceId, SyncReason.UserRequested);

        Assert.False(result.Succeeded);
        Assert.Equal(SyncState.LocalOnly, result.Status.State);
        Assert.Equal(workspaceId, result.Status.WorkspaceId);
    }
}
