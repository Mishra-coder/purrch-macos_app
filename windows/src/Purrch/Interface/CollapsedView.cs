using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using Purrch.Core;
using Purrch.DesignSystem;

namespace Purrch.Interface;

/// The closed pill: a live activity split across its two ends (icon on the left, status on the right),
/// or a message scrolling across the whole pill.
public sealed class CollapsedView : Grid
{
    private readonly ContentControl _leading = new() { HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };
    private readonly ContentControl _trailing = new() { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
    // Kept, not rebuilt, so other sources refreshing the hub don't restart the scroll.
    // Fixed height, so the letters don't grow while the pill swells under the pointer.
    private readonly DotMatrixMarquee _marquee = new()
    {
        Height = NotchState.PillSize.Height - 8,
        Margin = new Thickness(10, 4, 10, 0),
        VerticalAlignment = VerticalAlignment.Top,
        Visibility = Visibility.Collapsed,
    };
    private LiveActivity _shown = LiveActivity.None;

    public CollapsedView()
    {
        _leading.Margin = new Thickness(12, 0, 0, 0);
        _trailing.Margin = new Thickness(0, 0, 12, 0);
        Children.Add(_leading);
        Children.Add(_trailing);
        Children.Add(_marquee);
    }

    public void Show(LiveActivity activity)
    {
        var kindChanged = activity.Kind != _shown.Kind;
        _shown = activity;
        _leading.Content = Leading(activity);
        _trailing.Content = Trailing(activity);
        var message = activity.Kind == LiveActivityKind.Message;
        if (message) _marquee.Text = activity.Label;
        _marquee.Visibility = message ? Visibility.Visible : Visibility.Collapsed;
        if (kindChanged && message)
        {
            _marquee.Opacity = 0;
            _marquee.Animate(OpacityProperty, 1, Motion.ExpoOut(0.4));
        }
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
        LiveActivityKind.Timer => Glyph(a.Glyph.Length > 0 ? a.Glyph : "\uE916", 13),
        LiveActivityKind.Download => Glyph("\uE896", 14),
        LiveActivityKind.Music when a.Artwork != null => new Border
        {
            Width = 20,
            Height = 20,
            CornerRadius = new CornerRadius(5),
            Background = new ImageBrush(a.Artwork) { Stretch = Stretch.UniformToFill },
        },
        LiveActivityKind.Music => Glyph("\uE8D6", 13),
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

/// Text scrolling across the pill, drawn as a grid of lit dots with a soft glow.
public sealed class DotMatrixMarquee : Grid
{
    /// Pixels per second, as on the Mac.
    private const double Speed = 40;
    private const double DotSpacing = 2.6;

    private static readonly Brush DotMask = MakeDotMask();

    private readonly Canvas _stage = new() { ClipToBounds = true };
    private readonly TextBlock _text = new()
    {
        FontFamily = new FontFamily("Cascadia Mono, Consolas"),
        FontWeight = FontWeights.Black,
        TextWrapping = TextWrapping.NoWrap,
    };
    private readonly TranslateTransform _shift = new();
    private string _raw = "";

    public DotMatrixMarquee()
    {
        ClipToBounds = true;
        _text.SetResourceReference(TextBlock.ForegroundProperty, ThemeResources.Accent);
        _text.RenderTransform = _shift;
        _stage.Children.Add(_text);
        _stage.OpacityMask = DotMask;
        // The glow sits outside the dot mask, so it blooms around the lit dots; it follows the accent.
        var glow = new System.Windows.Media.Effects.DropShadowEffect { ShadowDepth = 0, BlurRadius = 6, Opacity = 0.8 };
        System.Windows.Data.BindingOperations.SetBinding(glow, System.Windows.Media.Effects.DropShadowEffect.ColorProperty,
            new System.Windows.Data.Binding("Foreground.Color") { Source = _text });
        var host = new Grid { Effect = glow };
        host.Children.Add(_stage);
        Children.Add(host);

        SizeChanged += (_, _) => Restart();
        IsVisibleChanged += (_, _) => Restart();
    }

    public string Text
    {
        get => _raw;
        set
        {
            if (_raw == value) return;
            _raw = value;
            _text.Text = value.ToUpperInvariant();
            Restart();
        }
    }

    /// Runs one pass from the right edge until the text has left on the left, forever. The phase comes
    /// from the clock, as on the Mac, so a resize (the pill peeking) doesn't send the text back to the start.
    private void Restart()
    {
        if (!IsVisible || ActualWidth <= 0 || ActualHeight <= 0)
        {
            _shift.BeginAnimation(TranslateTransform.XProperty, null);
            return;
        }
        _text.FontSize = Math.Max(1, ActualHeight * 0.9);
        _text.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var textWidth = _text.DesiredSize.Width;
        Canvas.SetTop(_text, (ActualHeight - _text.DesiredSize.Height) / 2);
        var cycle = (ActualWidth + textWidth) / Speed;
        var into = Environment.TickCount64 / 1000.0 % cycle;
        _shift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(ActualWidth, -textWidth, TimeSpan.FromSeconds(cycle))
        {
            RepeatBehavior = RepeatBehavior.Forever,
            BeginTime = TimeSpan.FromSeconds(-into),
        });
    }

    private static Brush MakeDotMask()
    {
        var dot = new GeometryDrawing(Brushes.White, null,
            new EllipseGeometry(new Point(DotSpacing / 2, DotSpacing / 2), 0.9, 0.9));
        var tile = new Rect(0, 0, DotSpacing, DotSpacing);
        var brush = new DrawingBrush(dot)
        {
            TileMode = TileMode.Tile,
            Viewport = tile,
            ViewportUnits = BrushMappingMode.Absolute,
            Viewbox = tile,
            ViewboxUnits = BrushMappingMode.Absolute,
            Stretch = Stretch.None,
        };
        brush.Freeze();
        return brush;
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
