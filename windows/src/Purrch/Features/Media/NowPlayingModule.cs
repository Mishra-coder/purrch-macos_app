using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using Purrch.Core;
using Purrch.DesignSystem;
using Purrch.Interface;

namespace Purrch.Features.Media;

/// Big artwork, track details, a scrubber and transport controls over a blurred copy of the artwork.
public sealed class NowPlayingModule : ContentControl
{
    private readonly NowPlayingService _player = NowPlayingService.Shared;
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private Scrubber? _scrubber;

    public NowPlayingModule()
    {
        Rebuild();
        _clock.Tick += (_, _) => _scrubber?.Update();
        Loaded += (_, _) => { _player.Changed += Rebuild; _clock.Start(); Rebuild(); };
        Unloaded += (_, _) => { _player.Changed -= Rebuild; _clock.Stop(); };
    }

    private void Rebuild()
    {
        if (!_player.HasTrack)
        {
            _scrubber = null;
            Content = new Card("Now Playing", "\uE8D6",
                Ui.EmptyState("\uE8D6", "Nothing playing", "Play something in Spotify, a browser or any media app"));
            return;
        }

        var root = new Grid { ClipToBounds = true };
        root.Children.Add(Backdrop());

        var layout = new DockPanel { Margin = new Thickness(16) };
        var art = Artwork(236);
        art.Margin = new Thickness(0, 0, 22, 0);
        DockPanel.SetDock(art, Dock.Left);
        layout.Children.Add(art);
        layout.Children.Add(Details());
        root.Children.Add(layout);

        var card = new Border { CornerRadius = new CornerRadius(20), BorderThickness = new Thickness(0.8), Child = root };
        card.SetResourceReference(Border.BorderBrushProperty, ThemeResources.Border);
        // Round the backdrop's corners too.
        root.SizeChanged += (_, _) => root.Clip = new RectangleGeometry(new Rect(root.RenderSize), 20, 20);
        Content = card;
    }

    private UIElement Details()
    {
        var panel = new DockPanel { Margin = new Thickness(0, 6, 0, 6) };

        var top = new StackPanel { Orientation = Orientation.Horizontal };
        var accent = ((SolidColorBrush)FindResource(ThemeResources.Accent)).Color;
        var tag = new Border
        {
            CornerRadius = new CornerRadius(5),
            Padding = new Thickness(6, 1, 6, 2),
            Background = accent.WithOpacity(0.18).Brush(),
            Child = new TextBlock
            {
                Text = _player.SourceApp.Length == 0 ? "Media" : _player.SourceApp,
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Foreground = accent.Brush(),
            },
            Margin = new Thickness(0, 0, 8, 0),
        };
        top.Children.Add(tag);
        if (_player.IsPlaying)
        {
            top.Children.Add(new MusicBars { Width = 18, Height = 14, VerticalAlignment = VerticalAlignment.Center });
        }
        else
        {
            var paused = Ui.Text("PAUSED", 9, ThemeResources.SecondaryText, FontWeights.Bold);
            top.Children.Add(paused);
        }
        DockPanel.SetDock(top, Dock.Top);
        panel.Children.Add(top);

        var controls = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        var previous = Ui.IconButton("\uE892", 44, _player.Previous, tooltip: "Previous");
        var play = Ui.IconButton(_player.IsPlaying ? "\uE769" : "\uE768", 58, _player.TogglePlayPause, prominent: true,
            tooltip: _player.IsPlaying ? "Pause" : "Play");
        var next = Ui.IconButton("\uE893", 44, _player.Next, tooltip: "Next");
        play.Margin = new Thickness(18, 0, 18, 0);
        previous.VerticalAlignment = next.VerticalAlignment = VerticalAlignment.Center;
        controls.Children.Add(previous);
        controls.Children.Add(play);
        controls.Children.Add(next);
        DockPanel.SetDock(controls, Dock.Bottom);
        panel.Children.Add(controls);

        _scrubber = new Scrubber(_player) { Margin = new Thickness(0, 12, 0, 12) };
        DockPanel.SetDock(_scrubber, Dock.Bottom);
        panel.Children.Add(_scrubber);

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Bottom };
        text.Children.Add(Line(_player.Title, 24, ThemeResources.Text, FontWeights.Bold));
        var artist = Line(_player.Artist, 15, ThemeResources.Text, FontWeights.Medium);
        artist.Opacity = 0.85;
        artist.Margin = new Thickness(0, 2, 0, 0);
        text.Children.Add(artist);
        if (_player.Album.Length > 0)
        {
            var album = Line(_player.Album, 12, ThemeResources.SecondaryText, FontWeights.Normal);
            album.Margin = new Thickness(0, 2, 0, 0);
            text.Children.Add(album);
        }
        panel.Children.Add(text);
        return panel;
    }

    private static TextBlock Line(string text, double size, string colour, FontWeight weight)
    {
        var line = Ui.Text(text, size, colour, weight);
        line.TextWrapping = TextWrapping.NoWrap;
        line.TextTrimming = TextTrimming.CharacterEllipsis;
        line.ToolTip = text;
        return line;
    }

    private FrameworkElement Artwork(double size)
    {
        var frame = new Border
        {
            Width = size,
            Height = size,
            CornerRadius = new CornerRadius(18),
            Effect = new DropShadowEffect { Color = Colors.Black, Opacity = 0.5, BlurRadius = 28, ShadowDepth = 6, Direction = 270 },
            // Slightly smaller while paused, like the Mac app.
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = new ScaleTransform(_player.IsPlaying ? 1 : 0.94, _player.IsPlaying ? 1 : 0.94),
        };
        if (_player.Artwork is { } artwork)
        {
            frame.Background = new ImageBrush(artwork) { Stretch = Stretch.UniformToFill };
        }
        else
        {
            frame.SetResourceReference(Border.BackgroundProperty, ThemeResources.Hairline);
            frame.Child = Ui.Glyph("\uE8D6", 64, ThemeResources.Accent);
        }
        return frame;
    }

    private UIElement Backdrop()
    {
        var layers = new Grid();
        var surface = new Border();
        surface.SetResourceReference(Border.BackgroundProperty, ThemeResources.Surface);
        layers.Children.Add(surface);

        if (_player.Artwork is { } artwork)
        {
            layers.Children.Add(new Border
            {
                Background = new ImageBrush(artwork) { Stretch = Stretch.UniformToFill },
                Opacity = 0.45,
                Effect = new BlurEffect { Radius = 50, KernelType = KernelType.Gaussian },
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = new ScaleTransform(1.4, 1.4),
            });
        }
        else
        {
            var accent = ((SolidColorBrush)FindResource(ThemeResources.Accent)).Color;
            layers.Children.Add(new Border
            {
                Background = new RadialGradientBrush(accent.WithOpacity(0.18), Colors.Transparent)
                {
                    Center = new Point(0, 0),
                    GradientOrigin = new Point(0, 0),
                    RadiusX = 0.8,
                    RadiusY = 1.2,
                },
            });
        }

        var background = ((SolidColorBrush)FindResource(ThemeResources.Background)).Color;
        layers.Children.Add(new Border
        {
            Background = new LinearGradientBrush(background.WithOpacity(0.1), background.WithOpacity(0.55), 90),
        });
        return layers;
    }
}

