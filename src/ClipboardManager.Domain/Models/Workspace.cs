namespace ClipboardManager.Domain.Models;

public enum WorkspaceType
{
    Local = 0,
    Personal = 1,
    Team = 2
}

public sealed record Workspace(
    Guid Id,
    WorkspaceType Type,
    string Name,
    Guid? OwnerId,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);
