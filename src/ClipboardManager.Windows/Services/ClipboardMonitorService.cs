using ClipboardManager.Application.Services;
using ClipboardManager.Domain.Contracts;
using ClipboardManager.Domain.Models;
using ClipboardManager.Windows.Interop;
using Microsoft.Extensions.Logging;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using System.Windows;
using System.Windows.Interop;

namespace ClipboardManager.Windows.Services;

public sealed class ClipboardMonitorService(
    ISystemClipboard systemClipboard,
    ClipboardPayloadCaptureService captureService,
    ClipboardCalculationService calculationService,
    ForegroundApplicationService foregroundApplicationService,
    IAppSettingsStore settingsStore,
    SelectionClipboardGate selectionGate,
    ILogger<ClipboardMonitorService> logger) : IDisposable
{
    private readonly Channel<ClipboardCaptureAttribution> _events = Channel.CreateBounded<ClipboardCaptureAttribution>(
        new BoundedChannelOptions(32)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.DropOldest
        });
    private CancellationTokenSource? _cancellation;
    private Task? _consumer;
    private HwndSource? _source;
    private IntPtr _windowHandle;
    private bool _registered;
    private bool _disposed;

    private bool _manuallyPaused;
    private DateTimeOffset? _pausedUntil;
    private System.Windows.Threading.DispatcherTimer? _pauseTimer;
    public bool IsPaused
    {
        get => _manuallyPaused || _pausedUntil > DateTimeOffset.UtcNow;
        private set => _manuallyPaused = value;
    }
    public event EventHandler<bool>? PauseChanged;

    public async Task StartAsync(Window window, CancellationToken cancellationToken = default)
    {
        if (_registered)
        {
            return;
        }

        var initialSettings = await settingsStore.LoadAsync(cancellationToken);
        IsPaused = initialSettings.IsCapturePaused;
        _pausedUntil = initialSettings.CapturePausedUntil;
        _pauseTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _pauseTimer.Tick += (_, _) =>
        {
            if (_pausedUntil is not null && _pausedUntil <= DateTimeOffset.UtcNow)
            {
                _pausedUntil = null;
                PauseChanged?.Invoke(this, IsPaused);
            }
        };
        _pauseTimer.Start();
        _windowHandle = new WindowInteropHelper(window).EnsureHandle();
        _source = HwndSource.FromHwnd(_windowHandle)
            ?? throw new InvalidOperationException("无法取得窗口消息源。");
        _source.AddHook(WindowProc);
        if (!NativeMethods.AddClipboardFormatListener(_windowHandle))
        {
            _source.RemoveHook(WindowProc);
            _source = null;
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法注册剪切板监听。");
        }

        _registered = true;
        _cancellation = new CancellationTokenSource();
        _consumer = ConsumeAsync(_cancellation.Token);
    }

    public async Task SetPausedAsync(bool paused, CancellationToken cancellationToken = default)
    {
        var settings = await settingsStore.LoadAsync(cancellationToken);
        await settingsStore.SaveAsync(settings with { IsCapturePaused = paused, CapturePausedUntil = null }, cancellationToken);
        IsPaused = paused;
        _pausedUntil = null;
        PauseChanged?.Invoke(this, paused);
    }

    public async Task PauseForAsync(TimeSpan duration, CancellationToken cancellationToken = default)
    {
        if (duration <= TimeSpan.Zero || duration > TimeSpan.FromDays(1))
            throw new ArgumentOutOfRangeException(nameof(duration));
        var settings = await settingsStore.LoadAsync(cancellationToken);
        var until = DateTimeOffset.UtcNow.Add(duration);
        await settingsStore.SaveAsync(settings with { IsCapturePaused = false, CapturePausedUntil = until }, cancellationToken);
        _manuallyPaused = false;
        _pausedUntil = until;
        PauseChanged?.Invoke(this, true);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _pauseTimer?.Stop();
        _pauseTimer = null;
        if (_registered)
        {
            NativeMethods.RemoveClipboardFormatListener(_windowHandle);
            _registered = false;
        }

        _source?.RemoveHook(WindowProc);
        _source = null;
        _events.Writer.TryComplete();
        _cancellation?.Cancel();
        try
        {
            _consumer?.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException exception) when (
            exception.InnerExceptions.All(item => item is OperationCanceledException))
        {
        }

        _cancellation?.Dispose();
    }

    private IntPtr WindowProc(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == NativeMethods.WmClipboardUpdate)
        {
            var sequence = NativeMethods.GetClipboardSequenceNumber();
            if (!IsPaused && !selectionGate.Observe(sequence))
            {
                // The foreground app is diagnostic context only. Attribution and privacy are
                // based on the clipboard owner, which can differ while another app is active.
                _events.Writer.TryWrite(new ClipboardCaptureAttribution(
                    sequence,
                    foregroundApplicationService.GetClipboardOwner(),
                    foregroundApplicationService.GetCurrentSource()));
            }

            handled = false;
        }

        return IntPtr.Zero;
    }

    private async Task ConsumeAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var clipboardEvent in _events.Reader.ReadAllAsync(cancellationToken))
            {
                var source = clipboardEvent.Owner.Source;
                try
                {
                    // Check before touching the clipboard, not only after materializing its payload.
                    var settings = await settingsStore.LoadAsync(cancellationToken);
                    if (!CapturePrivacyPolicy.CanRead(settings, source, DateTimeOffset.UtcNow, clipboardEvent.Owner.IsKnown)) continue;
                    if (IsPaused || selectionGate.Observe(clipboardEvent.Sequence) ||
                        !clipboardEvent.MatchesCurrent(
                            NativeMethods.GetClipboardSequenceNumber(),
                            foregroundApplicationService.GetClipboardOwner(),
                            foregroundApplicationService.GetCurrentSource())) continue;
                    var payload = await systemClipboard.ReadAsync(cancellationToken);
                    if (selectionGate.Observe(clipboardEvent.Sequence) ||
                        !clipboardEvent.MatchesCurrent(
                            NativeMethods.GetClipboardSequenceNumber(),
                            foregroundApplicationService.GetClipboardOwner(),
                            foregroundApplicationService.GetCurrentSource())) continue;
                    var result = await captureService.CaptureAsync(payload, source, cancellationToken);
                    if (result.Disposition is CaptureDisposition.Created or CaptureDisposition.Merged or CaptureDisposition.Restored)
                    {
                        logger.LogDebug(
                            "Captured clipboard payload {Disposition}; item {ItemId}; source {Source}; bytes {Bytes}.",
                            result.Disposition,
                            result.Item?.Id,
                            source.ProcessName ?? clipboardEvent.Foreground.ProcessName,
                            result.Item?.ContentSize);
                        if (!IsPaused && payload.Type == ClipboardPayloadType.Text)
                            await calculationService.TryCalculateAsync(payload.Text, clipboardEvent.Sequence, source, cancellationToken);
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception exception)
                {
                    logger.LogWarning(exception, "Clipboard text capture failed; source {Source}.", source.ProcessName);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }
}
