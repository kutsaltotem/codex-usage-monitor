using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Shapes;
using CodexUsageMonitor;

namespace CodexUsageMonitor.Windows;

public partial class MainWindow
{
    private static readonly CultureInfo TurkishCulture = CultureInfo.GetCultureInfo("tr-TR");
    private TokenHistorySnapshot? _tokenHistorySnapshot;
    private int _tokenPeriodDays = 30;
    private string _tokenProviderFilter = "all";
    private string _quotaProviderFilter = "all";
    private bool _renderingTokenHistory;
    private bool _savingTokenSettings;
    private IReadOnlyList<QuotaHistoryEntry> _quotaHistory = Array.Empty<QuotaHistoryEntry>();

    private void ShowQuotaTab()
    {
        QuotaPanel.Visibility = Visibility.Visible;
        if (_snapshot is not null) SetSnapshot(_snapshot, isCached: false);
        TokenPanel.Visibility = Visibility.Collapsed;
        TokenHost.Visibility = Visibility.Collapsed;
        QuotaTabButton.Background = Brush("#354252");
        QuotaTabButton.Foreground = Brush("#F1F4F8");
        TokenTabButton.Background = Brush("#222A34");
        TokenTabButton.Foreground = Brush("#AAB5C3");
    }

    private void ShowTokenTab()
    {
        QuotaPanel.Visibility = Visibility.Collapsed;
        TokenPanel.Visibility = Visibility.Visible;
        TokenHost.Visibility = Visibility.Visible;
        QuotaTabButton.Background = Brush("#222A34");
        QuotaTabButton.Foreground = Brush("#AAB5C3");
        TokenTabButton.Background = Brush("#354252");
        TokenTabButton.Foreground = Brush("#F1F4F8");
        RenderTokenHistory();
        _ = RefreshTokenHistoryAsync();
    }

