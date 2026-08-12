using CodexHud.Domain;

namespace CodexHud.Infrastructure;

public sealed class SessionStateStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, LampState> _sessionStates = new(StringComparer.Ordinal);

    public event EventHandler<StateChangedEventArgs>? StateChanged;

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

    public LampState GetSessionState(string sessionId)
    {
        lock (_gate)
        {
            return _sessionStates.TryGetValue(sessionId, out var state)
                ? state
                : LampState.Idle;
        }
    }

    public void Apply(HookObservation observation)
    {
        var nextState = MapEvent(observation.Event);
        if (nextState is null)
        {
            return;
        }

        LampState previousState;
        LampState currentState;
        lock (_gate)
        {
            previousState = GetAggregateState();
            _sessionStates[observation.SessionId] = nextState.Value;
            currentState = GetAggregateState();
        }

        if (previousState != currentState)
        {
            StateChanged?.Invoke(
                this,
                new StateChangedEventArgs(previousState, currentState));
        }
    }

    private LampState GetAggregateState()
    {
        if (_sessionStates.Values.Any(state => state == LampState.NeedsAttention))
        {
            return LampState.NeedsAttention;
        }

        if (_sessionStates.Values.Any(state => state == LampState.Running))
        {
            return LampState.Running;
        }

        return LampState.Idle;
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
