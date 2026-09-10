using System.Windows;
using System.Windows.Controls;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using WpfControl = System.Windows.Controls.Control;
using WpfPanel = System.Windows.Controls.Panel;
using WpfTextBox = System.Windows.Controls.TextBox;

namespace ClipboardManager.Windows;

public sealed class CloudLoginDialog : Window
{
    private readonly WpfTextBox _api = new();
    private readonly WpfTextBox _email = new();
    private readonly PasswordBox _password = new();
    private readonly string _bootstrapPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ClipboardManager", "secrets", "cloud-initial-admin.dpapi.txt");

    public string ApiBaseUrl => _api.Text.Trim();
    public string Email => _email.Text.Trim();
    public string Password => _password.Password;
    public bool UsedInitialCredential { get; private set; }

    public CloudLoginDialog(string apiBaseUrl)
    {
        Title = "登录云端"; Width = 410; Height = 330; ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _api.Text = string.IsNullOrWhiteSpace(apiBaseUrl) ? "https://clipboard.netkaize.com" : apiBaseUrl;
        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(new TextBlock { Text = "云端模式", FontSize = 20, FontWeight = FontWeights.SemiBold });
        panel.Children.Add(new TextBlock { Text = "仅同步个人配置和团队配置，剪切板历史仍只保存在本机。", Margin = new Thickness(0, 6, 0, 16), TextWrapping = TextWrapping.Wrap });
        AddField(panel, "服务地址（HTTPS）", _api);
        AddField(panel, "邮箱", _email);
        AddField(panel, "密码", _password);
        var actions = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        var cancel = new System.Windows.Controls.Button { Content = "取消", MinWidth = 76, Margin = new Thickness(0, 0, 8, 0), IsCancel = true };
        var login = new System.Windows.Controls.Button { Content = "登录", MinWidth = 76, IsDefault = true };
        if (File.Exists(_bootstrapPath))
        {
            var bootstrap = new System.Windows.Controls.Button { Content = "使用本机初始凭据", MinWidth = 132, Margin = new Thickness(0, 0, 8, 0) };
            bootstrap.Click += (_, _) => LoadInitialCredential();
            actions.Children.Add(bootstrap);
        }
        login.Click += (_, _) =>
        {
            if (!Uri.TryCreate(ApiBaseUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || string.IsNullOrWhiteSpace(Email) || string.IsNullOrEmpty(Password))
            { System.Windows.MessageBox.Show(this, "请输入 HTTPS 服务地址、邮箱和密码。", "无法登录", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
            DialogResult = true;
        };
        actions.Children.Add(cancel); actions.Children.Add(login); panel.Children.Add(actions); Content = panel;
    }

    public void DeleteInitialCredential()
    {
        if (!UsedInitialCredential) return;
        try { if (File.Exists(_bootstrapPath)) File.Delete(_bootstrapPath); }
        catch (IOException) { /* Credential remains protected and can be removed on the next successful login. */ }
    }

    private void LoadInitialCredential()
    {
        try
        {
            var protectedBytes = Convert.FromBase64String(File.ReadAllText(_bootstrapPath).Trim());
            var bytes = ProtectedData.Unprotect(protectedBytes, optionalEntropy: null, DataProtectionScope.CurrentUser);
            _password.Password = Encoding.UTF8.GetString(bytes);
            CryptographicOperations.ZeroMemory(bytes);
            UsedInitialCredential = true;
            System.Windows.MessageBox.Show(this, "初始凭据已载入。请输入已开通的邮箱后登录；登录成功后该凭据会自动清除。", "已载入本机凭据", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception)
        {
            System.Windows.MessageBox.Show(this, "无法读取本机初始凭据。请使用管理员提供的密码登录。", "凭据不可用", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private static void AddField(WpfPanel panel, string title, WpfControl control)
    {
        panel.Children.Add(new TextBlock { Text = title, Margin = new Thickness(0, 6, 0, 4) });
        control.MinHeight = 32; panel.Children.Add(control);
    }
}
