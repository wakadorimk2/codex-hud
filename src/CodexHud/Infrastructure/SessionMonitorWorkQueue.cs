namespace CodexHud.Infrastructure;

public sealed class SessionMonitorWorkQueue : IDisposable
{
    private readonly Action _work;
    private readonly SemaphoreSlim _signal = new(0, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _worker;
    private int _disposed;

    public SessionMonitorWorkQueue(Action work)
    {
        _work = work ?? throw new ArgumentNullException(nameof(work));
        _worker = Task.Run(RunAsync);
    }

    public void Request()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        try
        {
            _signal.Release();
        }
        catch (SemaphoreFullException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _shutdown.Cancel();
        try
        {
            _worker.GetAwaiter().GetResult();
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
        }
        finally
        {
            _signal.Dispose();
            _shutdown.Dispose();
        }
    }

    private async Task RunAsync()
    {
        try
        {
            while (true)
            {
                await _signal.WaitAsync(_shutdown.Token).ConfigureAwait(false);
                _work();
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
        }
    }
}
