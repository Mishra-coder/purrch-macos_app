using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;
using Purrch.Core;
using Purrch.DesignSystem;
using Purrch.Interface;

namespace Purrch.Features.Media;

/// A browser video mirrored live, with playback controls and a quality sheet. The picture comes from YouTube's
/// own player, muted and synced (<see cref="EmbeddedPlayerHost"/>), falling back to the extension's frames.
/// Streams while on screen and stops when it goes away. Always 16:9, as large as its slot allows.
public sealed class LiveVideoPlayer : Border
{
    private const double Radius = 16;
    private static readonly Color Red = Color.FromRgb(0xFF, 0x3B, 0x30);

    private readonly BrowserMediaService _browser = BrowserMediaService.Shared;
    private readonly EmbeddedPlayerHost _embed = EmbeddedPlayerHost.Shared;
    private readonly bool _compact;
    private readonly Action? _onToggleSize;
    private readonly bool _isTheater;
    private BrowserVideo _video;

    private readonly Grid _root = new() { Background = Brushes.Transparent, Cursor = Cursors.Hand };
    /// Where the embedded player shows through: nothing is drawn here, the web view lies underneath.
    private readonly Border _slot = new() { Background = Brushes.Transparent };
    private readonly Border _frameBack = new() { Background = Brushes.Black, Visibility = Visibility.Collapsed };
    private readonly Image _frame = new() { Stretch = Stretch.Uniform };
    /// Black with the video's thumbnail, until a picture arrives (or when the paused tab released it).
    private readonly Border _cover = new() { Background = Brushes.Black, ClipToBounds = true };
    private readonly Image _thumbnail = new() { Stretch = Stretch.UniformToFill };
    private readonly Border _bigPlay;
    private readonly Grid _chrome = new();
    private readonly Rectangle _line = new() { Height = 3, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom, IsHitTestVisible = false };
    private readonly Border _quality = new() { Visibility = Visibility.Collapsed, Background = Color.FromArgb(0xE0, 0x10, 0x10, 0x12).Brush() };
    private readonly TextBlock _title, _channel, _elapsed, _total;
    private readonly SeekLine _scrubber;
    private readonly TextBlock _playGlyph;
    private readonly DispatcherTimer _hideChrome = new();

    private bool _onScreen;
    private bool _chromeVisible = true;
    private bool _showQuality;
    private bool? _coverShown;
    private string? _thumbnailId;

