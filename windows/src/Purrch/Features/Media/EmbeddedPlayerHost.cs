using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Purrch.Core;

namespace Purrch.Features.Media;

/// Plays the same YouTube video as the browser tab, muted, inside the pill, and keeps it in step with the tab
/// (position and play / pause). This gives smooth, full-frame-rate video; sound stays in the browser. When the
/// video can't be embedded, or WebView2 isn't installed, players fall back to the extension's frames.
///
/// There is one player for the whole app. It is a <see cref="WebView2CompositionControl"/>: the pill window
/// is layered (AllowsTransparency), where the ordinary WebView2, a child window, doesn't draw at all, while
/// the composition control draws into WPF itself, so it fades, clips to rounded corners and lets WPF
/// controls sit on top. It lives on a layer under the panel's content (installed by the pill window) and is
/// never moved between parents: a player on screen leaves a transparent hole, and the web view is placed
/// under that hole every frame. Otherwise it is parked, invisible but still playing, for a quick next open.
public sealed class EmbeddedPlayerHost
{
    public static EmbeddedPlayerHost Shared { get; } = new();

    /// After this long out of sight the hidden player pauses instead of following the tab.
    private const double KeepWarm = 300;
    /// Size used while parked, so YouTube still picks a sharp quality.
    private static readonly Size HiddenSize = new(1280, 720);

    /// The loaded video has frames on screen.
    public string? ReadyVideoId { get; private set; }
    /// The video can't be embedded (the owner disabled it, or the player never loaded), or WebView2 is missing.
    public string? FailedVideoId { get; private set; }
    public string? VideoId { get; private set; }
    /// Ready or failed changed. Raised on the UI thread.
    public event Action? Changed;

