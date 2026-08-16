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
    bool ReadBlocked,
    bool IsInternal = false);

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
    private readonly int _selectionLimit;

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
                    CodexSessionCatalogPaths.GetDefaultCodexDirectory(),
                    "sessions")
                : sessionsRoot);
        _activeWindow = TimeSpan.FromMinutes(activeWindowMinutes);
        _maximumCandidates = maximumCandidates;
        _selectionLimit = Math.Max(
            maximumCandidates,
            checked(maximumCandidates * 8));
    }

    public string SessionsRoot { get; }

    public TimeSpan ActiveWindow => _activeWindow;

    public int MaximumCandidates => _maximumCandidates;

    public SessionDiscoveryResult Discover(DateTimeOffset? nowUtc = null)
    {
        var cutoffUtc = (nowUtc ?? DateTimeOffset.UtcNow).ToUniversalTime() - _activeWindow;
        var complete = true;
        var selected = new PriorityQueue<SessionFileCandidate, (long Ticks, string Path)>();

        if (!Directory.Exists(SessionsRoot))
        {
            return new SessionDiscoveryResult(
                Array.Empty<SessionFileCandidate>(),
                IsComplete: true);
        }

        var directories = new Stack<string>();
        directories.Push(SessionsRoot);

        while (directories.Count > 0)
        {
            var directory = directories.Pop();
            EnumerateFiles(directory, cutoffUtc, selected, ref complete);
            EnumerateDirectories(directory, directories, ref complete);
        }

        var candidates = selected.UnorderedItems
            .Select(item => item.Element)
            .Where(candidate => !candidate.IsInternal)
            .GroupBy(candidate => candidate.SessionId, StringComparer.Ordinal)
            .Select(group => group
                .OrderByDescending(candidate => candidate.LastWriteTimeUtc)
                .ThenBy(candidate => candidate.FullPath, StringComparer.OrdinalIgnoreCase)
                .First())
            .OrderByDescending(candidate => candidate.LastWriteTimeUtc)
            .ThenBy(candidate => candidate.FullPath, StringComparer.OrdinalIgnoreCase)
            .Take(_maximumCandidates)
            .ToArray();
        return new SessionDiscoveryResult(candidates, complete);
    }

    public bool TryCreateCandidate(
        string path,
        DateTimeOffset? nowUtc,
        bool enforceActiveWindow,
        out SessionFileCandidate candidate)
    {
        candidate = null!;
        if (string.IsNullOrWhiteSpace(path)
            || !IsPathUnderSessionsRoot(path))
        {
            return false;
        }

        var fullPath = Path.GetFullPath(path);
        if (!TryGetIdentity(fullPath, out var sessionId, out var isInternal))
        {
            return false;
        }

        try
        {
            var fileInfo = new FileInfo(fullPath);
            fileInfo.Refresh();
            if (!fileInfo.Exists
                || !fileInfo.Extension.Equals(".jsonl", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var lastWriteTimeUtc = new DateTimeOffset(
                DateTime.SpecifyKind(fileInfo.LastWriteTimeUtc, DateTimeKind.Utc));
            if (enforceActiveWindow
                && lastWriteTimeUtc < (nowUtc ?? DateTimeOffset.UtcNow).ToUniversalTime() - _activeWindow)
            {
                return false;
            }

            candidate = new SessionFileCandidate(
                sessionId,
                fullPath,
                lastWriteTimeUtc,
                fileInfo.Length,
                IsReadBlocked(fullPath),
                isInternal);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    public bool IsPathUnderSessionsRoot(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            var fullPath = Path.GetFullPath(path);
            var root = SessionsRoot.TrimEnd(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            return fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    public static bool TryGetFileNameSessionId(string path, out string sessionId)
    {
        sessionId = string.Empty;
        var match = SessionFilePattern.Match(Path.GetFileName(path));
        if (!match.Success)
        {
            return false;
        }

        sessionId = SessionIdHasher.Hash(match.Groups["sessionId"].Value);
        return !string.Equals(sessionId, "session-unknown", StringComparison.Ordinal);
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
                if (!TryCreateCandidate(
                        path,
                        cutoffUtc + _activeWindow,
                        enforceActiveWindow: true,
                        out var candidate))
                {
                    continue;
                }

                if (candidate.IsInternal)
                {
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

    private void AddCandidate(
        PriorityQueue<SessionFileCandidate, (long Ticks, string Path)> selected,
        SessionFileCandidate candidate)
    {
        var priority = (
            candidate.LastWriteTimeUtc.UtcDateTime.Ticks,
            candidate.FullPath);
        if (selected.Count < _selectionLimit)
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

    private static bool TryGetIdentity(
        string path,
        out string sessionId,
        out bool isInternal)
    {
        sessionId = string.Empty;
        isInternal = false;

        var hasFileNameIdentity = TryGetFileNameSessionId(path, out sessionId);
        var metadata = TryReadSessionMetadata(path);
        if (metadata is not null)
        {
            var metadataSessionId = SessionIdHasher.Hash(metadata.SessionId);
            if (!hasFileNameIdentity)
            {
                sessionId = metadataSessionId;
            }
            else if (!string.Equals(sessionId, metadataSessionId, StringComparison.Ordinal))
            {
                return false;
            }

            isInternal = metadata.IsInternal;
        }

        return !string.IsNullOrWhiteSpace(sessionId)
            && !string.Equals(sessionId, "session-unknown", StringComparison.Ordinal);
    }

    private static SessionMetadata? TryReadSessionMetadata(string path)
    {
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
            var newlineIndex = -1;
            while (read < buffer.Length)
            {
                var count = stream.Read(buffer, read, buffer.Length - read);
                if (count == 0)
                {
                    break;
                }

                read += count;
                newlineIndex = Array.IndexOf(buffer, (byte)'\n', 0, read);
                if (newlineIndex >= 0)
                {
                    break;
                }
            }

            if (read == 0 || newlineIndex < 0 && read == buffer.Length)
            {
                return null;
            }

            var lineLength = newlineIndex >= 0 ? newlineIndex + 1 : read;
            using var document = JsonDocument.Parse(buffer.AsMemory(0, lineLength));
            var root = document.RootElement;
            if (!TryGetString(root, "type", out var rootType)
                || !string.Equals(rootType, "session_meta", StringComparison.Ordinal))
            {
                return null;
            }

            var payload = root;
            if (root.TryGetProperty("payload", out var payloadElement)
                && payloadElement.ValueKind == JsonValueKind.Object)
            {
                payload = payloadElement;
            }

            if (!TryGetString(payload, "id", out var rawSessionId))
            {
                return null;
            }

            return new SessionMetadata(rawSessionId, IsSubagent(payload));
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool IsSubagent(JsonElement payload)
    {
        if (!payload.TryGetProperty("source", out var source))
        {
            return false;
        }

        if (source.ValueKind == JsonValueKind.String)
        {
            return string.Equals(source.GetString(), "subagent", StringComparison.OrdinalIgnoreCase);
        }

        if (source.ValueKind != JsonValueKind.Object
            || !source.TryGetProperty("subagent", out var subagent))
        {
            return false;
        }

        return subagent.ValueKind == JsonValueKind.True
            || subagent.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(subagent.GetString());
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

    private sealed record SessionMetadata(string SessionId, bool IsInternal);
}

internal static class CodexSessionCatalogPaths
{
    internal static string GetDefaultCodexDirectory()
    {
        var candidates = new List<string>();
        AddCandidate(candidates, Environment.GetEnvironmentVariable("CODEX_HOME"));
        AddUserProfileCandidate(candidates, Environment.GetEnvironmentVariable("USERPROFILE"));
        AddUserProfileCandidate(candidates, Environment.GetEnvironmentVariable("HOME"));
        AddUserProfileCandidate(
            candidates,
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

        var existingCatalog = candidates.FirstOrDefault(HasCatalogData);
        return existingCatalog
            ?? candidates.FirstOrDefault()
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".codex");
    }

    private static void AddUserProfileCandidate(
        ICollection<string> candidates,
        string? userProfile)
    {
        if (!string.IsNullOrWhiteSpace(userProfile))
        {
            AddCandidate(candidates, Path.Combine(userProfile, ".codex"));
        }
    }

    private static void AddCandidate(
        ICollection<string> candidates,
        string? candidate)
    {
        if (!string.IsNullOrWhiteSpace(candidate)
            && !candidates.Contains(candidate, StringComparer.OrdinalIgnoreCase))
        {
            candidates.Add(candidate);
        }
    }

    private static bool HasCatalogData(string directory)
    {
        return File.Exists(Path.Combine(directory, "session_index.jsonl"))
            || File.Exists(Path.Combine(directory, "state_5.sqlite"))
            || Directory.Exists(Path.Combine(directory, "sessions"));
    }
}