/// Progress bar with elapsed and remaining time. Click or drag along it to jump.
internal sealed class Scrubber : StackPanel
{
    private readonly NowPlayingService _player;
    private readonly Grid _bar = new() { Height = 16, Background = Brushes.Transparent, Cursor = Cursors.Hand };
    private readonly Border _track = new() { CornerRadius = new CornerRadius(3), Height = 5, VerticalAlignment = VerticalAlignment.Center };
    private readonly Border _fill = new() { CornerRadius = new CornerRadius(3), Height = 5, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };
    private readonly Border _knob = new() { Width = 10, Height = 10, CornerRadius = new CornerRadius(7), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _elapsed = Ui.Text("", 11, ThemeResources.SecondaryText, FontWeights.Medium).Tabular();
    private readonly TextBlock _remaining = Ui.Text("", 11, ThemeResources.SecondaryText, FontWeights.Medium).Tabular();
    private double? _dragFraction;

    public Scrubber(NowPlayingService player)
    {
        _player = player;
        _track.SetResourceReference(Border.BackgroundProperty, ThemeResources.Fill);
        _fill.SetResourceReference(Border.BackgroundProperty, ThemeResources.Accent);
        _knob.SetResourceReference(Border.BackgroundProperty, ThemeResources.Text);
        _knob.Effect = new DropShadowEffect { Color = Colors.Black, Opacity = 0.4, BlurRadius = 4, ShadowDepth = 0 };
        _bar.Children.Add(_track);
        _bar.Children.Add(_fill);
        _bar.Children.Add(_knob);
        Children.Add(_bar);

        var times = new DockPanel { Margin = new Thickness(0, 6, 0, 0) };
        DockPanel.SetDock(_remaining, Dock.Right);
        times.Children.Add(_remaining);
        times.Children.Add(_elapsed);
        Children.Add(times);

        _bar.MouseEnter += (_, _) => Grow(true);
        _bar.MouseLeave += (_, _) => { if (_dragFraction == null) Grow(false); };
        _bar.MouseLeftButtonDown += (_, e) =>
        {
            if (_player.Duration <= 0) return;
            _bar.CaptureMouse();
            _dragFraction = FractionAt(e);
            Update();
            e.Handled = true;
        };
        _bar.MouseMove += (_, e) =>
        {
            if (_dragFraction == null) return;
            _dragFraction = FractionAt(e);
            Update();
        };
        _bar.MouseLeftButtonUp += (_, e) =>
        {
            if (_dragFraction is not { } fraction) return;
            _bar.ReleaseMouseCapture();
            _dragFraction = null;
            _player.Seek(fraction * _player.Duration);
            if (!_bar.IsMouseOver) Grow(false);
            e.Handled = true;
        };
        SizeChanged += (_, _) => Update();
        Update();
    }

    private double FractionAt(MouseEventArgs e) =>
        Math.Clamp(e.GetPosition(_bar).X / Math.Max(1, _bar.ActualWidth), 0, 1);

    private void Grow(bool big)
    {
        _track.Height = _fill.Height = big ? 7 : 5;
        _knob.Width = _knob.Height = big ? 14 : 10;
        Update();
    }

    public void Update()
    {
        var duration = _player.Duration;
        var elapsed = _dragFraction is { } f ? f * duration : _player.Elapsed;
        var fraction = duration > 0 ? Math.Clamp(elapsed / duration, 0, 1) : 0;
        var width = _bar.ActualWidth;
        _fill.Width = Math.Max(0, width * fraction);
        _knob.Margin = new Thickness(Math.Max(0, width * fraction - _knob.Width / 2), 0, 0, 0);
        _knob.Visibility = duration > 0 ? Visibility.Visible : Visibility.Hidden;
        _elapsed.Text = Format.Duration(elapsed);
        _remaining.Text = duration > 0 ? "-" + Format.Duration(Math.Max(0, duration - elapsed)) : "--:--";
    }
}
