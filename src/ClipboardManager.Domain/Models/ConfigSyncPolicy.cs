using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ClipboardManager.Domain.Models;

public enum ConfigSyncError
{
    Unreachable,
    Timeout,
    NotPaired,
    PinMismatch,
    PairingCodeInvalid,
    PairingCodeExpired,
    PairingRevoked,
    VersionRejected,
    HashMismatch,
    TooLarge,
    InvalidPackage,
    NotFound,
    Protocol,
    Local
}

/// <summary>配置同步错误；Message 是可直接展示给用户的中文原因。</summary>
public sealed class ConfigSyncException(ConfigSyncError error, string message, Exception? inner = null)
    : Exception(message, inner)
{
    public ConfigSyncError Error { get; } = error;
}

/// <summary>配对失败；<see cref="RemainingAttempts"/> 为本码剩余可尝试次数。</summary>
public sealed class PairingException(ConfigSyncError error, string message, int remainingAttempts = 0)
    : Exception(message)
{
    public ConfigSyncError Error { get; } = error;
    public int RemainingAttempts { get; } = remainingAttempts;
}

public static class ConfigSyncPolicy
{
    public const int ProtocolVersion = 1;
    public const int DefaultPort = 53318;
    public const string MulticastGroup = "239.255.53.18";

    public const long MaxManifestBytes = 4L * 1024 * 1024;
    public const long MaxPackageBytes = 200L * 1024 * 1024;
    public const long MaxFileBytes = 50L * 1024 * 1024;
    public const long MaxTextBytes = 2L * 1024 * 1024;
    public const int MaxItemCount = 10_000;
    public const int MaxFolderNameLength = 80;
    /// <summary>单个配置包最多包含的文件夹数。</summary>
    public const int MaxFolderCount = 200;
    public const int MaxSnapshots = 3;
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan PairingCodeLifetime = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan DefaultCheckInterval = TimeSpan.FromMinutes(30);
    public static readonly TimeSpan AutoPublishQuietPeriod = TimeSpan.FromSeconds(60);
    public const int MaxPairingFailures = 5;

