using System.Windows;
using System.Windows.Media;

namespace LineCast.Controls;

/// <summary>Horizontal VU segment. The gradient is clipped to the current level.</summary>
public sealed class LevelMeter : FrameworkElement
{
    public static readonly DependencyProperty LevelProperty = DependencyProperty.Register(
        nameof(Level),
        typeof(double),
        typeof(LevelMeter),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TrackBrushProperty = DependencyProperty.Register(
        nameof(TrackBrush),
        typeof(Brush),
        typeof(LevelMeter),
        new FrameworkPropertyMetadata(Brushes.Transparent, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FillBrushProperty = DependencyProperty.Register(
        nameof(FillBrush),
        typeof(Brush),
        typeof(LevelMeter),
        new FrameworkPropertyMetadata(Brushes.LimeGreen, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Level
    {
        get => (double)GetValue(LevelProperty);
        set => SetValue(LevelProperty, value);
    }

    public Brush TrackBrush
    {
        get => (Brush)GetValue(TrackBrushProperty);
        set => SetValue(TrackBrushProperty, value);
    }

    public Brush FillBrush
    {
        get => (Brush)GetValue(FillBrushProperty);
        set => SetValue(FillBrushProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var width = ActualWidth;
        var height = ActualHeight;

        if (width <= 0 || height <= 0)
        {
            return;
        }

        var radius = height / 2;
        dc.DrawRoundedRectangle(TrackBrush, null, new Rect(0, 0, width, height), radius, radius);

        var level = Math.Clamp(Level, 0, 1);
        if (level <= 0.001)
        {
            return;
        }

        var fillWidth = Math.Max(2, level * width);
        dc.PushClip(new RectangleGeometry(new Rect(0, 0, fillWidth, height), radius, radius));
        dc.DrawRoundedRectangle(FillBrush, null, new Rect(0, 0, width, height), radius, radius);
        dc.Pop();
    }
}
