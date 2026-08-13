namespace CodexHud.Domain;

public sealed record SessionLampState(
    string SessionId,
    LampState State,
    long FirstSeenOrder);
