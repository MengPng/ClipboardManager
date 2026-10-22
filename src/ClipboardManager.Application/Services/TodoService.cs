using ClipboardManager.Domain.Contracts;
using ClipboardManager.Domain.Models;

namespace ClipboardManager.Application.Services;

/// <summary>
/// Personal todos live in the local capture workspace. They are never redirected by the
/// config-space UI, and they are not queued for cloud sync.
/// </summary>
public sealed class TodoService(ITodoRepository repository, IWorkspaceService workspaceService)
{
    public const int MaxTitleLength = 200;
    public const int MaxTagNameLength = 20;

    public Task<TodoSnapshot> GetSnapshotAsync(
        TodoListFilter filter,
        Guid? tagId,
        CancellationToken cancellationToken = default) =>
        GetSnapshotAsync(filter, tagId, DateTimeOffset.Now, cancellationToken);

    public async Task<TodoSnapshot> GetSnapshotAsync(
        TodoListFilter filter,
        Guid? tagId,
        DateTimeOffset now,
        CancellationToken cancellationToken = default,
        TodoStatsPeriod statsPeriod = TodoStatsPeriod.All)
    {
        var workspaceId = await CaptureWorkspaceIdAsync(cancellationToken);
        var tags = await repository.GetTagsAsync(workspaceId, cancellationToken);
        var items = await repository.GetItemsAsync(workspaceId, cancellationToken);
        var tagMap = tags.ToDictionary(tag => tag.Id);
        var (dayStart, dayEnd) = LocalDayBounds(now);
        var summary = new TodoSummary(
            TodayCompleted: items.Count(item => item.IsCompleted && item.CompletedAt is { } completed && completed >= dayStart && completed < dayEnd),
            Pending: items.Count(item => !item.IsCompleted),
            TodayCreated: items.Count(item => item.CreatedAt >= dayStart && item.CreatedAt < dayEnd),
            Total: items.Count,
            Completed: items.Count(item => item.IsCompleted),
            CompletionRate: Rate(items.Count(item => item.IsCompleted), items.Count));
        var tagStats = tags.Select(tag =>
        {
            var tagged = items.Where(item => item.TagIds.Contains(tag.Id)).ToList();
            var completed = tagged.Count(item => item.IsCompleted);
            return new TodoTagStat(
                tag,
                tagged.Count(item => item.IsCompleted && item.CompletedAt is { } completedAt && completedAt >= dayStart && completedAt < dayEnd),
                completed,
                tagged.Count,
                tagged.Count(item => !item.IsCompleted),
                Rate(completed, tagged.Count));
        }).ToList();

        IEnumerable<TodoItem> visible = items;
        if (filter == TodoListFilter.Active) visible = visible.Where(item => !item.IsCompleted);
        else if (filter == TodoListFilter.Completed) visible = visible.Where(item => item.IsCompleted);
        if (tagId is { } selected)
        {
            if (!tagMap.ContainsKey(selected)) selected = Guid.Empty;
            else visible = visible.Where(item => item.TagIds.Contains(selected));
            if (selected == Guid.Empty) tagId = null;
        }

        var ordered = visible
            .OrderBy(item => item.IsCompleted)
            .ThenByDescending(item => item.IsCompleted ? item.CompletedAt ?? item.UpdatedAt : item.CreatedAt)
            .ToList();
        return new TodoSnapshot(ordered, tags, summary, tagStats);
    }

    public async Task<TodoItem> CreateAsync(
        string title,
        IReadOnlyList<Guid> tagIds,
        CancellationToken cancellationToken = default)
    {
        title = NormalizeTitle(title);
        var workspaceId = await CaptureWorkspaceIdAsync(cancellationToken);
        var known = (await repository.GetTagsAsync(workspaceId, cancellationToken)).Select(tag => tag.Id).ToHashSet();
        var selected = tagIds.Distinct().Where(known.Contains).ToArray();
        return await repository.CreateItemAsync(workspaceId, title, selected, cancellationToken);
    }

