using System.Windows;
using System.Windows.Controls;
using ClipboardManager.Domain.Contracts;
using ClipboardManager.Domain.Models;
using Button = System.Windows.Controls.Button;
using Grid = System.Windows.Controls.Grid;
using Orientation = System.Windows.Controls.Orientation;
using RowDefinition = System.Windows.Controls.RowDefinition;
using StackPanel = System.Windows.Controls.StackPanel;
using TextBlock = System.Windows.Controls.TextBlock;

namespace ClipboardManager.Windows.Services;

/// <summary>
/// 已匹配设备发来文件时的确认窗口（ADR-0008）。置顶显示但不进任务栏；多个请求排队逐个询问；
/// 对方超时或取消时窗口自动关闭并视为拒绝。高风险文件额外提示，接收后也不会自动打开。
/// </summary>
public sealed class IncomingFilePrompt : IIncomingFilePrompt
{
    private readonly SemaphoreSlim queue = new(1, 1);

    public async Task<bool> ConfirmAsync(IncomingFileRequest request, CancellationToken cancellationToken = default)
    {
        await queue.WaitAsync(cancellationToken);
        try
        {
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher is null || dispatcher.HasShutdownStarted) return false;
            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            IncomingFileDialog? dialog = null;
            await dispatcher.InvokeAsync(() =>
            {
                dialog = new IncomingFileDialog(request);
                dialog.Closed += (_, _) => completion.TrySetResult(dialog.Accepted);
                dialog.Show();
                dialog.Activate();
            });
            await using var registration = cancellationToken.Register(() =>
                dispatcher.InvokeAsync(() => dialog?.Close()));
            return await completion.Task;
        }
        finally
        {
            queue.Release();
        }
    }

    private sealed class IncomingFileDialog : Window
    {
        public bool Accepted { get; private set; }

        public IncomingFileDialog(IncomingFileRequest request)
        {
            Title = "收到文件";
            Width = 420;
            SizeToContent = SizeToContent.Height;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Topmost = true;
            ShowInTaskbar = false;
            if (TryFindResource("DialogWindowStyle") is Style dialogStyle) Style = dialogStyle;

            var grid = new Grid { Margin = new Thickness(16) };
            for (var i = 0; i < 4; i++) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            grid.Children.Add(new TextBlock
            {
                Text = $"“{request.PeerName}”想发给你一个文件",
                FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8)
            });
            var detail = new TextBlock
            {
                Text = $"{request.FileName}\n大小：{FormatSize(request.Size)}\n保存到：{request.SaveFolder}",
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10)
            };
            Grid.SetRow(detail, 1);
            grid.Children.Add(detail);

            if (request.IsHighRisk)
            {
                var risk = new TextBlock
                {
                    Text = "这是可执行类文件，可能有害。接收后不会自动打开，请确认来源可信后再手动打开。",
                    TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10)
                };
                if (TryFindResource("DangerBrush") is System.Windows.Media.Brush danger) risk.Foreground = danger;
                Grid.SetRow(risk, 2);
                grid.Children.Add(risk);
            }

            var decline = new Button
            {
                Content = "拒绝", IsCancel = true, Padding = new Thickness(16, 5, 16, 5), Margin = new Thickness(0, 0, 8, 0)
            };
            decline.Click += (_, _) => Close();
            var accept = new Button { Content = "接收", IsDefault = !request.IsHighRisk, Padding = new Thickness(16, 5, 16, 5) };
            accept.Click += (_, _) =>
            {
                Accepted = true;
                Close();
            };
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Right };
            buttons.Children.Add(decline);
            buttons.Children.Add(accept);
            Grid.SetRow(buttons, 3);
            grid.Children.Add(buttons);
            Content = grid;
        }

        private static string FormatSize(long bytes) => bytes switch
        {
            >= 1L << 30 => $"{bytes / (double)(1L << 30):0.##} GB",
            >= 1L << 20 => $"{bytes / (double)(1L << 20):0.#} MB",
            >= 1L << 10 => $"{bytes / 1024.0:0} KB",
            _ => $"{bytes} 字节"
        };
    }
}
