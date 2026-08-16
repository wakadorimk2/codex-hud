using CodexHud.Domain;

namespace CodexHud.Infrastructure;

public sealed class SessionCatalogReconciler
{
    private readonly SessionStateStore _stateStore;
    private readonly CodexSessionCatalogProbe _catalogProbe;
    private readonly CodexSessionFileDiscovery _fileDiscovery;
    private readonly CodexSessionEventProbe _eventProbe;
    private readonly Action? _requestAgain;

    public SessionCatalogReconciler(
        SessionStateStore stateStore,
        CodexSessionCatalogProbe catalogProbe,
        CodexSessionFileDiscovery fileDiscovery,
        CodexSessionEventProbe eventProbe,
        Action? requestAgain = null)
    {
        _stateStore = stateStore ?? throw new ArgumentNullException(nameof(stateStore));
        _catalogProbe = catalogProbe ?? throw new ArgumentNullException(nameof(catalogProbe));
        _fileDiscovery = fileDiscovery ?? throw new ArgumentNullException(nameof(fileDiscovery));
        _eventProbe = eventProbe ?? throw new ArgumentNullException(nameof(eventProbe));
        _requestAgain = requestAgain;
    }

    public void Reconcile(DateTimeOffset? nowUtc = null)
    {
        var observedAtUtc = (nowUtc ?? DateTimeOffset.UtcNow).ToUniversalTime();
        var catalogReadSucceeded = _catalogProbe.TryRead(out var catalogEntries);
        var discovery = _fileDiscovery.Discover(observedAtUtc);
        var observations = _eventProbe.Read(discovery, observedAtUtc);

        foreach (var observation in observations)
        {
            _stateStore.Apply(observation);
        }

        if (catalogReadSucceeded)
        {
            _stateStore.ReconcileCatalog(
                catalogEntries,
                observedAtUtc,
                allowStaleRemoval: discovery.IsComplete);
        }

        if (_eventProbe.HasBacklog)
        {
            _requestAgain?.Invoke();
        }
    }
}
