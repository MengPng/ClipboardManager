using ClipboardManager.Domain.Models;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Brush = System.Windows.Media.Brush;
using Button = System.Windows.Controls.Button;
using Color = System.Windows.Media.Color;
using Grid = System.Windows.Controls.Grid;
using Orientation = System.Windows.Controls.Orientation;
using StackPanel = System.Windows.Controls.StackPanel;
using TextBlock = System.Windows.Controls.TextBlock;
using TextBox = System.Windows.Controls.TextBox;

namespace ClipboardManager.Windows;

internal sealed class TodoTagDialog : Window
{
    private readonly TextBox _nameBox;
    private string _color = TodoTagCatalog.Palette[0];

    public string TagName => _nameBox.Text.Trim();
    public string SelectedColor => _color;

    public TodoTagDialog(Window owner, TodoTag? existing = null)
    {
        Owner = owner;
        Topmost = owner.Topmost;
        Title = existing is null ? "自定义标签" : "编辑标签";
        if (existing is not null) _color = TodoTagCatalog.NormalizeColor(existing.Color);
        Width = 380;
        Height = 250;
        MinWidth = 320;
        MinHeight = 230;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        if (TryFindResource("DialogWindowStyle") is Style dialogStyle) Style = dialogStyle;

        var grid = new Grid { Margin = new Thickness(16) };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        grid.Children.Add(new TextBlock
        {
            Text = "标签名称",
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 8)
        });

        _nameBox = new TextBox { Margin = new Thickness(0, 0, 0, 14), Text = existing?.Name ?? string.Empty };
        _nameBox.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            Confirm();
        };
        Grid.SetRow(_nameBox, 1);
        grid.Children.Add(_nameBox);

        var colorLabel = new TextBlock
        {
            Text = "选择颜色",
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 8)
        };
        Grid.SetRow(colorLabel, 2);
        grid.Children.Add(colorLabel);

        var palette = new WrapPanel { Margin = new Thickness(0, 0, 0, 12) };
        foreach (var hex in TodoTagCatalog.Palette)
        {
            var swatch = CreateSwatch(hex);
            palette.Children.Add(swatch);
        }

        HighlightPalette(palette);
        Grid.SetRow(palette, 3);
        grid.Children.Add(palette);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right
        };
        var cancel = new Button { Content = "取消", IsCancel = true, Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(16, 5, 16, 5) };
        var ok = new Button { Content = existing is null ? "添加" : "保存", IsDefault = true, Padding = new Thickness(16, 5, 16, 5) };
        ok.Click += (_, _) => Confirm();
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);
        Grid.SetRow(buttons, 5);
        grid.Children.Add(buttons);

        Content = grid;
        Loaded += (_, _) => _nameBox.Focus();
    }

    private Button CreateSwatch(string hex)
    {
        var color = (Color)System.Windows.Media.ColorConverter.ConvertFromString(hex)!;
        var button = new Button
        {
            Width = 28,
            Height = 28,
            Margin = new Thickness(0, 0, 8, 8),
            Padding = new Thickness(0),
            Tag = hex,
            ToolTip = hex,
            Content = new Ellipse
            {
                Width = 18,
                Height = 18,
                Fill = new SolidColorBrush(color)
            }
        };
        button.Click += (_, _) =>
        {
            _color = hex;
            if (Content is Grid { Children: var children })
            {
                foreach (var child in children.OfType<WrapPanel>())
                    HighlightPalette(child);
            }
        };
        return button;
    }

    private void HighlightPalette(WrapPanel palette)
    {
        foreach (var button in palette.Children.OfType<Button>())
        {
            button.BorderBrush = string.Equals(button.Tag as string, _color, StringComparison.OrdinalIgnoreCase)
                ? (System.Windows.Media.Brush)FindResource("AccentBrush")
                : (System.Windows.Media.Brush)FindResource("BorderSubtleBrush");
            button.BorderThickness = new Thickness(2);
        }
    }

    private void Confirm()
    {
        if (string.IsNullOrWhiteSpace(_nameBox.Text))
        {
            System.Windows.MessageBox.Show(this, "请填写标签名称。", "自定义标签",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        DialogResult = true;
    }
}