    private Canvas? _layer;
    private WebView2CompositionControl? _view;
    /// Holds the web view and does its placing, fading and clipping (the control hides its own Opacity).
    private readonly Border _host = new() { IsHitTestVisible = false, Focusable = false };
    /// No WebView2 runtime, or it wouldn't start: every video uses frames.
    private bool _unavailable;
    private string? _pendingUrl;
    /// The player area the web view sits under, and its corner radius.
    private FrameworkElement? _slot;
    private double _radius;
    private Size _shownSize;
    private DateTime? _detachedAt = DateTime.UtcNow;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(0.5) };
    private DateTime? _loadDeadline;
    private DateTime _lastSeek = DateTime.MinValue;
    private bool _following;
    private bool _prewarming;
    /// Last position the browser reported for the loaded video, and when it arrived.
    private (double Time, DateTime At)? _reported;

    private EmbeddedPlayerHost()
    {
        _timer.Tick += (_, _) => Sync();
    }

    private static string PlayerFolder => Path.Combine(AppPaths.Root, "player");

    /// The layer under the pill window's content where the web view lives. Called once by the window.
    public void Install(Canvas layer) => _layer = layer;

    /// Load the playing browser video in advance, so the first open is instant too.
    public void StartPrewarming()
    {
        if (_prewarming) return;
        _prewarming = true;
        var service = BrowserMediaService.Shared;
        service.Changed += () =>
        {
            NotePosition(service.Videos);
            if (!WantsVideo) return;
            var video = service.Videos.FirstOrDefault(v => v.IsPlaying && v.Controllable);
            if (video?.VideoId is { } id && id != VideoId) Prepare(id, video.Current);
        };
    }

    private static bool WantsVideo =>
        AppSettings.Shared.NowPlayingMode == MediaViewMode.Video || AppSettings.Shared.VideoPeekOnHover;

    private void NotePosition(IReadOnlyList<BrowserVideo> videos)
    {
        if (VideoId == null || videos.FirstOrDefault(v => v.VideoId == VideoId) is not { } video) return;
        if (_reported?.Time != video.Current) _reported = (video.Current, DateTime.UtcNow);
    }

    /// Where the tab's video is now. The frame stream is the most precise source while a player is on screen;
    /// otherwise extrapolate from the last reported position.
    private double? BrowserTime(bool playing)
    {
        var service = BrowserMediaService.Shared;
        if (_slot != null && (DateTime.UtcNow - service.StreamTimeAt).TotalSeconds < 1.5) return service.EstimatedTime(playing);
        if (_reported is not { } reported) return null;
        return reported.Time + (playing ? (DateTime.UtcNow - reported.At).TotalSeconds : 0);
    }

    public void Prepare(string videoId, double? start = null)
    {
        if (videoId == VideoId) return;
        VideoId = videoId;
        _reported = null;
        if (_slot == null) _detachedAt = DateTime.UtcNow;
        ReadyVideoId = null;
        FailedVideoId = null;
        _loadDeadline = DateTime.UtcNow.AddSeconds(15);
        _lastSeek = DateTime.MinValue;
        var at = start ?? BrowserMediaService.Shared.Videos.FirstOrDefault(v => v.VideoId == videoId)?.Current ?? 0;
        if (EnsureView() == null)
        {
            FailedVideoId = videoId;
            Changed?.Invoke();
            return;
        }
        Navigate($"https://purrch.app/player.html?v={Uri.EscapeDataString(videoId)}&t={(int)Math.Max(0, at)}");
        StartTimer();
        Changed?.Invoke();
    }

    /// A player came on screen: put the web view under its picture area.
    public void Attach(FrameworkElement slot, double cornerRadius)
    {
        if (_view == null) return;
        _slot = slot;
        _radius = cornerRadius;
        _shownSize = default;
        _detachedAt = null;
        if (!_following)
        {
            _following = true;
            CompositionTarget.Rendering += Follow;
        }
        StartTimer();
        // Let the frame stream report a fresh position first, then catch up straight away.
        _lastSeek = DateTime.MinValue;
        var settle = new DispatcherTimer { Interval = TimeSpan.FromSeconds(0.4) };
        settle.Tick += (_, _) =>
        {
            settle.Stop();
            Sync();
        };
        settle.Start();
    }

    public void Detach(FrameworkElement slot)
    {
        // A newer player may already have taken it (e.g. while switching into theater).
        if (_slot != slot || _view == null) return;
        _slot = null;
        _detachedAt = DateTime.UtcNow;
        Park();
    }

    private WebView2CompositionControl? EnsureView()
    {
        if (_unavailable || _layer == null) return null;
        if (_view != null) return _view;
        try
        {
            CoreWebView2Environment.GetAvailableBrowserVersionString();
        }
        catch (Exception e) when (e is WebView2RuntimeNotFoundException or COMException or DllNotFoundException
                                       or BadImageFormatException)
        {
            // Windows 10 without the WebView2 Runtime (Windows 11 always has it).
            Log.Write($"Embedded player: WebView2 unavailable, using browser frames: {e.Message}");
            _unavailable = true;
            return null;
        }
        WritePage();
        _view = new WebView2CompositionControl
        {
            IsHitTestVisible = false,
            Focusable = false,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            DefaultBackgroundColor = System.Drawing.Color.Black,
            CreationProperties = new CoreWebView2CreationProperties
            {
                // Cache and cookies stay on this PC, not in the roaming profile.
                UserDataFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Purrch", "WebView2"),
                AdditionalBrowserArguments = "--autoplay-policy=no-user-gesture-required",
            },
        };
        _view.CoreWebView2InitializationCompleted += OnInitialized;
        _host.Child = _view;
        _layer.Children.Add(_host);
        Park();
        _ = _view.EnsureCoreWebView2Async();
        return _view;
    }

    private void OnInitialized(object? sender, CoreWebView2InitializationCompletedEventArgs e)
    {
        if (!e.IsSuccess || _view?.CoreWebView2 is not { } core)
        {
            Log.Write($"Embedded player: WebView2 didn't start, using browser frames: {e.InitializationException?.Message}");
            _unavailable = true;
            Fail();
            return;
        }
        var settings = core.Settings;
        settings.AreDefaultContextMenusEnabled = false;
        settings.AreDevToolsEnabled = false;
        settings.IsStatusBarEnabled = false;
        settings.IsZoomControlEnabled = false;
        settings.AreBrowserAcceleratorKeysEnabled = false;
        // Sound stays in the browser; the page mutes YouTube too.
        core.IsMuted = true;
        core.SetVirtualHostNameToFolderMapping("purrch.app", PlayerFolder, CoreWebView2HostResourceAccessKind.Allow);
        core.WebMessageReceived += OnMessage;
        if (_pendingUrl is { } url)
        {
            _pendingUrl = null;
            core.Navigate(url);
        }
    }

    private void Navigate(string url)
    {
        if (_view?.CoreWebView2 is { } core) core.Navigate(url);
        else _pendingUrl = url;
    }

    /// Off screen but still in the window, invisible: a web view that isn't shown would stop playing.
    private void Park()
    {
        _host.RenderTransform = Transform.Identity;
        _host.Clip = null;
        _host.Width = HiddenSize.Width;
        _host.Height = HiddenSize.Height;
        _host.Opacity = 0;
    }

    /// Every frame while a player shows it: sit exactly under the player's picture area, at its opacity.
    private void Follow(object? sender, EventArgs e)
    {
        if (_view == null || _layer == null || _slot == null)
        {
            CompositionTarget.Rendering -= Follow;
            _following = false;
            return;
        }
        var size = _slot.RenderSize;
        if (!_slot.IsVisible || size.Width < 1 || size.Height < 1)
        {
            _host.Opacity = 0;
            return;
        }
        Matrix matrix;
        try
        {
            if (_slot.TransformToVisual(_layer) is not Transform transform) return;
            matrix = transform.Value;
        }
        catch (InvalidOperationException)
        {
            return; // not in the window (yet)
        }
        if (size != _shownSize)
        {
            _shownSize = size;
            _host.Width = size.Width;
            _host.Height = size.Height;
            _host.Clip = new RectangleGeometry(new Rect(size), _radius, _radius);
        }
        if (_host.RenderTransform is not MatrixTransform current || current.Matrix != matrix)
            _host.RenderTransform = new MatrixTransform(matrix);
        _host.Opacity = EffectiveOpacity(_slot);
    }

    /// The slot's opacity including its fading parents, up to the panel area the layer shares with it.
    private double EffectiveOpacity(DependencyObject slot)
    {
        var stop = _layer == null ? null : VisualTreeHelper.GetParent(_layer);
        var opacity = 1.0;
        for (var e = slot; e != null && e != stop; e = VisualTreeHelper.GetParent(e))
            if (e is UIElement element) opacity *= element.Opacity;
        return opacity;
    }

    private void StartTimer()
    {
        if (!_timer.IsEnabled) _timer.Start();
    }

    /// Nudge the embedded player toward the tab: same play state, and close to the same position. Small drift
    /// is closed by playing slightly faster or slower; only a large gap seeks, and rarely, because every seek
    /// re-buffers and would show up as a stutter.
    private async void Sync()
    {
        if (VideoId is not { } id || FailedVideoId == id) return;
        if (ReadyVideoId != id)
        {
            if (_loadDeadline is { } deadline && DateTime.UtcNow > deadline) Fail();
            // YouTube sometimes drops the autoplay request and sits unstarted; ask again.
            await Run("window.purrchKick && purrchKick()");
            return;
        }
        var video = BrowserMediaService.Shared.Videos.FirstOrDefault(v => v.VideoId == id);
        if (_detachedAt is { } detached && (DateTime.UtcNow - detached).TotalSeconds > KeepWarm)
        {
            // Out of sight for a while: pause and rest until a player shows it again.
            await Run("window.purrchSync && purrchSync(0, false, false, true)");
            _timer.Stop();
            return;
        }
        var playing = video?.IsPlaying ?? false;
        // Track the position while hidden too, so opening the panel never needs a visible seek.
        var target = BrowserTime(playing);
        var live = (video?.Duration ?? 0) == 0 || target == null;
        var canSeek = (DateTime.UtcNow - _lastSeek).TotalSeconds > 8;
        var script = string.Format(CultureInfo.InvariantCulture, "window.purrchSync && purrchSync({0:0.###}, {1}, {2}, {3})",
            target ?? 0, Js(playing), Js(canSeek), Js(live));
        if (await Run(script) == "\"seek\"") _lastSeek = DateTime.UtcNow;
    }

    private static string Js(bool value) => value ? "true" : "false";

    private async Task<string?> Run(string script)
    {
        if (_view?.CoreWebView2 is not { } core) return null;
        try { return await core.ExecuteScriptAsync(script); }
        catch (Exception e) when (e is InvalidOperationException or COMException)
        {
            return null; // navigating, or the browser process restarted
        }
    }

    private void Fail()
    {
        if (VideoId is not { } id || FailedVideoId == id) return;
        FailedVideoId = id;
        Changed?.Invoke();
    }

    private void OnMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        string body;
        try { body = e.TryGetWebMessageAsString(); }
        catch (ArgumentException) { return; }
        if (body.StartsWith("ready:", StringComparison.Ordinal))
        {
            if (body[6..] != VideoId) return;
            ReadyVideoId = VideoId;
            Changed?.Invoke();
        }
        else if (body.StartsWith("error", StringComparison.Ordinal))
        {
            // 2, 5, 100, 101, 150, 153: bad id, HTML5 error, removed, embedding disabled, config error.
            Fail();
        }
    }

    /// The player page, served from %APPDATA%\Purrch\player as https://purrch.app/player.html, so YouTube sees
    /// a real origin and referrer (it refuses to play without one). Written each time WebView2 starts, so an
    /// update of Purrch brings the new page.
    private static void WritePage()
    {
        try
        {
            Directory.CreateDirectory(PlayerFolder);
            File.WriteAllText(Path.Combine(PlayerFolder, "player.html"), Page);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Write($"Embedded player page not written: {e.Message}");
        }
    }

    private const string Page = """
        <!doctype html><html><head><meta charset="utf-8"><meta name="viewport" content="width=device-width">
        <style>html,body{margin:0;height:100%;background:#000;overflow:hidden}
        /* The player is taller than the view: YouTube's title bar and end-screen strip land in the
           letterbox above and below the picture, which is cropped away. */
        #p,iframe{position:fixed;left:0;top:-70px;width:100%;height:calc(100% + 140px);border:0;pointer-events:none}</style>
        </head><body><div id="p"></div>
        <script>
        var query=new URLSearchParams(location.search),id=query.get('v')||'',start=parseInt(query.get('t')||'0',10)||0;
        function post(m){window.chrome.webview.postMessage(m)}
        var player;
        function onYouTubeIframeAPIReady(){
          player=new YT.Player('p',{videoId:id,
            playerVars:{autoplay:1,mute:1,controls:0,disablekb:1,fs:0,rel:0,iv_load_policy:3,playsinline:1,cc_load_policy:0,
                        modestbranding:1,start:start,origin:'https://purrch.app'},
            events:{onReady:function(e){e.target.mute();e.target.playVideo()},
                    onStateChange:function(e){if(e.data===1||e.data===2||e.data===0)reveal()},
                    onError:function(e){post('error:'+e.data)}}});
        }
        // "Ready" only once real frames are on screen; until then Purrch keeps showing the thumbnail
        // instead of YouTube's black loading screen.
        var revealed=false;
        function reveal(){if(revealed)return;revealed=true;setTimeout(function(){post('ready:'+id)},250)}
        function rate(r){var a=player.getAvailablePlaybackRates?player.getAvailablePlaybackRates():[1];
          if(a.indexOf(r)<0)r=1;if(player.getPlaybackRate()!==r)player.setPlaybackRate(r)}
        window.purrchKick=function(){
          if(!player||!player.getPlayerState)return;
          var s=player.getPlayerState();if(s===-1||s===5){player.mute();player.playVideo()}
        };
        window.purrchSync=function(t,play,seek,live){
          if(!player||!player.getPlayerState)return '';
          var s=player.getPlayerState();
          if(!play){rate(1);if(s===1)player.pauseVideo();return ''}
          if(s===3)return '';
          if(s!==1){player.playVideo();return ''}
          if(live){rate(1);return ''}
          var d=t-player.getCurrentTime(),a=Math.abs(d);
          // Seek a little ahead: the browser keeps playing while this player buffers.
          if(a>2.5&&a<600&&seek){rate(1);player.seekTo(t+0.6,true);return 'seek'}
          rate(a<0.3?1:(d>0?1.25:0.75));return '';
        };
        </script>
        <script src="https://www.youtube.com/iframe_api"></script>
        </body></html>
        """;
}
