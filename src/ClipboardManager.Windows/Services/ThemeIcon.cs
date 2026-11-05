using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace ClipboardManager.Windows.Services;

/// <summary>
/// Theme icons for navigation tabs. Only themes that set ThemeIconVisibility to Visible show them
/// (currently Kubai); the image sources come from DynamicResource keys, which are x:Null in the
/// standard palette, so other themes render exactly as before.
/// </summary>
public static class ThemeIcon
{
    public static readonly DependencyProperty RestProperty = DependencyProperty.RegisterAttached(
        "Rest", typeof(ImageSource), typeof(ThemeIcon), new FrameworkPropertyMetadata(null));

    public static readonly DependencyProperty HoverProperty = DependencyProperty.RegisterAttached(
        "Hover", typeof(ImageSource), typeof(ThemeIcon), new FrameworkPropertyMetadata(null));

    public static ImageSource? GetRest(DependencyObject element) => (ImageSource?)element.GetValue(RestProperty);
    public static void SetRest(DependencyObject element, ImageSource? value) => element.SetValue(RestProperty, value);
    public static ImageSource? GetHover(DependencyObject element) => (ImageSource?)element.GetValue(HoverProperty);
    public static void SetHover(DependencyObject element, ImageSource? value) => element.SetValue(HoverProperty, value);

    /// <summary>True when any bound value is true: the tab shows its hover icon while hovered or selected.</summary>
    public static IMultiValueConverter AnyTrue { get; } = new AnyTrueConverter();

    private sealed class AnyTrueConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture) =>
            values.Any(value => value is true);

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }
}
