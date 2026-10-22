using ClipboardManager.Domain.Contracts;
using ClipboardManager.Domain.Models;
using Microsoft.Data.Sqlite;
using System.Globalization;

namespace ClipboardManager.Infrastructure.Data;

public sealed class SqliteTodoRepository(SqliteConnectionFactory connectionFactory) : ITodoRepository
{
    public async Task<IReadOnlyList<TodoTag>> GetTagsAsync(Guid workspaceId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT * FROM todo_tags
            WHERE workspace_id=$workspace AND deleted_at IS NULL
            ORDER BY sort_key, created_at, id;
            """;
        command.Parameters.AddWithValue("$workspace", workspaceId.ToString("D"));
        var tags = new List<TodoTag>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) tags.Add(ReadTag(reader));
        return tags;
    }

    public async Task<TodoTag> CreateTagAsync(
        Guid workspaceId,
        string name,
        string color,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();
        await EnsureUniqueTagNameAsync(connection, transaction, workspaceId, name, null, cancellationToken);
        var sortKey = await NextTagSortKeyAsync(connection, transaction, workspaceId, cancellationToken);
        var now = DateTimeOffset.UtcNow;
        var tag = new TodoTag
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspaceId,
            Name = name,
            Color = color,
            SortKey = sortKey,
            CreatedAt = now,
            UpdatedAt = now
        };
        await InsertTagAsync(connection, transaction, tag, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return tag;
    }

    public async Task<TodoTag> UpdateTagAsync(
        Guid workspaceId,
        Guid tagId,
        string name,
        string color,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();
        await EnsureUniqueTagNameAsync(connection, transaction, workspaceId, name, tagId, cancellationToken);
        var now = DateTimeOffset.UtcNow;
        await using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = "UPDATE todo_tags SET name=$name, color=$color, updated_at=$now WHERE workspace_id=$workspace AND id=$id AND deleted_at IS NULL;";
        AddIdentity(update, workspaceId, tagId);
        update.Parameters.AddWithValue("$name", name);
        update.Parameters.AddWithValue("$color", color);
        update.Parameters.AddWithValue("$now", Format(now));
        if (await update.ExecuteNonQueryAsync(cancellationToken) == 0)
            throw new InvalidOperationException("标签不存在或不属于当前空间。");
        await transaction.CommitAsync(cancellationToken);
        return (await GetTagsAsync(workspaceId, cancellationToken)).Single(tag => tag.Id == tagId);
    }

    public async Task DeleteTagAsync(Guid workspaceId, Guid tagId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();
        await using (var find = connection.CreateCommand())
        {
            find.Transaction = transaction;
            find.CommandText = "SELECT COUNT(*) FROM todo_tags WHERE workspace_id=$workspace AND id=$id AND deleted_at IS NULL;";
            AddIdentity(find, workspaceId, tagId);
            if (Convert.ToInt32(await find.ExecuteScalarAsync(cancellationToken)) == 0)
                throw new InvalidOperationException("标签不存在或不属于当前空间。");
        }

        var now = Format(DateTimeOffset.UtcNow);
        await ExecuteAsync(connection, transaction,
            "UPDATE todo_tags SET deleted_at=$now, updated_at=$now WHERE workspace_id=$workspace AND id=$id AND deleted_at IS NULL;",
            cancellationToken,
            ("$workspace", workspaceId.ToString("D")),
            ("$id", tagId.ToString("D")),
            ("$now", now));
        await ExecuteAsync(connection, transaction,
            "DELETE FROM todo_item_tags WHERE workspace_id=$workspace AND tag_id=$id;",
            cancellationToken,
            ("$workspace", workspaceId.ToString("D")),
            ("$id", tagId.ToString("D")));
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<TodoItem>> GetItemsAsync(Guid workspaceId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var tagsByItem = new Dictionary<Guid, List<Guid>>();
        await using (var tagCommand = connection.CreateCommand())
        {
            tagCommand.CommandText = """
                SELECT todo_id, tag_id FROM todo_item_tags
                WHERE workspace_id=$workspace
                ORDER BY todo_id, tag_id;
                """;
            tagCommand.Parameters.AddWithValue("$workspace", workspaceId.ToString("D"));
            await using var tagReader = await tagCommand.ExecuteReaderAsync(cancellationToken);
            while (await tagReader.ReadAsync(cancellationToken))
            {
                var todoId = Guid.Parse(tagReader.GetString(0));
                if (!tagsByItem.TryGetValue(todoId, out var list))
                {
                    list = [];
                    tagsByItem[todoId] = list;
                }

                list.Add(Guid.Parse(tagReader.GetString(1)));
            }
        }

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT * FROM todo_items
            WHERE workspace_id=$workspace AND deleted_at IS NULL
            ORDER BY is_completed, created_at DESC, id;
            """;
        command.Parameters.AddWithValue("$workspace", workspaceId.ToString("D"));
        var items = new List<TodoItem>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var item = ReadItem(reader);
            items.Add(item with
            {
                TagIds = tagsByItem.TryGetValue(item.Id, out var ids) ? ids : []
            });
        }

