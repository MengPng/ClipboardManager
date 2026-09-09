using ClipboardManager.Application;
using ClipboardManager.Application.Services;
using ClipboardManager.Domain.Contracts;
using ClipboardManager.Infrastructure;
using ClipboardManager.Infrastructure.Paths;
using ClipboardManager.Windows.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Events;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Windows;

namespace ClipboardManager.Windows;

public partial class App : System.Windows.Application
{
    private IHost? _host;
    private SingleInstanceCoordinator? _singleInstance;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        await WaitForRestartParentAsync(e.Args);

        _singleInstance = new SingleInstanceCoordinator("ClipboardManager.LocalFirst.SingleInstance");
        if (!_singleInstance.TryAcquire())
        {
            // Exiting silently here makes a wedged instance look like the app refuses to launch.
            if (!_singleInstance.SignalExistingInstance())
            {
                System.Windows.MessageBox.Show(
                    "剪切板管理工具已在运行，但没有响应唤出请求，可能已卡住。\n\n"
                        + "请在任务管理器中结束 ClipboardManager.exe 进程后重新启动。",
                    "无法唤出已运行的实例",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }

            Shutdown();
            return;
        }

        try
        {
            _host = Host.CreateDefaultBuilder(e.Args)
                .UseSerilog((_, logger) => ConfigureLogging(logger))
                .ConfigureServices(services =>
                {
                    services.AddApplication();
                    services.AddInfrastructure();
                    services.AddSingleton<MainWindow>();
                    services.AddSingleton<GlobalHotkeyService>();
                    services.AddSingleton<TrayIconService>();
                    services.AddSingleton<ISystemClipboard, WindowsSystemClipboard>();
                    services.AddSingleton<ILocalAssetStore, WindowsLocalAssetStore>();
                    services.AddSingleton<ForegroundApplicationService>();
                    services.AddSingleton<ClipboardMonitorService>();
                    services.AddSingleton<AutomaticPasteService>();
                    services.AddSingleton<WindowDockingService>();
                    services.AddSingleton<AutomaticBackupService>();
                })
                .Build();

            await _host.StartAsync();

            // A startup that stalls leaves no window to complain with, so each step is recorded
            // before it runs. The last line in the log is the step that hung.
            var log = _host.Services.GetRequiredService<ILogger<App>>();
            log.LogInformation("Host started; initializing local state.");
            var context = await _host.Services
                .GetRequiredService<ApplicationBootstrapper>()
                .InitializeAsync();

            log.LogInformation("Local state ready; constructing main window.");
            var mainWindow = _host.Services.GetRequiredService<MainWindow>();
            MainWindow = mainWindow;
            mainWindow.RestoreBackupRequested += (_, backupPath) => RestoreBackupAndRestart(mainWindow, backupPath);
            log.LogInformation("Main window constructed; loading history and folders.");
            await mainWindow.InitializeAsync(context);
            _host.Services.GetRequiredService<AutomaticBackupService>().Start(context.Device.DeviceName);
            log.LogInformation("Content loaded; registering clipboard listener.");
            await _host.Services.GetRequiredService<ClipboardMonitorService>().StartAsync(mainWindow);

            var hotkey = _host.Services.GetRequiredService<GlobalHotkeyService>();
            hotkey.Pressed += (_, _) => ToggleMainWindow();
            try
            {
                hotkey.Register(mainWindow, HotkeyGesture.Parse(context.Settings.GlobalHotkey));
            }
            catch (Exception hotkeyException)
            {
                _host.Services.GetRequiredService<ILogger<App>>()
                    .LogWarning(hotkeyException, "Global hotkey registration failed; tray access remains available.");
                System.Windows.MessageBox.Show(
                    $"快捷键 {context.Settings.GlobalHotkey} 注册失败，仍可通过托盘打开。\n\n{hotkeyException.Message}",
                    "快捷键不可用",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }

            var tray = _host.Services.GetRequiredService<TrayIconService>();
            var monitor = _host.Services.GetRequiredService<ClipboardMonitorService>();
            tray.Initialize(
                showWindow: ShowMainWindow,
                toggleCapture: () => Dispatcher.InvokeAsync(async () =>
                    await monitor.SetPausedAsync(!monitor.IsPaused)),
                isCapturePaused: () => monitor.IsPaused,
                restartApplication: () => RestartApplication(mainWindow),
                exitApplication: () =>
                {
                    mainWindow.AllowClose();
                    Shutdown();
                });

            _singleInstance.StartListening(() => Dispatcher.Invoke(ShowMainWindow));
            ShowMainWindow();
            log.LogInformation("Startup complete; main window shown.");
        }
        catch (Exception exception)
        {
            _host?.Services.GetService<ILogger<App>>()?.LogCritical(exception, "Application startup failed.");
            System.Windows.MessageBox.Show(
                $"应用初始化失败：{exception.Message}",
                "剪切板管理工具",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(-1);
        }
    }

    private void RestartApplication(MainWindow mainWindow)
    {
        var executable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable))
        {
            System.Windows.MessageBox.Show("无法确定当前程序路径，请手动重新打开应用。", "重启失败",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var start = new ProcessStartInfo(executable) { UseShellExecute = true };
        start.ArgumentList.Add($"--restart-after-pid={Environment.ProcessId}");
        Process.Start(start);
        mainWindow.AllowClose();
        Shutdown();
    }

    private void RestoreBackupAndRestart(MainWindow mainWindow, string backupPath)
    {
        var executable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable)) throw new InvalidOperationException("无法确定当前程序路径。");
        var helper = EnsureMaintenanceHelper();
        var start = new ProcessStartInfo(helper) { UseShellExecute = true };
        start.ArgumentList.Add("restore");
        start.ArgumentList.Add(Path.GetFullPath(backupPath));
        start.ArgumentList.Add($"--wait-pid={Environment.ProcessId}");
        start.ArgumentList.Add($"--restart={executable}");
        Process.Start(start);
        mainWindow.AllowClose();
        Shutdown();
    }

