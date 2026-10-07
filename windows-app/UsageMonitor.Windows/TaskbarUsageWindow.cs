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
/// Child window hosted by Explorer's taskbar immediately left of the notification area.
/// </summary>
public sealed class TaskbarUsageWindow : Window, IDisposable
{
    private static TaskbarUsageWindow? _instance;

    public static bool IsIndicatorUnderPointer()
    {
        return _instance is { IsVisible: true } indicator && GetCursorPos(out var point) && indicator.ScreenBounds.Contains(point.X, point.Y);
    }

    private readonly StackPanel _providerItems = new() { Orientation = Orientation.Horizontal };
    private IntPtr _windowHandle;
    private IntPtr _taskbarParent;
    private Border? _capsule;
    private bool _attached;
    private static readonly Dictionary<string, BitmapImage> LogoCache = new();
    private bool _enabled;
    private bool _disposed;
    private UsageSnapshot? _snapshot;
    private readonly System.Windows.Threading.DispatcherTimer _clock = new() { Interval = TimeSpan.FromMinutes(1) };
    private readonly System.Windows.Threading.DispatcherTimer _anchor = new() { Interval = TimeSpan.FromSeconds(2) };
    private string? _lastPlacement;
    private IntPtr _locationHook;
    private WinEventCallback? _locationCallback;
    private RectNative _lastNotifiedBar;

    public event Action? OpenRequested;
    public event Action<System.Drawing.Rectangle>? PlacementChanged;
    public System.Drawing.Rectangle ScreenBounds { get; private set; }

    public void SetAttached(bool attached)
    {
        _attached = attached;
        foreach (var item in _providerItems.Children.OfType<Border>())
            item.CornerRadius = attached ? new CornerRadius(0, 0, 9, 9) : new CornerRadius(9);
        if (_capsule is not null)
            _capsule.CornerRadius = attached ? new CornerRadius(0, 0, 7, 7) : new CornerRadius(7);
        PositionOnTaskbar();
    }

    public TaskbarUsageWindow()
    {
        _instance = this;
        Title = "Kullanım Monitörü — Görev Çubuğu";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = false;
        AllowsTransparency = true;
        Background = System.Windows.Media.Brushes.Transparent;
        Height = 34;
        Width = 340;
        Content = BuildContent();
        RenderProviders();
        _clock.Tick += (_, _) => RenderProviders();
        _anchor.Tick += (_, _) => PositionOnTaskbar();
        SourceInitialized += (_, _) =>
        {
            _windowHandle = new WindowInteropHelper(this).Handle;
            _locationCallback = (_, _, hwnd, _, _, _, _) =>
            {
                if (!_enabled || !GetWindowRect(hwnd, out var rect)) return;
                var tray = FindWindowEx(_taskbarParent, IntPtr.Zero, "TrayNotifyWnd", null);
                if (hwnd == tray && tray != IntPtr.Zero)
                {
                    Dispatcher.BeginInvoke(new Action(() => { _trayCheckedAt = DateTime.MinValue; PositionOnTaskbar(); }));
                    return;
                }
                if (hwnd != _taskbarParent) return;
                if (rect.Left == _lastNotifiedBar.Left && rect.Top == _lastNotifiedBar.Top
                    && rect.Right == _lastNotifiedBar.Right && rect.Bottom == _lastNotifiedBar.Bottom) return;
                _lastNotifiedBar = rect;
                Dispatcher.BeginInvoke(new Action(PositionOnTaskbar));
            };
            var shell = FindWindow("Shell_TrayWnd", null);
            GetWindowThreadProcessId(shell, out var shellProcess);
            if (shellProcess != 0) _locationHook = SetWinEventHook(0x800B, 0x800B, IntPtr.Zero, _locationCallback, shellProcess, 0, 0);
        };
    }

    public void Enable()
    {
        if (_disposed) return;
        _enabled = true;
        PositionOnTaskbar();
        _clock.Start();
        _anchor.Start();
    }

