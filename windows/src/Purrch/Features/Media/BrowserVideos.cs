using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Purrch.Core;
using Purrch.DesignSystem;
using Purrch.Interface;

namespace Purrch.Features.Media;

/// Now Playing's browser section with the Audio / Video switch. Audio: every open video with its controls,
/// beside the music app's card. Video: the chosen one playing live, with the others in a slim sidebar.
/// Updated in place as reports arrive, so the live player and hover states survive.
public sealed class BrowserMediaSection : Grid
{
    /// The video picked in the sidebar, remembered while Purrch runs, like the Mac's @State.
    private static string? _selectedId;

    private readonly BrowserMediaService _browser = BrowserMediaService.Shared;
    private readonly AppSettings _settings = AppSettings.Shared;
    private UIElement? _musicCard;
    private string? _layoutKey;
    private UIElement? _layout;

    // Audio layout.
    private readonly Dictionary<string, VideoRow> _rows = new();
    private StackPanel? _rowList;
    private TextBlock? _openCount;
    private Grid? _musicColumn;
    // Video layout.
    private LiveVideoPlayer? _player;
    private StackPanel? _choiceList;
    private TextBlock? _choiceLabel;
    // Both.
    private readonly List<FrameworkElement> _pauseAll = new();

    public BrowserMediaSection(UIElement? musicCard)
    {
        _musicCard = musicCard;
        Refresh();
    }

    private BrowserVideo? Selected =>
        _browser.Videos.FirstOrDefault(v => v.Id == _selectedId) ?? _browser.PlayingVideo ?? _browser.Videos.FirstOrDefault();

    /// The music app's card for audio mode changed (or went).
    public void SetMusicCard(UIElement? card)
    {
        _musicCard = card;
        if (_musicColumn == null) return;
        _musicColumn.Children.Clear();
        // The previous layout may still hold it while it fades out.
        if (card is FrameworkElement { Parent: Panel holder }) holder.Children.Remove(card);
        if (card != null) _musicColumn.Children.Add(card);
        _musicColumn.Visibility = card == null ? Visibility.Collapsed : Visibility.Visible;
    }

    public void Refresh()
    {
        var selected = Selected;
        var video = _settings.NowPlayingMode == MediaViewMode.Video && selected is { Controllable: true };
        var key = video ? "video" : "audio";
        if (key != _layoutKey)
        {
            _layoutKey = key;
            var next = video ? VideoLayout(selected!) : AudioLayout();
            var previous = _layout;
            _layout = next;
            Children.Add(next);
            next.Opacity = 0;
            next.Animate(OpacityProperty, 1, Motion.ExpoOut(0.4));
            if (previous != null)
            {
                previous.IsHitTestVisible = false;
                previous.Animate(OpacityProperty, 0, Motion.ExpoOut(0.2), completed: () => Children.Remove(previous));
            }
        }
        foreach (var pause in _pauseAll) pause.Visibility = _browser.AnyPlaying ? Visibility.Visible : Visibility.Collapsed;
        if (key == "video") RefreshVideo(selected!);
        else RefreshAudio();
    }

    // MARK: - Audio

