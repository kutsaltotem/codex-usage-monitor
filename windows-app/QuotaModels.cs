using System.Net;

namespace CodexUsageMonitor;

public sealed record UsageWindow(
    string Kind,
    string Label,
    double? UsedPercent,
    double? RemainingPercent,
    DateTimeOffset? ResetsAt);

public sealed record ProviderSnapshot(
    string Id,
    string Name,
    string State,
    string? Plan,
    DateTimeOffset ObservedAt,
    string Source,
    IReadOnlyList<UsageWindow> Windows,
    string? ErrorCode = null,
    string? Message = null);

public sealed record UsageSnapshot(
    DateTimeOffset GeneratedAt,
    IReadOnlyList<ProviderSnapshot> Providers,
    string? StorageWarning = null);

public sealed record LocalCredential(
    string AccessToken,
    string? RefreshToken,
    string? AccountId,
    DateTimeOffset? ExpiresAt,
    string Source,
    string? FilePath = null,
    bool ApiKeyOnly = false);

public interface IQuotaConnector
{
    string Id { get; }
    Task<ProviderSnapshot> FetchAsync(CancellationToken cancellationToken);
}

public sealed record QuotaApiReply(HttpStatusCode StatusCode, System.Text.Json.JsonDocument? Body) : IDisposable
{
    public void Dispose() => Body?.Dispose();
}

public static class QuotaHttpClient
{
    public static HttpClient Create()
    {
        return new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
    }
}
