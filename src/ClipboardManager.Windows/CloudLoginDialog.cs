using System.Windows;
using System.Windows.Controls;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WpfControl = System.Windows.Controls.Control;
using WpfPanel = System.Windows.Controls.Panel;
using WpfTextBox = System.Windows.Controls.TextBox;

namespace ClipboardManager.Windows;

public sealed class CloudLoginDialog : Window
{
    private readonly WpfTextBox _email = new();
    private readonly PasswordBox _password = new();
    private readonly string _bootstrapPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ClipboardManager", "secrets", "cloud-initial-admin.dpapi.txt");

    public string ApiBaseUrl => "https://clipboard.netkaize.com";
    public string Email => _email.Text.Trim();
    public string Password => _password.Password;
    public bool UsedInitialCredential { get; private set; }

    public CloudLoginDialog(string apiBaseUrl)
    {
        Title = "登录云端"; Width = 430; SizeToContent = SizeToContent.Height;
        MaxHeight = SystemParameters.WorkArea.Height * 0.9; ResizeMode = ResizeMode.CanResizeWithGrip;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(new TextBlock { Text = "云端模式", FontSize = 20, FontWeight = FontWeights.SemiBold });
        panel.Children.Add(new TextBlock { Text = "仅同步个人配置和团队配置，剪切板历史仍只保存在本机。", Margin = new Thickness(0, 6, 0, 16), TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(new TextBlock { Text = "● 服务已连接：clipboard.netkaize.com", Foreground = System.Windows.Media.Brushes.SeaGreen, Margin = new Thickness(0, 0, 0, 10) });
        var loaded = TryLoadInitialCredential();
        AddField(panel, "邮箱", _email);
        AddField(panel, loaded ? "密码（已从本机安全凭据载入）" : "密码", _password);
        var actions = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        var cancel = new System.Windows.Controls.Button { Content = "取消", MinWidth = 76, Margin = new Thickness(0, 0, 8, 0), IsCancel = true };
        var login = new System.Windows.Controls.Button { Content = "登录", MinWidth = 76, IsDefault = true };
        login.Click += (_, _) =>
        {
            if (!Uri.TryCreate(ApiBaseUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || string.IsNullOrWhiteSpace(Email) || string.IsNullOrEmpty(Password))
            { System.Windows.MessageBox.Show(this, "请输入邮箱和密码。", "无法登录", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
            DialogResult = true;
        };
        actions.Children.Add(cancel); actions.Children.Add(login); panel.Children.Add(actions);
        Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }

    public void DeleteInitialCredential()
    {
        if (!UsedInitialCredential) return;
        try { if (File.Exists(_bootstrapPath)) File.Delete(_bootstrapPath); }
        catch (IOException) { /* Credential remains protected and can be removed on the next successful login. */ }
    }

    private bool TryLoadInitialCredential()
    {
        try
        {
            if (!File.Exists(_bootstrapPath)) return false;
            var protectedBytes = Convert.FromBase64String(File.ReadAllText(_bootstrapPath).Trim());
            var bytes = ProtectedData.Unprotect(protectedBytes, optionalEntropy: null, DataProtectionScope.CurrentUser);
            var plaintext = Encoding.UTF8.GetString(bytes);
            CryptographicOperations.ZeroMemory(bytes);
            if (plaintext.TrimStart().StartsWith('{'))
            {
                var credential = JsonSerializer.Deserialize<BootstrapCredential>(plaintext, new JsonSerializerOptions(JsonSerializerDefaults.Web));
                if (credential is null || string.IsNullOrWhiteSpace(credential.Email) || string.IsNullOrEmpty(credential.Password)) return false;
                _email.Text = credential.Email;
                _password.Password = credential.Password;
            }
            else _password.Password = plaintext;
            UsedInitialCredential = true;
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static void AddField(WpfPanel panel, string title, WpfControl control)
    {
        panel.Children.Add(new TextBlock { Text = title, Margin = new Thickness(0, 6, 0, 4) });
        control.MinHeight = 32; panel.Children.Add(control);
    }

    private sealed record BootstrapCredential(string Email, string Password);
}
