using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32;

namespace CodexUsageMonitor.Windows;

internal static class WindowsStartupRegistration
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "CodexUsageMonitor";
    private static string TaskName => "CodexUsageMonitor-" + WindowsIdentity.GetCurrent().User!.Value;

    public static bool IsEnabled()
    {
        dynamic service = Connect();
        try
        {
            dynamic folder = service.GetFolder(@"\");
            try { return (bool)folder.GetTask(TaskName).Enabled || LegacyEnabled(); }
            catch (COMException) { return LegacyEnabled(); }
        }
        finally { Marshal.FinalReleaseComObject(service); }
    }

    public static void SetEnabled(bool enabled)
    {
        dynamic service = Connect();
        try
        {
            dynamic folder = service.GetFolder(@"\");
            if (!enabled)
            {
                try { folder.DeleteTask(TaskName,0); }
                catch (COMException error) when (error.HResult == unchecked((int)0x80070002)) { }
            }
            else
            {
                var path = Environment.ProcessPath ?? throw new InvalidOperationException("Uygulama yolu bulunamadı.");
                var args = "--background-start";
                if (Path.GetFileNameWithoutExtension(path).Equals("dotnet",StringComparison.OrdinalIgnoreCase))
                    args = $"\"{Assembly.GetExecutingAssembly().Location}\" " + args;
                var sid = WindowsIdentity.GetCurrent().User!.Value;
                dynamic task = service.NewTask(0);
                task.RegistrationInfo.Description = "Kullanım göstergesi: oturum açıldığında başlar, beklenmedik kapanmada yeniden dener. Çıkış seçimiyle kapanır.";
                task.Principal.UserId = sid;
                task.Principal.LogonType = 3; // InteractiveToken: no stored password or elevation.
                task.Principal.RunLevel = 0;
                dynamic trigger = task.Triggers.Create(9); // Logon.
                trigger.UserId = sid;
                trigger.Delay = "PT10S";
                dynamic action = task.Actions.Create(0);
                var supervisor = Path.Combine(AppContext.BaseDirectory,"UsageMonitorSupervisor.exe");
                action.Path = File.Exists(supervisor) ? supervisor : Path.GetFullPath(path);
                action.Arguments = File.Exists(supervisor) ? "" : args;
                action.WorkingDirectory = AppContext.BaseDirectory;
                task.Settings.Enabled = true;
                task.Settings.StartWhenAvailable = true;
                task.Settings.DisallowStartIfOnBatteries = false;
                task.Settings.StopIfGoingOnBatteries = false;
                task.Settings.ExecutionTimeLimit = "PT0S";
                task.Settings.MultipleInstances = 2; // IgnoreNew.
                task.Settings.RestartCount = 999;
                task.Settings.RestartInterval = "PT1M";
                folder.RegisterTaskDefinition(TaskName,task,6,sid,null,3,null);
            }
            using var legacy = Registry.CurrentUser.OpenSubKey(RunKeyPath,true);
            legacy?.DeleteValue(RunValueName,false);
        }
        finally { Marshal.FinalReleaseComObject(service); }
    }

    private static dynamic Connect()
    {
        dynamic service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service")!)!;
        service.Connect();
        return service;
    }

    private static bool LegacyEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
        return key?.GetValue(RunValueName) is string command && !string.IsNullOrWhiteSpace(command);
    }
}
