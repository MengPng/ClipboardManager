namespace ClipboardManager.Domain.Models;

public sealed record LanShareDevice(string Alias, string Address, int Port, string Protocol, string Fingerprint, string? DeviceType)
{
    public string Endpoint => $"{Protocol}://{Address}:{Port}";
}

public sealed record LanShareProgress(string Stage, long BytesSent, long TotalBytes);

public sealed record LanShareResult(string DeviceAlias, int FileCount, long BytesSent);
