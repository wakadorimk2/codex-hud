using System.IO;
using CodexHud.Domain;

namespace CodexHud.Infrastructure;

public sealed class SessionMonitorEngine : IDisposable
{
    public static readonly TimeSpan DefaultSqliteActivityFreshness = TimeSpan.FromMinutes(3);
    public static readonly TimeSpan DefaultJsonlActiveFreshness = TimeSpan.FromSeconds(12);
    public static readonly TimeSpan DefaultListeningFreshness = TimeSpan.FromSeconds(90);
    public static readonly TimeSpan DefaultTerminalHold = TimeSpan.FromSeconds(120);
    public static readonly TimeSpan DefaultReadErrorHold = TimeSpan.FromSeconds(30);

    private readonly object _gate = new();
    private readonly CodexSessionFileDiscovery _fileDiscovery;
    private readonly CodexSessionEventProbe _eventProbe;
    private readonly ISessionActivitySource _activitySource;
    private readonly TimeSpan _sqliteActivityFreshness;
    private readonly TimeSpan _jsonlActiveFreshness;
    private readonly TimeSpan _listeningFreshness;
    private readonly TimeSpan _terminalHold;
    private readonly TimeSpan _readErrorHold;
    private readonly int _maximumSessions;
    private readonly Dictionary<string, MonitoredSession> _sessions =
        new(StringComparer.Ordinal);
    private DateTimeOffset _lastNowUtc = DateTimeOffset.UtcNow;
    private long _nextFirstSeenOrder;
    private bool _disposed;

