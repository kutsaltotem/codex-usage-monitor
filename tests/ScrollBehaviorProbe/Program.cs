using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using System.Windows.Markup;
using System.Xml.Linq;
using CodexUsageMonitor;
using CodexUsageMonitor.Windows;
internal static class Probe
{
    [STAThread] private static void Main()
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        XNamespace ns = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var xml = XDocument.Load("windows-app/UsageMonitor.Windows/App.xaml");
        var dictionary = new XElement(ns + "ResourceDictionary", xml.Root!.Element(ns + "Application.Resources")!.Elements());
        app.Resources = (ResourceDictionary)XamlReader.Parse(dictionary.ToString());
        var sync = new UsageSyncService(Array.Empty<IQuotaConnector>(), new QuotaSnapshotStore());
        var testRoot = Path.Combine(Path.GetTempPath(), "usage-ui-test-" + Guid.NewGuid());
        var tokenService = new TokenHistoryService(testRoot);
        var panel = new MainWindow(sync, tokenService);
        var viewer = (ScrollViewer)panel.FindName("QuotaPanel");
        var content = (StackPanel)panel.FindName("ProviderList");
        content.Children.Clear();
        for (var i = 0; i < 20; i++) content.Children.Add(new Border { Height = 41 });
        var quotaGrid = (Grid)viewer.Parent;
        quotaGrid.Children.Remove(viewer);
        viewer.Visibility = Visibility.Visible;
        var host = new Window { Content = viewer, Width = 300, Height = 180, Left = -10000, Top = -10000, ShowInTaskbar = false, ShowActivated = false };
        host.Show();
        viewer.ApplyTemplate();
        viewer.Measure(new Size(300,180)); viewer.Arrange(new Rect(0,0,300,180)); viewer.UpdateLayout();
        Console.WriteLine($"extent={viewer.ExtentHeight}, viewport={viewer.ViewportHeight}, content={content.Children.Count}");
        viewer.ScrollToVerticalOffset(17);
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);
        viewer.UpdateLayout();
        if (viewer.CanContentScroll || viewer.IsDeferredScrollingEnabled || Math.Abs(viewer.VerticalOffset - 17) > 0.1)
            throw new Exception($"Pixel scrolling failed: offset={viewer.VerticalOffset}, logical={viewer.CanContentScroll}");
        Console.WriteLine($"Pixel scrolling passed: offset={viewer.VerticalOffset}; card height41, no whole-card jump.");
        host.Content = null; quotaGrid.Children.Add(viewer);
        if (MainWindow.FormatAge(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow) != "1 dakika önce")
            throw new Exception("Relative age failed.");
        using (var strip = new TaskbarUsageWindow())
        {
            strip.Left = -12000; strip.Top = -12000; strip.Width = 350;
            strip.SetSnapshot(new UsageSnapshot(DateTimeOffset.UtcNow, new[] { "chatgpt-codex", "gemini", "claude" }
                .Select(id => new ProviderSnapshot(id, id, "ok", null, DateTimeOffset.UtcNow, "fixture", Array.Empty<UsageWindow>())).ToArray()));
            strip.Show(); strip.UpdateLayout();
            var outer = (Grid)strip.Content; var capsule = (Border)outer.Children[0];
            var groups = (StackPanel)capsule.Child;
            var first = (Border)groups.Children[0]; var last = (Border)groups.Children[2];
            var start = first.TranslatePoint(new Point(), strip); var end = last.TranslatePoint(new Point(last.ActualWidth,0), strip);
            if (Math.Abs(start.X) > .1 || Math.Abs(start.Y) > .1 || Math.Abs(end.X - 350) > .1)
                throw new Exception($"Capsule/popup edges differ: {start}/{end}");
            Console.WriteLine("Capsule geometry: first edge0, top0, final edge350; no inset gap.");
            foreach (var group in groups.Children.OfType<Border>())
            {
                var row = (StackPanel)group.Child;
                var origin = row.TranslatePoint(new Point(),group);
                if (Math.Abs(origin.X + row.ActualWidth / 2 - group.ActualWidth / 2) > .6 ||
                    Math.Abs(origin.Y + row.ActualHeight / 2 - group.ActualHeight / 2) > .6)
                    throw new Exception("Indicator contents are not centered within their capsule.");
            }
            Console.WriteLine("All three indicator contents centered horizontally and vertically.");
        }
        var tokenPanel = (ScrollViewer)panel.FindName("TokenPanel");
        ((Grid)panel.FindName("TokenHost")).Visibility = Visibility.Visible;
        tokenPanel.Visibility = Visibility.Visible; viewer.Visibility = Visibility.Collapsed;
        panel.SetTokenHistorySnapshot(new TokenHistorySnapshot(DateTimeOffset.UtcNow,
            new TokenHistorySettings(false, true), new[] { new TokenUsageRow("gemini", DateOnly.FromDateTime(DateTime.Now), "test", 10,20,0,0,0,0,30) }));
        panel.ShowAttached(new System.Drawing.Rectangle(-10000, -9000, 350, 34));
        var scale = PresentationSource.FromVisual(panel)!.CompositionTarget!.TransformToDevice.M11;
        if (Math.Abs(panel.ActualHeight - 560) > .1 || Math.Abs((panel.Top + panel.ActualHeight) * scale + 9000) > 1)
            throw new Exception("Taller popup did not retain its bottom anchor.");
        Console.WriteLine("Popup height560; bottom remains attached to indicator, grows upward.");
        var tokenContent = (StackPanel)panel.FindName("TokenHistoryContent");
        if (tokenContent.Children.Count < 4) throw new Exception("Initial token history blank.");
        panel.Hide(); panel.ShowAttached(new System.Drawing.Rectangle(-10000, -9000, 350, 34));
        if (tokenContent.Children.Count < 4) throw new Exception("Gemini token history disappeared on reopen.");
        Console.WriteLine("History reopen + relative age: passed; cached Gemini data remains.");
        Button FindFilter(string label) => Descendants(tokenContent).OfType<Button>()
            .Single(button => Equals(button.Content, label) && !Equals(button.Tag,"QuotaFilter"));
        FindFilter("Claude").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        if (!Descendants(tokenContent).OfType<TextBlock>().Any(text => text.Text.StartsWith("Bu sağlayıcı")))
            throw new Exception("Empty Claude filter did not show an explicit empty state.");
        if (!Descendants(tokenContent.Children[tokenContent.Children.Count - 1]).OfType<TextBlock>().Any(text => text.Text == "Token kayıtları"))
            throw new Exception("Token settings must always be last, including empty filters.");
        FindFilter("Tümü").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        if (!Descendants(tokenContent).OfType<TextBlock>().Any(text => text.Text == "Model dökümü"))
            throw new Exception("Switching back from empty Claude filter lost Gemini records.");
        var quota = new QuotaHistoryEntry(DateTimeOffset.UtcNow, "gemini", new[]
        {
            new UsageWindow("antigravity_five_hour", "AG Claude", 90,10,null),
            new UsageWindow("weekly", "Gemini hafta", 20,80,null),
            new UsageWindow("five_hour", "Gemini 5 saat", 0,100,null)
        });
        var measurement = MainWindow.QuotaChartMeasurement(quota);
        if (measurement?.Kind != "five_hour" || 100 - measurement.RemainingPercent != 0)
            throw new Exception("Unused Gemini must show 0% used; secondary AG Claude quota must not alter it.");
        Console.WriteLine("Claude empty filter -> Tümü preserves Gemini; settings last; unused Gemini chart 0%: passed.");
        var stamp = DateTimeOffset.UtcNow;
        var quotaRows = new[] { "chatgpt-codex", "gemini", "claude" }.SelectMany(id => new[]
        {
            new QuotaHistoryEntry(stamp.AddHours(-1),id,new[] {new UsageWindow("five_hour","5 saat",0,100,null)}),
            new QuotaHistoryEntry(stamp,id,new[] {new UsageWindow("five_hour","5 saat",40,60,null)})
        }).ToArray();
        typeof(MainWindow).GetField("_quotaHistory",System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(panel,quotaRows);
        panel.SetTokenHistorySnapshot(new TokenHistorySnapshot(stamp,new TokenHistorySettings(false,false),
            Enumerable.Range(0,25).Select(i => new TokenUsageRow("gemini",DateOnly.FromDateTime(DateTime.Now),$"model{i}",10,20,0,0,0,0,30)).ToArray()));
        panel.UpdateLayout();
        var lines = Descendants(tokenContent).OfType<System.Windows.Shapes.Polyline>().ToArray();
        if (lines.Length != 3 || lines.Any(line => line.Points.Count != 2 || line.Points[1].Y >= line.Points[0].Y))
            throw new Exception("Quota line series did not plot three rising usage traces.");
        Descendants(tokenContent).OfType<Button>().Single(button => Equals(button.Tag,"QuotaFilter") && Equals(button.Content,"Claude"))
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        panel.UpdateLayout();
        if (Descendants(tokenContent).OfType<System.Windows.Shapes.Polyline>().Count() != 1)
            throw new Exception("Individual Claude quota line filter failed.");
        var summaryHost = (StackPanel)panel.FindName("TokenSummaryHost");
        var pinnedY = summaryHost.TranslatePoint(new Point(),panel).Y;
        tokenPanel.ScrollToVerticalOffset(120); panel.UpdateLayout();
        if (summaryHost.Children.Count != 1 || Math.Abs(summaryHost.TranslatePoint(new Point(),panel).Y - pinnedY) > .1 || tokenPanel.VerticalOffset < 1)
            throw new Exception("Token summary did not stay pinned while history scrolled.");
        var dropdown = Descendants(tokenContent).OfType<Expander>().Single();
        if (dropdown.IsExpanded || dropdown.Content is not null) throw new Exception("Models must initially be collapsed and unloaded.");
        dropdown.IsExpanded = true;
        if (!Descendants(dropdown).OfType<TextBlock>().Any(text => text.Text == "model24")) throw new Exception("Expanded model list truncated after12.");
        dropdown.IsExpanded = false;
        if (dropdown.Content is not null) throw new Exception("Collapsed models retained their visual tree.");
        var unavailable = new ProviderSnapshot("claude","Claude","forbidden",null,stamp,"fixture",Array.Empty<UsageWindow>(),"forbidden","HTTP 403 test reason");
        panel.SetSnapshot(new UsageSnapshot(stamp,new[] {unavailable}),false);
        var warning = Descendants(content).OfType<Button>().Single(button => Equals(button.Tag,"ProviderWarning"));
        if (!warning.ToolTip.ToString()!.Contains("HTTP 403") || ((TextBlock)panel.FindName("SyncStatusText")).Visibility != Visibility.Collapsed)
            throw new Exception("Provider warning lost details or global warning remained visible.");
        Console.WriteLine("Quota lines/all+Claude filter, pinned totals, lazy25-model dropdown, provider warning details: passed.");
        // Exercise the opt-in checkbox and publication callback with an empty, isolated Claude home.
        var oldClaudeHome = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", Path.Combine(testRoot, "empty-claude"));
        try
        {
            panel.SetTokenHistorySnapshot(new TokenHistorySnapshot(DateTimeOffset.UtcNow, new TokenHistorySettings(false,false), Array.Empty<TokenUsageRow>()));
            var published = new TaskCompletionSource();
            tokenService.SnapshotUpdated += snapshot => { panel.SetTokenHistorySnapshot(snapshot); published.TrySetResult(); };
            var claudeChoice = Descendants(tokenContent).OfType<CheckBox>().Single(choice => Equals(choice.Content, "Claude"));
            claudeChoice.IsChecked = true;
            var frame = new DispatcherFrame();
            var timeout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
            timeout.Tick += (_,_) => frame.Continue = false;
            _ = published.Task.ContinueWith(_ => panel.Dispatcher.BeginInvoke(new Action(() => frame.Continue = false), DispatcherPriority.Background));
            timeout.Start(); Dispatcher.PushFrame(frame); timeout.Stop();
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);
            if (!published.Task.IsCompleted || !tokenService.Settings.ClaudeEnabled || tokenContent.Children.Count < 3 ||
                !Descendants(tokenContent).OfType<CheckBox>().Single(choice => Equals(choice.Content, "Claude")).IsChecked.GetValueOrDefault())
                throw new Exception("Claude opt-in publication cleared the view or lost its selection.");
            Console.WriteLine("Claude opt-in checkbox + snapshot publication: content and enabled selection preserved.");
        }
        finally
        {
            tokenService.DisposeAsync().AsTask().GetAwaiter().GetResult();
            Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", oldClaudeHome);
        }
        if (!panel.IsActive) throw new Exception("Test popup did not activate; focus test inconclusive.");
        host.Activate();
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);
        if (panel.IsVisible) throw new Exception("Popup did not close after focus moved to another window.");
        Console.WriteLine("Popup deactivation: passed; focus moved to another window and panel hid.");
        panel.AllowClose = true; panel.Close(); host.Close(); app.Shutdown();
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
            foreach (var descendant in Descendants(child)) yield return descendant;
    }
}

