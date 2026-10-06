using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace CodexUsageMonitor;

public sealed class GeminiQuotaConnector(HttpClient http) : IQuotaConnector
{
    private static readonly string[] Hosts =
    [
        "https://daily-cloudcode-pa.googleapis.com",
        "https://cloudcode-pa.googleapis.com"
    ];

    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);
    public string Id => "gemini";

    public async Task<ProviderSnapshot> FetchAsync(CancellationToken cancellationToken)
    {
        using var fetchTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        fetchTimeout.CancelAfter(TimeSpan.FromSeconds(30));
        var fetchToken = fetchTimeout.Token;
        var credential = LocalCredentialReader.ReadGemini();
        if (credential is null)
            return Empty("not_signed_in", "Gemini CLI/Antigravity oturumu bulunamadı.");
        if (credential.ExpiresAt is { } expiry && expiry <= DateTimeOffset.UtcNow.AddSeconds(60))
            return Empty("reauth_required", "Gemini oturumu süresi dolmuş; Gemini CLI/Antigravity'de tekrar giriş yapın.");

        var loadBody = new
        {
            metadata = new
            {
                ideType = credential.Source.Contains("antigravity", StringComparison.OrdinalIgnoreCase) ? "ANTIGRAVITY" : "IDE_UNSPECIFIED",
                platform = "PLATFORM_UNSPECIFIED",
                pluginType = "GEMINI"
            }
        };
        string? lastError = null;

        foreach (var host in Hosts)
        {
            try
            {
                using var load = await PostAndParseAsync(host, "loadCodeAssist", credential.AccessToken, loadBody, fetchToken);
                if (load.StatusCode == HttpStatusCode.Unauthorized)
                    return Empty("reauth_required", "Gemini oturumu kabul edilmedi; CLI'da tekrar giriş yapın.");
                if (load.StatusCode == HttpStatusCode.Forbidden)
                    return Empty("forbidden", "Gemini kota servisi isteği reddetti (HTTP 403). Hesap erişimini kontrol edin.");
                if (load.StatusCode == HttpStatusCode.TooManyRequests)
                    return Empty("rate_limited", "Gemini kota servisi istek sıklığını sınırladı.");
                if (!IsSuccess(load.StatusCode) || load.Body is null)
                {
                    lastError = $"http_{(int)load.StatusCode}";
                    continue;
                }

                var loadRoot = load.Body.RootElement;
                if (loadRoot.ValueKind != JsonValueKind.Object)
                {
                    lastError = "schema_changed";
                    continue;
                }
                var plan = ReadTier(loadRoot, "paidTier") ?? ReadTier(loadRoot, "currentTier");
                var project = ReadProject(loadRoot);
                var body = project is null
                    ? new Dictionary<string, object>()
                    : new Dictionary<string, object> { ["project"] = project };
                var quotaResult = await ReadQuotaWindowsAsync(host, credential.AccessToken, body, fetchToken);
                if (quotaResult.ErrorCode is "reauth_required" or "forbidden" or "rate_limited")
                    return Empty(quotaResult.ErrorCode, quotaResult.Message ?? "Gemini kota isteği tamamlanamadı.");
                if (quotaResult.Windows.Count > 0)
                    return new ProviderSnapshot(Id, "Gemini", "ok", plan, DateTimeOffset.UtcNow,
                        $"Gemini/Antigravity OAuth → {host}/v1internal quota", quotaResult.Windows);

                lastError = quotaResult.ErrorCode ?? "quota_missing";
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                lastError = "timeout";
            }
            catch (HttpRequestException)
            {
                lastError = "network_error";
            }
            catch (JsonException)
            {
                lastError = "schema_changed";
            }
        }

        return Empty(lastError switch
        {
            "timeout" => "timeout",
            "network_error" => "network_error",
            "schema_changed" => "schema_changed",
            _ => "fetch_failed"
        }, lastError switch
        {
            "timeout" => "Gemini kota isteği zaman aşımına uğradı.",
            "network_error" => "Gemini kota servisine ulaşılamadı.",
            "schema_changed" => "Gemini kota yanıtının biçimi tanınmadı.",
            _ => "Gemini kota penceresi alınamadı."
        }, lastError);
    }

    private async Task<QuotaReadResult> ReadQuotaWindowsAsync(
        string host, string token, Dictionary<string, object> body, CancellationToken cancellationToken)
    {
        using (var summary = await PostAndParseAsync(host, "retrieveUserQuotaSummary", token, body, cancellationToken))
        {
            if (summary.StatusCode == HttpStatusCode.Unauthorized)
                return QuotaReadResult.Failure("reauth_required", "Gemini oturumu kabul edilmedi; CLI'da tekrar giriş yapın.");
            if (summary.StatusCode == HttpStatusCode.Forbidden)
                return QuotaReadResult.Failure("forbidden", "Gemini kota servisi isteği reddetti (HTTP 403). Hesap erişimini kontrol edin.");
            if (summary.StatusCode == HttpStatusCode.TooManyRequests)
                return QuotaReadResult.Failure("rate_limited", "Gemini kota servisi istek sıklığını sınırladı.");
            if (summary.Body is not null)
            {
                var mapped = MapSummary(summary.Body.RootElement);
                if (mapped.Count > 0) return QuotaReadResult.Success(mapped);
            }
        }

        using (var quota = await PostAndParseAsync(host, "retrieveUserQuota", token, body, cancellationToken))
        {
            if (quota.StatusCode == HttpStatusCode.Unauthorized)
                return QuotaReadResult.Failure("reauth_required", "Gemini oturumu kabul edilmedi; CLI'da tekrar giriş yapın.");
            if (quota.StatusCode == HttpStatusCode.Forbidden)
                return QuotaReadResult.Failure("forbidden", "Gemini kota servisi isteği reddetti (HTTP 403). Hesap erişimini kontrol edin.");
            if (quota.StatusCode == HttpStatusCode.TooManyRequests)
                return QuotaReadResult.Failure("rate_limited", "Gemini kota servisi istek sıklığını sınırladı.");
            if (quota.Body is not null)
            {
                var mapped = MapBuckets(quota.Body.RootElement);
                if (mapped.Count > 0) return QuotaReadResult.Success(mapped);
            }
        }

        using (var models = await PostAndParseAsync(host, "fetchAvailableModels", token, body, cancellationToken))
        {
            if (models.StatusCode == HttpStatusCode.Unauthorized)
                return QuotaReadResult.Failure("reauth_required", "Gemini oturumu kabul edilmedi; CLI'da tekrar giriş yapın.");
            if (models.StatusCode == HttpStatusCode.Forbidden)
                return QuotaReadResult.Failure("forbidden", "Gemini kota servisi isteği reddetti (HTTP 403). Hesap erişimini kontrol edin.");
            if (models.StatusCode == HttpStatusCode.TooManyRequests)
                return QuotaReadResult.Failure("rate_limited", "Gemini kota servisi istek sıklığını sınırladı.");
            if (models.Body is not null)
            {
                var mapped = MapModels(models.Body.RootElement);
                if (mapped.Count > 0) return QuotaReadResult.Success(mapped);
            }
        }

        return QuotaReadResult.Failure("quota_missing", "Gemini hesabı kota penceresi döndürmedi.");
    }

    private async Task<QuotaApiReply> PostAndParseAsync(
        string host, string method, string token, object body, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{host}/v1internal:{method}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.UserAgent.ParseAdd("antigravity/windows/amd64");
        request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        var expectedHost = new Uri(host).Host;
        var actualHost = response.RequestMessage?.RequestUri?.Host;
        if (!string.Equals(expectedHost, actualHost, StringComparison.OrdinalIgnoreCase))
            return new QuotaApiReply(HttpStatusCode.BadGateway, null);
        if (!IsSuccess(response.StatusCode))
            return new QuotaApiReply(response.StatusCode, null);

        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
        var document = await JsonDocument.ParseAsync(stream, cancellationToken: timeout.Token);
        return new QuotaApiReply(response.StatusCode, document);
    }

    private static List<UsageWindow> MapSummary(JsonElement root)
    {
        var result = new List<UsageWindow>();
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("groups", out var groups)
            || groups.ValueKind != JsonValueKind.Array) return result;

        var selected = groups.EnumerateArray()
            .Where(group => IsGemini(GroupIdentity(group)))
            .ToArray();
        foreach (var group in selected)
        {
            if (group.ValueKind != JsonValueKind.Object
                || !group.TryGetProperty("buckets", out var buckets)
                || buckets.ValueKind != JsonValueKind.Array) continue;
            foreach (var bucket in buckets.EnumerateArray())
            {
                var remaining = ReadRemainingFraction(bucket);
                if (remaining is null) continue;
                var used = (1 - remaining.Value) * 100;
                var label = CodexQuotaConnector.String(bucket, "displayName")
                    ?? CodexQuotaConnector.String(bucket, "bucketId")
                    ?? "Kota";
                var window = CodexQuotaConnector.String(bucket, "window") ?? string.Empty;
                var kind = ClassifyWindow($"{label} {window}");
                var groupLabel = selected.Length > 1
                    ? (CodexQuotaConnector.String(group, "displayName") ?? "Gemini") + " "
                    : string.Empty;
                result.Add(new UsageWindow(kind, groupLabel + NormalizeWindowLabel(kind, label), used,
                    remaining.Value * 100, ReadDate(bucket, "resetTime")));
            }
        }
        return result;
    }

    private static List<UsageWindow> MapBuckets(JsonElement root)
    {
        var result = new List<UsageWindow>();
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("buckets", out var buckets)
            || buckets.ValueKind != JsonValueKind.Array) return result;

        foreach (var bucket in buckets.EnumerateArray())
        {
            var id = CodexQuotaConnector.String(bucket, "modelId")
                ?? CodexQuotaConnector.String(bucket, "id")
                ?? CodexQuotaConnector.String(bucket, "displayName")
                ?? string.Empty;
            var displayName = CodexQuotaConnector.String(bucket, "displayName") ?? id;
            if (!IsGemini($"{id} {displayName}")) continue;
            var remaining = ReadRemainingFraction(bucket);
            if (remaining is null) continue;
            result.Add(new UsageWindow("quota", displayName, (1 - remaining.Value) * 100,
                remaining.Value * 100, ReadDate(bucket, "resetTime")));
        }
        return result;
    }

    private static List<UsageWindow> MapModels(JsonElement root)
    {
        var result = new List<UsageWindow>();
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("models", out var models)) return result;

        IEnumerable<(string? Key, JsonElement Value)> items = models.ValueKind switch
        {
            JsonValueKind.Array => models.EnumerateArray().Select(model => ((string?)null, model)).ToArray(),
            JsonValueKind.Object => models.EnumerateObject().Select(property => ((string?)property.Name, property.Value)).ToArray(),
            _ => Array.Empty<(string?, JsonElement)>()
        };

        foreach (var (key, model) in items)
        {
            if (model.ValueKind != JsonValueKind.Object || !model.TryGetProperty("quotaInfo", out var quota)) continue;
            var remaining = ReadRemainingFraction(quota);
            if (remaining is null) continue;
            var name = CodexQuotaConnector.String(model, "displayName")
                ?? CodexQuotaConnector.String(model, "model")
                ?? key
                ?? string.Empty;
            if (!IsGemini(name)) continue;
            result.Add(new UsageWindow("quota", name, (1 - remaining.Value) * 100,
                remaining.Value * 100, ReadDate(quota, "resetTime")));
        }
        return result;
    }

    private static double? ReadRemainingFraction(JsonElement element)
    {
        var value = GeminiNumber(element, "remainingFraction");
        if (value is null && element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty("quotaInfo", out var quotaInfo))
            value = GeminiNumber(quotaInfo, "remainingFraction");
        return value is >= 0 and <= 1 ? value : null;
    }

    private static double? GeminiNumber(JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(property, out var value)) return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) && double.IsFinite(number)) return number;
        if (value.ValueKind == JsonValueKind.String
            && double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out number)
            && double.IsFinite(number)) return number;
        return null;
    }

    private static DateTimeOffset? ReadDate(JsonElement element, string property)
    {
        var text = CodexQuotaConnector.String(element, property);
        return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date)
            ? date.ToUniversalTime()
            : null;
    }

    private static string? ReadTier(JsonElement root, string property)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(property, out var tier)) return null;
        if (tier.ValueKind == JsonValueKind.String) return tier.GetString();
        return CodexQuotaConnector.String(tier, "name") ?? CodexQuotaConnector.String(tier, "id");
    }

    private static string? ReadProject(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("cloudaicompanionProject", out var project)) return null;
        if (project.ValueKind == JsonValueKind.String) return project.GetString();
        return CodexQuotaConnector.String(project, "id")
            ?? CodexQuotaConnector.String(project, "projectId")
            ?? CodexQuotaConnector.String(project, "name");
    }

    private static string GroupIdentity(JsonElement group) => string.Join(" ", new[]
    {
        CodexQuotaConnector.String(group, "displayName"),
        CodexQuotaConnector.String(group, "name"),
        CodexQuotaConnector.String(group, "id"),
        CodexQuotaConnector.String(group, "groupId")
    }.Where(value => !string.IsNullOrWhiteSpace(value)));

    private static bool IsGemini(string value) => value.Contains("gemini", StringComparison.OrdinalIgnoreCase);

    private static string ClassifyWindow(string value)
    {
        if (value.Contains("5h", StringComparison.OrdinalIgnoreCase)
            || value.Contains("5-hour", StringComparison.OrdinalIgnoreCase)
            || value.Contains("5 hour", StringComparison.OrdinalIgnoreCase)) return "five_hour";
        if (value.Contains("week", StringComparison.OrdinalIgnoreCase)) return "weekly";
        if (value.Contains("day", StringComparison.OrdinalIgnoreCase)) return "daily";
        return "quota";
    }

    private static string NormalizeWindowLabel(string kind, string fallback) => kind switch
    {
        "five_hour" => "5 saat",
        "weekly" => "Haftalık",
        "daily" => "Günlük",
        _ => fallback
    };

    private static bool IsSuccess(HttpStatusCode status) =>
        (int)status is >= 200 and < 300;

    private ProviderSnapshot Empty(string state, string message, string? error = null) =>
        new(Id, "Gemini", state, null, DateTimeOffset.UtcNow, "Gemini/Antigravity local OAuth",
            Array.Empty<UsageWindow>(), error ?? state, message);

    private sealed record QuotaReadResult(IReadOnlyList<UsageWindow> Windows, string? ErrorCode = null, string? Message = null)
    {
        public static QuotaReadResult Success(IReadOnlyList<UsageWindow> windows) => new(windows);
        public static QuotaReadResult Failure(string errorCode, string message) => new(Array.Empty<UsageWindow>(), errorCode, message);
    }
}
