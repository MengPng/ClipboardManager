using System.Text.RegularExpressions;

namespace ClipboardManager.Application.Services.FileSearch;

public enum FileSearchKind
{
    /// <summary>Nothing usable was selected.</summary>
    Empty,
    /// <summary>The selection is itself an absolute path; no index search is needed.</summary>
    DirectPath,
    /// <summary>The selection names a file with an extension, e.g. "报价单V3.xlsx".</summary>
    FileName,
    /// <summary>Free keywords, every one must appear in the path.</summary>
    Keywords,
}

/// <summary>
/// What 划词查找 looks for. <see cref="IndexQuery"/> is an Everything search string built only from
/// the selection; Everything operators the user did not intend are stripped or quoted.
/// </summary>
public sealed record FileSearchRequest(
    FileSearchKind Kind, string IndexQuery, string? FileName, string? Stem, string? Extension,
    IReadOnlyList<string> Keywords, string? DirectPath, string? Problem = null)
{
    public static FileSearchRequest None(string problem) => new(FileSearchKind.Empty, string.Empty, null, null, null, [], null, problem);
}

public sealed record FileSearchHit(string FullPath, bool IsFolder, long? Size, DateTime? ModifiedUtc)
{
    public string Name => Path.GetFileName(FullPath.TrimEnd('\\', '/')) is { Length: > 0 } name ? name : FullPath;
    public string? Directory => Path.GetDirectoryName(FullPath.TrimEnd('\\', '/'));
}

public static partial class FileSearchQuery
{
    public const int MaxQueryLength = 512;
    public const int MaxKeywordSelectionLength = 120;
    public const int MaxKeywords = 6;

    private static readonly char[] Wrappers = ['"', '\'', '“', '”', '‘', '’', '《', '》', '「', '」', '『', '』', '【', '】', '[', ']', '(', ')', '（', '）', '<', '>', '`'];

    public static FileSearchRequest Parse(string? selection)
    {
        if (string.IsNullOrWhiteSpace(selection)) return FileSearchRequest.None("没有选中文字。");
        var text = selection.Replace(' ', ' ').Trim();

        var singleLine = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (singleLine.Length == 1)
        {
            var candidate = Clean(singleLine[0]);
            if (IsAbsolutePath(candidate))
            {
                var name = Path.GetFileName(candidate.TrimEnd('\\', '/'));
                return new FileSearchRequest(FileSearchKind.DirectPath, string.Empty, name, null, null, [], candidate);
            }
        }

        var fileNames = FileNamePattern().Matches(text)
            .Select(match => Clean(match.Value))
            .Where(IsPlausibleFileName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (fileNames.Count > 0)
        {
            // The longest name is usually the real one ("最终版 报价单V3.xlsx" vs "V3.xlsx").
            var fileName = fileNames.OrderByDescending(name => name.Length).First();
            var extension = Path.GetExtension(fileName).TrimStart('.');
            var stem = Path.GetFileNameWithoutExtension(fileName);
            var query = $"\"{stem}\" *.{extension}";
            if (query.Length > MaxQueryLength) return FileSearchRequest.None("文件名太长，无法搜索。");
            return new FileSearchRequest(FileSearchKind.FileName, query, fileName, stem, extension, [], null);
        }

        if (text.Length > MaxKeywordSelectionLength)
            return FileSearchRequest.None("选中的文字太长，请只选文件名或关键词。");
        var keywords = KeywordSplitPattern().Split(text)
            .Select(word => word.Trim(Wrappers).Trim())
            .Where(word => word.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaxKeywords)
            .ToList();
        if (keywords.Count == 0 || keywords.Sum(word => word.Length) < 2)
            return FileSearchRequest.None("关键词太短，请多选几个字。");
        var keywordQuery = string.Join(' ', keywords.Select(word => $"\"{word}\""));
        if (keywordQuery.Length > MaxQueryLength) return FileSearchRequest.None("关键词太长，无法搜索。");
        return new FileSearchRequest(FileSearchKind.Keywords, keywordQuery, null, null, null, keywords, null);
    }

    public static bool IsAbsolutePath(string value) =>
        value.Length <= 32767 && value.IndexOfAny(Path.GetInvalidPathChars()) < 0 &&
        (DrivePathPattern().IsMatch(value) || UncPathPattern().IsMatch(value));

    private static bool IsPlausibleFileName(string name)
    {
        if (name.Length is < 3 or > 255) return false;
        var extension = Path.GetExtension(name).TrimStart('.');
        var stem = Path.GetFileNameWithoutExtension(name);
        // "3.5", "v1.2" and domain names like "example.com" are not file names worth searching.
        return stem.Length > 0 && extension.Any(char.IsAsciiLetter) && !NotFileExtensions.Contains(extension);
    }

    // Domain suffixes that often appear in chat text; real files rarely use them.
    private static readonly HashSet<string> NotFileExtensions = new(StringComparer.OrdinalIgnoreCase)
    { "com", "cn", "net", "org", "io", "gov", "edu", "top", "xyz", "info" };

    private static string Clean(string value) => TrimTrailingPunctuation(TrimTrailingPunctuation(value.Trim()).Trim(Wrappers));

    private static string TrimTrailingPunctuation(string value) => value.TrimEnd('。', '，', ',', '；', ';', '！', '!', '？', '?', '、', '.', ' ', ':', '：');

    [GeneratedRegex(@"^[A-Za-z]:[\\/]")]
    private static partial Regex DrivePathPattern();

    [GeneratedRegex(@"^\\\\[^\\/]+[\\/][^\\/]+")]
    private static partial Regex UncPathPattern();

    // A file name token: no separators or spaces (chat text uses spaces between words), ends in .ext (1–8 chars).
    // "Q3 report.docx" yields "report.docx", which still finds the file by substring.
    [GeneratedRegex(@"[^\s\\/:*?""<>|，,。；;：！!？、“”‘’《》「」『』【】（）()\[\]]+\.[A-Za-z0-9]{1,8}(?![A-Za-z0-9])")]
    private static partial Regex FileNamePattern();

    [GeneratedRegex(@"[\s\\/:*?""<>|，,。；;：！!？、“”‘’《》「」『』【】（）()\[\]]+")]
    private static partial Regex KeywordSplitPattern();
}

/// <summary>Orders index hits so the file the user most likely meant comes first.</summary>
public static class FileSearchRanker
{
    public const int MaxResults = 20;

