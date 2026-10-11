using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;

// Read-only integration check against the installed application's real HWNDs.
var taskbar = Native.FindWindow("Shell_TrayWnd", null);
var indicator = Native.FindWindowEx(taskbar, IntPtr.Zero, null, "Kullanım Monitörü — Görev Çubuğu");
Require(taskbar != IntPtr.Zero && indicator != IntPtr.Zero, "Taskbar child indicator not found.");
Require(Native.GetParent(indicator) == taskbar, "Indicator is not a taskbar child.");
var style = Native.GetWindowLongPtr(indicator, -16).ToInt64();
Require((style & 0x40000000L) != 0 && (style & 0x80000000L) == 0, "Indicator uses popup instead of child style.");
Require(Native.IsWindowVisible(indicator), "Indicator is not visible.");
Require((Native.GetWindowLongPtr(indicator, -20).ToInt64() & 0x80000L) != 0,
    "Indicator lost its working layered WPF presentation path.");
Native.GetWindowRect(indicator, out var bar);
Native.GetWindowRect(taskbar, out var taskbarRect);
Require(bar.Top >= taskbarRect.Top && bar.Bottom <= taskbarRect.Bottom, "Indicator is outside the taskbar.");
Native.GetWindowThreadProcessId(indicator, out var indicatorProcess);
var scale = Native.GetDpiForWindow(taskbar) / 96d;
Require(new[] { 350, 104, 34 }.Any(width => bar.Right - bar.Left == (int)Math.Ceiling(width * scale)), "Indicator has an unknown density width.");
var tray = Native.FindWindowEx(taskbar, IntPtr.Zero, "TrayNotifyWnd", null);
if (tray != IntPtr.Zero && Native.GetWindowRect(tray, out var trayBounds))
    Require(bar.Right <= trayBounds.Left || bar.Left >= trayBounds.Right, "Indicator overlaps the actual system tray.");
var desktopDiagnostics = ReadDesktopDiagnostics(indicator, taskbar);
var processManifest = ReadProcessManifest(indicatorProcess);
// Geometry/hit tests do not establish compositor pixels; visual acceptance remains separate.
// WS_VISIBLE alone also succeeds for a completely obscured child. Sample the
// solid interior of every provider capsule (or the single AI capsule), avoiding
// transparent corners and inter-provider gaps.
var sampleCount = bar.Right - bar.Left >= (int)Math.Ceiling(104 * scale) ? 3 : 1;
var visibilitySamples = Enumerable.Range(0, sampleCount).Select(index =>
{
    var screenPoint = new Native.Point
    {
        X = bar.Left + (int)Math.Round((bar.Right - bar.Left) * (index + .5) / sampleCount),
        Y = bar.Top + (bar.Bottom - bar.Top) / 2
    };
    var clientPoint = screenPoint;
    Require(Native.ScreenToClient(taskbar, ref clientPoint), "Cannot translate the indicator sample to taskbar client coordinates.");
    var top = Native.WindowFromPoint(screenPoint);
    var taskbarChild = Native.ChildWindowFromPointEx(taskbar, clientPoint, 1 | 2 | 4);
    bool BelongsToIndicator(IntPtr candidate) => candidate == indicator || Native.IsChild(indicator, candidate);
    return new { x = screenPoint.X, y = screenPoint.Y,
        unobscured = BelongsToIndicator(top) && BelongsToIndicator(taskbarChild),
        topWindow = DescribeWindow(top), taskbarChild = DescribeWindow(taskbarChild) };
}).ToArray();
if (visibilitySamples.Any(sample => !sample.unobscured))
{
    Console.WriteLine(JsonSerializer.Serialize(new { passed = false, reason = "indicator_occluded_or_not_hit_testable",
        indicator = DescribeWindow(indicator), taskbar = DescribeWindow(taskbar), desktop = desktopDiagnostics, samples = visibilitySamples }));
    throw new InvalidOperationException("Indicator has WS_VISIBLE but its capsule interiors are obscured or not hit-testable; see window diagnostics above.");
}
if (!processManifest.Windows10Supported)
{
    Console.WriteLine(JsonSerializer.Serialize(new { passed = false, reason = "windows_compatibility_manifest_missing",
        processId = indicatorProcess, manifest = processManifest }));
    throw new InvalidOperationException("The running indicator executable lacks the Windows 10 supportedOS compatibility manifest.");
}
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
    // MainWindow.AlignTo preserves a readable 350-DIP panel above compact
    // 104/34-DIP indicators, right-aligning it unless the work area clamps it.
    var popupScale = Native.GetDpiForWindow(popup) / 96d;
    Require(popupScale > 0, "Popup DPI is unavailable.");
    var monitor = Native.MonitorFromRect(ref bar, 2 /* MONITOR_DEFAULTTONEAREST */);
    var monitorInfo = new Native.MonitorInfo { Size = Marshal.SizeOf<Native.MonitorInfo>() };
    Require(monitor != IntPtr.Zero && Native.GetMonitorInfo(monitor, ref monitorInfo), "Popup monitor work area is unavailable.");
    var expectedWidth = Math.Max(350 * popupScale, bar.Right - bar.Left);
    var expectedLeft = Math.Clamp(bar.Right - expectedWidth, monitorInfo.Work.Left,
        Math.Max(monitorInfo.Work.Left, monitorInfo.Work.Right - expectedWidth));
    // Fractional DPI can round WPF DIP positions/sizes to the adjacent pixel.
    Require(Math.Abs((popupRect.Right - popupRect.Left) - expectedWidth) <= 1,
        "Popup does not preserve the expected readable width.");
    Require(Math.Abs(popupRect.Left - expectedLeft) <= 1 && Math.Abs(popupRect.Right - (expectedLeft + expectedWidth)) <= 1,
        "Popup is not right-aligned with its indicator within the monitor work area.");
    Require(popupRect.Bottom == bar.Top, "Popup and indicator have a gap or overlap.");
    popupResult = new { left = popupRect.Left, top = popupRect.Top, right = popupRect.Right, bottom = popupRect.Bottom, gap = bar.Top - popupRect.Bottom };
}
Console.WriteLine(JsonSerializer.Serialize(new { passed = true, embedded = true, visible = true,
    processId = indicatorProcess, indicator = new { left = bar.Left, top = bar.Top, right = bar.Right, bottom = bar.Bottom },
    unobscured = true, desktop = desktopDiagnostics, manifest = processManifest, samples = visibilitySamples, popup = popupResult }));

