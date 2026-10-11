using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Xml.Linq;
using CodexUsageMonitor;
using CodexUsageMonitor.Windows;

internal static class ProductScreenshots
{
    [STAThread] private static void Main(string[] args)
    {
        // Only synthetic snapshots are rendered. No StartAsync, credentials, log scans or network calls.
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        XNamespace ns = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var xml = XDocument.Load("windows-app/UsageMonitor.Windows/App.xaml");
        app.Resources = (ResourceDictionary)XamlReader.Parse(new XElement(ns + "ResourceDictionary", xml.Root!.Element(ns + "Application.Resources")!.Elements()).ToString());
        var scratch = Path.Combine(Path.GetTempPath(),"usage-product-demo-" + Guid.NewGuid());
        var sync = new UsageSyncService(Array.Empty<IQuotaConnector>(),new QuotaSnapshotStore(scratch));
        var now = DateTimeOffset.UtcNow;
        var ids = new[] {"chatgpt-codex","gemini","claude"};
        var names = new[] {"Codex","Gemini","Claude"};
        var remaining = new[] {68d,92d,81d};
        var snapshot = new UsageSnapshot(now,ids.Select((id,i) => new ProviderSnapshot(id,names[i],"ok",null,now.AddMinutes(-1),"demo",
            new[] { new UsageWindow("five_hour","5 saat",100-remaining[i],remaining[i],now.AddHours(3)),
                    new UsageWindow("weekly","Haftalık",10+i*9,90-i*9,now.AddDays(4)) })).ToArray());
        using var strip = new TaskbarUsageWindow { Left = -12000,Top = -12000,Width = 350 };
        strip.SetSnapshot(snapshot); strip.Show(); strip.UpdateLayout();
        Save(strip,"taskbar.png");
        strip.SetAttached(true); Save(strip,"taskbar-active.png"); strip.SetAttached(false);
        var warningSnapshot = snapshot with { Providers = snapshot.Providers.Select((provider,index) => provider with
            { State = index == 0 ? "stale" : index == 1 ? "partial" : "reauth_required", ObservedAt = now.AddMinutes(-12) }).ToArray() };
        strip.SetSnapshot(warningSnapshot); Save(strip,"taskbar-warning.png");
        var density = typeof(TaskbarUsageWindow).GetField("_density",BindingFlags.Instance|BindingFlags.NonPublic)!;
        density.SetValue(strip,1); strip.Width = 104; strip.SetSnapshot(warningSnapshot); Save(strip,"taskbar-compact.png");
        density.SetValue(strip,2); strip.Width = 34; strip.SetSnapshot(warningSnapshot); Save(strip,"taskbar-minimal.png");
        density.SetValue(strip,0); strip.Width = 350;
        // A second render covers the widest real value without changing any user account.
        strip.SetSnapshot(snapshot with { Providers = snapshot.Providers.Select(p => p with { Windows = p.Windows.Select(w => w with {UsedPercent=0,RemainingPercent=100}).ToArray() }).ToArray() });
        strip.UpdateLayout();
        var groups = (StackPanel)((Border)((Grid)strip.Content).Children[0]).Child;
        foreach (var group in groups.Children.OfType<Border>())
            if ((group.Child is Button button ? (FrameworkElement)button.Content : group.Child).RenderSize.Width > group.ActualWidth - 10)
                throw new Exception("100% value overflows its indicator group.");
        var panel = new MainWindow(sync,new TokenHistoryService(scratch), previewOnly: true);
        panel.SetSnapshot(snapshot,false);
        panel.ShowAttached(new System.Drawing.Rectangle(-10000,-9000,350,34));
        Save(panel,"quota.png");
        var quotaHistory = ids.SelectMany((id,p) => Enumerable.Range(0,12).Select(h =>
            new QuotaHistoryEntry(now.AddHours(h-11),id,new[] { new UsageWindow("five_hour","5 saat",h*3+p*8,100-h*3-p*8,null) }))).ToArray();
        typeof(MainWindow).GetField("_quotaHistory",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(panel,quotaHistory);
        typeof(MainWindow).GetField("_tokenPeriodDays",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(panel,7);
        var rows = ids.SelectMany((id,p) => Enumerable.Range(0,7).Select(d =>
            new TokenUsageRow(id,DateOnly.FromDateTime(DateTime.Today.AddDays(d-6)),names[p]+" demo model",(d+1)*12000,(d+1)*2400,0,0,0,0,(d+1)*14400))).ToArray();
        ((ScrollViewer)panel.FindName("QuotaPanel")).Visibility=Visibility.Collapsed;
        ((ScrollViewer)panel.FindName("TokenPanel")).Visibility=Visibility.Visible;
        ((Grid)panel.FindName("TokenHost")).Visibility=Visibility.Visible;
        panel.SetTokenHistorySnapshot(new TokenHistorySnapshot(now,new TokenHistorySettings(false,false),rows));
        ((Button)panel.FindName("QuotaTabButton")).Background = Brushes.Transparent;
        ((Button)panel.FindName("TokenTabButton")).Background = new SolidColorBrush(Color.FromRgb(52,74,101));
        Save(panel,"history.png");
        var historyPanel = (ScrollViewer)panel.FindName("TokenPanel");
        historyPanel.ScrollToBottom(); panel.UpdateLayout();
        Save(panel,"token-records.png");
        var content = (StackPanel)panel.FindName("TokenHistoryContent");
        var settings = content.Children.OfType<Expander>().Single(control => Equals(control.Tag,"TokenHistoryControls"));
        settings.IsExpanded = true; panel.UpdateLayout();
        historyPanel.ScrollToBottom(); panel.UpdateLayout();
        Save(panel,"token-record-settings.png");
        if (args.Contains("--interactive"))
        {
            strip.SetSnapshot(snapshot);
            strip.ShowInTaskbar = true;
            strip.Title = "AI Usage Bar — Çubuk önizlemesi";
            strip.ShowActivated = true;
            var area = System.Windows.Forms.Screen.FromPoint(System.Windows.Forms.Control.MousePosition).WorkingArea;
            var scale = PresentationSource.FromVisual(strip)!.CompositionTarget!.TransformToDevice.M11;
            strip.Left = area.Right / scale - 350; strip.Top = area.Bottom / scale - 34;
            var anchor = new System.Drawing.Rectangle((int)(strip.Left * scale),(int)(strip.Top * scale),(int)(350 * scale),(int)(34 * scale));
            panel.ShowInTaskbar = true;
            panel.Topmost = false;
            panel.Title = "AI Usage Bar — Tasarım önizlemesi";
            panel.AllowClose = true;
            strip.OpenRequested += () =>
            {
                if (panel.IsVisible) panel.Hide(); else panel.ShowAttached(anchor);
                strip.SetAttached(panel.IsVisible);
            };
            panel.ShowAttached(anchor); strip.SetAttached(true);
            panel.Closed += (_,_) => app.Shutdown();
            strip.Closed += (_,_) => app.Shutdown();
            app.Run();
            return;
        }
        panel.AllowClose=true;panel.Close();app.Shutdown();
        Console.WriteLine("Synthetic WPF screenshots exported; 100% values fit within fixed indicator width.");
    }

    private static void Save(Window window,string name)
    {
        window.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)window.ActualWidth,(int)window.ActualHeight,96,96,PixelFormats.Pbgra32);
        // The native region exposes the taskbar outside the capsules. Export the
        // capsule visual tree with alpha instead of the solid HWND backing surface.
        bitmap.Render(window is TaskbarUsageWindow ? (Visual)window.Content : window);
        var encoder = new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(Path.Combine("docs","images",name));encoder.Save(output);
    }
}
