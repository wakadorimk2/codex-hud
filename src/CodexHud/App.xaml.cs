using System.Collections.Concurrent;
using System.IO;
using System.Windows;
using DrawingIcon = System.Drawing.Icon;
using DrawingSystemIcons = System.Drawing.SystemIcons;
using CodexHud.Domain;
using CodexHud.Infrastructure;
using Forms = System.Windows.Forms;

namespace CodexHud;

public partial class App : System.Windows.Application
{
    private static readonly TimeSpan FullRefreshInterval = TimeSpan.FromSeconds(3);

    private Mutex? _instanceMutex;
    private bool _ownsInstanceMutex;
    private SessionMonitorEngine? _monitorEngine;
    private CodexSessionFileDiscovery? _sessionFileDiscovery;
    private CodexSessionFileWatcher? _sessionFileWatcher;
    private SessionMonitorWorkQueue? _monitorWorkQueue;
    private CancellationTokenSource? _monitorShutdown;
    private Task? _monitorTask;
    private readonly ConcurrentDictionary<string, byte> _pendingPaths = new(
        StringComparer.OrdinalIgnoreCase);
    private int _fullRefreshRequested;
    private MainWindow? _window;
    private Forms.NotifyIcon? _trayIcon;
    private DrawingIcon? _trayIconImage;
    private Forms.ContextMenuStrip? _trayMenu;
    private Forms.ToolStripMenuItem? _toggleHudMenuItem;

    protected override void OnStartup(StartupEventArgs e)
    {
        if (e.Args.Any(argument => string.Equals(
                argument,
                "--hook",
                StringComparison.OrdinalIgnoreCase)))
        {
            Environment.Exit(0);
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

        _sessionFileDiscovery = new CodexSessionFileDiscovery();
        var codexDirectory = Directory.GetParent(_sessionFileDiscovery.SessionsRoot)?.FullName
            ?? _sessionFileDiscovery.SessionsRoot;
        _monitorEngine = new SessionMonitorEngine(
            _sessionFileDiscovery,
            activitySource: new WindowsSessionActivitySource(codexDirectory));
        _monitorEngine.SessionsChanged += OnSessionsChanged;

        _window = new MainWindow();
        _window.SetSessions(_monitorEngine.GetVisibleSessions());

        _monitorShutdown = new CancellationTokenSource();
        _monitorWorkQueue = new SessionMonitorWorkQueue(ProcessMonitorWork);
        _sessionFileWatcher = new CodexSessionFileWatcher(
            _sessionFileDiscovery.SessionsRoot,
            OnSessionFileChanged,
            codexDirectory);
        _monitorTask = RunPeriodicFullRefreshAsync(_monitorShutdown.Token);

        MainWindow = _window;
        _window.Show();
        InitializeTrayIcon();

        RequestFullRefresh();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        DisposeTrayIcon();

        if (_monitorEngine is not null)
        {
            _monitorEngine.SessionsChanged -= OnSessionsChanged;
        }

        _monitorShutdown?.Cancel();
        _sessionFileWatcher?.Dispose();
        _sessionFileWatcher = null;
        try
        {
            _monitorTask?.GetAwaiter().GetResult();
            _monitorWorkQueue?.Dispose();
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        finally
        {
            _monitorShutdown?.Dispose();
            _monitorShutdown = null;
            _monitorTask = null;
            _monitorWorkQueue = null;
            _monitorEngine?.Dispose();
            _monitorEngine = null;
            _sessionFileDiscovery = null;
            _pendingPaths.Clear();
        }

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
        if (window is null)
        {
            return;
        }

        var sessions = e.Sessions;
        _ = window.Dispatcher.BeginInvoke(
            new Action(() =>
            {
                window.SetSessions(sessions);
                UpdateTrayState(sessions);
            }));
    }

    private void OnSessionFileChanged(SessionFileChange change)
    {
        if (change.RequiresFullRefresh)
        {
            Interlocked.Exchange(ref _fullRefreshRequested, 1);
        }
        else
        {
            foreach (var path in change.Paths)
            {
                _pendingPaths[path] = 0;
            }
        }

        _monitorWorkQueue?.Request();
    }

    private void RequestFullRefresh()
    {
        Interlocked.Exchange(ref _fullRefreshRequested, 1);
        _monitorWorkQueue?.Request();
    }

    private void ProcessMonitorWork()
    {
        var engine = _monitorEngine;
        if (engine is null)
        {
            return;
        }

        var nowUtc = DateTimeOffset.UtcNow;
        if (Interlocked.Exchange(ref _fullRefreshRequested, 0) != 0)
        {
            _pendingPaths.Clear();
            engine.RefreshActiveSessions(nowUtc);
            return;
        }

        var paths = _pendingPaths.Keys.ToArray();
        foreach (var path in paths)
        {
            _pendingPaths.TryRemove(path, out _);
        }

        if (paths.Length == 0)
        {
            engine.AdvanceLifecycle(nowUtc);
        }
        else
        {
            engine.PollPaths(paths, nowUtc);
        }
    }

    private async Task RunPeriodicFullRefreshAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(FullRefreshInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken)
                       .ConfigureAwait(false))
            {
                RequestFullRefresh();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
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

        UpdateTrayState(_monitorEngine?.GetVisibleSessions()
            ?? Array.Empty<SessionLampState>());
    }

    private void UpdateTrayState(IReadOnlyList<SessionLampState> sessions)
    {
        if (_trayIcon is null)
        {
            return;
        }

        var activeCount = sessions.Count(session => session.State == LampState.Active);
        var listeningCount = sessions.Count(session => session.State == LampState.Listening);
        var idleCount = sessions.Count(session => session.State == LampState.Idle);
        var completedCount = sessions.Count(session => session.State == LampState.Completed);
        var abortedCount = sessions.Count(session => session.State == LampState.Aborted);
        var readErrorCount = sessions.Count(session => session.State == LampState.ReadError);
        var status = sessions.Count == 0
            ? "セッションなし"
            : $"Act {activeCount} / Lis {listeningCount} / Idl {idleCount} / Cmp {completedCount} / Abt {abortedCount} / Err {readErrorCount}";
        _trayIcon.Text = LimitTrayText($"Codex HUD: {status}");
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
            || !File.Exists(processPath))
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
}
