using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CodexUsageMonitor;

/// <summary>
/// Opt-in reader for the token metadata in Claude Code's local JSONL session files.
/// It stores only daily/model token aggregates and opaque file/message keys.
/// </summary>
public sealed class ClaudeTokenHistoryReader
{
    public const int RetentionDays = 365;

    private const int StoreVersion = 1;
    private const int ReadBufferSize = 64 * 1024;
    private const int MaximumJsonLineBytes = 16 * 1024 * 1024;
    private const int MaximumHeaderBytes = 64 * 1024;
    private const string UnknownModel = "Unknown model";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    private readonly string _storePath;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public ClaudeTokenHistoryReader(string dataDirectory)
    {
        _storePath = Path.Combine(Path.GetFullPath(dataDirectory), "history", "claude-code-token-history.json");
    }

    public async Task<IReadOnlyList<TokenUsageRow>> CollectAndGetRowsAsync(
        DateOnly fromInclusive,
        DateOnly throughInclusive,
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
            PruneExpiredRows(store, DateOnly.FromDateTime(DateTime.Now), retentionDays);
            await SaveStoreAsync(store, cancellationToken).ConfigureAwait(false);
            return SelectRows(store, fromInclusive, throughInclusive);
        }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<TokenUsageRow>> GetRowsAsync(
        DateOnly fromInclusive,
        DateOnly throughInclusive,
        CancellationToken cancellationToken = default,
        int retentionDays = RetentionDays)
    {
        ValidatePeriod(fromInclusive, throughInclusive);
        ValidateRetentionDays(retentionDays);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var store = await LoadStoreAsync(cancellationToken).ConfigureAwait(false);
            var before = store.Files.Sum(file => file.Contributions.Count);
            PruneExpiredRows(store, DateOnly.FromDateTime(DateTime.Now), retentionDays);
            if (store.Files.Sum(file => file.Contributions.Count) != before)
                await SaveStoreAsync(store, cancellationToken).ConfigureAwait(false);
            return SelectRows(store, fromInclusive, throughInclusive);
        }
        finally { _gate.Release(); }
    }

    private static void ValidatePeriod(DateOnly fromInclusive, DateOnly throughInclusive)
    {
        if (fromInclusive > throughInclusive)
            throw new ArgumentOutOfRangeException(nameof(fromInclusive), "The start date must not be after the end date.");
    }

    private async Task CollectIntoStoreAsync(StoreDocument store, CancellationToken cancellationToken)
    {
        var projectsRoot = Path.Combine(LocalCredentialReader.ClaudeHome, "projects");
        if (!Directory.Exists(projectsRoot)) return;

        var files = store.Files
            .Where(file => file is not null && !string.IsNullOrWhiteSpace(file.FileKey))
            .GroupBy(file => file.FileKey, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Last(), StringComparer.Ordinal);

        foreach (var path in EnumerateJsonlFiles(projectsRoot))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var fileKey = HashPath(path);
                var fingerprint = ReadHeaderFingerprint(path);
                files.TryGetValue(fileKey, out var previous);
                var cursor = await ReadFileAsync(path, fileKey, fingerprint, previous, cancellationToken).ConfigureAwait(false);
                files[fileKey] = cursor;
            }
            catch (OperationCanceledException) { throw; }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            catch (ArgumentException) { }
        }

        store.Files = files.Values.OrderBy(file => file.FileKey, StringComparer.Ordinal).ToList();
    }

    private static async Task<FileCursor> ReadFileAsync(
        string path,
        string fileKey,
        string? fingerprint,
        FileCursor? previous,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, ReadBufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        var reset = previous is not null
            && (previous.OffsetBytes < 0
                || previous.OffsetBytes > stream.Length
                || (!string.IsNullOrWhiteSpace(fingerprint)
                    && !string.IsNullOrWhiteSpace(previous.Fingerprint)
                    && !string.Equals(fingerprint, previous.Fingerprint, StringComparison.Ordinal)));
        var offset = reset ? 0 : previous?.OffsetBytes ?? 0;
        var contributions = reset
            ? new Dictionary<string, MessageUsage>(StringComparer.Ordinal)
            : (previous?.Contributions ?? [])
                .Where(item => item is not null && !string.IsNullOrWhiteSpace(item.MessageKey))
                .GroupBy(item => item.MessageKey, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.Last(), StringComparer.Ordinal);

        stream.Seek(offset, SeekOrigin.Begin);
        var buffer = new byte[ReadBufferSize];
        using var line = new MemoryStream();
        var position = offset;
        var lastCompleteOffset = offset;
        var lineStartOffset = offset;
        var segmentStart = 0;
        var discardingOversizedLine = false;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = await stream.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (count == 0) break;
            segmentStart = 0;

            for (var index = 0; index < count; index++)
            {
                if (buffer[index] != (byte)'\n') continue;
                var segmentLength = index - segmentStart;
                if (!discardingOversizedLine)
                {
                    if (line.Length + segmentLength <= MaximumJsonLineBytes)
                    {
                        line.Write(buffer, segmentStart, segmentLength);
                        var value = ParseUsage(line.GetBuffer().AsSpan(0, checked((int)line.Length)), fileKey, lineStartOffset);
                        if (value is not null)
                        {
                            if (contributions.TryGetValue(value.MessageKey, out var existing))
                                contributions[value.MessageKey] = MergeUsage(existing, value);
                            else
                                contributions[value.MessageKey] = value;
                        }
                    }
                }

                line.SetLength(0);
                discardingOversizedLine = false;
                lastCompleteOffset = position + index + 1;
                lineStartOffset = lastCompleteOffset;
                segmentStart = index + 1;
            }

            var remaining = count - segmentStart;
            if (remaining > 0 && !discardingOversizedLine)
            {
                if (line.Length + remaining <= MaximumJsonLineBytes)
                    line.Write(buffer, segmentStart, remaining);
                else
                {
                    line.SetLength(0);
                    discardingOversizedLine = true;
                }
            }
            position += count;
        }

        return new FileCursor
        {
            FileKey = fileKey,
            Fingerprint = fingerprint,
            OffsetBytes = lastCompleteOffset,
            Contributions = contributions.Values
                .OrderBy(item => item.LocalDate, StringComparer.Ordinal)
                .ThenBy(item => item.Model, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.MessageKey, StringComparer.Ordinal)
                .ToList()
        };
    }

    private static MessageUsage? ParseUsage(ReadOnlySpan<byte> utf8Line, string fileKey, long lineOffset)
    {
        if (utf8Line.Length == 0) return null;
        if (utf8Line.Length >= 3 && utf8Line[0] == 0xEF && utf8Line[1] == 0xBB && utf8Line[2] == 0xBF)
            utf8Line = utf8Line[3..];
        if (utf8Line.Length > 0 && utf8Line[^1] == (byte)'\r') utf8Line = utf8Line[..^1];

        try
        {
            using var document = JsonDocument.Parse(utf8Line.ToArray());
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !TryGetString(root, "type", out var type)
                || !string.Equals(type, "assistant", StringComparison.Ordinal)
                || !root.TryGetProperty("message", out var message)
                || message.ValueKind != JsonValueKind.Object
                || !message.TryGetProperty("usage", out var usage)
                || usage.ValueKind != JsonValueKind.Object)
                return null;

            if (!TryReadDate(root, message, out var localDate)) return null;
            var model = ReadString(message, "model") ?? ReadString(root, "model") ?? UnknownModel;
            var input = ReadCounter(usage, "input_tokens");
            var output = ReadCounter(usage, "output_tokens");
            var cacheRead = ReadCounter(usage, "cache_read_input_tokens");
            var cacheWrite = ReadCounter(usage, "cache_creation_input_tokens");
            if (cacheWrite == 0 && usage.TryGetProperty("cache_creation", out var cacheCreation)
                && cacheCreation.ValueKind == JsonValueKind.Object)
                cacheWrite = Add(ReadCounter(cacheCreation, "ephemeral_5m_input_tokens"),
                    ReadCounter(cacheCreation, "ephemeral_1h_input_tokens"));
            if (input == 0 && output == 0 && cacheRead == 0 && cacheWrite == 0) return null;

            var id = ReadString(message, "id")
                ?? ReadString(root, "requestId")
                ?? ReadString(root, "uuid");
            var keyMaterial = id is not null
                ? $"file:{fileKey}|message:{id}"
                : $"file:{fileKey}|offset:{lineOffset.ToString(CultureInfo.InvariantCulture)}";

            return new MessageUsage
            {
                MessageKey = HashOpaque(Encoding.UTF8.GetBytes(keyMaterial)),
                LocalDate = localDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                Model = NormalizeModel(model),
                InputTokens = input,
                OutputTokens = output,
                CacheReadTokens = cacheRead,
                CacheWriteTokens = cacheWrite
            };
        }
        catch (JsonException) { return null; }
        catch (FormatException) { return null; }
    }

    private static MessageUsage MergeUsage(MessageUsage previous, MessageUsage current) => new()
    {
        MessageKey = current.MessageKey,
        LocalDate = current.LocalDate,
        Model = current.Model,
        InputTokens = Math.Max(previous.InputTokens, current.InputTokens),
        OutputTokens = Math.Max(previous.OutputTokens, current.OutputTokens),
        CacheReadTokens = Math.Max(previous.CacheReadTokens, current.CacheReadTokens),
        CacheWriteTokens = Math.Max(previous.CacheWriteTokens, current.CacheWriteTokens)
    };

    private static bool TryReadDate(JsonElement root, JsonElement message, out DateOnly localDate)
    {
        localDate = default;
        var timestamp = ReadString(root, "timestamp") ?? ReadString(message, "timestamp");
        if (timestamp is null || !DateTimeOffset.TryParse(timestamp, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
            return false;
        localDate = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(parsed, TimeZoneInfo.Local).DateTime);
        return true;
    }

    private static long ReadCounter(JsonElement parent, string property)
    {
        if (parent.ValueKind != JsonValueKind.Object || !parent.TryGetProperty(property, out var value)) return 0;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number)) return Math.Max(0, number);
        if (value.ValueKind == JsonValueKind.String
            && long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out number))
            return Math.Max(0, number);
        return 0;
    }

    private static string? ReadString(JsonElement parent, string property) =>
        parent.ValueKind == JsonValueKind.Object
        && parent.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool TryGetString(JsonElement parent, string property, out string value)
    {
        value = ReadString(parent, property) ?? string.Empty;
        return value.Length > 0;
    }

    private static string NormalizeModel(string? model) =>
        string.IsNullOrWhiteSpace(model) || model.Length > 128 ? UnknownModel : model;

    private static string? ReadHeaderFingerprint(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.SequentialScan);
            using var header = new MemoryStream();
            var buffer = new byte[4096];
            while (header.Length < MaximumHeaderBytes)
            {
                var requested = (int)Math.Min(buffer.Length, MaximumHeaderBytes - header.Length);
                var count = stream.Read(buffer, 0, requested);
                if (count == 0) return null;
                var newline = Array.IndexOf(buffer, (byte)'\n', 0, count);
                if (newline >= 0)
                {
                    header.Write(buffer, 0, newline);
                    break;
                }
                header.Write(buffer, 0, count);
            }

            using var document = JsonDocument.Parse(header.ToArray());
            var root = document.RootElement;
            var identity = ReadString(root, "sessionId")
                ?? ReadString(root, "session_id")
                ?? ReadString(root, "uuid");
            return identity is null ? null : HashOpaque(Encoding.UTF8.GetBytes("session:" + identity));
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
        catch (JsonException) { return null; }
    }

    private static IEnumerable<string> EnumerateJsonlFiles(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            string[] files;
            string[] children;
            try
            {
                files = Directory.GetFiles(directory, "*.jsonl", SearchOption.TopDirectoryOnly);
                children = Directory.GetDirectories(directory, "*", SearchOption.TopDirectoryOnly);
            }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }

            foreach (var file in files.OrderBy(path => path, StringComparer.Ordinal))
                yield return file;
            foreach (var child in children)
            {
                try
                {
                    if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) == 0)
                        pending.Push(child);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    private static string HashPath(string path)
    {
        var canonical = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (OperatingSystem.IsWindows()) canonical = canonical.ToUpperInvariant();
        return HashOpaque(Encoding.UTF8.GetBytes("path:" + canonical));
    }

    private static string HashOpaque(ReadOnlySpan<byte> data) => Convert.ToHexString(SHA256.HashData(data));

    private static void ValidateRetentionDays(int retentionDays)
    {
        if (retentionDays is not (30 or 90 or 365))
            throw new ArgumentOutOfRangeException(nameof(retentionDays), "Retention must be 30, 90, or 365 days.");
    }

    private static void PruneExpiredRows(StoreDocument store, DateOnly today, int retentionDays)
    {
        var oldest = today.AddDays(-(retentionDays - 1)).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        foreach (var file in store.Files)
            file.Contributions = file.Contributions
                .Where(item => DateOnly.TryParseExact(item.LocalDate, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var date)
                    && date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture).CompareTo(oldest) >= 0)
                .ToList();
    }

    private static IReadOnlyList<TokenUsageRow> SelectRows(StoreDocument store, DateOnly from, DateOnly through)
    {
        var totals = new Dictionary<(string Date, string Model), TokenCounters>();
        foreach (var file in store.Files)
        foreach (var contribution in file.Contributions)
        {
            if (!DateOnly.TryParseExact(contribution.LocalDate, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var date)
                || date < from || date > through)
                continue;

            var key = (contribution.LocalDate, contribution.Model);
            totals.TryGetValue(key, out var existing);
            totals[key] = new TokenCounters(
                Add(existing.Input, contribution.InputTokens),
                Add(existing.Output, contribution.OutputTokens),
                Add(existing.CacheRead, contribution.CacheReadTokens),
                Add(existing.CacheWrite, contribution.CacheWriteTokens));
        }

        return totals.Select(pair => new TokenUsageRow(
                "claude",
                DateOnly.ParseExact(pair.Key.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture),
                pair.Key.Model,
                pair.Value.Input,
                pair.Value.Output,
                pair.Value.CacheRead,
                pair.Value.CacheWrite,
                0,
                0,
                Add(Add(pair.Value.Input, pair.Value.Output), Add(pair.Value.CacheRead, pair.Value.CacheWrite))))
            .OrderBy(row => row.LocalDate)
            .ThenBy(row => row.Model, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private async Task<StoreDocument> LoadStoreAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = File.OpenRead(_storePath);
            var store = await JsonSerializer.DeserializeAsync<StoreDocument>(stream, JsonOptions, cancellationToken).ConfigureAwait(false);
            if (store is not { Version: StoreVersion }) return new StoreDocument { Version = StoreVersion };
            store.Files = (store.Files ?? []).Where(file => file is not null).ToList();
            foreach (var file in store.Files) file.Contributions = (file.Contributions ?? []).Where(item => item is not null).ToList();
            return store;
        }
        catch (FileNotFoundException) { return new StoreDocument { Version = StoreVersion }; }
        catch (DirectoryNotFoundException) { return new StoreDocument { Version = StoreVersion }; }
        catch (JsonException) { return new StoreDocument { Version = StoreVersion }; }
        catch (IOException) { return new StoreDocument { Version = StoreVersion }; }
        catch (UnauthorizedAccessException) { return new StoreDocument { Version = StoreVersion }; }
    }

    private async Task SaveStoreAsync(StoreDocument store, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_storePath)!);
        var temporary = _storePath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                16 * 1024, useAsync: true))
                await JsonSerializer.SerializeAsync(stream, store, JsonOptions, cancellationToken).ConfigureAwait(false);
            File.Move(temporary, _storePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static long Add(long left, long right) =>
        long.MaxValue - left < right ? long.MaxValue : left + Math.Max(0, right);

    private readonly record struct TokenCounters(long Input, long Output, long CacheRead, long CacheWrite);

    private sealed class StoreDocument
    {
        public int Version { get; set; }
        public List<FileCursor> Files { get; set; } = [];
    }

    private sealed class FileCursor
    {
        public string FileKey { get; set; } = string.Empty;
        public string? Fingerprint { get; set; }
        public long OffsetBytes { get; set; }
        public List<MessageUsage> Contributions { get; set; } = [];
    }

    private sealed class MessageUsage
    {
        public string MessageKey { get; set; } = string.Empty;
        public string LocalDate { get; set; } = string.Empty;
        public string Model { get; set; } = UnknownModel;
        public long InputTokens { get; set; }
        public long OutputTokens { get; set; }
        public long CacheReadTokens { get; set; }
        public long CacheWriteTokens { get; set; }
    }
}
