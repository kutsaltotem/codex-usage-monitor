using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows;
using CodexUsageMonitor;
using Forms = System.Windows.Forms;

namespace CodexUsageMonitor.Windows;

public partial class App : System.Windows.Application
{
    private UsageSyncService? _syncService;
    private TokenHistoryService? _tokenHistoryService;
    private readonly LowQuotaNotificationPolicy _notificationPolicy = new();
    private MainWindow? _popup;
    private AppBarStripWindow? _appBar;
    private Forms.NotifyIcon? _trayIcon;
    private Icon? _trayBitmapIcon;
    private Forms.ToolStripMenuItem? _appBarMenuItem;
    private Mutex? _singleInstanceMutex;
    private bool _ownsSingleInstanceMutex;
    private readonly List<HttpClient> _httpClients = new();
    private bool _isShuttingDown;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _singleInstanceMutex = new Mutex(true, @"Local\CodexUsageMonitor", out var createdNew);
        if (!createdNew)
        {
            _singleInstanceMutex.Dispose();
            _singleInstanceMutex = null;
            Shutdown();
            return;
        }
        _ownsSingleInstanceMutex = true;

        var store = new QuotaSnapshotStore();
        var codexHttp = QuotaHttpClient.Create();
        var geminiHttp = QuotaHttpClient.Create();
        var claudeHttp = QuotaHttpClient.Create();
        _httpClients.Add(codexHttp);
        _httpClients.Add(geminiHttp);
        _httpClients.Add(claudeHttp);
        _syncService = new UsageSyncService(
        [
            new CodexQuotaConnector(codexHttp),
            new GeminiQuotaConnector(geminiHttp),
            new ClaudeQuotaConnector(claudeHttp)
        ], store);
        _tokenHistoryService = new TokenHistoryService();

        _popup = new MainWindow(_syncService, _tokenHistoryService);
        _appBar = new AppBarStripWindow();
        _syncService.SnapshotUpdated += OnSnapshotUpdated;
        _tokenHistoryService.SnapshotUpdated += OnTokenHistoryUpdated;
        try { await _notificationPolicy.InitializeAsync(); }
        catch (Exception) { }
        InitializeTrayIcon();

        try
        {
            var cached = await store.LoadLatestAsync();
            if (cached is not null)
            {
                _popup.SetSnapshot(cached, isCached: true);
                _appBar.SetSnapshot(cached);
            }
        }
        catch
        {
            _popup?.SetLoadingMessage("Önbellek okunamadı; yeni veri bekleniyor.");
        }

