using ClipboardManager.Application;
using ClipboardManager.Application.Services;
using ClipboardManager.Domain.Contracts;
using ClipboardManager.Domain.Models;
using ClipboardManager.Infrastructure;
using ClipboardManager.Windows;
using ClipboardManager.Windows.Interop;
using ClipboardManager.Windows.Services;
using Microsoft.Extensions.DependencyInjection;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;

internal static class SelectionProbe
{
    private const string Selected = "Selection probe sample\r\n第二行";

    public static int Run(string[] args)
    {
        var completion = new TaskCompletionSource<int>();
        var thread = new Thread(() =>
        {
            var app = new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            app.Dispatcher.InvokeAsync(async () =>
            {
                try
                {
                    if (args.Contains("--selection-target")) { RunTarget(app); return; }
                    completion.SetResult(args.Contains("--selection-ui") || args.Contains("--selection-gateway")
                        ? await ProbeResultUiAsync(app, args) : await ProbeAsync(app));
                }
                catch (Exception ex) { Console.Error.WriteLine("SELECTION_PROBE_FAILED " + ex); completion.SetResult(1); }
                finally { if (!args.Contains("--selection-target")) app.Shutdown(); }
            });
            app.Run();
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        if (args.Contains("--selection-target")) { thread.Join(); return 0; }
        return completion.Task.GetAwaiter().GetResult();
    }

    private static void RunTarget(System.Windows.Application app)
    {
        bool blockCopy = false;
        var text = new TextBox { Text = Selected, AcceptsReturn = true, Margin = new Thickness(20) };
        var target = new Window { Title = "ClipboardManager selection test target", Width = 440, Height = 220, Content = text, Topmost = true };
        text.PreviewKeyDown += (_, e) =>
        {
            if (blockCopy && e.Key == System.Windows.Input.Key.C && System.Windows.Input.Keyboard.Modifiers.HasFlag(System.Windows.Input.ModifierKeys.Control))
                e.Handled = true;
        };
        target.Loaded += (_, _) =>
        {
            target.Activate(); text.Focus(); text.SelectAll();
            HwndSource.FromHwnd(new WindowInteropHelper(target).Handle).AddHook(
                (IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled) =>
                {
                    if (message == 0x8500) { blockCopy = wParam != IntPtr.Zero; handled = true; }
                    if (message == 0x8501) { handled = true; return new IntPtr(1); }
                    if (message == 0x8502) { handled = true; return new IntPtr((int)text.PointToScreen(new System.Windows.Point(12, 10)).X); }
                    if (message == 0x8503) { handled = true; return new IntPtr((int)text.PointToScreen(new System.Windows.Point(12, 10)).Y); }
                    if (message == 0x8504) { handled = true; return new IntPtr(text.SelectedText.Length); }
                    return IntPtr.Zero;
                });
        };
        target.Closed += (_, _) => app.Shutdown();
        app.MainWindow = target; target.Show();
    }

    private static async Task<int> ProbeAsync(System.Windows.Application app)
    {
        var root = Path.Combine(AppContext.BaseDirectory, "selection-evidence-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(root);
        var owner = new Window { Width = 1, Height = 1, Left = -10000, Top = -10000, ShowInTaskbar = false };
        app.MainWindow = owner; owner.Show();
        var ownerHandle = new WindowInteropHelper(owner).Handle;
        var gate = new SelectionClipboardGate();
        Console.WriteLine("STEP ORIGINAL_CLIPBOARD_SNAPSHOT");
        using var original = await CaptureSnapshotAsync(ownerHandle);
        Console.WriteLine("PASS ORIGINAL_CLIPBOARD_SNAPSHOT");
        uint lastOwnedSequence = original.Sequence;
        Process? child = null;
        ServiceProvider? services = null;
        ClipboardMonitorService? monitor = null;
        try
        {
            var registrations = new ServiceCollection();
            Console.WriteLine("STEP REGISTER_LOGGING");
            registrations.AddLogging();
            Console.WriteLine("STEP REGISTER_APPLICATION");
            registrations.AddApplication();
            Console.WriteLine("STEP REGISTER_INFRASTRUCTURE");
            registrations.AddInfrastructure(Path.Combine(root, "data"));
            Console.WriteLine("STEP REGISTER_WINDOWS");
            registrations.AddSingleton(gate);
            registrations.AddSingleton<ISystemClipboard, WindowsSystemClipboard>();
            registrations.AddSingleton<ILocalAssetStore, WindowsLocalAssetStore>();
            registrations.AddSingleton<IClipboardTextRewriter>(sp => (WindowsSystemClipboard)sp.GetRequiredService<ISystemClipboard>());
            registrations.AddSingleton<ForegroundApplicationService>();
            registrations.AddSingleton<ClipboardMonitorService>();
            registrations.AddSingleton<ISelectionTextCapture, SelectionTextCapture>();
            registrations.AddSingleton<ISelectionGestureMonitor, SelectionGestureMonitor>();
            registrations.AddSingleton<SelectionAiController>();
            using var noNetwork = new NoNetworkProvider();
            registrations.AddSingleton<ITextAiProvider>(noNetwork);
            services = registrations.BuildServiceProvider();
            Console.WriteLine("STEP INITIALIZE_ISOLATED_DATA");
            var context = await services.GetRequiredService<ApplicationBootstrapper>().InitializeAsync();
            monitor = services.GetRequiredService<ClipboardMonitorService>();
            await monitor.StartAsync(owner);
            child = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "--selection-target") { UseShellExecute = false })!;
            for (int i = 0; i < 100 && child.MainWindowHandle == IntPtr.Zero; i++) { await Task.Delay(50); child.Refresh(); }
            Check(child.MainWindowHandle != IntPtr.Zero, "TARGET_STARTED");
            for (int i = 0; i < 100 && SendMessage(child.MainWindowHandle, 0x8501, IntPtr.Zero, IntPtr.Zero) != new IntPtr(1); i++)
                await Task.Delay(30);
            Check(SendMessage(child.MainWindowHandle, 0x8501, IntPtr.Zero, IntPtr.Zero) == new IntPtr(1), "TARGET_INPUT_READY");
            var clipboard = services.GetRequiredService<ISystemClipboard>();
            var capture = services.GetRequiredService<ISelectionTextCapture>();
            const string paddedValue = "selected\0padding-must-not-be-read";
            var paddedText = Marshal.StringToHGlobalUni(paddedValue);
            try { Check(SelectionTextCapture.ReadNullTerminatedText(paddedText, (paddedValue.Length + 1) * 2) == "selected", "NUL_TERMINATOR_EXCLUDES_ALLOCATION_PADDING"); }
            finally { Marshal.FreeHGlobal(paddedText); }
            var vault = services.GetRequiredService<ICredentialVault>();
            var testCredential = "SelectionAI-Probe/" + Guid.NewGuid().ToString("N");
            try
            {
                await vault.WriteAsync(testCredential, "synthetic-test-value");
                Check(await vault.ReadAsync(testCredential) == "synthetic-test-value", "WINDOWS_CREDENTIAL_WRITE_READ");
            }
            finally { await vault.DeleteAsync(testCredential); }
            Check(await vault.ReadAsync(testCredential) is null, "WINDOWS_CREDENTIAL_DELETE");

            await clipboard.WriteTextAsync("Original rich clipboard text");
            lastOwnedSequence = NativeMethods.GetClipboardSequenceNumber();
            var rich = new DataObject();
            rich.SetText("Original rich clipboard text");
            rich.SetData(DataFormats.Html, "<b>Original rich clipboard text</b>");
            System.Windows.Clipboard.SetDataObject(rich, true);
            lastOwnedSequence = NativeMethods.GetClipboardSequenceNumber();
            await FocusAsync(child);
            Check(await capture.CaptureAsync(owner) == Selected, "TEXT_SELECTED_FROM_INDEPENDENT_PROCESS");
            lastOwnedSequence = NativeMethods.GetClipboardSequenceNumber();
            Check(System.Windows.Clipboard.GetText() == "Original rich clipboard text", "ORIGINAL_TEXT_RESTORED");
            Check((string)System.Windows.Clipboard.GetData(DataFormats.Html) == "<b>Original rich clipboard text</b>", "HTML_FORMAT_RESTORED");

            var imagePath = Path.Combine(root, "original.png");
            var bitmap = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[] { 10, 20, 30, 255 }, 4);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var file = File.Create(imagePath)) encoder.Save(file);
            await clipboard.WriteImageAsync(imagePath);
            lastOwnedSequence = NativeMethods.GetClipboardSequenceNumber();
            await FocusAsync(child);
            Check(await capture.CaptureAsync(owner) == Selected, "IMAGE_BASELINE_CAPTURE");
            lastOwnedSequence = NativeMethods.GetClipboardSequenceNumber();
            Check(System.Windows.Clipboard.GetImage()?.PixelWidth == 1, "ORIGINAL_IMAGE_RESTORED");

            var file1 = Path.Combine(root, "one.txt"); var file2 = Path.Combine(root, "two.txt");
            await File.WriteAllTextAsync(file1, "1"); await File.WriteAllTextAsync(file2, "2");
            await clipboard.WriteFilesAsync([file1, file2]);
            lastOwnedSequence = NativeMethods.GetClipboardSequenceNumber();
            await FocusAsync(child);
            Check(await capture.CaptureAsync(owner) == Selected, "FILE_BASELINE_CAPTURE");
            lastOwnedSequence = NativeMethods.GetClipboardSequenceNumber();
            Check(System.Windows.Clipboard.GetFileDropList().Cast<string>().SequenceEqual(new[] { file1, file2 }), "ORIGINAL_FILES_RESTORED");
            using (var previous = await CaptureSnapshotAsync(ownerHandle))
            {
                var oldSequence = NativeMethods.GetClipboardSequenceNumber();
                await clipboard.WriteTextAsync("Synthetic concurrent copy");
                lastOwnedSequence = NativeMethods.GetClipboardSequenceNumber();
                Check(!previous.TryRestore(ownerHandle, oldSequence, gate), "CONCURRENT_COPY_NOT_OVERWRITTEN");
                Check(System.Windows.Clipboard.GetText() == "Synthetic concurrent copy", "LATEST_CLIPBOARD_CONTENT_PRESERVED");
                gate.Begin((uint)Environment.ProcessId);
                try { Check(await TryRestoreAsync(previous, ownerHandle, lastOwnedSequence, gate), "PROBE_FILE_BASELINE_RESTORED"); }
                finally { gate.End(); }
                lastOwnedSequence = NativeMethods.GetClipboardSequenceNumber();
            }
            SendMessage(child.MainWindowHandle, 0x8500, new IntPtr(1), IntPtr.Zero);
            var beforeBlockedCopy = NativeMethods.GetClipboardSequenceNumber();
            Check(await capture.CaptureAsync(owner) is null, "COPY_RESTRICTED_APP_RETURNS_EMPTY");
            Check(NativeMethods.GetClipboardSequenceNumber() == beforeBlockedCopy, "COPY_RESTRICTION_PRESERVES_CLIPBOARD");
            SendMessage(child.MainWindowHandle, 0x8500, IntPtr.Zero, IntPtr.Zero);
            using (var release = new ManualResetEventSlim())
            {
                var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var holder = Task.Run(() =>
                {
                    if (!NativeMethods.OpenClipboard(IntPtr.Zero)) { held.SetException(new InvalidOperationException("Cannot hold clipboard.")); return; }
                    try { held.SetResult(); release.Wait(TimeSpan.FromSeconds(5)); }
                    finally { NativeMethods.CloseClipboard(); }
                });
                await held.Task;
                try
                {
                    await AssertBusyAsync(capture, owner);
                    Check(NativeMethods.GetClipboardSequenceNumber() == beforeBlockedCopy, "BUSY_CLIPBOARD_UNCHANGED");
                }
                finally { release.Set(); await holder; }
            }
            await Task.Delay(300);
            var history = await services.GetRequiredService<IClipboardHistoryRepository>().SearchAsync(new ClipboardQuery(context.Workspace.Id));
            Check(history.All(item => item.TextContent != Selected), "SELECTION_NOT_IN_HISTORY");
            Check(Marshal.SizeOf<NativeMethods.INPUT>() == 40, "X64_INPUT_LAYOUT");
            using (var first = new GlobalHotkeyService(0x6001))
            using (var conflicting = new GlobalHotkeyService(0x6002))
            {
                first.Register(owner, HotkeyGesture.Parse("Ctrl+Shift+F11"));
                try { conflicting.Register(owner, HotkeyGesture.Parse("Ctrl+Shift+F11")); throw new InvalidOperationException("Conflict was accepted."); }
                catch (System.ComponentModel.Win32Exception) { Check(true, "HOTKEY_CONFLICT_REPORTED"); }
            }
            var settings = services.GetRequiredService<IAppSettingsStore>();
            await settings.SaveAsync((await settings.LoadAsync()) with { IsCapturePaused = true });
            await FocusAsync(child);
            try { await capture.CaptureAsync(owner); throw new InvalidOperationException("Paused capture was accepted."); }
            catch (InvalidOperationException ex) when (ex.Message.Contains("暂停")) { Check(true, "PAUSE_BLOCKS_SELECTION"); }
            await settings.SaveAsync((await settings.LoadAsync()) with { IsCapturePaused = false, ExcludedProcesses = [child.ProcessName] });
            try { await capture.CaptureAsync(owner); throw new InvalidOperationException("Excluded capture was accepted."); }
            catch (InvalidOperationException ex) when (ex.Message.Contains("排除")) { Check(true, "EXCLUSION_BLOCKS_SELECTION"); }
            await CheckVisualsAsync(app, services, root);
            await CheckTriggersAsync(app, services, child, owner, root);
            lastOwnedSequence = NativeMethods.GetClipboardSequenceNumber();
            Check(noNetwork.Calls == 1, "ONLY_CLICKED_ACTION_CALLS_PROVIDER");
            await Task.Delay(200);
            history = await services.GetRequiredService<IClipboardHistoryRepository>().SearchAsync(new ClipboardQuery(context.Workspace.Id));
            Check(history.All(item => item.TextContent?.Contains("Selection", StringComparison.Ordinal) != true), "HOTKEY_AND_FLOATING_SELECTION_NOT_IN_HISTORY");
            await File.WriteAllTextAsync(Path.Combine(root, "result.txt"), "All selection probe assertions passed. No real model request was sent.");
            Console.WriteLine("SELECTION_PROBE_PASSED " + root);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("PROBE_FAILURE " + ex);
            throw;
        }
        finally
        {
            Console.WriteLine("STEP RESTORE_ENTERING_CLIPBOARD");
            monitor?.Dispose();
            var clipboardOwner = SelectionNative.GetClipboardOwner();
            NativeMethods.GetWindowThreadProcessId(clipboardOwner, out var ownerProcess);
            if (ownerProcess == (uint)Environment.ProcessId || (child is not null && ownerProcess == (uint)child.Id))
                lastOwnedSequence = NativeMethods.GetClipboardSequenceNumber();
            if (child is not null) { if (!child.HasExited) child.Kill(); child.Dispose(); }
            gate.Begin((uint)Environment.ProcessId);
            try
            {
                if (!await TryRestoreAsync(original, ownerHandle, lastOwnedSequence, gate))
                    throw new InvalidOperationException("Original clipboard could not be restored or changed externally.");
                Console.WriteLine("PASS ENTERING_CLIPBOARD_RESTORED");
            }
            finally
            {
                gate.End();
                Console.WriteLine("STEP DISPOSE_PROBE_SERVICES");
                services?.Dispose();
                owner.Close();
                Console.WriteLine("PASS PROBE_CLEANUP");
            }
        }
    }

    private static async Task CheckVisualsAsync(System.Windows.Application app, IServiceProvider services, string root)
    {
        var project = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../../"));
        foreach (var file in new[] { "Palette.xaml", "Controls.xaml" })
            app.Resources.MergedDictionaries.Add((ResourceDictionary)XamlReader.Parse(File.ReadAllText(Path.Combine(project, "src/ClipboardManager.Windows/Themes", file))));
        using var fake = new NoNetworkProvider();
        var window = new SelectionAiResultWindow(Selected, new SelectionAiSettings().Templates[0], new(), new SelectionAiService(fake));
        Check(fake.Calls == 0, "CONSTRUCTION_DOES_NOT_SEND");
        window.Show(); await Task.Delay(100); window.UpdateLayout();
        Check(fake.Calls == 1, "ACTION_WINDOW_SENDS_AUTOMATICALLY_ONCE");
        Check(Descendants<TextBox>(window).Count() == 1, "RESULT_WINDOW_HAS_ONLY_ONE_TEXT_FIELD");
        Check(Descendants<TextBox>(window).Single().Text == "Synthetic model result", "MODEL_RESULT_SHOWN_IN_WPF");
        Check(!Descendants<TextBlock>(window).Any(label => label.Text is "选中的原文" or "将发送的完整提示词"), "SOURCE_AND_PROMPT_NOT_DISPLAYED");
        Check(!Descendants<System.Windows.Controls.Button>(window).Any(button => button.Content as string == "确认发送"), "NO_CONFIRMATION_BUTTON");
        var rendered = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        rendered.Render(window);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(rendered));
        using (var stream = File.Create(Path.Combine(root, "result.png"))) encoder.Save(stream);
        double shortHeight = window.ActualHeight;
        var output = Descendants<TextBox>(window).Single();
        double shortResultHeight = output.ActualHeight;
        output.Text = string.Join("\n", Enumerable.Repeat("Long model response for layout verification.", 20));
        await Task.Delay(100); window.UpdateLayout();
        Check(window.ActualHeight > shortHeight && output.ActualHeight > shortResultHeight, "RESULT_AND_WINDOW_GROW_WITH_CONTENT");
        output.Text = string.Join("\n", Enumerable.Repeat("Very long model response for bounded layout.", 2000));
        await Task.Delay(100); window.UpdateLayout();
        Check(window.ActualHeight <= window.MaxHeight + 0.1, "LONG_RESULT_STAYS_WITHIN_SCREEN_HEIGHT");
        Check(output.ActualHeight <= output.MaxHeight + 0.1, "LONG_RESULT_TEXTBOX_HEIGHT_IS_BOUNDED");
        var scroll = Descendants<ScrollViewer>(output).First();
        Check(scroll.ScrollableHeight > 0, "VERY_LONG_RESULT_CAN_SCROLL");
        var longImage = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        longImage.Render(window);
        var longPng = new PngBitmapEncoder(); longPng.Frames.Add(BitmapFrame.Create(longImage));
        using (var stream = File.Create(Path.Combine(root, "result-long.png"))) longPng.Save(stream);
        window.Close();
        var pending = new PendingProvider();
        var cancelled = new SelectionAiResultWindow(Selected, new SelectionAiSettings().Templates[0], new(), new SelectionAiService(pending));
        cancelled.Show(); await Task.Delay(50);
        Check(pending.Calls == 1, "PENDING_REQUEST_STARTED");
        cancelled.Close(); await Task.Delay(50);
        Check(pending.Cancelled, "CLOSE_CANCELS_PENDING_REQUEST");
        Check(pending.Calls == 1, "CANCEL_DOES_NOT_RETRY");
        var settings = new SelectionAiSettingsWindow(services.GetRequiredService<IAppSettingsStore>(), services.GetRequiredService<ICredentialVault>());
        settings.Show(); await Task.Delay(100); settings.UpdateLayout();
        var image = new RenderTargetBitmap((int)settings.ActualWidth, (int)settings.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        image.Render(settings);
        var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(image));
        using (var stream = File.Create(Path.Combine(root, "settings.png"))) png.Save(stream);
        settings.Close();
        Check(true, "WPF_WINDOWS_RENDERED");
    }

    private static async Task<int> ProbeResultUiAsync(System.Windows.Application app, string[] args)
    {
        var root = Path.Combine(AppContext.BaseDirectory, "result-evidence-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(root);
        var registrations = new ServiceCollection();
        registrations.AddLogging(); registrations.AddApplication();
        registrations.AddInfrastructure(Path.Combine(root, "data"));
        using var services = registrations.BuildServiceProvider();
        await CheckVisualsAsync(app, services, root);
        if (args.Contains("--selection-gateway"))
        {
            var gateway = new SelectionAiSettings { Provider = "LocalGateway", BaseUrl = "http://127.0.0.1:3425/v1", Model = "qoder/dfmodel" };
            // Only synthetic text is sent. The optional configuration update preserves all other settings.
            var template = new PromptTemplate(Guid.NewGuid(), "网关联调", "Translate into Simplified Chinese, output only the translation: {text}");
            var result = new SelectionAiResultWindow("Hello", template, gateway, services.GetRequiredService<SelectionAiService>());
            result.Show();
            var timer = Stopwatch.StartNew();
            while (string.IsNullOrEmpty(Descendants<TextBox>(result).Single().Text) && timer.Elapsed < TimeSpan.FromSeconds(65))
            {
                if (Descendants<System.Windows.Controls.Button>(result).Any(button => button.Content as string == "重试" && button.IsVisible))
                    throw new InvalidOperationException("Gateway request failed: " + string.Join(" ", Descendants<TextBlock>(result).Select(label => label.Text)));
                await Task.Delay(100);
            }
            Check(!string.IsNullOrWhiteSpace(Descendants<TextBox>(result).Single().Text), "REAL_GATEWAY_RESPONSE_DISPLAYED");
            result.UpdateLayout();
            var image = new RenderTargetBitmap((int)result.ActualWidth, (int)result.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            image.Render(result);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
            using (var stream = File.Create(Path.Combine(root, "gateway-result.png"))) encoder.Save(stream);
            result.Close();
            if (args.Contains("--configure"))
            {
                var paths = new ClipboardManager.Infrastructure.Paths.AppPaths();
                var store = new ClipboardManager.Infrastructure.Paths.JsonAppSettingsStore(paths);
                var current = await store.LoadAsync();
                File.Copy(paths.SettingsPath, paths.SettingsPath + ".before-gateway-" + DateTime.Now.ToString("yyyyMMddHHmmss"), false);
                await store.SaveAsync(current with
                {
                    SelectionAi = current.SelectionAi with { Provider = gateway.Provider, BaseUrl = gateway.BaseUrl, Model = gateway.Model }
                });
                Check((await store.LoadAsync()).SelectionAi.BaseUrl == gateway.BaseUrl, "USER_GATEWAY_SETTINGS_UPDATED");
            }
        }
        await File.WriteAllTextAsync(Path.Combine(root, "result.txt"), "Result UI assertions passed. Gateway requests, if enabled, used only synthetic text.");
        Console.WriteLine("RESULT_UI_PROBE_PASSED " + root);
        return 0;
    }

    private static async Task CheckTriggersAsync(System.Windows.Application app, IServiceProvider services, Process child, Window owner, string root)
    {
        var store = services.GetRequiredService<IAppSettingsStore>();
        var latest = await store.LoadAsync();
        await store.SaveAsync(latest with { ExcludedProcesses = [], SelectionAi = latest.SelectionAi with { HotkeyEnabled = true, Hotkey = "Ctrl+Shift+F10" } });
        var controller = services.GetRequiredService<SelectionAiController>();
        int gestureCount = 0;
        services.GetRequiredService<ISelectionGestureMonitor>().Selected += _ => { gestureCount++; Console.WriteLine("PASS SYSTEM_SELECTION_GESTURE_RECEIVED"); };
        await controller.StartAsync(owner);
        owner.Hide();
        await FocusAsync(child);
        var keys = new[] { Keyboard(0x11, false), Keyboard(0x10, false), Keyboard(0x79, false), Keyboard(0x79, true), Keyboard(0x10, true), Keyboard(0x11, true) };
        Check(NativeMethods.SendInput((uint)keys.Length, keys, Marshal.SizeOf<NativeMethods.INPUT>()) == keys.Length, "HOTKEY_INPUT_SENT");
        var toolbar = await WaitForToolbarAsync(app);
        Check(toolbar is not null, "GLOBAL_HOTKEY_OPENS_TOOLBAR");
        Check(Math.Abs(toolbar!.ActualHeight - 34) < 0.1, "TOOLBAR_HEIGHT_IS_34");
        Check(!Descendants<TextBox>(toolbar).Any(), "TOOLBAR_HAS_NO_SELECTION_PREVIEW");
        Check(Descendants<System.Windows.Controls.Button>(toolbar).Select(button => button.Content as string).SequenceEqual(["翻译", "摘要", "解释"]), "TOOLBAR_HAS_ONLY_ACTIONS");
        var rendered = new RenderTargetBitmap((int)Math.Ceiling(toolbar.ActualWidth), 34, 96, 96, PixelFormats.Pbgra32);
        rendered.Render(toolbar);
        var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(rendered));
        using (var stream = File.Create(Path.Combine(root, "toolbar.png"))) png.Save(stream);
        Check(!owner.IsVisible, "HIDDEN_MAIN_WINDOW_STAYS_HIDDEN");
        Check(SetCursorPos(5, 5), "OUTSIDE_CLICK_POSITIONED");
        var outsideDown = new NativeMethods.INPUT
        {
            Type = 0, Data = new NativeMethods.InputUnion { Mouse = new NativeMethods.MOUSEINPUT { Flags = 2 } }
        };
        var outsideUp = new NativeMethods.INPUT
        {
            Type = 0, Data = new NativeMethods.InputUnion { Mouse = new NativeMethods.MOUSEINPUT { Flags = 4 } }
        };
        Check(NativeMethods.SendInput(1, [outsideDown], Marshal.SizeOf<NativeMethods.INPUT>()) == 1, "OUTSIDE_MOUSE_DOWN_SENT");
        Check(NativeMethods.SendInput(1, [outsideUp], Marshal.SizeOf<NativeMethods.INPUT>()) == 1, "OUTSIDE_MOUSE_UP_SENT");
        await Task.Delay(120);
        Check(!toolbar.IsVisible, "OUTSIDE_CLICK_CLOSES_TOOLBAR");
        await FocusAsync(child);
        Check(NativeMethods.SendInput((uint)keys.Length, keys, Marshal.SizeOf<NativeMethods.INPUT>()) == keys.Length, "HOTKEY_REOPENS_AFTER_OUTSIDE_CLICK");
        toolbar = await WaitForToolbarAsync(app);
        Check(toolbar is not null, "TOOLBAR_REOPENS_AFTER_OUTSIDE_CLICK");
        Descendants<System.Windows.Controls.Button>(toolbar!).First(button => button.Content as string == "翻译")
            .RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        await Task.Delay(100);
        var preview = app.Windows.Cast<Window>().Single(window => window.Title == "划词 · 翻译");
        Check(preview.IsVisible && !owner.IsVisible, "RESULT_WITH_HIDDEN_MAIN_WINDOW");
        preview.Close();
        latest = await store.LoadAsync();
        await store.SaveAsync(latest with { SelectionAi = latest.SelectionAi with { HotkeyEnabled = false, FloatingToolbarEnabled = true } });
        await controller.ReloadAsync();
        await FocusAsync(child);
        var x = SendMessage(child.MainWindowHandle, 0x8502, IntPtr.Zero, IntPtr.Zero).ToInt32();
        var y = SendMessage(child.MainWindowHandle, 0x8503, IntPtr.Zero, IntPtr.Zero).ToInt32();
        SetCursorPos(x, y);
        var down = new NativeMethods.INPUT { Type = 0, Data = new NativeMethods.InputUnion { Mouse = new NativeMethods.MOUSEINPUT { Flags = 2 } } };
        var up = new NativeMethods.INPUT { Type = 0, Data = new NativeMethods.InputUnion { Mouse = new NativeMethods.MOUSEINPUT { Flags = 4 } } };
        Check(NativeMethods.SendInput(1, [down], Marshal.SizeOf<NativeMethods.INPUT>()) == 1, "MOUSE_DOWN_SENT");
        await Task.Delay(80);
        Check(SetCursorPos(x + 110, y), "MOUSE_DRAG_POSITIONED");
        await Task.Delay(80);
        Check(NativeMethods.SendInput(1, [up], Marshal.SizeOf<NativeMethods.INPUT>()) == 1, "MOUSE_UP_SENT");
        await Task.Delay(80);
        Check(SendMessage(child.MainWindowHandle, 0x8504, IntPtr.Zero, IntPtr.Zero).ToInt32() > 0, "ACTUAL_MOUSE_TEXT_SELECTION");
        toolbar = await WaitForToolbarAsync(app);
        Check(gestureCount > 0, "SYSTEM_MOUSE_GESTURE_OBSERVED");
        Check(toolbar is not null, "MOUSE_SELECTION_OPENS_FLOATING_TOOLBAR");
        toolbar!.Close(); controller.Dispose();
    }

    private static async Task<Window?> WaitForToolbarAsync(System.Windows.Application app)
    {
        for (int i = 0; i < 80; i++)
        {
            var window = app.Windows.Cast<Window>().FirstOrDefault(w => w.Title == "划词操作" && w.IsVisible);
            if (window is not null) return window;
            await Task.Delay(40);
        }
        return null;
    }

    private static NativeMethods.INPUT Keyboard(ushort key, bool up) => new()
    {
        Type = NativeMethods.InputKeyboard,
        Data = new NativeMethods.InputUnion { Keyboard = new NativeMethods.KEYBDINPUT { VirtualKey = key, Flags = up ? NativeMethods.KeyEventKeyUp : 0 } }
    };
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetCursorPos(int x, int y);

    private sealed class NoNetworkProvider : ITextAiProvider, IDisposable
    {
        public int Calls;
        public Task<AiTextResponse> CompleteAsync(AiTextRequest request, CancellationToken cancellationToken = default)
        { Calls++; return Task.FromResult(new AiTextResponse("# Synthetic model result")); }
        public void Dispose() { }
    }

    private sealed class PendingProvider : ITextAiProvider
    {
        public int Calls;
        public bool Cancelled;
        public async Task<AiTextResponse> CompleteAsync(AiTextRequest request, CancellationToken cancellationToken = default)
        {
            Calls++;
            try { await Task.Delay(Timeout.Infinite, cancellationToken); }
            catch (OperationCanceledException) { Cancelled = true; throw; }
            throw new InvalidOperationException("Pending request unexpectedly completed.");
        }
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }

    private static async Task AssertBusyAsync(ISelectionTextCapture capture, Window owner)
    {
        var watch = Stopwatch.StartNew();
        try { await capture.CaptureAsync(owner); throw new InvalidOperationException("Busy clipboard was accepted."); }
        catch (System.ComponentModel.Win32Exception)
        { Check(watch.Elapsed < TimeSpan.FromSeconds(2), "BUSY_CLIPBOARD_FINITE_RETRIES"); }
    }

    private static async Task<bool> TryRestoreAsync(SelectionClipboardSnapshot snapshot, IntPtr owner, uint expectedSequence, SelectionClipboardGate gate)
    {
        for (int attempt = 0; attempt < 5; attempt++)
        {
            var current = NativeMethods.GetClipboardSequenceNumber();
            if (current != expectedSequence)
                throw new InvalidOperationException($"Clipboard changed externally: expected {expectedSequence}, actual {current}.");
            if (snapshot.TryRestore(owner, expectedSequence, gate)) return true;
            await Task.Delay(30);
        }
        return false;
    }

    private static async Task<SelectionClipboardSnapshot> CaptureSnapshotAsync(IntPtr owner)
    {
        for (int attempt = 0; ; attempt++)
        {
            try { return SelectionClipboardSnapshot.Capture(owner); }
            catch (System.ComponentModel.Win32Exception ex) when (attempt < 4)
            {
                Console.WriteLine($"SNAPSHOT_RETRY error={ex.NativeErrorCode} lock={NativeMethods.GetOpenClipboardWindow()} owner={owner}");
                await Task.Delay(30);
            }
        }
    }

    private static async Task FocusAsync(Process child)
    {
        for (int attempt = 0; attempt < 5; attempt++)
        {
        var current = NativeMethods.GetCurrentThreadId();
        var target = NativeMethods.GetWindowThreadProcessId(child.MainWindowHandle, out _);
        var foreground = NativeMethods.GetWindowThreadProcessId(NativeMethods.GetForegroundWindow(), out _);
        bool attachedTarget = current != target && NativeMethods.AttachThreadInput(current, target, true);
        bool attachedForeground = current != foreground && target != foreground && NativeMethods.AttachThreadInput(current, foreground, true);
        try
        {
            NativeMethods.BringWindowToTop(child.MainWindowHandle);
            NativeMethods.SetForegroundWindow(child.MainWindowHandle);
        }
        finally
        {
            if (attachedForeground) NativeMethods.AttachThreadInput(current, foreground, false);
            if (attachedTarget) NativeMethods.AttachThreadInput(current, target, false);
        }
        await Task.Delay(200);
        if (NativeMethods.GetForegroundWindow() == child.MainWindowHandle) break;
        }
        Check(NativeMethods.GetForegroundWindow() == child.MainWindowHandle, "TARGET_FOCUSED");
    }
    private static void Check(bool condition, string name)
    { if (!condition) throw new InvalidOperationException(name); Console.WriteLine("PASS " + name); }
}
