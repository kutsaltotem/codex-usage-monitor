using System.Diagnostics;
using System.Reflection;
using Microsoft.Win32;

namespace CodexUsageMonitor.Windows;

internal static class WindowsStartupRegistration
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "CodexUsageMonitor";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        return key?.GetValue(RunValueName) is string command && !string.IsNullOrWhiteSpace(command);
    }

    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true)
            ?? throw new InvalidOperationException("Windows başlangıç kaydı açılamadı.");

        if (!enabled)
        {
            key.DeleteValue(RunValueName, throwOnMissingValue: false);
            return;
        }

        key.SetValue(RunValueName, GetStartupCommand(), RegistryValueKind.String);
    }

    private static string GetStartupCommand()
    {
        var processPath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(processPath))
        {
            using var process = Process.GetCurrentProcess();
            processPath = process.MainModule?.FileName;
        }
        if (string.IsNullOrWhiteSpace(processPath))
            throw new InvalidOperationException("Uygulama yolu belirlenemedi.");

        processPath = Path.GetFullPath(processPath);
        if (string.Equals(Path.GetFileNameWithoutExtension(processPath), "dotnet", StringComparison.OrdinalIgnoreCase))
        {
            var assemblyPath = Assembly.GetExecutingAssembly().Location;
            if (string.IsNullOrWhiteSpace(assemblyPath))
                throw new InvalidOperationException("Geliştirme çalıştırması için uygulama DLL yolu belirlenemedi.");
            return $"\"{processPath}\" \"{Path.GetFullPath(assemblyPath)}\"";
        }

        return $"\"{processPath}\"";
    }
}
