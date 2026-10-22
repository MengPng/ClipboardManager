using ClipboardManager.Windows.Services;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Contains("--clipboard-safety", StringComparer.OrdinalIgnoreCase)) return ClipboardSafetyProbe.Run();
        if (args.Any(arg => arg is "--selection" or "--selection-target" or "--selection-ui" or "--selection-gateway")) return SelectionProbe.Run(args);
        var completion = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            application.Dispatcher.InvokeAsync(async () =>
            {
                try { completion.SetResult(await RunProbeAsync(application)); }
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

    private static async Task<int> RunProbeAsync(Application application)
    {
        var originalClipboard = Clipboard.GetDataObject();
        var evidenceDirectory = Path.Combine(
            AppContext.BaseDirectory,
            "evidence-" + DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(evidenceDirectory);

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
        owner.Show();

        try
        {
            var systemClipboard = new WindowsSystemClipboard();
            var text = $"ClipboardManager Phase4 文本恢复探针 {Guid.NewGuid():N} 😀\r\n第二行";
            await systemClipboard.WriteTextAsync(text);
            Check(ReadTextWithRetry() == text, "TEXT_RESTORE");

            var imagePath = Path.Combine(evidenceDirectory, "probe.png");
            var pixels = new byte[]
            {
                0x00, 0x00, 0xFF, 0xFF, 0x00, 0xFF, 0x00, 0xFF,
                0xFF, 0x00, 0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF
            };
            var bitmap = BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgra32, null, pixels, 8);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            await using (var stream = File.Create(imagePath)) encoder.Save(stream);
            await systemClipboard.WriteImageAsync(imagePath);
            var restoredImage = Clipboard.GetImage();
            Check(restoredImage is not null && restoredImage.PixelWidth == 2 && restoredImage.PixelHeight == 2, "IMAGE_RESTORE");

            var file1 = Path.Combine(evidenceDirectory, "文件 A.txt");
            var file2 = Path.Combine(evidenceDirectory, "file-B.txt");
            await File.WriteAllTextAsync(file1, "A");
            await File.WriteAllTextAsync(file2, "B");
            await systemClipboard.WriteFilesAsync([file1, file2]);
            StringCollection restoredFiles = Clipboard.GetFileDropList();
            Check(restoredFiles.Count == 2 && restoredFiles.Cast<string>().SequenceEqual(new[] { file1, file2 }), "FILES_RESTORE");

            var directWriter = typeof(WindowsSystemClipboard).GetMethod(
                "TryWriteTextDirect",
                BindingFlags.NonPublic | BindingFlags.Static)
                ?? throw new MissingMethodException("TryWriteTextDirect");
            var fallbackText = $"ClipboardManager Win32 fallback {Guid.NewGuid():N}";
            var ownerHandle = new WindowInteropHelper(owner).Handle;
            var directResult = false;
            for (var attempt = 0; attempt < 10 && !directResult; attempt++)
            {
                directResult = (bool)(directWriter.Invoke(null, [fallbackText, ownerHandle]) ?? false);
                if (!directResult) await Task.Delay(25 * (attempt + 1));
            }
            Check(directResult && ReadTextWithRetry() == fallbackText, "WIN32_FALLBACK_WITH_OWNER");

            using var automaticPaste = new AutomaticPasteService();

            var expectedPaste = $"ClipboardManager automatic paste {Guid.NewGuid():N}";
            var pasteInput = new System.Windows.Controls.TextBox { Width = 420, Height = 34 };
            var pasteTarget = new Window
            {
                Title = "ClipboardManager Paste Target Probe",
                Width = 480,
                Height = 120,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                Content = pasteInput,
                Topmost = true
            };
            pasteTarget.Show();
            pasteTarget.Activate();
            pasteInput.Focus();
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            var targetHandle = new WindowInteropHelper(pasteTarget).Handle;
            typeof(AutomaticPasteService).GetField(
                "_targetWindow", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(automaticPaste, targetHandle);
            typeof(AutomaticPasteService).GetField(
                "_targetLocked", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(automaticPaste, true);
            await systemClipboard.WriteTextAsync(expectedPaste);
            var rememberedTarget = (IntPtr)(typeof(AutomaticPasteService).GetField(
                "_targetWindow", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(automaticPaste) ?? IntPtr.Zero);
            Console.WriteLine($"AUTO_PASTE_REMEMBERED_TARGET=0x{rememberedTarget.ToInt64():X}");
            var automaticPasteResult = await automaticPaste.TryPasteAsync(owner);
            Console.WriteLine($"AUTO_PASTE_SEND_RESULT={automaticPasteResult}; REASON={automaticPaste.LastFailureReason}");
            await Task.Delay(100);
            Check(automaticPasteResult && pasteInput.Text == expectedPaste,
                "AUTO_PASTE_TO_FOCUSED_TEXTBOX");
            pasteTarget.Close();

            Console.WriteLine($"EVIDENCE_DIR={evidenceDirectory}");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("PROBE_FAILED=" + exception);
            return 1;
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
                }
            }
            owner.Close();
        }
    }

    private static void Check(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException(name + " failed");
        Console.WriteLine(name + "=PASS");
    }

    private static string ReadTextWithRetry()
    {
        Exception? last = null;
        for (var attempt = 0; attempt < 10; attempt++)
        {
            try { return Clipboard.GetText(TextDataFormat.UnicodeText); }
            catch (System.Runtime.InteropServices.ExternalException exception)
            {
                last = exception;
                Thread.Sleep(25 * (attempt + 1));
            }
        }
        throw new InvalidOperationException("Unable to read text back from the system clipboard.", last);
    }

}
