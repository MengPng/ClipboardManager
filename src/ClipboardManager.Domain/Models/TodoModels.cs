namespace ClipboardManager.Domain.Models;

public enum TodoListFilter
{
    All = 0,
    Active = 1,
    Completed = 2
}

public enum TodoStatsPeriod
{
    All = 0,
    Week = 1,
    Month = 2,
    Year = 3
}

public sealed record TodoTag
{
    public required Guid Id { get; init; }
    public required Guid WorkspaceId { get; init; }
    public required string Name { get; init; }
    public required string Color { get; init; }
    public bool IsPreset { get; init; }
    public required double SortKey { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required DateTimeOffset UpdatedAt { get; init; }
    public DateTimeOffset? DeletedAt { get; init; }
}

public sealed record TodoItem
{
    public required Guid Id { get; init; }
    public required Guid WorkspaceId { get; init; }
    public required string Title { get; init; }
    public bool IsCompleted { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required DateTimeOffset UpdatedAt { get; init; }
    public DateTimeOffset? DeletedAt { get; init; }
    public IReadOnlyList<Guid> TagIds { get; init; } = [];
}

public sealed record TodoSummary(
    int TodayCompleted,
    int Pending,
    int TodayCreated,
    int Total,
    int Completed,
    double CompletionRate);

public sealed record TodoTagStat(
    TodoTag Tag,
    int TodayCompleted,
    int Completed,
    int Total,
    int Pending,
    double CompletionRate);

public sealed record TodoSnapshot(
    IReadOnlyList<TodoItem> Items,
    IReadOnlyList<TodoTag> Tags,
    TodoSummary Summary,
    IReadOnlyList<TodoTagStat> TagStats);

public static class TodoTagCatalog
{
    public static readonly IReadOnlyList<string> Palette =
    [
        "#0F766E", "#2563EB", "#7C3AED", "#C026D3", "#E11D48",
        "#EA580C", "#D97706", "#15803D", "#0891B2", "#57534E"
    ];

    public static string NormalizeColor(string? color)
    {
        var value = (color ?? string.Empty).Trim();
        if (value.Length == 7 && value[0] == '#' &&
            value.Skip(1).All(char.IsAsciiHexDigit))
        {
            return value.ToUpperInvariant();
        }

        throw new ArgumentException("请从色板选择有效颜色。", nameof(color));
    }
}
