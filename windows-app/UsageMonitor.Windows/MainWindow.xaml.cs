using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using System.Windows.Input;
using CodexUsageMonitor;

namespace CodexUsageMonitor.Windows;

public partial class MainWindow : Window
{
    private readonly UsageSyncService _syncService;
    private readonly TokenHistoryService _tokenHistoryService;
    private UsageSnapshot? _snapshot;
    private bool _refreshing;
    private readonly bool _previewOnly;
    private readonly List<(TextBlock Label, ProviderSnapshot Provider)> _ageLabels = new();
    private readonly DispatcherTimer _ageClock = new() { Interval = TimeSpan.FromMinutes(1) };

    public bool AllowClose { get; set; }
    public event Action<ProviderAppTarget>? AppLaunchRequested;
    public event Action<string>? ProviderActionRequested;
    private readonly Dictionary<ProviderAppTarget, TextBlock> _launchErrors = new();

    public MainWindow(UsageSyncService syncService, TokenHistoryService tokenHistoryService, bool previewOnly = false)
    {
        InitializeComponent();
        _previewOnly = previewOnly;
        _syncService = syncService;
        _tokenHistoryService = tokenHistoryService;
        RefreshButton.Click += async (_, _) => await RefreshAsync();
        CloseButton.Click += (_, _) => Hide();
        PreviewKeyDown += async (_, args) =>
        {
            if (args.Key == Key.Escape) { Hide(); args.Handled = true; }
            else if (args.Key == Key.R && Keyboard.Modifiers == ModifierKeys.Control)
            { args.Handled = true; await RefreshAsync(); }
        };
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
            if (_previewOnly) return;
            // Defer until the click target is known so the strip can still toggle closed.
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (IsVisible && !IsActive && !TaskbarUsageWindow.IsIndicatorUnderPointer()) Hide();
            }), DispatcherPriority.Background);
        };
        IsVisibleChanged += (_, _) =>
        {
            if (!IsVisible) { _launchErrors.Clear(); _ageClock.Stop(); _ageLabels.Clear(); ProviderList.Children.Clear(); TokenHistoryContent.Children.Clear(); TokenSummaryHost.Children.Clear(); }
            else _ageClock.Start();
        };
        SetLoadingMessage("Henüz kota verisi yok.");
        Theme.Changed += RepaintForTheme;
        Unloaded += (_, _) => Theme.Changed -= RepaintForTheme;
    }

    private void RepaintForTheme()
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.InvokeAsync(RepaintForTheme); return; }
        if (TokenPanel.Visibility == Visibility.Visible) ShowTokenTab();
        else ShowQuotaTab();
        if (_snapshot is null) ProviderList.Children.Clear();
        if (_snapshot is null) ProviderList.Children.Add(CreateEmptyState("İlk eşitleme tamamlanınca kota pencereleri burada görünecek."));
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
        var availableHeight = (anchor.Top - screen.Top) / scale;
        if (availableHeight > 0)
        {
            MinHeight = Math.Min(320, availableHeight);
            Height = Math.Min(560, availableHeight);
        }
        Top = anchor.Top / scale - Height;
    }

    // The tray menu/shortcut must remain usable even when the indicator has no safe slot.
    public void ShowNearTaskbar()
    {
        var area = System.Windows.Forms.Screen.FromPoint(System.Windows.Forms.Control.MousePosition).WorkingArea;
        ShowAttached(new System.Drawing.Rectangle(Math.Max(area.Left, area.Right - 350), area.Bottom, 350, 1));
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
        _launchErrors.Clear();
        _ageLabels.Clear();
        foreach (var provider in snapshot.Providers.OrderBy(ProviderOrder))
            ProviderList.Children.Add(CreateProviderCard(provider));
        if (ProviderList.Children.OfType<Border>().LastOrDefault() is { } last)
            last.Margin = new Thickness(0);
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
        if (_refreshing || _previewOnly) return;
        _refreshing = true;
        RefreshButton.IsEnabled = false;
        RefreshButton.Content = "…";
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
            RefreshButton.Content = "↻";
            RefreshButton.ToolTip = SyncStatusText.Text + " · Ctrl+R";
        }
    }

    private UIElement CreateProviderCard(ProviderSnapshot provider)
    {
        var accent = ProviderAccent(provider.Id);
        var card = new Border
        {
            Background = Brush("#1C2735"),
            BorderBrush = Brush("#2C3C50"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(11),
            Padding = new Thickness(10),
            Margin = new Thickness(0, 0, 0, 9)
        };
        var layout = new StackPanel();
        card.Child = layout;

        var header = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var logoTarget = provider.Id switch {
            "chatgpt-codex" => ProviderAppTarget.ChatGPT,
            "gemini" => ProviderAppTarget.Gemini,
            _ => ProviderAppTarget.Claude
        };
        var nameTarget = provider.Id switch {
            "chatgpt-codex" => ProviderAppTarget.Codex,
            "gemini" => ProviderAppTarget.Antigravity,
            _ => ProviderAppTarget.Claude
        };
        header.Children.Add(CreateAppLink(CreateLogo(provider.Id, 14), logoTarget, "Logo"));
        var name = new TextBlock
        {
            Text = provider.Id == "gemini" ? "Gemini" : provider.Id == "chatgpt-codex" ? "Codex" : provider.Id == "claude" ? "Claude" : provider.Name,
            Foreground = Brush("#EDF1F6"),
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(2, 0, 0, 0)
        };
        var brand = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        brand.Children.Add(CreateAppLink(name, nameTarget, "Name"));
        if (CreatePlanBadge(provider) is { } planBadge) brand.Children.Add(planBadge);
        if (provider.State != "ok") brand.Children.Add(CreateProviderWarning(provider));
        Grid.SetColumn(brand, 1);
        header.Children.Add(brand);
        var badge = CreateStateBadge(provider);
        Grid.SetColumn(badge, 2);
        header.Children.Add(badge);
        layout.Children.Add(header);
        var launchError = new TextBlock { Visibility = Visibility.Collapsed, Foreground = Brush("#F0B56B"),
            FontSize = 10, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0,0,0,6), Tag = "AppLaunchError" };
        layout.Children.Add(launchError);
        _launchErrors[logoTarget] = launchError;
        _launchErrors[nameTarget] = launchError;

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
                Text = "Kota bağlantısı bekleniyor",
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
            for (var index = 0; index < provider.Windows.Count; index++)
            {
                var cell = (Border)CreateQuotaWindow(provider.Windows[index], accent, provider.Windows.Count >= 2, index);
                if (index >= (provider.Windows.Count - 1) / windowsPanel.Columns * windowsPanel.Columns)
                    cell.Margin = new Thickness(cell.Margin.Left, 0, cell.Margin.Right, 0);
                windowsPanel.Children.Add(cell);
            }
            layout.Children.Add(windowsPanel);
        }
        if (CreateBreakdown(provider) is { } breakdown) layout.Children.Add(breakdown);

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

    private Button CreateAppLink(UIElement content, ProviderAppTarget target, string role)
    {
        var title = target == ProviderAppTarget.ChatGPT ? "ChatGPT" : target.ToString();
        var button = new Button { Content = content, Style = (Style)FindResource("ProviderAppLink"),
            ToolTip = title + " uygulamasını aç", Tag = target };
        System.Windows.Automation.AutomationProperties.SetName(button, title + " uygulamasını aç");
        System.Windows.Automation.AutomationProperties.SetAutomationId(button, $"Launch.{role}.{target}");
        button.Click += (_, args) =>
        {
            args.Handled = true;
            foreach (var error in _launchErrors.Values.Distinct()) error.Visibility = Visibility.Collapsed;
            if (_previewOnly) return;
            AppLaunchRequested?.Invoke(target);
        };
        return button;
    }

    internal void ShowLaunchError(ProviderAppTarget target, string message)
    {
        if (!_launchErrors.TryGetValue(target, out var error)) return;
        error.Text = message;
        error.Visibility = Visibility.Visible;
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
        Popup? popup = null;
        popup = new Popup
        {
            PlacementTarget = button, Placement = PlacementMode.Bottom, StaysOpen = false, AllowsTransparency = true,
            Child = new Border
            {
                Background = Brush("#202731"), BorderBrush = Brush("#465363"), BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(9), Padding = new Thickness(10), MaxWidth = 280,
                Child = BuildWarningContent(provider, details, () => popup!.IsOpen = false)
            }
        };
        button.Click += (_,_) => popup.IsOpen = !popup.IsOpen;
        button.Unloaded += (_,_) => popup.IsOpen = false;
        return button;
    }

    private UIElement BuildWarningContent(ProviderSnapshot provider, string details, Action close)
    {
        var stack = new StackPanel();
        stack.Children.Add(new TextBlock { Text = details, Foreground = Brush("#E5EBF2"), FontSize = 10, TextWrapping = TextWrapping.Wrap });
        var signInNeeded = provider.State is "not_signed_in" or "reauth_required" or "stale" or "forbidden";
        string? label = null;
        Action? action = null;
        if (provider.Id == "claude" && signInNeeded)
        {
            label = "Claude Code girişini aç";
            action = () => ProviderActionRequested?.Invoke("claude-login");
        }
        else if (provider.Id == "gemini" && signInNeeded)
        {
            label = "Antigravity'yi aç";
            action = () => AppLaunchRequested?.Invoke(ProviderAppTarget.Antigravity);
        }
        if (label is not null && action is not null && !_previewOnly)
        {
            var button = new Button { Content = label, Margin = new Thickness(0, 9, 0, 0), HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
                Style = (Style)Application.Current.FindResource("ActionButton"), FontSize = 11, Padding = new Thickness(10, 6, 10, 6) };
            System.Windows.Automation.AutomationProperties.SetAutomationId(button, "ProviderWarning.Action." + provider.Id);
            button.Click += (_, _) => { close(); action(); };
            stack.Children.Add(button);
        }
        return stack;
    }

    private static UIElement? CreatePlanBadge(ProviderSnapshot provider)
    {
        var plan = FormatPlan(provider.Plan);
        if (plan is null) return null;
        return new Border
        {
            Background = Brush("#1E3A5A"), BorderBrush = Brush("#34567C"), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(5), Padding = new Thickness(6, 1, 6, 1), Margin = new Thickness(6, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = $"{provider.Name} planı: {plan}",
            Tag = "PlanBadge",
            Child = new TextBlock { Text = plan, Foreground = Brush("#9CC4F2"), FontSize = 9, FontWeight = FontWeights.SemiBold }
        };
    }

    internal static string? FormatPlan(string? plan)
    {
        if (string.IsNullOrWhiteSpace(plan)) return null;
        var trimmed = plan.Trim();
        return char.ToUpperInvariant(trimmed[0]) + trimmed[1..];
    }

    private static UIElement? CreateBreakdown(ProviderSnapshot provider)
    {
        if (provider.Breakdown is not { Count: > 0 } rows) return null;
        var parts = rows.Where(row => row.Percent >= 1).OrderByDescending(row => row.Percent)
            .Select(row => $"{row.Label} %{Math.Round(row.Percent):0}").ToList();
        var text = parts.Count == 0 ? "Haftalık kullanım dağılımı: henüz yok" : "Haftalık dağılım: " + string.Join(" · ", parts);
        return new TextBlock
        {
            Text = text, Foreground = Brush("#8592A1"), FontSize = 9, Margin = new Thickness(1, 7, 1, 0),
            TextWrapping = TextWrapping.Wrap, Tag = "UsageBreakdown",
            ToolTip = "Haftalık kullanımın hangi Claude yüzeyinden geldiği (Claude Code, sohbet, Cowork)."
        };
    }

    public static string ProviderWarningDetails(ProviderSnapshot provider)
    {
        var action = provider.State switch
        {
            "not_signed_in" or "reauth_required" => provider.Id == "claude"
                ? "Claude kotası için Claude Code abonelik oturumu gerekir (Claude Desktop girişi yetmez). Aşağıdaki düğme girişi başlatır; tarayıcıda onayladıktan sonra kota kendiliğinden gelir."
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

    private UIElement CreateQuotaWindow(UsageWindow window, string accent, bool compact, int index)
    {
        var cell = new Border
        {
            Background = Brush("#141E2A"),
            BorderBrush = Brush("#26364A"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 9, 10, 9),
            Margin = new Thickness(compact && index % 2 == 1 ? 4 : 0, 0, compact && index % 2 == 0 ? 4 : 0, 6)
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
                ? $"%{Math.Round(Math.Clamp(exactRemaining, 0, 100), MidpointRounding.AwayFromZero):0} kalan"
                : "Ölçüm yok",
            Foreground = Brush("#EEF2F7"),
            FontSize = 18,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 5, 0, 7)
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
            Background = Accent(remainingValue is null ? "#657180" : remaining <= 10 ? "#EC777A" : remaining <= 30 ? "#E8B75F" : accent),
            CornerRadius = new CornerRadius(3),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
            Width = double.NaN
        };
        track.Child = fill;
        track.SizeChanged += (_, _) => fill.Width = track.ActualWidth * remaining / 100;
        panel.Children.Add(track);
        reset.Margin = new Thickness(0, 4, 0, 0);
        reset.FontSize = 9;
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
            "reauth_required" => ("Giriş gerekli", "#F0B56B", "#403325"),
            "not_installed" or "not_signed_in" => ("Bağlantı yok", "#B4BFCC", "#333C48"),
            "forbidden" => ("Erişim yok", "#EF9894", "#432D32"),
            _ => ("Erişilemiyor", "#EF9894", "#432D32")
        };
        var ageLabel = new TextBlock { Text = text, Foreground = Brush(foreground), FontSize = 9, FontWeight = FontWeights.SemiBold };
        if (state is "ok" or "partial" or "stale") _ageLabels.Add((ageLabel, provider));
        return new Border
        {
            ToolTip = observedAt == default ? "Henüz başarılı ölçüm yok" : $"Son başarılı ölçüm: {observedAt.ToLocalTime():dd MMM HH:mm:ss}",
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

    private static System.Windows.Controls.Image CreateLogo(string providerId, double size) => BrandMarks.CreateThemed(providerId, size);

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

    private static SolidColorBrush Brush(string color) => Theme.Brush(color);
    private static SolidColorBrush Accent(string color) => Theme.Accent(color);
}