    private UIElement AudioLayout()
    {
        _player = null;
        _choiceList = null;
        _pauseAll.Clear();
        _rows.Clear();

        var layout = new DockPanel();
        var top = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        top.Children.Add(ModePicker());
        var pause = Ui.Pill("Pause all", "\uE769", _browser.PauseAll);
        pause.HorizontalAlignment = HorizontalAlignment.Right;
        _pauseAll.Add(pause);
        top.Children.Add(pause);
        DockPanel.SetDock(top, Dock.Top);
        layout.Children.Add(top);

        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        _musicColumn = new Grid { Width = 230, Margin = new Thickness(0, 0, 12, 0) };
        SetMusicCard(_musicCard);
        row.Children.Add(_musicColumn);

        _rowList = new StackPanel();
        _openCount = Ui.Text("", 11, ThemeResources.SecondaryText);
        var card = new Card("Browser videos", "\uE714",
            new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, Content = _rowList }, _openCount);
        SetColumn(card, 1);
        row.Children.Add(card);
        layout.Children.Add(row);
        return layout;
    }

    private void RefreshAudio()
    {
        if (_rowList == null || _openCount == null) return;
        _openCount.Text = $"{_browser.Videos.Count} open";
        var ids = _browser.Videos.Select(v => v.Id).ToHashSet();
        foreach (var gone in _rows.Keys.Where(id => !ids.Contains(id)).ToList())
        {
            var row = _rows[gone];
            _rows.Remove(gone);
            row.IsHitTestVisible = false;
            row.Animate(OpacityProperty, 0, Motion.ExpoOut(0.25), completed: () => _rowList.Children.Remove(row));
        }
        var index = 0;
        foreach (var video in _browser.Videos)
        {
            if (_rows.TryGetValue(video.Id, out var row))
            {
                row.Update(video);
            }
            else
            {
                row = new VideoRow(video) { Margin = new Thickness(0, 0, 0, 8) };
                _rows[video.Id] = row;
                // New rows slide down into place, like the Mac's move-from-top transition.
                var shift = new TranslateTransform(0, -10);
                row.RenderTransform = shift;
                row.Opacity = 0;
                shift.Animate(TranslateTransform.YProperty, 0, Motion.Pop);
                row.Animate(OpacityProperty, 1, Motion.ExpoOut(0.3));
            }
            var at = _rowList.Children.IndexOf(row);
            if (at != index)
            {
                if (at >= 0) _rowList.Children.RemoveAt(at);
                _rowList.Children.Insert(Math.Min(index, _rowList.Children.Count), row);
            }
            index++;
        }
    }

    // MARK: - Video

    /// The video takes all the height it can; controls and the other videos sit in a slim sidebar.
    private UIElement VideoLayout(BrowserVideo video)
    {
        _rowList = null;
        _musicColumn = null;
        _pauseAll.Clear();

        var layout = new Grid();
        layout.ColumnDefinitions.Add(new ColumnDefinition());
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(196) });

        _player = new LiveVideoPlayer(video, compact: true,
            onToggleSize: () => { if (_player != null) NotchState.Shared.EnterTheater(_player.Video.Id); })
        {
            VerticalAlignment = VerticalAlignment.Top,
        };
        layout.Children.Add(_player);

        var side = new DockPanel();
        var picker = ModePicker();
        picker.Margin = new Thickness(0, 0, 0, 8);
        DockPanel.SetDock(picker, Dock.Top);
        side.Children.Add(picker);

        var pause = WideButton("Pause all", "\uE769", _browser.PauseAll, prominent: false);
        pause.Margin = new Thickness(0, 0, 0, 8);
        _pauseAll.Add(pause);
        DockPanel.SetDock(pause, Dock.Top);
        side.Children.Add(pause);

        _choiceLabel = Ui.Text("", 11, ThemeResources.SecondaryText, FontWeights.SemiBold);
        _choiceLabel.Margin = new Thickness(0, 2, 0, 6);
        DockPanel.SetDock(_choiceLabel, Dock.Top);
        side.Children.Add(_choiceLabel);

        var full = WideButton("Full notch", "\uE740", () => { if (_player != null) NotchState.Shared.EnterTheater(_player.Video.Id); }, prominent: true);
        full.Margin = new Thickness(0, 8, 0, 0);
        DockPanel.SetDock(full, Dock.Bottom);
        side.Children.Add(full);

        _choiceList = new StackPanel();
        side.Children.Add(new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, Content = _choiceList });
        SetColumn(side, 2);
        layout.Children.Add(side);
        return layout;
    }

    private void RefreshVideo(BrowserVideo video)
    {
        _player?.Update(video);
        if (_choiceList == null || _choiceLabel == null) return;
        var count = _browser.Videos.Count;
        _choiceLabel.Text = count == 1 ? "Open video" : $"Open videos \u00B7 {count}";
        // Rebuilt only when something in it changes, so the playing one's bars don't restart on every report.
        var key = video.Id + "|" + string.Join("|", _browser.Videos.Select(v => $"{v.Id}:{v.Title}:{v.Channel}:{v.IsPlaying}:{v.VideoId}"));
        if (_choiceList.Tag as string == key) return;
        _choiceList.Tag = key;
        _choiceList.Children.Clear();
        foreach (var v in _browser.Videos) _choiceList.Children.Add(Choice(v, v.Id == video.Id));
    }

    private UIElement Choice(BrowserVideo v, bool active)
    {
        var thumb = new Border { Width = 48, Height = 28, CornerRadius = new CornerRadius(6) };
        thumb.SetResourceReference(Border.BackgroundProperty, ThemeResources.Hairline);
        VideoThumbnails.Into(v.ThumbnailUrl, image => thumb.Background = new ImageBrush(image) { Stretch = Stretch.UniformToFill });

        var title = Ui.Text(v.Title, 11, weight: FontWeights.SemiBold);
        var line = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 1, 0, 0) };
        if (v.IsPlaying) line.Children.Add(new MusicBars { Width = 10, Height = 8, Margin = new Thickness(0, 0, 4, 0) });
        var sub = Ui.Text(v.Channel.Length == 0 ? v.Browser : v.Channel, 10, ThemeResources.SecondaryText);
        line.Children.Add(sub);
        foreach (var t in new[] { title, sub })
        {
            t.TextWrapping = TextWrapping.NoWrap;
            t.TextTrimming = TextTrimming.CharacterEllipsis;
        }
        var names = new StackPanel { Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        names.Children.Add(title);
        names.Children.Add(line);
        var row = new DockPanel();
        DockPanel.SetDock(thumb, Dock.Left);
        row.Children.Add(thumb);
        row.Children.Add(names);

        var accent = ((SolidColorBrush)Application.Current.FindResource(ThemeResources.Accent)).Color;
        var text = ((SolidColorBrush)Application.Current.FindResource(ThemeResources.Text)).Color;
        var face = new Border
        {
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(6),
            Margin = new Thickness(0, 0, 0, 6),
            BorderThickness = new Thickness(0.8),
            Background = (active ? accent.WithOpacity(0.16) : text.WithOpacity(0.04)).Brush(),
            BorderBrush = active ? accent.WithOpacity(0.6).Brush() : Brushes.Transparent,
            Child = row,
        };
        var id = v.Id;
        var button = Ui.Bare(face, () =>
        {
            _selectedId = id;
            Refresh();
        });
        button.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        return button;
    }

    // MARK: - Shared

    /// Audio / Video capsule switch; the choice is kept in settings.json.
    private FrameworkElement ModePicker()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var mode in new[] { MediaViewMode.Audio, MediaViewMode.Video })
        {
            var on = _settings.NowPlayingMode == mode;
            var foreground = on ? ThemeResources.Background : ThemeResources.Text;
            var content = new StackPanel { Orientation = Orientation.Horizontal };
            content.Children.Add(Ui.Glyph(mode == MediaViewMode.Audio ? "\uE8D6" : "\uE714", 11, foreground));
            var label = Ui.Text(mode == MediaViewMode.Audio ? "Audio" : "Video", 12, foreground, FontWeights.SemiBold);
            label.Margin = new Thickness(6, 0, 0, 0);
            if (!on) label.Opacity = 0.8;
            content.Children.Add(label);
            var capsule = new Border { CornerRadius = new CornerRadius(12), Padding = new Thickness(12, 5, 12, 5), Child = content };
            if (on) capsule.SetResourceReference(Border.BackgroundProperty, ThemeResources.Accent);
            else capsule.Background = Brushes.Transparent;
            var m = mode;
            row.Children.Add(Ui.Bare(capsule, () =>
            {
                if (_settings.NowPlayingMode == m) return;
                _settings.NowPlayingMode = m;
                // A rebuilt picker shows the new choice in whichever layout comes next.
                _layoutKey = null;
                Refresh();
            }));
        }
        var track = new Border { CornerRadius = new CornerRadius(15), Padding = new Thickness(3), Child = row, HorizontalAlignment = HorizontalAlignment.Left };
        track.SetResourceReference(Border.BackgroundProperty, ThemeResources.Hairline);
        return track;
    }

    /// Full-width capsule button for the sidebar.
    private static Button WideButton(string title, string glyph, Action onClick, bool prominent)
    {
        var foreground = prominent ? ThemeResources.Background : ThemeResources.Text;
        var content = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        content.Children.Add(Ui.Glyph(glyph, 11, foreground));
        var label = Ui.Text(title, 12, foreground, FontWeights.SemiBold);
        label.Margin = new Thickness(6, 0, 0, 0);
        content.Children.Add(label);
        var capsule = new Border { CornerRadius = new CornerRadius(14), Padding = new Thickness(0, prominent ? 7 : 6, 0, prominent ? 7 : 6), Child = content };
        capsule.SetResourceReference(Border.BackgroundProperty, prominent ? ThemeResources.Accent : ThemeResources.Fill);
        var button = Ui.Bare(capsule, onClick);
        button.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        return button;
    }
}

