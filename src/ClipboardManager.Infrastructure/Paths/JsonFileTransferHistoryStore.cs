using System.Text.Json;
using ClipboardManager.Domain.Contracts;
using ClipboardManager.Domain.Models;

namespace ClipboardManager.Infrastructure.Paths;

/// <summary>
/// 传输记录存到 <c>transfers.json</c>（与 settings.json 同目录）：只有已结束的任务，
/// 不含文件内容。文件损坏或读不了时返回空列表，写入用临时文件 + 替换保证不留半截文件。
/// </summary>
public sealed class JsonFileTransferHistoryStore(AppPaths paths) : IFileTransferHistoryStore
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
    private readonly object _gate = new();

    public string FilePath => Path.Combine(paths.Root, "transfers.json");

    public IReadOnlyList<FileTransferSnapshot> Load()
    {
        lock (_gate)
        {
            try
            {
                if (!File.Exists(FilePath)) return [];
                var items = JsonSerializer.Deserialize<List<FileTransferSnapshot>>(File.ReadAllText(FilePath), Options) ?? [];
                return items.Where(item => item.IsFinished && !string.IsNullOrEmpty(item.FileName)).ToList();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
            {
                return [];
            }
        }
    }

    public void Save(IReadOnlyList<FileTransferSnapshot> finished)
    {
        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(paths.Root);
                var temporary = FilePath + ".tmp";
                File.WriteAllText(temporary, JsonSerializer.Serialize(finished, Options));
                File.Move(temporary, FilePath, true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // 记录写不进去只影响重启后的显示，不影响传输本身。
            }
        }
    }
}
