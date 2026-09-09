using ClipboardManager.Domain.Models;
using System.Windows;
using System.Windows.Controls;
using Button = System.Windows.Controls.Button;
using ListBox = System.Windows.Controls.ListBox;
using Orientation = System.Windows.Controls.Orientation;

namespace ClipboardManager.Windows;

public sealed class LanDeviceSelectionDialog : Window
{
    private readonly ListBox _list = new();
    public LanShareDevice? SelectedDevice => (_list.SelectedItem as LanDeviceViewModel)?.Device;

    public LanDeviceSelectionDialog(Window owner, IReadOnlyList<LanShareDevice> devices)
    {
        Owner = owner; Title = "发送到附近设备"; Width = 420; Height = 360;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        if (TryFindResource("DialogWindowStyle") is Style style) Style = style;
        _list.ItemsSource = devices.Select(x => new LanDeviceViewModel(x));
        _list.DisplayMemberPath = nameof(LanDeviceViewModel.Display);
        if (_list.Items.Count > 0) _list.SelectedIndex = 0;
        var send = new Button { Content = "发送", IsDefault = true, Padding = new Thickness(18, 7, 18, 7) };
        send.Click += (_, _) => { if (SelectedDevice is not null) DialogResult = true; };
        var cancel = new Button { Content = "取消", IsCancel = true, Padding = new Thickness(18, 7, 18, 7), Margin = new Thickness(0, 0, 8, 0) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        buttons.Children.Add(cancel); buttons.Children.Add(send);
        var root = new DockPanel { Margin = new Thickness(18) };
        DockPanel.SetDock(buttons, Dock.Bottom); root.Children.Add(buttons); root.Children.Add(_list); Content = root;
    }
}

public sealed record LanDeviceViewModel(LanShareDevice Device)
{
    public string Alias => Device.Alias;
    public string Detail => $"{Device.DeviceType ?? "设备"} · {Device.Address}:{Device.Port}";
    public string Display => $"{Alias}    {Detail}";
}
