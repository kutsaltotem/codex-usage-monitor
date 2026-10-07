using System.Diagnostics;

internal static class Program
{
    private static void Main(string[] args)
    {
        // No UI framework, polling, provider requests, or credentials. Block on the child process handle.
        using var single = new Mutex(true,@"Local\CodexUsageMonitor.Supervisor",out var owner);
        if (!owner) return;
        try
        {
            var executable = Path.Combine(AppContext.BaseDirectory,"CodexUsageMonitor.exe");
            var restartDelay = TimeSpan.FromMinutes(1);
            while (File.Exists(executable))
            {
                try
                {
                    using var child = Process.Start(new ProcessStartInfo(executable,"--background-start")
                    {
                        WorkingDirectory = AppContext.BaseDirectory,UseShellExecute=false,CreateNoWindow=true
                    });
                    if (child is null) return;
                    child.WaitForExit();
                    // The app's explicit tray Exit returns0. Never undo an intentional close.
                    if (child.ExitCode == 0) return;
                }
                catch (Exception error) when (error is System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException) { }
                Thread.Sleep(restartDelay);
            }
        }
        finally { single.ReleaseMutex(); }
    }
}
