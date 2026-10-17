using System.Security;
using System.Text.Json;
using CodexUsageMonitor;

namespace CodexUsageMonitor.Windows;

/// <summary>
/// Notices when a quota window that was well used has started over ("Codex 5 saatlik kotan sıfırlandı").
/// Only meaningful windows (previously at least <see cref="MinimumPreviousUsedPercent"/> used) are announced,
/// so idle windows rolling over never produce noise.
/// </summary>
internal sealed class ResetNotificationPolicy
{
    internal const double MinimumPreviousUsedPercent = 50;
    private static readonly TimeSpan ResetAdvanceTolerance = TimeSpan.FromMinutes(5);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, Seen> _last = new(StringComparer.Ordinal);
    private bool _loaded;

    public ResetNotificationPolicy(string? path = null) => _path = path ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CodexUsageMonitor", "settings", "reset-notifications.json");

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
            catch { Enabled = previous; throw; }
        }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<ResetNotice>> GetNewNoticesAsync(UsageSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
            var notices = new List<ResetNotice>();
            var changed = false;
            var now = DateTimeOffset.UtcNow;
            foreach (var provider in snapshot.Providers.Where(item => item.State is "ok" or "partial"))
            foreach (var window in provider.Windows)
            {
                var used = window.UsedPercent ?? (window.RemainingPercent is { } remaining ? 100 - remaining : null);
                if (used is not { } usedValue || !double.IsFinite(usedValue)) continue;
                var key = provider.Id + "|" + window.Kind;
                var current = new Seen(window.ResetsAt, Math.Clamp(usedValue, 0, 100));

                if (_last.TryGetValue(key, out var previous) && previous.UsedPercent >= MinimumPreviousUsedPercent)
                {
                    var resetAdvanced = previous.ResetsAt is { } before && current.ResetsAt is { } after
                        && after - before > ResetAdvanceTolerance;
                    var lapsedAndDropped = previous.ResetsAt is { } lapsed && lapsed <= now
                        && current.UsedPercent <= previous.UsedPercent - 20;
                    if (resetAdvanced || lapsedAndDropped)
                        notices.Add(new ResetNotice(provider.Name == "ChatGPT / Codex" ? "Codex" : provider.Name,
                            window.Label, (int)Math.Round(previous.UsedPercent), (int)Math.Round(current.UsedPercent)));
                }

                // Keep the highest-use reading of the running window so a drop right before the reset cannot hide it.
                if (!_last.TryGetValue(key, out var stored) || stored != current)
                {
                    var sameWindow = stored is not null && current.ResetsAt is { } c && stored.ResetsAt is { } s
                        && Math.Abs((c - s).TotalMinutes) <= ResetAdvanceTolerance.TotalMinutes;
                    _last[key] = sameWindow && stored!.UsedPercent > current.UsedPercent ? current with { UsedPercent = stored.UsedPercent } : current;
                    changed = true;
                }
            }

            if (changed)
            {
                try { await SaveAsync(cancellationToken).ConfigureAwait(false); }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception) { /* In-memory state still prevents repeats this session. */ }
            }
            return Enabled ? notices : Array.Empty<ResetNotice>();
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
            var document = await JsonSerializer.DeserializeAsync<StoreDocument>(stream, JsonOptions, cancellationToken).ConfigureAwait(false);
            if (document is null) return;
            Enabled = document.Enabled;
            foreach (var (key, value) in document.Last ?? []) _last[key] = value;
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
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, useAsync: true))
                await JsonSerializer.SerializeAsync(stream, new StoreDocument { Enabled = Enabled, Last = new Dictionary<string, Seen>(_last) },
                    JsonOptions, cancellationToken).ConfigureAwait(false);
            File.Move(temporary, _path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private sealed class StoreDocument
    {
        public bool Enabled { get; set; } = true;
        public Dictionary<string, Seen>? Last { get; set; }
    }

    internal sealed record Seen(DateTimeOffset? ResetsAt, double UsedPercent);
}

internal sealed record ResetNotice(string Provider, string Window, int PreviousUsedPercent, int CurrentUsedPercent);
