using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;

namespace CodexUsageMonitor.Windows;

/// <summary>Pixel scrolling with a short wheel transition; no idle timer.</summary>
internal sealed class SmoothScrolling : Animatable
{
    private readonly ScrollViewer _viewer;
    private double _target;
    private bool _animating;
    private static readonly DependencyProperty OffsetProperty = DependencyProperty.Register(
        "Offset", typeof(double), typeof(SmoothScrolling), new PropertyMetadata(0d,
            (sender, args) => ((SmoothScrolling)sender)._viewer.ScrollToVerticalOffset((double)args.NewValue)));

    private SmoothScrolling(ScrollViewer viewer)
    {
        _viewer = viewer;
        viewer.PreviewMouseWheel += OnWheel;
        // Thumb dragging and track clicks must immediately follow the pointer.
        viewer.PreviewMouseLeftButtonDown += (_, _) => Stop();
        viewer.IsVisibleChanged += (_, _) => { if (!viewer.IsVisible) Stop(); };
    }

    protected override Freezable CreateInstanceCore() => new SmoothScrolling(_viewer);

    internal static void Attach(ScrollViewer viewer) => _ = new SmoothScrolling(viewer);

    private void OnWheel(object sender, MouseWheelEventArgs args)
    {
        if (_viewer.ScrollableHeight <= 0) return;
        _target = Math.Clamp((_animating ? _target : _viewer.VerticalOffset) - args.Delta / 120d * 48,
            0, _viewer.ScrollableHeight);
        var from = _viewer.VerticalOffset;
        BeginAnimation(OffsetProperty, null);
        SetValue(OffsetProperty, from);
        _animating = true;
        var animation = new DoubleAnimation(from, _target, TimeSpan.FromMilliseconds(140))
        { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        animation.Completed += (_, _) => { if (_animating) { Stop(); _viewer.ScrollToVerticalOffset(_target); } };
        BeginAnimation(OffsetProperty, animation, HandoffBehavior.SnapshotAndReplace);
        args.Handled = true;
    }

    private void Stop()
    {
        _animating = false;
        var current = _viewer.VerticalOffset;
        BeginAnimation(OffsetProperty, null);
        SetValue(OffsetProperty, current);
    }
}
