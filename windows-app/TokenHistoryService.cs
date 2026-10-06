using System.Text.Json;

namespace CodexUsageMonitor;

/// <summary>One local-day/model aggregate normalized for the token history view.</summary>
public sealed record TokenUsageRow(
    string ProviderId,
    DateOnly LocalDate,
    string Model,
    long InputTokens,
    long OutputTokens,
    long CacheReadTokens,
    long CacheWriteTokens,
    long ThoughtsTokens,
    long ToolTokens,
    long TotalTokens);

public sealed record TokenHistorySettings(bool CodexEnabled, bool GeminiEnabled, bool ClaudeEnabled = false, int RetentionDays = 90)
{
    public bool AnyEnabled => CodexEnabled || GeminiEnabled || ClaudeEnabled;
}

public sealed record TokenHistorySnapshot(
    DateTimeOffset CollectedAt,
    TokenHistorySettings Settings,
    IReadOnlyList<TokenUsageRow> Rows,
    string? Message = null);

/// <summary>
/// Coordinates opt-in local token history. Quota polling never invokes this service.
/// Raw CLI log lines are parsed by the readers and are not persisted by this coordinator.
/// </summary>
public sealed class TokenHistoryService : IAsyncDisposable
{
    private const int SettingsVersion = 1;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly string _dataRoot;
    private readonly string _settingsPath;
    private readonly CodexTokenHistoryReader _codexReader;
    private readonly GeminiTokenHistoryReader _geminiReader;
    private readonly ClaudeTokenHistoryReader _claudeReader;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly TimeSpan _interval;
    private CancellationTokenSource? _runCancellation;
    private Task? _runTask;
    private TokenHistorySettings _settings = new(false, false);
    private bool _loaded;

    public TokenHistoryService(string? dataDirectory = null, TimeSpan? interval = null)
    {
        _dataRoot = string.IsNullOrWhiteSpace(dataDirectory)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexUsageMonitor")
            : Path.GetFullPath(dataDirectory);
        _settingsPath = Path.Combine(_dataRoot, "settings", "token-history.json");
        _codexReader = new CodexTokenHistoryReader(_dataRoot);
        _geminiReader = new GeminiTokenHistoryReader(_dataRoot);
        _claudeReader = new ClaudeTokenHistoryReader(_dataRoot);
        _interval = interval ?? TimeSpan.FromMinutes(5);
    }

    public event Action<TokenHistorySnapshot>? SnapshotUpdated;

