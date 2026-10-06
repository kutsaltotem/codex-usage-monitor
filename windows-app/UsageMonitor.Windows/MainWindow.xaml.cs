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
        Deactivated += (_, _) =>
        {
            if (!_showingConfirmation)
                Dispatcher.BeginInvoke(new Action(Hide), DispatcherPriority.Background);
        };
        Closing += (_, args) =>
        {
            if (AllowClose) return;
            args.Cancel = true;
            Hide();
        };
        SetLoadingMessage("Henüz kota verisi yok.");
    }

    public void ShowNear(System.Drawing.Rectangle workingArea)
    {
        if (!IsVisible)
        {
            Opacity = 0;
            Show();
            UpdateLayout();
            var scale = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
            Left = workingArea.Right / scale - ActualWidth - 12;
            Top = workingArea.Bottom / scale - ActualHeight - 12;
            Opacity = 1;
            Activate();
        }
        else
        {
            Activate();
        }
        _ = RefreshAsync(quiet: true);
        _ = RefreshTokenHistoryAsync();
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
                    : isCached ? "Kaydedilmiş son veri" : "Kota bilgileri güncel";
        if (storageWarning) SyncStatusText.Text += " · yerel geçmiş yazılamadı";
        SyncStatusText.Foreground = staleCount > 0 || errorCount > 0 || partialCount > 0 || storageWarning
            ? Brush("#F1C46C")
            : Brush("#B7C8D9");
        UpdatedAtText.Text = $"Son eşitleme: {snapshot.GeneratedAt.ToLocalTime():dd MMM, HH:mm}";

        ProviderList.Children.Clear();
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
        UpdatedAtText.Text = "Codex, Gemini ve Claude oturumu aranıyor";
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
            Padding = new Thickness(12),
            Margin = new Thickness(0, 0, 0, 10)
        };
        var layout = new StackPanel();
        card.Child = layout;

        var header = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(CreateLogo(provider.Id, 28));
        var name = new TextBlock
        {
            Text = provider.Name,
            Foreground = Brush("#EDF1F6"),
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(9, 0, 0, 0)
        };
        Grid.SetColumn(name, 1);
        header.Children.Add(name);
        var badge = CreateStateBadge(provider.State);
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
                Text = provider.Message ?? StateDescription(provider.State),
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
                Columns = provider.Windows.Count == 2 ? 2 : 1,
                Rows = (int)Math.Ceiling(provider.Windows.Count / (double)(provider.Windows.Count == 2 ? 2 : 1))
            };
            foreach (var window in provider.Windows)
                windowsPanel.Children.Add(CreateQuotaWindow(window, accent, provider.Windows.Count == 2));
            layout.Children.Add(windowsPanel);
            if (!string.IsNullOrWhiteSpace(provider.Message))
                layout.Children.Add(new TextBlock
                {
                    Text = provider.Message,
                    Foreground = Brush("#E7C579"),
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = 10,
                    Margin = new Thickness(1, 8, 1, 0)
                });
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
        layout.Children.Add(source);
        return card;
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
        Grid.SetColumn(reset, 1);
        top.Children.Add(reset);
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
        return cell;
    }

    private FrameworkElement CreateStateBadge(string state)
    {
        var (text, foreground, background) = state switch
        {
            "ok" => ("GÜNCEL", "#7ADDB8", "#203B35"),
            "partial" => ("KISMİ", "#EBC66F", "#3E3422"),
            "stale" => ("ÖNBELLEK", "#EBC66F", "#3E3422"),
            "reauth_required" => ("GİRİŞ GEREKLİ", "#F0B56B", "#403325"),
            "not_installed" or "not_signed_in" => ("BAĞLANTI YOK", "#B4BFCC", "#333C48"),
            "forbidden" => ("ERİŞİM YOK", "#EF9894", "#432D32"),
            _ => ("ERİŞİLEMİYOR", "#EF9894", "#432D32")
        };
        return new Border
        {
            Background = Brush(background),
            CornerRadius = new CornerRadius(5),
            Padding = new Thickness(7, 4, 7, 4),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock { Text = text, Foreground = Brush(foreground), FontSize = 8, FontWeight = FontWeights.Bold }
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

    private static BitmapImage? LoadImage(string path)
    {
        if (!File.Exists(path)) return null;
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.UriSource = new Uri(path, UriKind.Absolute);
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.EndInit();
        bitmap.Freeze();
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
