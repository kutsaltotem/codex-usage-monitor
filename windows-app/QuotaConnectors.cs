using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace CodexUsageMonitor;

public sealed class CodexQuotaConnector(HttpClient http) : IQuotaConnector
{
    private const string UsageUrl = "https://chatgpt.com/backend-api/wham/usage";
    public string Id => "chatgpt-codex";

    public async Task<ProviderSnapshot> FetchAsync(CancellationToken cancellationToken)
    {
        var credential = LocalCredentialReader.ReadCodex();
        if (credential is null)
            return Empty("not_signed_in", "Codex uygulama oturumu bulunamadı. Windows Codex uygulamasında ChatGPT hesabınızla giriş yapın.", "codex-local-session");
        if (credential.ApiKeyOnly)
            return Empty("api_key_auth_unsupported", "API key oturumu ChatGPT abonelik kotasını vermez.", "codex-local-session");

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            using var request = new HttpRequestMessage(HttpMethod.Get, UsageUrl);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential.AccessToken);
            request.Headers.UserAgent.ParseAdd("Windows-Usage-Monitor/0.1");
            if (!string.IsNullOrWhiteSpace(credential.AccountId))
                request.Headers.TryAddWithoutValidation("ChatGPT-Account-Id", credential.AccountId);

            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (response.RequestMessage?.RequestUri is { } responseUri
                && !string.Equals(responseUri.Host, "chatgpt.com", StringComparison.OrdinalIgnoreCase))
                return Empty("redirect_blocked", "Kota isteği beklenmeyen bir adrese yönlendirildi.", "codex-local-session");
            if (response.StatusCode == HttpStatusCode.Unauthorized)
                return Empty("reauth_required", "Codex oturumu yenilenmeli; Codex uygulamasında tekrar giriş yapın.", "codex-local-session");
            if (response.StatusCode == HttpStatusCode.Forbidden)
                return Empty("forbidden", "Codex kota servisi isteği reddetti (HTTP 403). Hesap erişimini veya geçici servis engelini kontrol edin.", "codex-local-session");
            if ((int)response.StatusCode == 429)
                return Empty("rate_limited", "Sağlayıcı istek sıklığını sınırladı.", "codex-local-session");
            if (!response.IsSuccessStatusCode)
                return Empty("fetch_failed", $"Kota yanıtı alınamadı (HTTP {(int)response.StatusCode}).", "codex-local-session");

            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: timeout.Token);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return Empty("schema_changed", "Codex kota yanıtının biçimi tanınmadı.", "codex-local-session");
            var plan = String(root, "plan_type");
            var windows = new List<UsageWindow>();
            if (root.TryGetProperty("rate_limit", out var rateLimit) && rateLimit.ValueKind == JsonValueKind.Object)
            {
                AddCodexWindow(windows, rateLimit, "primary_window");
                AddCodexWindow(windows, rateLimit, "secondary_window");
            }
            if (root.TryGetProperty("additional_rate_limits", out var extra) && extra.ValueKind == JsonValueKind.Array)
            {
                foreach (var limit in extra.EnumerateArray())
                    if (limit.ValueKind == JsonValueKind.Object && limit.TryGetProperty("primary_window", out var window))
                        AddCodexWindowValue(windows, window, String(limit, "limit_name") ?? String(limit, "name") ?? String(limit, "id") ?? "Ek kota");
            }

            var state = windows.Count == 0 ? "partial" : "ok";
            var message = windows.Count == 0 ? "Yanıt kota penceresi içermedi." : null;
            return new ProviderSnapshot(Id, "ChatGPT / Codex", state, plan, DateTimeOffset.UtcNow,
                "Codex app OAuth → ChatGPT usage endpoint", windows, state == "partial" ? "quota_missing" : null, message);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Empty("timeout", "Kota isteği zaman aşımına uğradı.", "codex-local-session");
        }
        catch (HttpRequestException)
        {
            return Empty("network_error", "Kota servisine ulaşılamadı.", "codex-local-session");
        }
        catch (JsonException)
        {
            return Empty("schema_changed", "Kota yanıtının biçimi tanınmadı.", "codex-local-session");
        }
    }

    private static void AddCodexWindow(List<UsageWindow> windows, JsonElement parent, string key)
    {
        if (parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(key, out var value))
            AddCodexWindowValue(windows, value, key);
    }

    private static void AddCodexWindowValue(List<UsageWindow> windows, JsonElement value, string fallbackLabel)
    {
        var used = Number(value, "used_percent");
        var remaining = Number(value, "remaining_percent");
        if (used is null && remaining is not null) used = 100 - remaining.Value;
        if (used is null) return;
        used = Math.Clamp(used.Value, 0, 100);

        var seconds = Number(value, "limit_window_seconds");
        var kind = seconds is >= 14_400 and <= 21_600 ? "five_hour"
            : seconds is >= 82_800 and <= 90_000 ? "daily"
            : seconds is >= 518_400 and <= 691_200 ? "weekly"
            : "unknown";
        var reset = EpochDate(value, "reset_at");
        if (reset is null && Number(value, "reset_after_seconds") is { } after)
            reset = DateTimeOffset.UtcNow.AddSeconds(Math.Max(0, after));

        windows.Add(new UsageWindow(kind, kind switch
        {
            "five_hour" => "5 saat",
            "daily" => "Günlük",
            "weekly" => "Haftalık",
            _ => fallbackLabel
        }, used, 100 - used, reset));
    }

    private ProviderSnapshot Empty(string state, string message, string source) =>
        new(Id, "ChatGPT / Codex", state, null, DateTimeOffset.UtcNow, source, Array.Empty<UsageWindow>(), state, message);

    internal static string? String(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    internal static double? Number(JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(property, out var value)) return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) && double.IsFinite(number)) return number;
        if (value.ValueKind == JsonValueKind.String && double.TryParse(value.GetString(), CultureInfo.InvariantCulture, out number) && double.IsFinite(number)) return number;
        return null;
    }

    internal static DateTimeOffset? EpochDate(JsonElement element, string property)
    {
        var seconds = Number(element, property);
        if (seconds is null) return null;
        try { return DateTimeOffset.FromUnixTimeSeconds((long)seconds.Value); }
        catch (ArgumentOutOfRangeException) { return null; }
    }
}
