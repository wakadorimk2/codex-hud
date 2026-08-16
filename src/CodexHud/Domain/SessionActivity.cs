namespace CodexHud.Domain;

public sealed record SessionActivity(
    string SessionId,
    string RolloutPath,
    DateTimeOffset ActivityAtUtc);
