using System.Text.Json;
using ClipboardManager.Domain.Models;

namespace ClipboardManager.Application.Services.Updates;

/// <summary>
/// 解析 latest.json（ADR-0005 schema 1）。不认识的 schema、产品名或格式一律返回 null，
/// 调用方按"无更新"处理，绝不因为清单异常而下载任何东西。
/// </summary>
public static class AppUpdateManifestReader
{
    public const int SupportedSchema = 1;
    public const string Product = "ClipboardManager";
    /// <summary>单文件 EXE 的合理上限，防止被错误清单诱导下载巨大文件。</summary>
    public const long MaxExecutableBytes = 512L * 1024 * 1024;

    public sealed record Result(AppUpdateManifest? Manifest, string? Reason);

    public static Result Read(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return Fail("清单不是 JSON 对象");
            if (!root.TryGetProperty("schema", out var schema) || schema.ValueKind != JsonValueKind.Number || !schema.TryGetInt32(out var schemaValue))
                return Fail("清单缺少 schema");
            if (schemaValue != SupportedSchema) return Fail($"不支持的清单 schema {schemaValue}");
            if (String(root, "product") != Product) return Fail("清单产品名不符");

            var version = ParseVersion(String(root, "version"));
            if (version is null) return Fail("清单版本号格式无效");
            if (!TryHttpsUri(String(root, "pageUrl"), out var pageUrl)) return Fail("下载页地址无效");

            var title = String(root, "title")?.Trim();
            var notes = new List<string>();
            if (root.TryGetProperty("notes", out var notesElement) && notesElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var note in notesElement.EnumerateArray())
                {
                    if (note.ValueKind == JsonValueKind.String && note.GetString() is { Length: > 0 } text) notes.Add(text.Trim());
                    if (notes.Count == 20) break;
                }
            }

            DateTimeOffset? releasedAt = root.TryGetProperty("releasedAt", out var released) &&
                                         released.ValueKind == JsonValueKind.String &&
                                         DateTimeOffset.TryParse(released.GetString(), out var parsed) ? parsed : null;

            AppUpdateFile? executable = null;
            if (root.TryGetProperty("files", out var files) && files.ValueKind == JsonValueKind.Array)
            {
                foreach (var file in files.EnumerateArray())
                {
                    if (file.ValueKind != JsonValueKind.Object || String(file, "kind") != "exe") continue;
                    executable = ReadExecutable(file);
                    break;
                }
            }

            return new Result(new AppUpdateManifest(version, string.IsNullOrWhiteSpace(title) ? $"版本 {version}" : title!,
                notes, pageUrl!, releasedAt, executable), null);
        }
        catch (JsonException)
        {
            return Fail("清单不是有效的 JSON");
        }
    }

    /// <summary>只接受 MAJOR.MINOR.PATCH，统一成三段便于与程序集版本比较。</summary>
    public static Version? ParseVersion(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var parts = value.Trim().Split('.');
        if (parts.Length != 3) return null;
        var numbers = new int[3];
        for (var i = 0; i < 3; i++)
        {
            if (parts[i].Length is 0 or > 6 || !parts[i].All(char.IsAsciiDigit) || !int.TryParse(parts[i], out numbers[i])) return null;
        }
        return new Version(numbers[0], numbers[1], numbers[2]);
    }

    /// <summary>把程序集版本（可能带第四段）规范为三段。</summary>
    public static Version Normalize(Version version) =>
        new(version.Major, version.Minor, Math.Max(version.Build, 0));

    private static AppUpdateFile? ReadExecutable(JsonElement file)
    {
        var name = String(file, "name");
        if (string.IsNullOrWhiteSpace(name) || name.Length > 200 ||
            name.IndexOfAny(['/', '\\', ':']) >= 0 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            name.StartsWith('.') || !name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            return null;
        if (!TryHttpsUri(String(file, "url"), out var url)) return null;
        if (!file.TryGetProperty("size", out var sizeElement) || !sizeElement.TryGetInt64(out var size) || size <= 0 || size > MaxExecutableBytes)
            return null;
        var sha256 = String(file, "sha256")?.Trim().ToLowerInvariant();
        if (sha256 is not { Length: 64 } || !sha256.All(char.IsAsciiHexDigitLower)) return null;
        return new AppUpdateFile("exe", name, url!, size, sha256);
    }

    private static string? String(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool TryHttpsUri(string? value, out Uri? uri) =>
        Uri.TryCreate(value, UriKind.Absolute, out uri) && uri.Scheme == Uri.UriSchemeHttps;

    private static Result Fail(string reason) => new(null, reason);
}