/// One browser video in the list: thumbnail (click to watch it in the whole notch), title, channel and
/// browser, a seekable progress line, and its controls.
internal sealed class VideoRow : Border
{
    private readonly BrowserMediaService _browser = BrowserMediaService.Shared;
    private readonly Border _thumb = new() { Width = 96, Height = 54, CornerRadius = new CornerRadius(8), ClipToBounds = true };
    private readonly System.Windows.Shapes.Rectangle _progress = new()
    {
        Height = 3,
        HorizontalAlignment = HorizontalAlignment.Left,
        VerticalAlignment = VerticalAlignment.Bottom,
        Fill = Color.FromRgb(0xFF, 0x3B, 0x30).Brush(),
    };
    private readonly Border _pausedBadge;
    private readonly TextBlock _title = Ui.Text("", 13, weight: FontWeights.SemiBold);
    private readonly TextBlock _subtitle = Ui.Text("", 11, ThemeResources.SecondaryText);
    private readonly MusicBars _bars = new() { Width = 12, Height = 10, Margin = new Thickness(0, 0, 5, 0) };
    private readonly SeekLine _seek;
    private readonly TextBlock _time = Ui.Text("", 10, ThemeResources.SecondaryText, FontWeights.Medium).Tabular();
    private readonly DockPanel _timeline = new() { Height = 14, Margin = new Thickness(0, 3, 0, 0) };
    private readonly ContentControl _controls = new() { VerticalAlignment = VerticalAlignment.Center };
    private BrowserVideo _video;
    private string? _thumbId;
    private bool? _controlsPlaying;
    private bool _hovering;

