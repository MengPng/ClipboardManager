using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ClipboardManager.Domain.Models;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using Grid = System.Windows.Controls.Grid;
using ListBox = System.Windows.Controls.ListBox;
using Orientation = System.Windows.Controls.Orientation;
using RowDefinition = System.Windows.Controls.RowDefinition;
using StackPanel = System.Windows.Controls.StackPanel;
using TextBlock = System.Windows.Controls.TextBlock;
using TextBox = System.Windows.Controls.TextBox;

namespace ClipboardManager.Windows;

/// <summary>共享页对话框的公共外观：颜色、字体全部来自主题的 DialogWindowStyle，不写死颜色。</summary>
internal abstract class ShareDialogBase : Window
{
    protected ShareDialogBase(Window owner, string title, double width, double height)
    {
        Owner = owner;
        Topmost = owner.Topmost;
        Title = title;
        Width = width;
        Height = height;
        MinWidth = 300;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        if (TryFindResource("DialogWindowStyle") is Style dialogStyle) Style = dialogStyle;
    }

    protected static StackPanel ButtonRow(Button cancel, Button ok, params Button[] extra)
    {
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Right };
        foreach (var button in extra) buttons.Children.Add(button);
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);
        return buttons;
    }

    protected static Button CancelButton(string text = "取消") => new()
    {
        Content = text, IsCancel = true, Padding = new Thickness(16, 5, 16, 5), Margin = new Thickness(0, 0, 8, 0)
    };

    protected static Button OkButton(string text) => new()
    {
        Content = text, IsDefault = true, Padding = new Thickness(16, 5, 16, 5)
    };
}

/// <summary>输入 6 位配对码。配对码只在本机参与 HMAC 计算，不会明文发送。</summary>
internal sealed class SharePairDialog : ShareDialogBase
{
    private readonly TextBox _code;

    public string Code => _code.Text.Trim();

    public SharePairDialog(Window owner, string deviceName, string address, string fingerprintPrefix)
        : base(owner, "输入配对码", 400, 250)
    {
        ResizeMode = ResizeMode.NoResize;
        var grid = new Grid { Margin = new Thickness(16) };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        grid.Children.Add(new TextBlock
        {
            Text = $"与“{deviceName}”配对\n{address} · 证书指纹 {fingerprintPrefix}",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8), FontWeight = FontWeights.SemiBold
        });
        var hint = new TextBlock
        {
            Text = "请核对对方屏幕上显示的指纹前缀与此处一致，然后输入对方“生成配对码”得到的 6 位数字。配对码 5 分钟内有效，连续输错 5 次作废。",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10)
        };
        if (TryFindResource("MetaTextStyle") is Style meta) hint.Style = meta;
        Grid.SetRow(hint, 1);
        grid.Children.Add(hint);

        _code = new TextBox { MaxLength = 6, Padding = new Thickness(8, 6, 8, 6), FontSize = 16, Margin = new Thickness(0, 0, 0, 12) };
        System.Windows.Automation.AutomationProperties.SetName(_code, "6 位配对码");
        _code.PreviewTextInput += (_, e) => e.Handled = !e.Text.All(char.IsAsciiDigit);
        System.Windows.DataObject.AddPastingHandler(_code, (_, e) =>
        {
            if (e.DataObject.GetData(System.Windows.DataFormats.UnicodeText) is not string text || !text.Trim().All(char.IsAsciiDigit)) e.CancelCommand();
        });
        Grid.SetRow(_code, 2);
        grid.Children.Add(_code);

        var ok = OkButton("配对");
        ok.Click += (_, _) =>
        {
            if (Code.Length != PairingProtocol.CodeLength)
            {
                System.Windows.MessageBox.Show(this, "配对码是 6 位数字。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                _code.Focus();
                return;
            }
            DialogResult = true;
        };
        var buttons = ButtonRow(CancelButton(), ok);
        Grid.SetRow(buttons, 3);
        grid.Children.Add(buttons);
        Content = grid;
        Loaded += (_, _) => _code.Focus();
    }
}

/// <summary>
/// 选择要发布的配置文件夹：默认全部勾选，一次发布为同一个版本；取消勾选即只发布其余的文件夹。
/// </summary>
internal sealed class SharePublishFoldersDialog : ShareDialogBase
{
    private readonly List<(CheckBox Box, Guid Id)> _boxes = [];

