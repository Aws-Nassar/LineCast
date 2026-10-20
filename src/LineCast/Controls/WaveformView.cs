using System.Windows;
using System.Windows.Media;

namespace LineCast.Controls;

/// <summary>
/// Draws precomputed peak buckets as a compact waveform. Kept dependency-free so
/// it can live inside a virtualized DataGrid cell without per-row adorners.
/// </summary>
public sealed class WaveformView : FrameworkElement
{
    // Registered as object on purpose: the XAML compiler refuses array-typed
    // dependency properties inside data templates ("PropertyArrayStart"), even
    // though a float[] binding is perfectly valid at runtime.
    public static readonly DependencyProperty PeaksProperty = DependencyProperty.Register(
        nameof(Peaks),
        typeof(object),
        typeof(WaveformView),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty BarBrushProperty = DependencyProperty.Register(
        nameof(BarBrush),
        typeof(Brush),
        typeof(WaveformView),
        new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly Pen PlaceholderPen = CreatePen();

    public object? Peaks
    {
        get => GetValue(PeaksProperty);
        set => SetValue(PeaksProperty, value);
    }

    public Brush BarBrush
    {
        get => (Brush)GetValue(BarBrushProperty);
        set => SetValue(BarBrushProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var width = ActualWidth;
        var height = ActualHeight;

        if (width <= 0 || height <= 0)
        {
            return;
        }

        var brush = BarBrush ?? Brushes.Gray;
        var buckets = Peaks as float[];
        if (buckets is null || buckets.Length == 0)
        {
            // Placeholder until the clip has been decoded: a quiet centre line.
            var y = Math.Round(height / 2) + 0.5;
            dc.DrawLine(PlaceholderPen, new Point(0, y), new Point(width, y));
            return;
        }

        // Aggregate the precomputed buckets down to the pixel budget so a narrow
        // row does not draw 128 overlapping bars as one solid block.
        var perBar = Math.Max(1, (int)Math.Ceiling(buckets.Length / Math.Max(4.0, width / 2.6)));
        var bars = (int)Math.Ceiling(buckets.Length / (double)perBar);
        var slot = width / bars;
        var barWidth = Math.Max(1.0, slot * 0.6);
        var radius = Math.Min(barWidth / 2, 1.5);
        var mid = height / 2;
        var maxBar = height - 2;

        for (var i = 0; i < bars; i++)
        {
            var start = i * perBar;
            var end = Math.Min(buckets.Length, start + perBar);
            var level = 0f;

            for (var b = start; b < end; b++)
            {
                if (buckets[b] > level)
                {
                    level = buckets[b];
                }
            }

            level = Math.Clamp(level, 0.04f, 1f);
            var barHeight = Math.Max(1.5, level * maxBar);
            var x = i * slot + (slot - barWidth) / 2;
            dc.DrawRoundedRectangle(
                brush,
                null,
                new Rect(x, mid - barHeight / 2, barWidth, barHeight),
                radius,
                radius);
        }
    }

    private static Pen CreatePen()
    {
        var pen = new Pen(new SolidColorBrush(Color.FromArgb(0x55, 0x9A, 0xA7, 0xB8)), 1);
        pen.Freeze();
        return pen;
    }
}
