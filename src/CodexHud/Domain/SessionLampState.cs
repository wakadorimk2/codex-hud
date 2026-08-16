namespace CodexHud.Domain;

public sealed record SessionLampState(
    string SessionId,
    LampState State,
    long FirstSeenOrder)
{
    public LampAppearance Appearance { get; init; } = LampAppearance.Default;

    public DateTimeOffset? LastObservedAtUtc { get; init; }

    public DateTimeOffset? LastHookObservedAtUtc { get; init; }

    public DateTimeOffset? LastJsonlActivityAtUtc { get; init; }
}
