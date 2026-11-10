using System.IO.Compression;

namespace ClipboardManager.Application.Services.LanTransfer;

/// <summary>
/// 发送文件夹时先打成一个 zip（ADR-0008 附录）：传输协议仍只传单个文件，旧版本也能接收。
/// 条目路径相对文件夹本身，时间取原文件修改时间，同一文件夹重发得到相同字节，可以续传。
/// 不跟随符号链接与目录联接，读不了的文件跳过并计数。
/// </summary>
public static class FolderArchive
{
    private static readonly EnumerationOptions Enumeration = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.ReparsePoint,
        ReturnSpecialDirectories = false
    };

    /// <summary>文件夹在 zip 中的显示名：文件夹名 + .zip；磁盘根目录等没有名字时用“文件夹”。</summary>
    public static string ArchiveName(string folder)
    {
        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)));
        return (string.IsNullOrWhiteSpace(name) ? "文件夹" : name) + ".zip";
    }

    /// <summary>把 <paramref name="folder"/> 打包到 <paramref name="zipPath"/>；返回跳过（读取失败）的文件数。</summary>
    public static int Create(string folder, string zipPath, Action<long> progress, CancellationToken cancellationToken)
    {
        var root = Path.GetFullPath(folder);
        var files = Directory.EnumerateFiles(root, "*", Enumeration)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var skipped = 0;
        long done = 0;
        var buffer = new byte[1 << 20];
        using (var output = new FileStream(zipPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16))
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create))
        {
            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                FileStream input;
                try
                {
                    input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    skipped++;
                    continue;
                }
                using (input)
                {
                    var entryName = Path.GetRelativePath(root, file).Replace('\\', '/');
                    var entry = archive.CreateEntry(entryName, CompressionLevel.Fastest);
                    var modified = File.GetLastWriteTime(file);
                    entry.LastWriteTime = modified.Year is < 1980 or > 2107 ? new DateTime(1980, 1, 1) : modified;
                    using var target = entry.Open();
                    int read;
                    while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        target.Write(buffer, 0, read);
                        done += read;
                        progress(done);
                    }
                }
            }
        }
        return skipped;
    }
}
