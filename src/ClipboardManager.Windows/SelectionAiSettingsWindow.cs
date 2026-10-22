using ClipboardManager.Domain.Contracts;
using System.Windows;

namespace ClipboardManager.Windows;

// Kept for callers outside the main shell. The primary entry now lives in the 划词 tab.
public sealed class SelectionAiSettingsWindow : Window
{
    public SelectionAiSettingsWindow(IAppSettingsStore store, ICredentialVault vault)
    {
        Title = "划词与 AI · 仅本机"; Width = 650; Height = 790; MinWidth = 480; MinHeight = 460;
        SelectionAiUi.Apply(this);
        var panel = new SelectionAiSettingsPanel(store, vault) { Margin = new Thickness(16) };
        panel.Saved += (_, _) => DialogResult = true;
        panel.Cancelled += (_, _) => Close();
        Content = panel;
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Escape) { e.Handled = true; Close(); }
        };
    }
}
