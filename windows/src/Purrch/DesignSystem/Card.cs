using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Purrch.DesignSystem;

/// The basic surface every module is built from: a raised panel with a tinted icon badge,
/// a title, an optional accessory on the right, and the content below.
public sealed class Card : Border
{
    public Card(string title, UIElement icon, UIElement content, UIElement? accessory = null)
    {
        Style = (Style)Application.Current.FindResource("Purrch.Card");
        Padding = new Thickness(14);

        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 10), LastChildFill = true };
        var badge = new Border
        {
            Width = 22,
            Height = 22,
            CornerRadius = new CornerRadius(7),
            Child = icon,
        };
        badge.SetResourceReference(BackgroundProperty, ThemeResources.AccentFaint);
        DockPanel.SetDock(badge, Dock.Left);
        header.Children.Add(badge);

        if (accessory != null)
        {
            DockPanel.SetDock(accessory, Dock.Right);
            header.Children.Add(accessory);
        }

        var label = Ui.Text(title, 12.5, weight: FontWeights.SemiBold);
        label.TextWrapping = TextWrapping.NoWrap;
        label.TextTrimming = TextTrimming.CharacterEllipsis;
        label.Margin = new Thickness(8, 0, 4, 0);
        header.Children.Add(label);

        var body = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        body.Children.Add(header);
        body.Children.Add(content);
        Child = body;
    }

    public Card(string title, string glyph, UIElement content, UIElement? accessory = null)
        : this(title, Ui.Glyph(glyph, 10.5, ThemeResources.Accent), content, accessory) { }
}

/// A progress ring: a faint full circle with the accent arc drawn over it, starting at 12 o'clock.
public sealed class Ring : FrameworkElement
{
    public static readonly DependencyProperty ProgressProperty = DependencyProperty.Register(
        nameof(Progress), typeof(double), typeof(Ring),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(
        nameof(Stroke), typeof(Brush), typeof(Ring),
        new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TrackProperty = DependencyProperty.Register(
        nameof(Track), typeof(Brush), typeof(Ring),
        new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Progress { get => (double)GetValue(ProgressProperty); set => SetValue(ProgressProperty, value); }
    public Brush Stroke { get => (Brush)GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }
    public Brush Track { get => (Brush)GetValue(TrackProperty); set => SetValue(TrackProperty, value); }
    public double Thickness { get; set; } = 8;

    public Ring()
    {
        SetResourceReference(StrokeProperty, ThemeResources.Accent);
        SetResourceReference(TrackProperty, ThemeResources.Fill);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var size = Math.Min(ActualWidth, ActualHeight);
        var r = (size - Thickness) / 2;
        var c = new Point(ActualWidth / 2, ActualHeight / 2);
        dc.DrawEllipse(null, new Pen(Track, Thickness), c, r, r);

        var p = Math.Clamp(Progress, 0, 1);
        if (p <= 0) return;
        var pen = new Pen(Stroke, Thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        if (p >= 0.999)
        {
            dc.DrawEllipse(null, pen, c, r, r);
            return;
        }
        var angle = p * 2 * Math.PI;
        var figure = new PathFigure { StartPoint = new Point(c.X, c.Y - r) };
        figure.Segments.Add(new ArcSegment(new Point(c.X + r * Math.Sin(angle), c.Y - r * Math.Cos(angle)),
            new Size(r, r), 0, angle > Math.PI, SweepDirection.Clockwise, true));
        dc.DrawGeometry(null, pen, new PathGeometry(new[] { figure }));
    }
}

/// A water drop, outlined or filled. Segoe has no drop glyph, so it is drawn.
public static class DropShape
{
    private static readonly Geometry Geometry = Geometry.Parse("M 6,0 C 6,0 0,7.5 0,11 A 6,6 0 0 0 12,11 C 12,7.5 6,0 6,0 Z");

    public static Path Make(bool filled, double height = 15)
    {
        var path = new Path
        {
            Data = Geometry,
            Stretch = Stretch.Uniform,
            Height = height,
            Width = height * 12 / 17,
            StrokeThickness = 1.4,
        };
        path.SetResourceReference(Shape.StrokeProperty, filled ? ThemeResources.Accent : ThemeResources.SecondaryText);
        if (filled) path.SetResourceReference(Shape.FillProperty, ThemeResources.Accent);
        return path;
    }
}
