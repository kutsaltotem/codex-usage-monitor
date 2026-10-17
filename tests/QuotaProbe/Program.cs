using CodexUsageMonitor;
using System.Text.Json;
var fixture = """{"response":{"groups":[{"displayName":"Gemini Models","buckets":[{"displayName":"Weekly Limit Remaining","remainingFraction":0,"resetTime":"2026-10-13T12:00:00Z"},{"displayName":"Five Hour Limit Remaining","remainingFraction":1}]},{"displayName":"Claude and GPT models","buckets":[{"displayName":"Weekly Limit Remaining","remainingFraction":0.42}]},{"displayName":"Unknown","buckets":[{"remainingFraction":1}]}]}}""";
using var json = JsonDocument.Parse(fixture);
var parsed = AntigravityLocalQuota.Parse(json.RootElement);
if (parsed.Count != 3 || parsed[0].RemainingPercent != 0 || parsed[1].RemainingPercent != 100 || parsed[2].RemainingPercent != 42 || parsed[2].Kind != "antigravity_weekly") throw new Exception("Quota grouping/zero mapping failed.");
Console.WriteLine("Quota parsing: passed (zero/full/secondary groups).");
if (args.Contains("--live")) {
 var live = await AntigravityLocalQuota.FetchAsync(CancellationToken.None);
 if (live is null) throw new Exception("Running Antigravity quota unavailable.");
 Console.WriteLine(JsonSerializer.Serialize(new { live.State, live.Source, live.Windows }));
}

var temporary = Path.Combine(Path.GetTempPath(), "usage-quota-test-" + Guid.NewGuid());
var store = new QuotaSnapshotStore(temporary);
var stamp = DateTimeOffset.UtcNow;
var provider = new ProviderSnapshot("gemini", "Gemini", "ok", null, stamp, "fixture", parsed);
await store.SaveAsync(new UsageSnapshot(stamp, new[] { provider }));
var old = new QuotaHistoryEntry(stamp.AddHours(-2), "gemini", parsed);
await File.AppendAllTextAsync(Path.Combine(temporary, "history", "quota-history.jsonl"),
    JsonSerializer.Serialize(old, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }) + "\n");
var history = await store.LoadHistoryAsync();
if (history.Count != 2 || history.Any(entry => entry.Id != "gemini" || entry.Windows.Count != 3))
    throw new Exception("Legacy/current Gemini quota history failed.");
Console.WriteLine("Quota history: compact + legacy multiline records passed; Gemini retained.");
// The tiny synthetic fixture remains under the system temp directory, never the user's history.

// Claude connector: synthetic credentials + canned usage response; nothing leaves the machine.
var claudeHome = Path.Combine(Path.GetTempPath(), "usage-claude-test-" + Guid.NewGuid());
Directory.CreateDirectory(claudeHome);
Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", claudeHome);
var expiry = DateTimeOffset.UtcNow.AddHours(5).ToUnixTimeMilliseconds();
await File.WriteAllTextAsync(Path.Combine(claudeHome, ".credentials.json"),
    "{\"claudeAiOauth\":{\"accessToken\":\"synthetic-test-token\",\"refreshToken\":\"x\",\"expiresAt\":" + expiry + ",\"subscriptionType\":\"max\",\"rateLimitTier\":\"default_claude_max_5x\"}}");
const string usage = """{"five_hour":{"utilization":11.0,"resets_at":"2026-10-17T18:20:00.578880+00:00"},"seven_day":{"utilization":2.0,"resets_at":"2026-10-20T05:00:00+00:00"},"seven_day_opus":null,"extra_usage":{"is_enabled":true,"utilization":40.5,"resets_at":null},"seven_day_breakdown":{"rows":[{"key":"claude_code","display_name":"Claude Code","percent":92},{"key":"chat","display_name":"Chats","percent":0},{"key":"cowork","display_name":"Cowork","percent":8}]}}""";
using var claudeHttp = new HttpClient(new FakeHandler(usage));
var claude = await new ClaudeQuotaConnector(claudeHttp).FetchAsync(CancellationToken.None);
if (claude.State != "ok" || claude.Plan != "Max 5x") throw new Exception($"Claude plan/state failed: {claude.State} {claude.Plan}");
if (claude.Windows.Count != 3 || claude.Windows[0].Kind != "five_hour" || claude.Windows[2].Kind != "extra_usage")
    throw new Exception("Claude windows (5h, weekly, extra usage) failed.");
if (LocalCredentialReader.ClaudePlanLabel("pro", "default_claude_ai") != "Pro" || LocalCredentialReader.ClaudePlanLabel(null, null) is not null)
    throw new Exception("Claude plan label failed.");
File.Delete(Path.Combine(claudeHome, ".credentials.json"));
var signedOut = await new ClaudeQuotaConnector(claudeHttp).FetchAsync(CancellationToken.None);
if (signedOut.State != "not_signed_in") throw new Exception("Claude signed-out state failed.");
Console.WriteLine("Claude connector: plan, windows, extra usage, signed-out passed.");

sealed class FakeHandler(string body) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
            RequestMessage = request
        });
}
