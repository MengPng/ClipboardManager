using ClipboardManager.Infrastructure.Updates;
using System.Diagnostics;
using System.IO;

namespace ClipboardManager.Windows.Services;

/// <summary>
/// 就地替换正在运行的单文件 EXE 并重启（ADR-0005）。只对正式发布的单文件版本开放；
/// MSI 安装与开发构建改为打开下载页，避免与 Windows Installer 记录或构建目录不一致。
/// </summary>
internal static class SelfUpdateInstaller
{
    public const string UpdatedFromArgument = "--updated-from=";

    public sealed record Capability(bool CanReplace, string? Reason);

    public static Capability Evaluate()
    {
        var executable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable) || !File.Exists(executable))
            return new Capability(false, "无法确定当前程序的位置，请从下载页手动更新。");
        if (File.Exists(Path.ChangeExtension(executable, ".dll")))
            return new Capability(false, "当前是开发构建，不能自动替换，请从下载页获取正式版本。");
        if (IsMsiInstall(executable))
            return new Capability(false, "当前版本由 MSI 安装包安装，请从下载页获取新的安装包。");
        return new Capability(true, null);
    }

    /// <summary>
    /// 把已校验的新 EXE 换到当前位置，并以 <c>--restart-after-pid</c> 启动它（新进程等旧进程退出后
    /// 才取单实例互斥体）。启动失败时回退到原文件并抛出异常；成功后调用方负责退出当前进程。
    /// </summary>
    public static void ReplaceAndLaunch(string downloadedExecutable, Version currentVersion)
    {
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("无法确定当前程序路径。");
        try
        {
            ExecutableSwap.Replace(executable, downloadedExecutable);
        }
        catch (UnauthorizedAccessException)
        {
            throw new InvalidOperationException("当前程序所在文件夹不允许写入，请从下载页手动更新，或把程序放到可写的文件夹。");
        }

        try
        {
            var start = new ProcessStartInfo(executable) { UseShellExecute = true };
            start.ArgumentList.Add($"--restart-after-pid={Environment.ProcessId}");
            start.ArgumentList.Add(UpdatedFromArgument + currentVersion.ToString(3));
            if (Process.Start(start) is null) throw new InvalidOperationException("新版本没有启动。");
        }
        catch
        {
            ExecutableSwap.Rollback(executable);
            throw;
        }
    }

    /// <summary>新进程启动完成后删除旧版本留下的 <c>*.old</c>。</summary>
    public static void CleanupPreviousExecutable()
    {
        if (Environment.ProcessPath is { } executable) ExecutableSwap.TryDeleteOld(executable);
    }

    public static string? UpdatedFrom(IReadOnlyList<string> arguments) =>
        arguments.FirstOrDefault(argument => argument.StartsWith(UpdatedFromArgument, StringComparison.OrdinalIgnoreCase))?[UpdatedFromArgument.Length..];

    public static void OpenPage(Uri page) =>
        Process.Start(new ProcessStartInfo(page.AbsoluteUri) { UseShellExecute = true });

    private static bool IsMsiInstall(string executable)
    {
        var msiFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClipboardManager.App");
        var folder = Path.GetDirectoryName(Path.GetFullPath(executable)) ?? string.Empty;
        return string.Equals(folder.TrimEnd(Path.DirectorySeparatorChar), Path.GetFullPath(msiFolder).TrimEnd(Path.DirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);
    }
}
