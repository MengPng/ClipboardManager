using System.Text.Json;
using ClipboardManager.Domain.Contracts;
using ClipboardManager.Domain.Models;
using ClipboardManager.Infrastructure.Paths;

namespace ClipboardManager.Infrastructure.Identity;

public sealed class JsonDeviceIdentityStore(AppPaths paths) : IDeviceIdentityStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public async Task<DeviceIdentity> GetOrCreateAsync(CancellationToken cancellationToken = default)
    {
        paths.EnsureCreated();

        if (File.Exists(paths.DevicePath))
        {
            await using var existingStream = File.OpenRead(paths.DevicePath);
            var existing = await JsonSerializer.DeserializeAsync<DeviceIdentity>(
                existingStream,
                SerializerOptions,
                cancellationToken);
            if (existing is not null)
            {
                return existing;
            }
        }

        var created = new DeviceIdentity(
            Guid.NewGuid(),
            Environment.MachineName,
            Environment.OSVersion.VersionString,
            DateTimeOffset.UtcNow);

        var temporaryPath = paths.DevicePath + ".tmp";
        await using (var stream = File.Create(temporaryPath))
        {
            await JsonSerializer.SerializeAsync(stream, created, SerializerOptions, cancellationToken);
        }

        File.Move(temporaryPath, paths.DevicePath, true);
        return created;
    }
}
