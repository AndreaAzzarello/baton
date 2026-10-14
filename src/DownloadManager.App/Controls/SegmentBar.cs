using System.Windows;
using System.Windows.Media;
using DownloadManager.Core;

namespace DownloadManager.App.Controls;

/// <summary>
/// The file drawn as a bar: downloaded bytes filled, every range boundary marked, and the point where each
/// active connection is writing shown as a taller marker, so parallel connections are visible at a glance.
/// </summary>
public sealed class SegmentBar : FrameworkElement
{
    public static readonly DependencyProperty SegmentsProperty = DependencyProperty.Register(
        nameof(Segments), typeof(IReadOnlyList<SegmentView>), typeof(SegmentBar),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TotalSizeProperty = DependencyProperty.Register(
        nameof(TotalSize), typeof(long), typeof(SegmentBar), new FrameworkPropertyMetadata(0L, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TrackBrushProperty = Brush(nameof(TrackBrush));
    public static readonly DependencyProperty DoneBrushProperty = Brush(nameof(DoneBrush));
    public static readonly DependencyProperty ActiveBrushProperty = Brush(nameof(ActiveBrush));
    public static readonly DependencyProperty BoundaryBrushProperty = Brush(nameof(BoundaryBrush));

    private static DependencyProperty Brush(string name) =>
        DependencyProperty.Register(name, typeof(Brush), typeof(SegmentBar), new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<SegmentView>? Segments
    {
        get => (IReadOnlyList<SegmentView>?)GetValue(SegmentsProperty);
        set => SetValue(SegmentsProperty, value);
    }

    public long TotalSize
    {
        get => (long)GetValue(TotalSizeProperty);
        set => SetValue(TotalSizeProperty, value);
    }

    public Brush TrackBrush { get => (Brush)GetValue(TrackBrushProperty); set => SetValue(TrackBrushProperty, value); }
    public Brush DoneBrush { get => (Brush)GetValue(DoneBrushProperty); set => SetValue(DoneBrushProperty, value); }
    public Brush ActiveBrush { get => (Brush)GetValue(ActiveBrushProperty); set => SetValue(ActiveBrushProperty, value); }
    public Brush BoundaryBrush { get => (Brush)GetValue(BoundaryBrushProperty); set => SetValue(BoundaryBrushProperty, value); }

    protected override Size MeasureOverride(Size availableSize) =>
        new(double.IsInfinity(availableSize.Width) ? 200 : availableSize.Width, 28);

    protected override void OnRender(DrawingContext dc)
    {
        const double markerWidth = 3;
        var width = ActualWidth;
        var height = ActualHeight;
        var barTop = 6.0;
        var barHeight = height - 2 * barTop;
        dc.DrawRoundedRectangle(TrackBrush, null, new Rect(0, barTop, width, barHeight), 3, 3);

        var segments = Segments;
        var total = TotalSize;
        if (segments is null || segments.Count == 0 || total <= 0)
            return;

        double X(long offset) => Math.Clamp(offset * width / total, 0, width);
        var boundary = new Pen(BoundaryBrush, 1);
        boundary.Freeze();

        foreach (var s in segments)
        {
            var start = X(s.Start);
            var written = X(s.Start + s.Written);
            if (written > start)
                dc.DrawRectangle(DoneBrush, null, new Rect(start, barTop, written - start, barHeight));
            if (s.Start > 0)
                dc.DrawLine(boundary, new Point(Math.Round(start) + 0.5, barTop), new Point(Math.Round(start) + 0.5, barTop + barHeight));
        }

        // Active connections on top, taller than the bar so they stand out without relying on colour alone.
        foreach (var s in segments.Where(s => s.Active))
        {
            var at = X(s.Start + s.Written);
            dc.DrawRectangle(ActiveBrush, null, new Rect(Math.Max(0, Math.Min(width - markerWidth, at - markerWidth / 2)), 0, markerWidth, height));
        }
    }
}