    public SessionMonitorEngine(
        CodexSessionFileDiscovery fileDiscovery,
        CodexSessionEventProbe? eventProbe = null,
        ISessionActivitySource? activitySource = null,
        TimeSpan? sqliteActivityFreshness = null,
        TimeSpan? jsonlActiveFreshness = null,
        TimeSpan? listeningFreshness = null,
        TimeSpan? terminalHold = null,
        TimeSpan? readErrorHold = null,
        int maximumSessions = 64)
    {
        _fileDiscovery = fileDiscovery ?? throw new ArgumentNullException(nameof(fileDiscovery));
        _eventProbe = eventProbe ?? new CodexSessionEventProbe();
        _activitySource = activitySource ?? new EmptySessionActivitySource();
        _sqliteActivityFreshness = ValidateDuration(
            sqliteActivityFreshness ?? DefaultSqliteActivityFreshness,
            nameof(sqliteActivityFreshness));
        _jsonlActiveFreshness = ValidateDuration(
            jsonlActiveFreshness ?? DefaultJsonlActiveFreshness,
            nameof(jsonlActiveFreshness));
        _listeningFreshness = ValidateDuration(
            listeningFreshness ?? DefaultListeningFreshness,
            nameof(listeningFreshness));
        _terminalHold = ValidateDuration(
            terminalHold ?? DefaultTerminalHold,
            nameof(terminalHold));
        _readErrorHold = ValidateDuration(
            readErrorHold ?? DefaultReadErrorHold,
            nameof(readErrorHold));
        if (maximumSessions <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumSessions));
        }

        _maximumSessions = maximumSessions;
    }

    public event EventHandler<StateChangedEventArgs>? StateChanged;

    public event EventHandler<SessionsChangedEventArgs>? SessionsChanged;

    public LampState CurrentState
    {
        get
        {
            lock (_gate)
            {
                return GetAggregateState();
            }
        }
    }

    public IReadOnlyList<SessionLampState> CurrentSessions => GetVisibleSessions();

    public IReadOnlyList<SessionLampState> GetVisibleSessions()
    {
        lock (_gate)
        {
            return CreateSnapshot();
        }
    }

    public void RefreshActiveSessions(DateTimeOffset? nowUtc = null)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            var observedAtUtc = (nowUtc ?? DateTimeOffset.UtcNow).ToUniversalTime();
            _lastNowUtc = observedAtUtc;
            var previous = CreateSnapshot();
            RefreshActiveSessionsCore(observedAtUtc);
            AdvanceLifecycleCore(observedAtUtc);
            PublishChanges(previous);
        }
    }

    public void PollPaths(
        IEnumerable<string> paths,
        DateTimeOffset? nowUtc = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            var observedAtUtc = (nowUtc ?? DateTimeOffset.UtcNow).ToUniversalTime();
            _lastNowUtc = observedAtUtc;
            var changedPaths = paths
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(Path.GetFullPath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (changedPaths.Any(IsFullRefreshPath))
            {
                var previous = CreateSnapshot();
                RefreshActiveSessionsCore(observedAtUtc);
                AdvanceLifecycleCore(observedAtUtc);
                PublishChanges(previous);
                return;
            }

            var previousSnapshot = CreateSnapshot();
            foreach (var path in changedPaths)
            {
                PollPathCore(path, observedAtUtc);
            }

            TrimToMaximum();
            AdvanceLifecycleCore(observedAtUtc);
            PublishChanges(previousSnapshot);
        }
    }

    public void AdvanceLifecycle(DateTimeOffset? nowUtc = null)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _lastNowUtc = (nowUtc ?? DateTimeOffset.UtcNow).ToUniversalTime();
            var previous = CreateSnapshot();
            AdvanceLifecycleCore(_lastNowUtc);
            PublishChanges(previous);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _sessions.Clear();
        }
    }

    private void RefreshActiveSessionsCore(DateTimeOffset nowUtc)
    {
        var discovery = _fileDiscovery.Discover(nowUtc);
        var candidates = SelectUniqueCandidates(discovery.Candidates);
        var activities = TryReadActivities(nowUtc);
        var activitiesById = activities
            .Where(IsValidActivity)
            .GroupBy(activity => activity.SessionId, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.OrderByDescending(activity => activity.ActivityAtUtc).First(),
                StringComparer.Ordinal);

        foreach (var activity in activitiesById.Values)
        {
            if (candidates.ContainsKey(activity.SessionId)
                || !_fileDiscovery.TryCreateCandidate(
                    activity.RolloutPath,
                    nowUtc,
                    enforceActiveWindow: false,
                    out var activityCandidate))
            {
                continue;
            }

            if (activityCandidate.IsInternal)
            {
                continue;
            }

            candidates[activity.SessionId] = activityCandidate;
        }

        var previousMembership = _sessions.Values
            .ToDictionary(
                session => session.SessionId,
                session => session.IsMember,
                StringComparer.Ordinal);
        foreach (var session in _sessions.Values)
        {
            session.IsMember = false;
        }

        var readResults = new Dictionary<string, JsonlReadResult>(StringComparer.Ordinal);
        foreach (var candidate in candidates.Values)
        {
            var result = _eventProbe.ReadCandidate(candidate, nowUtc);
            readResults[candidate.SessionId] = result;
        }

        foreach (var candidate in candidates.Values)
        {
            var session = GetOrCreate(candidate.SessionId);
            session.IsMember = true;
            session.RolloutPath = candidate.FullPath;
            session.LastFileWriteAtUtc = Max(
                session.LastFileWriteAtUtc,
                candidate.LastWriteTimeUtc);
            if (activitiesById.TryGetValue(candidate.SessionId, out var activity))
            {
                ApplyActivity(session, activity);
            }

            if (readResults.TryGetValue(candidate.SessionId, out var readResult))
            {
                ApplyReadResult(session, readResult, nowUtc);
            }
        }

        if (!discovery.IsComplete)
        {
            foreach (var session in _sessions.Values)
            {
                if (!session.IsMember
                    && previousMembership.TryGetValue(session.SessionId, out var wasMember)
                    && wasMember)
                {
                    session.IsMember = true;
                }
            }
        }

        TrimToMaximum();
    }

    private void PollPathCore(string path, DateTimeOffset nowUtc)
    {
        if (!_fileDiscovery.IsPathUnderSessionsRoot(path))
        {
            return;
        }

        if (!_fileDiscovery.TryCreateCandidate(
                path,
                nowUtc,
                enforceActiveWindow: true,
                out var candidate))
        {
            var fullPath = Path.GetFullPath(path);
            foreach (var matchingSession in _sessions.Values.Where(
                         candidateSession => string.Equals(
                             candidateSession.RolloutPath,
                             fullPath,
                             StringComparison.OrdinalIgnoreCase)))
            {
                matchingSession.IsMember = false;
            }

            AdvanceLifecycleCore(nowUtc);
            return;
        }

        if (candidate.IsInternal)
        {
            return;
        }

        var result = _eventProbe.ReadCandidate(candidate, nowUtc);
        var session = GetOrCreate(candidate.SessionId);
        session.IsMember = true;
        session.RolloutPath = candidate.FullPath;
        session.LastFileWriteAtUtc = Max(
            session.LastFileWriteAtUtc,
            candidate.LastWriteTimeUtc);
        ApplyReadResult(session, result, nowUtc);
    }

    private IReadOnlyList<SessionActivity> TryReadActivities(DateTimeOffset nowUtc)
    {
        try
        {
            return _activitySource.TryGetRecentActivities(
                    nowUtc - _fileDiscovery.ActiveWindow,
                    _maximumSessions,
                    out var activities)
                ? activities ?? Array.Empty<SessionActivity>()
                : Array.Empty<SessionActivity>();
        }
        catch (DllNotFoundException)
        {
            return Array.Empty<SessionActivity>();
        }
        catch (EntryPointNotFoundException)
        {
            return Array.Empty<SessionActivity>();
        }
        catch (BadImageFormatException)
        {
            return Array.Empty<SessionActivity>();
        }
        catch (IOException)
        {
            return Array.Empty<SessionActivity>();
        }
        catch (UnauthorizedAccessException)
        {
            return Array.Empty<SessionActivity>();
        }
    }

    private bool IsValidActivity(SessionActivity activity)
    {
        if (string.IsNullOrWhiteSpace(activity.SessionId)
            || string.IsNullOrWhiteSpace(activity.RolloutPath))
        {
            return false;
        }

        if (!_fileDiscovery.IsPathUnderSessionsRoot(activity.RolloutPath)
            || !CodexSessionFileDiscovery.TryGetFileNameSessionId(
                activity.RolloutPath,
                out var fileSessionId))
        {
            return false;
        }

        return string.Equals(fileSessionId, activity.SessionId, StringComparison.Ordinal);
    }

    private static Dictionary<string, SessionFileCandidate> SelectUniqueCandidates(
        IEnumerable<SessionFileCandidate> candidates)
    {
        return candidates
            .Where(candidate => !candidate.IsInternal)
            .GroupBy(candidate => candidate.SessionId, StringComparer.Ordinal)
            .Select(group => group
                .OrderByDescending(candidate => candidate.LastWriteTimeUtc)
                .ThenBy(candidate => candidate.FullPath, StringComparer.OrdinalIgnoreCase)
                .First())
            .ToDictionary(candidate => candidate.SessionId, StringComparer.Ordinal);
    }

    private MonitoredSession GetOrCreate(string sessionId)
    {
        if (_sessions.TryGetValue(sessionId, out var existing))
        {
            return existing;
        }

        var created = new MonitoredSession(sessionId, ++_nextFirstSeenOrder);
        _sessions.Add(sessionId, created);
        return created;
    }

    private static void ApplyActivity(MonitoredSession session, SessionActivity activity)
    {
        if (session.LastSqliteActivityAtUtc.HasValue
            && activity.ActivityAtUtc <= session.LastSqliteActivityAtUtc.Value)
        {
            return;
        }

        session.LastSqliteActivityAtUtc = activity.ActivityAtUtc.ToUniversalTime();
    }

    private void ApplyReadResult(
        MonitoredSession session,
        JsonlReadResult result,
        DateTimeOffset nowUtc)
    {
        if (result.ReadError)
        {
            session.ReadErrorUntilUtc = nowUtc + _readErrorHold;
        }

        foreach (var observation in result.Observations)
        {
            if (session.LastJsonlActivityAtUtc.HasValue
                && observation.ObservedAtUtc < session.LastJsonlActivityAtUtc.Value)
            {
                continue;
            }

            session.LastJsonlActivityAtUtc = observation.ObservedAtUtc.ToUniversalTime();
            switch (observation.Kind)
            {
                case JsonlActivityKind.TurnStarted:
                case JsonlActivityKind.ActivityHeartbeat:
                    session.LastJsonlActiveAtUtc = observation.ObservedAtUtc.ToUniversalTime();
                    session.TerminalState = null;
                    session.TerminalUntilUtc = null;
                    session.ReadErrorUntilUtc = null;
                    break;
                case JsonlActivityKind.TurnCompleted:
                    if (!observation.IsSilent)
                    {
                        session.TerminalState = LampState.Completed;
                        session.TerminalUntilUtc = nowUtc + _terminalHold;
                    }

                    break;
                case JsonlActivityKind.TurnAborted:
                    session.TerminalState = LampState.Aborted;
                    session.TerminalUntilUtc = nowUtc + _terminalHold;
                    break;
            }
        }
    }

    private void AdvanceLifecycleCore(DateTimeOffset nowUtc)
    {
        foreach (var session in _sessions.Values.ToArray())
        {
            if (session.ReadErrorUntilUtc > nowUtc)
            {
                session.State = LampState.ReadError;
            }
            else if (session.TerminalUntilUtc > nowUtc && session.TerminalState.HasValue)
            {
                session.State = session.TerminalState.Value;
            }
            else
            {
                session.ReadErrorUntilUtc = null;
                session.TerminalUntilUtc = null;
                session.TerminalState = null;
                session.State = DeriveLiveState(session, nowUtc);
            }

            if (!session.IsMember
                && session.ReadErrorUntilUtc <= nowUtc
                && session.TerminalUntilUtc <= nowUtc)
            {
                _sessions.Remove(session.SessionId);
            }
        }
    }

    private LampState DeriveLiveState(MonitoredSession session, DateTimeOffset nowUtc)
    {
        if (IsFresh(session.LastSqliteActivityAtUtc, nowUtc, _sqliteActivityFreshness)
            || IsFresh(session.LastJsonlActiveAtUtc, nowUtc, _jsonlActiveFreshness))
        {
            return LampState.Active;
        }

        var listeningAtUtc = Max(
            session.LastFileWriteAtUtc,
            session.LastJsonlActivityAtUtc);
        return IsFresh(listeningAtUtc, nowUtc, _listeningFreshness)
            ? LampState.Listening
            : LampState.Idle;
    }

    private void TrimToMaximum()
    {
        var overflow = _sessions.Values
            .Where(session => session.IsMember)
            .OrderByDescending(session => session.LastObservedAtUtc ?? DateTimeOffset.MinValue)
            .ThenBy(session => session.FirstSeenOrder)
            .Skip(_maximumSessions)
            .ToArray();
        foreach (var session in overflow)
        {
            session.IsMember = false;
        }
    }

    private IReadOnlyList<SessionLampState> CreateSnapshot()
    {
        var nowUtc = _lastNowUtc;
        return _sessions.Values
            .Where(session => session.IsMember
                || session.ReadErrorUntilUtc > nowUtc
                || session.TerminalUntilUtc > nowUtc)
            .Select(session => session.ToLampState())
            .OrderBy(session => GetStatePriority(session.State))
            .ThenBy(session => session.FirstSeenOrder)
            .ToArray();
    }

    private LampState GetAggregateState()
    {
        return CreateSnapshot()
            .OrderBy(state => GetStatePriority(state.State))
            .Select(state => state.State)
            .FirstOrDefault(LampState.Idle);
    }

    private void PublishChanges(IReadOnlyList<SessionLampState> previous)
    {
        var current = CreateSnapshot();
        var previousAggregate = previous
            .OrderBy(session => GetStatePriority(session.State))
            .Select(session => session.State)
            .FirstOrDefault(LampState.Idle);
        var currentAggregate = current
            .OrderBy(session => GetStatePriority(session.State))
            .Select(session => session.State)
            .FirstOrDefault(LampState.Idle);

        if (previousAggregate != currentAggregate)
        {
            StateChanged?.Invoke(
                this,
                new StateChangedEventArgs(previousAggregate, currentAggregate));
        }

        if (!AreSnapshotsEqual(previous, current))
        {
            SessionsChanged?.Invoke(this, new SessionsChangedEventArgs(current));
        }
    }

    private static bool AreSnapshotsEqual(
        IReadOnlyList<SessionLampState> first,
        IReadOnlyList<SessionLampState> second)
    {
        if (first.Count != second.Count)
        {
            return false;
        }

        for (var index = 0; index < first.Count; index++)
        {
            if (first[index].SessionId != second[index].SessionId
                || first[index].State != second[index].State
                || first[index].FirstSeenOrder != second[index].FirstSeenOrder)
            {
                return false;
            }
        }

        return true;
    }

    private bool IsFullRefreshPath(string path)
    {
        var fileName = Path.GetFileName(path);
        return fileName.Equals("session_index.jsonl", StringComparison.OrdinalIgnoreCase)
            || fileName.Equals("state_5.sqlite", StringComparison.OrdinalIgnoreCase);
    }

    private static int GetStatePriority(LampState state)
    {
        return state switch
        {
            LampState.Active => 0,
            LampState.Listening => 1,
            LampState.ReadError => 2,
            LampState.Aborted => 3,
            LampState.Completed => 4,
            LampState.Idle => 5,
            _ => 6
        };
    }

    private static bool IsFresh(
        DateTimeOffset? timestamp,
        DateTimeOffset nowUtc,
        TimeSpan freshness)
    {
        return timestamp.HasValue
            && timestamp.Value <= nowUtc
            && nowUtc - timestamp.Value <= freshness;
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

    private static TimeSpan ValidateDuration(TimeSpan value, string parameterName)
    {
        if (value < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }

        return value;
    }

    private sealed class MonitoredSession
    {
        public MonitoredSession(string sessionId, long firstSeenOrder)
        {
            SessionId = sessionId;
            FirstSeenOrder = firstSeenOrder;
            State = LampState.Idle;
        }

        public string SessionId { get; }

        public long FirstSeenOrder { get; }

        public string? RolloutPath { get; set; }

        public bool IsMember { get; set; }

        public LampState State { get; set; }

        public DateTimeOffset? LastFileWriteAtUtc { get; set; }

        public DateTimeOffset? LastJsonlActivityAtUtc { get; set; }

        public DateTimeOffset? LastJsonlActiveAtUtc { get; set; }

        public DateTimeOffset? LastSqliteActivityAtUtc { get; set; }

        public LampState? TerminalState { get; set; }

        public DateTimeOffset? TerminalUntilUtc { get; set; }

        public DateTimeOffset? ReadErrorUntilUtc { get; set; }

        public DateTimeOffset? LastObservedAtUtc => Max(
            Max(LastFileWriteAtUtc, LastJsonlActivityAtUtc),
            LastSqliteActivityAtUtc);

        public SessionLampState ToLampState()
        {
            return new SessionLampState(SessionId, State, FirstSeenOrder)
            {
                LastObservedAtUtc = LastObservedAtUtc,
                LastJsonlActivityAtUtc = LastJsonlActivityAtUtc,
                LastSqliteActivityAtUtc = LastSqliteActivityAtUtc
            };
        }
    }
}

public sealed class StateChangedEventArgs : EventArgs
{
    public StateChangedEventArgs(LampState previousState, LampState currentState)
    {
        PreviousState = previousState;
        CurrentState = currentState;
    }

    public LampState PreviousState { get; }

    public LampState CurrentState { get; }
}

public sealed class SessionsChangedEventArgs : EventArgs
{
    public SessionsChangedEventArgs(IReadOnlyList<SessionLampState> sessions)
    {
        Sessions = sessions;
    }

    public IReadOnlyList<SessionLampState> Sessions { get; }
}
