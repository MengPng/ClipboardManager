using ClipboardManager.Domain.Contracts;
using ClipboardManager.Domain.Models;
using ClipboardManager.Windows.Services;
using System.Windows;
using System.Windows.Controls;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using ComboBox = System.Windows.Controls.ComboBox;
using TextBox = System.Windows.Controls.TextBox;

namespace ClipboardManager.Windows;

public sealed class SelectionAiSettingsPanel : System.Windows.Controls.UserControl
{
    private readonly IAppSettingsStore _store;
    private readonly ICredentialVault _vault;
    private readonly CheckBox _hotkeyEnabled = new() { Content = "启用划词快捷键", Margin = new Thickness(0, 2, 0, 2) };
    private readonly CheckBox _floating = new() { Content = "选中文字后显示浮动操作条", Margin = new Thickness(0, 2, 0, 8) };
    private readonly TextBox _hotkey = new(), _url = new(), _model = new(), _name = new(), _targetUrl = new();
    private readonly PasswordBox _key = new();
    private readonly ComboBox _provider = ProviderList();
    private readonly ComboBox _action = new()
    {
        ItemsSource = new[]
        {
            new Choice(PromptActions.Prompt, "提示词"),
            new Choice(PromptActions.OpenUrl, "打开网站")
        },
        DisplayMemberPath = nameof(Choice.Label),
        SelectedValuePath = nameof(Choice.Value)
    };
    private readonly WrapPanel _templates = new() { Margin = new Thickness(0, 2, 0, 4) };
    private readonly StackPanel _promptFields = new();
    private readonly StackPanel _websiteFields = new();
    private readonly TextBlock _status = SelectionAiUi.Label("正在读取设置…", true);
    private readonly TextBlock _bodyState = SelectionAiUi.Label("尚未设置正文", true);
    private readonly List<PromptTemplate> _items = [];
    private readonly Button _save;
    private readonly Grid _pages = new();
    private readonly Button _connectionTab;
    private readonly Button _actionsTab;
    private bool _deleteKey, _saving, _loadingDraft;
    private Guid? _selectedId;

    public event EventHandler? Saved;
    public event EventHandler? Cancelled;

    public Task ReloadAsync() => LoadAsync();

    public SelectionAiSettingsPanel(IAppSettingsStore store, ICredentialVault vault)
    {
        _store = store;
        _vault = vault;
        var root = new DockPanel { Margin = new Thickness(2, 0, 2, 0) };
        var footer = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        _status.Margin = new Thickness(0, 0, 0, 4);
        footer.Children.Add(_status);
        var commands = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
        _save = SelectionAiUi.Button("保存划词设置", async (_, _) => await SaveAsync(), true);
        _save.IsEnabled = false;
        _save.Margin = new Thickness(0, 0, 8, 0);
        var cancel = SelectionAiUi.Button("取消", (_, _) => Cancelled?.Invoke(this, EventArgs.Empty));
        cancel.Margin = new Thickness(0);
        commands.Children.Add(_save);
        commands.Children.Add(cancel);
        footer.Children.Add(commands);
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);

        var tabs = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        _connectionTab = SelectionAiUi.ToolbarButton("触发与模型", (_, _) => ShowPage(0));
        _actionsTab = SelectionAiUi.ToolbarButton("提示词操作", (_, _) => ShowPage(1));
        tabs.Children.Add(_connectionTab);
        tabs.Children.Add(_actionsTab);
        DockPanel.SetDock(tabs, Dock.Top);
        root.Children.Add(tabs);