    private static string EnsureMaintenanceHelper()
    {
        const string resourceName = "ClipboardManager.Embedded.Maintenance.exe";
        var assembly = Assembly.GetExecutingAssembly();
        using var resource = assembly.GetManifestResourceStream(resourceName);
        if (resource is null)
        {
            var developmentHelper = Path.Combine(AppContext.BaseDirectory, "ClipboardManager.Maintenance.exe");
            if (File.Exists(developmentHelper)) return developmentHelper;
            throw new InvalidOperationException("当前程序未包含备份恢复组件，请使用正式单文件版本。");
        }

        var helperDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ClipboardManager", "runtime");
        Directory.CreateDirectory(helperDirectory);
        var helperPath = Path.Combine(helperDirectory, "ClipboardManager.Maintenance.exe");

        using var memory = new MemoryStream();
        resource.CopyTo(memory);
        var embeddedBytes = memory.ToArray();
        if (File.Exists(helperPath))
        {
            using var existing = File.OpenRead(helperPath);
            var existingHash = SHA256.HashData(existing);
            var embeddedHash = SHA256.HashData(embeddedBytes);
            if (existingHash.AsSpan().SequenceEqual(embeddedHash)) return helperPath;
        }

        var temporaryPath = helperPath + ".tmp-" + Guid.NewGuid().ToString("N");
        File.WriteAllBytes(temporaryPath, embeddedBytes);
        File.Move(temporaryPath, helperPath, true);
        return helperPath;
    }

    private static async Task WaitForRestartParentAsync(IReadOnlyList<string> arguments)
    {
        const string prefix = "--restart-after-pid=";
        var value = arguments.FirstOrDefault(argument => argument.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        if (value is null || !int.TryParse(value[prefix.Length..], out var processId) || processId <= 0) return;

        await Task.Run(() =>
        {
            try
            {
                using var parent = Process.GetProcessById(processId);
                parent.WaitForExit(30_000);
            }
            catch (ArgumentException)
            {
                // The previous process already exited before the replacement started.
            }
        });
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        if (_host is not null)
        {
            _host.Services.GetService<ClipboardMonitorService>()?.Dispose();
            _host.Services.GetService<GlobalHotkeyService>()?.Dispose();
            _host.Services.GetService<TrayIconService>()?.Dispose();
            _host.Services.GetService<WindowDockingService>()?.Dispose();
            _host.Services.GetService<AutomaticBackupService>()?.Dispose();
            await _host.StopAsync(TimeSpan.FromSeconds(3));
            _host.Dispose();
        }

        _singleInstance?.Dispose();
        base.OnExit(e);
    }

    /// <summary>
    /// Writes to the reserved local log directory. A WinExe launched from Explorer has no console,
    /// so console-only logging discarded every diagnostic the app produced.
    /// </summary>
    private static void ConfigureLogging(LoggerConfiguration logger)
    {
        var paths = new AppPaths();
        paths.EnsureCreated();

        logger
            .MinimumLevel.Information()
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .WriteTo.File(
                Path.Combine(paths.LogsPath, "clipboard-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7,
                fileSizeLimitBytes: 8L * 1024 * 1024,
                rollOnFileSizeLimit: true,
                shared: true,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}");
    }

    private void ToggleMainWindow()
    {
        if (MainWindow is null)
        {
            return;
        }

        var docking = _host?.Services.GetService<WindowDockingService>();
        if (MainWindow.IsVisible && (MainWindow.IsActive || docking?.ShouldRemainVisibleAfterDeactivation() == true))
        {
            docking?.Undock();
            MainWindow.Hide();
            return;
        }

        ShowMainWindow(preferChatWindowDocking: true);
    }

    private void ShowMainWindow() => ShowMainWindow(preferChatWindowDocking: false);

    private void ShowMainWindow(bool preferChatWindowDocking)
    {
        if (MainWindow is null)
        {
            return;
        }

        if (MainWindow is ClipboardManager.Windows.MainWindow clipboardWindow) clipboardWindow.RememberPasteTarget();
        var docking = _host?.Services.GetService<WindowDockingService>();
        var docked = preferChatWindowDocking && docking?.TryDockToForeground(MainWindow) == true;
        MainWindow.Show();
        if (MainWindow.WindowState == System.Windows.WindowState.Minimized)
        {
            MainWindow.WindowState = System.Windows.WindowState.Normal;
        }

        if (!docked)
        {
            docking?.Undock();
            DesktopWindowPlacement.PlaceAtCursor(MainWindow);
        }
        MainWindow.Activate();
        MainWindow.Topmost = true;
        MainWindow.Topmost = false;
        MainWindow.Focus();
    }
}
