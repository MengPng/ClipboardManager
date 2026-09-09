using ClipboardManager.Domain.Contracts;
using Microsoft.Extensions.Logging;
using System.IO;

namespace ClipboardManager.Windows.Services;

public sealed class AutomaticBackupService(
    IStorageMaintenanceService storage,
    IAppSettingsStore settingsStore,
    ILogger<AutomaticBackupService> logger) : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private System.Threading.Timer? _timer;
    private string _deviceName = Environment.MachineName;

    public void Start(string deviceName)
    {
        _deviceName = SanitizeFileName(string.IsNullOrWhiteSpace(deviceName) ? Environment.MachineName : deviceName);
        _timer ??= new System.Threading.Timer(async _ => await TryBackupAsync(), null, TimeSpan.FromSeconds(5), TimeSpan.FromHours(1));
    }

    public async Task TryBackupAsync(CancellationToken cancellationToken = default)
    {
        if (!await _gate.WaitAsync(0, cancellationToken)) return;
        try
        {
            var settings = await settingsStore.LoadAsync(cancellationToken);
            if (!settings.AutoBackupChoiceCompleted || settings.AutoBackupIntervalHours == 0) return;
            var now = DateTimeOffset.Now;
            if (settings.LastAutomaticBackupAt is { } last && now - last < TimeSpan.FromHours(settings.AutoBackupIntervalHours)) return;

            var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            var directory = Path.Combine(documents, "扶光剪切板备份", _deviceName);
            Directory.CreateDirectory(directory);
            var target = Path.Combine(directory, BackupFileName(_deviceName, now));
            await storage.CreateBackupAsync(target, cancellationToken);

            foreach (var old in Directory.EnumerateFiles(directory, $"{_deviceName}-*-剪切板备份.clipbackup"))
            {
                if (string.Equals(Path.GetFullPath(old), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase)) continue;
                if (File.GetCreationTime(old).Date == now.Date || File.GetLastWriteTime(old).Date == now.Date)
                    try { File.Delete(old); } catch (IOException ex) { logger.LogWarning(ex, "Could not remove superseded daily backup {BackupPath}.", old); }
            }

            await settingsStore.SaveAsync(settings with { LastAutomaticBackupAt = now });
            logger.LogInformation("Automatic backup completed at {BackupPath}.", target);
        }
        catch (Exception ex) { logger.LogError(ex, "Automatic backup failed."); }
        finally { _gate.Release(); }
    }

    public static string BackupFileName(string deviceName, DateTimeOffset time) =>
        $"{SanitizeFileName(deviceName)}-{time:yyyy年M月d日HH时mm分ss秒}-剪切板备份.clipbackup";

    private static string SanitizeFileName(string value)
    {
        foreach (var character in Path.GetInvalidFileNameChars()) value = value.Replace(character, '_');
        return string.IsNullOrWhiteSpace(value) ? "本机" : value.Trim();
    }

    public void Dispose() { _timer?.Dispose(); _gate.Dispose(); }
}
