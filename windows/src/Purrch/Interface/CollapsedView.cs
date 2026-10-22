using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using Purrch.Core;
using Purrch.DesignSystem;

namespace Purrch.Interface;

/// The closed pill: a live activity split across its two ends (icon on the left, status on the right).
public sealed class CollapsedView : Grid
{
    private readonly ContentControl _leading = new() { HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };
    private readonly ContentControl _trailing = new() { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
    private LiveActivity _shown = LiveActivity.None;

    public CollapsedView()
    {
        _leading.Margin = new Thickness(12, 0, 0, 0);
        _trailing.Margin = new Thickness(0, 0, 12, 0);
        Children.Add(_leading);
        Children.Add(_trailing);
    }

    public void Show(LiveActivity activity)
    {
        var kindChanged = activity.Kind != _shown.Kind;
        _shown = activity;
        _leading.Content = Leading(activity);
        _trailing.Content = Trailing(activity);
        // Icons pop in when the kind of activity changes, not on every tick.
        if (kindChanged && activity.Kind != LiveActivityKind.None)
        {
            Pop(_leading);
            Pop(_trailing);
        }
    }

    private static void Pop(FrameworkElement element)
    {
        var scale = new ScaleTransform(0.3, 0.3);
        element.RenderTransformOrigin = new Point(0.5, 0.5);
        element.RenderTransform = scale;
        element.Opacity = 0;
        scale.Animate(ScaleTransform.ScaleXProperty, 1, Motion.Pop);
        scale.Animate(ScaleTransform.ScaleYProperty, 1, Motion.Pop);
        element.Animate(OpacityProperty, 1, Motion.ExpoOut(0.25));
    }

    private static UIElement? Leading(LiveActivity a) => a.Kind switch
    {
        LiveActivityKind.Timer => Glyph(a.Glyph.Length > 0 ? a.Glyph : "", 13),
        LiveActivityKind.Download => Glyph("", 14),
        LiveActivityKind.Music => Glyph("", 13),
        _ => null,
    };

    private static UIElement? Trailing(LiveActivity a) => a.Kind switch
    {
        LiveActivityKind.Timer => new TextBlock
        {
            Text = a.Label,
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        }.WithResource(TextBlock.ForegroundProperty, ThemeResources.Text).Tabular(), // digits don't jiggle as seconds tick
        LiveActivityKind.Download => new ProgressRing(a.Progress) { Width = 16, Height = 16 },
        LiveActivityKind.Music => new MusicBars { Width = 18, Height = 14 },
        _ => null,
    };

    private static TextBlock Glyph(string glyph, double size) =>
        new TextBlock { Text = glyph, FontSize = size, Style = (Style)Application.Current.FindResource("Purrch.Glyph") }
            .WithResource(TextBlock.ForegroundProperty, ThemeResources.Accent);
}

internal static class ElementExtensions
{
    public static T WithResource<T>(this T element, DependencyProperty property, string key) where T : FrameworkElement
    {
        element.SetResourceReference(property, key);
        return element;
    }

    public static TextBlock Tabular(this TextBlock text)
    {
        System.Windows.Documents.Typography.SetNumeralAlignment(text, FontNumeralAlignment.Tabular);
        return text;
    }
}

/// Circular progress, drawn as an arc over a faint track.
public sealed class ProgressRing : Grid
{
    public ProgressRing(double progress)
    {
        var track = new Ellipse { StrokeThickness = 2.5, Opacity = 0.2 };
        track.SetResourceReference(Shape.StrokeProperty, ThemeResources.Text);
        Children.Add(track);

        var arc = new Path { StrokeThickness = 2.5, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
        arc.SetResourceReference(Shape.StrokeProperty, ThemeResources.Accent);
        Children.Add(arc);
        SizeChanged += (_, _) => arc.Data = Arc(ActualWidth, Math.Clamp(progress, 0, 1));
    }

    private static Geometry Arc(double size, double progress)
    {
        var r = (size - 2.5) / 2;
        var c = size / 2;
        if (progress >= 0.999) return new EllipseGeometry(new Point(c, c), r, r);
        var angle = progress * 2 * Math.PI;
        var start = new Point(c, c - r);
        var end = new Point(c + r * Math.Sin(angle), c - r * Math.Cos(angle));
        var figure = new PathFigure { StartPoint = start, IsClosed = false };
        figure.Segments.Add(new ArcSegment(end, new Size(r, r), 0, angle > Math.PI, SweepDirection.Clockwise, true));
        return new PathGeometry(new[] { figure });
    }
}

/// Animated equaliser bars shown while music plays.
public sealed class MusicBars : StackPanel
{
    public MusicBars()
    {
        Orientation = Orientation.Horizontal;
        for (var i = 0; i < 4; i++)
        {
            var scale = new ScaleTransform(1, 0.3);
            var bar = new Rectangle
            {
                Width = 2.5,
                RadiusX = 1.25,
                RadiusY = 1.25,
                Margin = new Thickness(i == 0 ? 0 : 2, 0, 0, 0),
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = scale,
            };
            bar.SetResourceReference(Shape.FillProperty, ThemeResources.Accent);
            Children.Add(bar);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.3, 1, TimeSpan.FromSeconds(0.45 + i * 0.13))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
                BeginTime = TimeSpan.FromSeconds(i * 0.08),
            });
        }
    }
}
