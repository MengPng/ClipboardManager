using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Button = System.Windows.Controls.Button;
using TextBox = System.Windows.Controls.TextBox;

namespace ClipboardManager.Windows;

internal static class SelectionAiUi
{
    public static void Apply(Window window)
    {
        window.Style = (Style)System.Windows.Application.Current.FindResource("DialogWindowStyle");
        window.Background = (System.Windows.Media.Brush)System.Windows.Application.Current.FindResource("WindowSurfaceBrush");
        window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        window.ShowInTaskbar = false;
    }

    public static TextBlock Label(string text, bool secondary = false)
    {
        var label = new TextBlock
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, secondary ? 4 : 12, 0, secondary ? 8 : 6),
            FontSize = secondary ? 12 : 13,
            Foreground = Brush(secondary ? "LabelSecondaryBrush" : "LabelPrimaryBrush")
        };
        return label;
    }

    public static TextBox Multiline(int height, bool readOnly = false) => new()
    {
        AcceptsReturn = true,
        TextWrapping = TextWrapping.Wrap,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        MinHeight = height,
        Height = double.NaN,
        MaxHeight = Math.Max(height, 420),
        IsReadOnly = readOnly,
        Padding = new Thickness(10, 8, 10, 8),
        VerticalContentAlignment = VerticalAlignment.Top
    };

    public static Button Button(string text, RoutedEventHandler click, bool primary = false)
    {
        var button = new Button
        {
            Content = text,
            MinHeight = 32,
            Padding = new Thickness(12, 0, 12, 0),
            Margin = new Thickness(0, 4, 8, 4),
            Template = FlatTemplate(primary)
        };
        button.Click += click;
        return button;
    }

    public static Button ToolbarButton(string text, RoutedEventHandler click)
    {
        var button = new Button
        {
            Content = text,
            Height = 26,
            MinHeight = 0,
            Padding = new Thickness(12, 0, 12, 0),
            Margin = new Thickness(0, 3, 6, 3),
            VerticalContentAlignment = VerticalAlignment.Center,
            IsTabStop = true,
            Template = CapsuleTemplate()
        };
        button.Click += click;
        return button;
    }

    private static ControlTemplate FlatTemplate(bool primary)
    {
        var fill = Brush(primary ? "LabelPrimaryBrush" : "ControlFillBrush");
        var hover = Brush(primary ? "AccentBrandStrongBrush" : "ControlFillHoverBrush");
        var pressed = Brush(primary ? "AccentBrush" : "ControlFillPressedBrush");
        var border = Brush(primary ? "LabelPrimaryBrush" : "BorderSubtleBrush");
        var focus = Brush("AccentBrandStrongBrush");
        var root = Frame(primary ? 16 : 6, fill, border);
        var content = Content();
        content.SetValue(TextBlock.ForegroundProperty, Brush(primary ? "WindowSurfaceBrush" : "LabelPrimaryBrush"));
        root.AppendChild(content);
        return Template(root, hover, pressed, focus);
    }

    private static ControlTemplate CapsuleTemplate()
    {
        var root = Frame(13, Brush("ControlFillBrush"), Brush("BorderSubtleBrush"));
        root.AppendChild(Content());
        return Template(root, Brush("ControlFillHoverBrush"), Brush("PressedOverlayBrush"), Brush("AccentBrandStrongBrush"));
    }

    private static FrameworkElementFactory Frame(double radius, System.Windows.Media.Brush fill, System.Windows.Media.Brush border)
    {
        var root = new FrameworkElementFactory(typeof(Border));
        root.Name = "Root";
        root.SetValue(Border.CornerRadiusProperty, new CornerRadius(radius));
        root.SetValue(Border.BackgroundProperty, fill);
        root.SetValue(Border.BorderBrushProperty, border);
        root.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        return root;
    }

    private static FrameworkElementFactory Content()
    {
        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetValue(FrameworkElement.MarginProperty, new TemplateBindingExtension(System.Windows.Controls.Control.PaddingProperty));
        content.SetValue(FrameworkElement.HorizontalAlignmentProperty, System.Windows.HorizontalAlignment.Center);
        content.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        return content;
    }

    private static ControlTemplate Template(
        FrameworkElementFactory root,
        System.Windows.Media.Brush hover,
        System.Windows.Media.Brush pressed,
        System.Windows.Media.Brush focus)
    {
        var border = Brush("BorderSubtleBrush");
        var template = new ControlTemplate(typeof(Button)) { VisualTree = root };
        template.Triggers.Add(State(UIElement.IsMouseOverProperty, hover, border));
        template.Triggers.Add(State(System.Windows.Controls.Primitives.ButtonBase.IsPressedProperty, pressed, border));
        template.Triggers.Add(State(UIElement.IsKeyboardFocusedProperty, hover, focus));
        return template;
    }

    private static System.Windows.Media.Brush Brush(string key) =>
        (System.Windows.Media.Brush)System.Windows.Application.Current.FindResource(key);

    private static Trigger State(DependencyProperty property, System.Windows.Media.Brush background, System.Windows.Media.Brush border)
    {
        var trigger = new Trigger { Property = property, Value = true };
        trigger.Setters.Add(new Setter(Border.BackgroundProperty, background, "Root"));
        trigger.Setters.Add(new Setter(Border.BorderBrushProperty, border, "Root"));
        return trigger;
    }
}
