using System.Windows;

namespace ClipboardManager.Windows.Services;

/// <summary>
/// Themes are override layers: Themes/Palette.xaml is the standard look, and the "jiajia" theme
/// is a dictionary merged last so its keys win. All palette references are DynamicResource, so
/// adding or removing the layer restyles open windows without a restart.
/// </summary>
public static class ThemeService
{
    private static readonly Uri JiajiaSource = new("pack://application:,,,/Themes/Palette.Jiajia.xaml", UriKind.Absolute);
    private static ResourceDictionary? _jiajia;

    public static void Apply(string theme)
    {
        var merged = System.Windows.Application.Current.Resources.MergedDictionaries;
        if (_jiajia is not null) merged.Remove(_jiajia);
        _jiajia = null;
        if (theme != "jiajia") return;
        _jiajia = new ResourceDictionary { Source = JiajiaSource };
        merged.Add(_jiajia);
    }
}