static object DescribeWindow(IntPtr window, bool includeTitle = false)
{
    var title = new StringBuilder(256);
    var className = new StringBuilder(256);
    if (includeTitle) Native.GetWindowText(window, title, title.Capacity);
    Native.GetClassName(window, className, className.Capacity);
    Native.GetWindowThreadProcessId(window, out var process);
    var extendedStyle = Native.GetWindowLongPtr(window, -20).ToInt64();
    var cloakResult = Native.DwmGetWindowAttribute(window, 14 /* DWMWA_CLOAKED */, out var cloakFlags, sizeof(int));
    var layeredAttributesAvailable = Native.GetLayeredWindowAttributes(window, out var colorKey, out var alpha, out var layeredFlags);
    var layeredError = layeredAttributesAvailable ? 0 : Marshal.GetLastWin32Error();
    var hasRect = Native.GetWindowRect(window, out var rect);
    return new { handle = $"0x{window.ToInt64():X}", processId = process, @class = className.ToString(), title = includeTitle ? title.ToString() : null,
        visible = Native.IsWindowVisible(window), extendedStyle = $"0x{extendedStyle:X}",
        layered = (extendedStyle & 0x80000) != 0, transparent = (extendedStyle & 0x20) != 0,
        cloaked = cloakResult == 0 ? (bool?)(cloakFlags != 0) : null,
        cloakFlags = cloakResult == 0 ? (int?)cloakFlags : null, cloakHResult = $"0x{cloakResult:X8}",
        // WPF per-pixel layered windows can legitimately lack global-alpha attributes.
        layeredAttributes = new { available = layeredAttributesAvailable,
            alpha = layeredAttributesAvailable ? (byte?)alpha : null,
            colorKey = layeredAttributesAvailable ? (uint?)colorKey : null,
            flags = layeredAttributesAvailable ? (uint?)layeredFlags : null, error = layeredError },
        rect = hasRect ? new { left = rect.Left, top = rect.Top, right = rect.Right, bottom = rect.Bottom } : null };
}

static ManifestInfo ReadProcessManifest(uint processId)
{
    string? executable = null;
    var resourceFound = false;
    try
    {
        using var process = System.Diagnostics.Process.GetProcessById((int)processId);
        executable = process.MainModule?.FileName;
        if (executable is null) return new ManifestInfo(null, false, false, "Process executable path unavailable.");
        // Resource-only mapping: do not execute the installed application's code.
        var module = Native.LoadLibraryEx(executable, IntPtr.Zero, 2 | 0x20);
        if (module == IntPtr.Zero) return new ManifestInfo(executable, false, false, $"LoadLibraryEx error {Marshal.GetLastWin32Error()}.");
        try
        {
            var resource = Native.FindResource(module, new IntPtr(1), new IntPtr(24) /* RT_MANIFEST */);
            if (resource == IntPtr.Zero) return new ManifestInfo(executable, false, false, $"RT_MANIFEST error {Marshal.GetLastWin32Error()}.");
            resourceFound = true;
            var size = Native.SizeofResource(module, resource);
            var data = Native.LockResource(Native.LoadResource(module, resource));
            if (data == IntPtr.Zero || size == 0 || size > 1_048_576)
                return new ManifestInfo(executable, true, false, "Manifest resource unavailable or invalid size.");
            var bytes = new byte[(int)size];
            Marshal.Copy(data, bytes, 0, bytes.Length);
            var xml = Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF').TrimEnd('\0');
            var windows10 = new Guid("8e0f7a12-bfb3-4fe8-b9a5-48fd50a15a9a");
            var document = XDocument.Parse(xml);
            var supported = document.Descendants(XName.Get("supportedOS", "urn:schemas-microsoft-com:compatibility.v1"))
                .Any(element => Guid.TryParse(element.Attribute("Id")?.Value, out var id) && id == windows10);
            return new ManifestInfo(executable, true, supported, null);
        }
        finally { Native.FreeLibrary(module); }
    }
    catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException or System.Xml.XmlException or IOException)
    {
        return new ManifestInfo(executable, resourceFound, false, error.Message);
    }
}

