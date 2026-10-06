using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodexUsageMonitor;

namespace CodexUsageMonitor.Windows;

internal sealed class LowQuotaNotificationPolicy
{
    private const int StoreVersion = 1;
    private const int NotificationRetentionDays = 90;
    private static readonly int[] QuotaThresholds = [20, 10];
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly string _path = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CodexUsageMonitor", "settings", "low-quota-notifications.json");
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, DateTimeOffset> _sent = new(StringComparer.Ordinal);
    private bool _loaded;
    private bool _needsSave;

    public bool Enabled { get; private set; } = true;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    public async Task SetEnabledAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
            var previous = Enabled;
            Enabled = enabled;
            try { await SaveAsync(cancellationToken).ConfigureAwait(false); }
            catch
            {
                Enabled = previous;
                throw;
            }
        }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<LowQuotaNotice>> GetNewNoticesAsync(
        UsageSnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
            if (!Enabled) return Array.Empty<LowQuotaNotice>();

            var now = DateTimeOffset.UtcNow;
            var previousCount = _sent.Count;
            foreach (var key in _sent.Where(item => item.Value < now.AddDays(-NotificationRetentionDays))
                         .Select(item => item.Key).ToArray())
                _sent.Remove(key);

            var notices = new List<LowQuotaNotice>();
            foreach (var provider in snapshot.Providers.Where(item => item.State is "ok" or "partial"))
            foreach (var window in provider.Windows)
            {
                if (!window.ResetsAt.HasValue) continue;
                var resetAt = window.ResetsAt.Value;
                var remaining = window.RemainingPercent
                    ?? (window.UsedPercent is { } used ? 100 - used : null);
                if (!remaining.HasValue) continue;
                var value = remaining.Value;
                if (!double.IsFinite(value)) continue;

                value = Math.Clamp(value, 0, 100);
                foreach (var threshold in QuotaThresholds)
                {
                    if (value > threshold) continue;

                    var key = HashKey($"{provider.Id}|{window.Kind}|{resetAt.UtcDateTime.Ticks}|{threshold}");
                    if (!_sent.TryAdd(key, now)) continue;
                    notices.Add(new LowQuotaNotice(provider.Name, window.Label, (int)Math.Round(value), threshold));
                }
            }

            if (notices.Count > 0 || _sent.Count != previousCount)
                _needsSave = true;
            if (_needsSave)
            {
                try { await SaveAsync(cancellationToken).ConfigureAwait(false); }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception)
                {
                    // Keep the in-memory keys so a storage error cannot show a balloon every poll.
                }
            }
            return notices;
        }
        finally { _gate.Release(); }
    }

    private async Task EnsureLoadedAsync(CancellationToken cancellationToken)
    {
        if (_loaded) return;
        _loaded = true;
        try
        {
            await using var stream = File.OpenRead(_path);
            var document = await JsonSerializer.DeserializeAsync<StoreDocument>(stream, JsonOptions, cancellationToken)
                .ConfigureAwait(false);
            if (document is not { Version: StoreVersion }) return;
            Enabled = document.Enabled;
            foreach (var item in document.Sent ?? [])
            {
                if (item is not null && !string.IsNullOrWhiteSpace(item.Key)
                    && item.RecordedAt >= DateTimeOffset.UtcNow.AddDays(-NotificationRetentionDays))
                    _sent[item.Key] = item.RecordedAt;
            }
        }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
        catch (JsonException) { }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        catch (SecurityException) { }
    }

    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temporary = _path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                4096, useAsync: true))
            await JsonSerializer.SerializeAsync(stream, new StoreDocument
                {
                    Version = StoreVersion,
                    Enabled = Enabled,
                    Sent = _sent.Select(item => new SentNotice(item.Key, item.Value)).ToList()
                }, JsonOptions, cancellationToken).ConfigureAwait(false);
            File.Move(temporary, _path, overwrite: true);
            _needsSave = false;
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static string HashKey(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private sealed class StoreDocument
    {
        public int Version { get; set; }
        public bool Enabled { get; set; } = true;
        public List<SentNotice> Sent { get; set; } = [];
    }

    private sealed record SentNotice(string Key, DateTimeOffset RecordedAt);
}

internal sealed record LowQuotaNotice(string Provider, string Window, int RemainingPercent, int ThresholdPercent);
