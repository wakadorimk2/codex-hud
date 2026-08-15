using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using CodexHud.Domain;

namespace CodexHud.Infrastructure;

public sealed class CodexSessionCatalogProbe
{
    private static readonly Regex ArchivedSessionFilePattern = new(
        @"^rollout-\d{4}-\d{2}-\d{2}T\d{2}-\d{2}-\d{2}-(?<sessionId>[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})\.jsonl$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly string _sessionIndexPath;
    private readonly string _archivedSessionsPath;

    public CodexSessionCatalogProbe(string? codexDirectory = null)
    {
        var directory = codexDirectory ?? GetDefaultCodexDirectory();
        _sessionIndexPath = Path.Combine(directory, "session_index.jsonl");
        _archivedSessionsPath = Path.Combine(directory, "archived_sessions");
    }

    public bool TryRead(out IReadOnlyList<SessionCatalogEntry> entries)
    {
        entries = Array.Empty<SessionCatalogEntry>();

        try
        {
            if (!File.Exists(_sessionIndexPath))
            {
                return false;
            }

            var catalog = new Dictionary<string, SessionCatalogEntry>(StringComparer.Ordinal);
            foreach (var line in File.ReadLines(_sessionIndexPath))
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                var record = JsonSerializer.Deserialize<SessionIndexRecord>(line, JsonOptions);
                if (record is null || string.IsNullOrWhiteSpace(record.Id))
                {
                    continue;
                }

                var sessionId = HookObservationParser.HashSessionId(record.Id);
                if (string.Equals(sessionId, "session-unknown", StringComparison.Ordinal))
                {
                    continue;
                }

                var updatedAtUtc = record.UpdatedAtUtc?.ToUniversalTime();
                if (catalog.TryGetValue(sessionId, out var existing))
                {
                    catalog[sessionId] = existing with
                    {
                        LastUpdatedAtUtc = Max(existing.LastUpdatedAtUtc, updatedAtUtc)
                    };
                }
                else
                {
                    catalog.Add(
                        sessionId,
                        new SessionCatalogEntry(sessionId, updatedAtUtc, IsArchived: false));
                }
            }

            foreach (var archivedSessionId in ReadArchivedSessionIds())
            {
                if (catalog.TryGetValue(archivedSessionId, out var existing))
                {
                    catalog[archivedSessionId] = existing with { IsArchived = true };
                }
                else
                {
                    catalog.Add(
                        archivedSessionId,
                        new SessionCatalogEntry(
                            archivedSessionId,
                            LastUpdatedAtUtc: null,
                            IsArchived: true));
                }
            }

            entries = catalog.Values
                .OrderBy(entry => entry.SessionId, StringComparer.Ordinal)
                .ToArray();
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (JsonException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    private IReadOnlySet<string> ReadArchivedSessionIds()
    {
        var archivedSessionIds = new HashSet<string>(StringComparer.Ordinal);
        if (!Directory.Exists(_archivedSessionsPath))
        {
            return archivedSessionIds;
        }

        foreach (var path in Directory.EnumerateFiles(
                     _archivedSessionsPath,
                     "rollout-*.jsonl",
                     SearchOption.TopDirectoryOnly))
        {
            var fileName = Path.GetFileName(path);
            var match = ArchivedSessionFilePattern.Match(fileName);
            if (!match.Success)
            {
                continue;
            }

            var sessionId = HookObservationParser.HashSessionId(
                match.Groups["sessionId"].Value);
            if (!string.Equals(sessionId, "session-unknown", StringComparison.Ordinal))
            {
                archivedSessionIds.Add(sessionId);
            }
        }

        return archivedSessionIds;
    }

    private static DateTimeOffset? Max(
        DateTimeOffset? first,
        DateTimeOffset? second)
    {
        if (!first.HasValue)
        {
            return second;
        }

        if (!second.HasValue)
        {
            return first;
        }

        return first.Value >= second.Value ? first : second;
    }

    private static string GetDefaultCodexDirectory()
    {
        var candidates = new List<string>();
        AddCandidate(candidates, Environment.GetEnvironmentVariable("CODEX_HOME"));
        AddUserProfileCandidate(
            candidates,
            Environment.GetEnvironmentVariable("USERPROFILE"));
        AddUserProfileCandidate(
            candidates,
            Environment.GetEnvironmentVariable("HOME"));
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
        if (string.IsNullOrWhiteSpace(userProfile))
        {
            return;
        }

        AddCandidate(candidates, Path.Combine(userProfile, ".codex"));
    }

    private static void AddCandidate(
        ICollection<string> candidates,
        string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate)
            || candidates.Contains(candidate, StringComparer.OrdinalIgnoreCase))
        {
            return;
        }

        candidates.Add(candidate);
    }

    private static bool HasCatalogData(string directory)
    {
        return File.Exists(Path.Combine(directory, "session_index.jsonl"))
            || Directory.Exists(Path.Combine(directory, "archived_sessions"));
    }

    private sealed class SessionIndexRecord
    {
        [JsonPropertyName("id")]
        public string? Id { get; set; }

        [JsonPropertyName("updated_at")]
        public DateTimeOffset? UpdatedAtUtc { get; set; }
    }
}
