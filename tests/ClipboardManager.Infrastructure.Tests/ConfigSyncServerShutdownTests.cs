namespace ClipboardManager.Infrastructure.Tests;

/// <summary>
/// 退出时 Host.Dispose 在 UI 线程上同步等待 DisposeAsync。监听的停止流程若把续体排回调用线程的
/// SynchronizationContext，就会与正在等待的 UI 线程互相等待，进程挂在退出且一直占着单实例锁
/// （0.9.3 实测：再次启动只弹出“无法唤出已运行的实例”）。
/// </summary>
public sealed class ConfigSyncServerShutdownTests
{
    [Fact]
    public async Task DisposeCompletesWhileTheCallingThreadsContextIsBlocked()
    {
        var host = await ConfigSyncTestHost.CreateAsync("shutdown");
        try
        {
            await host.Server.StartAsync();
            Assert.True(host.Server.Status.IsRunning);

            var completed = false;
            var thread = new Thread(() =>
            {
                // 与 WPF 退出相同：调用线程装着一个只能由它自己执行的上下文，并同步阻塞等待。
                SynchronizationContext.SetSynchronizationContext(new NeverRunsContext());
                completed = host.Server.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(10));
            });
            thread.Start();
            Assert.True(thread.Join(TimeSpan.FromSeconds(20)), "停止监听的线程没有返回");

            Assert.True(completed, "在被阻塞的同步上下文中停止监听发生死锁");
            Assert.False(host.Server.Status.IsRunning);
        }
        finally
        {
            await host.DisposeAsync();
        }
    }

    /// <summary>模拟被同步等待阻塞的 UI 线程：投递过来的续体永远不会执行。</summary>
    private sealed class NeverRunsContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state) { }

        public override void Send(SendOrPostCallback d, object? state) { }
    }
}
