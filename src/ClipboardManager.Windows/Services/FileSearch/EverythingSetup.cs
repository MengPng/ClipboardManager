using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using Microsoft.Win32;

namespace ClipboardManager.Windows.Services;

public enum EverythingState
{
    Running,
    InstalledNotRunning,
    NotInstalled
}

public enum EverythingSetupResult
{
    Done,
    Cancelled,
    Failed
}

/// <summary>
/// 0.9.5（ADR-0007 附录）：随 EXE 内嵌官方 Everything 1.4 安装程序。
/// 只在用户点击「安装 Everything」时解出到临时目录、校验 SHA-256，再运行官方安装向导（不加静默参数）；
/// 已安装但没运行时只启动它（<c>-startup</c>，在后台运行）。不修改 Everything 的任何设置。
/// </summary>
public static class EverythingSetup
{
    public const string Version = "1.4.1.1032";
    public const string InstallerFileName = "Everything-1.4.1.1032.x64-Setup.exe";
    /// <summary>与 voidtools 公布的校验值一致；发布脚本核对 Assets/Everything 中的文件与此值、NOTICE.txt 三者相同。</summary>
    public const string InstallerSha256 = "C42EFAD041D4C0BB4D4AC97AE7CBE89F153EC1FE078772392E749C7F5D5282D3";
    private const string ResourceName = "ClipboardManager.Embedded.EverythingSetup.exe";
    private const int UserCancelledUac = 1223;

    public static bool IsBundled => Assembly.GetExecutingAssembly().GetManifestResourceInfo(ResourceName) is not null;

    public static EverythingState GetState()
    {
        if (EverythingIpcClient.IsRunning()) return EverythingState.Running;
        return FindInstalledExe() is not null ? EverythingState.InstalledNotRunning : EverythingState.NotInstalled;
    }

    /// <summary>从卸载信息、App Paths 和默认安装目录找 Everything.exe；便携版无法发现，按未安装处理。</summary>
    public static string? FindInstalledExe()
    {
        foreach (var candidate in Candidates())
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(candidate) && File.Exists(candidate)) return candidate;
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException) { }
        }
        return null;
    }

    /// <summary>启动已安装的 Everything 并等待它可以接受查询（最多约 8 秒）。</summary>
    public static async Task<EverythingSetupResult> StartAsync(CancellationToken cancellationToken = default)
    {
        if (FindInstalledExe() is not { } exe) return EverythingSetupResult.Failed;
        try
        {
            Process.Start(new ProcessStartInfo(exe, "-startup") { UseShellExecute = true })?.Dispose();
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == UserCancelledUac)
        {
            return EverythingSetupResult.Cancelled;
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            Debug.WriteLine($"启动 Everything 失败: {exception.GetType().Name}");
            return EverythingSetupResult.Failed;
        }
        return await WaitUntilRunningAsync(TimeSpan.FromSeconds(8), cancellationToken)
            ? EverythingSetupResult.Done
            : EverythingSetupResult.Failed;
    }

    /// <summary>
    /// 解出内嵌安装程序、校验 SHA-256 后运行官方安装向导并等待它结束；
    /// 校验不一致时拒绝运行。返回后临时文件即删除。
    /// </summary>
    public static async Task<EverythingSetupResult> InstallAsync(CancellationToken cancellationToken = default)
    {
        var folder = Path.Combine(Path.GetTempPath(), "ClipboardManager", "Everything", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(folder, InstallerFileName);
        try
        {
            using (var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName))
            {
                if (resource is null) return EverythingSetupResult.Failed;
                Directory.CreateDirectory(folder);
                await using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                await resource.CopyToAsync(file, cancellationToken);
            }
            await using (var check = File.OpenRead(path))
            {
                var hash = Convert.ToHexString(await SHA256.HashDataAsync(check, cancellationToken));
                if (!hash.Equals(InstallerSha256, StringComparison.OrdinalIgnoreCase)) return EverythingSetupResult.Failed;
            }
            using var process = Process.Start(new ProcessStartInfo(path) { UseShellExecute = true, WorkingDirectory = folder });
            if (process is null) return EverythingSetupResult.Failed;
            await process.WaitForExitAsync(cancellationToken);
            return GetState() == EverythingState.NotInstalled ? EverythingSetupResult.Cancelled : EverythingSetupResult.Done;
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == UserCancelledUac)
        {
            return EverythingSetupResult.Cancelled;
        }
        catch (Exception exception) when (exception is Win32Exception or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Debug.WriteLine($"安装 Everything 失败: {exception.GetType().Name}");
            return EverythingSetupResult.Failed;
        }
        finally
        {
            try { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
    }

    public static async Task<bool> WaitUntilRunningAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var until = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < until)
        {
            if (EverythingIpcClient.IsRunning()) return true;
            await Task.Delay(400, cancellationToken);
        }
        return EverythingIpcClient.IsRunning();
    }

    private static IEnumerable<string?> Candidates()
    {
        foreach (var hive in new[] { Registry.LocalMachine, Registry.CurrentUser })
        {
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                string? appPath = null, icon = null, location = null, uninstall = null;
                try
                {
                    using var root = RegistryKey.OpenBaseKey(hive == Registry.LocalMachine ? RegistryHive.LocalMachine : RegistryHive.CurrentUser, view);
                    using (var key = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\Everything.exe"))
                        appPath = key?.GetValue(null) as string;
                    using (var key = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Everything"))
                    {
                        icon = key?.GetValue("DisplayIcon") as string;
                        location = key?.GetValue("InstallLocation") as string;
                        uninstall = key?.GetValue("UninstallString") as string;
                    }
                }
                catch (Exception exception) when (exception is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
                yield return Unquote(appPath);
                yield return Unquote(icon?.Split(',')[0]);
                if (!string.IsNullOrWhiteSpace(location)) yield return Path.Combine(Unquote(location)!, "Everything.exe");
                if (Unquote(uninstall) is { } un && Path.GetDirectoryName(un) is { } dir) yield return Path.Combine(dir, "Everything.exe");
            }
        }
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Everything", "Everything.exe");
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Everything", "Everything.exe");
    }

    private static string? Unquote(string? value) => value?.Trim().Trim('"');
}
