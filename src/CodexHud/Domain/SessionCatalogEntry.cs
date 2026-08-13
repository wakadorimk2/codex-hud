namespace CodexHud.Domain;

public sealed record SessionCatalogEntry(
    string SessionId,
    DateTimeOffset? LastUpdatedAtUtc,
    bool IsArchived);
