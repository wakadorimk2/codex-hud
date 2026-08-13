using System.Windows;
using CodexHud.Infrastructure;

namespace CodexHud;

public partial class App : Application
{
    private static readonly TimeSpan SessionCatalogCleanupInterval =
        TimeSpan.FromMinutes(5);

    private Mutex? _instanceMutex;
    private bool _ownsInstanceMutex;
    private NamedPipeStateServer? _stateServer;
    private SessionStateStore? _stateStore;
    private CodexSessionCatalogProbe? _catalogProbe;
    private CancellationTokenSource? _catalogCleanupShutdown;
    private Task? _catalogCleanupTask;
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
        ReconcileSessionCatalog();

        _window = new MainWindow();
        _window.SetSessions(_stateStore.CurrentSessions);
        _stateStore.SessionsChanged += OnSessionsChanged;

        _stateServer = new NamedPipeStateServer(_stateStore.Apply);
        _stateServer.Start();

        _catalogCleanupShutdown = new CancellationTokenSource();
        _catalogCleanupTask = RunSessionCatalogCleanupAsync(
            _catalogCleanupShutdown.Token);

        MainWindow = _window;
        _window.Show();
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
        }

        _stateServer?.Dispose();
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
        if (_window is null)
        {
            return;
        }

        _ = _window.Dispatcher.BeginInvoke(
            new Action(() => _window.SetSessions(e.Sessions)));
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
                ReconcileSessionCatalog();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }
}
