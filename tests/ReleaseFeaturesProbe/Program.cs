using System.IO;
using System.Globalization;
using CodexUsageMonitor;
using CodexUsageMonitor.Windows;

// Synthetic data only: temp files, no credentials, no network.
var temp = Path.Combine(Path.GetTempPath(), "usage-release-probe-" + Guid.NewGuid());
Directory.CreateDirectory(temp);
var now = DateTimeOffset.UtcNow;

UsageSnapshot Snap(double used, DateTimeOffset? resets, string state = "ok") => new(now, new[]
{
    new ProviderSnapshot("chatgpt-codex", "ChatGPT / Codex", state, "plus", now, "demo",
        new[] { new UsageWindow("five_hour", "5 saat", used, 100 - used, resets) })
});

// --- Reset notifications ---
var policy = new ResetNotificationPolicy(Path.Combine(temp, "reset.json"));
await policy.InitializeAsync();
if ((await policy.GetNewNoticesAsync(Snap(80, now.AddHours(1)))).Count != 0) throw new Exception("First reading must not notify.");
if ((await policy.GetNewNoticesAsync(Snap(85, now.AddHours(1).AddSeconds(2)))).Count != 0) throw new Exception("Same window (jitter) must not notify.");
var notices = await policy.GetNewNoticesAsync(Snap(3, now.AddHours(6)));
if (notices.Count != 1 || notices[0].Provider != "Codex" || notices[0].PreviousUsedPercent != 85 || notices[0].CurrentUsedPercent != 3)
    throw new Exception("Reset after heavy use must notify once.");
if ((await policy.GetNewNoticesAsync(Snap(4, now.AddHours(6)))).Count != 0) throw new Exception("A reset must be announced only once.");
// Light use window rolling over stays silent.
var quiet = new ResetNotificationPolicy(Path.Combine(temp, "quiet.json"));
await quiet.GetNewNoticesAsync(Snap(10, now.AddHours(1)));
if ((await quiet.GetNewNoticesAsync(Snap(0, now.AddHours(6)))).Count != 0) throw new Exception("Idle window reset must stay silent.");
// Disabled policy keeps state but stays silent; setting survives a restart.
await policy.SetEnabledAsync(false);
await policy.GetNewNoticesAsync(Snap(90, now.AddHours(6)));
if ((await policy.GetNewNoticesAsync(Snap(1, now.AddHours(12)))).Count != 0) throw new Exception("Disabled policy must not notify.");
var reloaded = new ResetNotificationPolicy(Path.Combine(temp, "reset.json"));
await reloaded.InitializeAsync();
if (reloaded.Enabled) throw new Exception("Enabled flag not persisted.");
Console.WriteLine("Reset notifications: first/jitter/once/idle/disabled/persisted passed.");

// --- CSV export ---
var separator = CsvExporter.Separator;
var records = new[]
{
    new QuotaHistoryRecord(now, "claude", "Claude", "Pro", "src", new[] { new UsageWindow("five_hour", "5 saat", 11, 89, now.AddHours(2)) }),
    new QuotaHistoryRecord(now.AddHours(-1), "gemini", "Gemini", null, "src", new[] { new UsageWindow("weekly", "Gemini · \"Hafta\"", 0, 100, null) })
};
var quotaPath = Path.Combine(temp, "kota.csv");
if (await CsvExporter.WriteQuotaAsync(quotaPath, records) != 2) throw new Exception("Quota row count.");
var bytes = await File.ReadAllBytesAsync(quotaPath);
if (bytes[0] != 0xEF || bytes[1] != 0xBB || bytes[2] != 0xBF) throw new Exception("CSV must start with a UTF-8 BOM for Excel.");
var lines = (await File.ReadAllTextAsync(quotaPath)).Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r')).ToArray();
if (lines.Length != 3 || !lines[0].Contains("Sağlayıcı") || !lines[1].Contains("Gemini") || !lines[2].Contains("Claude") || !lines[2].Contains("Pro"))
    throw new Exception("Quota CSV rows/order wrong:\n" + string.Join("\n", lines));
if (!lines[1].Contains("\"Gemini · \"\"Hafta\"\"\"")) throw new Exception("Quote escaping failed: " + lines[1]);
var tokenPath = Path.Combine(temp, "token.csv");
await CsvExporter.WriteTokensAsync(tokenPath, new[] { new TokenUsageRow("claude", new DateOnly(2026, 10, 17), "claude-sonnet-5-5", 10, 20, 3, 4, 0, 0, 37) });
var tokenLines = (await File.ReadAllTextAsync(tokenPath)).Split('\n', StringSplitOptions.RemoveEmptyEntries);
if (tokenLines.Length != 2 || !tokenLines[1].StartsWith("2026-10-17" + separator + "Claude") || !tokenLines[1].TrimEnd().EndsWith("37")) throw new Exception("Token CSV wrong: " + tokenLines[1]);
if (CsvExporter.Escape("=1+1", separator) != "'=1+1") throw new Exception("Formula guard failed.");
Console.WriteLine($"CSV export: BOM, order, quoting, token rows, formula guard passed (separator '{separator}').");

// --- Theme: light palette flips lightness, keeps accents readable ---
Theme.ForceForPreview(light: true);
var canvas = Theme.Brush("#111923").Color; var text = Theme.Brush("#F1F4F8").Color;
if (canvas.R < 200 || text.R > 60) throw new Exception("Light theme must produce light surfaces and dark text.");
Theme.ForceForPreview(light: false);
if (Theme.Brush("#111923").Color.R > 30) throw new Exception("Dark theme must keep the original palette.");
Console.WriteLine("Theme palette: dark original, light derived passed.");
Directory.Delete(temp, true);
