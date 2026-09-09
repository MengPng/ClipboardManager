using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace ClipboardManager.Windows.Services;

/// <summary>
/// Gives the panel a translucent, rounded frame using the DWM system backdrop.
/// </summary>
/// <remarks>
/// Only the documented <c>DwmSetWindowAttribute</c> surface is used. The undocumented
/// <c>SetWindowCompositionAttribute</c> blur that Windows 10 needs is deliberately not
/// called: it is unsupported, has changed behaviour across builds, and the opaque
/// fallback is an acceptable appearance. Callers must therefore honour the return value
/// and paint an opaque background when it is <see langword="false"/>, because a
/// transparent composition target over a window with no backdrop renders as black.
/// </remarks>
internal static class WindowBackdrop
{
    private const int WindowCornerPreferenceAttribute = 33;
    private const int SystemBackdropTypeAttribute = 38;
    private const int CornerPreferenceRound = 2;

    /// <summary>Acrylic. Named "transient window" because it is intended for popovers and flyouts.</summary>
    private const int SystemBackdropAcrylic = 3;

    /// <summary>The backdrop and corner attributes were both introduced in Windows 11 22000/22621.</summary>
    private static bool IsSupported => Environment.OSVersion.Version.Build >= 22621;

    /// <summary>
    /// Rounds the window and requests an acrylic backdrop.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> when the backdrop is active and the window may paint
    /// translucent surfaces; otherwise <see langword="false"/>, meaning the caller must
    /// supply an opaque background.
    /// </returns>
    public static bool TryApplyAcrylic(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (!IsSupported) return false;

        try
        {
            var handle = new WindowInteropHelper(window).EnsureHandle();
            if (handle == IntPtr.Zero) return false;

            // Rounding is cosmetic, so a failure here must not veto the backdrop.
            TrySetAttribute(handle, WindowCornerPreferenceAttribute, CornerPreferenceRound);

            if (!TrySetAttribute(handle, SystemBackdropTypeAttribute, SystemBackdropAcrylic)) return false;

            // WPF fills the composition target with an opaque colour before the visual
            // tree is drawn, which would hide the backdrop entirely. This is the step
            // that actually lets it through, and it is only safe once the attribute above
            // has been accepted.
            var source = HwndSource.FromHwnd(handle);
            if (source?.CompositionTarget is null) return false;
            source.CompositionTarget.BackgroundColor = Colors.Transparent;
            return true;
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException)
        {
            // An unexpectedly old or trimmed dwmapi.dll must degrade, not crash startup.
            return false;
        }
    }

    private static bool TrySetAttribute(IntPtr handle, int attribute, int value)
    {
        var payload = value;
        return DwmSetWindowAttribute(handle, attribute, ref payload, sizeof(int)) == 0;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int valueSize);
}