    public VideoRow(BrowserVideo video)
    {
        _video = video;
        CornerRadius = new CornerRadius(12);
        Padding = new Thickness(8);
        BorderThickness = new Thickness(0.8);

        var thumbGrid = new Grid();
        thumbGrid.Children.Add(_thumb);
        var progressHost = new Grid { Width = 96, Height = 54, ClipToBounds = true, IsHitTestVisible = false };
        progressHost.Children.Add(_progress);
        thumbGrid.Children.Add(progressHost);
        _pausedBadge = new Border
        {
            Width = 26,
            Height = 26,
            CornerRadius = new CornerRadius(13),
            Background = Colors.Black.WithOpacity(0.5).Brush(),
            Child = new TextBlock { Text = "\uE769", FontSize = 12, Foreground = Brushes.White, Style = (Style)Application.Current.FindResource("Purrch.Glyph") },
            IsHitTestVisible = false,
        };
        thumbGrid.Children.Add(_pausedBadge);
        var thumbButton = Ui.Bare(thumbGrid, () => { if (_video.Controllable) NotchState.Shared.EnterTheater(_video.Id); });
        thumbButton.Margin = new Thickness(0, 0, 12, 0);
        thumbButton.VerticalAlignment = VerticalAlignment.Center;

        foreach (var t in new[] { _title, _subtitle })
        {
            t.TextWrapping = TextWrapping.NoWrap;
            t.TextTrimming = TextTrimming.CharacterEllipsis;
        }
        var line = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 0) };
        line.Children.Add(_bars);
        line.Children.Add(_subtitle);
        _seek = new SeekLine(onVideo: false, f => _browser.SeekToFraction(_video, f)) { Height = 10, VerticalAlignment = VerticalAlignment.Center };
        _time.Margin = new Thickness(8, 0, 0, 0);
        DockPanel.SetDock(_time, Dock.Right);
        _timeline.Children.Add(_time);
        _timeline.Children.Add(_seek);
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(_title);
        text.Children.Add(line);
        text.Children.Add(_timeline);

        var layout = new DockPanel();
        DockPanel.SetDock(thumbButton, Dock.Left);
        layout.Children.Add(thumbButton);
        var show = Ui.IconButton("\uE8A7", 26, () => _browser.Focus(_video), tooltip: "Show tab");
        show.VerticalAlignment = VerticalAlignment.Center;
        show.Margin = new Thickness(6, 0, 0, 0);
        DockPanel.SetDock(show, Dock.Right);
        layout.Children.Add(show);
        _controls.Margin = new Thickness(4, 0, 0, 0);
        DockPanel.SetDock(_controls, Dock.Right);
        layout.Children.Add(_controls);
        layout.Children.Add(text);
        Child = layout;

        MouseEnter += (_, _) => { _hovering = true; RefreshFace(); };
        MouseLeave += (_, _) => { _hovering = false; RefreshFace(); };
        Update(video);
    }

    public void Update(BrowserVideo video)
    {
        _video = video;
        _title.Text = video.Title;
        _title.ToolTip = video.Title;
        _subtitle.Text = string.Join(" \u00B7 ", new[] { video.Channel, video.Browser }.Where(s => s.Length > 0));
        _bars.Visibility = video.IsPlaying ? Visibility.Visible : Visibility.Collapsed;
        _timeline.Visibility = video.Duration > 0 ? Visibility.Visible : Visibility.Collapsed;
        _seek.Update(video.Progress);
        _time.Text = $"{Format.Duration(video.Current)} / {Format.Duration(video.Duration)}";
        _progress.Width = 96 * video.Progress;
        _progress.Visibility = video.Duration > 0 ? Visibility.Visible : Visibility.Collapsed;
        _pausedBadge.Visibility = !video.IsPlaying && video.Controllable ? Visibility.Visible : Visibility.Collapsed;
        ToolTip = null;
        _thumb.ToolTip = video.Controllable ? "Watch in the notch" : null;
        if (video.VideoId != _thumbId)
        {
            _thumbId = video.VideoId;
            _thumb.SetResourceReference(BackgroundProperty, ThemeResources.Hairline);
            var wanted = _thumbId;
            VideoThumbnails.Into(video.ThumbnailUrl, image =>
            {
                if (_thumbId == wanted) _thumb.Background = new ImageBrush(image) { Stretch = Stretch.UniformToFill };
            });
        }
        RefreshControls();
        RefreshFace();
    }

    private void RefreshControls()
    {
        bool? key = _video.Controllable ? _video.IsPlaying : null;
        if (key == _controlsPlaying && _controls.Content != null) return;
        _controlsPlaying = key;
        if (!_video.Controllable)
        {
            _controls.Content = null;
            return;
        }
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        void Add(Button b)
        {
            b.VerticalAlignment = VerticalAlignment.Center;
            b.Margin = new Thickness(row.Children.Count == 0 ? 0 : 6, 0, 0, 0);
            row.Children.Add(b);
        }
        Add(Ui.IconButton("\uEB9E", 26, () => _browser.Seek(_video, -10), tooltip: "Back 10s"));
        Add(Ui.IconButton(_video.IsPlaying ? "\uE769" : "\uE768", 34, () => _browser.Toggle(_video), prominent: true,
            tooltip: _video.IsPlaying ? "Pause" : "Play"));
        Add(Ui.IconButton("\uEB9D", 26, () => _browser.Seek(_video, 10), tooltip: "Forward 10s"));
        Add(Ui.IconButton("\uE893", 26, () => _browser.Next(_video), tooltip: "Next video"));
        _controls.Content = row;
    }

    private void RefreshFace()
    {
        var accent = ((SolidColorBrush)Application.Current.FindResource(ThemeResources.Accent)).Color;
        var text = ((SolidColorBrush)Application.Current.FindResource(ThemeResources.Text)).Color;
        var border = ((SolidColorBrush)Application.Current.FindResource(ThemeResources.Border)).Color;
        Background = (_video.IsPlaying ? accent.WithOpacity(0.1) : text.WithOpacity(_hovering ? 0.06 : 0.03)).Brush();
        BorderBrush = (_video.IsPlaying ? accent.WithOpacity(0.55) : border.WithOpacity(0.5)).Brush();
    }
}

