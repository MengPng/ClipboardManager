using ClipboardManager.Domain.Contracts;
using ClipboardManager.Domain.Models;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using Button = System.Windows.Controls.Button;
using ComboBox = System.Windows.Controls.ComboBox;
using Control = System.Windows.Controls.Control;
using Panel = System.Windows.Controls.Panel;
using Orientation = System.Windows.Controls.Orientation;
using TextBox = System.Windows.Controls.TextBox;

namespace ClipboardManager.Windows;

public sealed class LanShareSettingsDialog : Window
{
    private readonly IAppSettingsStore _store;
    private readonly TextBox _alias = new(), _port = new();
    private readonly ComboBox _seconds = new();
    private readonly PasswordBox _pin = new();
    public string SessionPin => _pin.Password;

    public LanShareSettingsDialog(Window owner, IAppSettingsStore store, string currentPin)
    {
        _store = store; Owner = owner; Title = "局域网共享设置"; Width = 470; Height = 430;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        if (TryFindResource("DialogWindowStyle") is Style style) Style = style;
        _seconds.ItemsSource = new[] { "1 秒", "2 秒", "3 秒", "5 秒", "10 秒" };
        _pin.Password = currentPin;
        var fields = new StackPanel();
        AddHeading(fields, "设备标识");
        AddField(fields, "附近设备看到的名称", _alias);
        AddHeading(fields, "连接");
        AddField(fields, "LocalSend 端口（1024–65535）", _port);
        AddField(fields, "发现设备等待时间", _seconds);
        AddHeading(fields, "本次发送");
        fields.Children.Add(new TextBlock { Text = "发送 PIN（对方要求时填写）" });
        _pin.Margin = new Thickness(0, 5, 0, 5); _pin.Padding = new Thickness(7); _pin.MaxLength = 12; fields.Children.Add(_pin);
        fields.Children.Add(new TextBlock { Text = "PIN 只保留到本程序退出，不写入设置或日志。", FontSize = 12, Opacity = .58, TextWrapping = TextWrapping.Wrap });
        var save = new Button { Content = "保存", IsDefault = true, Padding = new Thickness(20, 7, 20, 7) };
        save.Click += async (_, _) => await SaveAsync();
        var cancel = new Button { Content = "取消", IsCancel = true, Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(20, 7, 20, 7) };
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        actions.Children.Add(cancel); actions.Children.Add(save);
        var root = new DockPanel { Margin = new Thickness(20) }; DockPanel.SetDock(actions, Dock.Bottom); root.Children.Add(actions);
        root.Children.Add(new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = fields }); Content = root;
        Loaded += async (_, _) => await LoadAsync();
    }
    private async Task LoadAsync()
    {
        var value = await _store.LoadAsync(); _alias.Text = value.LanDeviceAlias; _port.Text = value.LanPort.ToString(CultureInfo.InvariantCulture);
        _seconds.SelectedIndex = value.LanDiscoverySeconds switch { 1 => 0, 2 => 1, 3 => 2, 5 => 3, 10 => 4, _ => 1 };
    }
    private async Task SaveAsync()
    {
        try
        {
            if (!int.TryParse(_port.Text.Trim(), out var port)) throw new ArgumentException("端口必须是数字。");
            int[] values = [1, 2, 3, 5, 10]; var current = await _store.LoadAsync();
            await _store.SaveAsync(current with { LanDeviceAlias = _alias.Text.Trim(), LanPort = port, LanDiscoverySeconds = values[Math.Max(0, _seconds.SelectedIndex)] });
            DialogResult = true;
        }
        catch (Exception ex) { System.Windows.MessageBox.Show(this, ex.Message, "无法保存", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }
    private static void AddHeading(Panel panel, string text) => panel.Children.Add(new TextBlock { Text = text, FontWeight = FontWeights.SemiBold, FontSize = 16, Margin = new Thickness(0, 12, 0, 9) });
    private static void AddField(Panel panel, string label, Control field) { panel.Children.Add(new TextBlock { Text = label }); field.Margin = new Thickness(0, 5, 0, 10); field.Padding = new Thickness(7); panel.Children.Add(field); }
}
