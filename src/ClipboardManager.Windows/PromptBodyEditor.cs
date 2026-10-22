using System.Windows;
using System.Windows.Controls;
using Button = System.Windows.Controls.Button;
using TextBox = System.Windows.Controls.TextBox;

namespace ClipboardManager.Windows;

internal sealed class PromptBodyEditor : Window
{
    private readonly TextBox _body = SelectionAiUi.Multiline(180);

    public string Body => _body.Text;

    public PromptBodyEditor(string body)
    {
        Title = "提示词正文";
        Width = 420;
        MinWidth = 320;
        SizeToContent = SizeToContent.Height;
        MaxHeight = 620;
        ResizeMode = ResizeMode.NoResize;
        SelectionAiUi.Apply(this);

        var root = new DockPanel { Margin = new Thickness(16) };
        var actions = new StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0)
        };
        DockPanel.SetDock(actions, Dock.Bottom);
        actions.Children.Add(SelectionAiUi.Button("取消", (_, _) => { DialogResult = false; Close(); }));
        var confirm = SelectionAiUi.Button("保存正文", (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(_body.Text) || !_body.Text.Contains("{text}", StringComparison.Ordinal))
            {
                System.Windows.MessageBox.Show(this, "提示词正文必须包含 {text}。", "提示词正文",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            DialogResult = true;
            Close();
        }, true);
        confirm.IsDefault = true;
        confirm.Margin = new Thickness(8, 4, 0, 4);
        actions.Children.Add(confirm);
        root.Children.Add(actions);

        var fields = new StackPanel();
        fields.Children.Add(SelectionAiUi.Label("正文会替换 {text} 后发送给模型。窗口按文字高度展开。", true));
        _body.Text = body;
        fields.Children.Add(_body);
        root.Children.Add(fields);
        Content = root;
    }
}