    /// <param name="compact">Smaller title and controls for tight layouts.</param>
    /// <param name="onToggleSize">Grow to / shrink from theater. When set, clicking the picture does this instead of pausing.</param>
    public LiveVideoPlayer(BrowserVideo video, bool compact = false, Action? onToggleSize = null, bool isTheater = false)
    {
        _video = video;
        _compact = compact;
        _onToggleSize = onToggleSize;
        _isTheater = isTheater;
        HorizontalAlignment = HorizontalAlignment.Center;
        VerticalAlignment = VerticalAlignment.Center;
        Child = _root;

        _root.Children.Add(_slot);
        _frameBack.Child = _frame;
        _root.Children.Add(_frameBack);
        _cover.Child = _thumbnail;
        _root.Children.Add(_cover);

        _playGlyph = White(Ui.Glyph("\uE768", compact ? 20 : 26));
        _bigPlay = new Border
        {
            Width = compact ? 54 : 66,
            Height = compact ? 54 : 66,
            CornerRadius = new CornerRadius(compact ? 27 : 33),
            Background = Colors.Black.WithOpacity(0.45).Brush(),
            BorderBrush = Colors.White.WithOpacity(0.35).Brush(),
            BorderThickness = new Thickness(1),
            Child = White(Ui.Glyph("\uE768", compact ? 20 : 26)),
            IsHitTestVisible = false,
            Effect = new DropShadowEffect { Color = Colors.Black, Opacity = 0.4, BlurRadius = 20, ShadowDepth = 0 },
        };
        _root.Children.Add(_bigPlay);

        _title = White(Ui.Text("", compact ? 12 : 13, weight: FontWeights.SemiBold));
        _channel = White(Ui.Text("", compact ? 10 : 11));
        _channel.Opacity = 0.7;
        _elapsed = White(Ui.Text("", 10, weight: FontWeights.Medium)).Tabular();
        _total = White(Ui.Text("", 10, weight: FontWeights.Medium)).Tabular();
        _elapsed.Opacity = _total.Opacity = 0.85;
        _scrubber = new SeekLine(onVideo: true, f => _browser.SeekToFraction(_video, f)) { Height = 12, Margin = new Thickness(8, 0, 8, 0) };
        BuildChrome();
        _root.Children.Add(_chrome);
        _line.Fill = Red.Brush();
        _root.Children.Add(_line);
        _quality.MouseLeftButtonUp += (_, e) => e.Handled = true;
        _root.Children.Add(_quality);

        // Stroke on top of the picture, like the Mac's overlay.
        var stroke = new Border { CornerRadius = new CornerRadius(Radius), BorderThickness = new Thickness(0.8), IsHitTestVisible = false };
        stroke.SetResourceReference(BorderBrushProperty, ThemeResources.Border);
        stroke.Opacity = 0.6;
        _root.Children.Add(stroke);
        _root.SizeChanged += (_, _) =>
        {
            _root.Clip = new RectangleGeometry(new Rect(_root.RenderSize), Radius, Radius);
            Refresh();
        };

        ToolTip = onToggleSize != null && !isTheater ? "Click to watch in the whole notch" : null;
        _root.MouseLeftButtonUp += (_, e) =>
        {
            if (e.Handled) return;
            e.Handled = true;
            if (_onToggleSize != null && !_isTheater) _onToggleSize();
            else _browser.Toggle(_video);
        };
        // Title and controls fade out a few seconds after the pointer stops moving, like YouTube.
        _root.MouseMove += (_, _) => RevealChrome();
        _root.MouseLeave += (_, _) => ScheduleHideChrome(0.8);
        _hideChrome.Tick += (_, _) =>
        {
            _hideChrome.Stop();
            _chromeVisible = false;
            RefreshChrome(animated: true);
        };

        Loaded += (_, _) => RefreshOnScreen();
        Unloaded += (_, _) => RefreshOnScreen();
        IsVisibleChanged += (_, _) => RefreshOnScreen();
        Refresh();
    }

    public BrowserVideo Video => _video;

    /// Opens or closes the quality sheet (also from a gear outside the player, as in the video window).
    public bool ShowQuality
    {
        get => _showQuality;
        set
        {
            _showQuality = value;
            RefreshQuality();
            RefreshChrome(animated: true);
        }
    }

    /// Smooth path: YouTube's own player, muted and synced. Falls back to mirrored frames.
    private bool UsesEmbed => _video.VideoId != null && _embed.FailedVideoId != _video.VideoId;
    private bool EmbedReady => _video.VideoId != null && _embed.ReadyVideoId == _video.VideoId;
    /// Paused, or the quality sheet is open: keep the controls up.
    private bool ShowsChrome => _chromeVisible || !_video.IsPlaying || _showQuality;

    /// The same tab's newer state, or another video to show.
    public void Update(BrowserVideo video)
    {
        var previous = _video;
        _video = video;
        if (_onScreen && previous.Id != video.Id)
        {
            _browser.SetStreamImages(!UsesEmbed);
            _browser.SwitchFrames(video);
        }
        if (_onScreen && previous.VideoId != video.VideoId && UsesEmbed && video.VideoId is { } id)
        {
            _embed.Prepare(id, video.Current);
            _embed.Attach(_slot, Radius);
        }
        Refresh();
    }

    private void RefreshOnScreen()
    {
        var on = IsLoaded && IsVisible;
        if (on == _onScreen) return;
        _onScreen = on;
        if (on)
        {
            _browser.FrameArrived += OnFrame;
            _browser.Changed += OnServiceChanged;
            _embed.Changed += OnEmbedChanged;
            _browser.SetStreamImages(!UsesEmbed);
            _browser.StartFrames(_video);
            if (UsesEmbed && _video.VideoId is { } id)
            {
                _embed.Prepare(id, _video.Current);
                _embed.Attach(_slot, Radius);
            }
            OnFrame(_browser.LastFrame);
            RevealChrome();
        }
        else
        {
            _browser.FrameArrived -= OnFrame;
            _browser.Changed -= OnServiceChanged;
            _embed.Changed -= OnEmbedChanged;
            _hideChrome.Stop();
            _browser.StopFrames();
            _embed.Detach(_slot);
        }
        Refresh();
    }

    private void OnFrame(ImageSource? frame)
    {
        _frame.Source = frame;
        if (!UsesEmbed) RefreshCover();
    }

