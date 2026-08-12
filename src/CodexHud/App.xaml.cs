using System.Windows;
using CodexHud.Infrastructure;

namespace CodexHud;

public partial class App : Application
{
    private Mutex? _instanceMutex;
    private bool _ownsInstanceMutex;
    private NamedPipeStateServer? _stateServer;
    private SessionStateStore? _stateStore;
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
        _window = new MainWindow();
        _window.SetState(_stateStore.CurrentState);
        _stateStore.StateChanged += OnStateChanged;

        _stateServer = new NamedPipeStateServer(_stateStore.Apply);
        _stateServer.Start();

        MainWindow = _window;
        _window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _stateServer?.Dispose();

        if (_ownsInstanceMutex)
        {
            _instanceMutex?.ReleaseMutex();
        }

        _instanceMutex?.Dispose();
        base.OnExit(e);
    }

    private void OnStateChanged(object? sender, StateChangedEventArgs e)
    {
        if (_window is null)
        {
            return;
        }

        _ = _window.Dispatcher.BeginInvoke(
            new Action(() => _window.SetState(e.CurrentState)));
    }
}
