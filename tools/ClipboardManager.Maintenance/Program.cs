using ClipboardManager.Infrastructure.Data;
using ClipboardManager.Infrastructure.Paths;
using Microsoft.Extensions.Logging;

if (args.Length >= 2 && string.Equals(args[0], "backup", StringComparison.OrdinalIgnoreCase))
{
    var backupPaths = new AppPaths();
    var backupFactory = new SqliteConnectionFactory(backupPaths);
    using var backupLoggerFactory = LoggerFactory.Create(builder => builder.AddSimpleConsole(options => options.SingleLine = true));
    var backupService = new StorageMaintenanceService(backupPaths, backupFactory, backupLoggerFactory.CreateLogger<StorageMaintenanceService>());
    await backupService.CreateBackupAsync(args[1]);
    var backup = await backupService.ValidateBackupAsync(args[1]);
    Console.WriteLine($"Backup completed: {Path.GetFullPath(args[1])}, schema {backup.SchemaVersion}, at {backup.CreatedAtUtc:O}");
    return 0;
}

if (args.Length < 2 || !string.Equals(args[0], "restore", StringComparison.OrdinalIgnoreCase))
{
    Console.Error.WriteLine("Usage:\n  ClipboardManager.Maintenance backup <backup.clipbackup>\n  ClipboardManager.Maintenance restore <backup.clipbackup> [--wait-pid=<pid>] [--restart=<exe>]");
    return 2;
}

var waitPid = ReadIntOption(args, "--wait-pid=");
var restartPath = ReadStringOption(args, "--restart=");
if (waitPid is > 0)
{
    try
    {
        using var parent = System.Diagnostics.Process.GetProcessById(waitPid.Value);
        if (!parent.WaitForExit(30_000))
        {
            Console.Error.WriteLine("ClipboardManager did not exit within 30 seconds.");
            return 3;
        }
    }
    catch (ArgumentException)
    {
        // The application already exited before the helper started waiting.
    }
}

if (System.Diagnostics.Process.GetProcessesByName("ClipboardManager").Any(process => process.Id != Environment.ProcessId))
{
    Console.Error.WriteLine("ClipboardManager is still running. Exit it before restore.");
    return 3;
}
var paths = new AppPaths();
var factory = new SqliteConnectionFactory(paths);
using var loggerFactory = LoggerFactory.Create(builder => builder.AddSimpleConsole(options => options.SingleLine = true));
var service = new StorageMaintenanceService(paths, factory, loggerFactory.CreateLogger<StorageMaintenanceService>());
var validation = await service.ValidateBackupAsync(args[1]);
if (!validation.IsValid)
{
    Console.Error.WriteLine($"Backup validation failed: {validation.Error}");
    return 4;
}
Console.WriteLine($"Validated backup: schema {validation.SchemaVersion}, created {validation.CreatedAtUtc:O}");
var result = await service.RestoreAsync(args[1]);
Console.WriteLine($"Restore completed: schema {result.SchemaVersion}, at {result.RestoredAtUtc:O}");

if (!string.IsNullOrWhiteSpace(restartPath))
{
    if (!File.Exists(restartPath))
    {
        Console.Error.WriteLine($"Restore succeeded, but the application executable was not found: {restartPath}");
        return 5;
    }

    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(restartPath) { UseShellExecute = true });
}

return 0;

static int? ReadIntOption(IReadOnlyList<string> values, string prefix)
{
    var value = ReadStringOption(values, prefix);
    return int.TryParse(value, out var number) ? number : null;
}

static string? ReadStringOption(IReadOnlyList<string> values, string prefix) =>
    values.Skip(2).FirstOrDefault(value => value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))?[prefix.Length..];