    public void Disable()
    {
        _enabled = false;
        _clock.Stop();
        _anchor.Stop();
        HideIndicator();
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
        PositionOnTaskbar();
    }

    private void HideIndicator()
    {
        if (IsVisible) Hide();
        if (ScreenBounds.IsEmpty) return;
        ScreenBounds = System.Drawing.Rectangle.Empty;
        PlacementChanged?.Invoke(ScreenBounds);
    }

    private void PositionOnTaskbar()
    {
        if (!_enabled || _disposed) return;
        var taskbar = FindWindow("Shell_TrayWnd", null);
        if (taskbar == IntPtr.Zero || !IsWindowVisible(taskbar) || !GetWindowRect(taskbar, out var bar)
            || bar.Right <= bar.Left || bar.Bottom <= bar.Top)
        {
            SavePlacement("taskbar_unavailable");
            HideIndicator();
            return;
        }
        // A hidden auto-hide taskbar can remain a visible HWND outside the monitor.
        var screen = Forms.Screen.FromHandle(taskbar);
        if (bar.Top >= screen.Bounds.Bottom - 2 || bar.Bottom <= screen.Bounds.Top + 2
            || bar.Left >= screen.Bounds.Right - 2 || bar.Right <= screen.Bounds.Left + 2)
        {
            SavePlacement("taskbar_hidden_or_fullscreen");
            HideIndicator();
            return;
        }
        if (_cachedTrayTaskbar != taskbar) { _cachedModernTray = default; _occupied = Array.Empty<int[]>(); _layoutObservedAt = default; }
        TryGetModernTray(taskbar, out _);
        var tray = FindWindowEx(taskbar, IntPtr.Zero, "TrayNotifyWnd", null);
        RectNative trayBounds;
        if (!(tray != IntPtr.Zero && GetWindowRect(tray, out trayBounds))
            && !TryGetModernTray(taskbar, out trayBounds))
        {
            SavePlacement("tray_unavailable");
            // Do not guess a position that could cover the clock or notification icons.
            HideIndicator();
            return;
        }
        var dpi = GetDpiForWindow(taskbar);
        var scale = dpi > 0 ? dpi / 96d : 1d;
        var content = (UIElement)Content;
        content.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
        var wantedWidth = (int)Math.Ceiling((3 * (110 + 4) + 8) * scale);
        var wantedHeight = Math.Max(24, (int)Math.Ceiling(34 * scale));
        var gap = Math.Max(2, (int)Math.Round(6 * scale));
        int x, y, width, height;
        if (bar.Right - bar.Left >= bar.Bottom - bar.Top)
        {
            width = Math.Min(wantedWidth, Math.Max(1, trayBounds.Left - bar.Left - gap));
            height = Math.Min(wantedHeight, bar.Bottom - bar.Top);
            y = bar.Top + (bar.Bottom - bar.Top - height) / 2;
            if (DateTime.UtcNow - _layoutObservedAt > TimeSpan.FromSeconds(10))
            {
                HideIndicator();
                SavePlacement("layout_pending_or_stale");
                return;
            }
            var plan = TaskbarSpacePlanner.Find(bar.Left, bar.Right, y, y + height, gap, scale,
                _occupied.Append(new[] { trayBounds.Left, bar.Top, trayBounds.Right, bar.Bottom }), _density);
            if (plan is null) { HideIndicator(); SavePlacement("no_safe_taskbar_space"); return; }
            if (_density != plan.Density) { _density = plan.Density; RenderProviders(); }
            width = plan.Width;
            x = plan.Left;
        }
        else
        {
            width = Math.Min(wantedWidth, bar.Right - bar.Left);
            height = Math.Min(wantedHeight, Math.Max(1, trayBounds.Top - bar.Top - gap));
            x = bar.Left + (bar.Right - bar.Left - width) / 2;
            y = trayBounds.Top - gap - height;
        }
        if (!IsVisible) Show();
        if (GetParent(_windowHandle) != taskbar)
        {
            // SetParent does not change popup/child styles; explicitly convert before attaching.
            var style = GetWindowLongPtr(_windowHandle, -16).ToInt64();
            SetWindowLongPtr(_windowHandle, -16, new IntPtr((style & ~0x80000000L) | 0x40000000L));
            SetParent(_windowHandle, taskbar);
            if (GetParent(_windowHandle) != taskbar)
            {
                SetWindowLongPtr(_windowHandle, -16, new IntPtr(style));
                SavePlacement($"embedding_failed: {Marshal.GetLastWin32Error()}");
                Hide();
                return;
            }
            _taskbarParent = taskbar;
        }
        var target = new System.Drawing.Rectangle(x, y, width, height);
        if (ScreenBounds != target)
            SetWindowPos(_windowHandle, IntPtr.Zero, x - bar.Left, y - bar.Top, width, height, 0x0010 | 0x0020 | 0x0040);
        GetWindowRect(_windowHandle, out var actual);
        var changed = ScreenBounds != target;
        ScreenBounds = System.Drawing.Rectangle.FromLTRB(actual.Left, actual.Top, actual.Right, actual.Bottom);
        if (changed) PlacementChanged?.Invoke(ScreenBounds);
        SavePlacement($"embedded: {actual.Left},{actual.Top},{actual.Right - actual.Left},{actual.Bottom - actual.Top}; parent_matches_taskbar: {GetParent(_windowHandle) == taskbar}; visible: {IsWindowVisible(_windowHandle)}; tray: {trayBounds.Left},{trayBounds.Top},{trayBounds.Right},{trayBounds.Bottom}; taskbar: {bar.Left},{bar.Top},{bar.Right},{bar.Bottom}");
    }

