using System.Windows;

namespace ClipboardManager.Windows.Services;

/// <summary>
/// Themes are override layers: Themes/Palette.xaml is the standard look, and every other theme
/// ("jiajia", "kami") is a dictionary merged last so its keys win. All palette references are
/// DynamicResource, so swapping the layer restyles open windows without a restart.
/// </summary>
public static class ThemeService
{
    private static readonly IReadOnlyDictionary<string, Uri> Layers = new Dictionary<string, Uri>
    {
        ["jiajia"] = new("pack://application:,,,/Themes/Palette.Jiajia.xaml", UriKind.Absolute),
        ["kami"] = new("pack://application:,,,/Themes/Palette.Kami.xaml", UriKind.Absolute),
    };

    private static ResourceDictionary? _layer;

    public static void Apply(string theme)
    {
        var merged = System.Windows.Application.Current.Resources.MergedDictionaries;
        // Load the new layer before touching the old one, so a missing resource cannot leave the
        // window without a theme layer half-way through a switch.
        ResourceDictionary? next = null;
        if (Layers.TryGetValue(theme, out var source)) next = new ResourceDictionary { Source = source };
        if (_layer is not null) merged.Remove(_layer);
        _layer = next;
        if (next is not null) merged.Add(next);
    }
}
