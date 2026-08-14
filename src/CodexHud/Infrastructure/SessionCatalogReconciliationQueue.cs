namespace CodexHud.Infrastructure;

public sealed class SessionCatalogReconciliationQueue : IDisposable
{
    private readonly Action _reconcile;
    private readonly SemaphoreSlim _signal = new(0, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _worker;
    private int _disposed;

    public SessionCatalogReconciliationQueue(Action reconcile)
    {
        _reconcile = reconcile ?? throw new ArgumentNullException(nameof(reconcile));
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
            // A reconciliation is already queued or running.
        }
        catch (ObjectDisposedException)
        {
            // Shutdown won the race with a late request.
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
                _reconcile();
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
        }
    }
}
