using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using CodexHud.Domain;

namespace CodexHud.Infrastructure;

public sealed record SessionFileCandidate(
    string SessionId,
    string FullPath,
    DateTimeOffset LastWriteTimeUtc,
    long Length,
    bool ReadBlocked);

public sealed record SessionDiscoveryResult(
    IReadOnlyList<SessionFileCandidate> Candidates,
    bool IsComplete)
{
    public bool IsPartial => !IsComplete;
}

public sealed class CodexSessionFileDiscovery
{
    private static readonly Regex SessionFilePattern = new(
        @"^rollout-\d{4}-\d{2}-\d{2}T\d{2}-\d{2}-\d{2}-(?<sessionId>[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})\.jsonl$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private const int MaximumSessionMetaBytes = 64 * 1024;

    private readonly TimeSpan _activeWindow;
    private readonly int _maximumCandidates;

    public CodexSessionFileDiscovery(
        string? sessionsRoot = null,
        int activeWindowMinutes = 30,
        int maximumCandidates = 64)
    {
        if (activeWindowMinutes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(activeWindowMinutes));
        }

        if (maximumCandidates <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumCandidates));
        }

        SessionsRoot = Path.GetFullPath(
            string.IsNullOrWhiteSpace(sessionsRoot)
                ? Path.Combine(
                    CodexSessionCatalogProbe.GetDefaultCodexDirectory(),
                    "sessions")
                : sessionsRoot);
        _activeWindow = TimeSpan.FromMinutes(activeWindowMinutes);
        _maximumCandidates = maximumCandidates;
    }

    public string SessionsRoot { get; }

    public SessionDiscoveryResult Discover(DateTimeOffset? nowUtc = null)
    {
        var cutoffUtc = (nowUtc ?? DateTimeOffset.UtcNow).ToUniversalTime() - _activeWindow;
        var complete = true;
        var selected = new PriorityQueue<SessionFileCandidate, (long Ticks, string Path)>();

        if (!Directory.Exists(SessionsRoot))
        {
            return new SessionDiscoveryResult(Array.Empty<SessionFileCandidate>(), false);
        }

        var directories = new Stack<string>();
        directories.Push(SessionsRoot);

        while (directories.Count > 0)
        {
            var directory = directories.Pop();
            EnumerateFiles(
                directory,
                cutoffUtc,
                selected,
                ref complete);
            EnumerateDirectories(directory, directories, ref complete);
        }

        var candidates = selected.UnorderedItems
            .Select(item => item.Element)
            .OrderByDescending(candidate => candidate.LastWriteTimeUtc)
            .ThenBy(candidate => candidate.FullPath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return new SessionDiscoveryResult(candidates, complete);
    }

    private void EnumerateFiles(
        string directory,
        DateTimeOffset cutoffUtc,
        PriorityQueue<SessionFileCandidate, (long Ticks, string Path)> selected,
        ref bool complete)
    {
        try
        {
            foreach (var path in Directory.EnumerateFiles(
                         directory,
                         "*.jsonl",
                         SearchOption.TopDirectoryOnly))
            {
                if (!TryGetSessionId(path, out var sessionId))
                {
                    continue;
                }

                if (!TryCreateCandidate(
                        path,
                        sessionId,
                        cutoffUtc,
                        out var candidate,
                        out var metadataReadFailed))
                {
                    complete &= !metadataReadFailed;
                    continue;
                }

                AddCandidate(selected, candidate);
            }
        }
        catch (IOException)
        {
            complete = false;
        }
        catch (UnauthorizedAccessException)
        {
            complete = false;
        }
    }

    private static void EnumerateDirectories(
        string directory,
        Stack<string> directories,
        ref bool complete)
    {
        try
        {
            foreach (var child in Directory.EnumerateDirectories(
                         directory,
                         "*",
                         SearchOption.TopDirectoryOnly))
            {
                try
                {
                    var attributes = File.GetAttributes(child);
                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        continue;
                    }
                }
                catch (IOException)
                {
                    complete = false;
                    continue;
                }
                catch (UnauthorizedAccessException)
                {
                    complete = false;
                    continue;
                }

                directories.Push(child);
            }
        }
        catch (IOException)
        {
            complete = false;
        }
        catch (UnauthorizedAccessException)
        {
            complete = false;
        }
    }

    private static bool TryCreateCandidate(
        string path,
        string sessionId,
        DateTimeOffset cutoffUtc,
        out SessionFileCandidate candidate,
        out bool metadataReadFailed)
    {
        candidate = null!;
        metadataReadFailed = false;

        try
        {
            var fileInfo = new FileInfo(path);
            fileInfo.Refresh();
            if (!fileInfo.Exists)
            {
                return false;
            }

            var lastWriteTimeUtc = new DateTimeOffset(
                DateTime.SpecifyKind(fileInfo.LastWriteTimeUtc, DateTimeKind.Utc));
            if (lastWriteTimeUtc < cutoffUtc)
            {
                return false;
            }

            candidate = new SessionFileCandidate(
                sessionId,
                path,
                lastWriteTimeUtc,
                fileInfo.Length,
                IsReadBlocked(path));
            return true;
        }
        catch (IOException)
        {
            metadataReadFailed = true;
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            metadataReadFailed = true;
            return false;
        }
    }

    private void AddCandidate(
        PriorityQueue<SessionFileCandidate, (long Ticks, string Path)> selected,
        SessionFileCandidate candidate)
    {
        var priority = (
            candidate.LastWriteTimeUtc.UtcDateTime.Ticks,
            candidate.FullPath);
        if (selected.Count < _maximumCandidates)
        {
            selected.Enqueue(candidate, priority);
            return;
        }

        if (!selected.TryPeek(out _, out var oldestPriority)
            || priority.CompareTo(oldestPriority) <= 0)
        {
            return;
        }

        selected.Dequeue();
        selected.Enqueue(candidate, priority);
    }

    private static bool TryGetSessionId(string path, out string sessionId)
    {
        sessionId = string.Empty;
        var match = SessionFilePattern.Match(Path.GetFileName(path));
        if (match.Success)
        {
            sessionId = HookObservationParser.HashSessionId(
                match.Groups["sessionId"].Value);
            return !string.Equals(sessionId, "session-unknown", StringComparison.Ordinal);
        }

        return TryReadSessionMetaSessionId(path, out sessionId);
    }

    private static bool TryReadSessionMetaSessionId(string path, out string sessionId)
    {
        sessionId = string.Empty;

        try
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 4096,
                options: FileOptions.SequentialScan);
            var buffer = new byte[MaximumSessionMetaBytes];
            var read = 0;
            var hasNewline = false;
            while (read < buffer.Length)
            {
                var count = stream.Read(buffer, read, buffer.Length - read);
                if (count == 0)
                {
                    break;
                }

                read += count;
                if (Array.IndexOf(buffer, (byte)'\n', 0, read) >= 0)
                {
                    hasNewline = true;
                    break;
                }
            }

            if (read == 0 || (!hasNewline && read == buffer.Length))
            {
                return false;
            }

            var newlineIndex = Array.IndexOf(buffer, (byte)'\n', 0, read);
            var lineLength = newlineIndex >= 0 ? newlineIndex + 1 : read;
            using var document = JsonDocument.Parse(buffer.AsMemory(0, lineLength));
            var root = document.RootElement;
            if (!TryGetString(root, "type", out var rootType)
                || !string.Equals(rootType, "session_meta", StringComparison.Ordinal))
            {
                return false;
            }

            var idElement = root;
            if (root.TryGetProperty("payload", out var payload)
                && payload.ValueKind == JsonValueKind.Object)
            {
                idElement = payload;
            }

            if (!TryGetString(idElement, "id", out var rawSessionId))
            {
                return false;
            }

            sessionId = HookObservationParser.HashSessionId(rawSessionId);
            return !string.Equals(sessionId, "session-unknown", StringComparison.Ordinal);
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryGetString(
        JsonElement element,
        string propertyName,
        out string value)
    {
        value = string.Empty;
        if (!element.TryGetProperty(propertyName, out var property)
            || property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var text = property.GetString();
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        value = text;
        return true;
    }

    private static bool IsReadBlocked(string path)
    {
        try
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 1,
                options: FileOptions.SequentialScan);
            return false;
        }
        catch (IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }
}
