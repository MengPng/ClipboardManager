using ClipboardManager.Application.Services;
using ClipboardManager.Domain.Models;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Runtime.InteropServices;

namespace ClipboardManager.Windows;

public sealed class SelectionAiResultWindow : Window
{
    private readonly CancellationTokenSource _closing = new();
    private CancellationTokenSource? _request;
    private readonly TextBlock _status = SelectionAiUi.Label("正在请求模型…", true);
    private readonly System.Windows.Controls.TextBox _result = SelectionAiUi.Multiline(180, true);
    private readonly System.Windows.Controls.Button _retry, _cancel, _copy;
    private readonly SelectionAiService _service;
    private readonly SelectionAiSettings _settings;
    private readonly string _prompt;
    private bool _busy, _started, _closed;

    public SelectionAiResultWindow(string text, PromptTemplate template, SelectionAiSettings settings, SelectionAiService service)
    {
        _service = service; _settings = settings; _prompt = SelectionAiService.Expand(template, text);
        Title = $"划词 · {template.Name}"; Width = 520; MinWidth = 360; MinHeight = 180;
        SizeToContent = SizeToContent.Height;
        SelectionAiUi.Apply(this);
        var root = new DockPanel { Margin = new Thickness(16) };
        var footer = new StackPanel(); footer.Children.Add(_status);
        var actions = new WrapPanel();
        _retry = SelectionAiUi.Button("重试", async (_, _) => await SendAsync());
        _retry.Visibility = Visibility.Collapsed;
        actions.Children.Add(_retry);
        _cancel = SelectionAiUi.Button("取消请求", (_, _) => _request?.Cancel());
        actions.Children.Add(_cancel);
        _copy = SelectionAiUi.Button("复制结果", (_, _) =>
        {
            try
            {
                if (!string.IsNullOrEmpty(_result.Text)) { System.Windows.Clipboard.SetText(_result.Text); _status.Text = "结果已复制；按正常剪切板规则处理。"; }
            }
            catch { _status.Text = "剪切板暂时被占用，请稍后再试。"; }
        });
        _copy.IsEnabled = false; actions.Children.Add(_copy);
        actions.Children.Add(SelectionAiUi.Button("关闭", (_, _) => Close()));
        footer.Children.Add(actions); DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        _result.Height = double.NaN; _result.MinHeight = 64;
        System.Windows.Automation.AutomationProperties.SetName(_result, "模型结果");
        root.Children.Add(_result);
        Content = root;
        PreviewKeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape) { e.Handled = true; Close(); } };
        Loaded += async (_, _) =>
        {
            if (_started || _closed) return;
            _started = true;
            UpdateHeightLimit();
            await SendAsync();
        };
        SizeChanged += (_, _) => KeepInWorkArea();
        Closed += (_, _) => { _closed = true; _closing.Cancel(); _closing.Dispose(); };
    }

    private async Task SendAsync()
    {
        if (_busy || _closed) return;
        _busy = true; _retry.Visibility = Visibility.Collapsed; _cancel.IsEnabled = true; _copy.IsEnabled = false;
        _result.Clear(); _status.Text = "正在等待模型结果…";
        using var request = CancellationTokenSource.CreateLinkedTokenSource(_closing.Token);
        _request = request;
        try
        {
            var response = await _service.SendAsync(_settings, _prompt, request.Token);
            if (_closed || request.IsCancellationRequested) return;
            _result.Text = response.Text; _copy.IsEnabled = true;
            _status.Text = "处理完成。";
        }
        catch (OperationCanceledException) { _status.Text = "请求已取消，未自动重试。"; }
        catch (AiRequestException ex) { _status.Text = ex.Message; }
        catch (ArgumentException ex) { _status.Text = ex.Message; }
        catch { _status.Text = "处理失败，请检查配置后重试。"; }
        finally
        {
            _request = null; _busy = false; _cancel.IsEnabled = false;
            if (!_closed && string.IsNullOrEmpty(_result.Text)) _retry.Visibility = Visibility.Visible;
        }
    }

    private void UpdateHeightLimit()
    {
        var screen = System.Windows.Forms.Screen.FromHandle(new WindowInteropHelper(this).Handle);
        var dpi = VisualTreeHelper.GetDpi(this);
        MaxHeight = Math.Max(MinHeight, screen.WorkingArea.Height / dpi.DpiScaleY - 24);
        _result.MaxHeight = Math.Max(_result.MinHeight, MaxHeight - 180);
    }

    private void KeepInWorkArea()
    {
        if (!IsLoaded || _closed) return;
        UpdateHeightLimit();
        var handle = new WindowInteropHelper(this).Handle;
        var work = System.Windows.Forms.Screen.FromHandle(handle).WorkingArea;
        if (!GetWindowRect(handle, out var rectangle)) return;
        var dpi = VisualTreeHelper.GetDpi(this);
        int width = (int)Math.Ceiling(ActualWidth * dpi.DpiScaleX);
        int height = (int)Math.Ceiling(ActualHeight * dpi.DpiScaleY);
        int x = Math.Max(work.Left, Math.Min(rectangle.Left, work.Right - width));
        int y = Math.Max(work.Top, Math.Min(rectangle.Top, work.Bottom - height));
        if (x != rectangle.Left || y != rectangle.Top)
            SetWindowPos(handle, IntPtr.Zero, x, y, 0, 0, 0x0015);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowRectangle { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr window, out WindowRectangle rectangle);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int cx, int cy, uint flags);
}