        _pages.Children.Add(Page(BuildConnection()));
        _pages.Children.Add(Page(BuildActions()));
        root.Children.Add(_pages);
        Content = root;
        ShowPage(0);
        Loaded += async (_, _) => await LoadAsync();
    }

    private static ComboBox ProviderList() => new()
    {
        ItemsSource = new[]
        {
            new Choice("DeepSeek", "DeepSeek"),
            new Choice("OpenAICompatible", "OpenAI 兼容接口"),
            new Choice("LocalGateway", "本地 AI 网关")
        },
        DisplayMemberPath = nameof(Choice.Label),
        SelectedValuePath = nameof(Choice.Value)
    };

    private static ScrollViewer Page(UIElement content) => new()
    {
        Content = content,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
    };

    private UIElement BuildConnection()
    {
        var fields = new StackPanel();
        fields.Children.Add(SelectionAiUi.Label("触发方式"));
        fields.Children.Add(_hotkeyEnabled);
        fields.Children.Add(_floating);
        Field(fields, "划词快捷键", _hotkey);
        fields.Children.Add(SelectionAiUi.Label("拖选或双击后读取。暂停监听和排除应用同样生效。", true));
        Field(fields, "模型服务", _provider);
        Field(fields, "API Base URL", _url);
        Field(fields, "模型名称", _model);
        Field(fields, "API Key", _key);
        var remove = SelectionAiUi.Button("移除已保存密钥", (_, _) =>
        {
            _deleteKey = true;
            _key.Clear();
            _status.Text = "保存后移除当前地址密钥。";
        });
        remove.Margin = new Thickness(0, 8, 0, 0);
        fields.Children.Add(remove);
        fields.Children.Add(SelectionAiUi.Label("本地网关仅接受本机地址。密钥不进入备份，留空表示保留原密钥。", true));
        _provider.SelectionChanged += (_, _) =>
        {
            if (_provider.SelectedValue as string == "DeepSeek")
            {
                _url.Text = "https://api.deepseek.com";
                _model.Text = "deepseek-flash";
            }
            else if (_provider.SelectedValue as string == "LocalGateway")
            {
                _url.Text = "http://127.0.0.1:3425/v1";
                _model.Text = "qoder/dfmodel";
            }
            _deleteKey = false;
            _key.Clear();
        };
        _url.TextChanged += (_, _) => { _deleteKey = false; _key.Clear(); };
        _key.PasswordChanged += (_, _) => { if (_key.Password.Length > 0) _deleteKey = false; };
        return fields;
    }

    private UIElement BuildActions()
    {
        var fields = new StackPanel();
        fields.Children.Add(SelectionAiUi.Label("提示词操作"));
        fields.Children.Add(_templates);
        var commands = new WrapPanel { Margin = new Thickness(0, 2, 0, 4) };
        commands.Children.Add(Command("新增提示词", () => Add(PromptActions.Prompt)));
        commands.Children.Add(Command("新增网站", () => Add(PromptActions.OpenUrl)));
        commands.Children.Add(Command("删除", DeleteSelected));
        commands.Children.Add(Command("上移", () => Move(-1)));
        commands.Children.Add(Command("下移", () => Move(1)));
        fields.Children.Add(commands);

        Field(fields, "操作名称", _name);
        Field(fields, "操作类型", _action);
        var edit = SelectionAiUi.Button("编辑提示词正文", (_, _) => EditBody(), true);
        edit.Margin = new Thickness(0, 8, 0, 0);
        _promptFields.Children.Add(edit);
        _bodyState.Margin = new Thickness(0, 4, 0, 0);
        _promptFields.Children.Add(_bodyState);
        fields.Children.Add(_promptFields);

        Field(_websiteFields, "目标网址", _targetUrl);
        _websiteFields.Children.Add(SelectionAiUi.Label("使用默认浏览器打开，不发送所选文字。", true));
        fields.Children.Add(_websiteFields);
        _name.TextChanged += (_, _) => CommitDraft();
        _targetUrl.TextChanged += (_, _) => CommitDraft();
        _action.SelectionChanged += (_, _) =>
        {
            CommitDraft();
            UpdateEditor();
        };
        return fields;
    }

    private static Button Command(string text, Action action)
    {
        var button = SelectionAiUi.Button(text, (_, _) => action());
        button.Margin = new Thickness(0, 0, 6, 6);
        return button;
    }

    private static void Field(System.Windows.Controls.Panel panel, string label, UIElement control)
    {
        panel.Children.Add(SelectionAiUi.Label(label));
        panel.Children.Add(control);
    }

    private void ShowPage(int index)
    {
        for (int i = 0; i < _pages.Children.Count; i++) _pages.Children[i].Visibility = i == index ? Visibility.Visible : Visibility.Collapsed;
        Mark(_connectionTab, index == 0);
        Mark(_actionsTab, index == 1);
    }

    private static void Mark(Button button, bool selected) => button.FontWeight = selected ? FontWeights.SemiBold : FontWeights.Normal;

    private void Add(string action)
    {
        CommitDraft();
        var item = action == PromptActions.OpenUrl
            ? new PromptTemplate(Guid.NewGuid(), "打开网站", "{text}", PromptActions.OpenUrl, "https://")
            : new PromptTemplate(Guid.NewGuid(), "新操作", "请处理以下文字：\n\n{text}");
        _items.Add(item);
        Select(item.Id);
    }

    private void DeleteSelected()
    {
        CommitDraft();
        var selected = SelectedIndex;
        if (selected < 0) return;
        _items.RemoveAt(selected);
        Select(selected < _items.Count ? _items[selected].Id : _items.LastOrDefault()?.Id);
    }

    private void CommitDraft()
    {
        if (_loadingDraft) return;
        var selected = SelectedIndex;
        if (selected < 0) return;
        var action = _action.SelectedValue as string ?? PromptActions.Prompt;
        var current = _items[selected];
        _items[selected] = current with
        {
            Name = _name.Text.Trim(),
            Action = action,
            Body = action == PromptActions.OpenUrl ? "{text}" : current.Body,
            Url = action == PromptActions.OpenUrl ? _targetUrl.Text.Trim() : null
        };
        foreach (var button in _templates.Children.OfType<Button>()
                     .Where(button => button.Tag is Guid id && id == current.Id))
            button.Content = _items[selected].Name;
    }

    private void Select(int index)
    {
        CommitDraft();
        Select(index >= 0 && index < _items.Count ? _items[index].Id : null, false);
    }

    private void Select(Guid? id) => Select(id, true);

    private void Select(Guid? id, bool commitDraft)
    {
        if (commitDraft) CommitDraft();
        _selectedId = id is Guid candidate && _items.Any(item => item.Id == candidate) ? candidate : null;
        RenderTemplates();
        _loadingDraft = true;
        try
        {
            var selected = SelectedIndex;
            if (selected < 0)
            {
                _name.Clear();
                _targetUrl.Clear();
                _action.SelectedValue = PromptActions.Prompt;
                _bodyState.Text = "尚未选择操作";
            }
            else
            {
                var item = _items[selected];
                _name.Text = item.Name;
                _action.SelectedValue = item.Action == PromptActions.OpenUrl ? PromptActions.OpenUrl : PromptActions.Prompt;
                _targetUrl.Text = item.Url ?? string.Empty;
                _bodyState.Text = item.Body.Contains("{text}", StringComparison.Ordinal) ? "正文已包含 {text}" : "正文缺少 {text}";
            }
        }
        finally
        {
            _loadingDraft = false;
        }
        UpdateEditor();
    }

    private int SelectedIndex =>
        _selectedId is Guid id ? _items.FindIndex(item => item.Id == id) : -1;

    private void RenderTemplates()
    {
        _templates.Children.Clear();
        for (int index = 0; index < _items.Count; index++)
        {
            var item = _items[index];
            var button = SelectionAiUi.ToolbarButton(item.Name, (_, _) => Select(item.Id));
            button.Tag = item.Id;
            if (item.Id == _selectedId) button.FontWeight = FontWeights.SemiBold;
            _templates.Children.Add(button);
        }
    }

    private void UpdateEditor()
    {
        bool website = (_action.SelectedValue as string) == PromptActions.OpenUrl;
        _promptFields.Visibility = website ? Visibility.Collapsed : Visibility.Visible;
        _websiteFields.Visibility = website ? Visibility.Visible : Visibility.Collapsed;
    }

    private void EditBody()
    {
        var selected = SelectedIndex;
        if (selected < 0) return;
        var editor = new PromptBodyEditor(_items[selected].Body) { Owner = Window.GetWindow(this) };
        if (editor.ShowDialog() != true) return;
        _items[selected] = _items[selected] with { Body = editor.Body, Action = PromptActions.Prompt, Url = null };
        _bodyState.Text = "正文已包含 {text}";
    }

    private void Move(int delta)
    {
        CommitDraft();
        var selected = SelectedIndex;
        int next = selected + delta;
        if (selected < 0 || next < 0 || next >= _items.Count) return;
        var selectedId = _items[selected].Id;
        (_items[selected], _items[next]) = (_items[next], _items[selected]);
        Select(selectedId);
    }

    private async Task LoadAsync()
    {
        try
        {
            var value = (await _store.LoadAsync()).SelectionAi;
            _loadingDraft = true;
            try
            {
                _provider.SelectedValue = value.Provider;
                _url.Text = value.BaseUrl;
                _model.Text = value.Model;
                _hotkey.Text = value.Hotkey;
                _hotkeyEnabled.IsChecked = value.HotkeyEnabled;
                _floating.IsChecked = value.FloatingToolbarEnabled;
                _items.Clear();
                _items.AddRange(value.Templates);
                Select(_items.Count > 0 ? _items[0].Id : null, false);
            }
            finally
            {
                _loadingDraft = false;
            }
            var hasKey = !string.IsNullOrEmpty(await _vault.ReadAsync(value.CredentialKey));
            _status.Text = hasKey ? "当前地址已有密钥，可直接保留。" : "当前地址尚未保存密钥。";
            _save.IsEnabled = true;
        }
        catch
        {
            _status.Text = "无法读取划词设置，请稍后重试。";
        }
    }

    private async Task SaveAsync()
    {
        if (_saving) return;
        _saving = true;
        _save.IsEnabled = false;
        try
        {
            CommitDraft();
            var latest = await _store.LoadAsync();
            var config = new SelectionAiSettings
            {
                HotkeyEnabled = _hotkeyEnabled.IsChecked == true,
                FloatingToolbarEnabled = _floating.IsChecked == true,
                Hotkey = _hotkey.Text.Trim(),
                Provider = (string)_provider.SelectedValue,
                BaseUrl = _url.Text.Trim().TrimEnd('/'),
                Model = _model.Text.Trim(),
                Templates = _items.ToArray()
            };
            SelectionAiSettings.Validate(config);
            if (config.HotkeyEnabled && HotkeyGesture.Parse(config.Hotkey) == HotkeyGesture.Parse(latest.GlobalHotkey))
                throw new ArgumentException("划词快捷键不能与主面板快捷键相同。");
            if (_deleteKey) await _vault.DeleteAsync(config.CredentialKey);
            else if (!string.IsNullOrWhiteSpace(_key.Password)) await _vault.WriteAsync(config.CredentialKey, _key.Password.Trim());
            await _store.SaveAsync(latest with { SelectionAi = config });
            _status.Text = "划词设置已保存。";
            Saved?.Invoke(this, EventArgs.Empty);
        }
        catch (ArgumentException ex) { _status.Text = ex.Message; }
        catch { _status.Text = "保存失败，请检查设置和凭据后重试。"; }
        finally
        {
            _saving = false;
            _save.IsEnabled = true;
        }
    }

    private sealed record Choice(string Value, string Label);
}
