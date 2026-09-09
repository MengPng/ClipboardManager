using System.Windows;
using System.Windows.Controls;
using ComboBox = System.Windows.Controls.ComboBox;
using Button = System.Windows.Controls.Button;
using Orientation = System.Windows.Controls.Orientation;

namespace ClipboardManager.Windows;

internal sealed class AutoBackupSetupDialog : Window
{
    private readonly ComboBox _interval = new() { MinWidth = 180 };
    public int SelectedHours => _interval.SelectedValue is int value ? value : 24;

    public AutoBackupSetupDialog(Window owner)
    {
        if (owner.IsLoaded) Owner = owner;
        Title = "自动备份"; Width = 420; Height = 245;
        WindowStartupLocation = owner.IsLoaded ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen;
        ResizeMode = ResizeMode.NoResize;
        if (TryFindResource("DialogWindowStyle") is Style style) Style = style;
        _interval.ItemsSource = new[]
        {
            new Option("每 24 小时", 24), new Option("每 48 小时", 48), new Option("每 72 小时", 72)
        };
        _interval.DisplayMemberPath = nameof(Option.Label); _interval.SelectedValuePath = nameof(Option.Hours); _interval.SelectedIndex = 0;
        var root = new StackPanel { Margin = new Thickness(22) };
        root.Children.Add(new TextBlock { Text = "是否启用自动备份？", FontSize = 18, FontWeight = FontWeights.SemiBold });
        root.Children.Add(new TextBlock { Text = "自动备份包含历史、个人配置、图片与设置。每天的新备份会替换当天上一份备份。", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 14) });
        root.Children.Add(_interval);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        var disable = new Button { Content = "暂不启用", IsCancel = true, Padding = new Thickness(14, 6, 14, 6), Margin = new Thickness(0, 0, 8, 0) };
        var enable = new Button { Content = "启用自动备份", IsDefault = true, Padding = new Thickness(14, 6, 14, 6) };
        enable.Click += (_, _) => DialogResult = true;
        buttons.Children.Add(disable); buttons.Children.Add(enable); root.Children.Add(buttons); Content = root;
    }

    private sealed record Option(string Label, int Hours);
}
