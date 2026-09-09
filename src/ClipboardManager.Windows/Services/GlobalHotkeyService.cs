using ClipboardManager.Windows.Interop;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace ClipboardManager.Windows.Services;

public sealed class GlobalHotkeyService : IDisposable
{
    private const int HotkeyId = 0x434D;
    private HwndSource? _source;
    private IntPtr _windowHandle;
    private bool _registered;

    public static HotkeyGesture DefaultHotkey { get; } = new(ModifierKeys.Alt, Key.V);

    public event EventHandler? Pressed;

    public void Register(Window window, HotkeyGesture gesture)
    {
        if (_registered)
        {
            throw new InvalidOperationException("Global hotkey is already registered.");
        }

        _windowHandle = new WindowInteropHelper(window).EnsureHandle();
        _source = HwndSource.FromHwnd(_windowHandle)
            ?? throw new InvalidOperationException("Cannot access WPF window message source.");
        _source.AddHook(WindowProc);

        var modifiers = ToNativeModifiers(gesture.Modifiers) | NativeMethods.ModNoRepeat;
        var virtualKey = (uint)KeyInterop.VirtualKeyFromKey(gesture.Key);
        if (!NativeMethods.RegisterHotKey(_windowHandle, HotkeyId, modifiers, virtualKey))
        {
            var error = Marshal.GetLastWin32Error();
            _source.RemoveHook(WindowProc);
            _source = null;
            throw new Win32Exception(error, $"无法注册全局快捷键 {gesture}，可能已被其他应用占用。");
        }

        _registered = true;
    }

    public void Dispose()
    {
        if (_registered)
        {
            NativeMethods.UnregisterHotKey(_windowHandle, HotkeyId);
            _registered = false;
        }

        _source?.RemoveHook(WindowProc);
        _source = null;
    }

    private IntPtr WindowProc(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == NativeMethods.WmHotkey && wParam.ToInt32() == HotkeyId)
        {
            Pressed?.Invoke(this, EventArgs.Empty);
            handled = true;
        }

        return IntPtr.Zero;
    }

    private static uint ToNativeModifiers(ModifierKeys modifiers)
    {
        uint result = 0;
        if (modifiers.HasFlag(ModifierKeys.Alt)) result |= NativeMethods.ModAlt;
        if (modifiers.HasFlag(ModifierKeys.Control)) result |= NativeMethods.ModControl;
        if (modifiers.HasFlag(ModifierKeys.Shift)) result |= NativeMethods.ModShift;
        if (modifiers.HasFlag(ModifierKeys.Windows)) result |= NativeMethods.ModWin;
        return result;
    }
}

public sealed record HotkeyGesture(ModifierKeys Modifiers, Key Key)
{
    public static HotkeyGesture Parse(string text)
    {
        var parts = text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) throw new ArgumentException("快捷键需要修饰键和字母/数字/功能键，例如 Alt+V。");
        ModifierKeys modifiers = ModifierKeys.None;
        foreach (var part in parts[..^1])
        {
            modifiers |= part.ToUpperInvariant() switch
            {
                "ALT" => ModifierKeys.Alt,
                "CTRL" or "CONTROL" => ModifierKeys.Control,
                "SHIFT" => ModifierKeys.Shift,
                "WIN" or "WINDOWS" => ModifierKeys.Windows,
                _ => throw new ArgumentException($"不支持的修饰键：{part}")
            };
        }
        if (!Enum.TryParse<Key>(parts[^1], true, out var key) ||
            !((key >= Key.A && key <= Key.Z) || (key >= Key.D0 && key <= Key.D9) || (key >= Key.F1 && key <= Key.F24)))
            throw new ArgumentException("快捷键主键必须为 A–Z、D0–D9 或 F1–F24。");
        return new HotkeyGesture(modifiers, key);
    }

    public override string ToString() => $"{Modifiers}+{Key}";
}
