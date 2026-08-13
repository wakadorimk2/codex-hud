using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using CodexHud.Domain;

namespace CodexHud.Infrastructure;

public sealed class SessionStateSnapshotStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public SessionStateSnapshotStore(string? path = null)
    {
        _path = path ?? GetDefaultPath();
    }

    private readonly string _path;

    public IReadOnlyList<SessionLampState> Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return Array.Empty<SessionLampState>();
            }

            var savedStates = JsonSerializer.Deserialize<List<PersistedSessionState>>(
                File.ReadAllText(_path),
                JsonOptions);
            if (savedStates is null)
            {
                return Array.Empty<SessionLampState>();
            }

            return savedStates
                .Where(IsValid)
                .GroupBy(state => state.SessionId, StringComparer.Ordinal)
                .Select(group => group
                    .OrderBy(state => state.FirstSeenOrder)
                    .First())
                .OrderBy(state => state.FirstSeenOrder)
                .ThenBy(state => state.SessionId, StringComparer.Ordinal)
                .Select(state => new SessionLampState(
                    state.SessionId,
                    state.State,
                    state.FirstSeenOrder))
                .ToArray();
        }
        catch (IOException)
        {
            return Array.Empty<SessionLampState>();
        }
        catch (JsonException)
        {
            return Array.Empty<SessionLampState>();
        }
        catch (UnauthorizedAccessException)
        {
            return Array.Empty<SessionLampState>();
        }
    }

    public bool TrySave(IEnumerable<SessionLampState> sessions)
    {
        var savedStates = sessions
            .Where(state => IsValid(new PersistedSessionState(
                state.SessionId,
                state.State,
                state.FirstSeenOrder)))
            .Where(state => state.State != LampState.Idle)
            .GroupBy(state => state.SessionId, StringComparer.Ordinal)
            .Select(group => group
                .OrderBy(state => state.FirstSeenOrder)
                .First())
            .OrderBy(state => state.FirstSeenOrder)
            .ThenBy(state => state.SessionId, StringComparer.Ordinal)
            .Select(state => new PersistedSessionState(
                state.SessionId,
                state.State,
                state.FirstSeenOrder))
            .ToArray();

        try
        {
            var directory = Path.GetDirectoryName(_path);
            if (string.IsNullOrWhiteSpace(directory))
            {
                return false;
            }

            Directory.CreateDirectory(directory);
            var json = JsonSerializer.Serialize(savedStates, JsonOptions);
            File.WriteAllText(_path, json);
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

    private static bool IsValid(PersistedSessionState state)
    {
        return !string.IsNullOrWhiteSpace(state.SessionId)
            && !string.Equals(state.SessionId, "session-unknown", StringComparison.Ordinal)
            && state.FirstSeenOrder > 0
            && Enum.IsDefined(state.State);
    }

    private static string GetDefaultPath()
    {
        var localApplicationData = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(localApplicationData, "CodexHud", "sessions.json");
    }

    private sealed record PersistedSessionState(
        string SessionId,
        LampState State,
        long FirstSeenOrder);
}