    public async Task<TodoItem> UpdateAsync(Guid itemId, string title, IReadOnlyList<Guid> tagIds, CancellationToken cancellationToken = default)
    {
        title = NormalizeTitle(title);
        var workspaceId = await CaptureWorkspaceIdAsync(cancellationToken);
        var known = (await repository.GetTagsAsync(workspaceId, cancellationToken)).Select(tag => tag.Id).ToHashSet();
        var selected = tagIds.Distinct().Where(known.Contains).ToArray();
        return await repository.UpdateItemAsync(workspaceId, itemId, title, selected, cancellationToken);
    }

    public async Task<TodoItem> SetCompletedAsync(Guid itemId, bool completed, CancellationToken cancellationToken = default) =>
        await repository.SetCompletedAsync(await CaptureWorkspaceIdAsync(cancellationToken), itemId, completed, DateTimeOffset.UtcNow, cancellationToken);

    public async Task DeleteAsync(Guid itemId, CancellationToken cancellationToken = default) =>
        await repository.DeleteItemAsync(await CaptureWorkspaceIdAsync(cancellationToken), itemId, cancellationToken);

    public async Task<int> ClearCompletedAsync(CancellationToken cancellationToken = default) =>
        await repository.ClearCompletedAsync(await CaptureWorkspaceIdAsync(cancellationToken), DateTimeOffset.UtcNow, cancellationToken);

    public async Task<TodoTag> CreateTagAsync(string name, string color, CancellationToken cancellationToken = default)
    {
        name = NormalizeTagName(name);
        color = TodoTagCatalog.NormalizeColor(color);
        return await repository.CreateTagAsync(await CaptureWorkspaceIdAsync(cancellationToken), name, color, cancellationToken);
    }

    public async Task<TodoTag> UpdateTagAsync(Guid tagId, string name, string color, CancellationToken cancellationToken = default)
    {
        name = NormalizeTagName(name);
        color = TodoTagCatalog.NormalizeColor(color);
        return await repository.UpdateTagAsync(await CaptureWorkspaceIdAsync(cancellationToken), tagId, name, color, cancellationToken);
    }

    public async Task DeleteTagAsync(Guid tagId, CancellationToken cancellationToken = default)
    {
        var workspaceId = await CaptureWorkspaceIdAsync(cancellationToken);
        await repository.DeleteTagAsync(workspaceId, tagId, cancellationToken);
    }

    public static bool IsCompletedToday(TodoItem item, DateTimeOffset now)
    {
        if (!item.IsCompleted || item.CompletedAt is null) return false;
        var (dayStart, dayEnd) = LocalDayBounds(now);
        return item.CompletedAt >= dayStart && item.CompletedAt < dayEnd;
    }

    private async Task<Guid> CaptureWorkspaceIdAsync(CancellationToken cancellationToken) =>
        (await workspaceService.GetCurrentAsync(cancellationToken)).Id;

    private static string NormalizeTitle(string title)
    {
        title = title.Trim();
        if (title.Length is < 1 or > MaxTitleLength)
            throw new ArgumentException($"待办内容长度必须为 1 到 {MaxTitleLength} 个字符。", nameof(title));
        return title;
    }

    private static string NormalizeTagName(string name)
    {
        name = name.Trim();
        if (name.Length is < 1 or > MaxTagNameLength)
            throw new ArgumentException($"标签名称长度必须为 1 到 {MaxTagNameLength} 个字符。", nameof(name));
        return name;
    }

    private static (DateTimeOffset Start, DateTimeOffset End) LocalDayBounds(DateTimeOffset now)
    {
        var local = now.ToLocalTime();
        var start = new DateTimeOffset(local.Year, local.Month, local.Day, 0, 0, 0, local.Offset);
        return (start, start.AddDays(1));
    }

    private static double Rate(int completed, int total) => total == 0 ? 0 : Math.Round(completed * 100d / total, 1);
}
