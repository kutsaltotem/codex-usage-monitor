using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CodexUsageMonitor;

namespace CodexUsageMonitor.Windows;

public partial class MainWindow : Window
{
    private readonly UsageSyncService _syncService;
    private readonly TokenHistoryService _tokenHistoryService;
    private UsageSnapshot? _snapshot;
    private bool _refreshing;
    private readonly List<(TextBlock Label, ProviderSnapshot Provider)> _ageLabels = new();
    private readonly DispatcherTimer _ageClock = new() { Interval = TimeSpan.FromMinutes(1) };

    public bool AllowClose { get; set; }

    public MainWindow(UsageSyncService syncService, TokenHistoryService tokenHistoryService)
    {
        InitializeComponent();
        _syncService = syncService;
        _tokenHistoryService = tokenHistoryService;
        RefreshButton.Click += async (_, _) => await RefreshAsync();
        CloseButton.Click += (_, _) => Hide();
        QuotaTabButton.Click += (_, _) => ShowQuotaTab();
        TokenTabButton.Click += (_, _) => ShowTokenTab();
        Closing += (_, args) =>
        {
            if (AllowClose) return;
            args.Cancel = true;
            Hide();
        };
        _ageClock.Tick += (_, _) =>
        {
            foreach (var (label, provider) in _ageLabels)
                label.Text = (provider.State == "stale" ? "Eski · " : "") + FormatAge(provider.ObservedAt, DateTimeOffset.UtcNow);
        };
        SmoothScrolling.Attach(QuotaPanel);
        SmoothScrolling.Attach(TokenPanel);
        Deactivated += (_, _) =>
        {
            // Defer until the click target is known so the strip can still toggle closed.
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (IsVisible && !IsActive && !TaskbarUsageWindow.IsIndicatorUnderPointer()) Hide();
            }), DispatcherPriority.Background);
        };
        IsVisibleChanged += (_, _) =>
        {
            if (!IsVisible) { _ageClock.Stop(); _ageLabels.Clear(); ProviderList.Children.Clear(); TokenHistoryContent.Children.Clear(); TokenSummaryHost.Children.Clear(); }
            else _ageClock.Start();
        };
        SetLoadingMessage("Henüz kota verisi yok.");
    }

    public void ShowAttached(System.Drawing.Rectangle anchor)
    {
        if (!IsVisible)
        {
            Opacity = 0;
            Show();
            if (_snapshot is not null) SetSnapshot(_snapshot, isCached: false);
            if (TokenPanel.Visibility == Visibility.Visible) RenderTokenHistory();
            UpdateLayout();
            AlignTo(anchor);
            Opacity = 1;
            Activate();
        }
        else
        {
            AlignTo(anchor);
            Activate();
        }
    }

    public void AlignTo(System.Drawing.Rectangle anchor)
    {
        if (!IsVisible) return;
        if (anchor.Width <= 0) { Hide(); return; }
        var scale = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
        Width = Math.Max(350, anchor.Width / scale);
        var screen = System.Windows.Forms.Screen.FromRectangle(anchor).WorkingArea;
        Left = Math.Clamp(anchor.Right / scale - Width, screen.Left / scale,
            Math.Max(screen.Left / scale, screen.Right / scale - Width));
        Top = anchor.Top / scale - ActualHeight;
    }

    public void SetSnapshot(UsageSnapshot snapshot, bool isCached)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.InvokeAsync(() => SetSnapshot(snapshot, isCached));
            return;
        }

        _snapshot = snapshot;
        var staleCount = snapshot.Providers.Count(provider => provider.State == "stale");
        var errorCount = snapshot.Providers.Count(provider => provider.State is not ("ok" or "partial" or "stale"));
        var partialCount = snapshot.Providers.Count(provider => provider.State == "partial");
        var storageWarning = !string.IsNullOrWhiteSpace(snapshot.StorageWarning);
        SyncStatusText.Text = staleCount > 0
            ? $"Son başarılı veri gösteriliyor · {staleCount} sağlayıcı eski"
            : errorCount > 0
                ? $"Bazı kota verileri alınamadı · {errorCount} sağlayıcı"
                : partialCount > 0
                    ? "Bazı kota pencereleri yanıt içinde yok"
                    : isCached ? "Kaydedilmiş son veri" : "Son okuma tamamlandı";
        if (storageWarning) SyncStatusText.Text += " · yerel geçmiş yazılamadı";
        SyncStatusText.Foreground = staleCount > 0 || errorCount > 0 || partialCount > 0 || storageWarning
            ? Brush("#F1C46C")
            : Brush("#B7C8D9");


        if (!IsVisible) return;
        ProviderList.Children.Clear();
        _ageLabels.Clear();
        foreach (var provider in snapshot.Providers.OrderBy(ProviderOrder))
            ProviderList.Children.Add(CreateProviderCard(provider));
    }

    public void SetLoadingMessage(string message)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.InvokeAsync(() => SetLoadingMessage(message));
            return;
        }
        if (_snapshot is not null) return;
        SyncStatusText.Text = message;

        ProviderList.Children.Clear();
        ProviderList.Children.Add(CreateEmptyState("İlk eşitleme tamamlanınca kota pencereleri burada görünecek."));
    }

    private async Task RefreshAsync(bool quiet = false)
    {
        if (_refreshing) return;
        _refreshing = true;
        RefreshButton.IsEnabled = false;
        if (!quiet || _snapshot is null)
        {
            SyncStatusText.Text = "Kota bilgileri yenileniyor…";
            SyncStatusText.Foreground = Brush("#B7C8D9");
        }
        try
        {
            var snapshot = await _syncService.SyncNowAsync();
            SetSnapshot(snapshot, isCached: false);
            if (TokenPanel.Visibility == Visibility.Visible)
                await RefreshTokenHistoryAsync();
        }
        catch (OperationCanceledException) { }
        catch (Exception)
        {
            if (_snapshot is null)
                SetLoadingMessage("Yenileme tamamlanamadı. CLI girişini kontrol edin.");
            else
            {
                SyncStatusText.Text = "Yenileme başarısız · son görülen veri korunuyor";
                SyncStatusText.Foreground = Brush("#F1C46C");
            }
        }
        finally
        {
            _refreshing = false;
            RefreshButton.IsEnabled = true;
        }
    }

    private UIElement CreateProviderCard(ProviderSnapshot provider)
    {
        var accent = ProviderAccent(provider.Id);
        var card = new Border
        {
            Background = Brush("#242B35"),
            BorderBrush = Brush("#394452"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(11),
            Padding = new Thickness(8),
            Margin = new Thickness(0, 0, 0, 10)
        };
        var layout = new StackPanel();
        card.Child = layout;

        var header = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(CreateLogo(provider.Id, 14));
        var name = new TextBlock
        {
            Text = provider.Id == "gemini" ? "Gemini" : provider.Id == "chatgpt-codex" ? "Codex" : provider.Name,
            Foreground = Brush("#EDF1F6"),
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(9, 0, 0, 0)
        };
        var brand = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        brand.Children.Add(name);
        if (provider.State != "ok") brand.Children.Add(CreateProviderWarning(provider));
        Grid.SetColumn(brand, 1);
        header.Children.Add(brand);
        var badge = CreateStateBadge(provider);
        Grid.SetColumn(badge, 2);
        header.Children.Add(badge);
        layout.Children.Add(header);

        if (provider.Windows.Count == 0)
        {
            var empty = new Border
            {
                Background = Brush("#1D232C"),
                BorderBrush = Brush("#343E4A"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(10, 9, 10, 9)
            };
            empty.Child = new TextBlock
            {
                Text = "Kota verisi yok",
                Foreground = Brush(provider.State is "stale" or "partial" ? "#E7C579" : "#AFBAC8"),
                TextWrapping = TextWrapping.Wrap,
                FontSize = 11
            };
            layout.Children.Add(empty);
        }
        else
        {
            var windowsPanel = new UniformGrid
            {
                Columns = provider.Windows.Count >= 2 ? 2 : 1,
                Rows = (int)Math.Ceiling(provider.Windows.Count / (double)(provider.Windows.Count >= 2 ? 2 : 1))
            };
            foreach (var window in provider.Windows)
                windowsPanel.Children.Add(CreateQuotaWindow(window, accent, provider.Windows.Count >= 2));
            layout.Children.Add(windowsPanel);
        }

        var source = new TextBlock
        {
            Text = $"Kaynak: {FriendlySource(provider.Source)}" + (provider.ObservedAt == default ? "" : $" · {provider.ObservedAt.ToLocalTime():HH:mm}"),
            Foreground = Brush("#8592A1"),
            FontSize = 9,
            Margin = new Thickness(1, 8, 1, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
            ToolTip = provider.Id == "claude"
                ? "Claude kotası, Claude Code'un yerel abonelik oturumu ve Anthropic'in belgelenmemiş kullanım uç noktasıyla okunur."
                : FriendlySource(provider.Source)
        };
        card.ToolTip = source.Text;
        return card;
    }

    private UIElement CreateProviderWarning(ProviderSnapshot provider)
    {
        var details = ProviderWarningDetails(provider);
        var button = new System.Windows.Controls.Button
        {
            Content = "!", Foreground = Brush("#FF8585"), FontWeight = FontWeights.Bold, FontSize = 12,
            Width = 22, Height = 24, Padding = new Thickness(0), Margin = new Thickness(3,0,0,0),
            Background = Brushes.Transparent, BorderThickness = new Thickness(0),
            Style = (Style)Application.Current.FindResource("ActionButton"), ToolTip = details,
            Tag = "ProviderWarning"
        };
        ToolTipService.SetInitialShowDelay(button, 100);
        var popup = new Popup
        {
            PlacementTarget = button, Placement = PlacementMode.Bottom, StaysOpen = false, AllowsTransparency = true,
            Child = new Border
            {
                Background = Brush("#202731"), BorderBrush = Brush("#465363"), BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(9), Padding = new Thickness(10), MaxWidth = 280,
                Child = new TextBlock { Text = details, Foreground = Brush("#E5EBF2"), FontSize = 10, TextWrapping = TextWrapping.Wrap }
            }
        };
        button.Click += (_,_) => popup.IsOpen = !popup.IsOpen;
        button.Unloaded += (_,_) => popup.IsOpen = false;
        return button;
    }

    public static string ProviderWarningDetails(ProviderSnapshot provider)
    {
        var action = provider.State switch
        {
            "not_signed_in" or "reauth_required" => provider.Id == "claude"
                ? "Claude Desktop girişi bu bağlantı için yeterli olmayabilir. Claude Code abonelik oturumu gerekir; ücretsiz hesabın kotası bu bağlantıyla okunamayabilir."
                : "Sağlayıcı uygulamasında girişini kontrol et; ardından yenile.",
            "forbidden" => "Hesabın planını ve kota erişimini kontrol et. Servis erişim izni vermedi.",
            "rate_limited" => "İstek sınırı nedeniyle bekle; sonraki otomatik ölçüm yeniden dener.",
            "stale" => "Son başarılı ölçüm korunuyor. Bağlantını ve sağlayıcı oturumunu kontrol et.",
            "partial" => "Servis bazı kota pencerelerini döndürmedi. Mevcut ölçümler gösteriliyor.",
            _ => "Bağlantını kontrol et; yenileme düğmesiyle tekrar dene."
        };
        var reason = provider.Message ?? StateDescription(provider.State);
        return $"{provider.Name}\n{reason}\n\n{action}\n\nDurum: {provider.ErrorCode ?? provider.State}\nKaynak: {FriendlySource(provider.Source)}" +
            (provider.ObservedAt == default ? "" : $"\nSon ölçüm: {provider.ObservedAt.ToLocalTime():dd MMM HH:mm}");
    }

    private UIElement CreateQuotaWindow(UsageWindow window, string accent, bool compact)
    {
        var cell = new Border
        {
            Background = Brush("#1D232C"),
            BorderBrush = Brush("#343E4A"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(9),
            Margin = new Thickness(0, 0, compact ? 7 : 0, 7)
        };
        var panel = new StackPanel();
        cell.Child = panel;

        var top = new Grid();
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var label = new TextBlock
        {
            Text = DisplayWindowLabel(window),
            Foreground = Brush("#B4BECA"),
            FontSize = 10,
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        top.Children.Add(label);
        var reset = new TextBlock
        {
            Text = FormatReset(window.ResetsAt),
            Foreground = Brush("#9AA6B4"),
            FontSize = 9,
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center
        };

        panel.Children.Add(top);

        var remainingValue = window.RemainingPercent ?? (window.UsedPercent is { } used ? 100 - used : null);
        var remaining = Math.Clamp(remainingValue ?? 0, 0, 100);
        var value = new TextBlock
        {
            Text = remainingValue is { } exactRemaining
                ? $"Kalan %{Math.Round(Math.Clamp(exactRemaining, 0, 100), MidpointRounding.AwayFromZero):0}"
                : "Kalan veri yok",
            Foreground = Brush("#EEF2F7"),
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 5, 0, 5)
        };
        panel.Children.Add(value);

        var track = new Border
        {
            Background = Brush("#3C4653"),
            CornerRadius = new CornerRadius(3),
            Height = 5,
            ClipToBounds = true
        };
        var fill = new Border
        {
            Background = Brush(remainingValue is null ? "#657180" : remaining <= 10 ? "#EC777A" : remaining <= 30 ? "#E8B75F" : accent),
            CornerRadius = new CornerRadius(3),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
            Width = double.NaN
        };
        track.Child = fill;
        track.SizeChanged += (_, _) => fill.Width = track.ActualWidth * remaining / 100;
        panel.Children.Add(track);
        reset.Margin = new Thickness(0, 4, 0, 0);
        reset.FontSize = 8;
        panel.Children.Add(reset);
        return cell;
    }

    public static string FormatAge(DateTimeOffset observedAt, DateTimeOffset now)
    {
        if (observedAt == default) return "Ölçüm yok";
        var elapsed = now - observedAt;
        if (elapsed.TotalMinutes < 1) return "Az önce";
        if (elapsed.TotalHours < 1) return $"{(int)elapsed.TotalMinutes} dakika önce";
        if (elapsed.TotalDays < 1) return $"{(int)elapsed.TotalHours} saat önce";
        return $"{(int)elapsed.TotalDays} gün önce";
    }

    private FrameworkElement CreateStateBadge(ProviderSnapshot provider)
    {
        var state = provider.State;
        var observedAt = provider.ObservedAt;
        var (text, foreground, background) = state switch
        {
            "ok" => (FormatAge(observedAt, DateTimeOffset.UtcNow), "#7ADDB8", "#203B35"),
            "partial" => (FormatAge(observedAt, DateTimeOffset.UtcNow), "#EBC66F", "#3E3422"),
            "stale" => ("Eski · " + FormatAge(observedAt, DateTimeOffset.UtcNow), "#EBC66F", "#3E3422"),
            "reauth_required" => ("GİRİŞ GEREKLİ", "#F0B56B", "#403325"),
            "not_installed" or "not_signed_in" => ("BAĞLANTI YOK", "#B4BFCC", "#333C48"),
            "forbidden" => ("ERİŞİM YOK", "#EF9894", "#432D32"),
            _ => ("ERİŞİLEMİYOR", "#EF9894", "#432D32")
        };
        var ageLabel = new TextBlock { Text = text, Foreground = Brush(foreground), FontSize = 8, FontWeight = FontWeights.SemiBold };
        if (state is "ok" or "partial" or "stale") _ageLabels.Add((ageLabel, provider));
        return new Border
        {
            ToolTip = $"Son başarılı ölçüm: {observedAt.ToLocalTime():dd MMM HH:mm:ss}",
            Background = Brush(background),
            CornerRadius = new CornerRadius(5),
            Padding = new Thickness(7, 4, 7, 4),
            VerticalAlignment = VerticalAlignment.Center,
            Child = ageLabel
        };
    }

    private UIElement CreateEmptyState(string message)
    {
        var panel = new Border
        {
            Background = Brush("#202731"),
            BorderBrush = Brush("#35404D"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(15),
            Margin = new Thickness(0, 0, 0, 10),
            Child = new TextBlock
            {
                Text = message,
                Foreground = Brush("#AAB5C3"),
                TextWrapping = TextWrapping.Wrap,
                FontSize = 11
            }
        };
        return panel;
    }

    private System.Windows.Controls.Image CreateLogo(string providerId, double size)
    {
        var file = LogoFile(providerId);
        var path = Path.Combine(AppContext.BaseDirectory, "Assets", file);
        var image = new System.Windows.Controls.Image
        {
            Width = size,
            Height = size,
            Stretch = Stretch.Uniform,
            Source = LoadImage(path)
        };
        return image;
    }

    private static readonly Dictionary<string, BitmapImage> LogoCache = new();

    private static BitmapImage? LoadImage(string path)
    {
        if (LogoCache.TryGetValue(path, out var cached)) return cached;
        if (!File.Exists(path)) return null;
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.DecodePixelWidth = 64;
        bitmap.UriSource = new Uri(path, UriKind.Absolute);
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.EndInit();
        bitmap.Freeze();
        LogoCache[path] = bitmap;
        return bitmap;
    }

    private static string LogoFile(string id) => id switch
    {
        "gemini" => "gemini-mark.jpg",
        "claude" => "claude-mark.jpg",
        _ => "openai-mark-codex.jpg"
    };

    private static int ProviderOrder(ProviderSnapshot provider) => provider.Id switch
    {
        "chatgpt-codex" => 0,
        "gemini" => 1,
        "claude" => 2,
        _ => 3
    };

    private static string ProviderAccent(string id) => id switch
    {
        "gemini" => "#829DFF",
        "claude" => "#E2A078",
        _ => "#37C9A3"
    };

    private static string DisplayWindowLabel(UsageWindow window)
    {
        if (window.Label.StartsWith("Gemini ·") || window.Kind.StartsWith("antigravity_")) return window.Label;
        if (window.Kind == "five_hour") return "5 saatlik";
        if (window.Kind == "weekly") return "Haftalık";
        if (window.Kind.StartsWith("weekly_", StringComparison.Ordinal)) return window.Label;
        if (window.Kind == "daily") return "Günlük";
        return string.IsNullOrWhiteSpace(window.Label) ? "Kota penceresi" : window.Label;
    }

    private static string FormatReset(DateTimeOffset? resetAt)
    {
        if (resetAt is null) return "Sıfırlama zamanı yok";
        var remaining = resetAt.Value - DateTimeOffset.Now;
        if (remaining <= TimeSpan.Zero) return "Sıfırlanıyor";
        if (remaining.TotalDays >= 1) return $"{(int)remaining.TotalDays} gün {(int)remaining.Hours} sa sonra";
        if (remaining.TotalHours >= 1) return $"{(int)remaining.TotalHours} sa {(int)remaining.Minutes} dk sonra";
        return $"{Math.Max(1, (int)Math.Ceiling(remaining.TotalMinutes))} dk sonra";
    }

    private static string FriendlySource(string source) => source switch
    {
        "local-cache" => "yerel önbellek",
        var value when value.StartsWith("Codex app OAuth", StringComparison.OrdinalIgnoreCase) => "Codex uygulama oturumu",
        var value when value.StartsWith("Codex CLI OAuth", StringComparison.OrdinalIgnoreCase) => "Codex uygulama oturumu",
        var value when value.StartsWith("Gemini/Antigravity OAuth", StringComparison.OrdinalIgnoreCase) => "Antigravity oturumu",
        var value when value.StartsWith("Gemini CLI OAuth", StringComparison.OrdinalIgnoreCase) => "Gemini/Antigravity oturumu",
        var value when value.StartsWith("Claude Code OAuth", StringComparison.OrdinalIgnoreCase) => "Claude Code oturumu · belgelenmemiş kota yolu",
        "codex-local-session" => "Codex uygulama oturumu",
        "gemini-local-session" => "Gemini/Antigravity oturumu",
        "claude-code-credentials" => "Claude Code oturumu",
        "none" => "yerel oturum bulunamadı",
        _ => source
    };

    private static string StateDescription(string state) => state switch
    {
        "not_installed" => "Codex uygulama oturumu bulunamadı. Codex'te ChatGPT hesabınızla giriş yapın.",
        "not_signed_in" => "Uygulama oturumu bulunamadı. İlgili sağlayıcı uygulamasında tekrar giriş yapın.",
        "reauth_required" => "Oturum süresi dolmuş. İlgili uygulamada tekrar giriş yapın.",
        "stale" => "Son başarılı kota verisi gösteriliyor.",
        "schema_changed" => "Sağlayıcının kota yanıt biçimi değişmiş olabilir.",
        "rate_limited" => "Kota servisi istekleri geçici olarak sınırladı.",
        _ => "Kota verisi şu anda alınamadı. Biraz sonra yeniden deneyin."
    };

    private static SolidColorBrush Brush(string color) => new((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(color));
}
