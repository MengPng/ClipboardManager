using System.Windows;
using Button = System.Windows.Controls.Button;
using Grid = System.Windows.Controls.Grid;
using Orientation = System.Windows.Controls.Orientation;
using RowDefinition = System.Windows.Controls.RowDefinition;
using StackPanel = System.Windows.Controls.StackPanel;
using TextBlock = System.Windows.Controls.TextBlock;
using TextBox = System.Windows.Controls.TextBox;

namespace ClipboardManager.Windows;

internal sealed class SingleInputDialog : Window
{
    private readonly TextBox _textBox;

    public string Value => _textBox.Text.Trim();

    public SingleInputDialog(Window owner, string title, string prompt, string defaultValue = "")
    {
        Owner = owner;
        Topmost = owner.Topmost;
        Title = title;
        Width = 380;
        Height = 175;
        MinWidth = 300;
        MinHeight = 160;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        // Resolved from Application.Resources; a dialog is not in the main visual tree,
        // so the theme has to be applied rather than inherited.
        if (TryFindResource("DialogWindowStyle") is Style dialogStyle) Style = dialogStyle;

        var grid = new Grid { Margin = new Thickness(16) };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var promptBlock = new TextBlock
        {
            Text = prompt,
            FontWeight = FontWeights.SemiBold,
            FontSize = 13,
            Margin = new Thickness(0, 0, 0, 8)
        };
        grid.Children.Add(promptBlock);

        _textBox = new TextBox
        {
            Text = defaultValue,
            Padding = new Thickness(8, 6, 8, 6),
            FontSize = 13,
            Margin = new Thickness(0, 0, 0, 14)
        };
        _textBox.KeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Enter)
            {
                e.Handled = true;
                Confirm();
            }
        };
        Grid.SetRow(_textBox, 1);
        grid.Children.Add(_textBox);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right
        };
        var cancelBtn = new Button
        {
            Content = "取消",
            IsCancel = true,
            Padding = new Thickness(16, 5, 16, 5),
            Margin = new Thickness(0, 0, 8, 0)
        };
        var okBtn = new Button
        {
            Content = "确定",
            Padding = new Thickness(16, 5, 16, 5)
        };
        okBtn.Click += (_, _) => Confirm();

        buttons.Children.Add(cancelBtn);
        buttons.Children.Add(okBtn);
        Grid.SetRow(buttons, 3);
        grid.Children.Add(buttons);

        Content = grid;
        Loaded += (_, _) =>
        {
            _textBox.Focus();
            _textBox.SelectAll();
        };
    }

    private void Confirm()
    {
        if (string.IsNullOrWhiteSpace(_textBox.Text))
        {
            System.Windows.MessageBox.Show(this, "请输入有效内容。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            _textBox.Focus();
            return;
        }
        DialogResult = true;
    }
}
