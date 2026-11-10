namespace ClipboardManager.Infrastructure.Updates;

/// <summary>
/// 单文件 EXE 的就地替换（ADR-0005 第 5 条）。Windows 允许重命名正在运行的 EXE：
/// 先把当前文件改名为 <c>*.old</c>，再把校验过的新文件复制到原路径；复制失败立即改回原名。
/// 新进程启动成功后调用 <see cref="TryDeleteOld"/>；启动新进程失败时调用 <see cref="Rollback"/>。
/// 用户数据在 %LOCALAPPDATA%\ClipboardManager，不受影响。
/// </summary>
public static class ExecutableSwap
{
    public static string OldPath(string executable) => executable + ".old";

    public static void Replace(string executable, string replacement)
    {
        if (!File.Exists(executable)) throw new FileNotFoundException("找不到当前程序文件。", executable);
        if (!File.Exists(replacement)) throw new FileNotFoundException("找不到下载的新版本。", replacement);

        var old = OldPath(executable);
        if (File.Exists(old)) File.Delete(old);
        File.Move(executable, old);
        try
        {
            File.Copy(replacement, executable, overwrite: false);
        }
        catch
        {
            Restore(executable, old);
            throw;
        }
    }

    /// <summary>把 <c>*.old</c> 改回原名；没有可回退的文件时返回 false。</summary>
    public static bool Rollback(string executable)
    {
        var old = OldPath(executable);
        if (!File.Exists(old)) return false;
        Restore(executable, old);
        return true;
    }

    /// <summary>删除上次更新留下的 <c>*.old</c>；文件仍被占用时保留，下次启动再试。</summary>
    public static bool TryDeleteOld(string executable)
    {
        var old = OldPath(executable);
        try
        {
            if (File.Exists(old)) File.Delete(old);
            return true;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private static void Restore(string executable, string old)
    {
        if (File.Exists(executable)) File.Delete(executable);
        File.Move(old, executable);
    }
}