    private static readonly string[] HiddenSegments = [@"\$RECYCLE.BIN\", @"\System Volume Information\", @"\$Recycle.Bin\"];
    private static readonly string[] NoisySegments =
        [@"\node_modules\", @"\.git\", @"\AppData\Local\Temp\", @"\site-packages\", @"\Windows\WinSxS\", @"\obj\", @"\.cache\", @"\Windows\Installer\"];
    private static readonly string[] ChatFolders =
        [@"\WeChat Files\", @"\xwechat_files\", @"\WXWork\", @"\Tencent Files\", @"\Downloads\", @"\Desktop\"];

    public static IReadOnlyList<FileSearchHit> Rank(IEnumerable<FileSearchHit> hits, FileSearchRequest request, DateTime nowUtc) =>
        hits.Where(hit => !HiddenSegments.Any(segment => hit.FullPath.Contains(segment, StringComparison.OrdinalIgnoreCase)))
            .DistinctBy(hit => hit.FullPath, StringComparer.OrdinalIgnoreCase)
            .Select(hit => (Hit: hit, Score: Score(hit, request, nowUtc)))
            .OrderByDescending(entry => entry.Score)
            .ThenByDescending(entry => entry.Hit.ModifiedUtc ?? DateTime.MinValue)
            .Take(MaxResults)
            .Select(entry => entry.Hit)
            .ToList();

    internal static int Score(FileSearchHit hit, FileSearchRequest request, DateTime nowUtc)
    {
        var name = hit.Name;
        var score = 0;
        switch (request.Kind)
        {
            case FileSearchKind.FileName when request.FileName is not null && request.Stem is not null:
                var stem = Path.GetFileNameWithoutExtension(name);
                var sameExtension = string.Equals(Path.GetExtension(name).TrimStart('.'), request.Extension, StringComparison.OrdinalIgnoreCase);
                if (string.Equals(name, request.FileName, StringComparison.OrdinalIgnoreCase)) score += 100;
                // Chat apps save duplicates as "name(1).ext".
                else if (sameExtension && IsNumberedCopy(stem, request.Stem)) score += 80;
                else if (stem.StartsWith(request.Stem, StringComparison.OrdinalIgnoreCase)) score += 40;
                else if (name.Contains(request.Stem, StringComparison.OrdinalIgnoreCase)) score += 20;
                if (hit.IsFolder) score -= 30;
                break;
            case FileSearchKind.Keywords:
                foreach (var keyword in request.Keywords)
                {
                    if (name.Contains(keyword, StringComparison.OrdinalIgnoreCase)) score += 15;
                    else if (hit.FullPath.Contains(keyword, StringComparison.OrdinalIgnoreCase)) score += 5;
                }
                if (request.Keywords.Count == 1 && string.Equals(Path.GetFileNameWithoutExtension(name), request.Keywords[0], StringComparison.OrdinalIgnoreCase))
                    score += 20;
                break;
        }

        if (NoisySegments.Any(segment => hit.FullPath.Contains(segment, StringComparison.OrdinalIgnoreCase))) score -= 25;
        if (ChatFolders.Any(segment => hit.FullPath.Contains(segment, StringComparison.OrdinalIgnoreCase))) score += 6;
        if (hit.ModifiedUtc is { } modified)
        {
            var age = nowUtc - modified;
            if (age <= TimeSpan.FromDays(1)) score += 12;
            else if (age <= TimeSpan.FromDays(7)) score += 8;
            else if (age <= TimeSpan.FromDays(30)) score += 4;
        }
        return score;
    }

    private static bool IsNumberedCopy(string stem, string expected)
    {
        if (!stem.StartsWith(expected, StringComparison.OrdinalIgnoreCase)) return false;
        var suffix = stem[expected.Length..].Trim();
        return suffix.Length >= 3 && suffix[0] is '(' or '（' && suffix[^1] is ')' or '）' && suffix[1..^1].All(char.IsAsciiDigit);
    }
}
