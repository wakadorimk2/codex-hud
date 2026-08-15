using CodexHud.Domain;

namespace CodexHud.Infrastructure;

public sealed class SessionStateStore : IDisposable
{
    private static readonly TimeSpan DefaultSessionEndGrace =
        TimeSpan.FromMilliseconds(240);
    private static readonly TimeSpan DefaultSessionCleanupAge =
        TimeSpan.FromMinutes(5);

    private readonly object _gate = new();
    private readonly Dictionary<string, SessionLampState> _sessionStates = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _removalGenerations = new(StringComparer.Ordinal);
    private readonly SessionStateSnapshotStore _snapshotStore;
    private readonly TimeSpan _sessionEndGrace;
    private readonly CancellationTokenSource _shutdown = new();
    private long _nextFirstSeenOrder;
    private bool _disposed;

    public SessionStateStore(
        SessionStateSnapshotStore? snapshotStore = null,
        TimeSpan? sessionEndGrace = null)
    {
        _snapshotStore = snapshotStore ?? new SessionStateSnapshotStore();
        _sessionEndGrace = sessionEndGrace ?? DefaultSessionEndGrace;
        if (_sessionEndGrace < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(sessionEndGrace));
        }

        var restoredSessions = _snapshotStore.Load();
        var hasDiscardedSessions = false;
        foreach (var session in restoredSessions)
        {
            if (session.State == LampState.Idle
                || string.IsNullOrWhiteSpace(session.SessionId)
                || session.FirstSeenOrder <= 0)
            {
                continue;
            }

            if (_sessionStates.ContainsKey(session.SessionId))
            {
                continue;
            }

            if (!session.LastObservedAtUtc.HasValue)
            {
                hasDiscardedSessions = true;
                continue;
            }

            _sessionStates.Add(session.SessionId, session);
            _nextFirstSeenOrder = Math.Max(
                _nextFirstSeenOrder,
                session.FirstSeenOrder);
        }

        if (hasDiscardedSessions)
        {
            _snapshotStore.TrySave(_sessionStates.Values);
        }
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

    public IReadOnlyList<SessionLampState> CurrentSessions
    {
        get
        {
            lock (_gate)
            {
                return CreateSnapshot();
            }
        }
    }

    public LampState GetSessionState(string sessionId)
    {
        lock (_gate)
        {
            return _sessionStates.TryGetValue(sessionId, out var state)
                ? state.State
                : LampState.Idle;
        }
    }

    public void Apply(HookObservation observation)
    {
        if (_disposed || string.IsNullOrWhiteSpace(observation.SessionId)
            || string.Equals(observation.SessionId, "session-unknown", StringComparison.Ordinal))
        {
            return;
        }

        var nextState = MapEvent(observation.Event);
        if (nextState is null)
        {
            return;
        }

        var nextAppearance = MapAppearance(observation.Event);
        var observedAtUtc = observation.ObservedAtUtc.ToUniversalTime();
        LampState previousState;
        LampState currentState;
        IReadOnlyList<SessionLampState>? sessions = null;
        var notifySessions = false;
        var persistSessions = false;
        long removalGeneration = 0;
        var scheduleRemoval = false;

        lock (_gate)
        {
            previousState = GetAggregateState();

            if (observation.Event == HookEventKind.SessionEnd)
            {
                if (!_sessionStates.TryGetValue(observation.SessionId, out var existingSession))
                {
                    return;
                }

                removalGeneration = _removalGenerations.TryGetValue(
                    observation.SessionId,
                    out var previousGeneration)
                    ? previousGeneration + 1
                    : 1;
                _removalGenerations[observation.SessionId] = removalGeneration;

                if (existingSession.State != LampState.Idle
                    || existingSession.Appearance != LampAppearance.Default)
                {
                    _sessionStates[observation.SessionId] = existingSession with
                    {
                        State = LampState.Idle,
                        Appearance = LampAppearance.Default,
                        LastObservedAtUtc = Max(
                            existingSession.LastObservedAtUtc,
                            observedAtUtc)
                    };
                    notifySessions = true;
                }

                sessions = CreateSnapshot();
                persistSessions = true;
                scheduleRemoval = true;
            }
            else
            {
                _removalGenerations.Remove(observation.SessionId);

                if (_sessionStates.TryGetValue(observation.SessionId, out var existingSession))
                {
                    var lastObservedAtUtc = Max(
                        existingSession.LastObservedAtUtc,
                        observedAtUtc);
                    if (existingSession.State != nextState.Value
                        || existingSession.Appearance != nextAppearance
                        || existingSession.LastObservedAtUtc != lastObservedAtUtc)
                    {
                        _sessionStates[observation.SessionId] = existingSession with
                        {
                            State = nextState.Value,
                            Appearance = nextAppearance,
                            LastObservedAtUtc = lastObservedAtUtc
                        };
                        notifySessions = existingSession.State != nextState.Value
                            || existingSession.Appearance != nextAppearance;
                    }
                }
                else
                {
                    var session = new SessionLampState(
                        observation.SessionId,
                        nextState.Value,
                        ++_nextFirstSeenOrder);
                    session = session with
                    {
                        Appearance = nextAppearance,
                        LastObservedAtUtc = observedAtUtc
                    };
                    _sessionStates.Add(observation.SessionId, session);
                    notifySessions = true;
                }

                sessions = CreateSnapshot();
                persistSessions = true;
            }

            currentState = GetAggregateState();
        }

        RaiseChanges(
            previousState,
            currentState,
            sessions!,
            notifySessions);

        if (persistSessions)
        {
            _snapshotStore.TrySave(sessions!);
        }

        if (scheduleRemoval)
        {
            _ = RemoveAfterGraceAsync(
                observation.SessionId,
                removalGeneration);
        }
    }

