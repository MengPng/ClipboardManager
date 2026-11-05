using ClipboardManager.Windows.Interop;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace ClipboardManager.Windows.Services;

public sealed class GlobalHotkeyService(int hotkeyId = 0x434D) : IDisposable
{
    private HwndSource? _source;
    private IntPtr _windowHandle;
    private bool _registered;

    public static HotkeyGesture DefaultHotkey { get; } = new(ModifierKeys.Alt, Key.V);

    public HotkeyGesture? Current { get; private set; }

    public bool IsRegistered => _registered;

    public event EventHandler? Pressed;

    public void Register(Window window, HotkeyGesture gesture)
    {
        if (_registered)
        {
            throw new InvalidOperationException("Global hotkey is already registered.");
        }

        AttachSource(window);
        RegisterCore(gesture);
    }

    /// <summary>
    /// Swaps the registered gesture. When the new gesture cannot be registered (for example it is
    /// owned by another application) the previous gesture is registered again and the error rethrown.
    /// </summary>
    public void Rebind(Window window, HotkeyGesture gesture)
    {
        if (_registered && Current == gesture) return;
        AttachSource(window);
        var previous = _registered ? Current : null;
        Unregister();
        try
        {
            RegisterCore(gesture);
        }
        catch
        {
            if (previous is not null)
            {
                try { RegisterCore(previous); } catch (Win32Exception) { }
            }
            throw;
        }
    }

    /// <summary>Releases the key combination but keeps the message hook so a later Rebind works.</summary>
    public void Unregister()
    {
        if (!_registered) return;
        NativeMethods.UnregisterHotKey(_windowHandle, hotkeyId);
        _registered = false;
        Current = null;
    }

    public void Dispose()
    {
        Unregister();
        _source?.RemoveHook(WindowProc);
        _source = null;
    }

    private void AttachSource(Window window)
    {
        if (_source is not null) return;
        _windowHandle = new WindowInteropHelper(window).EnsureHandle();
        _source = HwndSource.FromHwnd(_windowHandle)
            ?? throw new InvalidOperationException("Cannot access WPF window message source.");
        _source.AddHook(WindowProc);
    }

    private void RegisterCore(HotkeyGesture gesture)
    {
        var modifiers = ToNativeModifiers(gesture.Modifiers) | NativeMethods.ModNoRepeat;
        var virtualKey = (uint)KeyInterop.VirtualKeyFromKey(gesture.Key);
        if (!NativeMethods.RegisterHotKey(_windowHandle, hotkeyId, modifiers, virtualKey))
        {
            var error = Marshal.GetLastWin32Error();
            throw new Win32Exception(error, $"无法注册全局快捷键 {gesture.ToDisplayString()}，可能已被其他应用占用。");
        }

        _registered = true;
        Current = gesture;
    }

    private IntPtr WindowProc(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (_registered && message == NativeMethods.WmHotkey && wParam.ToInt32() == hotkeyId)
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
        if (!TryNormalizeKey(parts[^1], out var key))
            throw new ArgumentException("快捷键主键必须为 A–Z、0–9 或 F1–F24。");
        return new HotkeyGesture(modifiers, key);
    }

    /// <summary>Builds a gesture from a key press in a recorder box; null while only modifiers are held.</summary>
    public static HotkeyGesture? FromKeyPress(ModifierKeys modifiers, Key key)
    {
        if (modifiers == ModifierKeys.None || modifiers == ModifierKeys.Shift) return null;
        if (!IsSupportedKey(key)) return null;
        return new HotkeyGesture(modifiers, key);
    }

    public static bool IsSupportedKey(Key key) =>
        (key >= Key.A && key <= Key.Z) || (key >= Key.D0 && key <= Key.D9) || (key >= Key.F1 && key <= Key.F24);

    private static bool TryNormalizeKey(string text, out Key key)
    {
        if (text.Length == 1 && char.IsAsciiDigit(text[0])) text = "D" + text;
        return Enum.TryParse(text, true, out key) && !int.TryParse(text, out _) && IsSupportedKey(key);
    }

    /// <summary>Settings form: "Ctrl+Alt+F" (stable order, parseable by <see cref="Parse"/>).</summary>
    public string ToSettingsString() => string.Join('+', ModifierNames().Append(KeyName()));

    /// <summary>Display form with "D1" shown as "1".</summary>
    public string ToDisplayString() => ToSettingsString();

    private IEnumerable<string> ModifierNames()
    {
        if (Modifiers.HasFlag(ModifierKeys.Control)) yield return "Ctrl";
        if (Modifiers.HasFlag(ModifierKeys.Alt)) yield return "Alt";
        if (Modifiers.HasFlag(ModifierKeys.Shift)) yield return "Shift";
        if (Modifiers.HasFlag(ModifierKeys.Windows)) yield return "Win";
    }

    private string KeyName() => Key is >= Key.D0 and <= Key.D9 ? ((int)(Key - Key.D0)).ToString() : Key.ToString();

    public override string ToString() => ToSettingsString();
}