    public TokenHistorySettings Settings => _settings;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_loaded)
            {
                _settings = await LoadSettingsAsync(cancellationToken).ConfigureAwait(false);
                _loaded = true;
            }

            if (_runTask is null)
            {
                _runCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                _runTask = RunTimerAsync(_runCancellation.Token);
            }
        }
        finally { _gate.Release(); }

        if (_settings.AnyEnabled)
            await SyncNowAsync(cancellationToken).ConfigureAwait(false);
        else
            await PublishStoredRowsAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<TokenHistorySnapshot> SetProvidersEnabledAsync(
        bool codexEnabled,
        bool geminiEnabled,
        bool claudeEnabled,
        int retentionDays,
        CancellationToken cancellationToken = default)
    {
        retentionDays = NormalizeRetentionDays(retentionDays);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        TokenHistorySnapshot snapshot;
        try
        {
            if (!_loaded)
            {
                _settings = await LoadSettingsAsync(cancellationToken).ConfigureAwait(false);
                _loaded = true;
            }

            var next = new TokenHistorySettings(codexEnabled, geminiEnabled, claudeEnabled, retentionDays);
            await SaveSettingsAsync(next, cancellationToken).ConfigureAwait(false);
            _settings = next;
            snapshot = await CollectRowsAsync(cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }

        Publish(snapshot);
        EnsureTimerStarted(cancellationToken);
        return snapshot;
    }

    public async Task<TokenHistorySnapshot> SyncNowAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        TokenHistorySnapshot snapshot;
        try
        {
            if (!_loaded)
            {
                _settings = await LoadSettingsAsync(cancellationToken).ConfigureAwait(false);
                _loaded = true;
            }
            snapshot = _settings.AnyEnabled
                ? await CollectRowsAsync(cancellationToken).ConfigureAwait(false)
                : await ReadStoredRowsAsync(cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }

        Publish(snapshot);
        return snapshot;
    }

    public async Task<TokenHistorySnapshot> ClearHistoryAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        TokenHistorySnapshot snapshot;
        try
        {
            var disabled = new TokenHistorySettings(false, false, false, _settings.RetentionDays);
            await SaveSettingsAsync(disabled, cancellationToken).ConfigureAwait(false);
            _settings = disabled;
            _loaded = true;
            foreach (var file in new[]
                     {
                         Path.Combine(_dataRoot, "history", "codex-token-history.json"),
                         Path.Combine(_dataRoot, "history", "gemini-cli-token-history.json"),
                         Path.Combine(_dataRoot, "history", "claude-code-token-history.json")
                     })
            {
                cancellationToken.ThrowIfCancellationRequested();
                try { File.Delete(file); }
                catch (FileNotFoundException) { }
            }
            snapshot = new TokenHistorySnapshot(DateTimeOffset.UtcNow, _settings, Array.Empty<TokenUsageRow>(), "Yerel token geçmişi silindi ve toplama durduruldu.");
        }
        finally { _gate.Release(); }

        Publish(snapshot);
        return snapshot;
    }

    private async Task<TokenHistorySnapshot> CollectRowsAsync(CancellationToken cancellationToken)
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        var from = today.AddDays(-(_settings.RetentionDays - 1));
        var rows = new List<TokenUsageRow>();
        var errors = new List<string>();

        if (_settings.CodexEnabled)
        {
            try
            {
                var codexRows = await _codexReader.CollectAndGetRowsAsync(from, today,
                    cancellationToken: cancellationToken, retentionDays: _settings.RetentionDays).ConfigureAwait(false);
                rows.AddRange(codexRows.Select(row => new TokenUsageRow(
                    "chatgpt-codex", row.LocalDate, row.Model, row.InputTokens, row.OutputTokens,
                    row.CacheReadTokens, row.CacheWriteTokens, 0, 0, row.TotalTokens)));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception)
            {
                errors.Add("Codex geçmişi okunamadı; son kaydedilen veriler korunuyor.");
                try { rows.AddRange(await ReadCodexStoredRowsAsync(from, today, cancellationToken, _settings.RetentionDays).ConfigureAwait(false)); }
                catch (Exception) { }
            }
        }
        else
        {
            rows.AddRange(await ReadCodexStoredRowsAsync(from, today, cancellationToken, _settings.RetentionDays).ConfigureAwait(false));
        }

        if (_settings.GeminiEnabled)
        {
            try
            {
                var geminiRows = await _geminiReader.CollectAndGetRowsAsync(from, today,
                    cancellationToken: cancellationToken, retentionDays: _settings.RetentionDays).ConfigureAwait(false);
                rows.AddRange(geminiRows.Select(row => new TokenUsageRow(
                    "gemini", row.LocalDate, row.Model, row.InputTokens, row.OutputTokens,
                    row.CachedTokens, 0, row.ThoughtsTokens, row.ToolTokens, row.TotalTokens)));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception)
            {
                errors.Add("Gemini CLI geçmişi okunamadı; son kaydedilen veriler korunuyor.");
                try { rows.AddRange(await ReadGeminiStoredRowsAsync(from, today, cancellationToken, _settings.RetentionDays).ConfigureAwait(false)); }
                catch (Exception) { }
            }
        }
        else
        {
            rows.AddRange(await ReadGeminiStoredRowsAsync(from, today, cancellationToken, _settings.RetentionDays).ConfigureAwait(false));
        }

        if (_settings.ClaudeEnabled)
        {
            try
            {
                rows.AddRange(await _claudeReader.CollectAndGetRowsAsync(from, today, cancellationToken, _settings.RetentionDays).ConfigureAwait(false));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception)
            {
                errors.Add("Claude Code geçmişi okunamadı; son kaydedilen veriler korunuyor.");
                try { rows.AddRange(await ReadClaudeStoredRowsAsync(from, today, cancellationToken, _settings.RetentionDays).ConfigureAwait(false)); }
                catch (Exception) { }
            }
        }
        else
        {
            rows.AddRange(await ReadClaudeStoredRowsAsync(from, today, cancellationToken, _settings.RetentionDays).ConfigureAwait(false));
        }

        return MakeSnapshot(rows, errors.Count == 0 ? null : string.Join(" ", errors));
    }

    private async Task<TokenHistorySnapshot> ReadStoredRowsAsync(CancellationToken cancellationToken)
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        var from = today.AddDays(-(_settings.RetentionDays - 1));
        var rows = new List<TokenUsageRow>();
        rows.AddRange(await ReadCodexStoredRowsAsync(from, today, cancellationToken, _settings.RetentionDays).ConfigureAwait(false));
        rows.AddRange(await ReadGeminiStoredRowsAsync(from, today, cancellationToken, _settings.RetentionDays).ConfigureAwait(false));
        rows.AddRange(await ReadClaudeStoredRowsAsync(from, today, cancellationToken, _settings.RetentionDays).ConfigureAwait(false));
        return MakeSnapshot(rows, _settings.AnyEnabled ? null : "Toplama kapalı · kaydedilmiş yerel geçmiş duruyor.");
    }

    private async Task<TokenHistorySnapshot> PublishStoredRowsAsync(CancellationToken cancellationToken)
    {
        var snapshot = await ReadStoredRowsAsync(cancellationToken).ConfigureAwait(false);
        Publish(snapshot);
        return snapshot;
    }

    private async Task<IReadOnlyList<TokenUsageRow>> ReadCodexStoredRowsAsync(DateOnly from, DateOnly today, CancellationToken cancellationToken, int retentionDays)
    {
        var result = await _codexReader.GetRowsAsync(from, today,
            cancellationToken: cancellationToken, retentionDays: retentionDays).ConfigureAwait(false);
        return result.Select(row => new TokenUsageRow(
            "chatgpt-codex", row.LocalDate, row.Model, row.InputTokens, row.OutputTokens,
            row.CacheReadTokens, row.CacheWriteTokens, 0, 0, row.TotalTokens)).ToArray();
    }

    private async Task<IReadOnlyList<TokenUsageRow>> ReadGeminiStoredRowsAsync(DateOnly from, DateOnly today, CancellationToken cancellationToken, int retentionDays)
    {
        var result = await _geminiReader.GetRowsAsync(from, today,
            cancellationToken: cancellationToken, retentionDays: retentionDays).ConfigureAwait(false);
        return result.Select(row => new TokenUsageRow(
            "gemini", row.LocalDate, row.Model, row.InputTokens, row.OutputTokens,
            row.CachedTokens, 0, row.ThoughtsTokens, row.ToolTokens, row.TotalTokens)).ToArray();
    }

    private Task<IReadOnlyList<TokenUsageRow>> ReadClaudeStoredRowsAsync(DateOnly from, DateOnly today, CancellationToken cancellationToken, int retentionDays) =>
        _claudeReader.GetRowsAsync(from, today, cancellationToken, retentionDays);

    private TokenHistorySnapshot MakeSnapshot(IEnumerable<TokenUsageRow> rows, string? message) => new(
        DateTimeOffset.UtcNow,
        _settings,
        rows.OrderBy(row => row.LocalDate).ThenBy(row => row.ProviderId, StringComparer.Ordinal).ThenBy(row => row.Model, StringComparer.OrdinalIgnoreCase).ToArray(),
        message);

    private void Publish(TokenHistorySnapshot snapshot)
    {
        snapshot = snapshot with { Settings = _settings };
        if (SnapshotUpdated is not { } handlers) return;
        foreach (Action<TokenHistorySnapshot> handler in handlers.GetInvocationList())
        {
            try { handler(snapshot); }
            catch (Exception) { /* Keep a UI subscriber from stopping local collection. */ }
        }
    }

    private void EnsureTimerStarted(CancellationToken cancellationToken)
    {
        if (_runTask is not null) return;
        _runCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _runTask = RunTimerAsync(_runCancellation.Token);
    }

    private async Task RunTimerAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(_interval);
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            try { await SyncNowAsync(cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception) { /* A failed file scan is retried on the next interval. */ }
        }
    }

    private async Task<TokenHistorySettings> LoadSettingsAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = File.OpenRead(_settingsPath);
            var document = await JsonSerializer.DeserializeAsync<SettingsDocument>(stream, JsonOptions, cancellationToken).ConfigureAwait(false);
            return document is { Version: SettingsVersion }
                ? new TokenHistorySettings(document.CodexEnabled, document.GeminiEnabled, document.ClaudeEnabled,
                    NormalizeRetentionDays(document.RetentionDays))
                : new TokenHistorySettings(false, false);
        }
        catch (FileNotFoundException) { return new TokenHistorySettings(false, false); }
        catch (DirectoryNotFoundException) { return new TokenHistorySettings(false, false); }
        catch (JsonException) { return new TokenHistorySettings(false, false); }
        catch (IOException) { return new TokenHistorySettings(false, false); }
        catch (UnauthorizedAccessException) { return new TokenHistorySettings(false, false); }
    }

    private async Task SaveSettingsAsync(TokenHistorySettings settings, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);
        var temporary = _settingsPath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, useAsync: true))
                await JsonSerializer.SerializeAsync(stream, new SettingsDocument
                {
                    Version = SettingsVersion,
                    CodexEnabled = settings.CodexEnabled,
                    GeminiEnabled = settings.GeminiEnabled,
                    ClaudeEnabled = settings.ClaudeEnabled,
                    RetentionDays = NormalizeRetentionDays(settings.RetentionDays)
                }, JsonOptions, cancellationToken).ConfigureAwait(false);
            File.Move(temporary, _settingsPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static int NormalizeRetentionDays(int retentionDays) => retentionDays is 30 or 90 or 365 ? retentionDays : 90;

    public async ValueTask DisposeAsync()
    {
        if (_runCancellation is not null)
        {
            await _runCancellation.CancelAsync().ConfigureAwait(false);
            if (_runTask is not null)
            {
                try { await _runTask.ConfigureAwait(false); }
                catch (OperationCanceledException) { }
            }
            _runCancellation.Dispose();
            _runCancellation = null;
            _runTask = null;
        }
        _gate.Dispose();
    }

    private sealed class SettingsDocument
    {
        public int Version { get; set; }
        public bool CodexEnabled { get; set; }
        public bool GeminiEnabled { get; set; }
        public bool ClaudeEnabled { get; set; }
        public int RetentionDays { get; set; } = 90;
    }
}
