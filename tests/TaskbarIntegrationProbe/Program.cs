using System.Runtime.InteropServices;
using System.Text.Json;

// Read-only integration check against the installed application's real HWNDs.
var taskbar = Native.FindWindow("Shell_TrayWnd", null);
var indicator = Native.FindWindowEx(taskbar, IntPtr.Zero, null, "Kullanım Monitörü — Görev Çubuğu");
Require(taskbar != IntPtr.Zero && indicator != IntPtr.Zero, "Taskbar child indicator not found.");
Require(Native.GetParent(indicator) == taskbar, "Indicator is not a taskbar child.");
var style = Native.GetWindowLongPtr(indicator, -16).ToInt64();
Require((style & 0x40000000L) != 0 && (style & 0x80000000L) == 0, "Indicator uses popup instead of child style.");
Require(Native.IsWindowVisible(indicator), "Indicator is not visible.");
Native.GetWindowRect(indicator, out var bar);
Native.GetWindowRect(taskbar, out var taskbarRect);
Require(bar.Top >= taskbarRect.Top && bar.Bottom <= taskbarRect.Bottom, "Indicator is outside the taskbar.");
Native.GetWindowThreadProcessId(indicator, out var indicatorProcess);
var scale = Native.GetDpiForWindow(taskbar) / 96d;
Require(new[] { 350, 104, 34 }.Any(width => bar.Right - bar.Left == (int)Math.Ceiling(width * scale)), "Indicator has an unknown density width.");
var tray = Native.FindWindowEx(taskbar, IntPtr.Zero, "TrayNotifyWnd", null);
if (tray != IntPtr.Zero && Native.GetWindowRect(tray, out var trayBounds))
    Require(bar.Right <= trayBounds.Left || bar.Left >= trayBounds.Right, "Indicator overlaps the actual system tray.");
object? popupResult = null;
if (args.Contains("--popup-closed"))
{
    var hiddenPopup = Native.FindWindow(null, "Kullanım Özeti");
    Require(hiddenPopup == IntPtr.Zero || !Native.IsWindowVisible(hiddenPopup), "Popup is still visible.");
}
if (args.Contains("--popup-open"))
{
    var popup = Native.FindWindow(null, "Kullanım Özeti");
    Require(popup != IntPtr.Zero && Native.IsWindowVisible(popup), "Popup is not visible.");
    Native.GetWindowThreadProcessId(popup, out var popupProcess);
    Require(popupProcess == indicatorProcess, "Popup belongs to a different app.");
    var popupStyle = Native.GetWindowLongPtr(popup, -20).ToInt64();
    Require((popupStyle & 0x40000L) == 0, "Popup requests a taskbar application button.");
    Native.GetWindowRect(popup, out var popupRect);
    Require(popupRect.Left == bar.Left && popupRect.Right == bar.Right, "Popup and indicator do not align horizontally.");
    Require(popupRect.Bottom == bar.Top, "Popup and indicator have a gap or overlap.");
    popupResult = new { left = popupRect.Left, top = popupRect.Top, right = popupRect.Right, bottom = popupRect.Bottom, gap = bar.Top - popupRect.Bottom };
}
Console.WriteLine(JsonSerializer.Serialize(new { passed = true, embedded = true, visible = true,
    processId = indicatorProcess, indicator = new { left = bar.Left, top = bar.Top, right = bar.Right, bottom = bar.Bottom }, popup = popupResult }));

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

internal static class Native
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] internal static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern IntPtr FindWindow(string? className, string? windowName);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string? className, string? windowName);
    [DllImport("user32.dll")]
    internal static extern IntPtr GetParent(IntPtr hwnd);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    internal static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);
    [DllImport("user32.dll")]
    internal static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
}
