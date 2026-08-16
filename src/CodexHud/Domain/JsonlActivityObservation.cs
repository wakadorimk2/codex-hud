namespace CodexHud.Domain;

public enum JsonlActivityKind
{
    TurnStarted,
    ActivityHeartbeat,
    TurnCompleted,
    TurnAborted
}

public sealed record JsonlActivityObservation(
    string SessionId,
    JsonlActivityKind Kind,
    DateTimeOffset ObservedAtUtc,
    bool IsSilent = false);
