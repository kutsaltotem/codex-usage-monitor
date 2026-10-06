using System.Text.Json;

namespace CodexUsageMonitor;

public sealed class QuotaSnapshotStore
{
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly string _root;
    private readonly string _snapshotPath;
    private readonly string _historyPath;
    private DateTime _historyPrunedUtcDate = DateTime.MinValue;

    public QuotaSnapshotStore(string? root = null)
    {
        _root = root ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexUsageMonitor");
        _snapshotPath = Path.Combine(_root, "cache", "snapshot.json");
        _historyPath = Path.Combine(_root, "history", "quota-history.jsonl");
    }

    public async Task<UsageSnapshot?> LoadLatestAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await using var stream = File.OpenRead(_snapshotPath);
            return await JsonSerializer.DeserializeAsync<UsageSnapshot>(stream, _json, cancellationToken);
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
        catch (JsonException) { return null; }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    public async Task SaveAsync(UsageSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_snapshotPath)!);
        var temporary = _snapshotPath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 16_384, useAsync: true))
                await JsonSerializer.SerializeAsync(stream, snapshot, _json, cancellationToken);
            File.Move(temporary, _snapshotPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }

        var freshProviders = snapshot.Providers
            .Where(p => (p.State is "ok" or "partial") && p.Windows.Count > 0)
            .ToArray();
        if (freshProviders.Length == 0) return;
        Directory.CreateDirectory(Path.GetDirectoryName(_historyPath)!);
        await using (var history = new FileStream(_historyPath, FileMode.Append, FileAccess.Write, FileShare.Read, 16_384, useAsync: true))
        {
            foreach (var provider in freshProviders)
            {
                var line = JsonSerializer.Serialize(new
                {
                    snapshot.GeneratedAt,
                    provider.Id,
                    provider.Name,
                    provider.Plan,
                    provider.Source,
                    provider.Windows
                }, _json);
                var bytes = System.Text.Encoding.UTF8.GetBytes(line + "\n");
                await history.WriteAsync(bytes, cancellationToken);
            }
        }
        await PruneHistoryIfNeededAsync(cancellationToken);
    }

    private async Task PruneHistoryIfNeededAsync(CancellationToken cancellationToken)
    {
        var today = DateTime.UtcNow.Date;
        if (_historyPrunedUtcDate == today) return;
        if (!File.Exists(_historyPath))
        {
            _historyPrunedUtcDate = today;
            return;
        }

        var cutoff = DateTimeOffset.UtcNow.AddDays(-365);
        var temporary = _historyPath + ".prune-" + Guid.NewGuid().ToString("N");
        try
        {
            await using (var input = new StreamReader(new FileStream(_historyPath, FileMode.Open, FileAccess.Read, FileShare.Read, 16_384, useAsync: true)))
            await using (var output = new StreamWriter(new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 16_384, useAsync: true)))
            {
                while (await input.ReadLineAsync(cancellationToken) is { } line)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (ShouldKeepHistoryLine(line, cutoff))
                        await output.WriteLineAsync(line.AsMemory(), cancellationToken);
                }
            }
            File.Move(temporary, _historyPath, overwrite: true);
            _historyPrunedUtcDate = today;
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private bool ShouldKeepHistoryLine(string line, DateTimeOffset cutoff)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            if (!document.RootElement.TryGetProperty("generatedAt", out var value)
                || value.ValueKind != JsonValueKind.String
                || !DateTimeOffset.TryParse(value.GetString(), out var capturedAt))
                return true;
            return capturedAt >= cutoff;
        }
        catch (JsonException)
        {
            return true;
        }
    }
}