        return items;
    }

    public async Task<TodoItem> CreateItemAsync(
        Guid workspaceId,
        string title,
        IReadOnlyList<Guid> tagIds,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();
        var now = DateTimeOffset.UtcNow;
        var item = new TodoItem
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspaceId,
            Title = title,
            CreatedAt = now,
            UpdatedAt = now,
            TagIds = tagIds
        };
        await using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO todo_items(id, workspace_id, title, is_completed, completed_at, created_at, updated_at, deleted_at)
                VALUES($id, $workspace, $title, 0, NULL, $created, $updated, NULL);
                """;
            insert.Parameters.AddWithValue("$id", item.Id.ToString("D"));
            insert.Parameters.AddWithValue("$workspace", workspaceId.ToString("D"));
            insert.Parameters.AddWithValue("$title", title);
            insert.Parameters.AddWithValue("$created", Format(now));
            insert.Parameters.AddWithValue("$updated", Format(now));
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        await ReplaceItemTagsAsync(connection, transaction, workspaceId, item.Id, tagIds, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return item;
    }

    public async Task<TodoItem> UpdateItemAsync(
        Guid workspaceId,
        Guid itemId,
        string title,
        IReadOnlyList<Guid> tagIds,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();
        var existing = await RequireItemAsync(connection, transaction, workspaceId, itemId, cancellationToken);
        var now = DateTimeOffset.UtcNow;
        await using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = "UPDATE todo_items SET title=$title, updated_at=$now WHERE workspace_id=$workspace AND id=$id AND deleted_at IS NULL;";
            update.Parameters.AddWithValue("$title", title);
            update.Parameters.AddWithValue("$now", Format(now));
            AddIdentity(update, workspaceId, itemId);
            await update.ExecuteNonQueryAsync(cancellationToken);
        }
        await ReplaceItemTagsAsync(connection, transaction, workspaceId, itemId, tagIds, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return existing with { Title = title, TagIds = tagIds.Distinct().ToArray(), UpdatedAt = now };
    }

    public async Task<TodoItem> SetCompletedAsync(
        Guid workspaceId,
        Guid itemId,
        bool completed,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();
        var existing = await RequireItemAsync(connection, transaction, workspaceId, itemId, cancellationToken);
        var completedAt = completed ? now : (DateTimeOffset?)null;
        await using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE todo_items
                SET is_completed=$done, completed_at=$completed, updated_at=$now
                WHERE workspace_id=$workspace AND id=$id AND deleted_at IS NULL;
                """;
            update.Parameters.AddWithValue("$done", completed ? 1 : 0);
            update.Parameters.AddWithValue("$completed", Db(completedAt is null ? null : Format(completedAt.Value)));
            update.Parameters.AddWithValue("$now", Format(now));
            AddIdentity(update, workspaceId, itemId);
            await update.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return existing with
        {
            IsCompleted = completed,
            CompletedAt = completedAt,
            UpdatedAt = now
        };
    }

    public async Task DeleteItemAsync(Guid workspaceId, Guid itemId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();
        await RequireItemAsync(connection, transaction, workspaceId, itemId, cancellationToken);
        var now = Format(DateTimeOffset.UtcNow);
        await ExecuteAsync(connection, transaction,
            "UPDATE todo_items SET deleted_at=$now, updated_at=$now WHERE workspace_id=$workspace AND id=$id AND deleted_at IS NULL;",
            cancellationToken,
            ("$workspace", workspaceId.ToString("D")),
            ("$id", itemId.ToString("D")),
            ("$now", now));
        await ExecuteAsync(connection, transaction,
            "DELETE FROM todo_item_tags WHERE workspace_id=$workspace AND todo_id=$id;",
            cancellationToken,
            ("$workspace", workspaceId.ToString("D")),
            ("$id", itemId.ToString("D")));
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<int> ClearCompletedAsync(
        Guid workspaceId,
        DateTimeOffset deletedAt,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();
        await using var select = connection.CreateCommand();
        select.Transaction = transaction;
        select.CommandText = """
            SELECT id FROM todo_items
            WHERE workspace_id=$workspace AND deleted_at IS NULL AND is_completed=1;
            """;
        select.Parameters.AddWithValue("$workspace", workspaceId.ToString("D"));
        var ids = new List<Guid>();
        await using (var reader = await select.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken)) ids.Add(Guid.Parse(reader.GetString(0)));
        }

        if (ids.Count == 0)
        {
            await transaction.CommitAsync(cancellationToken);
            return 0;
        }

        var now = Format(deletedAt);
        await ExecuteAsync(connection, transaction,
            "UPDATE todo_items SET deleted_at=$now, updated_at=$now WHERE workspace_id=$workspace AND deleted_at IS NULL AND is_completed=1;",
            cancellationToken,
            ("$workspace", workspaceId.ToString("D")),
            ("$now", now));
        await ExecuteAsync(connection, transaction,
            """
            DELETE FROM todo_item_tags
            WHERE workspace_id=$workspace AND todo_id IN (
                SELECT id FROM todo_items WHERE workspace_id=$workspace AND deleted_at=$now AND is_completed=1);
            """,
            cancellationToken,
            ("$workspace", workspaceId.ToString("D")),
            ("$now", now));
        await transaction.CommitAsync(cancellationToken);
        return ids.Count;
    }

    private async Task ReplaceItemTagsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid workspaceId,
        Guid todoId,
        IReadOnlyList<Guid> tagIds,
        CancellationToken cancellationToken)
    {
        await ExecuteAsync(connection, transaction,
            "DELETE FROM todo_item_tags WHERE workspace_id=$workspace AND todo_id=$id;",
            cancellationToken,
            ("$workspace", workspaceId.ToString("D")),
            ("$id", todoId.ToString("D")));
        foreach (var tagId in tagIds.Distinct())
        {
            await using var exists = connection.CreateCommand();
            exists.Transaction = transaction;
            exists.CommandText = "SELECT COUNT(*) FROM todo_tags WHERE workspace_id=$workspace AND id=$id AND deleted_at IS NULL;";
            AddIdentity(exists, workspaceId, tagId);
            if (Convert.ToInt32(await exists.ExecuteScalarAsync(cancellationToken)) == 0)
                throw new InvalidOperationException("标签不存在或不属于当前空间。");

            await using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO todo_item_tags(todo_id, tag_id, workspace_id)
                VALUES($todo, $tag, $workspace);
                """;
            insert.Parameters.AddWithValue("$todo", todoId.ToString("D"));
            insert.Parameters.AddWithValue("$tag", tagId.ToString("D"));
            insert.Parameters.AddWithValue("$workspace", workspaceId.ToString("D"));
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task<TodoItem> RequireItemAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid workspaceId,
        Guid itemId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT * FROM todo_items WHERE workspace_id=$workspace AND id=$id AND deleted_at IS NULL;";
        AddIdentity(command, workspaceId, itemId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            throw new InvalidOperationException("待办不存在或不属于当前空间。");
        return ReadItem(reader);
    }

    private static async Task InsertTagAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        TodoTag tag,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO todo_tags(id, workspace_id, name, color, is_preset, sort_key, created_at, updated_at, deleted_at)
            VALUES($id, $workspace, $name, $color, $preset, $sort, $created, $updated, NULL);
            """;
        command.Parameters.AddWithValue("$id", tag.Id.ToString("D"));
        command.Parameters.AddWithValue("$workspace", tag.WorkspaceId.ToString("D"));
        command.Parameters.AddWithValue("$name", tag.Name);
        command.Parameters.AddWithValue("$color", tag.Color);
        command.Parameters.AddWithValue("$preset", tag.IsPreset ? 1 : 0);
        command.Parameters.AddWithValue("$sort", tag.SortKey);
        command.Parameters.AddWithValue("$created", Format(tag.CreatedAt));
        command.Parameters.AddWithValue("$updated", Format(tag.UpdatedAt));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<HashSet<string>> ReadTagNamesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid workspaceId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT name FROM todo_tags WHERE workspace_id=$workspace AND deleted_at IS NULL;";
        command.Parameters.AddWithValue("$workspace", workspaceId.ToString("D"));
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) names.Add(reader.GetString(0));
        return names;
    }

    private static async Task EnsureUniqueTagNameAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid workspaceId,
        string name,
        Guid? exceptId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT COUNT(*) FROM todo_tags
            WHERE workspace_id=$workspace AND name=$name COLLATE NOCASE AND deleted_at IS NULL
              AND ($except IS NULL OR id<>$except);
            """;
        command.Parameters.AddWithValue("$workspace", workspaceId.ToString("D"));
        command.Parameters.AddWithValue("$name", name);
        command.Parameters.AddWithValue("$except", Db(exceptId?.ToString("D")));
        if (Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken)) > 0)
            throw new InvalidOperationException("标签名称不能重复。");
    }

    private static async Task<double> NextTagSortKeyAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid workspaceId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COALESCE(MAX(sort_key), -1) FROM todo_tags WHERE workspace_id=$workspace AND deleted_at IS NULL;";
        command.Parameters.AddWithValue("$workspace", workspaceId.ToString("D"));
        return Convert.ToDouble(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) + 1;
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = connectionFactory.Create();
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private static async Task ExecuteAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        CancellationToken cancellationToken,
        params (string Name, object Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void AddIdentity(SqliteCommand command, Guid workspaceId, Guid id)
    {
        command.Parameters.AddWithValue("$workspace", workspaceId.ToString("D"));
        command.Parameters.AddWithValue("$id", id.ToString("D"));
    }

    private static TodoTag ReadTag(SqliteDataReader reader) => new()
    {
        Id = Guid.Parse(reader.GetString(reader.GetOrdinal("id"))),
        WorkspaceId = Guid.Parse(reader.GetString(reader.GetOrdinal("workspace_id"))),
        Name = reader.GetString(reader.GetOrdinal("name")),
        Color = reader.GetString(reader.GetOrdinal("color")),
        IsPreset = reader.GetInt32(reader.GetOrdinal("is_preset")) != 0,
        SortKey = reader.GetDouble(reader.GetOrdinal("sort_key")),
        CreatedAt = Date(reader, "created_at")!.Value,
        UpdatedAt = Date(reader, "updated_at")!.Value,
        DeletedAt = Date(reader, "deleted_at")
    };

    private static TodoItem ReadItem(SqliteDataReader reader) => new()
    {
        Id = Guid.Parse(reader.GetString(reader.GetOrdinal("id"))),
        WorkspaceId = Guid.Parse(reader.GetString(reader.GetOrdinal("workspace_id"))),
        Title = reader.GetString(reader.GetOrdinal("title")),
        IsCompleted = reader.GetInt32(reader.GetOrdinal("is_completed")) != 0,
        CompletedAt = Date(reader, "completed_at"),
        CreatedAt = Date(reader, "created_at")!.Value,
        UpdatedAt = Date(reader, "updated_at")!.Value,
        DeletedAt = Date(reader, "deleted_at")
    };

    private static object Db(object? value) => value ?? DBNull.Value;
    private static string Format(DateTimeOffset value) => value.ToUniversalTime().ToString("O");
    private static string? StringOrNull(SqliteDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }

    private static DateTimeOffset? Date(SqliteDataReader reader, string name)
    {
        var text = StringOrNull(reader, name);
        return text is null ? null : DateTimeOffset.Parse(text, null, DateTimeStyles.RoundtripKind);
    }
}
