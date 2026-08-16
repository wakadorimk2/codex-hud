using System.Windows;
using DrawingIcon = System.Drawing.Icon;
using DrawingSystemIcons = System.Drawing.SystemIcons;
using CodexHud.Domain;
using CodexHud.Infrastructure;
using Forms = System.Windows.Forms;

namespace CodexHud;

public partial class App : System.Windows.Application
{
    private static readonly TimeSpan SessionCatalogCleanupInterval =
        TimeSpan.FromMinutes(1);

    private Mutex? _instanceMutex;
    private bool _ownsInstanceMutex;
    private NamedPipeStateServer? _stateServer;
    private SessionStateStore? _stateStore;
    private CodexSessionCatalogProbe? _catalogProbe;
    private CodexSessionFileDiscovery? _sessionFileDiscovery;
    private CodexSessionEventProbe? _sessionEventProbe;
    private SessionCatalogReconciler? _sessionCatalogReconciler;
    private CodexSessionFileWatcher? _sessionFileWatcher;
    private CancellationTokenSource? _catalogCleanupShutdown;
    private Task? _catalogCleanupTask;
    private SessionCatalogReconciliationQueue? _catalogReconciliationQueue;
    private MainWindow? _window;
    private Forms.NotifyIcon? _trayIcon;
    private DrawingIcon? _trayIconImage;
    private Forms.ContextMenuStrip? _trayMenu;
    private Forms.ToolStripMenuItem? _toggleHudMenuItem;

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
        _sessionFileDiscovery = new CodexSessionFileDiscovery();
        _sessionEventProbe = new CodexSessionEventProbe();
        _sessionCatalogReconciler = new SessionCatalogReconciler(
            _stateStore,
            _catalogProbe,
            _sessionFileDiscovery,
            _sessionEventProbe,
            RequestSessionCatalogReconciliation);

        _window = new MainWindow();
        _window.SetSessions(_stateStore.CurrentSessions);
        _stateStore.SessionsChanged += OnSessionsChanged;

        _stateServer = new NamedPipeStateServer(HandleHookObservation);
        _stateServer.Start();

        _catalogCleanupShutdown = new CancellationTokenSource();
        _catalogReconciliationQueue = new SessionCatalogReconciliationQueue(
            ReconcileSessionCatalog);
        _sessionFileWatcher = new CodexSessionFileWatcher(
            _sessionFileDiscovery.SessionsRoot,
            RequestSessionCatalogReconciliation);
        _catalogCleanupTask = RunSessionCatalogCleanupAsync(
            _catalogCleanupShutdown.Token);

        MainWindow = _window;
        _window.Show();
        InitializeTrayIcon();

        RequestSessionCatalogReconciliation();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        DisposeTrayIcon();

        if (_stateStore is not null)
        {
            _stateStore.SessionsChanged -= OnSessionsChanged;
        }

        _catalogCleanupShutdown?.Cancel();
        _sessionFileWatcher?.Dispose();
        _sessionFileWatcher = null;
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
            _sessionCatalogReconciler = null;
            _sessionEventProbe = null;
            _sessionFileDiscovery = null;
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
            new Action(() =>
            {
                var sessions = stateStore.CurrentSessions;
                window.SetSessions(sessions);
                UpdateTrayState(sessions);
            }));
    }

    private void InitializeTrayIcon()
    {
        if (_window is null || _trayIcon is not null)
        {
            return;
        }

        _toggleHudMenuItem = new Forms.ToolStripMenuItem();
        _toggleHudMenuItem.Click += OnToggleHudMenuItemClick;

        var positionEditMenuItem = new Forms.ToolStripMenuItem("位置編集モード");
        positionEditMenuItem.Click += OnPositionEditMenuItemClick;

        var exitMenuItem = new Forms.ToolStripMenuItem("終了");
        exitMenuItem.Click += OnExitMenuItemClick;

        _trayMenu = new Forms.ContextMenuStrip();
        _trayMenu.Items.Add(_toggleHudMenuItem);
        _trayMenu.Items.Add(positionEditMenuItem);
        _trayMenu.Items.Add(new Forms.ToolStripSeparator());
        _trayMenu.Items.Add(exitMenuItem);

        _trayIconImage = TryLoadApplicationIcon();
        _trayIcon = new Forms.NotifyIcon
        {
            Icon = _trayIconImage ?? DrawingSystemIcons.Application,
            Visible = true,
            ContextMenuStrip = _trayMenu
        };
        _trayIcon.DoubleClick += OnTrayIconDoubleClick;

        UpdateTrayState(_stateStore?.CurrentSessions ?? Array.Empty<SessionLampState>());
    }

    private void UpdateTrayState(IReadOnlyList<SessionLampState> sessions)
    {
        if (_trayIcon is null)
        {
            return;
        }

        var needsAttentionCount = sessions.Count(
            session => session.State == LampState.NeedsAttention);
        var runningCount = sessions.Count(
            session => session.State == LampState.Running);
        var idleCount = sessions.Count(
            session => session.State == LampState.Idle);

        var status = sessions.Count == 0
            ? "セッションなし"
            : $"要対応 {needsAttentionCount} / 実行中 {runningCount} / Idle {idleCount}";
        _trayIcon.Text = LimitTrayText($"Codex HUD: 起動中 / {status}");
        UpdateTrayMenuState();
    }

    private void UpdateTrayMenuState()
    {
        if (_window is null || _toggleHudMenuItem is null)
        {
            return;
        }

        _toggleHudMenuItem.Text = _window.IsVisible
            ? "HUDを非表示"
            : "HUDを表示";
    }

    private void ToggleHudVisibility()
    {
        var window = _window;
        if (window is null)
        {
            return;
        }

        window.ToggleVisibilityFromTray();
        UpdateTrayMenuState();
    }

    private void OnTrayIconDoubleClick(object? sender, EventArgs e)
    {
        ToggleHudVisibility();
    }

    private void OnToggleHudMenuItemClick(object? sender, EventArgs e)
    {
        ToggleHudVisibility();
    }

    private void OnPositionEditMenuItemClick(object? sender, EventArgs e)
    {
        _window?.TogglePositionEditingFromTray();
        UpdateTrayMenuState();
    }

    private void OnExitMenuItemClick(object? sender, EventArgs e)
    {
        Shutdown(0);
    }

    private void DisposeTrayIcon()
    {
        var trayIcon = _trayIcon;
        _trayIcon = null;
        if (trayIcon is null)
        {
            return;
        }

        trayIcon.DoubleClick -= OnTrayIconDoubleClick;
        trayIcon.Visible = false;
        _trayMenu?.Dispose();
        _trayMenu = null;
        trayIcon.Dispose();
        _trayIconImage?.Dispose();
        _trayIconImage = null;
        _toggleHudMenuItem = null;
    }

    private static DrawingIcon? TryLoadApplicationIcon()
    {
        var processPath = Environment.ProcessPath;
        if (processPath is null
            || processPath.Length == 0
            || !System.IO.File.Exists(processPath))
        {
            return null;
        }

        try
        {
            return DrawingIcon.ExtractAssociatedIcon(processPath);
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            return null;
        }
    }

    private static string LimitTrayText(string text)
    {
        const int maxNotifyIconTextLength = 63;
        return text.Length <= maxNotifyIconTextLength
            ? text
            : text[..maxNotifyIconTextLength];
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
        _sessionCatalogReconciler?.Reconcile();
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
