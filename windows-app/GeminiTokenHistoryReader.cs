using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CodexUsageMonitor;

/// <summary>
/// An explicit, opt-in reader for token metadata in Gemini CLI chat JSONL files.
/// It is not used by quota polling and does not read Antigravity logs.
/// </summary>
/// <remarks>
/// The reader retains daily/model aggregates and an internal reconciliation ledger. The
/// ledger contains token counters, timestamps, byte positions, and hashes of message and
/// file identities; it never stores raw JSONL lines, prompts, responses, paths, or IDs.
/// </remarks>
public sealed class GeminiTokenHistoryReader
{
    public const int RetentionDays = 365;
    public const string SourceMode = "Gemini CLI";

    private const int StoreVersion = 1;
    private const int ReadBufferSize = 64 * 1024;
    private const int MaximumJsonLineBytes = 8 * 1024 * 1024;
    private const string UnknownModel = "Unknown model";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        PropertyNameCaseInsensitive = false
    };

    private readonly string _storePath;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <param name="dataDirectory">
    /// Optional directory for the aggregate store. Defaults to this application's local
    /// application data directory. No directory is created until collection is requested.
    /// </param>
    public GeminiTokenHistoryReader(string? dataDirectory = null)
    {
        var root = string.IsNullOrWhiteSpace(dataDirectory)
            ? GetDefaultDataDirectory()
            : Path.GetFullPath(dataDirectory);

        _storePath = Path.Combine(root, "history", "gemini-cli-token-history.json");
    }

    /// <summary>
    /// Explicitly scans Gemini CLI chat logs, updates local-day/model aggregates, and returns
    /// rows for the requested inclusive period.
    /// </summary>
    public async Task<IReadOnlyList<GeminiTokenUsageRow>> CollectAndGetRowsAsync(
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
            RebuildAggregates(store);
            await SaveStoreAsync(store, cancellationToken).ConfigureAwait(false);
            return SelectRows(store, fromInclusive, throughInclusive, model);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Explicitly scans Gemini CLI chat logs and returns all retained rows.</summary>
    public Task<IReadOnlyList<GeminiTokenUsageRow>> CollectAsync(
        CancellationToken cancellationToken = default)
    {
        var today = GetLocalToday();
        return CollectAndGetRowsAsync(
            today.AddDays(-(RetentionDays - 1)),
            today,
            cancellationToken: cancellationToken);
    }

    /// <summary>Returns already-collected rows without reading Gemini CLI chat files.</summary>
    public async Task<IReadOnlyList<GeminiTokenUsageRow>> GetRowsAsync(
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
            PruneExpiredRows(store, GetLocalToday(), retentionDays);
            RebuildAggregates(store);
            return SelectRows(store, fromInclusive, throughInclusive, model);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task CollectIntoStoreAsync(StoreDocument store, CancellationToken cancellationToken)
    {
        var root = GetGeminiCliHome();
        if (root is null || !Directory.Exists(root))
        {
            return;
        }

        var cursors = store.Cursors
            .Where(cursor => !string.IsNullOrWhiteSpace(cursor.FileKey))
            .GroupBy(cursor => cursor.FileKey, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Last(), StringComparer.Ordinal);

        var messagesByFile = store.Messages
            .Where(message => !string.IsNullOrWhiteSpace(message.FileKey)
                && !string.IsNullOrWhiteSpace(message.MessageKey)
                && TryParseDate(message.LocalDate, out _)
                && !string.IsNullOrWhiteSpace(message.Model))
            .GroupBy(message => message.FileKey, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group
                    .GroupBy(message => message.MessageKey, StringComparer.Ordinal)
                    .ToDictionary(inner => inner.Key, inner => inner.OrderBy(item => item.PositionBytes).Last(), StringComparer.Ordinal),
                StringComparer.Ordinal);

        foreach (var filePath in EnumerateChatFiles(root))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fileKey = HashFileCursor(filePath);
            cursors.TryGetValue(fileKey, out var cursor);
            if (!messagesByFile.TryGetValue(fileKey, out var fileMessages))
            {
                fileMessages = new Dictionary<string, MessageEntry>(StringComparer.Ordinal);
                messagesByFile[fileKey] = fileMessages;
            }

            try
            {
                var updated = await ReadFileAsync(
                    filePath,
                    fileKey,
                    cursor,
                    fileMessages,
                    cancellationToken).ConfigureAwait(false);
                cursors[fileKey] = updated;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (IOException)
            {
                // Chat files may be actively written. Leave their cursor unchanged and retry later.
            }
            catch (UnauthorizedAccessException)
            {
                // Do not expose local paths or file contents in diagnostics.
            }
        }

        store.Messages = messagesByFile.Values
            .SelectMany(messages => messages.Values)
            .OrderBy(message => message.LocalDate, StringComparer.Ordinal)
            .ThenBy(message => message.Model, StringComparer.OrdinalIgnoreCase)
            .ThenBy(message => message.FileKey, StringComparer.Ordinal)
            .ThenBy(message => message.MessageKey, StringComparer.Ordinal)
            .ToList();
        store.Cursors = cursors.Values
            .OrderBy(cursor => cursor.FileKey, StringComparer.Ordinal)
            .ToList();
    }

    private static async Task<FileCursor> ReadFileAsync(
        string filePath,
        string fileKey,
        FileCursor? existingCursor,
        Dictionary<string, MessageEntry> messages,
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
        if (offset < 0 || offset > stream.Length)
        {
            // A truncated/replaced log starts over; discard that file's old contributions.
            offset = 0;
            messages.Clear();
        }

        stream.Seek(offset, SeekOrigin.Begin);
        var buffer = new byte[ReadBufferSize];
        using var line = new MemoryStream();
        var currentPosition = offset;
        var lastCompleteOffset = offset;
        var lineStartOffset = offset;
        var discardingOversizedLine = false;

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
                if (!discardingOversizedLine && line.Length + segmentLength <= MaximumJsonLineBytes)
                {
                    line.Write(buffer, segmentStart, segmentLength);
                    ProcessCompleteLine(
                        line.GetBuffer().AsSpan(0, checked((int)line.Length)),
                        fileKey,
                        lineStartOffset,
                        messages);
                }

                line.SetLength(0);
                discardingOversizedLine = false;
                lastCompleteOffset = currentPosition + index + 1;
                lineStartOffset = lastCompleteOffset;
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
                    // Bound memory use; an oversized complete event is intentionally skipped.
                    line.SetLength(0);
                    discardingOversizedLine = true;
                }
            }

            currentPosition += count;
        }

        return new FileCursor
        {
            FileKey = fileKey,
            OffsetBytes = lastCompleteOffset
        };
    }

    private static void ProcessCompleteLine(
        Span<byte> utf8Line,
        string fileKey,
        long linePosition,
        Dictionary<string, MessageEntry> messages)
    {
        if (utf8Line.Length == 0)
        {
            return;
        }

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
            if (root.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            if (TryGetProperty(root, "$rewindTo", out var rewindTarget))
            {
                ApplyRewind(fileKey, rewindTarget, messages);
            }

            if (!TryGetString(root, "type", out var type)
                || !string.Equals(type, "gemini", StringComparison.OrdinalIgnoreCase)
                || !TryReadTimestamp(root, out var timestamp)
                || !TryGetProperty(root, "tokens", out var tokenObject)
                || tokenObject.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            var messageId = ReadMessageId(root);
            // Without an ID, line position is a stable per-file fallback for incremental reads;
            // repeated records without IDs cannot be reliably identified as revisions.
            var messageKey = messageId is null
                ? HashMessageKey(fileKey, "line:" + linePosition.ToString(CultureInfo.InvariantCulture))
                : HashMessageKey(fileKey, messageId);

            var localDate = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(timestamp, TimeZoneInfo.Local).DateTime)
                .ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var model = NormalizeModel(ReadString(root, "model")) ?? UnknownModel;
            var counters = ReadCounters(tokenObject);
            var revision = ReadRevision(root);
            if (messages.TryGetValue(messageKey, out var previous)
                && previous.Revision.HasValue
                && revision.HasValue
                && revision.Value < previous.Revision.Value)
            {
                // A delayed older revision does not replace a newer version of the same ID.
                return;
            }

            messages[messageKey] = new MessageEntry
            {
                FileKey = fileKey,
                MessageKey = messageKey,
                LocalDate = localDate,
                Model = model,
                TimestampUtcTicks = timestamp.UtcDateTime.Ticks,
                PositionBytes = linePosition,
                Revision = revision,
                Counters = counters
            };
        }
        catch (JsonException)
        {
            // A malformed complete line does not block later valid events.
        }
        catch (FormatException)
        {
            // Ignore malformed timestamps and continue with later lines.
        }
        catch (OverflowException)
        {
            // Ignore malformed numeric metadata.
        }
    }

    private static void ApplyRewind(
        string fileKey,
        JsonElement target,
        Dictionary<string, MessageEntry> messages)
    {
        if (TryExtractRewindId(target, out var messageId))
        {
            var key = HashMessageKey(fileKey, messageId);
            if (messages.TryGetValue(key, out var entry))
            {
                RemoveMessagesAfter(messages, entry.PositionBytes);
                return;
            }
        }

        if (TryExtractRewindTimestamp(target, out var timestamp))
        {
            var ticks = timestamp.UtcDateTime.Ticks;
            var keysToRemove = messages
                .Where(pair => pair.Value.TimestampUtcTicks > ticks)
                .Select(pair => pair.Key)
                .ToArray();
            foreach (var key in keysToRemove)
            {
                messages.Remove(key);
            }
        }
    }

    private static void RemoveMessagesAfter(Dictionary<string, MessageEntry> messages, long position)
    {
        var keysToRemove = messages
            .Where(pair => pair.Value.PositionBytes > position)
            .Select(pair => pair.Key)
            .ToArray();
        foreach (var key in keysToRemove)
        {
            messages.Remove(key);
        }
    }

    private static bool TryExtractRewindId(JsonElement target, out string messageId)
    {
        messageId = string.Empty;
        if (target.ValueKind == JsonValueKind.String)
        {
            var value = target.GetString();
            if (!string.IsNullOrWhiteSpace(value)
                && !DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out _))
            {
                messageId = value;
                return true;
            }

            return false;
        }

        if (target.ValueKind == JsonValueKind.Object)
        {
            foreach (var name in new[] { "id", "messageId", "message_id" })
            {
                if (TryGetString(target, name, out messageId))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool TryExtractRewindTimestamp(JsonElement target, out DateTimeOffset timestamp)
    {
        timestamp = default;
        if (target.ValueKind == JsonValueKind.String)
        {
            return DateTimeOffset.TryParse(
                target.GetString(),
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out timestamp);
        }

        if (target.ValueKind == JsonValueKind.Object)
        {
            foreach (var name in new[] { "timestamp", "time", "createdAt", "created_at" })
            {
                if (TryGetProperty(target, name, out var value) && TryParseTimestamp(value, out timestamp))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool TryReadTimestamp(JsonElement root, out DateTimeOffset timestamp)
    {
        timestamp = default;
        return TryGetProperty(root, "timestamp", out var value) && TryParseTimestamp(value, out timestamp);
    }

    private static bool TryParseTimestamp(JsonElement value, out DateTimeOffset timestamp)
    {
        timestamp = default;
        if (value.ValueKind == JsonValueKind.String)
        {
            return DateTimeOffset.TryParse(
                value.GetString(),
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out timestamp);
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number))
        {
            try
            {
                timestamp = Math.Abs(number) >= 100_000_000_000
                    ? DateTimeOffset.FromUnixTimeMilliseconds(number)
                    : DateTimeOffset.FromUnixTimeSeconds(number);
                return true;
            }
            catch (ArgumentOutOfRangeException)
            {
                return false;
            }
        }

        return false;
    }

    private static string? ReadMessageId(JsonElement root)
    {
        foreach (var name in new[] { "id", "messageId", "message_id", "uuid" })
        {
            if (TryGetString(root, name, out var value))
            {
                return value;
            }
        }

        return TryGetProperty(root, "message", out var message)
            && message.ValueKind == JsonValueKind.Object
            && TryGetString(message, "id", out var nestedId)
                ? nestedId
                : null;
    }

    private static long? ReadRevision(JsonElement root)
    {
        foreach (var name in new[] { "revision", "revisionId", "revision_id", "version", "editVersion" })
        {
            if (!TryGetProperty(root, name, out var value))
            {
                continue;
            }

            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number))
            {
                return number;
            }

            if (value.ValueKind == JsonValueKind.String
                && long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out number))
            {
                return number;
            }
        }

        return null;
    }

    private static TokenCounters ReadCounters(JsonElement tokens)
    {
        return new TokenCounters(
            ReadTokenCount(tokens, "input"),
            ReadTokenCount(tokens, "output"),
            ReadTokenCount(tokens, "cached"),
            ReadTokenCount(tokens, "thoughts"),
            ReadTokenCount(tokens, "tool"),
            ReadTokenCount(tokens, "total"));
    }

    private static long ReadTokenCount(JsonElement tokens, string name)
    {
        if (!TryGetProperty(tokens, name, out var value))
        {
            return 0;
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

        return 0;
    }

    private static bool TryGetString(JsonElement element, string propertyName, out string value)
    {
        value = string.Empty;
        if (!TryGetProperty(element, propertyName, out var property)
            || property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = property.GetString() ?? string.Empty;
        return value.Length > 0;
    }

    private static string? ReadString(JsonElement element, string propertyName)
    {
        return TryGetString(element, propertyName, out var value) ? value : null;
    }

    private static bool TryGetProperty(JsonElement element, string propertyName, out JsonElement property)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var candidate in element.EnumerateObject())
            {
                if (string.Equals(candidate.Name, propertyName, StringComparison.OrdinalIgnoreCase))
                {
                    property = candidate.Value;
                    return true;
                }
            }
        }

        property = default;
        return false;
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

            store.Aggregates ??= [];
            store.Cursors ??= [];
            store.Messages ??= [];
            return store;
        }
        catch (JsonException)
        {
            // Recover from a damaged aggregate file without scanning logs implicitly.
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

    private static IEnumerable<string> EnumerateChatFiles(string geminiHome)
    {
        var tmpRoot = Path.Combine(geminiHome, "tmp");
        if (!Directory.Exists(tmpRoot) || IsReparsePoint(tmpRoot))
        {
            yield break;
        }

        string[] projectDirectories;
        try
        {
            projectDirectories = Directory.GetDirectories(tmpRoot, "*", SearchOption.TopDirectoryOnly);
        }
        catch (IOException)
        {
            yield break;
        }
        catch (UnauthorizedAccessException)
        {
            yield break;
        }

        foreach (var projectDirectory in projectDirectories)
        {
            if (IsReparsePoint(projectDirectory))
            {
                continue;
            }

            var chatsDirectory = Path.Combine(projectDirectory, "chats");
            if (!Directory.Exists(chatsDirectory) || IsReparsePoint(chatsDirectory))
            {
                continue;
            }

            string[] files;
            try
            {
                files = Directory.GetFiles(chatsDirectory, "*", SearchOption.TopDirectoryOnly);
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
                var name = Path.GetFileName(file);
                if (name.StartsWith("session-", StringComparison.OrdinalIgnoreCase)
                    && name.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase)
                    && !IsReparsePoint(file))
                {
                    yield return file;
                }
            }
        }
    }

    private static bool IsReparsePoint(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch (IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }

    private static string? GetGeminiCliHome()
    {
        var configuredHome = Environment.GetEnvironmentVariable("GEMINI_CLI_HOME");
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
        return string.IsNullOrWhiteSpace(profile) ? null : Path.Combine(profile, ".gemini");
    }

    private static string HashFileCursor(string filePath)
    {
        var canonicalPath = Path.GetFullPath(filePath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (OperatingSystem.IsWindows())
        {
            canonicalPath = canonicalPath.ToUpperInvariant();
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalPath)));
    }

    private static string HashMessageKey(string fileKey, string messageId)
    {
        var identity = fileKey + "\n" + messageId;
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
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
        store.Messages = store.Messages
            .Where(message => TryParseDate(message.LocalDate, out var date)
                && date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture).CompareTo(oldestRetainedDate) >= 0)
            .ToList();
    }

    private static void RebuildAggregates(StoreDocument store)
    {
        store.Aggregates = store.Messages
            .GroupBy(message => (message.LocalDate, message.Model))
            .Select(group => new AggregateEntry
            {
                LocalDate = group.Key.LocalDate,
                Model = group.Key.Model,
                Counters = Sum(group.Select(message => message.Counters))
            })
            .OrderBy(row => row.LocalDate, StringComparer.Ordinal)
            .ThenBy(row => row.Model, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static IReadOnlyList<GeminiTokenUsageRow> SelectRows(
        StoreDocument store,
        DateOnly fromInclusive,
        DateOnly throughInclusive,
        string? model)
    {
        var modelFilter = NormalizeModel(model);
        return store.Aggregates
            .Select(row => TryParseDate(row.LocalDate, out var date)
                ? (Valid: true, Date: date, Row: row)
                : (Valid: false, Date: default, Row: row))
            .Where(item => item.Valid
                && item.Date >= fromInclusive
                && item.Date <= throughInclusive
                && (modelFilter is null || string.Equals(item.Row.Model, modelFilter, StringComparison.OrdinalIgnoreCase)))
            .Select(item => new GeminiTokenUsageRow(
                item.Date,
                item.Row.Model,
                item.Row.Counters.Input,
                item.Row.Counters.Output,
                item.Row.Counters.Cached,
                item.Row.Counters.Thoughts,
                item.Row.Counters.Tool,
                item.Row.Counters.Total))
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

    private static TokenCounters Sum(IEnumerable<TokenCounters> counters)
    {
        var total = TokenCounters.Zero;
        foreach (var counter in counters)
        {
            total = new TokenCounters(
                SaturatingAdd(total.Input, counter.Input),
                SaturatingAdd(total.Output, counter.Output),
                SaturatingAdd(total.Cached, counter.Cached),
                SaturatingAdd(total.Thoughts, counter.Thoughts),
                SaturatingAdd(total.Tool, counter.Tool),
                SaturatingAdd(total.Total, counter.Total));
        }

        return total;
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

    private readonly record struct TokenCounters(long Input, long Output, long Cached, long Thoughts, long Tool, long Total)
    {
        public static TokenCounters Zero => new(0, 0, 0, 0, 0, 0);
    }

    private sealed class StoreDocument
    {
        public StoreDocument() { }
        public int Version { get; set; } = StoreVersion;
        public List<AggregateEntry> Aggregates { get; set; } = [];
        public List<FileCursor> Cursors { get; set; } = [];
        public List<MessageEntry> Messages { get; set; } = [];
    }

    private sealed class AggregateEntry
    {
        public AggregateEntry() { }
        public string LocalDate { get; set; } = string.Empty;
        public string Model { get; set; } = string.Empty;
        public TokenCounters Counters { get; set; }
    }

    private sealed class FileCursor
    {
        public FileCursor() { }
        public string FileKey { get; set; } = string.Empty;
        public long OffsetBytes { get; set; }
    }

    private sealed class MessageEntry
    {
        public MessageEntry() { }
        public string FileKey { get; set; } = string.Empty;
        public string MessageKey { get; set; } = string.Empty;
        public string LocalDate { get; set; } = string.Empty;
        public string Model { get; set; } = string.Empty;
        public long TimestampUtcTicks { get; set; }
        public long PositionBytes { get; set; }
        public long? Revision { get; set; }
        public TokenCounters Counters { get; set; }
    }
}

/// <summary>Daily Gemini CLI token totals grouped by local date and model.</summary>
public sealed record GeminiTokenUsageRow(
    DateOnly LocalDate,
    string Model,
    long InputTokens,
    long OutputTokens,
    long CachedTokens,
    long ThoughtsTokens,
    long ToolTokens,
    long TotalTokens);
