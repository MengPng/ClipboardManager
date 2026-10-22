using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace ClipboardManager.Windows.Services;

/// <summary>
/// Gives the panel rounded DWM window corners.
/// </summary>
/// <remarks>
/// Only the documented <c>DwmSetWindowAttribute</c> surface is used. The undocumented
/// <c>SetWindowCompositionAttribute</c> blur that Windows 10 would need is deliberately not
/// called: it is unsupported, and its behaviour has changed across builds.
///
/// This type used to also request the acrylic system backdrop. Since 0.7.7 the shell paints
/// an opaque 信笺 surface, so the backdrop sat entirely underneath an opaque fill: it cost a
/// backdrop composition on every resize, and it forced the composition target to be made
/// transparent (which then had to be corrected for on every host that refused the attribute).
/// Both are gone; rounding is cosmetic and simply degrades to square corners on a host that
/// does not support it.
/// </remarks>
internal static class WindowBackdrop
{
    private const int WindowCornerPreferenceAttribute = 33;
    private const int CornerPreferenceRound = 2;

    /// <summary>The corner attribute was introduced in Windows 11 22000/22621.</summary>
    private static bool IsSupported => Environment.OSVersion.Version.Build >= 22621;

    /// <summary>
    /// Rounds the window corners. Best effort: an unsupported host keeps square corners.
    /// </summary>
    public static void TryApplyRoundedCorners(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (!IsSupported) return;

        try
        {
            var handle = new WindowInteropHelper(window).EnsureHandle();
            if (handle == IntPtr.Zero) return;

            _ = TrySetAttribute(handle, WindowCornerPreferenceAttribute, CornerPreferenceRound);
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException)
        {
            // An unexpectedly old or trimmed dwmapi.dll must degrade, not crash startup.
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
