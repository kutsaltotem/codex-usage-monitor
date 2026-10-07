using CodexUsageMonitor.Windows;
static TaskbarSpacePlanner.Placement? Plan(int appsRight, int trayLeft, double scale = 1) =>
    TaskbarSpacePlanner.Find(0, 2560, 1399, 1433, 6, scale,
        new[] { new[] { 0, 1392, appsRight, 1440 }, new[] { trayLeft, 1392, 2560, 1440 } });
static void Check(bool passed, string scenario) { if (!passed) throw new Exception(scenario); Console.WriteLine(scenario + ": passed"); }
var full = Plan(1500, 2137)!;
var expanded = Plan(1500, 2000)!;
Check(full.Density == 0 && expanded.Density == 0 && expanded.Left < full.Left && expanded.Left + expanded.Width <= 1994, "Expanding tray moves full bar without overlap");
Check(Plan(1880, 2000)?.Density == 1, "Crowded taskbar uses three logos");
Check(Plan(1940, 2000)?.Density == 2, "Very narrow space uses AI button");
Check(Plan(1980, 2000) is null, "Full taskbar never overlays controls");
Check(Plan(1500, 2137, 1.5)?.Width == 525, "DPI scales footprint");

var obstacles = new[] { new[] { 0, 1392, 1500, 1440 }, new[] { 1865, 1392, 2560, 1440 } };
Check(TaskbarSpacePlanner.Find(0, 2560, 1399, 1433, 6, 1, obstacles, 1)?.Density == 1,
    "Compact mode stays stable near full-width boundary");
Check(TaskbarSpacePlanner.Find(0, 2560, 1399, 1433, 6, 1,
    new[] { obstacles[0], new[] { 1890, 1392, 2560, 1440 } }, 1)?.Density == 0,
    "Full mode returns when sufficient clearance exists");
