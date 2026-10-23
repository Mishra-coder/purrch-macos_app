using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Purrch.Core;

namespace Purrch.Features.Media;

/// The YouTube tabs open in Chrome, Edge, Brave, Vivaldi and Chromium, as Purrch's extension reports them,
/// and their controls, so several videos can be handled from the pill. The Mac app reads the same through
/// Apple Events; on Windows the extension pushes changes as they happen (see <see cref="BrowserBridge"/>).
public sealed class BrowserMediaService
{
    public static BrowserMediaService Shared { get; } = new();

    private static readonly string[] ChromiumProcesses = { "chrome", "msedge", "brave", "vivaldi" };

    private readonly BrowserBridge _bridge = BrowserBridge.Shared;
    /// Each connection's tabs, as last reported, in the order the browser listed them.
    private readonly Dictionary<int, List<BrowserVideo>> _byConnection = new();
    /// Commands on their way, per video: a report from before the command keeps the optimistic state, until
    /// the page acknowledges it (its seq catches up) or a few seconds pass.
    private readonly Dictionary<string, (long Seq, DateTime At)> _pending = new();
    /// Command numbers. Seeded from the clock so they keep rising across restarts of Purrch.
    private long _seq = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    private string? _artworkVideoId;
    private bool _started;
    private DateTime _browsersCheckedAt = DateTime.MinValue;
    private bool _browserRunning;

    /// Videos, play state or the connection changed. Raised on the UI thread.
    public event Action? Changed;

    public IReadOnlyList<BrowserVideo> Videos { get; private set; } = Array.Empty<BrowserVideo>();
    public bool AnyPlaying => Videos.Any(v => v.IsPlaying);
    public BrowserVideo? PlayingVideo => Videos.FirstOrDefault(v => v.IsPlaying);
    /// Thumbnail of the video that is playing, for the closed pill's live activity.
    public ImageSource? PlayingArtwork { get; private set; }
    /// At least one browser has the extension connected.
    public bool IsConnected => _bridge.Connections.Count > 0;

    private BrowserMediaService()
    {
        // The hub prefers the source registered first within a kind; music from the system media controls
        // should win over a browser video, as on the Mac, whichever is touched first.
        _ = NowPlayingService.Shared;
        LiveActivityHub.Shared.Register(() =>
            PlayingVideo is { } video ? new LiveActivity(LiveActivityKind.Music, video.Title, Artwork: PlayingArtwork) : null);
    }

    public void Start()
    {
        if (_started) return;
        _started = true;
        _bridge.MessageReceived += OnMessage;
        _bridge.Changed += OnConnectionsChanged;
        _bridge.Start();
    }

    /// A Chromium browser has a window open (so the extension could be helping), checked at most every few
    /// seconds. Edge often runs in the background with no window; that doesn't count.
    public bool ChromiumBrowserOpen
    {
        get
        {
            if ((DateTime.UtcNow - _browsersCheckedAt).TotalSeconds < 4) return _browserRunning;
            _browsersCheckedAt = DateTime.UtcNow;
            _browserRunning = false;
            foreach (var name in ChromiumProcesses)
            {
                foreach (var process in Process.GetProcessesByName(name))
                {
                    using (process)
                    {
                        try { _browserRunning |= process.MainWindowHandle != IntPtr.Zero; }
                        catch (InvalidOperationException) { /* exited meanwhile */ }
                    }
                }
                if (_browserRunning) break;
            }
            return _browserRunning;
        }
    }

    /// Asks every browser for its list again (when Now Playing opens).
    public void Refresh()
    {
        foreach (var connection in _bridge.Connections) _bridge.Send(connection.Id, new { type = "refresh" });
    }

    private void OnConnectionsChanged()
    {
        var live = _bridge.Connections.Select(c => c.Id).ToHashSet();
        foreach (var gone in _byConnection.Keys.Where(id => !live.Contains(id)).ToList()) _byConnection.Remove(gone);
        // A browser that just said which it is: relabel its videos.
        foreach (var connection in _bridge.Connections)
        {
            if (_byConnection.TryGetValue(connection.Id, out var list) && list.Any(v => v.Browser != connection.Browser))
                _byConnection[connection.Id] = list.Select(v => v with { Browser = connection.Browser }).ToList();
        }
        Publish(force: true);
    }