        if (_appBarMenuItem is not null) _appBarMenuItem.Checked = true;
        _ = StartAutomaticSyncAsync();
        _ = StartTokenHistoryAsync();
    }

    private async Task StartAutomaticSyncAsync()
    {
        try
        {
            if (_syncService is not null)
                await _syncService.StartAsync();
        }
        catch (Exception)
        {
            _popup?.SetLoadingMessage("Otomatik eşitleme başlatılamadı. Yenile düğmesini deneyin.");
        }
    }

    private async Task StartTokenHistoryAsync()
    {
        try
        {
            if (_tokenHistoryService is not null)
                await _tokenHistoryService.StartAsync();
        }
        catch (Exception)
        {
            _popup?.SetTokenHistorySnapshot(new TokenHistorySnapshot(
                DateTimeOffset.UtcNow,
                _tokenHistoryService?.Settings ?? new TokenHistorySettings(false, false),
                Array.Empty<TokenUsageRow>(),
                "Yerel token geçmişi başlatılamadı; kota eşitlemesi bundan etkilenmez."));
        }
    }

    private void InitializeTrayIcon()
    {
        var menu = new Forms.ContextMenuStrip();
        var open = new Forms.ToolStripMenuItem("Kullanım özetini aç");
        open.Click += (_, _) => ShowPopup();
        menu.Items.Add(open);

        var refresh = new Forms.ToolStripMenuItem("Şimdi yenile");
        refresh.Click += async (_, _) => await RefreshFromTrayAsync();
        menu.Items.Add(refresh);

        menu.Items.Add(new Forms.ToolStripSeparator());
        var startupItem = new Forms.ToolStripMenuItem("Windows oturumunda başlat") { CheckOnClick = true };
        try
        {
            startupItem.Checked = WindowsStartupRegistration.IsEnabled();
            if (startupItem.Checked)
            {
                try { WindowsStartupRegistration.SetEnabled(true); } // Refresh the path if the portable folder moved.
                catch (Exception) { startupItem.ToolTipText = "Açılış kaydı güncellenemedi; eski yol kullanılıyor."; }
            }
        }
        catch (Exception) { startupItem.Enabled = false; }
        var changingStartupItem = false;
        startupItem.CheckedChanged += (_, _) =>
        {
            if (changingStartupItem) return;
            var requestedValue = startupItem.Checked;
            try
            {
                WindowsStartupRegistration.SetEnabled(requestedValue);
            }
            catch (Exception)
            {
                changingStartupItem = true;
                startupItem.Checked = !requestedValue;
                changingStartupItem = false;
                MessageBox.Show(
                    "Windows açılış tercihi kaydedilemedi. Kullanıcı kayıt defteri yazma iznini kontrol edin.",
                    "Kullanım Monitörü",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        };
        menu.Items.Add(startupItem);

        var notificationItem = new Forms.ToolStripMenuItem("Düşük kota uyarıları")
        {
            CheckOnClick = true,
            Checked = _notificationPolicy.Enabled
        };
        var changingNotificationItem = false;
        notificationItem.CheckedChanged += async (_, _) =>
        {
            if (changingNotificationItem) return;
            var requestedValue = notificationItem.Checked;
            try
            {
                await _notificationPolicy.SetEnabledAsync(requestedValue);
            }
            catch (Exception)
            {
                changingNotificationItem = true;
                notificationItem.Checked = !requestedValue;
                changingNotificationItem = false;
                MessageBox.Show("Kota uyarısı tercihi kaydedilemedi.", "Kullanım Monitörü",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        };
        menu.Items.Add(notificationItem);

        menu.Items.Add(new Forms.ToolStripSeparator());
        _appBarMenuItem = new Forms.ToolStripMenuItem("Kompakt alt şerit · ekran alanı ayırır") { CheckOnClick = true };
        _appBarMenuItem.CheckedChanged += (_, _) => ToggleAppBar(_appBarMenuItem.Checked);
        menu.Items.Add(_appBarMenuItem);

        menu.Items.Add(new Forms.ToolStripSeparator());
        var exit = new Forms.ToolStripMenuItem("Çıkış");
        exit.Click += (_, _) => ShutdownApplication();
        menu.Items.Add(exit);

        _trayBitmapIcon = CreateTrayIcon();
        _trayIcon = new Forms.NotifyIcon
        {
            Icon = _trayBitmapIcon ?? SystemIcons.Application,
            Text = "Codex · Gemini · Claude Kullanım Monitörü",
            ContextMenuStrip = menu,
            Visible = true
        };
        _trayIcon.MouseClick += (_, args) =>
        {
            if (args.Button == Forms.MouseButtons.Left)
                ShowPopup();
        };
    }

    private static Icon? CreateTrayIcon()
    {
        var imagePath = Path.Combine(AppContext.BaseDirectory, "Assets", "openai-mark-codex.jpg");
        if (!File.Exists(imagePath)) return null;

        IntPtr nativeHandle = IntPtr.Zero;
        try
        {
            using var source = new Bitmap(imagePath);
            using var bitmap = new Bitmap(32, 32);
            using (var graphics = Graphics.FromImage(bitmap))
            {
                graphics.Clear(Color.White);
                graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                graphics.DrawImage(source, new Rectangle(1, 1, 30, 30));
            }
            nativeHandle = bitmap.GetHicon();
            using var nativeIcon = Icon.FromHandle(nativeHandle);
            using var clone = (Icon)nativeIcon.Clone();
            using var stream = new MemoryStream();
            clone.Save(stream);
            stream.Position = 0;
            using var detached = new Icon(stream);
            return (Icon)detached.Clone();
        }
        catch
        {
            return null;
        }
        finally
        {
            if (nativeHandle != IntPtr.Zero)
                DestroyIcon(nativeHandle);
        }
    }

    private void ShowPopup()
    {
        if (_popup is null) return;
        var cursor = Forms.Cursor.Position;
        var screen = Forms.Screen.FromPoint(cursor);
        _popup.ShowNear(screen.WorkingArea);
    }

    private async Task RefreshFromTrayAsync()
    {
        try
        {
            if (_syncService is not null) await _syncService.SyncNowAsync();
            if (_tokenHistoryService is not null) await _tokenHistoryService.SyncNowAsync();
        }
        catch (Exception) { _popup?.SetLoadingMessage("Yenileme tamamlanamadı. Son başarılı veri korunuyor."); }
    }

    private void ToggleAppBar(bool enabled)
    {
        if (_appBar is null) return;
        if (enabled) _appBar.Enable();
        else _appBar.Disable();
    }

    private void OnSnapshotUpdated(UsageSnapshot snapshot)
    {
        Dispatcher.InvokeAsync(() =>
        {
            _popup?.SetSnapshot(snapshot, isCached: false);
            _appBar?.SetSnapshot(snapshot);
        });
        _ = ShowLowQuotaNotificationsAsync(snapshot);
    }

    private async Task ShowLowQuotaNotificationsAsync(UsageSnapshot snapshot)
    {
        try
        {
            var notices = await _notificationPolicy.GetNewNoticesAsync(snapshot);
            if (notices.Count == 0) return;
            var lines = notices.Take(3)
                .Select(notice => $"{notice.Provider} · {notice.Window}: %{notice.RemainingPercent} kaldı (eşik %{notice.ThresholdPercent})")
                .ToList();
            if (notices.Count > lines.Count) lines.Add($"+{notices.Count - lines.Count} pencere daha");
            var message = string.Join(Environment.NewLine, lines);
            if (message.Length > 240) message = message[..240];
            await Dispatcher.InvokeAsync(() =>
                _trayIcon?.ShowBalloonTip(7000, "Düşük kota", message, Forms.ToolTipIcon.Warning));
        }
        catch (Exception) { /* A notification failure must not affect quota polling. */ }
    }

    private void OnTokenHistoryUpdated(TokenHistorySnapshot snapshot)
    {
        Dispatcher.InvokeAsync(() => _popup?.SetTokenHistorySnapshot(snapshot));
    }

    private async void ShutdownApplication()
    {
        if (_isShuttingDown) return;
        _isShuttingDown = true;
        if (_popup is not null) _popup.AllowClose = true;
        _appBar?.Disable();
        _trayIcon?.Dispose();
        _trayIcon = null;
        _trayBitmapIcon?.Dispose();
        _trayBitmapIcon = null;
        if (_syncService is not null)
        {
            _syncService.SnapshotUpdated -= OnSnapshotUpdated;
            try { await _syncService.DisposeAsync(); }
            catch (Exception) { }
            _syncService = null;
        }
        if (_tokenHistoryService is not null)
        {
            _tokenHistoryService.SnapshotUpdated -= OnTokenHistoryUpdated;
            try { await _tokenHistoryService.DisposeAsync(); }
            catch (Exception) { }
            _tokenHistoryService = null;
        }
        DisposeHttpClients();
        ReleaseSingleInstanceMutex();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_syncService is not null)
        {
            _syncService.SnapshotUpdated -= OnSnapshotUpdated;
            _ = _syncService.DisposeAsync();
            _syncService = null;
        }
        if (_tokenHistoryService is not null)
        {
            _tokenHistoryService.SnapshotUpdated -= OnTokenHistoryUpdated;
            _ = _tokenHistoryService.DisposeAsync();
            _tokenHistoryService = null;
        }
        _appBar?.Disable();
        _trayIcon?.Dispose();
        _trayIcon = null;
        _trayBitmapIcon?.Dispose();
        _trayBitmapIcon = null;
        DisposeHttpClients();
        ReleaseSingleInstanceMutex();
        if (_popup is not null)
        {
            _popup.AllowClose = true;
            _popup.Close();
        }
        _appBar?.Dispose();
        base.OnExit(e);
    }

    private void DisposeHttpClients()
    {
        foreach (var client in _httpClients)
            client.Dispose();
        _httpClients.Clear();
    }

    private void ReleaseSingleInstanceMutex()
    {
        if (_singleInstanceMutex is null) return;
        if (_ownsSingleInstanceMutex)
        {
            try { _singleInstanceMutex.ReleaseMutex(); }
            catch (ApplicationException) { }
        }
        _singleInstanceMutex.Dispose();
        _singleInstanceMutex = null;
        _ownsSingleInstanceMutex = false;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr handle);
}