    public void SetTokenHistorySnapshot(TokenHistorySnapshot snapshot)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.InvokeAsync(() => SetTokenHistorySnapshot(snapshot));
            return;
        }

        _tokenHistorySnapshot = snapshot;
        if (!_savingTokenSettings && IsVisible && TokenPanel.Visibility == Visibility.Visible) RenderTokenHistory();
    }

    private async Task RefreshTokenHistoryAsync()
    {
        try
        {
            _quotaHistory = await new QuotaSnapshotStore().LoadHistoryAsync();
            var snapshot = await _tokenHistoryService.SyncNowAsync();
            SetTokenHistorySnapshot(snapshot);
        }
        catch (OperationCanceledException) { }
        catch (Exception)
        {
            if (_tokenHistorySnapshot is null)
                TokenHistoryContent.Children.Clear();
            AddTokenMessage("Token geçmişi okunamadı. Yerel uygulama verileri kullanılabilir durumda değil.", "#F1C46C");
        }
    }

    private void RenderTokenHistory()
    {
        if (_tokenHistorySnapshot is null || _renderingTokenHistory) return;
        _renderingTokenHistory = true;
        try
        {
            TokenHistoryContent.Children.Clear();
            TokenSummaryHost.Children.Clear();
            var snapshot = _tokenHistorySnapshot;
            TokenHistoryContent.Children.Add(CreateQuotaHistoryCard());

            if (!string.IsNullOrWhiteSpace(snapshot.Message))
                AddTokenMessage(snapshot.Message, "#E7C579");

            var from = DateOnly.FromDateTime(DateTime.Now).AddDays(-(_tokenPeriodDays - 1));
            var through = DateOnly.FromDateTime(DateTime.Now);
            var filteredRows = FilterTokenRows(snapshot.Rows)
                .Where(row => row.LocalDate >= from && row.LocalDate <= through)
                .ToArray();
            TokenSummaryHost.Children.Add(CreateTokenSummary(filteredRows));
            if (filteredRows.Length == 0)
            {
                var emptyText = snapshot.Settings.AnyEnabled
                    ? "Henüz CLI token kaydı yok. Antigravity kota ölçümleri üstteki grafikte gösterilir."
                    : snapshot.Message?.Contains("silindi", StringComparison.OrdinalIgnoreCase) == true
                        ? "Geçmiş temizlendi. İstediğinde Codex, Gemini CLI veya Claude Code için yerel toplamayı yeniden açabilirsin."
                        : "Token geçmişi kapalı. Aşağıdaki seçimlerden toplamayı açabilirsin.";
                if (_tokenProviderFilter != "all") emptyText = "Bu sağlayıcı için bu dönemde token kaydı yok. Diğer kayıtlar korunuyor; Tümü ile görebilirsin.";
                TokenHistoryContent.Children.Add(CreateTokenChartCard(filteredRows));
                TokenHistoryContent.Children.Add(CreateEmptyState(emptyText));
                TokenHistoryContent.Children.Add(CreateHistoryControls(snapshot));
                return;
            }

            TokenHistoryContent.Children.Add(CreateTokenChartCard(filteredRows));
            TokenHistoryContent.Children.Add(CreateModelDropdown(filteredRows));
            TokenHistoryContent.Children.Add(new TextBlock
            {
                Text = "Tokenlar yerel CLI kayıtlarıdır; abonelik kotası veya fatura değildir.",
                Foreground = Brush("#8592A1"),
                FontSize = 9,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(2, 0, 2, 8)
            });
            TokenHistoryContent.Children.Add(CreateHistoryControls(snapshot));
        }
        finally { _renderingTokenHistory = false; }
    }

    private UIElement CreateHistoryControls(TokenHistorySnapshot snapshot)
    {
        var card = MakeTokenCard();
        card.Tag = "TokenHistoryControls";
        var stack = new StackPanel(); card.Child = stack;
        stack.Children.Add(new TextBlock { Text = "Token kayıtları", Foreground = Brush("#EDF1F6"), FontSize = 11, FontWeight = FontWeights.SemiBold });
        stack.Children.Add(new TextBlock { Text = "Yerel CLI sayaçları • konuşma metni kaydedilmez", Foreground = Brush("#AAB5C3"), FontSize = 9, Margin = new Thickness(0,4,0,8) });
        var providers = new UniformGrid { Columns = 3 };
        var codex = MakeProviderChoice("Codex", snapshot.Settings.CodexEnabled);
        var gemini = MakeProviderChoice("Gemini CLI", snapshot.Settings.GeminiEnabled);
        var claude = MakeProviderChoice("Claude", snapshot.Settings.ClaudeEnabled);
        providers.Children.Add(codex); providers.Children.Add(gemini); providers.Children.Add(claude);
        stack.Children.Add(providers);
        var status = new TextBlock { Text = "Seçimler otomatik kaydedilir", Foreground = Brush("#8592A1"), FontSize = 8, Margin = new Thickness(0,5,0,0) };
        stack.Children.Add(status);
        var retentionDays = snapshot.Settings.RetentionDays;
        async Task SaveAsync()
        {
            if (_savingTokenSettings) return;
            _savingTokenSettings = true;
            providers.IsEnabled = false; status.Text = "Kaydediliyor…";
            try
            {
                var updated = await _tokenHistoryService.SetProvidersEnabledAsync(codex.IsChecked == true,
                    gemini.IsChecked == true, claude.IsChecked == true, retentionDays);
                SetTokenHistorySnapshot(updated);
            }
            catch (Exception) { status.Text = "Kaydedilemedi; yeniden dene"; }
            finally
            {
                providers.IsEnabled = true;
                _savingTokenSettings = false;
                if (IsVisible && TokenPanel.Visibility == Visibility.Visible)
                    _ = Dispatcher.InvokeAsync(RenderTokenHistory, System.Windows.Threading.DispatcherPriority.Background);
            }
        }
        foreach (var choice in new[] { codex, gemini, claude })
        {
            choice.Checked += async (_, _) => await SaveAsync();
            choice.Unchecked += async (_, _) => await SaveAsync();
        }
        var options = new StackPanel { Visibility = Visibility.Collapsed, Margin = new Thickness(0,7,0,0) };
        var settings = MakeHistoryButton("Saklama ve temizleme ▾", false);
        settings.Margin = new Thickness(0,7,0,0);
        settings.Click += (_, _) => options.Visibility = options.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
        stack.Children.Add(settings); stack.Children.Add(options);
        var periods = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var days in new[] { 30, 90, 365 })
        {
            var option = MakeFilterButton($"{days} gün", days == retentionDays);
            option.Margin = new Thickness(0,0,5,0);
            option.Click += async (_, _) => { retentionDays = days; await SaveAsync(); };
            periods.Children.Add(option);
        }
        options.Children.Add(periods);
        var clear = MakeHistoryButton("Token geçmişini sil", false); clear.Margin = new Thickness(0,7,0,0);
        clear.Click += async (_, _) =>
        {
            var answer = MessageBox.Show(this, "Yerel token geçmişi silinsin ve toplama durdurulsun? Kota ölçümleri korunur.",
                "Token geçmişi", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
            if (answer != MessageBoxResult.Yes) return;
            try { SetTokenHistorySnapshot(await _tokenHistoryService.ClearHistoryAsync()); }
            catch (Exception) { status.Text = "Geçmiş silinemedi"; }
        };
        options.Children.Add(clear);
        return card;
    }

    private static CheckBox MakeProviderChoice(string name, bool enabled) => new()
    {
        Content = name, IsChecked = enabled, FontSize = 9,
        Foreground = Brush("#E5EBF2"), Margin = new Thickness(0,0,4,0),
        Style = (Style)Application.Current.FindResource("ProviderChoice")
    };

    private UIElement CreateQuotaHistoryCard()
    {
        var card = MakeTokenCard(); var stack = new StackPanel(); card.Child = stack;
        stack.Children.Add(new TextBlock { Text = "Kota geçmişi · son 24 saat", Foreground = Brush("#EDF1F6"), FontSize = 11, FontWeight = FontWeights.SemiBold });
        if (_quotaHistory.Count == 0)
        {
            stack.Children.Add(new TextBlock { Text = "İlk ölçümler bekleniyor…", Foreground = Brush("#AAB5C3"), FontSize = 9, Margin = new Thickness(0,6,0,0) });
        }
        var legend = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0,6,0,0) };
        legend.Children.Add(CreateLegend("Codex", "#37C9A3")); legend.Children.Add(CreateLegend("Gemini", "#829DFF"));
        legend.Children.Add(CreateLegend("Claude", "#E2A078")); stack.Children.Add(legend);
        stack.Children.Add(new TextBlock { Text = "Kullanılan kota (%) · saatlik ölçümler", Foreground = Brush("#8D99A8"), FontSize = 8, Margin = new Thickness(0,4,0,0) });
        stack.Children.Add(new TextBlock { Text = "5 saatlik / haftalık limit; 24 saatteki tüketim toplamı değil", Foreground = Brush("#8D99A8"), FontSize = 8, TextWrapping = TextWrapping.Wrap });
        var chart = new Canvas { Height = 105, Background = Brushes.Transparent };
        chart.SizeChanged += (_, _) => DrawQuotaHistory(chart);
        stack.Children.Add(chart);
        var filters = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0,6,0,0) };
        foreach (var (key, label) in new[] { ("all", "Tümü"), ("chatgpt-codex", "Codex"), ("gemini", "Gemini"), ("claude", "Claude") })
        {
            var button = MakeFilterButton(label, _quotaProviderFilter == key);
            button.Margin = new Thickness(0,0,5,0);
            button.Tag = "QuotaFilter";
            button.Click += (_,_) => { _quotaProviderFilter = key; RenderTokenHistory(); };
            filters.Children.Add(button);
        }
        stack.Children.Add(filters);
        return card;
    }

    private void DrawQuotaHistory(Canvas chart)
    {
        chart.Children.Clear();
        if (chart.ActualWidth < 40) return;
        var hours = _quotaHistory.GroupBy(row => row.GeneratedAt.ToLocalTime().ToString("dd HH"))
            .OrderBy(group => group.Min(row => row.GeneratedAt)).TakeLast(24).ToArray();
        if (hours.Length == 0) return;
        var width = chart.ActualWidth;
        var times = hours.Select(group => group.Min(row => row.GeneratedAt)).ToArray();
        var span = Math.Max(1,(times[^1] - times[0]).TotalSeconds);
        var positions = times.Select(time => 6 + (time - times[0]).TotalSeconds / span * (width - 12)).ToArray();
        for (var percent = 0; percent <= 100; percent += 50)
        {
            var y = 85 - percent * .75;
            chart.Children.Add(new Line { X1 = 0, X2 = width, Y1 = y, Y2 = y,
                Stroke = Brush("#35404C"), StrokeThickness = 1, StrokeDashArray = new DoubleCollection { 2,3 } });
        }
        foreach (var id in new[] { "chatgpt-codex", "gemini", "claude" })
        {
            if (_quotaProviderFilter != "all" && _quotaProviderFilter != id) continue;
            var line = new Polyline { Stroke = Brush(ProviderAccent(id)), StrokeThickness = 2,
                StrokeLineJoin = PenLineJoin.Round, IsHitTestVisible = false };
            for (var i = 0; i < hours.Length; i++)
            {
                var entry = hours[i].Where(row => row.Id == id).MaxBy(row => row.GeneratedAt);
                var measurement = entry is null ? null : QuotaChartMeasurement(entry);
                if (measurement is null) continue;
                var point = new System.Windows.Point(positions[i], 85 - Math.Clamp(100 - measurement.RemainingPercent!.Value,0,100) * .75);
                line.Points.Add(point);
                var dot = new Ellipse { Width = 5, Height = 5, Fill = Brush(ProviderAccent(id)), IsHitTestVisible = false };
                Canvas.SetLeft(dot, point.X - 2.5); Canvas.SetTop(dot, point.Y - 2.5); chart.Children.Add(dot);
            }
            chart.Children.Add(line);
        }
        for (var i = 0; i < hours.Length; i++)
        {
            var entries = hours[i].Where(row => _quotaProviderFilter == "all" || row.Id == _quotaProviderFilter).ToArray();
            var details = new List<string> { hours[i].Max(row => row.GeneratedAt).ToLocalTime().ToString("dd MMM HH:mm", TurkishCulture) };
            foreach (var entry in entries)
            {
                var measurement = QuotaChartMeasurement(entry);
                if (measurement is null) continue;
                var used = Math.Clamp(100 - measurement.RemainingPercent!.Value,0,100);
                var name = entry.Id == "gemini" ? "Gemini" : entry.Id == "claude" ? "Claude" : "Codex";
                var period = measurement.Kind == "five_hour" ? "5s" : measurement.Kind == "weekly" ? "hafta" : "kota";
                details.Add($"{name}: %{used:0.#} · {period}");
            }
            if (details.Count > 1)
            {
                var left = i == 0 ? 0 : (positions[i-1] + positions[i]) / 2;
                var right = i == hours.Length - 1 ? width : (positions[i] + positions[i+1]) / 2;
                var hit = new Border { Width = right - left, Height = 88, Background = Brushes.Transparent, ToolTip = string.Join(Environment.NewLine,details) };
                ToolTipService.SetInitialShowDelay(hit,0);
                Canvas.SetLeft(hit,left); chart.Children.Add(hit);
            }
            if (i == 0 || i == hours.Length - 1)
            {
                var label = new TextBlock { Text = hours[i].Max(row => row.GeneratedAt).ToLocalTime().ToString("HH:mm"), Foreground = Brush("#8D99A8"), FontSize = 8 };
                Canvas.SetLeft(label, Math.Min(positions[i], Math.Max(0,width - 32))); Canvas.SetTop(label,90); chart.Children.Add(label);
            }
        }
    }

    public static UsageWindow? QuotaChartMeasurement(QuotaHistoryEntry entry) => entry.Windows
        .Where(window => window.RemainingPercent.HasValue && !window.Kind.StartsWith("antigravity_", StringComparison.Ordinal))
        .OrderBy(window => window.Kind == "five_hour" ? 0 : window.Kind == "weekly" ? 1 : 2)
        .FirstOrDefault();

    private UIElement CreateTokenSummary(IReadOnlyList<TokenUsageRow> rows)
    {
        var totals = AggregateTokens(rows);
        var grid = new UniformGrid { Columns = 3, Margin = new Thickness(0, 0, 0, 9) };
        grid.Children.Add(CreateKpi("TOPLAM TOKEN", FormatCount(totals.Total), "kayıtlardaki toplam", "#37C9A3"));
        grid.Children.Add(CreateKpi("GİRDİ", FormatCount(totals.Input), "input", "#829DFF"));
        grid.Children.Add(CreateKpi("ÇIKTI", FormatCount(totals.Output), "output", "#E7B766"));
        return grid;
    }

    private UIElement CreateKpi(string label, string value, string detail, string accent)
    {
        var border = new Border
        {
            Background = Brush("#242B35"),
            BorderBrush = Brush("#394452"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(9),
            Padding = new Thickness(9, 8, 8, 7),
            Margin = new Thickness(0, 0, 6, 0)
        };
        border.Child = new StackPanel
        {
            Children =
            {
                new TextBlock { Text = label, Foreground = Brush("#AAB5C3"), FontSize = 8, FontWeight = FontWeights.Bold },
                new TextBlock { Text = value, Foreground = Brush(accent), FontSize = 15, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 3, 0, 1), TextTrimming = TextTrimming.CharacterEllipsis },
                new TextBlock { Text = detail, Foreground = Brush("#8592A1"), FontSize = 8 }
            }
        };
        return border;
    }

    private UIElement CreateTokenChartCard(IReadOnlyList<TokenUsageRow> rows)
    {
        var card = MakeTokenCard();
        var stack = new StackPanel();
        card.Child = stack;
        var header = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(new TextBlock { Text = "Token kullanımı", Foreground = Brush("#EAF0F7"), FontSize = 11, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        var periods = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var (days, label) in new[] { (7, "7g"), (30, "30g"), (90, "90g"), (365, "1y") })
        {
            var button = MakeFilterButton(label, _tokenPeriodDays == days);
            button.Margin = new Thickness(3, 0, 0, 0);
            button.Click += (_, _) => { _tokenPeriodDays = days; RenderTokenHistory(); };
            periods.Children.Add(button);
        }
        Grid.SetColumn(periods, 1);
        header.Children.Add(periods);
        stack.Children.Add(header);

        var legend = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
        legend.Children.Add(CreateLegend("Codex", "#37C9A3"));
        legend.Children.Add(CreateLegend("Gemini", "#829DFF"));
        legend.Children.Add(CreateLegend("Claude", "#E2A078"));
        stack.Children.Add(legend);

        var chart = new Canvas { Height = 128, HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch, ClipToBounds = true };
        chart.SizeChanged += (_, _) => DrawTokenChart(chart, rows);
        stack.Children.Add(chart);

        var filters = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 7, 0, 0) };
        foreach (var (key, label) in new[]
                 {
                     ("all", "Tümü"),
                     ("chatgpt-codex", "Codex"),
                     ("gemini", "Gemini"),
                     ("claude", "Claude")
                 })
        {
            var button = MakeFilterButton(label, _tokenProviderFilter == key);
            button.Margin = new Thickness(0, 0, 5, 0);
            button.Click += (_, _) => { _tokenProviderFilter = key; RenderTokenHistory(); };
            filters.Children.Add(button);
        }
        stack.Children.Add(filters);
        return card;
    }

    private void DrawTokenChart(Canvas chart, IReadOnlyList<TokenUsageRow> rows)
    {
        chart.Children.Clear();
        var width = chart.ActualWidth;
        if (width < 40) return;
        var bins = MakeChartBins(rows);
        if (bins.Count == 0) return;

        var plotTop = 7d;
        var plotBottom = 99d;
        var plotHeight = plotBottom - plotTop;
        var maxValue = Math.Max(1d, bins.Max(bin => _tokenProviderFilter switch
        {
            "chatgpt-codex" => bin.Codex,
            "gemini" => bin.Gemini,
            "claude" => bin.Claude,
            _ => bin.Codex + bin.Gemini + bin.Claude
        }));
        foreach (var fraction in new[] { 0d, 0.5d, 1d })
        {
            var y = plotBottom - plotHeight * fraction;
            chart.Children.Add(new Line
            {
                X1 = 0, X2 = width, Y1 = y, Y2 = y,
                Stroke = Brush("#35404C"), StrokeThickness = 1,
                StrokeDashArray = fraction == 0 ? null : new DoubleCollection { 2, 3 }
            });
        }

        var slot = width / bins.Count;
        var barWidth = Math.Max(3, Math.Min(20, slot * 0.58));
        for (var index = 0; index < bins.Count; index++)
        {
            var bin = bins[index];
            var x = slot * index + (slot - barWidth) / 2;
            var showCodex = _tokenProviderFilter is "all" or "chatgpt-codex";
            var showGemini = _tokenProviderFilter is "all" or "gemini";
            var showClaude = _tokenProviderFilter is "all" or "claude";
            var codexHeight = showCodex ? plotHeight * bin.Codex / maxValue : 0;
            var geminiHeight = showGemini ? plotHeight * bin.Gemini / maxValue : 0;
            var claudeHeight = showClaude ? plotHeight * bin.Claude / maxValue : 0;
            var cursorY = plotBottom;
            if (showCodex && codexHeight > 0)
            {
                cursorY -= codexHeight;
                AddChartBar(chart, x, cursorY, barWidth, codexHeight, "#37C9A3");
            }
            if (showGemini && geminiHeight > 0)
            {
                cursorY -= geminiHeight;
                AddChartBar(chart, x, cursorY, barWidth, geminiHeight, "#829DFF");
            }
            if (showClaude && claudeHeight > 0)
            {
                cursorY -= claudeHeight;
                AddChartBar(chart, x, cursorY, barWidth, claudeHeight, "#E2A078");
            }

            var total = bin.Codex + bin.Gemini + bin.Claude;
            var title = $"{bin.Label}\nCodex: {FormatCount(bin.Codex)}\nGemini: {FormatCount(bin.Gemini)}\nClaude: {FormatCount(bin.Claude)}\nToplam: {FormatCount(total)} token";
            var hitTarget = new Border { Width = slot, Height = plotBottom - plotTop, Background = Brushes.Transparent, ToolTip = title };
            ToolTipService.SetInitialShowDelay(hitTarget, 0);
            ToolTipService.SetShowDuration(hitTarget, 60000);
            Canvas.SetLeft(hitTarget, slot * index);
            Canvas.SetTop(hitTarget, plotTop);
            chart.Children.Add(hitTarget);

            var label = new TextBlock
            {
                Text = bin.AxisLabel,
                Width = slot,
                TextAlignment = TextAlignment.Center,
                Foreground = Brush("#8D99A8"),
                FontSize = 8,
                ToolTip = title
            };
            Canvas.SetLeft(label, slot * index);
            Canvas.SetTop(label, 105);
            chart.Children.Add(label);
        }
    }

    private IReadOnlyList<ChartBin> MakeChartBins(IReadOnlyList<TokenUsageRow> rows)
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        var start = today.AddDays(-(_tokenPeriodDays - 1));
        var dayTotals = FilterTokenRows(rows)
            .Where(row => row.LocalDate >= start && row.LocalDate <= today)
            .GroupBy(row => row.LocalDate)
            .ToDictionary(group => group.Key, group => new
            {
                Codex = group.Where(row => row.ProviderId == "chatgpt-codex").Sum(row => (double)row.TotalTokens),
                Gemini = group.Where(row => row.ProviderId == "gemini").Sum(row => (double)row.TotalTokens),
                Claude = group.Where(row => row.ProviderId == "claude").Sum(row => (double)row.TotalTokens)
            });

        var bins = new List<ChartBin>();
        if (_tokenPeriodDays == 7)
        {
            for (var offset = 0; offset < 7; offset++)
            {
                var day = start.AddDays(offset);
                dayTotals.TryGetValue(day, out var totals);
                bins.Add(new ChartBin(day.ToString("dd MMM", TurkishCulture), day.ToString("ddd", TurkishCulture), totals?.Codex ?? 0, totals?.Gemini ?? 0, totals?.Claude ?? 0));
            }
        }
        else if (_tokenPeriodDays == 365)
        {
            var monthStart = new DateOnly(start.Year, start.Month, 1);
            var endMonth = new DateOnly(today.Year, today.Month, 1);
            while (monthStart <= endMonth)
            {
                var codex = 0d;
                var gemini = 0d;
                var claude = 0d;
                foreach (var item in dayTotals)
                {
                    if (item.Key.Year == monthStart.Year && item.Key.Month == monthStart.Month)
                    {
                        codex += item.Value.Codex;
                        gemini += item.Value.Gemini;
                        claude += item.Value.Claude;
                    }
                }
                bins.Add(new ChartBin(monthStart.ToString("MMMM yyyy", TurkishCulture), monthStart.ToString("MMM", TurkishCulture), codex, gemini, claude));
                monthStart = monthStart.AddMonths(1);
            }
        }
        else
        {
            var offset = 0;
            while (offset < _tokenPeriodDays)
            {
                var from = start.AddDays(offset);
                var to = DateOnly.FromDayNumber(Math.Min(today.DayNumber, from.AddDays(6).DayNumber));
                var codex = 0d;
                var gemini = 0d;
                var claude = 0d;
                foreach (var item in dayTotals)
                {
                    if (item.Key >= from && item.Key <= to)
                    {
                        codex += item.Value.Codex;
                        gemini += item.Value.Gemini;
                        claude += item.Value.Claude;
                    }
                }
                bins.Add(new ChartBin($"{from:dd MMM} – {to:dd MMM}", from.ToString("dd MMM", TurkishCulture), codex, gemini, claude));
                offset += 7;
            }
        }
        return bins;
    }

    private UIElement CreateModelDropdown(IReadOnlyList<TokenUsageRow> rows)
    {
        var dropdown = new Expander
        {
            Header = new TextBlock { Text = "Model dökümü", FontSize = 11, FontWeight = FontWeights.SemiBold },
            Foreground = Brush("#EAF0F7"), Background = Brush("#242B35"), Margin = new Thickness(0,0,0,8)
        };
        dropdown.Expanded += (_,_) => { if (dropdown.Content is null) dropdown.Content = CreateModelBreakdown(rows); };
        dropdown.Collapsed += (_,_) => dropdown.Content = null;
        return dropdown;
    }

    private UIElement CreateModelBreakdown(IReadOnlyList<TokenUsageRow> rows)
    {
        var card = MakeTokenCard();
        var stack = new StackPanel();
        card.Child = stack;
        stack.Children.Add(new TextBlock
        {
            Text = "Model dökümü",
            Foreground = Brush("#EAF0F7"),
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 7)
        });

        var models = FilterTokenRows(rows)
            .Where(row => row.LocalDate >= DateOnly.FromDateTime(DateTime.Now).AddDays(-(_tokenPeriodDays - 1)))
            .GroupBy(row => (row.ProviderId, row.Model))
            .Select(group => new
            {
                ProviderId = group.Key.ProviderId,
                Model = group.Key.Model,
                Input = Sum(group.Select(row => row.InputTokens)),
                Output = Sum(group.Select(row => row.OutputTokens)),
                Read = Sum(group.Select(row => row.CacheReadTokens)),
                Write = Sum(group.Select(row => row.CacheWriteTokens)),
                Thoughts = Sum(group.Select(row => row.ThoughtsTokens)),
                Tool = Sum(group.Select(row => row.ToolTokens)),
                Total = Sum(group.Select(row => row.TotalTokens))
            })
            .OrderByDescending(item => item.Total)
            .ToArray();

        if (models.Length == 0)
        {
            stack.Children.Add(new TextBlock { Text = "Bu filtrede model satırı yok.", Foreground = Brush("#AAB5C3"), FontSize = 10 });
            return card;
        }

        var max = Math.Max(1d, models.Max(item => (double)item.Total));
        foreach (var item in models)
        {
            var rowBorder = new Border
            {
                Background = Brush("#1D232C"),
                BorderBrush = Brush("#343E4A"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(7),
                Padding = new Thickness(8, 6, 8, 6),
                Margin = new Thickness(0, 0, 0, 5)
            };
            var rowStack = new StackPanel();
            rowBorder.Child = rowStack;
            var top = new Grid();
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            top.Children.Add(new TextBlock
            {
                Text = item.Model,
                Foreground = Brush("#EEF2F7"),
                FontSize = 10,
                FontWeight = FontWeights.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis,
                ToolTip = item.Model
            });
            var total = new TextBlock
            {
                Text = FormatCount(item.Total),
                Foreground = Brush("#D9E1EA"),
                FontSize = 10,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(7, 0, 0, 0)
            };
            Grid.SetColumn(total, 1);
            top.Children.Add(total);
            rowStack.Children.Add(top);
            rowStack.Children.Add(new TextBlock
            {
                Text = ProviderDisplayName(item.ProviderId),
                Foreground = Brush(ProviderAccent(item.ProviderId)),
                FontSize = 8,
                Margin = new Thickness(0, 2, 0, 3)
            });
            var details = $"Girdi {FormatCount(item.Input)} · Çıktı {FormatCount(item.Output)} · Cache oku {FormatCount(item.Read)} · yaz {FormatCount(item.Write)}";
            if (item.Thoughts > 0 || item.Tool > 0)
                details += $" · Düşünce {FormatCount(item.Thoughts)} · Araç {FormatCount(item.Tool)}";
            rowStack.Children.Add(new TextBlock
            {
                Text = details,
                Foreground = Brush("#AAB5C3"),
                FontSize = 8,
                TextWrapping = TextWrapping.Wrap
            });
            var track = new Border { Background = Brush("#3C4653"), CornerRadius = new CornerRadius(2), Height = 3, ClipToBounds = true, Margin = new Thickness(0, 5, 0, 0) };
            var fill = new Border { Background = Brush(ProviderAccent(item.ProviderId)), CornerRadius = new CornerRadius(2), HorizontalAlignment = System.Windows.HorizontalAlignment.Left };
            track.Child = fill;
            track.SizeChanged += (_, _) => fill.Width = track.ActualWidth * item.Total / max;
            rowStack.Children.Add(track);
            stack.Children.Add(rowBorder);
        }
        return card;
    }

    private IEnumerable<TokenUsageRow> FilterTokenRows(IEnumerable<TokenUsageRow> rows) =>
        _tokenProviderFilter == "all" ? rows : rows.Where(row => row.ProviderId == _tokenProviderFilter);

    private static (long Input, long Output, long Read, long Write, long Thoughts, long Tool, long Total) AggregateTokens(IEnumerable<TokenUsageRow> rows)
    {
        var materialized = rows.ToArray();
        return (
            Sum(materialized.Select(row => row.InputTokens)),
            Sum(materialized.Select(row => row.OutputTokens)),
            Sum(materialized.Select(row => row.CacheReadTokens)),
            Sum(materialized.Select(row => row.CacheWriteTokens)),
            Sum(materialized.Select(row => row.ThoughtsTokens)),
            Sum(materialized.Select(row => row.ToolTokens)),
            Sum(materialized.Select(row => row.TotalTokens)));
    }

    private static long Sum(IEnumerable<long> values)
    {
        long sum = 0;
        foreach (var value in values)
            sum = long.MaxValue - sum < value ? long.MaxValue : sum + Math.Max(0, value);
        return sum;
    }

    private static string FormatCount(long value) => value.ToString("N0", TurkishCulture);

    private static string FormatCount(double value) => Math.Round(value).ToString("N0", TurkishCulture);

    private static string ProviderDisplayName(string providerId) => providerId switch
    {
        "gemini" => "Gemini CLI",
        "claude" => "Claude Code",
        _ => "ChatGPT / Codex"
    };

    private static Border MakeTokenCard() => new()
    {
        Background = Brush("#242B35"),
        BorderBrush = Brush("#394452"),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(10),
        Padding = new Thickness(10),
        Margin = new Thickness(0, 0, 0, 8)
    };

    private static System.Windows.Controls.Button MakeHistoryButton(string label, bool primary) => new()
    {
        Content = label,
        Style = (Style)Application.Current.FindResource("ActionButton"),
        FontSize = 9,
        Padding = new Thickness(9, 6, 9, 6),
        Background = Brush(primary ? "#30453F" : "#303A47"),
        BorderBrush = Brush(primary ? "#416558" : "#465363")
    };

    private static System.Windows.Controls.Button MakeFilterButton(string label, bool selected) => new()
    {
        Content = label,
        Style = (Style)Application.Current.FindResource("ActionButton"),
        FontSize = 8,
        Padding = new Thickness(6, 4, 6, 4),
        Background = Brush(selected ? "#354252" : "#202731"),
        BorderBrush = Brush(selected ? "#596A7F" : "#38424F"),
        Foreground = Brush(selected ? "#F1F4F8" : "#AAB5C3")
    };

    private static UIElement CreateLegend(string label, string color)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 13, 0), VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(new Ellipse { Width = 7, Height = 7, Fill = Brush(color), Margin = new Thickness(0, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(new TextBlock { Text = label, Foreground = Brush("#AAB5C3"), FontSize = 9 });
        return row;
    }

    private static void AddChartBar(Canvas chart, double x, double y, double width, double height, string color)
    {
        var bar = new Border
        {
            Width = width,
            Height = height,
            Background = Brush(color),
            CornerRadius = new CornerRadius(3, 3, 0, 0),
            IsHitTestVisible = false
        };
        Canvas.SetLeft(bar, x);
        Canvas.SetTop(bar, y);
        chart.Children.Add(bar);
    }

    private void AddTokenMessage(string text, string color)
    {
        var message = new Border
        {
            Background = Brush("#3C3425"),
            BorderBrush = Brush("#665638"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(7),
            Padding = new Thickness(9, 7, 9, 7),
            Margin = new Thickness(0, 0, 0, 8),
            Child = new TextBlock { Text = text, Foreground = Brush(color), FontSize = 9, TextWrapping = TextWrapping.Wrap }
        };
        var controls = TokenHistoryContent.Children.OfType<FrameworkElement>().FirstOrDefault(element => Equals(element.Tag, "TokenHistoryControls"));
        if (controls is null) TokenHistoryContent.Children.Add(message);
        else TokenHistoryContent.Children.Insert(TokenHistoryContent.Children.IndexOf(controls), message);
    }

    private sealed record ChartBin(string Label, string AxisLabel, double Codex, double Gemini, double Claude);
}
