using System.Globalization;
using System.Text;
using CodexUsageMonitor;

namespace CodexUsageMonitor.Windows;

/// <summary>Writes the local quota history and token counters as Excel-friendly CSV (UTF-8 BOM, locale list separator).</summary>
internal static class CsvExporter
{
    private static readonly string[] QuotaHeader = ["Zaman", "Sağlayıcı", "Plan", "Pencere", "Kullanılan %", "Kalan %", "Sıfırlanma"];
    private static readonly string[] TokenHeader =
        ["Tarih", "Sağlayıcı", "Model", "Girdi", "Çıktı", "Önbellek okuma", "Önbellek yazma", "Düşünme", "Araç", "Toplam"];

    public static string Separator => CultureInfo.CurrentCulture.TextInfo.ListSeparator is { Length: 1 } separator ? separator : ";";

    public static async Task<int> WriteQuotaAsync(string path, IReadOnlyList<QuotaHistoryRecord> records, CancellationToken cancellationToken = default)
    {
        var rows = new List<string[]>();
        foreach (var record in records.OrderBy(item => item.GeneratedAt))
        foreach (var window in record.Windows)
            rows.Add([
                record.GeneratedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                ProviderName(record.Id, record.Name ?? record.Id),
                record.Plan ?? "",
                window.Label,
                Number(window.UsedPercent ?? (window.RemainingPercent is { } r ? 100 - r : null)),
                Number(window.RemainingPercent ?? (window.UsedPercent is { } u ? 100 - u : null)),
                window.ResetsAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) ?? ""
            ]);
        await WriteAsync(path, QuotaHeader, rows, cancellationToken).ConfigureAwait(false);
        return rows.Count;
    }

    public static async Task<int> WriteTokensAsync(string path, IReadOnlyList<TokenUsageRow> tokenRows, CancellationToken cancellationToken = default)
    {
        var rows = tokenRows.OrderBy(row => row.LocalDate).ThenBy(row => row.ProviderId).ThenBy(row => row.Model)
            .Select(row => new[]
            {
                row.LocalDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), ProviderName(row.ProviderId, row.ProviderId), row.Model,
                row.InputTokens.ToString(CultureInfo.InvariantCulture), row.OutputTokens.ToString(CultureInfo.InvariantCulture),
                row.CacheReadTokens.ToString(CultureInfo.InvariantCulture), row.CacheWriteTokens.ToString(CultureInfo.InvariantCulture),
                row.ThoughtsTokens.ToString(CultureInfo.InvariantCulture), row.ToolTokens.ToString(CultureInfo.InvariantCulture),
                row.TotalTokens.ToString(CultureInfo.InvariantCulture)
            }).ToList();
        await WriteAsync(path, TokenHeader, rows, cancellationToken).ConfigureAwait(false);
        return rows.Count;
    }

    internal static string Escape(string value, string separator)
    {
        // Leading = + - @ would be run as a formula by spreadsheets; values here are labels or numbers, never formulas.
        if (value.Length > 0 && value[0] is '=' or '+' or '@' || (value.Length > 1 && value[0] == '-' && !char.IsDigit(value[1])))
            value = "'" + value;
        return value.Contains(separator) || value.Contains('"') || value.Contains('\n') || value.Contains('\r')
            ? "\"" + value.Replace("\"", "\"\"") + "\""
            : value;
    }

    private static async Task WriteAsync(string path, string[] header, IEnumerable<string[]> rows, CancellationToken cancellationToken)
    {
        var separator = Separator;
        var builder = new StringBuilder();
        builder.AppendLine(string.Join(separator, header.Select(item => Escape(item, separator))));
        foreach (var row in rows) builder.AppendLine(string.Join(separator, row.Select(item => Escape(item, separator))));
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await File.WriteAllTextAsync(temporary, builder.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true), cancellationToken).ConfigureAwait(false);
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static string Number(double? value) =>
        value is { } number && double.IsFinite(number) ? Math.Round(number, 1).ToString("0.#", CultureInfo.CurrentCulture) : "";

    private static string ProviderName(string id, string fallback) => id switch
    {
        "chatgpt-codex" or "codex" => "Codex",
        "gemini" => "Gemini",
        "claude" or "claude-code" => "Claude",
        _ => string.IsNullOrWhiteSpace(fallback) ? id : fallback
    };
}