    /// Runs on the pipe's thread: frames are decoded here so the UI thread only swaps a picture.
    private void OnMessage(BrowserBridge.Connection connection, JsonElement message)
    {
        switch (Str(message, "type"))
        {
            case "videos":
                OnUi(() => ApplyList(connection, message));
                break;
            case "frame":
                var image = DecodeFrame(Str(message, "img"));
                OnUi(() => ApplyFrame(connection, message, image));
                break;
        }
    }

    private static void OnUi(Action action) => Application.Current?.Dispatcher.BeginInvoke(action);

    private void ApplyList(BrowserBridge.Connection connection, JsonElement message)
    {
        if (!message.TryGetProperty("videos", out var items) || items.ValueKind != JsonValueKind.Array) return;
        var old = Videos.ToDictionary(v => v.Id);
        var list = new List<BrowserVideo>();
        foreach (var item in items.EnumerateArray())
        {
            var video = new BrowserVideo
            {
                Connection = connection.Id,
                Browser = connection.Browser,
                TabId = Int(item, "tab"),
                WindowId = Int(item, "window"),
                Url = Str(item, "url"),
                Title = Str(item, "title"),
                Channel = Str(item, "channel"),
                IsPlaying = Bool(item, "playing"),
                Current = Num(item, "current"),
                Duration = Num(item, "duration"),
                Controllable = Bool(item, "controllable"),
            };
            if (old.TryGetValue(video.Id, out var known) && IsStale(video.Id, (long)Num(item, "seq")))
                video = video with { IsPlaying = known.IsPlaying, Current = known.Current };
            list.Add(video);
        }
        _byConnection[connection.Id] = list;
        Publish();
    }

    /// True while a command sent to this video hasn't shown up in what the page reports.
    private bool IsStale(string id, long reportedSeq)
    {
        if (!_pending.TryGetValue(id, out var pending)) return false;
        if (reportedSeq >= pending.Seq || (DateTime.UtcNow - pending.At).TotalSeconds > 3)
        {
            _pending.Remove(id);
            return false;
        }
        return true;
    }

    /// Tells listeners when the list changed (or `force`, for connection and quality changes).
    private void Publish(bool force = false)
    {
        var videos = _byConnection.OrderBy(p => p.Key).SelectMany(p => p.Value).ToList();
        var changed = !videos.SequenceEqual(Videos);
        if (changed)
        {
            Videos = videos;
            // The tab being watched in theater closed.
            if (NotchState.Shared.TheaterVideoId is { } theater && videos.All(v => v.Id != theater))
                NotchState.Shared.ExitTheater();
            if (_streamingId != null && videos.All(v => v.Id != _streamingId)) _streamingId = null;
            UpdateArtwork();
        }
        if (!changed && !force) return;
        Changed?.Invoke();
        LiveActivityHub.Shared.Refresh();
    }

    private void Update(string id, Func<BrowserVideo, BrowserVideo> change, bool force = false)
    {
        foreach (var list in _byConnection.Values)
        {
            var i = list.FindIndex(v => v.Id == id);
            if (i >= 0) list[i] = change(list[i]);
        }
        Publish(force);
    }

    // MARK: - Controls

    public void Toggle(BrowserVideo video) =>
        Execute(video, "toggle", null, v => v with { IsPlaying = !v.IsPlaying });

    public void Pause(BrowserVideo video) =>
        Execute(video, "pause", null, v => v with { IsPlaying = false });

    public void PauseAll()
    {
        foreach (var video in Videos.Where(v => v.IsPlaying).ToList()) Pause(video);
    }

    public void Seek(BrowserVideo video, double by) =>
        Execute(video, "seekBy", by, v => v with { Current = Math.Max(0, v.Current + by) });

    public void SeekToFraction(BrowserVideo video, double fraction)
    {
        var target = Math.Clamp(fraction, 0, 1) * video.Duration;
        Execute(video, "seekTo", target, v => v with { Current = target });
    }

    public void Next(BrowserVideo video) => Execute(video, "next", null);

