using System.Windows;
using CodexHud.Domain;
using CodexHud.Infrastructure;

namespace CodexHud;

public partial class App : Application
{
    private static readonly TimeSpan SessionCatalogCleanupInterval =
        TimeSpan.FromMinutes(1);

    private Mutex? _instanceMutex;
    private bool _ownsInstanceMutex;
    private NamedPipeStateServer? _stateServer;
    private SessionStateStore? _stateStore;
    private CodexSessionCatalogProbe? _catalogProbe;
    private CancellationTokenSource? _catalogCleanupShutdown;
    private Task? _catalogCleanupTask;
    private SessionCatalogReconciliationQueue? _catalogReconciliationQueue;
    private MainWindow? _window;

    protected override void OnStartup(StartupEventArgs e)
    {
        if (e.Args.Any(argument => string.Equals(argument, "--hook", StringComparison.OrdinalIgnoreCase)))
        {
            var exitCode = HookBridge.CreateDefault()
                .RunAsync(Console.In)
                .GetAwaiter()
                .GetResult();
            Environment.ExitCode = exitCode;
            Shutdown(exitCode);
            return;
        }

        WindowsEnvironmentBootstrap.EnsureWindowsDirectoryEnvironment();
        base.OnStartup(e);

        _instanceMutex = new Mutex(false, "Local\\CodexHud");
        try
        {
            _ownsInstanceMutex = _instanceMutex.WaitOne(0);
        }
        catch (AbandonedMutexException)
        {
            _ownsInstanceMutex = true;
        }

        if (!_ownsInstanceMutex)
        {
            Shutdown(0);
            return;
        }

        _stateStore = new SessionStateStore();
        _catalogProbe = new CodexSessionCatalogProbe();

        _window = new MainWindow();
        _window.SetSessions(_stateStore.CurrentSessions);
        _stateStore.SessionsChanged += OnSessionsChanged;

        _stateServer = new NamedPipeStateServer(HandleHookObservation);
        _stateServer.Start();

        _catalogCleanupShutdown = new CancellationTokenSource();
        _catalogReconciliationQueue = new SessionCatalogReconciliationQueue(
            ReconcileSessionCatalog);
        _catalogCleanupTask = RunSessionCatalogCleanupAsync(
            _catalogCleanupShutdown.Token);

        MainWindow = _window;
        _window.Show();

        RequestSessionCatalogReconciliation();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_stateStore is not null)
        {
            _stateStore.SessionsChanged -= OnSessionsChanged;
        }

        _catalogCleanupShutdown?.Cancel();
        try
        {
            _catalogCleanupTask?.GetAwaiter().GetResult();

            _stateServer?.Dispose();
            _catalogReconciliationQueue?.Dispose();
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        finally
        {
            _catalogCleanupShutdown?.Dispose();
            _catalogCleanupShutdown = null;
            _catalogCleanupTask = null;
            _catalogReconciliationQueue = null;
        }

        _stateStore?.Dispose();

        if (_ownsInstanceMutex)
        {
            _instanceMutex?.ReleaseMutex();
        }

        _instanceMutex?.Dispose();
        base.OnExit(e);
    }

    private void OnSessionsChanged(object? sender, SessionsChangedEventArgs e)
    {
        var window = _window;
        var stateStore = _stateStore;
        if (window is null || stateStore is null)
        {
            return;
        }

        _ = window.Dispatcher.BeginInvoke(
            new Action(() => window.SetSessions(stateStore.CurrentSessions)));
    }

    private void HandleHookObservation(HookObservation observation)
    {
        if (_stateStore is null)
        {
            return;
        }

        _stateStore.Apply(observation);
        RequestSessionCatalogReconciliation();
    }

    private void RequestSessionCatalogReconciliation()
    {
        _catalogReconciliationQueue?.Request();
    }

    private void ReconcileSessionCatalog()
    {
        if (_stateStore is null
            || _catalogProbe is null
            || !_catalogProbe.TryRead(out var entries))
        {
            return;
        }

        _stateStore.ReconcileCatalog(entries, DateTimeOffset.UtcNow);
    }

    private async Task RunSessionCatalogCleanupAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(SessionCatalogCleanupInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken)
                       .ConfigureAwait(false))
            {
                RequestSessionCatalogReconciliation();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }
}
