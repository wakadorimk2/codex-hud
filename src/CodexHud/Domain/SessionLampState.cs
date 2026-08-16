namespace CodexHud.Domain;

public sealed record SessionLampState(
    string SessionId,
    LampState State,
    long FirstSeenOrder)
{
    public DateTimeOffset? LastObservedAtUtc { get; init; }

    public DateTimeOffset? LastJsonlActivityAtUtc { get; init; }

    public DateTimeOffset? LastSqliteActivityAtUtc { get; init; }
}
