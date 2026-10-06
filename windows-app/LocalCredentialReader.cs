using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace CodexUsageMonitor;

public static class LocalCredentialReader
{
    public const string CodexAuthFileName = "auth.json";
    public const string GeminiCredentialTarget = "gemini:antigravity";
    public const string ClaudeCredentialsFileName = ".credentials.json";

    public static string CodexHome =>
        Environment.GetEnvironmentVariable("CODEX_HOME") is { Length: > 0 } configured
            ? configured
            : Path.Combine(UserProfile, ".codex");

    public static string GeminiHome
    {
        get
        {
            var cliHome = Environment.GetEnvironmentVariable("GEMINI_CLI_HOME");
            if (!string.IsNullOrWhiteSpace(cliHome))
                return Environment.ExpandEnvironmentVariables(cliHome);

            var geminiHome = Environment.GetEnvironmentVariable("GEMINI_HOME");
            return !string.IsNullOrWhiteSpace(geminiHome)
                ? Environment.ExpandEnvironmentVariables(geminiHome)
                : Path.Combine(UserProfile, ".gemini");
        }
    }

    public static string ClaudeHome
    {
        get
        {
            var configHome = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
            if (string.IsNullOrWhiteSpace(configHome)) return Path.Combine(UserProfile, ".claude");

            var expanded = Environment.ExpandEnvironmentVariables(configHome.Trim());
            if (expanded == "~") return UserProfile;
            if (expanded.StartsWith("~/", StringComparison.Ordinal) || expanded.StartsWith("~\\", StringComparison.Ordinal))
                expanded = Path.Combine(UserProfile, expanded[2..]);
            return Path.GetFullPath(expanded);
        }
    }

    private static string UserProfile =>
        Environment.GetEnvironmentVariable("USERPROFILE") is { Length: > 0 } profile
            ? profile
            : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public static LocalCredential? ReadCodex()
    {
        var path = Path.Combine(CodexHome, CodexAuthFileName);
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            var tokens = root.TryGetProperty("tokens", out var nested) ? nested : default;
            var accessToken = ReadString(tokens, "access_token");
            if (string.IsNullOrWhiteSpace(accessToken))
            {
                var hasApiKey = ReadString(root, "api_key") is not null
                    || ReadString(root, "OPENAI_API_KEY") is not null
                    || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OPENAI_API_KEY"));
                return hasApiKey
                    ? new LocalCredential("", null, null, null, "codex-auth-file", path, ApiKeyOnly: true)
                    : null;
            }

            return new LocalCredential(
                accessToken,
                ReadString(tokens, "refresh_token"),
                ReadString(tokens, "account_id"),
                ReadJwtExpiry(accessToken),
                "codex-auth-file",
                path);
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
        catch (JsonException) { return null; }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    public static LocalCredential? ReadGemini()
    {
        if (OperatingSystem.IsWindows())
        {
            try
            {
                var raw = WindowsCredentialManager.ReadGeneric(GeminiCredentialTarget);
                var fromCredentialManager = ParseGeminiCredential(raw, "windows-credential-manager");
                if (fromCredentialManager is not null) return fromCredentialManager;
            }
            catch (Exception) { /* Fall back to the CLI's local credential files. */ }
        }

        var candidates = new[]
        {
            (Path.Combine(GeminiHome, "antigravity-cli", "antigravity-oauth-token"), "antigravity-token-file"),
            (Path.Combine(GeminiHome, "oauth_creds.json"), "gemini-oauth-file")
        };

        foreach (var (path, source) in candidates)
        {
            try
            {
                var credential = ParseGeminiCredential(File.ReadAllText(path), source, path);
                if (credential is not null) return credential;
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
            catch (JsonException) { }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return null;
    }

    public static LocalCredential? ReadClaude()
    {
        var path = Path.Combine(ClaudeHome, ClaudeCredentialsFileName);
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("claudeAiOauth", out var oauth)
                || oauth.ValueKind != JsonValueKind.Object)
                return null;

            var accessToken = ReadString(oauth, "accessToken");
            if (string.IsNullOrWhiteSpace(accessToken)) return null;

            return new LocalCredential(
                accessToken,
                ReadString(oauth, "refreshToken"),
                null,
                ReadExpiry(oauth, "expiresAt"),
                "claude-code-credentials",
                path);
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
        catch (JsonException) { return null; }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    private static LocalCredential? ParseGeminiCredential(string? raw, string source, string? path = null)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        using var document = JsonDocument.Parse(raw);
        var root = document.RootElement;
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("token", out var token) && token.ValueKind == JsonValueKind.Object)
            root = token;

        var accessToken = ReadString(root, "access_token") ?? ReadString(root, "accessToken");
        var refreshToken = ReadString(root, "refresh_token") ?? ReadString(root, "refreshToken");
        if (string.IsNullOrWhiteSpace(accessToken)) return null;

        return new LocalCredential(
            accessToken,
            refreshToken,
            null,
            ReadExpiry(root, "expiry") ?? ReadExpiry(root, "expiry_date") ?? ReadExpiry(root, "expires_at"),
            source,
            path);
    }

    private static string? ReadString(JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(property, out var value)) return null;
        return value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }

    private static DateTimeOffset? ReadExpiry(JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(property, out var value)) return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number))
        {
            try { return DateTimeOffset.FromUnixTimeMilliseconds(number > 1_000_000_000_000 ? (long)number : (long)(number * 1000)); }
            catch (ArgumentOutOfRangeException) { return null; }
        }
        if (value.ValueKind == JsonValueKind.String)
        {
            var text = value.GetString();
            if (DateTimeOffset.TryParse(text, out var date)) return date;
            if (long.TryParse(text, out var epoch))
            {
                try { return DateTimeOffset.FromUnixTimeMilliseconds(epoch > 1_000_000_000_000 ? epoch : epoch * 1000); }
                catch (ArgumentOutOfRangeException) { return null; }
            }
        }
        return null;
    }

    private static DateTimeOffset? ReadJwtExpiry(string token)
    {
        try
        {
            var parts = token.Split('.');
            if (parts.Length < 2) return null;
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + ((4 - payload.Length % 4) % 4), '=');
            using var document = JsonDocument.Parse(Convert.FromBase64String(payload));
            if (document.RootElement.TryGetProperty("exp", out var exp) && exp.TryGetInt64(out var seconds))
                return DateTimeOffset.FromUnixTimeSeconds(seconds);
        }
        catch (Exception) { }
        return null;
    }
}

internal static class WindowsCredentialManager
{
    private const uint GenericCredential = 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeCredential
    {
        public uint Flags;
        public uint Type;
        public IntPtr TargetName;
        public IntPtr Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public IntPtr TargetAlias;
        public IntPtr UserName;
    }

    [DllImport("Advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredRead(string target, uint type, uint flags, out IntPtr credential);

    [DllImport("Advapi32.dll", EntryPoint = "CredFree", SetLastError = false)]
    private static extern void CredFree(IntPtr credential);

    public static string? ReadGeneric(string target)
    {
        if (!CredRead(target, GenericCredential, 0, out var pointer)) return null;
        try
        {
            var credential = Marshal.PtrToStructure<NativeCredential>(pointer);
            if (credential.CredentialBlob == IntPtr.Zero || credential.CredentialBlobSize == 0 || credential.CredentialBlobSize > 1_048_576)
                return null;
            var bytes = new byte[(int)credential.CredentialBlobSize];
            Marshal.Copy(credential.CredentialBlob, bytes, 0, bytes.Length);
            return Encoding.UTF8.GetString(bytes).TrimEnd('\0');
        }
        finally { CredFree(pointer); }
    }
}
