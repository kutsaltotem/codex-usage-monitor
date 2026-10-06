using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CodexUsageMonitor;
using Forms = System.Windows.Forms;

namespace CodexUsageMonitor.Windows;

/// <summary>
/// Optional compact bottom appbar. The shell reserves its height so maximized windows
/// stay above it; the visible content remains centered and only shows provider marks
/// and quota reset/remaining values.
/// </summary>
public sealed class AppBarStripWindow : Window, IDisposable
{
    private const uint AbmNew = 0x00000000;
    private const uint AbmRemove = 0x00000001;
    private const uint AbmQueryPos = 0x00000002;
    private const uint AbmSetPos = 0x00000003;
    private const uint AbeBottom = 3;
    private const int AbnPosChanged = 1;
    private const int CallbackMessage = 0x8000 + 51;
    private const int WmDisplayChange = 0x007E;

    private const double BarHeightDip = 50;
    private readonly StackPanel _providerItems = new() { Orientation = Orientation.Horizontal };
    private HwndSource? _source;
    private IntPtr _windowHandle;
    private bool _registered;
    private bool _disposed;
    private UsageSnapshot? _snapshot;

    public AppBarStripWindow()
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        AllowsTransparency = true;
        Background = System.Windows.Media.Brushes.Transparent;
        Height = BarHeightDip;
        Content = BuildContent();
        SourceInitialized += OnSourceInitialized;
        Closing += (_, _) => RemoveAppBar();
    }

    public void Enable()
    {
        if (_disposed) return;
        if (!IsVisible) Show();
        RegisterAppBar();
        if (_registered) PositionAppBar();
        else Hide();
    }

    public void Disable()
    {
        RemoveAppBar();
        if (IsVisible) Hide();
    }

    public void SetSnapshot(UsageSnapshot snapshot)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.InvokeAsync(() => SetSnapshot(snapshot));
            return;
        }
        _snapshot = snapshot;
        RenderProviders();
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _windowHandle = new WindowInteropHelper(this).Handle;
        _source = HwndSource.FromHwnd(_windowHandle);
        _source?.AddHook(WindowProc);
    }

    private void RegisterAppBar()
    {
        if (_registered || _windowHandle == IntPtr.Zero) return;
        var data = NewAppBarData();
        _registered = SHAppBarMessage(AbmNew, ref data) != IntPtr.Zero;
    }

    private void RemoveAppBar()
    {
        if (!_registered || _windowHandle == IntPtr.Zero) return;
        var data = NewAppBarData();
        SHAppBarMessage(AbmRemove, ref data);
        _registered = false;
    }

    private void PositionAppBar()
    {
        if (!_registered || _windowHandle == IntPtr.Zero) return;
        var screen = Forms.Screen.PrimaryScreen;
        if (screen is null) return;

        var bounds = screen.Bounds;
        var scale = GetScaleFactor();
        var heightPixels = Math.Max(1, (int)Math.Round(BarHeightDip * scale));
        var data = NewAppBarData();
        data.uEdge = AbeBottom;
        data.rc = new RectNative
        {
            Left = bounds.Left,
            Top = bounds.Bottom - heightPixels,
            Right = bounds.Right,
            Bottom = bounds.Bottom
        };

        SHAppBarMessage(AbmQueryPos, ref data);
        data.rc.Top = data.rc.Bottom - heightPixels;
        SHAppBarMessage(AbmSetPos, ref data);

        Left = data.rc.Left / scale;
        Top = data.rc.Top / scale;
        Width = Math.Max(1, (data.rc.Right - data.rc.Left) / scale);
        Height = Math.Max(1, (data.rc.Bottom - data.rc.Top) / scale);
    }

    private double GetScaleFactor()
    {
        try
        {
            var dpi = _source?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
            return dpi > 0 ? dpi : 1.0;
        }
        catch { return 1.0; }
    }

    private IntPtr WindowProc(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if ((message == CallbackMessage && wParam.ToInt32() == AbnPosChanged) || message == WmDisplayChange)
        {
            PositionAppBar();
        }
        return IntPtr.Zero;
    }

    private AppBarData NewAppBarData() => new()
    {
        cbSize = (uint)Marshal.SizeOf<AppBarData>(),
        hWnd = _windowHandle,
        uCallbackMessage = CallbackMessage
    };

    private UIElement BuildContent()
    {
        var outer = new Grid { Background = System.Windows.Media.Brushes.Transparent };
        var capsule = new Border
        {
            Background = Brush("#E91B222C"),
            BorderBrush = Brush("#596474"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(7, 5, 7, 5),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                Color = Colors.Black,
                BlurRadius = 9,
                ShadowDepth = 2,
                Opacity = 0.3
            }
        };
        capsule.Child = _providerItems;
        outer.Children.Add(capsule);
        return outer;
    }

    private void RenderProviders()
    {
        _providerItems.Children.Clear();
        var providers = (_snapshot?.Providers ?? Array.Empty<ProviderSnapshot>())
            .OrderBy(provider => ProviderOrder(provider.Id));
        foreach (var provider in providers)
        {
            var accent = ProviderAccent(provider.Id);
            var group = new Border
            {
                Background = Brush("#282F39"),
                BorderBrush = Brush("#3A4553"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(9),
                Padding = new Thickness(5, 3, 5, 3),
                Margin = new Thickness(2, 0, 2, 0),
                ToolTip = provider.State == "stale"
                    ? $"{provider.Name} · önbellek · son başarılı veri {provider.ObservedAt.ToLocalTime():HH:mm}"
                    : $"{provider.Name} · {StateText(provider.State)}",
                Opacity = provider.State == "stale" ? 0.68 : 1
            };
            var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            var logoTile = new Border
            {
                Background = System.Windows.Media.Brushes.White,
                CornerRadius = new CornerRadius(4),
                Width = 21,
                Height = 21,
                Padding = new Thickness(2),
                Child = CreateLogo(provider.Id)
            };
            row.Children.Add(logoTile);

            var taskbarWindows = SelectTaskbarWindows(provider);
            for (var index = 0; index < taskbarWindows.Count; index++)
            {
                var window = taskbarWindows[index];
                var remainingValue = window.RemainingPercent ?? (window.UsedPercent is { } used ? 100 - used : null);
                var remaining = Math.Clamp(remainingValue ?? 0, 0, 100);
                var reset = FormatCompactReset(window.ResetsAt);
                var timeCapsule = MakeCapsule(reset, "#303B49", "#D5DEE9");
                timeCapsule.ToolTip = $"{window.Label}: sıfırlanmaya kalan süre";
                row.Children.Add(timeCapsule);

                var percentTone = remainingValue is null ? (Background: "#343D48", Foreground: "#ABB6C4")
                    : remaining <= 10 ? (Background: "#493236", Foreground: "#FFB5B4")
                    : remaining <= 30 ? (Background: "#493D2A", Foreground: "#F1CF83")
                    : (Background: ProviderAccentBackground(provider.Id), Foreground: accent);
                var percentText = remainingValue is { } exactRemaining
                    ? $"%{Math.Round(Math.Clamp(exactRemaining, 0, 100), MidpointRounding.AwayFromZero):0}"
                    : "—";
                var percentCapsule = MakeCapsule(percentText,
                    percentTone.Background, percentTone.Foreground);
                percentCapsule.ToolTip = $"{window.Label}: kalan kullanım";
                row.Children.Add(percentCapsule);
                if (index < taskbarWindows.Count - 1)
                    row.Children.Add(new Border
                    {
                        Width = 1,
                        Height = 15,
                        Background = Brush("#596474"),
                        Margin = new Thickness(5, 0, 2, 0),
                        VerticalAlignment = VerticalAlignment.Center
                    });
            }

            group.Child = row;
            _providerItems.Children.Add(group);
        }
    }

    private static Border MakeCapsule(string text, string background, string foreground) => new()
    {
        Background = Brush(background),
        CornerRadius = new CornerRadius(6),
        Padding = new Thickness(5, 3, 5, 3),
        Margin = new Thickness(3, 0, 0, 0),
        VerticalAlignment = VerticalAlignment.Center,
        Child = new TextBlock
        {
            Text = text,
            Foreground = Brush(foreground),
            FontSize = 8,
            FontWeight = FontWeights.SemiBold,
            FontFamily = new System.Windows.Media.FontFamily("Segoe UI")
        }
    };

    private static System.Windows.Controls.Image CreateLogo(string providerId)
    {
        var file = providerId switch
        {
            "gemini" => "gemini-mark.jpg",
            "claude" => "claude-mark.jpg",
            _ => "openai-mark-codex.jpg"
        };
        var path = Path.Combine(AppContext.BaseDirectory, "Assets", file);
        BitmapImage? source = null;
        if (File.Exists(path))
        {
            source = new BitmapImage();
            source.BeginInit();
            source.UriSource = new System.Uri(path, System.UriKind.Absolute);
            source.CacheOption = BitmapCacheOption.OnLoad;
            source.EndInit();
            source.Freeze();
        }
        return new System.Windows.Controls.Image { Source = source, Stretch = Stretch.Uniform };
    }

    private static string FormatCompactReset(DateTimeOffset? resetAt)
    {
        if (resetAt is null) return "—";
        var remaining = resetAt.Value - DateTimeOffset.Now;
        if (remaining <= TimeSpan.Zero) return "0dk";
        if (remaining.TotalDays >= 1) return $"{(int)remaining.TotalDays}g{remaining.Hours}sa";
        if (remaining.TotalHours >= 1) return $"{(int)remaining.TotalHours}sa{remaining.Minutes}dk";
        return $"{Math.Max(1, (int)Math.Ceiling(remaining.TotalMinutes))}dk";
    }

    private static IReadOnlyList<UsageWindow> SelectTaskbarWindows(ProviderSnapshot provider) => provider.Windows
        .OrderBy(window => window.Kind switch
        {
            "five_hour" => 0,
            "weekly" => 1,
            "daily" => 2,
            "unknown" => 3,
            _ => 4
        })
        .ThenBy(window => window.RemainingPercent ?? (window.UsedPercent is { } used ? 100 - used : 101))
        .Take(2)
        .ToArray();

    private static int ProviderOrder(string providerId) => providerId switch
    {
        "chatgpt-codex" => 0,
        "gemini" => 1,
        "claude" => 2,
        _ => 3
    };

    private static string ProviderAccent(string providerId) => providerId switch
    {
        "gemini" => "#829DFF",
        "claude" => "#E2A078",
        _ => "#37C9A3"
    };

    private static string ProviderAccentBackground(string providerId) => providerId switch
    {
        "gemini" => "#2E3852",
        "claude" => "#4B342F",
        _ => "#29463F"
    };

    private static string StateText(string state) => state switch
    {
        "ok" => "güncel",
        "partial" => "kısmi kota verisi",
        "reauth_required" => "yeniden giriş gerekli",
        "forbidden" => "erişim reddedildi",
        _ => "kota alınamadı"
    };

    private static SolidColorBrush Brush(string color) => new((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(color));

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        RemoveAppBar();
        _source?.RemoveHook(WindowProc);
        if (IsVisible) Hide();
        Close();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AppBarData
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uCallbackMessage;
        public uint uEdge;
        public RectNative rc;
        public IntPtr lParam;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RectNative
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("shell32.dll", EntryPoint = "SHAppBarMessage")]
    private static extern IntPtr SHAppBarMessage(uint dwMessage, ref AppBarData appBarData);
}
