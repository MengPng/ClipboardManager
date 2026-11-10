using ClipboardManager.Domain.Models;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Button = System.Windows.Controls.Button;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Orientation = System.Windows.Controls.Orientation;
using ProgressBar = System.Windows.Controls.ProgressBar;

namespace ClipboardManager.Windows;

/// <summary>What the update window can do; the app host supplies each action.</summary>
internal sealed record UpdateDialogActions(
    Func<IProgress<double>, CancellationToken, Task> InstallAsync,
    Func<Task> SkipAsync,
    Action OpenPage);

/// <summary>
/// 发现新版本时的非模态提示（ADR-0005）：版本说明 + 立即更新 / 打开下载页 / 跳过此版本 / 稍后。
/// 下载与校验期间可取消；失败只在窗口内说明原因，当前版本继续正常运行。
/// </summary>
internal sealed class UpdateDialog : Window
{
    private readonly UpdateDialogActions _actions;
    private readonly Button _install = new() { Content = "立即更新", Padding = new Thickness(16, 5, 16, 5), Margin = new Thickness(8, 0, 0, 0) };
    private readonly Button _page = new() { Content = "打开下载页", Padding = new Thickness(12, 5, 12, 5), Margin = new Thickness(8, 0, 0, 0) };
    private readonly Button _skip = new() { Content = "跳过此版本", Padding = new Thickness(12, 5, 12, 5) };
    private readonly Button _later = new() { Content = "稍后", Padding = new Thickness(12, 5, 12, 5), Margin = new Thickness(8, 0, 0, 0) };
    private readonly ProgressBar _progress = new() { Height = 6, Minimum = 0, Maximum = 1, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 12, 0, 0) };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0), Visibility = Visibility.Collapsed };
    private CancellationTokenSource? _download;

    public UpdateDialog(AppUpdateManifest manifest, Version currentVersion, bool canReplace, string? cannotReplaceReason, UpdateDialogActions actions)
    {
        _actions = actions;
        Manifest = manifest;
        Title = "发现新版本";
        Width = 470;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ShowInTaskbar = true;
        if (TryFindResource("DialogWindowStyle") is Style dialogStyle) Style = dialogStyle;

        var root = new StackPanel { Margin = new Thickness(20, 18, 20, 16) };
        root.Children.Add(new TextBlock
        {
            Text = $"v{manifest.Version.ToString(3)} · {manifest.Title}",
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
        });

        var meta = new List<string> { $"当前 v{currentVersion.ToString(3)}" };
        if (manifest.ReleasedAt is { } released) meta.Add($"发布于 {released.ToLocalTime():yyyy-MM-dd}");
        if (manifest.Executable is { } file) meta.Add($"约 {file.Size / 1024d / 1024d:0.#} MB");
        root.Children.Add(Caption(string.Join(" · ", meta), new Thickness(0, 4, 0, 10)));

        if (manifest.Notes.Count > 0)
        {
            var notes = new StackPanel();
            foreach (var note in manifest.Notes)
            {
                var line = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
                var bullet = new TextBlock { Text = "•", Margin = new Thickness(0, 0, 8, 0) };
                DockPanel.SetDock(bullet, Dock.Left);
                line.Children.Add(bullet);
                line.Children.Add(new TextBlock { Text = note, TextWrapping = TextWrapping.Wrap });
                notes.Children.Add(line);
            }
            root.Children.Add(new ScrollViewer
            {
                Content = notes,
                MaxHeight = 200,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            });
        }

        root.Children.Add(Caption(
            canReplace
                ? "更新会下载新版本、核对 SHA-256 后替换当前程序并自动重启。历史、收藏、待办和设置都保留在本机，不会上传。"
                : (cannotReplaceReason ?? "当前版本不能自动替换，请从下载页更新。") + "历史、收藏、待办和设置都保留在本机。",
            new Thickness(0, 10, 0, 0)));
        root.Children.Add(_progress);
        root.Children.Add(_status);

        var buttons = new DockPanel { Margin = new Thickness(0, 16, 0, 0), LastChildFill = false };
        DockPanel.SetDock(_skip, Dock.Left);
        buttons.Children.Add(_skip);
        var right = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        right.Children.Add(_later);
        right.Children.Add(_page);
        if (canReplace && manifest.Executable is not null) right.Children.Add(_install);
        DockPanel.SetDock(right, Dock.Right);
        buttons.Children.Add(right);
        root.Children.Add(buttons);
        Content = root;

        var primary = right.Children.Contains(_install) ? _install : _page;
        if (TryFindResource("PrimaryButtonStyle") is Style primaryStyle) primary.Style = primaryStyle;
        primary.IsDefault = true;

        _install.Click += async (_, _) => await InstallAsync();
        _page.Click += (_, _) => { _actions.OpenPage(); Close(); };
        _skip.Click += async (_, _) =>
        {
            try { await _actions.SkipAsync(); Close(); }
            catch (Exception exception) { ShowStatus("无法保存“跳过此版本”：" + exception.Message, error: true); }
        };
        _later.Click += (_, _) =>
        {
            if (_download is { } running) running.Cancel();
            else Close();
        };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { Close(); e.Handled = true; }
        };
        Closing += (_, _) => _download?.Cancel();
    }

    public AppUpdateManifest Manifest { get; }

    private async Task InstallAsync()
    {
        _download = new CancellationTokenSource();
        SetBusy(true);
        ShowStatus("正在下载新版本…");
        var progress = new Progress<double>(value =>
        {
            _progress.Value = value;
            ShowStatus(value >= 1 ? "下载完成，正在校验并替换…" : $"正在下载新版本… {value:P0}");
        });
        try
        {
            await _actions.InstallAsync(progress, _download.Token);
            ShowStatus("新版本已就绪，正在重启…");
        }
        catch (OperationCanceledException) when (_download.IsCancellationRequested)
        {
            if (IsLoaded) ShowStatus("已取消下载，当前版本保持不变。");
            SetBusy(false);
        }
        catch (Exception exception)
        {
            ShowStatus("更新失败：" + exception.Message + " 当前版本保持不变，也可以从下载页手动更新。", error: true);
            SetBusy(false);
        }
        finally
        {
            _download?.Dispose();
            _download = null;
        }
    }

    private void SetBusy(bool busy)
    {
        _install.IsEnabled = !busy;
        _page.IsEnabled = !busy;
        _skip.IsEnabled = !busy;
        _later.Content = busy ? "取消" : "稍后";
        _progress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        if (!busy) _progress.Value = 0;
    }

    private void ShowStatus(string text, bool error = false)
    {
        _status.Text = text;
        _status.Visibility = Visibility.Visible;
        _status.SetResourceReference(TextBlock.ForegroundProperty, error ? "DangerBrush" : "LabelSecondaryBrush");
    }

    private static TextBlock Caption(string text, Thickness margin)
    {
        var block = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = margin };
        block.SetResourceReference(TextBlock.ForegroundProperty, "LabelSecondaryBrush");
        block.SetResourceReference(TextBlock.FontSizeProperty, "FontSizeCaption");
        return block;
    }
}
