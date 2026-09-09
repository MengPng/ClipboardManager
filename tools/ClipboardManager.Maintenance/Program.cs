using ClipboardManager.Infrastructure.Data;
using ClipboardManager.Infrastructure.Paths;
using Microsoft.Extensions.Logging;

if (args.Length != 2 || !string.Equals(args[0], "restore", StringComparison.OrdinalIgnoreCase))
{
    Console.Error.WriteLine("Usage: ClipboardManager.Maintenance restore <backup.clipbackup>");
    return 2;
}
if (System.Diagnostics.Process.GetProcessesByName("ClipboardManager").Length > 0)
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
return 0;
