using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Purrch.Core;
using Purrch.DesignSystem;
using Purrch.Features.Media;

namespace Purrch.Interface;

/// The pill as a small live window onto a browser video (Settings → Behaviour → Video on hover).
public sealed class VideoPeekView : Grid
{
    private readonly BrowserMediaService _browser = BrowserMediaService.Shared;
    private readonly NotchState _state = NotchState.Shared;
    private readonly StackPanel _badge = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _cycle;
    private readonly Grid _body = new() { Margin = new Thickness(12, 8, 12, 12), RenderTransformOrigin = new Point(0.5, 0) };
    private readonly ScaleTransform _bodyScale = new(1, 1);
    private readonly Grid _header = new() { Height = Math.Max(NotchState.PillSize.Height, 28), Margin = new Thickness(14, 0, 14, 0) };
    private LiveVideoPlayer? _player;
    private string? _videoId;

    public VideoPeekView()
    {
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition());

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        _cycle = Ui.IconButton("\uE8AB", 24, Cycle, tooltip: "Next open video");
        _cycle.Margin = new Thickness(0, 0, 6, 0);
        buttons.Children.Add(_cycle);
        var gear = Ui.IconButton("\uE713", 24, () => { if (_player != null) _player.ShowQuality = !_player.ShowQuality; }, tooltip: "Quality");
        gear.Margin = new Thickness(0, 0, 6, 0);
        buttons.Children.Add(gear);
        buttons.Children.Add(Ui.IconButton("\uE8A9", 24, () => _state.ExpandToFullPanel(ModuleId.NowPlaying), tooltip: "All tools"));
        _header.Children.Add(_badge);
        _header.Children.Add(buttons);
        Children.Add(_header);

        _body.RenderTransform = _bodyScale;
        SetRow(_body, 1);
        Children.Add(_body);

        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible)
            {
                // Opens on the video that is playing.
                _videoId = (_browser.PlayingVideo ?? _browser.Videos.FirstOrDefault())?.Id;
                _browser.Changed += Refresh;
                Refresh();
            }
            else
            {
                _browser.Changed -= Refresh;
            }
        };
    }

    private BrowserVideo? Video =>
        _browser.Videos.FirstOrDefault(v => v.Id == _videoId) ?? _browser.PlayingVideo ?? _browser.Videos.FirstOrDefault();

    /// Fades in and grows from slightly smaller, anchored at the top, as the pill opens.
    public void PlayEntrance()
    {
        foreach (var part in new UIElement[] { _header, _body })
        {
            part.Opacity = 0;
            part.Animate(OpacityProperty, 1, Motion.ExpoOut(0.5), delay: 0.05);
        }
        _bodyScale.ScaleX = _bodyScale.ScaleY = 0.94;
        _bodyScale.Animate(ScaleTransform.ScaleXProperty, 1, Motion.ExpoOut(0.5), delay: 0.05);
        _bodyScale.Animate(ScaleTransform.ScaleYProperty, 1, Motion.ExpoOut(0.5), delay: 0.05);
    }

    private void Refresh()
    {
        var video = Video;
        _cycle.Visibility = _browser.Videos.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        if (video == null)
        {
            _player = null;
            _badge.Children.Clear();
            _badge.Tag = null;
            _body.Children.Clear();
            _body.Children.Add(Ui.EmptyState("\uE714", "No YouTube video open", "Play one in Chrome, Edge, Brave or Vivaldi"));
            return;
        }
        VideoTheaterView.Badge(_badge, video);
        if (_player == null)
        {
            _body.Children.Clear();
            _player = new LiveVideoPlayer(video, onToggleSize: () =>
            {
                var id = _player?.Video.Id;
                _state.ExpandToFullPanel(ModuleId.NowPlaying);
                if (id != null) _state.EnterTheater(id);
            })
            {
                VerticalAlignment = VerticalAlignment.Top,
            };
            _body.Children.Add(_player);
        }
        else
        {
            _player.Update(video);
        }
    }

    private void Cycle()
    {
        var list = _browser.Videos;
        var i = list.ToList().FindIndex(v => v.Id == Video?.Id);
        if (i < 0) return;
        _videoId = list[(i + 1) % list.Count].Id;
        Refresh();
    }
}

/// A browser video filling the whole expanded panel. Esc or Exit returns to the panel.
public sealed class VideoTheaterView : DockPanel
{
    private readonly BrowserMediaService _browser = BrowserMediaService.Shared;
    private readonly NotchState _state = NotchState.Shared;
    private readonly StackPanel _badge = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
    private LiveVideoPlayer? _player;

    public VideoTheaterView()
    {
        var header = new Grid { Height = Math.Max(NotchState.PillSize.Height, 28), Margin = new Thickness(18, 0, 18, 0) };
        header.Children.Add(_badge);
        var exit = Ui.Pill("Exit", "\uE73F", _state.ExitTheater);
        exit.HorizontalAlignment = HorizontalAlignment.Right;
        exit.VerticalAlignment = VerticalAlignment.Center;
        exit.ToolTip = "Back to Now Playing (Esc)";
        header.Children.Add(exit);
        SetDock(header, Dock.Top);
        Children.Add(header);

        Loaded += (_, _) =>
        {
            _browser.Changed += Refresh;
            Refresh();
        };
        Unloaded += (_, _) => _browser.Changed -= Refresh;
    }

    private void Refresh()
    {
        // The tab closing ends theater (the service does that); nothing to show meanwhile.
        if (_browser.Videos.FirstOrDefault(v => v.Id == _state.TheaterVideoId) is not { } video) return;
        Badge(_badge, video);
        if (_player == null)
        {
            _player = new LiveVideoPlayer(video, onToggleSize: _state.ExitTheater, isTheater: true)
            {
                Margin = new Thickness(12, 6, 12, 12),
                VerticalAlignment = VerticalAlignment.Top,
            };
            Children.Add(_player);
        }
        else
        {
            _player.Update(video);
        }
    }

    /// Red video icon, the browser's name, and bars while it plays.
    internal static void Badge(StackPanel badge, BrowserVideo video)
    {
        // Rebuilt only when it changes, so the bars keep moving smoothly between reports.
        var key = $"{video.Browser}|{video.IsPlaying}";
        if (badge.Tag as string == key) return;
        badge.Tag = key;
        badge.Children.Clear();
        var icon = Ui.Glyph("\uE714", 13);
        icon.Foreground = Color.FromRgb(0xFF, 0x3B, 0x30).Brush();
        badge.Children.Add(icon);
        var name = Ui.Text(video.Browser, 12, weight: FontWeights.SemiBold);
        name.Margin = new Thickness(8, 0, 8, 0);
        badge.Children.Add(name);
        if (video.IsPlaying) badge.Children.Add(new MusicBars { Width = 14, Height = 11, VerticalAlignment = VerticalAlignment.Center });
    }
}