    private void SavePlacement(string placement)
    {
        if (placement == _lastPlacement) return;
        _lastPlacement = placement;
        try
        {
            File.WriteAllText(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "CodexUsageMonitor", "taskbar-placement.txt"), placement);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private RectNative _cachedModernTray;
    private IntPtr _cachedTrayTaskbar;
    private DateTime _trayCheckedAt;
    private bool _readingTray;
    private int[][] _occupied = Array.Empty<int[]>();
    private int _density;
    private DateTime _layoutObservedAt;

    private bool TryGetModernTray(IntPtr taskbar, out RectNative bounds)
    {
        bounds = _cachedModernTray;
        if (!_readingTray && (taskbar != _cachedTrayTaskbar || DateTime.UtcNow - _trayCheckedAt > TimeSpan.FromSeconds(2)))
        {
            _cachedTrayTaskbar = taskbar;
            _trayCheckedAt = DateTime.UtcNow;
            _ = ReadModernTrayAsync();
        }
        return bounds.Right > bounds.Left;
    }

    private async Task ReadModernTrayAsync()
    {
        _readingTray = true;
        try
        {
            var taskbar = _cachedTrayTaskbar;
            var layout = await Task.Run(() => TaskbarGeometryProbe.ReadLayout(taskbar));
            if (layout is not null && taskbar == FindWindow("Shell_TrayWnd", null))
            {
                var rect = layout.Tray;
                _cachedModernTray = new RectNative { Left = rect[0], Top = rect[1], Right = rect[2], Bottom = rect[3] };
                _occupied = layout.Occupied;
                _layoutObservedAt = DateTime.UtcNow;
            }
        }
        catch (Exception) { /* Preserve the last known position on a transient probe failure. */ }
        finally { _readingTray = false; }
        PositionOnTaskbar();
    }

    private UIElement BuildContent()
    {
        var outer = new Grid { Background = System.Windows.Media.Brushes.Transparent };
        var capsule = new Border
        {
            Background = System.Windows.Media.Brushes.Transparent,
            BorderBrush = System.Windows.Media.Brushes.Transparent,
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(7),
            Padding = new Thickness(0),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };
        _capsule = capsule;
        capsule.Child = _providerItems;
        capsule.Cursor = System.Windows.Input.Cursors.Hand;
        capsule.ToolTip = "Kota ve token ayrıntılarını aç · tekrar tıklayınca kapanır";
        capsule.MouseLeftButtonUp += (_, _) => OpenRequested?.Invoke();
        outer.Children.Add(capsule);
        return outer;
    }

    private void RenderProviders()
    {
        _providerItems.Children.Clear();
        if (_density == 2)
        {
            var button = MakeCapsule("AI", "#282F39", "#D5DEE9");
            button.ToolTip = _snapshot is null ? "Kota bilgileri yükleniyor…" : string.Join("\n", _snapshot.Providers
                .OrderBy(provider => ProviderOrder(provider.Id)).Select(CompactSummary));
            _providerItems.Children.Add(button);
            return;
        }
        if (_snapshot is null)
        {
            _providerItems.Children.Add(MakeCapsule("Kota bilgileri yükleniyor…", "#303B49", "#D5DEE9"));
            return;
        }
        var providers = (_snapshot?.Providers ?? Array.Empty<ProviderSnapshot>())
            .OrderBy(provider => ProviderOrder(provider.Id));
        foreach (var provider in providers)
        {
            var accent = ProviderAccent(provider.Id);
            var displayName = provider.Id == "gemini" ? "Gemini" : provider.Name;
            var group = new Border
            {
                Background = Brush("#282F39"),
                BorderBrush = Brush("#3A4553"),
                BorderThickness = new Thickness(1),
                CornerRadius = _attached ? new CornerRadius(0, 0, 9, 9) : new CornerRadius(9),
                Width = _density == 1 ? 32 : 114,
                Padding = new Thickness(4, 3, 4, 3),
                Margin = new Thickness(0, 0, provider.Id == providers.Last().Id ? 0 : 4, 0),
                ToolTip = _density == 1 ? CompactSummary(provider) : provider.State == "stale"
                    ? $"{displayName} · önbellek · son başarılı veri {provider.ObservedAt.ToLocalTime():HH:mm}"
                    : $"{displayName} · {StateText(provider.State)}",
                Opacity = provider.State == "stale" ? 0.68 : 1
            };
            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            var logoTile = new Border
            {
                Background = System.Windows.Media.Brushes.White,
                CornerRadius = new CornerRadius(4),
                Width = 21,
                Height = 21,
                VerticalAlignment = VerticalAlignment.Center,
                Padding = new Thickness(2),
                Child = CreateLogo(provider.Id)
            };
            row.Children.Add(logoTile);

            var taskbarWindows = SelectTaskbarWindows(provider);
            if (_density == 0 && taskbarWindows.Count == 0)
                row.Children.Add(MakeCapsule((provider.State == "reauth_required" ? "Giriş" : "—"), "#493D2A", "#F1CF83"));
            for (var index = 0; index < taskbarWindows.Count && _density == 0; index++)
            {
                var window = taskbarWindows[index];
                var remainingValue = window.RemainingPercent ?? (window.UsedPercent is { } used ? 100 - used : null);
                var remaining = Math.Clamp(remainingValue ?? 0, 0, 100);
                var reset = FormatCompactReset(window.ResetsAt);
                // Reset is available on the percentage tooltip; keep the strip compact.

                var percentTone = remainingValue is null ? (Background: "#343D48", Foreground: "#ABB6C4")
                    : remaining <= 10 ? (Background: "#493236", Foreground: "#FFB5B4")
                    : remaining <= 30 ? (Background: "#493D2A", Foreground: "#F1CF83")
                    : (Background: ProviderAccentBackground(provider.Id), Foreground: accent);
                var percentText = remainingValue is { } exactRemaining
                    ? $"%{Math.Round(Math.Clamp(exactRemaining, 0, 100), MidpointRounding.AwayFromZero):0}"
                    : "—";
                var percentCapsule = MakeCapsule(percentText,
                    percentTone.Background, percentTone.Foreground);
                percentCapsule.ToolTip = $"{window.Label}: kalan kullanım · {reset}";
                row.Children.Add(percentCapsule);
                if (index < taskbarWindows.Count - 1)
                    row.Children.Add(new Border
                    {
                        Width = 1,
                        Height = 15,
                        Background = Brush("#596474"),
                        Margin = new Thickness(3, 0, 0, 0),
                        VerticalAlignment = VerticalAlignment.Center
                    });
            }

            group.Child = row;
            _providerItems.Children.Add(group);
        }
    }

    private static string CompactSummary(ProviderSnapshot provider)
    {
        var name = provider.Id == "gemini" ? "Gemini" : provider.Name;
        var windows = SelectTaskbarWindows(provider);
        if (windows.Count == 0) return $"{name}: {StateText(provider.State)}";
        var values = windows.Select(window =>
        {
            var remaining = window.RemainingPercent ?? (window.UsedPercent is { } used ? 100 - used : null);
            return $"{window.Label}: " + (remaining is { } value ? $"kalan %{Math.Clamp(value, 0, 100):0}" : "veri yok");
        });
        return $"{name} · " + string.Join(" · ", values);
    }

    private static Border MakeCapsule(string text, string background, string foreground) => new()
    {
        Background = Brush(background),
        CornerRadius = new CornerRadius(6),
        Height = 21,
        MinWidth = 21,
        Padding = new Thickness(2, 0, 2, 0),
        Margin = new Thickness(2, 0, 0, 0),
        VerticalAlignment = VerticalAlignment.Center,
        Child = new TextBlock
        {
            Text = text,
            Foreground = Brush(foreground),
            FontSize = 13,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
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
        LogoCache.TryGetValue(path, out var source);
        if (source is null && File.Exists(path))
        {
            source = new BitmapImage();
            source.BeginInit();
            source.DecodePixelWidth = 64;
            source.UriSource = new System.Uri(path, System.UriKind.Absolute);
            source.CacheOption = BitmapCacheOption.OnLoad;
            source.EndInit();
            source.Freeze();
            LogoCache[path] = source;
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
        "not_signed_in" or "not_installed" => "oturum bulunamadı",
        "forbidden" => "erişim reddedildi",
        _ => "kota alınamadı"
    };

    private static SolidColorBrush Brush(string color) => new((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(color));

    public void Dispose()
    {
        if (_locationHook != IntPtr.Zero) { UnhookWinEvent(_locationHook); _locationHook = IntPtr.Zero; }
        if (_disposed) return;
        Disable();
        if (_windowHandle != IntPtr.Zero && _taskbarParent != IntPtr.Zero)
        {
            SetParent(_windowHandle, IntPtr.Zero);
            var style = GetWindowLongPtr(_windowHandle, -16).ToInt64();
            SetWindowLongPtr(_windowHandle, -16, new IntPtr((style & ~0x40000000L) | 0x80000000L));
        }
        _disposed = true;
        Close();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RectNative
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string className, string? windowName);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr childAfter, string className, string? windowName);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hwnd, out RectNative rect);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")]
    private static extern IntPtr GetParent(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    private delegate void WinEventCallback(IntPtr hook, uint eventType, IntPtr hwnd, int objectId, int childId, uint threadId, uint timestamp);
    [DllImport("user32.dll")] private static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr module, WinEventCallback callback, uint processId, uint threadId, uint flags);
    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(IntPtr hook);

    [StructLayout(LayoutKind.Sequential)] private struct CursorPoint { public int X, Y; }
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out CursorPoint point);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetParent(IntPtr hwnd, IntPtr parent);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);
}
