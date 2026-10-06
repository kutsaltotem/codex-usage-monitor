using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CodexUsageMonitor;

/// <summary>Read-only quota connection to the signed-in, running Antigravity desktop app.</summary>
public static class AntigravityLocalQuota
{
    private static readonly HttpClient Http = new(new HttpClientHandler
    {
        AllowAutoRedirect = false,
        UseProxy = false,
        // Antigravity uses a self-signed certificate. Requests are restricted to loopback
        // ports owned by the identified local process, never remote hosts.
        ServerCertificateCustomValidationCallback = (request, _, _, _) =>
            request.RequestUri?.Host == "127.0.0.1"
    }) { Timeout = TimeSpan.FromSeconds(3) };

    public static async Task<ProviderSnapshot?> FetchAsync(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows()) return null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        cancellationToken = timeout.Token;
        foreach (var process in Process.GetProcessesByName("language_server"))
        {
            using (process)
            {
                try
                {
                    var path = process.MainModule?.FileName;
                    if (path is null || !path.Contains("\\Antigravity\\resources\\bin\\", StringComparison.OrdinalIgnoreCase)) continue;
                    var args = ReadCommandLine(process.Id);
                    var csrf = Regex.Match(args, @"--csrf_token(?:=|\s+)([^\s""']+)").Groups[1].Value;
                    if (string.IsNullOrEmpty(csrf)) continue;
                    var ports = OwnedPorts(process.Id);
                    var explicitPort = Regex.Match(args, @"--https_server_port(?:=|\s+)(\d+)").Groups[1].Value;
                    var candidates = new List<int>();
                    if (int.TryParse(explicitPort, out var port) && port > 0) candidates.Add(port);
                    var logPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Antigravity", "logs", "language_server.log");
                    if (File.Exists(logPath))
                    {
                        using var stream = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                        stream.Seek(Math.Max(0, stream.Length - 65536), SeekOrigin.Begin);
                        using var reader = new StreamReader(stream);
                        var tail = await reader.ReadToEndAsync(cancellationToken);
                        candidates.AddRange(Regex.Matches(tail, @"Language server listening on random port at (\d+)")
                            .Select(m => int.Parse(m.Groups[1].Value)).Reverse());
                    }
                    foreach (var candidate in candidates.Distinct().Where(ports.Contains).Take(3))
                    {
                        try
                        {
                            using var request = new HttpRequestMessage(HttpMethod.Post,
                                $"https://127.0.0.1:{candidate}/exa.language_server_pb.LanguageServerService/RetrieveUserQuotaSummary");
                            request.Headers.Add("X-Codeium-Csrf-Token", csrf);
                            request.Headers.Add("Connect-Protocol-Version", "1");
                            request.Content = new StringContent("{}", Encoding.UTF8, "application/json");
                            using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                            if (!response.IsSuccessStatusCode) continue;
                            await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
                            using var json = await JsonDocument.ParseAsync(body, cancellationToken: cancellationToken);
                            var windows = Parse(json.RootElement);
                            if (windows.Count > 0) return new ProviderSnapshot("gemini", "Gemini", "ok", null,
                                DateTimeOffset.UtcNow, "Antigravity · yerel kota servisi", windows);
                        }
                        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException && !cancellationToken.IsCancellationRequested) { }
                    }
                }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException or InvalidOperationException) { }
            }
        }
        return null;
    }

    public static IReadOnlyList<UsageWindow> Parse(JsonElement root)
    {
        if (root.TryGetProperty("response", out var response)) root = response;
        var windows = new List<UsageWindow>();
        if (!root.TryGetProperty("groups", out var groups) || groups.ValueKind != JsonValueKind.Array) return windows;
        foreach (var group in groups.EnumerateArray())
        {
            var label = CodexQuotaConnector.String(group, "displayName") ?? "";
            var gemini = label.Contains("Gemini", StringComparison.OrdinalIgnoreCase);
            if (!gemini && !label.Contains("Claude", StringComparison.OrdinalIgnoreCase)) continue;
            if (!group.TryGetProperty("buckets", out var buckets) || buckets.ValueKind != JsonValueKind.Array) continue;
            foreach (var bucket in buckets.EnumerateArray())
            {
                if (!bucket.TryGetProperty("remainingFraction", out var fraction) || !fraction.TryGetDouble(out var remaining)
                    || !double.IsFinite(remaining) || remaining < 0 || remaining > 1) continue;
                var name = CodexQuotaConnector.String(bucket, "displayName") ?? "";
                var kind = name.Contains("Weekly", StringComparison.OrdinalIgnoreCase) ? "weekly" :
                    name.Contains("Five", StringComparison.OrdinalIgnoreCase) ? "five_hour" : "quota";
                DateTimeOffset? reset = DateTimeOffset.TryParse(CodexQuotaConnector.String(bucket, "resetTime"), out var parsed) ? parsed : null;
                var display = (gemini ? "Gemini" : "AG · Claude/GPT") + " · " + (kind == "weekly" ? "Hafta" : kind == "five_hour" ? "5 saat" : name);
                // Secondary group remains separate from the user's Claude subscription.
                windows.Add(new UsageWindow(gemini ? kind : "antigravity_" + kind, display, (1 - remaining) * 100, remaining * 100, reset));
            }
        }
        return windows;
    }

    private static string ReadCommandLine(int pid)
    {
        var process = OpenProcess(0x1000, false, pid);
        if (process == IntPtr.Zero) return "";
        IntPtr buffer = IntPtr.Zero;
        try
        {
            NtQueryInformationProcess(process, 60, IntPtr.Zero, 0, out var size);
            if (size < 16 || size > 65536) return "";
            buffer = Marshal.AllocHGlobal(size);
            if (NtQueryInformationProcess(process, 60, buffer, size, out _) != 0) return "";
            var text = Marshal.PtrToStructure<UnicodeString>(buffer);
            return Marshal.PtrToStringUni(text.Buffer, text.Length / 2) ?? "";
        }
        finally { if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer); CloseHandle(process); }
    }

    private static HashSet<int> OwnedPorts(int pid)
    {
        var result = new HashSet<int>();
        var size = 0;
        GetExtendedTcpTable(IntPtr.Zero, ref size, false, 2, 5, 0);
        if (size < 4 || size > 4 * 1024 * 1024) return result;
        var table = Marshal.AllocHGlobal(size);
        try
        {
            if (GetExtendedTcpTable(table, ref size, false, 2, 5, 0) != 0) return result;
            var count = Marshal.ReadInt32(table);
            for (var i = 0; i < count && 4 + (i + 1) * 24 <= size; i++)
            {
                var row = IntPtr.Add(table, 4 + i * 24);
                if (Marshal.ReadInt32(row, 20) == pid)
                    result.Add(Marshal.ReadByte(row, 8) * 256 + Marshal.ReadByte(row, 9));
            }
        }
        finally { Marshal.FreeHGlobal(table); }
        return result;
    }

    [StructLayout(LayoutKind.Sequential)] private struct UnicodeString { public ushort Length, MaximumLength; public IntPtr Buffer; }
    [DllImport("kernel32.dll")] private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("ntdll.dll")] private static extern int NtQueryInformationProcess(IntPtr process, int infoClass, IntPtr info, int length, out int returnLength);
    [DllImport("iphlpapi.dll")] private static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool sort, int family, int tableClass, uint reserved);
}
