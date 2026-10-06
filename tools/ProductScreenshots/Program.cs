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
    [STAThread] private static void Main()
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
        // A second render covers the widest real value without changing any user account.
        strip.SetSnapshot(snapshot with { Providers = snapshot.Providers.Select(p => p with { Windows = p.Windows.Select(w => w with {UsedPercent=0,RemainingPercent=100}).ToArray() }).ToArray() });
        strip.UpdateLayout();
        var groups = (StackPanel)((Border)((Grid)strip.Content).Children[0]).Child;
        foreach (var group in groups.Children.OfType<Border>())
            if (((StackPanel)group.Child).ActualWidth > group.ActualWidth - 10)
                throw new Exception("100% value overflows its indicator group.");
        var panel = new MainWindow(sync,new TokenHistoryService(scratch));
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
        Save(panel,"history.png");
        panel.AllowClose=true;panel.Close();app.Shutdown();
        Console.WriteLine("Synthetic WPF screenshots exported; 100% values fit within fixed indicator width.");
    }

    private static void Save(Window window,string name)
    {
        window.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)window.ActualWidth,(int)window.ActualHeight,96,96,PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(Path.Combine("docs","images",name));encoder.Save(output);
    }
}
