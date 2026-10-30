using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace ClipboardManager.Windows.Services;

/// <summary>
/// The collapsed-mode "desktop pet": a transparent, topmost, pet-sized window with
/// no chrome at all — just the pet floating over the desktop. It owns a DEDICATED
/// <see cref="PetCompanion"/> instance: frames are cached statically, so a second
/// surface costs nothing, and reparenting a control across windows (which broke
/// rendering in practice) is avoided by construction.
///
/// Interactions: a short click expands the main panel (<see cref="ExpandRequested"/>);
/// press-and-hold (~280ms) arms dragging, then moving the mouse repositions the window;
/// Esc hides the whole panel.
/// </summary>
public sealed class CollapsedPetWindow : Window
{
    private const int HoldDelayMs = 280;
    private const int ClickMaxMs = 400;
    private const double ClickMaxDistance = 6;

    private readonly PetCompanion _pet;
    private readonly DispatcherTimer _holdTimer;
    private System.Windows.Point _downPosition;
    private System.Windows.Point? _lastDragPosition;
    private DateTime _downAt;
    private bool _dragArmed, _dragged;

    public event Action? ExpandRequested;
    public event Action? HideRequested;

    public CollapsedPetWindow()
    {
        AllowsTransparency = true;
        WindowStyle = WindowStyle.None;
        Background = System.Windows.Media.Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Width = 56;
        Height = 40;
        _pet = new PetCompanion
        {
            IsHitTestVisible = true,
            Cursor = System.Windows.Input.Cursors.Hand,
            ToolTip = "点击展开剪切板；按住可拖动",
        };
        Content = new Grid { Width = 56, Height = 40, Children = { _pet } };
        _holdTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(HoldDelayMs) };
        _holdTimer.Tick += (_, _) => { _dragArmed = true; _holdTimer.Stop(); _pet.SetCarried(true); };
        _pet.MouseLeftButtonDown += OnPetDown;
        _pet.MouseMove += OnPetMove;
        _pet.MouseLeftButtonUp += OnPetUp;
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { HideRequested?.Invoke(); e.Handled = true; }
        };
    }

    public void SetListening(bool listening) => _pet.SetListening(listening);
    public void NotifyCopied() => _pet.NotifyCopied();

    protected override void OnClosed(EventArgs e)
    {
        _holdTimer.Stop();
        _pet.MouseLeftButtonDown -= OnPetDown;
        _pet.MouseMove -= OnPetMove;
        _pet.MouseLeftButtonUp -= OnPetUp;
        base.OnClosed(e);
    }

    private void OnPetDown(object sender, MouseButtonEventArgs e)
    {
        _downPosition = e.GetPosition(this);
        _downAt = DateTime.UtcNow;
        _dragArmed = false;
        _dragged = false;
        _lastDragPosition = null;
        _pet.CaptureMouse();
        _holdTimer.Start();
    }

    private void OnPetMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        // Manual delta drag: control-level capture keeps moves flowing even outside
        // the window, and unlike DragMove() there is no modal message pump that can
        // deadlock against the captured element.
        if (!_dragArmed || e.LeftButton != MouseButtonState.Pressed) return;
        var screen = PointToScreen(e.GetPosition(this));
        if (_lastDragPosition is { } last)
        {
            // PointToScreen yields device pixels while Left/Top are DIPs: divide by the
            // window DPI, and keep the pet inside the work area of the screen under the cursor.
            var dpi = VisualTreeHelper.GetDpi(this);
            var area = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point((int)screen.X, (int)screen.Y)).WorkingArea;
            Left = Math.Clamp(Left + (screen.X - last.X) / dpi.DpiScaleX, area.Left / dpi.DpiScaleX, area.Right / dpi.DpiScaleX - Width);
            Top = Math.Clamp(Top + (screen.Y - last.Y) / dpi.DpiScaleY, area.Top / dpi.DpiScaleY, area.Bottom / dpi.DpiScaleY - Height);
            _dragged = true;
        }
        _lastDragPosition = screen;
    }

    private void OnPetUp(object sender, MouseButtonEventArgs e)
    {
        _holdTimer.Stop();
        _pet.ReleaseMouseCapture();
        _pet.SetCarried(false);
        var wasDragged = _dragged;
        _lastDragPosition = null;
        if (wasDragged) return;
        var moved = (e.GetPosition(this) - _downPosition).Length;
        var held = (DateTime.UtcNow - _downAt).TotalMilliseconds;
        if (moved <= ClickMaxDistance && held <= ClickMaxMs) ExpandRequested?.Invoke();
    }
}
