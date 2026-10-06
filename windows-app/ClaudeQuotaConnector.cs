using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace CodexUsageMonitor;

/// <summary>
/// Reads Claude Code's existing claude.ai OAuth login and the usage endpoint used by
/// Claude Code's /usage screen. Anthropic does not document this endpoint as a public API.
/// The connector never refreshes or writes credentials.
/// </summary>
public sealed class ClaudeQuotaConnector(HttpClient http) : IQuotaConnector
{
    private const string UsageUrl = "https://api.anthropic.com/api/oauth/usage";
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);

    public string Id => "claude";

    public async Task<ProviderSnapshot> FetchAsync(CancellationToken cancellationToken)
    {
        var credential = LocalCredentialReader.ReadClaude();
        if (credential is null)
            return Empty("not_signed_in", "Claude Code abonelik oturumu bulunamadı. Claude Code'da claude.ai hesabınızla giriş yapın.");
        if (credential.ExpiresAt is { } expiry && expiry <= DateTimeOffset.UtcNow.AddSeconds(30))
            return Empty("reauth_required", "Claude Code oturumu süresi dolmuş. Claude Code'u açıp tekrar giriş yapın.");

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(RequestTimeout);
            using var request = new HttpRequestMessage(HttpMethod.Get, UsageUrl);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential.AccessToken);
            request.Headers.TryAddWithoutValidation("anthropic-beta", "oauth-2025-04-20");
            request.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.UserAgent.ParseAdd("CodexUsageMonitor/0.1");

            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                .ConfigureAwait(false);
            if (response.RequestMessage?.RequestUri is { } responseUri
                && !string.Equals(responseUri.Host, "api.anthropic.com", StringComparison.OrdinalIgnoreCase))
                return Empty("redirect_blocked", "Claude kota isteği beklenmeyen bir adrese yönlendirildi.");
            if (response.StatusCode == HttpStatusCode.Unauthorized)
                return Empty("reauth_required", "Claude oturumu kabul edilmedi. Claude Code'da tekrar giriş yapın.");
            if (response.StatusCode == HttpStatusCode.Forbidden)
                return Empty("forbidden", "Claude kota servisi isteği reddetti (HTTP 403). Oturum erişimi veya kota bağlantısı değişmiş olabilir.");
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
                return Empty("rate_limited", "Claude kota servisi istek sıklığını sınırladı.");
            if (!response.IsSuccessStatusCode)
                return Empty("fetch_failed", $"Claude kota yanıtı alınamadı (HTTP {(int)response.StatusCode}).");

            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: timeout.Token).ConfigureAwait(false);
            var windows = MapWindows(document.RootElement);
            if (windows.Count == 0)
                return Empty("schema_changed", "Claude kota yanıtında tanınan kullanım penceresi bulunamadı.");

            var hasPrimaryWindows = windows.Any(window => window.Kind == "five_hour")
                && windows.Any(window => window.Kind == "weekly");
            return new ProviderSnapshot(
                Id,
                "Claude",
                hasPrimaryWindows ? "ok" : "partial",
                null,
                DateTimeOffset.UtcNow,
                "Claude Code OAuth → Anthropic usage endpoint",
                windows,
                hasPrimaryWindows ? null : "quota_partial",
                hasPrimaryWindows ? null : "5 saatlik veya haftalık ana pencere yanıt içinde yok.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Empty("timeout", "Claude kota isteği zaman aşımına uğradı.");
        }
        catch (HttpRequestException)
        {
            return Empty("network_error", "Claude kota servisine ulaşılamadı.");
        }
        catch (JsonException)
        {
            return Empty("schema_changed", "Claude kota yanıtının biçimi tanınmadı.");
        }
    }

    private static List<UsageWindow> MapWindows(JsonElement root)
    {
        var windows = new List<UsageWindow>();
        if (root.ValueKind != JsonValueKind.Object) return windows;

        if (root.TryGetProperty("five_hour", out var fiveHour))
            AddWindow(windows, fiveHour, "five_hour", "5 saat");

        if (root.TryGetProperty("seven_day", out var weekly))
            AddWindow(windows, weekly, "weekly", "Haftalık");

        foreach (var property in root.EnumerateObject()
                     .Where(property => property.Name.StartsWith("seven_day_", StringComparison.Ordinal))
                     .OrderBy(property => property.Name, StringComparer.Ordinal))
        {
            var suffix = property.Name["seven_day_".Length..];
            var model = suffix switch
            {
                "sonnet" => "Sonnet",
                "opus" => "Opus",
                "haiku" => "Haiku",
                _ => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(suffix.Replace('_', ' '))
            };
            AddWindow(windows, property.Value, $"weekly_{suffix}", $"Haftalık · {model}");
        }

        return windows;
    }

    private static void AddWindow(List<UsageWindow> windows, JsonElement value, string kind, string label)
    {
        if (value.ValueKind != JsonValueKind.Object
            || !TryReadNumber(value, "utilization", out var used)
            || !double.IsFinite(used))
            return;

        used = Math.Clamp(used, 0, 100);
        DateTimeOffset? resetsAt = null;
        if (value.TryGetProperty("resets_at", out var resetValue)
            && resetValue.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(resetValue.GetString(), CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsedReset))
            resetsAt = parsedReset;

        windows.Add(new UsageWindow(kind, label, used, 100 - used, resetsAt));
    }

    private static bool TryReadNumber(JsonElement element, string property, out double number)
    {
        number = 0;
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(property, out var value)) return false;
        if (value.ValueKind == JsonValueKind.Number) return value.TryGetDouble(out number);
        return value.ValueKind == JsonValueKind.String
            && double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out number);
    }

    private static ProviderSnapshot Empty(string state, string message) => new(
        "claude", "Claude", state, null, DateTimeOffset.UtcNow,
        "claude-code-credentials", Array.Empty<UsageWindow>(), state, message);
}
