using Microsoft.Win32;
using System.IO;

namespace ClipboardManager.Windows.Services;

/// <summary>Owns only this application's current-user Run value; never changes other startup entries.</summary>
internal sealed class StartupRegistrationService
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "ClipboardManager";

    internal sealed record Snapshot(bool Exists, object? Value, RegistryValueKind Kind);

    public Snapshot Read()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        var exists = key?.GetValueNames().Contains(ValueName, StringComparer.OrdinalIgnoreCase) == true;
        return exists
            ? new Snapshot(true, key!.GetValue(ValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames), key.GetValueKind(ValueName))
            : new Snapshot(false, null, RegistryValueKind.String);
    }

    public void SetEnabled(bool enabled)
    {
        if (!enabled)
        {
            using var existing = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            existing?.DeleteValue(ValueName, throwOnMissingValue: false);
            return;
        }
        var executable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable) || !File.Exists(executable) ||
            !string.Equals(Path.GetExtension(executable), ".exe", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(Path.GetFileNameWithoutExtension(executable), "dotnet", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("请通过已安装或发布的 ClipboardManager.exe 运行后启用开机启动。");
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true)
            ?? throw new InvalidOperationException("无法访问当前用户的开机启动设置。");
        key.SetValue(ValueName, $"\"{executable}\"", RegistryValueKind.String);
        key.Flush();
    }

    public void Restore(Snapshot snapshot)
    {
        if (!snapshot.Exists) { SetEnabled(false); return; }
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true)
            ?? throw new InvalidOperationException("无法恢复当前用户的开机启动设置。");
        key.SetValue(ValueName, snapshot.Value ?? throw new InvalidOperationException("原开机启动值不可读取。"), snapshot.Kind);
        key.Flush();
    }
}
