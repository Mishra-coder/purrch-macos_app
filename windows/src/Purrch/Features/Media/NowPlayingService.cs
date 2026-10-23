using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Purrch.Core;
using Windows.Media.Control;

namespace Purrch.Features.Media;

/// What is playing anywhere on the PC, through Windows' system media controls: Spotify, a browser tab,
/// the Media Player app, anything that shows up in the volume flyout. Also controls it.
public sealed class NowPlayingService
{
    public static NowPlayingService Shared { get; } = new();

    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _session;
    /// Every session Windows knows about, watched so a player that starts playing can take over.
    private IReadOnlyList<GlobalSystemMediaTransportControlsSession> _watched = Array.Empty<GlobalSystemMediaTransportControlsSession>();
    private TimeSpan _position;
    private DateTimeOffset _positionUpdated;
    private int _refreshGeneration;
    /// Right after a local action the app may still report the old state; ignore it until then.
    private DateTimeOffset _suppressUntil;

    /// Track, play state or artwork changed.
    public event Action? Changed;

    public bool HasTrack => _session != null && Title.Length > 0;
    public bool IsPlaying { get; private set; }
    public string Title { get; private set; } = "";
    public string Artist { get; private set; } = "";
    public string Album { get; private set; } = "";
    public string SourceApp { get; private set; } = "";
    public ImageSource? Artwork { get; private set; }
    /// Track length in seconds; 0 when the app doesn't say (many browser players).
    public double Duration { get; private set; }

    private NowPlayingService()
    {
        LiveActivityHub.Shared.Register(() =>
            IsPlaying && HasTrack ? new LiveActivity(LiveActivityKind.Music, Title, Artwork: Artwork) : null);
    }

    public async void Start()
    {
        if (_manager != null) return;
        try
        {
            _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            _manager.CurrentSessionChanged += (_, _) => OnUi(AttachCurrent);
            _manager.SessionsChanged += (_, _) => OnUi(() => { WatchSessions(); AttachCurrent(); });
            WatchSessions();
            AttachCurrent();
        }
        catch (Exception e)
        {
            // Very old Windows 10 builds, or the media service is disabled.
            Log.Write($"Now Playing unavailable: {e.Message}");
        }
    }

    /// Seconds into the track, extrapolated between the app's (sometimes rare) timeline updates.
    public double Elapsed
    {
        get
        {
            var elapsed = _position.TotalSeconds;
            if (IsPlaying) elapsed += (DateTimeOffset.Now - _positionUpdated).TotalSeconds;
            return Duration > 0 ? Math.Clamp(elapsed, 0, Duration) : Math.Max(0, elapsed);
        }
    }

    private static void OnUi(Action action) => Application.Current?.Dispatcher.BeginInvoke(action);

    private void WatchSessions()
    {
        foreach (var old in _watched) old.PlaybackInfoChanged -= OnAnyPlaybackChanged;
        try { _watched = _manager?.GetSessions().ToList() ?? new List<GlobalSystemMediaTransportControlsSession>(); }
        catch (Exception e)
        {
            Log.Write($"Now Playing sessions failed: {e.Message}");
            _watched = Array.Empty<GlobalSystemMediaTransportControlsSession>();
        }
        foreach (var session in _watched) session.PlaybackInfoChanged += OnAnyPlaybackChanged;
    }

    private void OnAnyPlaybackChanged(GlobalSystemMediaTransportControlsSession s, PlaybackInfoChangedEventArgs e) =>
        OnUi(AttachCurrent);

    private static bool IsSessionPlaying(GlobalSystemMediaTransportControlsSession? session)
    {
        try { return session?.GetPlaybackInfo()?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing; }
        catch (Exception) { return false; }
    }

    /// Windows' current session, unless it is paused while another one is actually playing (a paused
    /// Spotify shouldn't hide a playing browser tab). The one already shown wins a tie, so it doesn't flicker.
    private GlobalSystemMediaTransportControlsSession? ChooseSession()
    {
        var current = _manager?.GetCurrentSession();
        if (current == null || IsSessionPlaying(current)) return current;
        if (IsSessionPlaying(_session)) return _session;
        return _watched.FirstOrDefault(IsSessionPlaying) ?? current;
    }

    private void AttachCurrent()
    {
        GlobalSystemMediaTransportControlsSession? session;
        try { session = ChooseSession(); }
        catch (Exception e)
        {
            Log.Write($"Now Playing session failed: {e.Message}");
            return;
        }
        if (ReferenceEquals(session, _session)) return;
        if (_session != null)
        {
            _session.MediaPropertiesChanged -= OnMediaChanged;
            _session.PlaybackInfoChanged -= OnPlaybackChanged;
            _session.TimelinePropertiesChanged -= OnTimelineChanged;
        }
        _session = session;
        _suppressUntil = default;
        if (_session != null)
        {
            _session.MediaPropertiesChanged += OnMediaChanged;
            _session.PlaybackInfoChanged += OnPlaybackChanged;
            _session.TimelinePropertiesChanged += OnTimelineChanged;
        }
        SourceApp = session == null ? "" : AppName(session.SourceAppUserModelId);
        ReadPlayback();
        ReadTimeline();
        _ = ReadMediaAsync();
    }

    private void OnMediaChanged(GlobalSystemMediaTransportControlsSession s, MediaPropertiesChangedEventArgs e) =>
        OnUi(() => _ = ReadMediaAsync());

    private void OnPlaybackChanged(GlobalSystemMediaTransportControlsSession s, PlaybackInfoChangedEventArgs e) =>
        OnUi(() => { ReadPlayback(); ReadTimeline(); Publish(); });