/// Shown in Now Playing while a Chromium browser is open but its extension isn't talking to Purrch: one click
/// leads to Settings, where the user decides whether to connect. Dismissed until Purrch restarts.
internal sealed class BrowserBanner : Border
{
    public static bool Dismissed { get; private set; }

    /// Worth showing: a browser window is open, nothing is connected, and it wasn't dismissed.
    public static bool Wanted =>
        !Dismissed && !BrowserMediaService.Shared.IsConnected && BrowserMediaService.Shared.ChromiumBrowserOpen;

    private static readonly Color Orange = Color.FromRgb(0xFF, 0x9F, 0x0A);

    public BrowserBanner(Action dismissed, bool compact = false)
    {
        CornerRadius = new CornerRadius(12);
        Padding = compact ? new Thickness(10, 6, 6, 6) : new Thickness(10);
        Background = Orange.WithOpacity(0.12).Brush();
        BorderBrush = Orange.WithOpacity(0.35).Brush();
        BorderThickness = new Thickness(0.8);

        var registered = BrowserSetup.Current == BrowserSetup.State.Registered;
        var title = registered ? "Load Purrch's extension in your browser" : "Control YouTube in Chrome or Edge from the pill";
        var layout = new DockPanel();
        var icon = Ui.Glyph("\uE714", compact ? 13 : 16);
        icon.Foreground = Orange.Brush();
        icon.Width = 22;
        icon.VerticalAlignment = compact ? VerticalAlignment.Center : VerticalAlignment.Top;
        DockPanel.SetDock(icon, Dock.Left);
        layout.Children.Add(icon);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        actions.Children.Add(Ui.Pill(registered ? "Show steps" : "Set up", "\uE713",
            () => SettingsView.Open(SettingsView.Section.Behaviour), prominent: true));
        var close = Ui.Bare(Ui.Glyph("\uE711", 10, ThemeResources.SecondaryText), () =>
        {
            Dismissed = true;
            dismissed();
        }, "Not now");
        close.Margin = new Thickness(10, 0, 4, 0);
        close.VerticalAlignment = VerticalAlignment.Center;
        actions.Children.Add(close);
        DockPanel.SetDock(actions, Dock.Right);
        layout.Children.Add(actions);

        var text = new StackPanel { Margin = new Thickness(8, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        var heading = Ui.Text(title, 12, weight: FontWeights.SemiBold);
        heading.TextWrapping = TextWrapping.NoWrap;
        heading.TextTrimming = TextTrimming.CharacterEllipsis;
        text.Children.Add(heading);
        if (!compact)
        {
            text.Children.Add(Step(1, registered ? "Settings shows where the extension is and how to load it." : "Click Set up, then Connect browser in Settings."));
            text.Children.Add(Step(2, "Videos then get controls and a live picture in the notch."));
        }
        layout.Children.Add(text);
        Child = layout;
    }

    private static UIElement Step(int number, string text)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
        var badge = new Border
        {
            Width = 14,
            Height = 14,
            CornerRadius = new CornerRadius(7),
            Background = Orange.Brush(),
            Child = Ui.Text(number.ToString(), 9, ThemeResources.Background, FontWeights.Bold),
        };
        ((TextBlock)badge.Child).HorizontalAlignment = HorizontalAlignment.Center;
        row.Children.Add(badge);
        var label = Ui.Text(text, 11, ThemeResources.SecondaryText);
        label.Margin = new Thickness(6, 0, 0, 0);
        row.Children.Add(label);
        return row;
    }
}