    public IReadOnlyList<Guid> SelectedIds => _boxes.Where(entry => entry.Box.IsChecked == true).Select(entry => entry.Id).ToList();

    /// <summary>勾选了列表里的全部文件夹，即“发布全部个人配置”。</summary>
    public bool AllSelected => _boxes.All(entry => entry.Box.IsChecked == true);

    public SharePublishFoldersDialog(Window owner, IReadOnlyList<(Guid Id, string Name, int Count)> folders)
        : base(owner, "发布配置", 400, 400)
    {
        var grid = new Grid { Margin = new Thickness(14) };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.Children.Add(new TextBlock
        {
            Text = "选择要发布的配置文件夹（默认全部）", FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 4)
        });
        var hint = new TextBlock
        {
            Text = "所选文件夹会一起发布为同一个版本；订阅方一次得到全部、整体更新。空文件夹不会发布。",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8)
        };
        if (TryFindResource("MetaTextStyle") is Style meta) hint.Style = meta;
        Grid.SetRow(hint, 1);
        grid.Children.Add(hint);

        var list = new StackPanel();
        var everything = new CheckBox { Content = "全选", IsChecked = true, Margin = new Thickness(0, 0, 0, 6), FontWeight = FontWeights.SemiBold };
        list.Children.Add(everything);
        var ok = OkButton("发布");
        void Sync()
        {
            var checkedCount = _boxes.Count(entry => entry.Box.IsChecked == true);
            ok.IsEnabled = checkedCount > 0;
            everything.IsChecked = checkedCount == _boxes.Count ? true : checkedCount == 0 ? false : null;
        }
        foreach (var folder in folders)
        {
            var box = new CheckBox { Content = $"{folder.Name}（{folder.Count} 项）", IsChecked = true, Margin = new Thickness(14, 0, 0, 4) };
            box.Click += (_, _) => Sync();
            _boxes.Add((box, folder.Id));
            list.Children.Add(box);
        }
        everything.Click += (_, _) =>
        {
            var value = everything.IsChecked == true;
            foreach (var entry in _boxes) entry.Box.IsChecked = value;
            Sync();
        };
        var scroll = new ScrollViewer
        {
            Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 0, 0, 12)
        };
        Grid.SetRow(scroll, 2);
        grid.Children.Add(scroll);

        ok.Click += (_, _) => { if (_boxes.Any(entry => entry.Box.IsChecked == true)) DialogResult = true; };
        var buttons = ButtonRow(CancelButton(), ok);
        Grid.SetRow(buttons, 3);
        grid.Children.Add(buttons);
        Content = grid;
        Sync();
    }
}

/// <summary>从一组文本行里选一项（选择要订阅的发布）。</summary>
internal sealed class ShareListDialog : ShareDialogBase
{
    private readonly ListBox _list;

    public int SelectedIndex => _list.SelectedIndex;

    public ShareListDialog(Window owner, string title, string prompt, IReadOnlyList<string> lines, string okText)
        : base(owner, title, 380, 320)
    {
        var grid = new Grid { Margin = new Thickness(14) };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.Children.Add(new TextBlock
        {
            Text = prompt, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8)
        });
        _list = new ListBox { ItemsSource = lines, Margin = new Thickness(0, 0, 0, 12) };
        if (lines.Count > 0) _list.SelectedIndex = 0;
        _list.MouseDoubleClick += (_, _) => { if (_list.SelectedIndex >= 0) DialogResult = true; };
        Grid.SetRow(_list, 1);
        grid.Children.Add(_list);
        var ok = OkButton(okText);
        ok.Click += (_, _) => { if (_list.SelectedIndex >= 0) DialogResult = true; };
        var buttons = ButtonRow(CancelButton(), ok);
        Grid.SetRow(buttons, 2);
        grid.Children.Add(buttons);
        Content = grid;
        Loaded += (_, _) => _list.Focus();
    }
}

/// <summary>
/// 订阅预览、更新摘要与危险操作确认共用：正文说明后果，若包含可执行类文件，
/// 必须额外勾选“我了解风险”才能确认，导入后也不会自动打开这些文件。
/// </summary>
internal sealed class ShareConfirmDialog : ShareDialogBase
{
    private readonly CheckBox? _risk;

    public bool RiskConfirmed => _risk?.IsChecked == true;