    private void OnServiceChanged()
    {
        if (_showQuality) RefreshQuality();
        RefreshCover();
    }

    private void OnEmbedChanged()
    {
        // The embed failed: the frames take over.
        if (!UsesEmbed)
        {
            _embed.Detach(_slot);
            _browser.SetStreamImages(true);
        }
        Refresh();
    }

    private void Refresh()
    {
        _frameBack.Visibility = UsesEmbed ? Visibility.Collapsed : Visibility.Visible;
        _title.Text = _video.Title;
        _channel.Text = _video.Channel;
        _channel.Visibility = _video.Channel.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        _elapsed.Text = Format.Duration(_video.Current);
        _total.Text = Format.Duration(_video.Duration);
        _scrubber.Update(_video.Progress);
        _line.Width = Math.Max(0, _root.ActualWidth * _video.Progress);
        _playGlyph.Text = _video.IsPlaying ? "\uE769" : "\uE768";
        _bigPlay.Visibility = _video.IsPlaying ? Visibility.Collapsed : Visibility.Visible;
        if (_video.VideoId != _thumbnailId)
        {
            _thumbnailId = _video.VideoId;
            _thumbnail.Source = null;
            var wanted = _thumbnailId;
            VideoThumbnails.Into(_video.LargeThumbnailUrl, image => { if (_thumbnailId == wanted) _thumbnail.Source = image; });
        }
        RefreshCover();
        RefreshChrome(animated: false);
    }

    /// No picture from the browser yet (or the paused tab released it): show the video's thumbnail.
    private void RefreshCover()
    {
        var show = !(UsesEmbed ? EmbedReady : _browser.HasLiveFrame && _frame.Source != null);
        if (show == _coverShown) return;
        var first = _coverShown == null;
        _coverShown = show;
        _cover.IsHitTestVisible = false;
        if (first) _cover.Set(OpacityProperty, show ? 1 : 0);
        else _cover.Animate(OpacityProperty, show ? 1 : 0, Motion.EaseOut(0.3));
    }

    private void RevealChrome()
    {
        if (!_chromeVisible)
        {
            _chromeVisible = true;
            RefreshChrome(animated: true);
        }
        ScheduleHideChrome(2.5);
    }

    private void ScheduleHideChrome(double seconds)
    {
        _hideChrome.Stop();
        _hideChrome.Interval = TimeSpan.FromSeconds(seconds);
        _hideChrome.Start();
    }

    private void RefreshChrome(bool animated)
    {
        var shown = ShowsChrome;
        _chrome.IsHitTestVisible = shown;
        if (animated)
        {
            _chrome.Animate(OpacityProperty, shown ? 1 : 0, Motion.EaseOut(shown ? 0.2 : 0.4));
            _line.Animate(OpacityProperty, shown ? 0 : 1, Motion.EaseOut(0.3));
        }
        else
        {
            _chrome.Set(OpacityProperty, shown ? 1 : 0);
            _line.Set(OpacityProperty, shown ? 0 : 1);
        }
    }

