namespace ClipboardManager.Infrastructure.Paths;

public sealed class AppPaths
{
    public AppPaths(string? rootPath = null)
    {
        Root = rootPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ClipboardManager");
        DatabasePath = Path.Combine(Root, "clipboard.db");
        SettingsPath = Path.Combine(Root, "settings.json");
        DevicePath = Path.Combine(Root, "device.json");
        LogsPath = Path.Combine(Root, "logs");
        BlobsPath = Path.Combine(Root, "blobs");
        ThumbnailsPath = Path.Combine(Root, "thumbnails");
        ImportsPath = Path.Combine(Root, "imports");
    }

    public string Root { get; }
    public string DatabasePath { get; }
    public string SettingsPath { get; }
    public string DevicePath { get; }
    public string LogsPath { get; }
    public string BlobsPath { get; }
    public string ThumbnailsPath { get; }
    public string ImportsPath { get; }

    public void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(LogsPath);
        Directory.CreateDirectory(BlobsPath);
        Directory.CreateDirectory(ThumbnailsPath);
        Directory.CreateDirectory(ImportsPath);
    }
}