    public ShareConfirmDialog(Window owner, string title, string body, string okText, string? riskLabel = null)
        : base(owner, title, 420, riskLabel is null ? 360 : 420)
    {
        var grid = new Grid { Margin = new Thickness(16) };
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 0, 0, 10) };
        scroll.Content = new TextBlock { Text = body, TextWrapping = TextWrapping.Wrap };
        grid.Children.Add(scroll);

        var ok = OkButton(okText);
        if (riskLabel is not null)
        {
            _risk = new CheckBox { Content = riskLabel, Margin = new Thickness(0, 0, 0, 10) };
            _risk.Checked += (_, _) => ok.IsEnabled = true;
            _risk.Unchecked += (_, _) => ok.IsEnabled = false;
            ok.IsEnabled = false;
            Grid.SetRow(_risk, 1);
            grid.Children.Add(_risk);
        }
        ok.Click += (_, _) => DialogResult = true;
        var buttons = ButtonRow(CancelButton(), ok);
        Grid.SetRow(buttons, 2);
        grid.Children.Add(buttons);
        Content = grid;
    }
}

/// <summary>订阅设置：自动更新、停留当前版本、跳过最新版本；以及取消订阅入口。</summary>
internal sealed class SubscriptionSettingsDialog : ShareDialogBase
{
    private readonly CheckBox _auto;
    private readonly CheckBox _stay;
    private readonly CheckBox? _skip;
    private readonly int? _latest;
    private readonly int? _skippedVersion;

    public bool UnsubscribeRequested { get; private set; }
    public bool AutoUpdate => _auto.IsChecked == true;
    public bool StayOnCurrent => _stay.IsChecked == true;

    public int? SkippedVersion => _skip is null ? _skippedVersion : _skip.IsChecked == true ? _latest : null;

    public SubscriptionSettingsDialog(Window owner, ConfigSubscription subscription)
        : base(owner, $"订阅设置 · {subscription.Name}", 400, 330)
    {
        ResizeMode = ResizeMode.NoResize;
        _latest = subscription.LatestKnownVersion is { } latest && latest > subscription.LocalVersion ? latest : null;
        _skippedVersion = subscription.SkippedVersion;
        var grid = new Grid { Margin = new Thickness(16) };
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var options = new StackPanel();
        _auto = new CheckBox { Content = "发现新版本后自动更新（含可执行类文件的版本仍需手动确认）", IsChecked = subscription.AutoUpdate, Margin = new Thickness(0, 0, 0, 10) };
        _stay = new CheckBox { Content = $"停留在当前版本 v{subscription.LocalVersion}（不提示、不自动更新）", IsChecked = subscription.StayOnCurrent, Margin = new Thickness(0, 0, 0, 10) };
        foreach (var box in new[] { _auto, _stay })
        {
            if (box.Content is string text) box.Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, MaxWidth = 330 };
            options.Children.Add(box);
        }
        if (_latest is { } skipTarget)
        {
            _skip = new CheckBox
            {
                Content = new TextBlock { Text = $"跳过发布者的最新版本 v{skipTarget}", TextWrapping = TextWrapping.Wrap },
                IsChecked = subscription.SkippedVersion == skipTarget, Margin = new Thickness(0, 0, 0, 10)
            };
            options.Children.Add(_skip);
        }
        else if (subscription.SkippedVersion is { } previous)
        {
            options.Children.Add(new TextBlock { Text = $"当前记录的跳过版本：v{previous}（发布者出现更新的版本后会再次提示）", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) });
        }
        var note = new TextBlock
        {
            Text = "取消订阅会删除这份订阅对应的受管文件夹及其全部内容和历史快照；你自己的配置不受影响。",
            TextWrapping = TextWrapping.Wrap
        };
        if (TryFindResource("MetaTextStyle") is Style meta) note.Style = meta;
        options.Children.Add(note);
        grid.Children.Add(options);

        var unsubscribe = new Button { Content = "取消订阅…", Padding = new Thickness(16, 5, 16, 5), Margin = new Thickness(0, 0, 24, 0) };
        unsubscribe.Click += (_, _) => { UnsubscribeRequested = true; DialogResult = true; };
        var save = OkButton("保存");
        save.Click += (_, _) => DialogResult = true;
        var buttons = ButtonRow(CancelButton(), save, unsubscribe);
        Grid.SetRow(buttons, 1);
        grid.Children.Add(buttons);
        Content = grid;
    }
}