    private static readonly HashSet<string> HighRiskExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".bat", ".cmd", ".ps1", ".psm1", ".vbs", ".vbe", ".js", ".jse", ".wsf", ".wsh", ".lnk",
        ".msi", ".msp", ".scr", ".com", ".jar", ".reg", ".hta", ".cpl", ".dll", ".sys", ".pif",
        ".application", ".gadget", ".msc", ".chm", ".appx", ".msix", ".iso", ".vhd", ".vhdx", ".url"
    };

    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    /// <summary>规范化后的文件名是否属于可执行/可被系统自动执行的高风险类型。</summary>
    public static bool IsHighRiskFile(string? fileName)
    {
        var name = SanitizeFileName(fileName);
        var extension = Path.GetExtension(name);
        return extension.Length > 0 && HighRiskExtensions.Contains(extension);
    }

    /// <summary>去掉路径与非法字符，保留扩展名；不会返回空串或 Windows 保留设备名。</summary>
    public static string SanitizeFileName(string? value)
    {
        var name = value ?? string.Empty;
        var slash = Math.Max(name.LastIndexOf('/'), name.LastIndexOf('\\'));
        if (slash >= 0) name = name[(slash + 1)..];
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(name.Length);
        foreach (var ch in name) builder.Append(ch < ' ' || invalid.Contains(ch) ? '_' : ch);
        name = builder.ToString().Trim().TrimEnd('.', ' ');
        if (name.Length == 0) return "file";
        var extension = Path.GetExtension(name);
        var stem = Path.GetFileNameWithoutExtension(name);
        if (stem.Length == 0) stem = "file";
        if (ReservedNames.Contains(stem)) stem = "_" + stem;
        if (extension.Length > 16) extension = extension[..16];
        if (stem.Length > 120) stem = stem[..120];
        return stem + extension;
    }

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static string SerializeManifest(ConfigManifest manifest) => JsonSerializer.Serialize(manifest, Json);

    public static ConfigManifest ParseManifest(string json) =>
        JsonSerializer.Deserialize<ConfigManifest>(json, Json) ?? throw new JsonException("清单为空。");

    public static string Fingerprint(byte[] certificateDer) => Convert.ToHexString(SHA256.HashData(certificateDer));

    public static string FingerprintPrefix(string fingerprint) =>
        fingerprint.Length <= 12 ? fingerprint : fingerprint[..12];

    public const string FirewallHint = "如果对方设备在线并已开启共享，请在对方电脑的 Windows 防火墙中放行 TCP/UDP 53318。";

    public static readonly IReadOnlyList<string> FirewallCommands =
    [
        "netsh advfirewall firewall add rule name=\"ClipboardManager Config Sync TCP\" dir=in action=allow protocol=TCP localport=53318",
        "netsh advfirewall firewall add rule name=\"ClipboardManager Config Sync UDP\" dir=in action=allow protocol=UDP localport=53318"
    ];

    /// <summary>解析手动输入的地址：IPv4/主机名/[IPv6]，可带 :端口；省略端口时使用默认端口。</summary>
    public static bool TryParseAddress(string? text, out string host, out int port)
    {
        host = string.Empty;
        port = DefaultPort;
        var value = (text ?? string.Empty).Trim();
        foreach (var scheme in new[] { "https://", "http://" })
            if (value.StartsWith(scheme, StringComparison.OrdinalIgnoreCase)) value = value[scheme.Length..];
        value = value.TrimEnd('/');
        if (value.Length == 0 || value.Any(char.IsWhiteSpace)) return false;
        string portText = string.Empty;
        if (value.StartsWith('['))
        {
            var close = value.IndexOf(']');
            if (close < 2) return false;
            host = value[1..close];
            var rest = value[(close + 1)..];
            if (rest.Length > 0)
            {
                if (rest[0] != ':') return false;
                portText = rest[1..];
            }
        }
        else
        {
            var first = value.IndexOf(':');
            if (first >= 0 && first == value.LastIndexOf(':'))
            {
                host = value[..first];
                portText = value[(first + 1)..];
            }
            else if (first < 0) host = value;
            else return false;
        }
        if (host.Length == 0 || host.Contains('/') || host.Contains('?')) return false;
        if (!value.StartsWith('[') && value.Contains(':') && portText.Length == 0) return false;
        if (portText.Length > 0 && (!int.TryParse(portText, out port) || port is < 1 or > 65535)) return false;
        return true;
    }

    /// <summary>仅允许 PNG / JPEG（按文件头判断）。</summary>
    public static bool IsPngOrJpeg(ReadOnlySpan<byte> header) =>
        (header.Length >= 8 && header[0] == 0x89 && header[1] == 0x50 && header[2] == 0x4E && header[3] == 0x47 &&
         header[4] == 0x0D && header[5] == 0x0A && header[6] == 0x1A && header[7] == 0x0A) ||
        (header.Length >= 3 && header[0] == 0xFF && header[1] == 0xD8 && header[2] == 0xFF);

    /// <summary>按条目 ID 比较两个清单：新增 / 修改（摘要变化）/ 删除。</summary>
    public static ConfigChangeSummary Compare(ConfigManifest? installed, ConfigManifest remote, int sampleLimit = 8)
    {
        var old = (installed?.Items ?? []).ToDictionary(item => item.Id);
        var next = remote.Items.ToDictionary(item => item.Id);
        var added = remote.Items.Where(item => !old.ContainsKey(item.Id)).ToList();
        // 条目被移到别的文件夹也算修改（旧清单没有文件夹信息时不比较）。
        var modified = remote.Items.Where(item => old.TryGetValue(item.Id, out var before) &&
            (before.Digest != item.Digest ||
             (before.FolderId != Guid.Empty && item.FolderId != Guid.Empty && before.FolderId != item.FolderId))).ToList();
        var removed = old.Values.Where(item => !next.ContainsKey(item.Id)).ToList();
        static IReadOnlyList<string> Titles(IEnumerable<ConfigManifestItem> items, int limit) =>
            items.Take(limit).Select(item => string.IsNullOrWhiteSpace(item.Title) ? "（无标题）" : item.Title!).ToArray();
        var oldFolders = (installed?.FolderList ?? []).Select(folder => folder.Id).ToHashSet();
        var newFolders = remote.FolderList.Select(folder => folder.Id).ToHashSet();
        var foldersAdded = oldFolders.Count == 0 ? 0 : newFolders.Count(id => !oldFolders.Contains(id));
        var foldersRemoved = oldFolders.Count(id => !newFolders.Contains(id));
        return new ConfigChangeSummary(added.Count, modified.Count, removed.Count,
            Titles(added, sampleLimit), Titles(modified, sampleLimit), Titles(removed, sampleLimit), foldersAdded, foldersRemoved);
    }
}

/// <summary>配对码 HMAC-SHA256(配对码, 角色 ‖ 双方证书指纹 ‖ 双方 nonce)，配对码本身不上网。</summary>
public static class PairingProtocol
{
    public const int CodeLength = 6;
    public const int NonceBytes = 16;

    public static string GenerateCode() =>
        RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D" + CodeLength, System.Globalization.CultureInfo.InvariantCulture);

    public static bool IsWellFormedCode(string? code) =>
        code is { Length: CodeLength } && code.All(char.IsAsciiDigit);

    public static byte[] NewNonce() => RandomNumberGenerator.GetBytes(NonceBytes);

    /// <param name="role">'C' 表示订阅方证明，'S' 表示发布方证明，防止证明被反射。</param>
    public static byte[] ComputeProof(string code, char role, string clientFingerprint, string serverFingerprint,
        byte[] clientNonce, byte[] serverNonce)
    {
        var data = new List<byte> { (byte)role };
        data.AddRange(Encoding.UTF8.GetBytes(clientFingerprint.ToUpperInvariant()));
        data.Add(0);
        data.AddRange(Encoding.UTF8.GetBytes(serverFingerprint.ToUpperInvariant()));
        data.Add(0);
        data.AddRange(clientNonce);
        data.AddRange(serverNonce);
        return HMACSHA256.HashData(Encoding.UTF8.GetBytes(code), data.ToArray());
    }

    public static bool VerifyProof(byte[] expected, byte[]? actual) =>
        actual is not null && actual.Length == expected.Length && CryptographicOperations.FixedTimeEquals(expected, actual);
}
