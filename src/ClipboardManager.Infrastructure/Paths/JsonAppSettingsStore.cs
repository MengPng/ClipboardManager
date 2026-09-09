using System.Text.Json;
using ClipboardManager.Domain.Contracts;
using ClipboardManager.Domain.Models;

namespace ClipboardManager.Infrastructure.Paths;

public sealed class JsonAppSettingsStore(AppPaths paths) : IAppSettingsStore
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            paths.EnsureCreated();
            if (!File.Exists(paths.SettingsPath))
            {
                var defaults = new AppSettings();
                await SaveCoreAsync(defaults, cancellationToken);
                return defaults;
            }
            await using var stream = File.OpenRead(paths.SettingsPath);
            var settings = await JsonSerializer.DeserializeAsync<AppSettings>(stream, SerializerOptions, cancellationToken)
                ?? throw new InvalidDataException("设置文件为空，未覆盖原文件。");
            AppSettingsValidator.Validate(settings);
            return settings;
        }
        finally { _gate.Release(); }
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        AppSettingsValidator.Validate(settings);
        await _gate.WaitAsync(cancellationToken);
        try { paths.EnsureCreated(); await SaveCoreAsync(settings, cancellationToken); }
        finally { _gate.Release(); }
    }

    private async Task SaveCoreAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        var temporaryPath = paths.SettingsPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await JsonSerializer.SerializeAsync(stream, settings, SerializerOptions, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, paths.SettingsPath, true);
        }
        finally { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
    }
}