    private void BuildChrome()
    {
        var top = new Border
        {
            Height = _compact ? 50 : 64,
            VerticalAlignment = VerticalAlignment.Top,
            Background = new LinearGradientBrush(Colors.Black.WithOpacity(0.75), Colors.Transparent, 90),
        };
        var names = new StackPanel { Margin = new Thickness(_compact ? 10 : 12), VerticalAlignment = VerticalAlignment.Top };
        foreach (var t in new[] { _title, _channel })
        {
            t.TextWrapping = TextWrapping.NoWrap;
            t.TextTrimming = TextTrimming.CharacterEllipsis;
            t.HorizontalAlignment = HorizontalAlignment.Left;
        }
        names.Children.Add(_title);
        _channel.Margin = new Thickness(0, 2, 0, 0);
        names.Children.Add(_channel);
        top.Child = names;
        _chrome.Children.Add(top);

        var times = new DockPanel();
        DockPanel.SetDock(_elapsed, Dock.Left);
        DockPanel.SetDock(_total, Dock.Right);
        times.Children.Add(_elapsed);
        times.Children.Add(_total);
        times.Children.Add(_scrubber);

        var buttons = new Grid { Margin = new Thickness(0, _compact ? 6 : 8, 0, 0) };
        for (var i = 0; i < 5; i++)
            buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = i % 2 == 1 ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });
        Place(buttons, Round("\uE713", _compact ? 24 : 28, () => ShowQuality = !_showQuality, tooltip: "Quality"), 0);

        var middle = new StackPanel { Orientation = Orientation.Horizontal };
        var gap = _compact ? 10 : 14;
        middle.Children.Add(Round("\uEB9E", _compact ? 26 : 30, () => _browser.Seek(_video, -10), tooltip: "Back 10s"));
        var play = Round("", _compact ? 36 : 42, () => _browser.Toggle(_video), filled: true, tooltip: "Play / pause", icon: _playGlyph);
        play.Margin = new Thickness(gap, 0, gap, 0);
        middle.Children.Add(play);
        var forward = Round("\uEB9D", _compact ? 26 : 30, () => _browser.Seek(_video, 10), tooltip: "Forward 10s");
        forward.Margin = new Thickness(0, 0, gap, 0);
        middle.Children.Add(forward);
        middle.Children.Add(Round("\uE893", _compact ? 26 : 30, () => _browser.Next(_video), tooltip: "Next video"));
        Place(buttons, middle, 2);

        var right = new StackPanel { Orientation = Orientation.Horizontal };
        right.Children.Add(Round("\uE8A7", _compact ? 24 : 28, () => _browser.Focus(_video), tooltip: "Show tab"));
        if (_onToggleSize != null)
        {
            var size = Round(_isTheater ? "\uE73F" : "\uE740", _compact ? 24 : 28, _onToggleSize,
                tooltip: _isTheater ? "Back to Now Playing" : "Watch in the whole notch");
            size.Margin = new Thickness(gap, 0, 0, 0);
            right.Children.Add(size);
        }
        Place(buttons, right, 4);

        var bottom = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Bottom,
            Background = new LinearGradientBrush(Colors.Transparent, Colors.Black.WithOpacity(0.8), 90),
        };
        var inner = new StackPanel { Margin = new Thickness(12, 22, 12, _compact ? 8 : 10) };
        inner.Children.Add(times);
        inner.Children.Add(buttons);
        bottom.Children.Add(inner);
        _chrome.Children.Add(bottom);
    }

    private static void Place(Grid grid, UIElement element, int column)
    {
        Grid.SetColumn(element, column);
        if (element is FrameworkElement f) f.VerticalAlignment = VerticalAlignment.Center;
        grid.Children.Add(element);
    }

    /// YouTube's own resolution plus how sharp / smooth the notch preview is.
    private void RefreshQuality()
    {
        _quality.Visibility = _showQuality ? Visibility.Visible : Visibility.Collapsed;
        if (!_showQuality) return;
        var panel = new StackPanel { Margin = new Thickness(14) };
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 12) };
        var close = Round("\uE711", 24, () => ShowQuality = false, tooltip: "Close");
        DockPanel.SetDock(close, Dock.Right);
        header.Children.Add(close);
        header.Children.Add(White(Ui.Text("Quality", 14, weight: FontWeights.Bold)));
        panel.Children.Add(header);

        panel.Children.Add(Caption("YOUTUBE"));
        if (_browser.YouTubeQualities.Count == 0)
        {
            var none = White(Ui.Text("This player doesn't offer quality choices.", 11));
            none.Opacity = 0.7;
            none.Margin = new Thickness(0, 0, 0, 12);
            panel.Children.Add(none);
        }
        else
        {
            var chips = Chips(_browser.YouTubeQualities, _browser.YouTubeQuality, YouTubeQualityLabel.Text,
                level => _browser.SetYouTubeQuality(_video, level));
            chips.Margin = new Thickness(0, 0, 0, 6);
            panel.Children.Add(chips);
        }

        panel.Children.Add(Caption("NOTCH PREVIEW"));
        var qualities = Enum.GetValues<PreviewQuality>();
        panel.Children.Add(Chips(qualities.Select(q => q.ToString()).ToList(), AppSettings.Shared.PreviewQuality.ToString(),
            raw => raw, raw =>
            {
                if (Enum.TryParse<PreviewQuality>(raw, out var q)) AppSettings.Shared.PreviewQuality = q;
                RefreshQuality();
            }));
        var hint = White(Ui.Text("Smooth moves more frames, Sharp shows more detail. Used when YouTube's own player can't be embedded.", 10));
        hint.Opacity = 0.55;
        panel.Children.Add(hint);
        _quality.Child = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, Content = panel };
    }

    private static TextBlock Caption(string text)
    {
        var caption = White(Ui.Text(text, 10, weight: FontWeights.SemiBold));
        caption.Opacity = 0.6;
        caption.Margin = new Thickness(0, 0, 0, 6);
        return caption;
    }

    /// Row of selectable capsules that wraps onto new lines.
    private static WrapPanel Chips(IReadOnlyList<string> items, string selected, Func<string, string> label, Action<string> onSelect)
    {
        var wrap = new WrapPanel();
        foreach (var item in items)
        {
            var on = item == selected;
            var text = Ui.Text(label(item), 11, weight: FontWeights.SemiBold);
            text.Foreground = on ? Brushes.Black : Brushes.White;
            text.HorizontalAlignment = HorizontalAlignment.Center;
            var chip = new Border
            {
                MinWidth = 62,
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(10, 5, 10, 5),
                Background = on ? Brushes.White : Colors.White.WithOpacity(0.14).Brush(),
                Child = text,
            };
            var value = item;
            var button = Ui.Bare(chip, () => onSelect(value));
            button.Margin = new Thickness(0, 0, 6, 6);
            wrap.Children.Add(button);
        }
        return wrap;
    }

    /// A round control over the picture: white glyph on a faint disc, or black on white when filled.
    private static Button Round(string glyph, double size, Action onClick, bool filled = false, string? tooltip = null,
                                TextBlock? icon = null)
    {
        icon ??= Ui.Glyph(glyph, size * 0.4);
        icon.FontSize = size * 0.4;
        icon.Foreground = filled ? Brushes.Black : Brushes.White;
        var disc = new Border
        {
            Width = size,
            Height = size,
            CornerRadius = new CornerRadius(size / 2),
            Background = filled ? Brushes.White : Colors.White.WithOpacity(0.16).Brush(),
            Child = icon,
        };
        return Ui.Bare(disc, onClick, tooltip);
    }

    private static TextBlock White(TextBlock text)
    {
        text.Foreground = Brushes.White;
        return text;
    }

    protected override Size MeasureOverride(Size available)
    {
        var size = Fit(available);
        base.MeasureOverride(size);
        return size;
    }

    /// The largest 16:9 rectangle that fits.
    private static Size Fit(Size available)
    {
        var width = double.IsInfinity(available.Width) ? 480 : available.Width;
        var height = width * 9 / 16;
        if (!double.IsInfinity(available.Height) && height > available.Height)
        {
            height = available.Height;
            width = height * 16 / 9;
        }
        return new Size(Math.Max(0, width), Math.Max(0, height));
    }
}

