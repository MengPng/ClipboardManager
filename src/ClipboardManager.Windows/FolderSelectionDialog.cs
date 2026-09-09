using System.Windows;
using Button = System.Windows.Controls.Button;
using Grid = System.Windows.Controls.Grid;
using ListBox = System.Windows.Controls.ListBox;
using Orientation = System.Windows.Controls.Orientation;
using RowDefinition = System.Windows.Controls.RowDefinition;
using StackPanel = System.Windows.Controls.StackPanel;
using TextBlock = System.Windows.Controls.TextBlock;

namespace ClipboardManager.Windows;

internal sealed class FolderSelectionDialog : Window
{
    private readonly ListBox _folderListBox;
    public FolderViewModel? SelectedFolder => _folderListBox.SelectedItem as FolderViewModel;

    public FolderSelectionDialog(Window owner, IReadOnlyList<FolderViewModel> folders, string title = "选择目标文件夹")
    {
        Owner = owner;
        Topmost = owner.Topmost;
        Title = title;
        Width = 360;
        Height = 320;
        MinWidth = 280;
        MinHeight = 240;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Background = System.Windows.Media.Brushes.White;

        var grid = new Grid { Margin = new Thickness(14) };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var prompt = new TextBlock
        {
            Text = "请选择目标文件夹：",
            FontWeight = FontWeights.SemiBold,
            FontSize = 13,
            Margin = new Thickness(0, 0, 0, 8)
        };
        grid.Children.Add(prompt);

        _folderListBox = new ListBox
        {
            ItemsSource = folders,
            DisplayMemberPath = "Name",
            Margin = new Thickness(0, 0, 0, 12),
            FontSize = 13
        };
        if (folders.Count > 0)
        {
            _folderListBox.SelectedIndex = 0;
        }

        _folderListBox.MouseDoubleClick += (_, _) =>
        {
            if (_folderListBox.SelectedItem is not null)
            {
                DialogResult = true;
            }
        };
        _folderListBox.KeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Enter && _folderListBox.SelectedItem is not null)
            {
                e.Handled = true;
                DialogResult = true;
            }
        };

        Grid.SetRow(_folderListBox, 1);
        grid.Children.Add(_folderListBox);

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
        okBtn.Click += (_, _) =>
        {
            if (_folderListBox.SelectedItem is null)
            {
                System.Windows.MessageBox.Show(this, "请先选择一个文件夹。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            DialogResult = true;
        };

        buttons.Children.Add(cancelBtn);
        buttons.Children.Add(okBtn);
        Grid.SetRow(buttons, 2);
        grid.Children.Add(buttons);

        Content = grid;
        Loaded += (_, _) => _folderListBox.Focus();
    }
}
