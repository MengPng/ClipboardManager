using System.IO;
using System.IO.Pipes;

namespace ClipboardManager.Windows.Services;

public sealed class SingleInstanceCoordinator(string name) : IDisposable
{
    private static readonly TimeSpan ActivationBudget = TimeSpan.FromSeconds(5);
    private const int ConnectAttemptMilliseconds = 500;
    private const int RetryPauseMilliseconds = 200;

    private readonly string _pipeName = name + ".Pipe";
    private Mutex? _mutex;
    private bool _ownsMutex;
    private CancellationTokenSource? _listenerCancellation;
    private Task? _listenerTask;

    public bool TryAcquire()
    {
        _mutex = new Mutex(initiallyOwned: true, name, out var createdNew);
        _ownsMutex = createdNew;
        return createdNew;
    }

    /// <summary>
    /// Asks the running instance to show its window. Returns <c>false</c> when it never answers,
    /// which means the mutex is held by a process that cannot be brought to the foreground.
    /// </summary>
    public bool SignalExistingInstance()
    {
        // The owning instance only starts listening once its own startup finishes, so a single
        // short attempt would misreport a slow-but-healthy instance as unresponsive.
        var deadline = DateTime.UtcNow + ActivationBudget;
        while (true)
        {
            try
            {
                using var client = new NamedPipeClientStream(".", _pipeName, PipeDirection.Out);
                client.Connect(ConnectAttemptMilliseconds);
                client.WriteByte(1);
                client.Flush();
                return true;
            }
            catch (TimeoutException)
            {
                // Existing process may still be starting.
            }
            catch (IOException)
            {
                // Existing process may be shutting down, or the pipe was torn down mid-handshake.
            }

            if (DateTime.UtcNow >= deadline)
            {
                return false;
            }

            Thread.Sleep(RetryPauseMilliseconds);
        }
    }

    public void StartListening(Action activated)
    {
        if (!_ownsMutex)
        {
            throw new InvalidOperationException("Only the primary instance can listen for activation requests.");
        }

        if (_listenerTask is not null)
        {
            throw new InvalidOperationException("The activation listener has already been started.");
        }

        _listenerCancellation = new CancellationTokenSource();
        // Create the first server synchronously. Exceptions thrown before the first await in an
        // async method are otherwise stored in its Task and can leave a healthy-looking window
        // holding the mutex without any activation pipe.
        var firstServer = CreateServer();
        _listenerTask = ListenAsync(activated, firstServer, _listenerCancellation.Token);
    }

    public void Dispose()
    {
        _listenerCancellation?.Cancel();
        try
        {
            _listenerTask?.Wait(TimeSpan.FromSeconds(1));
        }
        catch (AggregateException exception) when (
            exception.InnerExceptions.All(item => item is OperationCanceledException))
        {
        }

        _listenerCancellation?.Dispose();
        if (_ownsMutex)
        {
            _mutex?.ReleaseMutex();
            _ownsMutex = false;
        }

        _mutex?.Dispose();
    }

    private async Task ListenAsync(
        Action activated,
        NamedPipeServerStream firstServer,
        CancellationToken cancellationToken)
    {
        NamedPipeServerStream? nextServer = firstServer;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await using var server = nextServer ?? CreateServer();
                nextServer = null;
                await server.WaitForConnectionAsync(cancellationToken);
                var buffer = new byte[1];
                if (await server.ReadAsync(buffer, cancellationToken) > 0)
                {
                    activated();
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (IOException)
            {
                await Task.Delay(100, cancellationToken);
            }
            catch (UnauthorizedAccessException)
            {
                await Task.Delay(100, cancellationToken);
            }
        }
    }

    private NamedPipeServerStream CreateServer() =>
        new(
            _pipeName,
            PipeDirection.In,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
}