    public void SetYouTubeQuality(BrowserVideo video, string level)
    {
        YouTubeQuality = level;
        Publish(force: true);
        Execute(video, "quality", level);
    }

    /// Brings the video's tab and window to the front, and folds the panel away.
    public void Focus(BrowserVideo video)
    {
        // Purrch has the foreground (the click landed in it), so it may hand it on to the browser.
        Native.AllowSetForegroundWindow(Native.ASFW_ANY);
        _bridge.Send(video.Connection, new { type = "focus", tab = video.TabId });
        NotchState.Shared.Close();
    }

    /// Sends a command, and shows its result straight away instead of waiting for the page to report it.
    private void Execute(BrowserVideo video, string action, object? value, Func<BrowserVideo, BrowserVideo>? optimistic = null)
    {
        if (!video.Controllable) return;
        var seq = ++_seq;
        if (optimistic != null)
        {
            _pending[video.Id] = (seq, DateTime.UtcNow);
            Update(video.Id, optimistic);
        }
        _bridge.Send(video.Connection, new { type = "command", tab = video.TabId, action, value, seq });
    }

    // MARK: - Live frames

    /// The newest frame of the streamed video, for a player that comes on screen mid-stream.
    public ImageSource? LastFrame { get; private set; }
    /// A frame arrived; players show it without rebuilding anything.
    public event Action<ImageSource?>? FrameArrived;
    /// A first frame has arrived for the current stream.
    public bool HasLiveFrame { get; private set; }
    public bool LiveIsPortrait { get; private set; }
    /// YouTube's quality levels for the streamed video (e.g. "hd1080") and the one in use.
    public IReadOnlyList<string> YouTubeQualities { get; private set; } = Array.Empty<string>();
    public string YouTubeQuality { get; private set; } = "";

    private string? _streamingId;
    /// False while the embedded player draws the picture; the stream then carries playback state only.
    public bool WantsImages { get; private set; } = true;
    /// Latest playback position of the streamed video and when it was read, for keeping a mirror in sync.
    public double StreamTime { get; private set; }
    public DateTime StreamTimeAt { get; private set; } = DateTime.MinValue;
    /// Players currently showing the stream.
    private int _viewers;
    /// Bumped per stream so a superseded frame loop stops by itself.
    private int _generation;
    private DateTime _lastStateSync = DateTime.MinValue;
    /// The frame request waiting for an answer (0 when none), and when it went.
    private long _inFlight;
    private long _requests;
    private DateTime _requestedAt;

    /// A player came on screen. Streaming continues while at least one player is showing, so handing the
    /// video from one player to another (e.g. into theater) never drops the stream.
    public void StartFrames(BrowserVideo video)
    {
        _viewers++;
        SwitchFrames(video);
    }

    /// The player on screen now shows a different video.
    public void SwitchFrames(BrowserVideo video)
    {
        if (!video.Controllable || _streamingId == video.Id) return;
        HasLiveFrame = false;
        LastFrame = null;
        FrameArrived?.Invoke(null);
        YouTubeQualities = Array.Empty<string>();
        YouTubeQuality = "";
        _streamingId = video.Id;
        _generation++;
        PumpFrame(video.Id, _generation);
    }

    /// Where the streamed video should be now: the last reading plus the time since, while playing.
    public double EstimatedTime(bool isPlaying)
    {
        if (StreamTimeAt == DateTime.MinValue) return StreamTime;
        return StreamTime + (isPlaying ? (DateTime.UtcNow - StreamTimeAt).TotalSeconds : 0);
    }

    /// Switch the running stream between frames and state-only.
    public void SetStreamImages(bool images)
    {
        if (images == WantsImages) return;
        WantsImages = images;
        if (images) HasLiveFrame = false;
    }

    public void StopFrames()
    {
        _viewers = Math.Max(0, _viewers - 1);
        if (_viewers == 0) _streamingId = null;
    }

