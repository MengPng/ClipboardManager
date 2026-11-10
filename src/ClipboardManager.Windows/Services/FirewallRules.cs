using System.ComponentModel;
using System.Diagnostics;
using ClipboardManager.Domain.Models;

namespace ClipboardManager.Windows.Services;

public enum FirewallInstallResult
{
    Installed,
    Cancelled,
    Failed
}

/// <summary>
/// 0.9.5：放行局域网匹配与传输用的 TCP/UDP 53318。
/// 只在用户点击后以管理员身份运行 netsh（会弹出 UAC），先删除同名旧规则再添加，避免重复；
/// 查询规则不需要管理员权限。不修改其他防火墙设置。
/// </summary>
public static class FirewallRules
{
    public const string TcpRuleName = "ClipboardManager Config Sync TCP";
    public const string UdpRuleName = "ClipboardManager Config Sync UDP";
    private const int UserCancelledUac = 1223;

    /// <summary>两条规则都已存在时返回 true；查询失败按未放行处理。</summary>
    public static async Task<bool> AreInstalledAsync(CancellationToken cancellationToken = default)
    {
        return await RuleExistsAsync(TcpRuleName, cancellationToken) && await RuleExistsAsync(UdpRuleName, cancellationToken);
    }

    public static async Task<FirewallInstallResult> InstallAsync(CancellationToken cancellationToken = default)
    {
        var commands =
            $"netsh advfirewall firewall delete rule name=\"{TcpRuleName}\" >nul 2>&1 & " +
            $"netsh advfirewall firewall delete rule name=\"{UdpRuleName}\" >nul 2>&1 & " +
            string.Join(" && ", ConfigSyncPolicy.FirewallCommands);
        var start = new ProcessStartInfo("cmd.exe", "/c " + commands)
        {
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden
        };
        try
        {
            using var process = Process.Start(start);
            if (process is null) return FirewallInstallResult.Failed;
            await process.WaitForExitAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
            return process.ExitCode == 0 && await AreInstalledAsync(cancellationToken)
                ? FirewallInstallResult.Installed
                : FirewallInstallResult.Failed;
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == UserCancelledUac)
        {
            return FirewallInstallResult.Cancelled;
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or TimeoutException)
        {
            Debug.WriteLine($"放行防火墙失败: {exception.GetType().Name}");
            return FirewallInstallResult.Failed;
        }
    }

    private static async Task<bool> RuleExistsAsync(string name, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo("netsh.exe", $"advfirewall firewall show rule name=\"{name}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        try
        {
            using var process = Process.Start(start);
            if (process is null) return false;
            _ = process.StandardOutput.ReadToEndAsync(cancellationToken);
            _ = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            return process.ExitCode == 0;
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or TimeoutException)
        {
            Debug.WriteLine($"查询防火墙规则失败: {exception.GetType().Name}");
            return false;
        }
    }
}