static object ReadDesktopDiagnostics(IntPtr indicator, IntPtr taskbar)
{
    var parents = new List<object>();
    var parent = Native.GetParent(indicator);
    var visited = new HashSet<IntPtr>();
    while (parent != IntPtr.Zero && visited.Add(parent))
    {
        parents.Add(DescribeWindow(parent));
        parent = Native.GetParent(parent);
    }
    object ReadMonitor(IntPtr monitor)
    {
        var info = new Native.MonitorInfo { Size = Marshal.SizeOf<Native.MonitorInfo>() };
        var available = Native.GetMonitorInfo(monitor, ref info);
        return new { handle = $"0x{monitor.ToInt64():X}", available,
            primary = available && (info.Flags & 1) != 0,
            bounds = new { left = info.Monitor.Left, top = info.Monitor.Top, right = info.Monitor.Right, bottom = info.Monitor.Bottom },
            work = new { left = info.Work.Left, top = info.Work.Top, right = info.Work.Right, bottom = info.Work.Bottom } };
    }
    var monitors = new List<object>();
    Native.MonitorEnumProc callback = (IntPtr monitor, IntPtr dc, ref Native.Rect bounds, IntPtr data) =>
    {
        monitors.Add(ReadMonitor(monitor));
        return true;
    };
    var enumerationSucceeded = Native.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero);
    var foreground = Native.GetForegroundWindow();
    return new { indicator = DescribeWindow(indicator, includeTitle: true), visibleParents = parents,
        indicatorMonitor = ReadMonitor(Native.MonitorFromWindow(indicator, 2)),
        taskbarMonitor = ReadMonitor(Native.MonitorFromWindow(taskbar, 2)),
        foreground = DescribeWindow(foreground), foregroundMonitor = ReadMonitor(Native.MonitorFromWindow(foreground, 2)),
        monitorEnumerationSucceeded = enumerationSucceeded, monitors };
}

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

internal sealed record ManifestInfo(string? ExecutablePath, bool ResourceFound, bool Windows10Supported, string? Error);

internal static class Native
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    internal struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)]
    internal struct MonitorInfo { public int Size; public Rect Monitor, Work; public uint Flags; }
    [DllImport("user32.dll")] internal static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll")] internal static extern IntPtr MonitorFromRect(ref Rect rect, uint flags);
    [DllImport("user32.dll")] internal static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] internal static extern IntPtr GetForegroundWindow();
    internal delegate bool MonitorEnumProc(IntPtr monitor, IntPtr dc, ref Rect bounds, IntPtr data);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool EnumDisplayMonitors(IntPtr dc, IntPtr clip, MonitorEnumProc callback, IntPtr data);
    [DllImport("dwmapi.dll")]
    internal static extern int DwmGetWindowAttribute(IntPtr hwnd, uint attribute, out int value, int size);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetLayeredWindowAttributes(IntPtr hwnd, out uint colorKey, out byte alpha, out uint flags);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "LoadLibraryExW")]
    internal static extern IntPtr LoadLibraryEx(string path, IntPtr file, uint flags);
    [DllImport("kernel32.dll", SetLastError = true, EntryPoint = "FindResourceW")]
    internal static extern IntPtr FindResource(IntPtr module, IntPtr name, IntPtr type);
    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern uint SizeofResource(IntPtr module, IntPtr resource);
    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern IntPtr LoadResource(IntPtr module, IntPtr resource);
    [DllImport("kernel32.dll")]
    internal static extern IntPtr LockResource(IntPtr resource);
    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool FreeLibrary(IntPtr module);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern IntPtr FindWindow(string? className, string? windowName);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string? className, string? windowName);
    [DllImport("user32.dll")]
    internal static extern IntPtr GetParent(IntPtr hwnd);
    [DllImport("user32.dll")] internal static extern IntPtr WindowFromPoint(Point point);
    [DllImport("user32.dll")] internal static extern IntPtr ChildWindowFromPointEx(IntPtr parent, Point point, uint flags);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ScreenToClient(IntPtr hwnd, ref Point point);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsChild(IntPtr parent, IntPtr child);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern int GetClassName(IntPtr hwnd, StringBuilder name, int count);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int count);
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