/// Thin seekable progress line: red on white over a video, accent on the theme's faint fill elsewhere.
/// It thickens under the pointer, and a click jumps there.
public sealed class SeekLine : Grid
{
    private readonly Border _track = new() { CornerRadius = new CornerRadius(3), Height = 4, VerticalAlignment = VerticalAlignment.Center };
    private readonly Border _fill = new() { CornerRadius = new CornerRadius(3), Height = 4, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };
    private double _progress;

    public SeekLine(bool onVideo, Action<double> onSeek)
    {
        Background = Brushes.Transparent;
        Cursor = Cursors.Hand;
        if (onVideo)
        {
            _track.Background = Colors.White.WithOpacity(0.25).Brush();
            _fill.Background = Color.FromRgb(0xFF, 0x3B, 0x30).Brush();
        }
        else
        {
            _track.SetResourceReference(Border.BackgroundProperty, ThemeResources.Fill);
            _fill.SetResourceReference(Border.BackgroundProperty, ThemeResources.Accent);
        }
        Children.Add(_track);
        Children.Add(_fill);
        MouseEnter += (_, _) => _track.Height = _fill.Height = 6;
        MouseLeave += (_, _) => _track.Height = _fill.Height = 4;
        MouseLeftButtonDown += (_, e) => e.Handled = true;
        MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            onSeek(Math.Clamp(e.GetPosition(this).X / Math.Max(1, ActualWidth), 0, 1));
        };
        SizeChanged += (_, _) => Update(_progress);
    }

    public void Update(double progress)
    {
        _progress = progress;
        _fill.Width = Math.Max(4, ActualWidth * Math.Clamp(progress, 0, 1));
    }
}
