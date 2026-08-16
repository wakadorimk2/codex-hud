using System.IO;

namespace CodexHud.Infrastructure;

public sealed class CodexSessionFileWatcher : IDisposable
{
    private readonly Action _onChanged;
    private readonly FileSystemWatcher? _watcher;
    private int _disposed;

    public CodexSessionFileWatcher(string sessionsRoot, Action onChanged)
    {
        if (string.IsNullOrWhiteSpace(sessionsRoot))
        {
            throw new ArgumentException("A sessions root is required.", nameof(sessionsRoot));
        }

        _onChanged = onChanged ?? throw new ArgumentNullException(nameof(onChanged));
        var watchDirectory = FindWatchDirectory(sessionsRoot, out var watchJsonlOnly);
        if (watchDirectory is null)
        {
            return;
        }

        try
        {
            _watcher = new FileSystemWatcher(watchDirectory)
            {
                Filter = watchJsonlOnly ? "*.jsonl" : "*",
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName
                    | NotifyFilters.LastWrite
                    | NotifyFilters.Size
                    | NotifyFilters.CreationTime,
                EnableRaisingEvents = false
            };
            _watcher.Changed += OnChanged;
            _watcher.Created += OnCreated;
            _watcher.Deleted += OnDeleted;
            _watcher.Renamed += OnRenamed;
            _watcher.Error += OnError;
            _watcher.EnableRaisingEvents = true;
        }
        catch (ArgumentException)
        {
            _watcher?.Dispose();
        }
        catch (IOException)
        {
            _watcher?.Dispose();
        }
        catch (UnauthorizedAccessException)
        {
            _watcher?.Dispose();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        if (_watcher is null)
        {
            return;
        }

        _watcher.Changed -= OnChanged;
        _watcher.Created -= OnCreated;
        _watcher.Deleted -= OnDeleted;
        _watcher.Renamed -= OnRenamed;
        _watcher.Error -= OnError;
        _watcher.Dispose();
    }

    private static string? FindWatchDirectory(
        string sessionsRoot,
        out bool watchJsonlOnly)
    {
        var fullRoot = Path.GetFullPath(sessionsRoot);
        if (Directory.Exists(fullRoot))
        {
            watchJsonlOnly = true;
            return fullRoot;
        }

        var current = Directory.GetParent(fullRoot)?.FullName;
        while (!string.IsNullOrWhiteSpace(current))
        {
            if (Directory.Exists(current))
            {
                watchJsonlOnly = false;
                return current;
            }

            current = Directory.GetParent(current)?.FullName;
        }

        watchJsonlOnly = true;
        return null;
    }

    private void OnChanged(object sender, FileSystemEventArgs e)
    {
        Notify();
    }

    private void OnCreated(object sender, FileSystemEventArgs e)
    {
        Notify();
    }

    private void OnDeleted(object sender, FileSystemEventArgs e)
    {
        Notify();
    }

    private void OnRenamed(object sender, RenamedEventArgs e)
    {
        Notify();
    }

    private void OnError(object sender, ErrorEventArgs e)
    {
        Notify();
    }

    private void Notify()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        try
        {
            _onChanged();
        }
        catch (ObjectDisposedException)
        {
            // Shutdown can race with a final watcher notification.
        }
    }
}
