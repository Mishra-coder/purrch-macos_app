using System.Windows;
using System.Windows.Media;

namespace Purrch.DesignSystem;

/// The pill silhouette: a flat top that flares outward into the screen edge, rounded bottom corners.
/// Same geometry as the Mac app's NotchShape.
public sealed class NotchSurface : FrameworkElement
{
    public static readonly DependencyProperty TopRadiusProperty = DependencyProperty.Register(
        nameof(TopRadius), typeof(double), typeof(NotchSurface),
        new FrameworkPropertyMetadata(10.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty BottomRadiusProperty = DependencyProperty.Register(
        nameof(BottomRadius), typeof(double), typeof(NotchSurface),
        new FrameworkPropertyMetadata(10.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FillProperty = DependencyProperty.Register(
        nameof(Fill), typeof(Brush), typeof(NotchSurface),
        new FrameworkPropertyMetadata(Brushes.Black, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SheenProperty = DependencyProperty.Register(
        nameof(Sheen), typeof(Brush), typeof(NotchSurface),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(
        nameof(Stroke), typeof(Brush), typeof(NotchSurface),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public double TopRadius { get => (double)GetValue(TopRadiusProperty); set => SetValue(TopRadiusProperty, value); }
    public double BottomRadius { get => (double)GetValue(BottomRadiusProperty); set => SetValue(BottomRadiusProperty, value); }
    public Brush Fill { get => (Brush)GetValue(FillProperty); set => SetValue(FillProperty, value); }
    /// Soft light painted over the fill (the lamp-in-the-notch glow when open).
    public Brush? Sheen { get => (Brush?)GetValue(SheenProperty); set => SetValue(SheenProperty, value); }
    public Brush? Stroke { get => (Brush?)GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }

    protected override void OnRender(DrawingContext dc)
    {
        var geometry = Build(RenderSize, TopRadius, BottomRadius);
        dc.DrawGeometry(Fill, null, geometry);
        if (Sheen != null) dc.DrawGeometry(Sheen, null, geometry);
        if (Stroke != null) dc.DrawGeometry(null, new Pen(Stroke, 1), geometry);
    }

    public static Geometry Build(Size size, double topRadius, double bottomRadius)
    {
        double w = size.Width, h = size.Height;
        double t = Math.Min(topRadius, w / 2);
        double b = Math.Max(0, Math.Min(bottomRadius, Math.Min(h / 2, (w - 2 * t) / 2)));
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            c.BeginFigure(new Point(0, 0), isFilled: true, isClosed: true);
            c.QuadraticBezierTo(new Point(t, 0), new Point(t, t), true, true);
            c.LineTo(new Point(t, h - b), true, true);
            c.QuadraticBezierTo(new Point(t, h), new Point(t + b, h), true, true);
            c.LineTo(new Point(w - t - b, h), true, true);
            c.QuadraticBezierTo(new Point(w - t, h), new Point(w - t, h - b), true, true);
            c.LineTo(new Point(w - t, t), true, true);
            c.QuadraticBezierTo(new Point(w - t, 0), new Point(w, 0), true, true);
        }
        g.Freeze();
        return g;
    }
}
