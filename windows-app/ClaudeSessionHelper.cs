using System.Diagnostics;

namespace CodexUsageMonitor;

/// <summary>
/// Finds the Claude Code executable that is already installed on this PC. The monitor never touches the
/// OAuth endpoints or the credentials file itself: refreshing is delegated to Claude Code, which owns the login.
/// </summary>
public static class ClaudeSessionHelper
{
    private static readonly TimeSpan RefreshCooldown = TimeSpan.FromMinutes(5);
    private static DateTimeOffset _lastRefreshAttempt = DateTimeOffset.MinValue;
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public static string? FindExecutable()
    {
        var candidates = new List<string>();
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var desktopRoot = Path.Combine(appData, "Claude", "claude-code");
        try
        {
            if (Directory.Exists(desktopRoot))
                candidates.AddRange(Directory.EnumerateFiles(desktopRoot, "claude.exe", SearchOption.AllDirectories)
                    .OrderByDescending(File.GetLastWriteTimeUtc));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }

        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        candidates.Add(Path.Combine(profile, ".local", "bin", "claude.exe"));
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator,
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try { candidates.Add(Path.Combine(directory, "claude.exe")); }
            catch (ArgumentException) { }
        }
        return candidates.FirstOrDefault(File.Exists);
    }

    /// <summary>
    /// Asks Claude Code to check its own login ("claude auth status"); Claude Code renews an expired access token
    /// with its stored refresh token while doing so. Rate limited so a broken login cannot spawn processes every poll.
    /// </summary>
    public static async Task<bool> TryRefreshAsync(CancellationToken cancellationToken)
    {
        if (!await Gate.WaitAsync(0, cancellationToken).ConfigureAwait(false)) return false;
        try
        {
            if (DateTimeOffset.UtcNow - _lastRefreshAttempt < RefreshCooldown) return false;
            _lastRefreshAttempt = DateTimeOffset.UtcNow;
            var executable = FindExecutable();
            if (executable is null) return false;

            var startInfo = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true
            };
            startInfo.ArgumentList.Add("auth");
            startInfo.ArgumentList.Add("status");
            using var process = Process.Start(startInfo);
            if (process is null) return false;
            process.StandardInput.Close();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(25));
            try
            {
                var drain = Task.WhenAll(process.StandardOutput.ReadToEndAsync(timeout.Token),
                    process.StandardError.ReadToEndAsync(timeout.Token));
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                await drain.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch (Exception) { }
                if (cancellationToken.IsCancellationRequested) throw;
                return false;
            }
            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return false;
        }
        finally { Gate.Release(); }
    }

    /// <summary>Opens Claude Code's own browser login in a visible console window. The user completes it.</summary>
    public static bool TryStartLogin(out string error)
    {
        error = "";
        var executable = FindExecutable();
        if (executable is null)
        {
            error = "Claude Code bulunamadı. Claude masaüstü uygulamasını bir kez açıp tekrar deneyin.";
            return false;
        }
        try
        {
            var startInfo = new ProcessStartInfo(executable)
            {
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(executable)!,
                Arguments = "auth login --claudeai"
            };
            using var process = Process.Start(startInfo);
            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            error = "Claude Code girişi başlatılamadı: " + ex.Message;
            return false;
        }
    }
}