    public int ReconcileCatalog(
        IEnumerable<SessionCatalogEntry> catalogEntries,
        DateTimeOffset nowUtc,
        TimeSpan? cleanupAge = null)
    {
        if (_disposed)
        {
            return 0;
        }

        var maxAge = cleanupAge ?? DefaultSessionCleanupAge;
        if (maxAge < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(cleanupAge));
        }

        var catalog = CreateCatalogMap(catalogEntries);
        nowUtc = nowUtc.ToUniversalTime();

        LampState previousState;
        LampState currentState;
        IReadOnlyList<SessionLampState> sessions;
        var removedCount = 0;

        lock (_gate)
        {
            previousState = GetAggregateState();

            foreach (var session in _sessionStates.Values.ToArray())
            {
                if (!ShouldRemove(
                        session,
                        catalog,
                        nowUtc,
                        maxAge))
                {
                    continue;
                }

                _sessionStates.Remove(session.SessionId);
                _removalGenerations.Remove(session.SessionId);
                removedCount++;
            }

            if (removedCount == 0)
            {
                return 0;
            }

            currentState = GetAggregateState();
            sessions = CreateSnapshot();
        }

        RaiseChanges(previousState, currentState, sessions, notifySessions: true);
        _snapshotStore.TrySave(sessions);
        return removedCount;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _shutdown.Cancel();
        _shutdown.Dispose();
    }

    private async Task RemoveAfterGraceAsync(string sessionId, long removalGeneration)
    {
        try
        {
            await Task.Delay(_sessionEndGrace, _shutdown.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        LampState previousState;
        LampState currentState;
        IReadOnlyList<SessionLampState> sessions;

        lock (_gate)
        {
            if (_disposed
                || !_removalGenerations.TryGetValue(sessionId, out var currentGeneration)
                || currentGeneration != removalGeneration
                || !_sessionStates.TryGetValue(sessionId, out var session)
                || session.State != LampState.Idle)
            {
                return;
            }

            previousState = GetAggregateState();
            _sessionStates.Remove(sessionId);
            _removalGenerations.Remove(sessionId);
            currentState = GetAggregateState();
            sessions = CreateSnapshot();
        }

        RaiseChanges(previousState, currentState, sessions, notifySessions: true);
        _snapshotStore.TrySave(sessions);
    }

    private void RaiseChanges(
        LampState previousState,
        LampState currentState,
        IReadOnlyList<SessionLampState> sessions,
        bool notifySessions)
    {
        if (previousState != currentState)
        {
            StateChanged?.Invoke(
                this,
                new StateChangedEventArgs(previousState, currentState));
        }

        if (notifySessions)
        {
            SessionsChanged?.Invoke(
                this,
                new SessionsChangedEventArgs(sessions));
        }
    }

    private IReadOnlyList<SessionLampState> CreateSnapshot()
    {
        return _sessionStates.Values
            .OrderBy(session => GetStatePriority(session.State))
            .ThenBy(session => session.FirstSeenOrder)
            .ToArray();
    }

    private static int GetStatePriority(LampState state)
    {
        return state switch
        {
            LampState.NeedsAttention => 0,
            LampState.Running => 1,
            LampState.Idle => 2,
            _ => 3
        };
    }

    private LampState GetAggregateState()
    {
        if (_sessionStates.Values.Any(session => session.State == LampState.NeedsAttention))
        {
            return LampState.NeedsAttention;
        }

        if (_sessionStates.Values.Any(session => session.State == LampState.Running))
        {
            return LampState.Running;
        }

        return LampState.Idle;
    }

    private static Dictionary<string, SessionCatalogEntry> CreateCatalogMap(
        IEnumerable<SessionCatalogEntry> catalogEntries)
    {
        var catalog = new Dictionary<string, SessionCatalogEntry>(StringComparer.Ordinal);
        foreach (var entry in catalogEntries)
        {
            if (string.IsNullOrWhiteSpace(entry.SessionId))
            {
                continue;
            }

            if (catalog.TryGetValue(entry.SessionId, out var existing))
            {
                catalog[entry.SessionId] = existing with
                {
                    LastUpdatedAtUtc = Max(
                        existing.LastUpdatedAtUtc,
                        entry.LastUpdatedAtUtc),
                    IsArchived = existing.IsArchived || entry.IsArchived
                };
            }
            else
            {
                catalog.Add(entry.SessionId, entry);
            }
        }

        return catalog;
    }

    private static bool ShouldRemove(
        SessionLampState session,
        IReadOnlyDictionary<string, SessionCatalogEntry> catalog,
        DateTimeOffset nowUtc,
        TimeSpan maxAge)
    {
        catalog.TryGetValue(session.SessionId, out var catalogEntry);
        if (catalogEntry?.IsArchived == true)
        {
            return true;
        }

        var lastActivityAtUtc = Max(
            session.LastObservedAtUtc,
            catalogEntry?.LastUpdatedAtUtc);
        return !lastActivityAtUtc.HasValue
            || nowUtc - lastActivityAtUtc.Value >= maxAge;
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

    private static LampState? MapEvent(HookEventKind eventKind)
    {
        return eventKind switch
        {
            HookEventKind.SessionStart => LampState.Running,
            HookEventKind.UserPromptSubmit => LampState.Running,
            HookEventKind.PermissionRequest => LampState.NeedsAttention,
            HookEventKind.Stop => LampState.NeedsAttention,
            HookEventKind.SessionEnd => LampState.Idle,
            _ => null
        };
    }

    private static LampAppearance MapAppearance(HookEventKind eventKind)
    {
        return eventKind == HookEventKind.Stop
            ? LampAppearance.Muted
            : LampAppearance.Default;
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
