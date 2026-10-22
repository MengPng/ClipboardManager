using ClipboardManager.Domain.Models;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using Grid = System.Windows.Controls.Grid;
using Orientation = System.Windows.Controls.Orientation;
using StackPanel = System.Windows.Controls.StackPanel;
using TextBlock = System.Windows.Controls.TextBlock;
using TextBox = System.Windows.Controls.TextBox;

namespace ClipboardManager.Windows;

internal sealed class TodoEditDialog : Window
{
    private readonly TextBox _titleBox;
    private readonly Dictionary<Guid, CheckBox> _tagBoxes = [];

    public string TodoTitle => _titleBox.Text.Trim();
    public IReadOnlyList<Guid> SelectedTagIds => _tagBoxes.Where(pair => pair.Value.IsChecked == true).Select(pair => pair.Key).ToArray();

    public TodoEditDialog(Window owner, TodoItem item, IReadOnlyList<TodoTag> tags)
    {
        Owner = owner;
        Topmost = owner.Topmost;
        Title = "编辑待办";
        Width = 400;
        Height = 300;
        MinWidth = 340;
        MinHeight = 260;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        if (TryFindResource("DialogWindowStyle") is Style dialogStyle) Style = dialogStyle;

        var grid = new Grid { Margin = new Thickness(16) };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        grid.Children.Add(new TextBlock { Text = "待办内容", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8) });
        _titleBox = new TextBox { Text = item.Title, Margin = new Thickness(0, 0, 0, 14) };
        _titleBox.KeyDown += (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; Confirm(); } };
        Grid.SetRow(_titleBox, 1);
        grid.Children.Add(_titleBox);

        var tagLabel = new TextBlock { Text = "标签", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8) };
        Grid.SetRow(tagLabel, 2);
        grid.Children.Add(tagLabel);
        var tagPanel = new WrapPanel();
        foreach (var tag in tags)
        {
            var box = new CheckBox { Content = tag.Name, IsChecked = item.TagIds.Contains(tag.Id), Margin = new Thickness(0, 0, 12, 8) };
            _tagBoxes[tag.Id] = box;
            tagPanel.Children.Add(box);
        }
        Grid.SetRow(tagPanel, 3);
        grid.Children.Add(tagPanel);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Right };
        buttons.Children.Add(new Button { Content = "取消", IsCancel = true, Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(16, 5, 16, 5) });
        var save = new Button { Content = "保存", IsDefault = true, Padding = new Thickness(16, 5, 16, 5) };
        save.Click += (_, _) => Confirm();
        buttons.Children.Add(save);
        Grid.SetRow(buttons, 4);
        grid.Children.Add(buttons);

        Content = grid;
        Loaded += (_, _) => { _titleBox.Focus(); _titleBox.SelectAll(); };
    }

    private void Confirm()
    {
        if (string.IsNullOrWhiteSpace(_titleBox.Text))
        {
            System.Windows.MessageBox.Show(this, "请填写待办内容。", "编辑待办", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        DialogResult = true;
    }
}
