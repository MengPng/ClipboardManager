using System.Drawing;
using System.Windows.Forms;

namespace ClipboardManager.Windows.Services;

public sealed class TrayIconService : IDisposable
{
    private NotifyIcon? _notifyIcon;
    private ToolStripMenuItem? _pauseItem;
    private Func<bool>? _isCapturePaused;

    public void Initialize(
        Action showWindow,
        Action toggleCapture,
        Func<bool> isCapturePaused,
        Action exitApplication)
    {
        if (_notifyIcon is not null)
        {
            return;
        }

        _isCapturePaused = isCapturePaused;
        var menu = new ContextMenuStrip();
        menu.Items.Add("打开剪切板", null, (_, _) => showWindow());
        _pauseItem = new ToolStripMenuItem();
        _pauseItem.Click += (_, _) =>
        {
            toggleCapture();
            UpdatePauseText();
        };
        menu.Items.Add(_pauseItem);
        menu.Opening += (_, _) => UpdatePauseText();
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => exitApplication());

        _notifyIcon = new NotifyIcon
        {
            Text = "剪切板管理工具",
            Icon = LoadAppIcon(),
            ContextMenuStrip = menu,
            Visible = true
        };
        _notifyIcon.DoubleClick += (_, _) => showWindow();
        UpdatePauseText();
    }

    public void Dispose()
    {
        if (_notifyIcon is null)
        {
            return;
        }

        _notifyIcon.Visible = false;
        _notifyIcon.ContextMenuStrip?.Dispose();
        _notifyIcon.Dispose();
        _notifyIcon = null;
        _pauseItem = null;
    }

    private static Icon LoadAppIcon()
    {
        try
        {
            var streamInfo = System.Windows.Application.GetResourceStream(
                new Uri("pack://application:,,,/Assets/AppIcon.ico"));
            if (streamInfo?.Stream is not null)
            {
                using var stream = streamInfo.Stream;
                return new Icon(stream);
            }
        }
        catch { /* Fall back to the default system icon. */ }
        return SystemIcons.Application;
    }

    private void UpdatePauseText()
    {
        if (_pauseItem is not null)
        {
            _pauseItem.Text = _isCapturePaused?.Invoke() == true ? "继续监听" : "暂停监听";
        }
    }
}