    private void OnTimelineChanged(GlobalSystemMediaTransportControlsSession s, TimelinePropertiesChangedEventArgs e) =>
        OnUi(ReadTimeline);

    private bool Suppressed => DateTimeOffset.Now < _suppressUntil;

    private void ReadPlayback()
    {
        if (Suppressed) return;
        IsPlaying = _session?.GetPlaybackInfo()?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
    }

    private void ReadTimeline()
    {
        var timeline = _session?.GetTimelineProperties();
        if (timeline == null)
        {
            Duration = 0;
            _position = TimeSpan.Zero;
            _positionUpdated = DateTimeOffset.Now;
            return;
        }
        Duration = Math.Max(0, (timeline.EndTime - timeline.StartTime).TotalSeconds);
        if (Suppressed) return;
        _position = timeline.Position - timeline.StartTime;
        // Some apps report a zero LastUpdatedTime; then the position is as of now.
        _positionUpdated = timeline.LastUpdatedTime.Year > 2000 ? timeline.LastUpdatedTime : DateTimeOffset.Now;
    }

    private async Task ReadMediaAsync()
    {
        var generation = ++_refreshGeneration;
        var session = _session;
        if (session == null)
        {
            Title = Artist = Album = "";
            Artwork = null;
            Publish();
            return;
        }
        try
        {
            var media = await session.TryGetMediaPropertiesAsync();
            // A newer change arrived while we waited; let that one win.
            if (generation != _refreshGeneration || media == null) return;
            Title = media.Title ?? "";
            Artist = media.Artist ?? "";
            Album = media.AlbumTitle ?? "";
            Artwork = media.Thumbnail == null ? null : await LoadArtworkAsync(media.Thumbnail);
            if (generation != _refreshGeneration) return;
        }
        catch (Exception e)
        {
            Log.Write($"Now Playing read failed: {e.Message}");
        }
        ReadTimeline();
        Publish();
    }

    private static async Task<ImageSource?> LoadArtworkAsync(Windows.Storage.Streams.IRandomAccessStreamReference reference)
    {
        try
        {
            using var stream = await reference.OpenReadAsync();
            using var input = stream.AsStreamForRead();
            var buffer = new MemoryStream();
            await input.CopyToAsync(buffer);
            buffer.Position = 0;
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = buffer;
            image.DecodePixelWidth = 480;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception e)
        {
            Log.Write($"Now Playing artwork failed: {e.Message}");
            return null;
        }
    }

    private void Publish()
    {
        Changed?.Invoke();
        LiveActivityHub.Shared.Refresh();
    }

    /// "Spotify.exe" → "Spotify", "MSEdge" → "Microsoft Edge", and so on.
    private static string AppName(string id)
    {
        var lower = id.ToLowerInvariant();
        if (lower.Contains("spotify")) return "Spotify";
        if (lower.Contains("msedge")) return "Microsoft Edge";
        if (lower.Contains("chrome")) return "Chrome";
        if (lower.Contains("firefox")) return "Firefox";
        if (lower.Contains("brave")) return "Brave";
        if (lower.Contains("opera")) return "Opera";
        if (lower.Contains("zunemusic") || lower.Contains("mediaplayer")) return "Media Player";
        if (lower.Contains("vlc")) return "VLC";
        if (lower.Contains("applemusic") || lower.Contains("itunes")) return "Apple Music";
        var name = id.Split('!')[0].Split('\\').Last();
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        return name.Split('_')[0];
    }

    // MARK: - Controls

    /// Flips the button straight away, like the Mac app, then checks what the player really did.
    public async void TogglePlayPause()
    {
        if (_session is not { } session) return;
        _position = TimeSpan.FromSeconds(Elapsed);
        _positionUpdated = DateTimeOffset.Now;
        IsPlaying = !IsPlaying;
        _suppressUntil = DateTimeOffset.Now.AddSeconds(1.2);
        Publish();
        try { await session.TryTogglePlayPauseAsync(); }
        catch (Exception e) { Log.Write($"Play/pause failed: {e.Message}"); }
        // If the app ignored the command, or never reports back, show its real state once the grace period ends.
        await Task.Delay(1300);
        if (!ReferenceEquals(session, _session) || Suppressed) return;
        ReadPlayback();
        ReadTimeline();
        Publish();
    }

    public async void Next()
    {
        if (_session == null) return;
        try { await _session.TrySkipNextAsync(); }
        catch (Exception e) { Log.Write($"Next failed: {e.Message}"); }
    }

    public async void Previous()
    {
        if (_session == null) return;
        try { await _session.TrySkipPreviousAsync(); }
        catch (Exception e) { Log.Write($"Previous failed: {e.Message}"); }
    }

    public async void Seek(double seconds)
    {
        if (_session == null || Duration <= 0) return;
        try
        {
            var start = _session.GetTimelineProperties()?.StartTime ?? TimeSpan.Zero;
            var target = start + TimeSpan.FromSeconds(Math.Clamp(seconds, 0, Duration));
            if (await _session.TryChangePlaybackPositionAsync(target.Ticks))
            {
                // Show the new spot right away rather than waiting for the app to report it.
                _position = TimeSpan.FromSeconds(seconds);
                _positionUpdated = DateTimeOffset.Now;
                _suppressUntil = DateTimeOffset.Now.AddSeconds(1.2);
                Changed?.Invoke();
            }
        }
        catch (Exception e) { Log.Write($"Seek failed: {e.Message}"); }
    }
}
