using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Automation;

namespace CodexUsageMonitor.Windows;

/// <summary>Read native tray and accessible taskbar controls without provider network requests.</summary>
internal static class TaskbarGeometryProbe
{
    internal static string Read()
    {
        try
        {
            var taskbar = FindWindow("Shell_TrayWnd", null);
            if (taskbar == IntPtr.Zero) return "[]";
            var nativeTray = FindWindowEx(taskbar, IntPtr.Zero, "TrayNotifyWnd", null);
            if (nativeTray != IntPtr.Zero && GetWindowRect(nativeTray, out var nativeRect))
                return JsonSerializer.Serialize(new[] { nativeRect.Left, nativeRect.Top, nativeRect.Right, nativeRect.Bottom });
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
    internal sealed record Layout(int[] Tray, int[][] Occupied);

    // Called only on a worker: Explorer accessibility must never block the UI.
    internal static Layout? ReadLayout(IntPtr taskbar)
    {
        try
        {
            var root = AutomationElement.FromHandle(taskbar);
            var request = new CacheRequest();
            request.Add(AutomationElement.BoundingRectangleProperty);
            request.Add(AutomationElement.ControlTypeProperty);
            request.Add(AutomationElement.ProcessIdProperty);
            request.Add(AutomationElement.IsOffscreenProperty);
            request.Add(AutomationElement.AutomationIdProperty);
            request.Add(AutomationElement.ClassNameProperty);
            request.AutomationElementMode = AutomationElementMode.None;
            using var cache = request.Activate();
            var roles = new[] { ControlType.Button, ControlType.ListItem, ControlType.CheckBox,
                ControlType.RadioButton, ControlType.TabItem, ControlType.Edit, ControlType.SplitButton, ControlType.MenuItem };
            var filters = roles.Select(role => (Condition)new PropertyCondition(AutomationElement.ControlTypeProperty, role))
                .Concat(new Condition[] {
                    new PropertyCondition(AutomationElement.AutomationIdProperty, "SystemTrayFrame"),
                    new PropertyCondition(AutomationElement.ClassNameProperty, "SystemTrayFrame") }).ToArray();
            var elements = root.FindAll(TreeScope.Descendants, new OrCondition(filters));
            int[]? tray = null;
            var occupied = new List<int[]>();
            for (var i = 0; i < elements.Count; i++)
            {
                var item = elements[i].Cached;
                if (item.ProcessId == Environment.ProcessId || item.IsOffscreen) continue;
                var rect = item.BoundingRectangle;
                if (rect.IsEmpty || rect.Width <= 0 || rect.Height <= 0) continue;
                var bounds = new[] { (int)rect.Left, (int)rect.Top, (int)rect.Right, (int)rect.Bottom };
                if (item.AutomationId == "SystemTrayFrame" || item.ClassName == "SystemTrayFrame") tray = bounds;
                if (item.ControlType == ControlType.Button || item.ControlType == ControlType.ListItem
                    || item.ControlType == ControlType.CheckBox || item.ControlType == ControlType.RadioButton
                    || item.ControlType == ControlType.TabItem || item.ControlType == ControlType.Edit
                    || item.ControlType == ControlType.SplitButton || item.ControlType == ControlType.MenuItem)
                    occupied.Add(bounds);
            }
            var nativeTray = FindWindowEx(taskbar, IntPtr.Zero, "TrayNotifyWnd", null);
            if (nativeTray != IntPtr.Zero && GetWindowRect(nativeTray, out var nativeRect))
                tray = new[] { nativeRect.Left, nativeRect.Top, nativeRect.Right, nativeRect.Bottom };
            return tray is null || occupied.Count == 0 ? null : new Layout(tray, occupied.ToArray());
        }
        catch (Exception) { return null; }
    }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out NativeRect rect);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string className, string? title);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string className, string? title);
}
