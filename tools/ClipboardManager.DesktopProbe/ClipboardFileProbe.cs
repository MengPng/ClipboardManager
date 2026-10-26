using ClipboardManager.Windows.Services;
using System.Collections.Specialized;
using System.IO;
using System.Windows;

internal static class ClipboardFileProbe
{
    public static int Run()
    {
        var completion = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            application.Dispatcher.InvokeAsync(async () =>
            {
                try { completion.SetResult(await RunAsync(application)); }
                catch (Exception exception) { completion.SetException(exception); }
                finally
                {
                    application.Shutdown();
                    System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvokeShutdown(
                        System.Windows.Threading.DispatcherPriority.Background);
                }
            });
            System.Windows.Threading.Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task.GetAwaiter().GetResult();
    }

    private static async Task<int> RunAsync(Application application)
    {
        IDataObject? originalClipboard = null;
        var owner = new Window
        {
            Width = 1,
            Height = 1,
            Left = -10000,
            Top = -10000,
            ShowInTaskbar = false,
            WindowStyle = WindowStyle.None
        };
        application.MainWindow = owner;
        var result = 1;
        try
        {
            originalClipboard = Clipboard.GetDataObject();
            owner.Show();
            var evidenceDirectory = Path.Combine(
                Directory.GetCurrentDirectory(),
                "临时文件",
                DateTimeOffset.Now.ToString("yyyyMMdd_HHmmss") + "-剪切板文件写回探针");
            Directory.CreateDirectory(evidenceDirectory);
            var file1 = Path.Combine(evidenceDirectory, "文件 A.txt");
            var file2 = Path.Combine(evidenceDirectory, "file-B.txt");
            await File.WriteAllTextAsync(file1, "A");
            await File.WriteAllTextAsync(file2, "B");

            var clipboard = new WindowsSystemClipboard();
            for (var attempt = 1; attempt <= 4; attempt++)
            {
                await clipboard.WriteFilesAsync([file1, file2]);
                StringCollection restored = Clipboard.GetFileDropList();
                if (restored.Count != 2 || !restored.Cast<string>().SequenceEqual([file1, file2]))
                    throw new InvalidOperationException($"FILES_ONLY_RESTORE content mismatch at attempt {attempt}.");
                Console.WriteLine($"FILES_ONLY_RESTORE_{attempt}=PASS");
            }
            Console.WriteLine("FILES_ONLY_RESTORE=PASS");
            result = 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("FILES_ONLY_PROBE_FAILED=" + exception);
            result = 1;
        }
        finally
        {
            if (originalClipboard is not null)
            {
                try
                {
                    Clipboard.SetDataObject(originalClipboard, true);
                    Console.WriteLine("ORIGINAL_CLIPBOARD_RESTORED=PASS");
                }
                catch (Exception exception)
                {
                    Console.Error.WriteLine("ORIGINAL_CLIPBOARD_RESTORED=FAILED: " + exception.Message);
                    result = 1;
                }
            }
            owner.Close();
            if (result != 0) Console.Error.WriteLine("FILES_ONLY_PROBE=FAILED");
        }
        return result;
    }
}
