using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Automation;

namespace CodexUsageMonitor.Windows;

/// <summary>Isolate Windows 11 XAML UI Automation provider memory in a short-lived process.</summary>
internal static class TaskbarGeometryProbe
{
    internal static string Read()
    {
        try
        {
            var taskbar = FindWindow("Shell_TrayWnd", null);
            if (taskbar == IntPtr.Zero) return "[]";
            var root = AutomationElement.FromHandle(taskbar);
            var tray = root.FindFirst(TreeScope.Descendants, new OrCondition(
                new PropertyCondition(AutomationElement.AutomationIdProperty, "SystemTrayFrame"),
                new PropertyCondition(AutomationElement.ClassNameProperty, "SystemTrayFrame")));
            if (tray is null) return "[]";
            var rect = tray.Current.BoundingRectangle;
            return rect.IsEmpty ? "[]" : JsonSerializer.Serialize(new[] { (int)rect.Left, (int)rect.Top, (int)rect.Right, (int)rect.Bottom });
        }
        catch (Exception) { return "[]"; }
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string className, string? title);
}
