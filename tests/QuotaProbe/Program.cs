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
