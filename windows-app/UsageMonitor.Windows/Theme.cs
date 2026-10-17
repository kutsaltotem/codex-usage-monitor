using System.IO;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace CodexUsageMonitor.Windows;

public enum AppThemeMode { System, Dark, Light }

/// <summary>
/// Dark is the designed palette. Light is derived from it by flipping lightness (hue is kept), so every
/// hard-coded dark colour in the popup has a matching light one without a second set of literals.
/// The taskbar capsule strip is intentionally not themed: it sits on the Windows taskbar.
/// </summary>
public static class Theme
{
    private static readonly string SettingPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CodexUsageMonitor", "settings", "theme.txt");

    // Every colour literal that App.xaml / MainWindow.xaml reference as {DynamicResource C_RRGGBB}.
    private static readonly string[] XamlHexes =
    [
        "141E2A", "141F2D", "1C2735", "252D38", "293B50", "2B3A4D", "2C3C50", "2E8A73", "303A47", "34465E",
        "344A65", "354E68", "3A4655", "404B59", "425571", "465363", "526071", "6987AC", "72839A", "7ADDB8",
        "8D99A8", "91B8ED", "94A8C2", "A6B5C8", "A8C8F1", "C0CDDC", "CBD4DF", "E2FFF4", "EAF0F7", "EDF1F6"
    ];

    private static readonly (string Key, string Hex)[] NamedBrushes =
    [
        ("CanvasBrush", "111923"), ("SurfaceBrush", "1C2735"), ("InsetBrush", "141E2A"),
        ("StrokeBrush", "2B3A4D"), ("PrimaryTextBrush", "F1F4F8"), ("SecondaryTextBrush", "A6B5C8")
    ];

    private static readonly Dictionary<(bool Light, string Hex, bool Accent), SolidColorBrush> Cache = new();
    private static AppThemeMode _mode = AppThemeMode.Dark; // dark by default; light is opt-in

    public static AppThemeMode Mode => _mode;
    public static bool IsLight { get; private set; }
    public static event Action? Changed;

    public static void Load()
    {
        try
        {
            if (Enum.TryParse<AppThemeMode>(File.ReadAllText(SettingPath).Trim(), true, out var saved)) _mode = saved;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        Resolve();
        SystemEvents.UserPreferenceChanged += (_, args) =>
        {
            if (args.Category is not (UserPreferenceCategory.General or UserPreferenceCategory.Color) || _mode != AppThemeMode.System) return;
            Application.Current?.Dispatcher.BeginInvoke(new Action(() => Update(raise: true)));
        };
    }

    public static void SetMode(AppThemeMode mode)
    {
        _mode = mode;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingPath)!);
            File.WriteAllText(SettingPath, mode.ToString());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        Update(raise: true);
    }

    /// <summary>Test hook: force a theme without touching the saved setting.</summary>
    public static void ForceForPreview(bool light)
    {
        IsLight = light;
        Apply();
    }

    private static void Update(bool raise)
    {
        var before = IsLight;
        Resolve();
        Apply();
        if (raise || before != IsLight) Changed?.Invoke();
    }

    private static void Resolve() => IsLight = _mode switch
    {
        AppThemeMode.Light => true,
        AppThemeMode.Dark => false,
        _ => SystemUsesLightApps()
    };

    private static bool SystemUsesLightApps()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int value && value != 0;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>Publishes the palette as application resources; DynamicResource consumers repaint on their own.</summary>
    public static void Apply()
    {
        var app = Application.Current;
        if (app is null) return;
        foreach (var hex in XamlHexes) app.Resources["C_" + hex] = Brush("#" + hex);
        foreach (var (key, hex) in NamedBrushes) app.Resources[key] = Brush("#" + hex);
    }

    public static SolidColorBrush Brush(string color) => Get(color, accent: false);

    /// <summary>Saturated fills (progress bars, brand colours) keep their identity but get deeper on light.</summary>
    public static SolidColorBrush Accent(string color) => Get(color, accent: true);

    public static System.Windows.Media.Color AccentColor(string color) => Accent(color).Color;

    private static SolidColorBrush Get(string color, bool accent)
    {
        var key = (IsLight, color.ToUpperInvariant(), accent);
        if (Cache.TryGetValue(key, out var cached)) return cached;
        var source = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(color);
        var brush = new SolidColorBrush(IsLight ? ToLight(source, accent) : source);
        brush.Freeze();
        Cache[key] = brush;
        return brush;
    }

    private static System.Windows.Media.Color ToLight(System.Windows.Media.Color color, bool accent)
    {
        RgbToHsl(color, out var h, out var s, out var l);
        double newL, newS = s;
        if (accent)
        {
            newL = Math.Min(l, 0.47);
        }
        else
        {
            newL = Math.Clamp(0.5 + (0.5 - l) * 1.08, 0.08, 0.975);
            if (l < 0.45) newS = s * 0.6; // dark surfaces become soft tints, not saturated pastels
        }
        return HslToRgb(h, newS, newL, color.A);
    }

    private static void RgbToHsl(System.Windows.Media.Color color, out double h, out double s, out double l)
    {
        double r = color.R / 255d, g = color.G / 255d, b = color.B / 255d;
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        l = (max + min) / 2;
        var d = max - min;
        if (d < 1e-9) { h = 0; s = 0; return; }
        s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
        h = max == r ? (g - b) / d + (g < b ? 6 : 0) : max == g ? (b - r) / d + 2 : (r - g) / d + 4;
        h /= 6;
    }

    private static System.Windows.Media.Color HslToRgb(double h, double s, double l, byte alpha)
    {
        if (s < 1e-9) { var v = (byte)Math.Round(l * 255); return System.Windows.Media.Color.FromArgb(alpha, v, v, v); }
        var q = l < 0.5 ? l * (1 + s) : l + s - l * s;
        var p = 2 * l - q;
        byte Channel(double t)
        {
            if (t < 0) t += 1;
            if (t > 1) t -= 1;
            var value = t < 1d / 6 ? p + (q - p) * 6 * t : t < 0.5 ? q : t < 2d / 3 ? p + (q - p) * (2d / 3 - t) * 6 : p;
            return (byte)Math.Round(Math.Clamp(value, 0, 1) * 255);
        }
        return System.Windows.Media.Color.FromArgb(alpha, Channel(h + 1d / 3), Channel(h), Channel(h - 1d / 3));
    }
}
