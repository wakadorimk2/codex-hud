using CodexHud.Domain;

namespace CodexHud.Infrastructure;

public interface ISessionActivitySource
{
    bool TryGetRecentActivities(
        DateTimeOffset cutoffUtc,
        int maximumRows,
        out IReadOnlyList<SessionActivity> activities);
}
