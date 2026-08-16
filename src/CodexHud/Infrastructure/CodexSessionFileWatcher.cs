using System.IO;

namespace CodexHud.Infrastructure;

public sealed record SessionFileChange(
    IReadOnlyList<string> Paths,
    bool RequiresFullRefresh);

public sealed class CodexSessionFileWatcher : IDisposable
{
    private readonly string _sessionsRoot;
    private readonly string _codexDirectory;
    private readonly Action<SessionFileChange> _onChanged;
    private readonly List<FileSystemWatcher> _watchers = new();
    private int _disposed;

    public CodexSessionFileWatcher(
        string sessionsRoot,
        Action<SessionFileChange> onChanged,
        string? codexDirectory = null)
    {
        if (string.IsNullOrWhiteSpace(sessionsRoot))
        {
            throw new ArgumentException("A sessions root is required.", nameof(sessionsRoot));
        }

        _sessionsRoot = Path.GetFullPath(sessionsRoot);
        _codexDirectory = Path.GetFullPath(
            string.IsNullOrWhiteSpace(codexDirectory)
                ? Directory.GetParent(_sessionsRoot)?.FullName ?? _sessionsRoot
                : codexDirectory);
        _onChanged = onChanged ?? throw new ArgumentNullException(nameof(onChanged));

        CreateSessionsWatcher();
        CreateCodexRootWatcher();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        foreach (var watcher in _watchers)
        {
            watcher.Dispose();
        }

        _watchers.Clear();
    }

    private void CreateSessionsWatcher()
    {
        var watchDirectory = FindExistingDirectory(_sessionsRoot)
            ?? FindExistingDirectory(_codexDirectory);
        if (watchDirectory is null)
        {
            return;
        }

        var watchSessionsRootDirectly = Directory.Exists(_sessionsRoot);
        CreateWatcher(
            watchDirectory,
            watchSessionsRootDirectly ? "*.jsonl" : "*",
            includeSubdirectories: true,
            onChanged: (path, fullRefresh) =>
            {
                if (fullRefresh || IsSessionJsonlPath(path))
                {
                    Notify(path, fullRefresh);
                }
            });
    }

    private void CreateCodexRootWatcher()
    {
        var watchDirectory = FindExistingDirectory(_codexDirectory);
        if (watchDirectory is null)
        {
            return;
        }

        CreateWatcher(
            watchDirectory,
            "*",
            includeSubdirectories: false,
            onChanged: (path, _) =>
            {
                var fileName = Path.GetFileName(path);
                if (fileName.Equals("session_index.jsonl", StringComparison.OrdinalIgnoreCase)
                    || fileName.Equals("state_5.sqlite", StringComparison.OrdinalIgnoreCase))
                {
                    Notify(path, requiresFullRefresh: true);
                }
            });
    }

    private void CreateWatcher(
        string directory,
        string filter,
        bool includeSubdirectories,
        Action<string, bool> onChanged)
    {
        try
        {
            var watcher = new FileSystemWatcher(directory)
            {
                Filter = filter,
                IncludeSubdirectories = includeSubdirectories,
                NotifyFilter = NotifyFilters.FileName
                    | NotifyFilters.LastWrite
                    | NotifyFilters.Size
                    | NotifyFilters.CreationTime,
                EnableRaisingEvents = false
            };
            watcher.Changed += (_, e) => onChanged(e.FullPath, false);
            watcher.Created += (_, e) => onChanged(e.FullPath, true);
            watcher.Deleted += (_, e) => onChanged(e.FullPath, true);
            watcher.Renamed += (_, e) =>
            {
                onChanged(e.OldFullPath, true);
                onChanged(e.FullPath, true);
            };
            watcher.Error += (_, _) => Notify(string.Empty, requiresFullRefresh: true);
            watcher.EnableRaisingEvents = true;
            _watchers.Add(watcher);
        }
        catch (ArgumentException)
        {
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private bool IsSessionJsonlPath(string path)
    {
        return !string.IsNullOrWhiteSpace(path)
            && path.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase)
            && IsUnderRoot(path, _sessionsRoot);
    }

    private void Notify(string path, bool requiresFullRefresh)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        var paths = string.IsNullOrWhiteSpace(path)
            ? Array.Empty<string>()
            : new[] { Path.GetFullPath(path) };
        try
        {
            _onChanged(new SessionFileChange(paths, requiresFullRefresh));
        }
        catch (ObjectDisposedException)
        {
            // Shutdown can race with a final watcher notification.
        }
    }

    private static string? FindExistingDirectory(string path)
    {
        var current = Path.GetFullPath(path);
        while (!string.IsNullOrWhiteSpace(current))
        {
            if (Directory.Exists(current))
            {
                return current;
            }

            current = Directory.GetParent(current)?.FullName ?? string.Empty;
        }

        return null;
    }

    private static bool IsUnderRoot(string path, string root)
    {
        var normalizedRoot = root.TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        return Path.GetFullPath(path).StartsWith(
            normalizedRoot,
            StringComparison.OrdinalIgnoreCase);
    }
}