    private void PumpFrame(string id, int generation)
    {
        if (_streamingId != id || generation != _generation) return;
        if (Videos.FirstOrDefault(v => v.Id == id) is not { } video)
        {
            _streamingId = null;
            return;
        }
        var quality = AppSettings.Shared.PreviewQuality;
        var request = ++_requests;
        _inFlight = request;
        _requestedAt = DateTime.UtcNow;
        _bridge.Send(video.Connection, new
        {
            type = "frame",
            tab = video.TabId,
            gen = generation,
            width = quality.Width(),
            jpeg = quality.Jpeg(),
            images = WantsImages,
        });
        // No answer (the browser is busy, or the tab is stuck loading): ask again.
        After(2, () =>
        {
            if (_inFlight != request) return;
            _inFlight = 0;
            PumpFrame(id, generation);
        });
    }

    private static ImageSource? DecodeFrame(string dataUrl)
    {
        var comma = dataUrl.IndexOf(',');
        if (comma < 0) return null;
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = new MemoryStream(Convert.FromBase64String(dataUrl[(comma + 1)..]));
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception e) when (e is FormatException or NotSupportedException or IOException or InvalidOperationException)
        {
            return null;
        }
    }

    private void ApplyFrame(BrowserBridge.Connection connection, JsonElement m, ImageSource? image)
    {
        var id = $"{connection.Id}-{Int(m, "tab")}";
        if (_inFlight == 0 || _streamingId != id || Int(m, "gen") != _generation) return;
        _inFlight = 0;
        var generation = _generation;
        var error = Bool(m, "error");
        var hasState = m.TryGetProperty("playing", out _);
        var playing = Bool(m, "playing");
        var current = Num(m, "current");
        if (hasState)
        {
            StreamTime = current;
            StreamTimeAt = DateTime.UtcNow;
        }
        if (image != null)
        {
            LastFrame = image;
            HasLiveFrame = true;
            FrameArrived?.Invoke(image);
        }
        // Playback state for the controls: twice a second is plenty.
        if (hasState && (DateTime.UtcNow - _lastStateSync).TotalSeconds > 0.5)
        {
            _lastStateSync = DateTime.UtcNow;
            var portrait = Num(m, "height") > Num(m, "width");
            var stateChanged = portrait != LiveIsPortrait;
            LiveIsPortrait = portrait;
            if (m.TryGetProperty("qualities", out var levels) && levels.ValueKind == JsonValueKind.Array)
            {
                var list = levels.EnumerateArray().Select(l => l.GetString() ?? "").Where(l => l.Length > 0).ToList();
                if (list.Count > 0 && !list.SequenceEqual(YouTubeQualities))
                {
                    YouTubeQualities = list;
                    stateChanged = true;
                }
            }
            if (Str(m, "quality") is { Length: > 0 } level && level != YouTubeQuality)
            {
                YouTubeQuality = level;
                stateChanged = true;
            }
            var duration = Num(m, "duration");
            var stale = IsStale(id, (long)Num(m, "seq"));
            Update(id, v => v with
            {
                IsPlaying = stale ? v.IsPlaying : playing,
                Current = stale ? v.Current : current,
                Duration = duration > 0 ? duration : v.Duration,
            }, force: stateChanged);
        }
        // A paused picture doesn't change; checking twice a second is enough. Back off when the tab is gone.
        var interval = 1.0 / AppSettings.Shared.PreviewQuality.Fps();
        var wait = error ? 1.0
            : !WantsImages ? 0.25
            : !hasState ? 0.5
            : hasState && !playing ? 0.5
            : Math.Max(0.01, interval - (DateTime.UtcNow - _requestedAt).TotalSeconds);
        After(wait, () => PumpFrame(id, generation));
    }

    private static void After(double seconds, Action action)
    {
        var timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromSeconds(seconds) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            action();
        };
        timer.Start();
    }

    // MARK: - Artwork

    private void UpdateArtwork()
    {
        var video = PlayingVideo;
        if (video?.VideoId == _artworkVideoId) return;
        _artworkVideoId = video?.VideoId;
        PlayingArtwork = null;
        if (video?.ThumbnailUrl is not { } url) return;
        var wanted = _artworkVideoId;
        VideoThumbnails.Into(url, image =>
        {
            if (_artworkVideoId != wanted) return;
            PlayingArtwork = image;
            LiveActivityHub.Shared.Refresh();
        });
    }

    // MARK: - JSON

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static double Num(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;

    private static int Int(JsonElement e, string name) => (int)Num(e, name);

    private static bool Bool(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
}
