using System.Windows;
using System.Windows.Controls;
using ClipboardManager.Domain.Models;
using Button = System.Windows.Controls.Button;
using TextBox = System.Windows.Controls.TextBox;

namespace ClipboardManager.Windows;

/// <summary>Edits a draft; persistence errors leave the draft open for correction/retry.</summary>
internal sealed class CollectionContentDialog : Window
{
    private readonly TextBox _titleBox;
    private readonly TextBox _contentBox;

    public CollectionContentDialog(Window owner, CollectionItemType type, string title, string content,
        Func<string, string, Task> save, bool editing = false)
    {
        Owner = owner;
        Topmost = owner.Topmost;
        Title = $"{(editing ? "编辑" : "添加")}{(type == CollectionItemType.Link ? "链接" : "文本")}";
        Width = 560; Height = 430; MinWidth = 400; MinHeight = 300;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new Grid { Margin = new Thickness(18) };
        foreach (var height in new[] { GridLength.Auto, GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto })
            panel.RowDefinitions.Add(new RowDefinition { Height = height });
        panel.Children.Add(new TextBlock { Text = "标题（可选）" });
        _titleBox = new TextBox { Text = title, Padding = new Thickness(7), Margin = new Thickness(0, 6, 0, 12) };
        Grid.SetRow(_titleBox, 1); panel.Children.Add(_titleBox);
        var label = new TextBlock { Text = type == CollectionItemType.Link ? "链接地址（http / https）" : "正文（保留换行；Enter 换行）" };
        Grid.SetRow(label, 2); panel.Children.Add(label);
        _contentBox = new TextBox
        {
            Text = content, AcceptsReturn = true, AcceptsTab = true, TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(8), Margin = new Thickness(0, 6, 0, 12)
        };
        Grid.SetRow(_contentBox, 3); panel.Children.Add(_contentBox);
        var buttons = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Right };
        var cancel = new Button { Content = "取消", IsCancel = true, Padding = new Thickness(18, 6, 18, 6) };
        var submit = new Button { Content = "保存", Padding = new Thickness(18, 6, 18, 6), Margin = new Thickness(8, 0, 0, 0) };
        var saving = false;
        Closing += (_, e) => { if (saving) e.Cancel = true; };
        submit.Click += async (_, _) =>
        {
            if (saving) return;
            if (string.IsNullOrWhiteSpace(_contentBox.Text)) { System.Windows.MessageBox.Show(this, "内容不能为空。", "无法保存"); return; }
            saving = true; submit.IsEnabled = cancel.IsEnabled = false;
            try
            {
                await save(_titleBox.Text, _contentBox.Text);
                saving = false;
                DialogResult = true;
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show(this, ex.Message, "保存失败", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            finally { saving = false; submit.IsEnabled = cancel.IsEnabled = true; }
        };
        buttons.Children.Add(cancel); buttons.Children.Add(submit);
        Grid.SetRow(buttons, 4); panel.Children.Add(buttons); Content = panel;
        Loaded += (_, _) => { _contentBox.Focus(); _contentBox.CaretIndex = _contentBox.Text.Length; };
    }
}
