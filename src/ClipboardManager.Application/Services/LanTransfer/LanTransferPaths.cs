using ClipboardManager.Domain.Models;

namespace ClipboardManager.Application.Services.LanTransfer;

/// <summary>接收文件的保存位置：设置为空时使用“下载\剪切板传输”。</summary>
public static class LanTransferPaths
{
    public static string DefaultReceiveFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads",
            FileTransferPolicy.DefaultFolderName);

    public static string ResolveReceiveFolder(AppSettings settings) =>
        string.IsNullOrWhiteSpace(settings.LanReceiveFolder) ? DefaultReceiveFolder : settings.LanReceiveFolder;
}
