using System.Drawing;
using System.Windows.Forms;

namespace ClipboardManager.Windows.Services;

public sealed class TrayIconService : IDisposable
{
    private NotifyIcon? _notifyIcon;
    private ToolStripMenuItem? _pauseItem;
    private Func<bool>? _isCapturePaused;
    private Action? _balloonClick;

    public void Initialize(
        Action showWindow,
        Action toggleCapture,
        Func<bool> isCapturePaused,
        Action restartApplication,
        Action exitApplication,
        Action? checkForUpdates = null)
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
        if (checkForUpdates is not null) menu.Items.Add("检查更新", null, (_, _) => checkForUpdates());
        menu.Items.Add("重启", null, (_, _) => restartApplication());
        menu.Items.Add("退出", null, (_, _) => exitApplication());

        _notifyIcon = new NotifyIcon
        {
            Text = "剪切板管理工具",
            Icon = LoadAppIcon(),
            ContextMenuStrip = menu,
            Visible = true
        };
        _notifyIcon.DoubleClick += (_, _) => showWindow();
        _notifyIcon.BalloonTipClicked += (_, _) =>
        {
            var click = _balloonClick;
            _balloonClick = null;
            click?.Invoke();
        };
        _notifyIcon.BalloonTipClosed += (_, _) => _balloonClick = null;
        UpdatePauseText();
    }

    /// <summary>托盘气泡提示；托盘未初始化时静默忽略。</summary>
    public void ShowBalloon(string title, string text) => ShowBalloon(title, text, null);

    /// <summary>托盘气泡提示，点击气泡时执行 <paramref name="onClick"/>（只对这一条气泡有效）。</summary>
    public void ShowBalloon(string title, string text, Action? onClick)
    {
        if (_notifyIcon is null) return;
        _balloonClick = onClick;
        _notifyIcon.ShowBalloonTip(onClick is null ? 5000 : 10000, title, text, ToolTipIcon.Info);
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
