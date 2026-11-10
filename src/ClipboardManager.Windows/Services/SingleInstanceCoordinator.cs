using System.IO;
using System.IO.Pipes;

namespace ClipboardManager.Windows.Services;

public sealed class SingleInstanceCoordinator(string name) : IDisposable
{
    private static readonly TimeSpan ActivationBudget = TimeSpan.FromSeconds(5);
    private const int ConnectAttemptMilliseconds = 500;
    private const int RetryPauseMilliseconds = 200;
    private const int WriteBudgetMilliseconds = 1000;

    private static readonly byte[] ActivationMessage = [1];

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
                using var client = new NamedPipeClientStream(".", _pipeName, PipeDirection.Out, PipeOptions.Asynchronous);
                client.Connect(ConnectAttemptMilliseconds);
                // A synchronous write to an unbuffered pipe blocks until the server reads. A wedged
                // primary that still owns the pipe would hang this launch forever, so bound it.
                var write = client.WriteAsync(ActivationMessage, 0, ActivationMessage.Length);
                return write.Wait(WriteBudgetMilliseconds);
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
        // Try to create the first server synchronously so activation works as soon as this returns.
        // A previous instance that is still exiting can own the pipe for a moment ("all pipe
        // instances are busy"); that must not crash startup, so the listener keeps retrying.
        NamedPipeServerStream? firstServer = null;
        try
        {
            firstServer = CreateServer();
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        var token = _listenerCancellation.Token;
        // Task.Run: no captured UI context, so reading activation requests never depends on the
        // UI thread being free.
        _listenerTask = Task.Run(() => ListenAsync(activated, firstServer, token));
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
        NamedPipeServerStream? firstServer,
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
