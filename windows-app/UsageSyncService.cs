namespace CodexUsageMonitor;

public sealed class UsageSyncService : IAsyncDisposable
{
    private readonly IReadOnlyList<IQuotaConnector> _connectors;
    private readonly QuotaSnapshotStore _store;
    private readonly SemaphoreSlim _syncGate = new(1, 1);
    private readonly TimeSpan _interval;
    private CancellationTokenSource? _runCancellation;
    private Task? _runTask;

    public UsageSyncService(IReadOnlyList<IQuotaConnector> connectors, QuotaSnapshotStore store, TimeSpan? interval = null)
    {
        _connectors = connectors;
        _store = store;
        _interval = interval ?? TimeSpan.FromMinutes(5);
    }

    public event Action<UsageSnapshot>? SnapshotUpdated;

    public async Task<UsageSnapshot> SyncNowAsync(CancellationToken cancellationToken = default)
    {
        await _syncGate.WaitAsync(cancellationToken);
        try
        {
            var previous = await _store.LoadLatestAsync(cancellationToken);
            var previousById = (previous?.Providers ?? Array.Empty<ProviderSnapshot>()).ToDictionary(p => p.Id);
            var tasks = _connectors.Select(async connector =>
            {
                ProviderSnapshot fresh;
                try { fresh = await connector.FetchAsync(cancellationToken); }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception)
                {
                    fresh = new ProviderSnapshot(connector.Id, connector.Id, "fetch_failed", null, DateTimeOffset.UtcNow,
                        "none", Array.Empty<UsageWindow>(), "unexpected_error", "Beklenmeyen sağlayıcı hatası.");
                }
                return MergeWithPrevious(fresh, previousById.GetValueOrDefault(connector.Id));
            });

            var providers = await Task.WhenAll(tasks);
            var snapshot = new UsageSnapshot(DateTimeOffset.UtcNow, providers);
            string? storageWarning = null;
            try { await _store.SaveAsync(snapshot, cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (IOException) { storageWarning = "local_store_unavailable"; }
            catch (UnauthorizedAccessException) { storageWarning = "local_store_access_denied"; }

            var published = snapshot with { StorageWarning = storageWarning };
            if (SnapshotUpdated is { } handlers)
            {
                foreach (Action<UsageSnapshot> handler in handlers.GetInvocationList())
                {
                    try { handler(published); }
                    catch (Exception) { /* Keep a UI subscriber from stopping background sync. */ }
                }
            }
            return published;
        }
        finally { _syncGate.Release(); }
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_runTask is not null) return;
        _runCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = _runCancellation.Token;
        await SyncNowAsync(token);
        _runTask = RunTimerAsync(token);
    }

    public async Task StopAsync()
    {
        if (_runCancellation is null) return;
        await _runCancellation.CancelAsync();
        if (_runTask is not null)
        {
            try { await _runTask; }
            catch (OperationCanceledException) { }
        }
        _runCancellation.Dispose();
        _runCancellation = null;
        _runTask = null;
    }

    private async Task RunTimerAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(_interval);
        while (await timer.WaitForNextTickAsync(cancellationToken))
            await SyncNowAsync(cancellationToken);
    }

    private static ProviderSnapshot MergeWithPrevious(ProviderSnapshot fresh, ProviderSnapshot? previous)
    {
        if (fresh.State == "ok" || (fresh.State == "partial" && fresh.Windows.Count > 0)
            || previous is null || previous.Windows.Count == 0) return fresh;
        return previous with
        {
            State = "stale",
            Source = "local-cache",
            ErrorCode = fresh.ErrorCode ?? fresh.State,
            Message = fresh.Message ?? "Son başarılı kota verisi gösteriliyor."
        };
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _syncGate.Dispose();
    }
}
