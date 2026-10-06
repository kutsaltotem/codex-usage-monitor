using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CodexUsageMonitor;

/// <summary>A local, opt-in reader for Codex JSONL token history.</summary>
/// <remarks>
/// No filesystem access occurs in the constructor. Call <see cref="CollectAndGetRowsAsync"/>
/// or <see cref="CollectAsync"/> explicitly to scan Codex session files. This reader is
/// intentionally separate from quota polling and writes aggregate counters and opaque
/// file cursors only; it never stores raw session lines, paths, or session identifiers.
/// </remarks>
public sealed class CodexTokenHistoryReader
{
    public const int RetentionDays = 365;

    private const int StoreVersion = 2;
    private const int ReadBufferSize = 64 * 1024;
    private const int MaximumJsonLineBytes = 16 * 1024 * 1024;
    private const int MaximumCursorHeaderBytes = 64 * 1024;
    private const string UnknownModel = "Unknown model";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        PropertyNameCaseInsensitive = false
    };

    private readonly string _storePath;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <param name="dataDirectory">
    /// Optional directory for the aggregate store. Defaults to the current user's local
    /// application data directory. No directory is created until a collection is saved.
    /// </param>
    public CodexTokenHistoryReader(string? dataDirectory = null)
    {
        var root = string.IsNullOrWhiteSpace(dataDirectory)
            ? GetDefaultDataDirectory()
            : Path.GetFullPath(dataDirectory);

        _storePath = Path.Combine(root, "history", "codex-token-history.json");
    }

    /// <summary>
    /// Explicitly scans the active and archived Codex JSONL session directories, updates
    /// local-day/model aggregates, and returns rows for the requested inclusive period.
    /// </summary>
    public async Task<IReadOnlyList<CodexTokenUsageRow>> CollectAndGetRowsAsync(
        DateOnly fromInclusive,
        DateOnly throughInclusive,
        string? model = null,
        CancellationToken cancellationToken = default,
        int retentionDays = RetentionDays)
    {
        ValidatePeriod(fromInclusive, throughInclusive);
        ValidateRetentionDays(retentionDays);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var store = await LoadStoreAsync(cancellationToken).ConfigureAwait(false);
            await CollectIntoStoreAsync(store, cancellationToken).ConfigureAwait(false);
            PruneExpiredRows(store, GetLocalToday(), retentionDays);
            await SaveStoreAsync(store, cancellationToken).ConfigureAwait(false);
            return SelectRows(store, fromInclusive, throughInclusive, model);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Explicitly scans Codex session logs and returns all retained rows.</summary>
    public Task<IReadOnlyList<CodexTokenUsageRow>> CollectAsync(
        CancellationToken cancellationToken = default)
    {
        var today = GetLocalToday();
        return CollectAndGetRowsAsync(today.AddDays(-(RetentionDays - 1)), today, cancellationToken: cancellationToken);
    }

    /// <summary>Returns already-collected rows without reading any Codex session files.</summary>
    public async Task<IReadOnlyList<CodexTokenUsageRow>> GetRowsAsync(
        DateOnly fromInclusive,
        DateOnly throughInclusive,
        string? model = null,
        CancellationToken cancellationToken = default,
        int retentionDays = RetentionDays)
    {
        ValidatePeriod(fromInclusive, throughInclusive);
        ValidateRetentionDays(retentionDays);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var store = await LoadStoreAsync(cancellationToken).ConfigureAwait(false);
            var contributionCountBeforePrune = store.Cursors.Sum(cursor => cursor.Contributions.Count);
            PruneExpiredRows(store, GetLocalToday(), retentionDays);
            if (store.Cursors.Sum(cursor => cursor.Contributions.Count) != contributionCountBeforePrune)
            {
                await SaveStoreAsync(store, cancellationToken).ConfigureAwait(false);
            }

            return SelectRows(store, fromInclusive, throughInclusive, model);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task CollectIntoStoreAsync(StoreDocument store, CancellationToken cancellationToken)
    {
        var root = GetCodexHome();
        if (root is null || !Directory.Exists(root))
        {
            return;
        }

        var cursors = store.Cursors
            .Where(cursor => cursor is not null && !string.IsNullOrWhiteSpace(cursor.FileKey))
            .GroupBy(cursor => cursor.FileKey, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Last(), StringComparer.Ordinal);

        foreach (var sessionsRoot in new[]
                 {
                     Path.Combine(root, "sessions"),
                     Path.Combine(root, "archived_sessions")
                 })
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var filePath in EnumerateJsonlFiles(sessionsRoot))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var pathKey = HashFilePath(filePath);
                var fingerprint = HashFileHeader(filePath);
                var cursor = ResolveCursor(pathKey, fingerprint, cursors, out var previousPathKey);
                var fileAggregates = new Dictionary<(string LocalDate, string Model), TokenCounters>();

                try
                {
                    var updated = await ReadFileAsync(
                        filePath,
                        pathKey,
                        fingerprint,
                        cursor,
                        fileAggregates,
                        cancellationToken).ConfigureAwait(false);
                    if (previousPathKey is not null && !string.Equals(previousPathKey, pathKey, StringComparison.Ordinal))
                    {
                        cursors.Remove(previousPathKey);
                    }

                    cursors[pathKey] = updated;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (IOException)
                {
                    // Session logs can be in use by Codex. Leave that cursor unchanged and retry later.
                }
                catch (UnauthorizedAccessException)
                {
                    // Do not expose local paths or file contents in diagnostics.
                }
            }
        }

        store.Cursors = cursors.Values
            .OrderBy(cursor => cursor.FileKey, StringComparer.Ordinal)
            .ToList();
    }

    private static async Task<FileCursor> ReadFileAsync(
        string filePath,
        string fileKey,
        string? fingerprint,
        FileCursor? existingCursor,
        Dictionary<(string LocalDate, string Model), TokenCounters> aggregates,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            ReadBufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        var offset = existingCursor?.OffsetBytes ?? 0;
        var model = NormalizeModel(existingCursor?.Model) ?? UnknownModel;
        var previous = existingCursor?.PreviousCounters?.ToValue() ?? TokenCounters.Zero;
        var hasPreviousCounters = existingCursor?.HasPreviousCounters ?? false;
        var reset = offset < 0
            || offset > stream.Length
            || (existingCursor is not null
                && !string.Equals(existingCursor.Fingerprint, fingerprint, StringComparison.Ordinal));

        if (!reset && offset > 0 && !string.IsNullOrWhiteSpace(existingCursor?.AnchorHash))
        {
            var currentAnchor = HashLineAtOffset(stream, offset);
            reset = !string.Equals(currentAnchor, existingCursor.AnchorHash, StringComparison.Ordinal);
        }

        if (reset)
        {
            // A truncated or replaced log starts over; its old contribution is discarded below.
            offset = 0;
            model = UnknownModel;
            previous = TokenCounters.Zero;
            hasPreviousCounters = false;
        }

        stream.Seek(offset, SeekOrigin.Begin);
        var buffer = new byte[ReadBufferSize];
        using var line = new MemoryStream();
        var currentPosition = offset;
        var lastCompleteOffset = offset;
        var discardingOversizedLine = false;
        var anchorHash = reset ? null : existingCursor?.AnchorHash;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = await stream.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (count == 0)
            {
                break;
            }

            var segmentStart = 0;
            for (var index = 0; index < count; index++)
            {
                if (buffer[index] != (byte)'\n')
                {
                    continue;
                }

                var segmentLength = index - segmentStart;
                if (!discardingOversizedLine)
                {
                    if (line.Length + segmentLength <= MaximumJsonLineBytes)
                    {
                        line.Write(buffer, segmentStart, segmentLength);
                        var completeLine = line.GetBuffer().AsSpan(0, checked((int)line.Length));
                        anchorHash = HashOpaqueLine(completeLine);
                        ProcessCompleteLine(
                            completeLine,
                            ref model,
                            ref previous,
                            ref hasPreviousCounters,
                            aggregates);
                    }
                    else
                    {
                        anchorHash = null;
                    }
                }
                else
                {
                    anchorHash = null;
                }

                line.SetLength(0);
                discardingOversizedLine = false;
                lastCompleteOffset = currentPosition + index + 1;
                segmentStart = index + 1;
            }

            var remaining = count - segmentStart;
            if (remaining > 0 && !discardingOversizedLine)
            {
                if (line.Length + remaining <= MaximumJsonLineBytes)
                {
                    line.Write(buffer, segmentStart, remaining);
                }
                else
                {
                    // Keep memory bounded. An oversized complete event is intentionally skipped.
                    line.SetLength(0);
                    discardingOversizedLine = true;
                }
            }

            currentPosition += count;
        }

        var contributions = reset
            ? new Dictionary<(string LocalDate, string Model), TokenCounters>()
            : ContributionsToDictionary(existingCursor?.Contributions);
        foreach (var pair in aggregates)
        {
            contributions.TryGetValue(pair.Key, out var current);
            contributions[pair.Key] = Add(current, pair.Value);
        }

        return new FileCursor
        {
            FileKey = fileKey,
            Fingerprint = fingerprint,
            OffsetBytes = lastCompleteOffset,
            Model = model == UnknownModel ? null : model,
            PreviousCounters = CounterDocument.From(previous),
            HasPreviousCounters = hasPreviousCounters,
            AnchorHash = lastCompleteOffset == offset && !reset ? existingCursor?.AnchorHash : anchorHash,
            Contributions = contributions
                .Select(pair => new AggregateEntry
                {
                    LocalDate = pair.Key.LocalDate,
                    Model = pair.Key.Model,
                    Counters = CounterDocument.From(pair.Value)
                })
                .OrderBy(row => row.LocalDate, StringComparer.Ordinal)
                .ThenBy(row => row.Model, StringComparer.OrdinalIgnoreCase)
                .ToList()
        };
    }

    private static void ProcessCompleteLine(
        Span<byte> utf8Line,
        ref string model,
        ref TokenCounters previous,
        ref bool hasPreviousCounters,
        Dictionary<(string LocalDate, string Model), TokenCounters> aggregates)
    {
        if (utf8Line.Length == 0)
        {
            return;
        }

        // Allow a UTF-8 BOM at the beginning of a session file.
        if (utf8Line.Length >= 3
            && utf8Line[0] == 0xEF
            && utf8Line[1] == 0xBB
            && utf8Line[2] == 0xBF)
        {
            utf8Line = utf8Line[3..];
        }

        if (utf8Line.Length > 0 && utf8Line[^1] == (byte)'\r')
        {
            utf8Line = utf8Line[..^1];
        }

        try
        {
            using var document = JsonDocument.Parse(utf8Line.ToArray());
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !TryGetString(root, "type", out var eventType))
            {
                return;
            }

            if (string.Equals(eventType, "turn_context", StringComparison.Ordinal))
            {
                if (root.TryGetProperty("payload", out var context)
                    && context.ValueKind == JsonValueKind.Object)
                {
                    model = ReadModel(context) ?? model;
                }

                return;
            }

            if (!string.Equals(eventType, "event_msg", StringComparison.Ordinal)
                || !root.TryGetProperty("payload", out var payload)
                || payload.ValueKind != JsonValueKind.Object
                || !TryGetString(payload, "type", out var payloadType)
                || !string.Equals(payloadType, "token_count", StringComparison.Ordinal)
                || !payload.TryGetProperty("info", out var info)
                || info.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            var total = ReadCounters(info, "total_token_usage");
            var last = ReadCounters(info, "last_token_usage");
            var (delta, updatedPrevious, updatedHasPrevious) = ComputeDelta(total, last, previous, hasPreviousCounters);
            previous = updatedPrevious;
            hasPreviousCounters = updatedHasPrevious;

            if (!TryGetString(root, "timestamp", out var timestampText)
                || !DateTimeOffset.TryParse(
                    timestampText,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out var timestamp))
            {
                return;
            }

            var localDate = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(timestamp, TimeZoneInfo.Local).DateTime);
            var eventModel = ReadModel(info) ?? model;
            var rowKey = (localDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), eventModel);
            aggregates.TryGetValue(rowKey, out var current);
            aggregates[rowKey] = Add(current, delta);
        }
        catch (JsonException)
        {
            // A malformed complete line does not block later valid events.
        }
        catch (FormatException)
        {
            // Ignore malformed timestamps and continue with later lines.
        }
    }

    private static (TokenCounters Delta, TokenCounters UpdatedPrevious, bool UpdatedHasPrevious) ComputeDelta(
        PartialCounters total,
        PartialCounters last,
        TokenCounters previous,
        bool hasPreviousCounters)
    {
        if (!total.HasAny)
        {
            return (SplitInput(last.ToCounters()), previous, hasPreviousCounters);
        }

        var next = new TokenCounters(
            total.Input ?? previous.Input,
            total.Output ?? previous.Output,
            total.CacheRead ?? previous.CacheRead,
            total.CacheWrite ?? previous.CacheWrite);

        if (!hasPreviousCounters)
        {
            return (SplitInput(last.ToCounters()), next, true);
        }

        var delta = new TokenCounters(
            CounterDelta(total.Input, previous.Input, last.Input),
            CounterDelta(total.Output, previous.Output, last.Output),
            CounterDelta(total.CacheRead, previous.CacheRead, last.CacheRead),
            CounterDelta(total.CacheWrite, previous.CacheWrite, last.CacheWrite));

        return (SplitInput(delta), next, true);
    }

    private static long CounterDelta(long? current, long previous, long? last)
    {
        if (!current.HasValue)
        {
            return Math.Max(0, last ?? 0);
        }

        if (current.Value >= previous)
        {
            return current.Value - previous;
        }

        // A reset within a file is rare; use the per-event value when available.
        return Math.Max(0, last ?? current.Value);
    }

    private static TokenCounters SplitInput(TokenCounters counters)
    {
        var cacheRead = Math.Max(0, counters.CacheRead);
        var cacheWrite = Math.Max(0, counters.CacheWrite);
        var uncachedInput = Math.Max(0, counters.Input - Math.Min(counters.Input, cacheRead));
        return new TokenCounters(uncachedInput, Math.Max(0, counters.Output), cacheRead, cacheWrite);
    }

    private static PartialCounters ReadCounters(JsonElement parent, string propertyName)
    {
        if (!parent.TryGetProperty(propertyName, out var usage) || usage.ValueKind != JsonValueKind.Object)
        {
            return default;
        }

        var input = ReadInt64(usage, "input_tokens", "total_input_tokens", "prompt_tokens");
        var output = ReadInt64(usage, "output_tokens", "total_output_tokens", "completion_tokens");
        var cacheRead = ReadInt64(usage, "cached_input_tokens", "cache_read_input_tokens", "cache_read_tokens", "cached_tokens");
        var cacheWrite = ReadInt64(usage, "cache_write_input_tokens", "cache_creation_input_tokens", "cache_write_tokens", "cache_creation_tokens");

        if (usage.TryGetProperty("input_tokens_details", out var details)
            && details.ValueKind == JsonValueKind.Object)
        {
            cacheRead ??= ReadInt64(details, "cached_tokens", "cached_input_tokens", "cache_read_tokens");
            cacheWrite ??= ReadInt64(details, "cache_creation_tokens", "cache_write_tokens", "cache_write_input_tokens");
        }

        return new PartialCounters(input, output, cacheRead, cacheWrite);
    }

    private static long? ReadInt64(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (!element.TryGetProperty(name, out var value))
            {
                continue;
            }

            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number))
            {
                return Math.Max(0, number);
            }

            if (value.ValueKind == JsonValueKind.String
                && long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out number))
            {
                return Math.Max(0, number);
            }
        }

        return null;
    }

    private static string? ReadModel(JsonElement element)
    {
        var model = NormalizeModel(ReadString(element, "model"))
            ?? NormalizeModel(ReadString(element, "model_slug"))
            ?? NormalizeModel(ReadString(element, "model_id"));
        return model;
    }

    private static string? ReadString(JsonElement element, string propertyName)
    {
        return TryGetString(element, propertyName, out var value) ? value : null;
    }

    private static bool TryGetString(JsonElement element, string propertyName, out string value)
    {
        value = string.Empty;
        if (!element.TryGetProperty(propertyName, out var property)
            || property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = property.GetString() ?? string.Empty;
        return value.Length > 0;
    }

    private static string? NormalizeModel(string? model)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            return null;
        }

        var normalized = new string(model.Trim().Where(character => !char.IsControl(character)).Take(128).ToArray());
        return normalized.Length == 0 ? null : normalized;
    }

    private async Task<StoreDocument> LoadStoreAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_storePath))
        {
            return new StoreDocument { Version = StoreVersion };
        }

        try
        {
            await using var stream = new FileStream(
                _storePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                ReadBufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var store = await JsonSerializer.DeserializeAsync<StoreDocument>(stream, JsonOptions, cancellationToken)
                .ConfigureAwait(false);
            if (store is null || store.Version != StoreVersion)
            {
                return new StoreDocument { Version = StoreVersion };
            }

            store.Cursors = (store.Cursors ?? [])
                .Where(cursor => cursor is not null && !string.IsNullOrWhiteSpace(cursor.FileKey))
                .ToList();
            foreach (var cursor in store.Cursors)
            {
                cursor.Contributions = (cursor.Contributions ?? [])
                    .Where(contribution => contribution is not null && contribution.Counters is not null)
                    .ToList();
                cursor.PreviousCounters ??= new CounterDocument();
            }

            return store;
        }
        catch (JsonException)
        {
            // Recover from an interrupted or manually damaged aggregate file without reading logs implicitly.
            return new StoreDocument { Version = StoreVersion };
        }
        catch (IOException)
        {
            return new StoreDocument { Version = StoreVersion };
        }
        catch (UnauthorizedAccessException)
        {
            return new StoreDocument { Version = StoreVersion };
        }
    }

    private async Task SaveStoreAsync(StoreDocument store, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_storePath)!;
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, $".{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             ReadBufferSize,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, store, JsonOptions, cancellationToken)
                    .ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporaryPath, _storePath, overwrite: true);
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
            catch (IOException)
            {
                // Best effort cleanup only.
            }
            catch (UnauthorizedAccessException)
            {
                // Best effort cleanup only.
            }
        }
    }

    private static IEnumerable<string> EnumerateJsonlFiles(string root)
    {
        if (!Directory.Exists(root))
        {
            yield break;
        }

        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            string[] files;
            string[] childDirectories;
            try
            {
                files = Directory.GetFiles(directory, "*", SearchOption.TopDirectoryOnly);
                childDirectories = Directory.GetDirectories(directory, "*", SearchOption.TopDirectoryOnly);
            }
            catch (IOException)
            {
                continue;
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var file in files)
            {
                var isReadableJsonl = false;
                try
                {
                    isReadableJsonl = Path.GetExtension(file).Equals(".jsonl", StringComparison.OrdinalIgnoreCase)
                        && (File.GetAttributes(file) & FileAttributes.ReparsePoint) == 0;
                }
                catch (IOException)
                {
                    // The file may disappear while Codex rotates session logs.
                }
                catch (UnauthorizedAccessException)
                {
                    // Skip files the current user cannot inspect.
                }

                if (isReadableJsonl)
                {
                    yield return file;
                }
            }

            foreach (var child in childDirectories)
            {
                try
                {
                    if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) == 0)
                    {
                        pending.Push(child);
                    }
                }
                catch (IOException)
                {
                    // Skip paths that disappeared during enumeration.
                }
                catch (UnauthorizedAccessException)
                {
                    // Skip inaccessible directories.
                }
            }
        }
    }

    private static string? GetCodexHome()
    {
        var configuredHome = Environment.GetEnvironmentVariable("CODEX_HOME");
        if (!string.IsNullOrWhiteSpace(configuredHome))
        {
            try
            {
                return Path.GetFullPath(Environment.ExpandEnvironmentVariables(configuredHome));
            }
            catch (ArgumentException)
            {
                return null;
            }
            catch (NotSupportedException)
            {
                return null;
            }
            catch (PathTooLongException)
            {
                return null;
            }
        }

        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return string.IsNullOrWhiteSpace(profile) ? null : Path.Combine(profile, ".codex");
    }

    private static FileCursor ResolveCursor(
        string pathKey,
        string? fingerprint,
        Dictionary<string, FileCursor> cursors,
        out string? previousPathKey)
    {
        previousPathKey = null;
        if (cursors.TryGetValue(pathKey, out var samePathCursor))
        {
            return samePathCursor;
        }

        if (!string.IsNullOrWhiteSpace(fingerprint))
        {
            var movedCursor = cursors.Values.FirstOrDefault(cursor =>
                string.Equals(cursor.Fingerprint, fingerprint, StringComparison.Ordinal));
            if (movedCursor is not null)
            {
                previousPathKey = movedCursor.FileKey;
                return new FileCursor
                {
                    FileKey = pathKey,
                    Fingerprint = fingerprint,
                    OffsetBytes = movedCursor.OffsetBytes,
                    Model = movedCursor.Model,
                    PreviousCounters = movedCursor.PreviousCounters is null
                        ? new CounterDocument()
                        : CounterDocument.From(movedCursor.PreviousCounters.ToValue()),
                    HasPreviousCounters = movedCursor.HasPreviousCounters,
                    AnchorHash = movedCursor.AnchorHash,
                    Contributions = movedCursor.Contributions
                        .Select(CloneContribution)
                        .ToList()
                };
            }
        }

        return new FileCursor { FileKey = pathKey, Fingerprint = fingerprint };
    }

    private static AggregateEntry CloneContribution(AggregateEntry contribution) => new()
    {
        LocalDate = contribution.LocalDate,
        Model = contribution.Model,
        Counters = contribution.Counters is null
            ? new CounterDocument()
            : CounterDocument.From(contribution.Counters.ToValue())
    };

    private static Dictionary<(string LocalDate, string Model), TokenCounters> ContributionsToDictionary(
        IEnumerable<AggregateEntry>? contributions)
    {
        var result = new Dictionary<(string LocalDate, string Model), TokenCounters>();
        foreach (var contribution in contributions ?? [])
        {
            if (contribution is null
                || contribution.Counters is null
                || !TryParseDate(contribution.LocalDate, out _)
                || string.IsNullOrWhiteSpace(contribution.Model))
            {
                continue;
            }

            var key = (contribution.LocalDate, contribution.Model);
            result.TryGetValue(key, out var current);
            result[key] = Add(current, contribution.Counters.ToValue());
        }

        return result;
    }

    private static string HashFilePath(string filePath)
    {
        var canonicalPath = Path.GetFullPath(filePath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (OperatingSystem.IsWindows())
        {
            canonicalPath = canonicalPath.ToUpperInvariant();
        }

        var pathBytes = Encoding.UTF8.GetBytes(canonicalPath);
        var pathMaterial = new byte[pathBytes.Length + 1];
        pathMaterial[0] = 0x02;
        Buffer.BlockCopy(pathBytes, 0, pathMaterial, 1, pathBytes.Length);
        return Convert.ToHexString(SHA256.HashData(pathMaterial));
    }

    private static string? HashFileHeader(string filePath)
    {
        // The first complete line is used only as an opaque move/replacement fingerprint.
        // Its contents are never parsed or persisted.
        try
        {
            using var stream = new FileStream(
                filePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 4096,
                FileOptions.SequentialScan);
            using var prefix = new MemoryStream();
            var buffer = new byte[4096];
            while (prefix.Length < MaximumCursorHeaderBytes)
            {
                var requested = (int)Math.Min(buffer.Length, MaximumCursorHeaderBytes - prefix.Length);
                var count = stream.Read(buffer, 0, requested);
                if (count == 0)
                {
                    break;
                }

                var newline = Array.IndexOf(buffer, (byte)'\n', 0, count);
                if (newline >= 0)
                {
                    prefix.Write(buffer, 0, newline);
                    var header = prefix.ToArray();
                    var headerLength = header.Length;
                    if (headerLength > 0 && header[headerLength - 1] == (byte)'\r')
                    {
                        headerLength--;
                    }

                    return HashOpaqueBytes(header.AsSpan(0, headerLength));
                }

                prefix.Write(buffer, 0, count);
            }
        }
        catch (IOException)
        {
            // The later file read will retry; no path or error detail is exposed.
        }
        catch (UnauthorizedAccessException)
        {
            // The file reader will retry access and report no path information.
        }

        return null;
    }

    private static string HashOpaqueLine(ReadOnlySpan<byte> line)
    {
        if (line.Length > 0 && line[^1] == (byte)'\r')
        {
            line = line[..^1];
        }

        return HashOpaqueBytes(line);
    }

    private static string HashOpaqueBytes(ReadOnlySpan<byte> bytes)
    {
        var material = new byte[bytes.Length + 1];
        material[0] = 0x01;
        bytes.CopyTo(material.AsSpan(1));
        return Convert.ToHexString(SHA256.HashData(material));
    }

    private static string? HashLineAtOffset(FileStream stream, long offset)
    {
        if (offset <= 0 || offset > stream.Length)
        {
            return null;
        }

        stream.Seek(offset - 1, SeekOrigin.Begin);
        if (stream.ReadByte() != (byte)'\n')
        {
            return null;
        }

        var lineEnd = offset - 1;
        var searchEnd = lineEnd;
        var buffer = new byte[4096];
        long lineStart = 0;
        var foundStart = lineEnd == 0;
        while (searchEnd > Math.Max(0, lineEnd - MaximumJsonLineBytes))
        {
            var chunkStart = Math.Max(0, searchEnd - buffer.Length);
            var count = checked((int)(searchEnd - chunkStart));
            stream.Seek(chunkStart, SeekOrigin.Begin);
            var read = 0;
            while (read < count)
            {
                var amount = stream.Read(buffer, read, count - read);
                if (amount == 0)
                {
                    return null;
                }

                read += amount;
            }

            var newlineIndex = Array.LastIndexOf(buffer, (byte)'\n', count - 1, count);
            if (newlineIndex >= 0)
            {
                lineStart = chunkStart + newlineIndex + 1;
                foundStart = true;
                break;
            }

            searchEnd = chunkStart;
            if (chunkStart == 0)
            {
                foundStart = true;
                lineStart = 0;
                break;
            }
        }

        if (!foundStart || lineEnd - lineStart > MaximumJsonLineBytes)
        {
            return null;
        }

        var lineLength = checked((int)(lineEnd - lineStart));
        var line = new byte[lineLength];
        stream.Seek(lineStart, SeekOrigin.Begin);
        var offsetRead = 0;
        while (offsetRead < lineLength)
        {
            var amount = stream.Read(line, offsetRead, lineLength - offsetRead);
            if (amount == 0)
            {
                return null;
            }

            offsetRead += amount;
        }

        return HashOpaqueLine(line);
    }

    private static string GetDefaultDataDirectory()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localAppData))
        {
            localAppData = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "AppData",
                "Local");
        }

        return Path.Combine(localAppData, "CodexUsageMonitor");
    }

    private static void ValidateRetentionDays(int retentionDays)
    {
        if (retentionDays is not (30 or 90 or 365))
            throw new ArgumentOutOfRangeException(nameof(retentionDays), "Retention must be 30, 90, or 365 days.");
    }

    private static void PruneExpiredRows(StoreDocument store, DateOnly today, int retentionDays)
    {
        var oldestRetainedDate = today.AddDays(-(retentionDays - 1))
            .ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        foreach (var cursor in store.Cursors)
        {
            cursor.Contributions = cursor.Contributions
                .Where(row => TryParseDate(row.LocalDate, out var date)
                    && date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture).CompareTo(oldestRetainedDate) >= 0)
                .ToList();
        }
    }

    private static IReadOnlyList<CodexTokenUsageRow> SelectRows(
        StoreDocument store,
        DateOnly fromInclusive,
        DateOnly throughInclusive,
        string? model)
    {
        var modelFilter = NormalizeModel(model);
        var totals = new Dictionary<(string LocalDate, string Model), TokenCounters>();
        foreach (var cursor in store.Cursors)
        {
            foreach (var contribution in cursor.Contributions)
            {
                if (!TryParseDate(contribution.LocalDate, out var date)
                    || date < fromInclusive
                    || date > throughInclusive
                    || (modelFilter is not null
                        && !string.Equals(contribution.Model, modelFilter, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                var key = (contribution.LocalDate, contribution.Model);
                totals.TryGetValue(key, out var current);
                totals[key] = Add(current, contribution.Counters.ToValue());
            }
        }

        return totals
            .Select(pair => new CodexTokenUsageRow(
                DateOnly.ParseExact(pair.Key.LocalDate, "yyyy-MM-dd", CultureInfo.InvariantCulture),
                pair.Key.Model,
                pair.Value.Input,
                pair.Value.Output,
                pair.Value.CacheRead,
                pair.Value.CacheWrite))
            .OrderBy(row => row.LocalDate)
            .ThenBy(row => row.Model, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool TryParseDate(string? text, out DateOnly date)
    {
        return DateOnly.TryParseExact(
            text,
            "yyyy-MM-dd",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out date);
    }

    private static DateOnly GetLocalToday() => DateOnly.FromDateTime(DateTime.Now);

    private static void ValidatePeriod(DateOnly fromInclusive, DateOnly throughInclusive)
    {
        if (throughInclusive < fromInclusive)
        {
            throw new ArgumentOutOfRangeException(nameof(throughInclusive), "The end date must not precede the start date.");
        }
    }

    private static TokenCounters Add(TokenCounters left, TokenCounters right)
    {
        return new TokenCounters(
            SaturatingAdd(left.Input, right.Input),
            SaturatingAdd(left.Output, right.Output),
            SaturatingAdd(left.CacheRead, right.CacheRead),
            SaturatingAdd(left.CacheWrite, right.CacheWrite));
    }

    private static long SaturatingAdd(long left, long right)
    {
        if (left < 0)
        {
            left = 0;
        }

        if (right < 0)
        {
            right = 0;
        }

        return long.MaxValue - left < right ? long.MaxValue : left + right;
    }

    private readonly record struct PartialCounters(long? Input, long? Output, long? CacheRead, long? CacheWrite)
    {
        public bool HasAny => Input.HasValue || Output.HasValue || CacheRead.HasValue || CacheWrite.HasValue;

        public TokenCounters ToCounters() => SplitInput(new TokenCounters(
            Math.Max(0, Input ?? 0),
            Math.Max(0, Output ?? 0),
            Math.Max(0, CacheRead ?? 0),
            Math.Max(0, CacheWrite ?? 0)));
    }

    private readonly record struct TokenCounters(long Input, long Output, long CacheRead, long CacheWrite)
    {
        public static TokenCounters Zero => new(0, 0, 0, 0);
    }

    private sealed class StoreDocument
    {
        public StoreDocument() { }
        public int Version { get; set; } = StoreVersion;
        public List<FileCursor> Cursors { get; set; } = [];
    }

    private sealed class AggregateEntry
    {
        public AggregateEntry() { }
        public string LocalDate { get; set; } = string.Empty;
        public string Model { get; set; } = string.Empty;
        public CounterDocument Counters { get; set; } = new();
    }

    private sealed class FileCursor
    {
        public FileCursor() { }
        public string FileKey { get; set; } = string.Empty;
        public string? Fingerprint { get; set; }
        public long OffsetBytes { get; set; }
        public string? Model { get; set; }
        public CounterDocument? PreviousCounters { get; set; } = new();
        public bool HasPreviousCounters { get; set; }
        public string? AnchorHash { get; set; }
        public List<AggregateEntry> Contributions { get; set; } = [];
    }

    private sealed class CounterDocument
    {
        public CounterDocument() { }
        public long Input { get; set; }
        public long Output { get; set; }
        public long CacheRead { get; set; }
        public long CacheWrite { get; set; }

        public TokenCounters ToValue() => new(
            Math.Max(0, Input),
            Math.Max(0, Output),
            Math.Max(0, CacheRead),
            Math.Max(0, CacheWrite));

        public static CounterDocument From(TokenCounters counters) => new()
        {
            Input = counters.Input,
            Output = counters.Output,
            CacheRead = counters.CacheRead,
            CacheWrite = counters.CacheWrite
        };
    }
}

/// <summary>Daily Codex token totals grouped by local date and model.</summary>
public sealed record CodexTokenUsageRow(
    DateOnly LocalDate,
    string Model,
    long InputTokens,
    long OutputTokens,
    long CacheReadTokens,
    long CacheWriteTokens)
{
    public long TotalTokens
    {
        get
        {
            static long Add(long left, long right) => long.MaxValue - left < right ? long.MaxValue : left + right;
            return Add(Add(Add(InputTokens, OutputTokens), CacheReadTokens), CacheWriteTokens);
        }
    }
}
