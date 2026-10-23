using System.Windows.Media;

namespace Purrch.Core;

/// In priority order: when several have something to show, the earliest kind wins.
public enum LiveActivityKind { None, Message, Timer, Download, Music }

/// What the closed pill shows: a scrolling message across it, or on either side a running timer, a download, or music.
public sealed record LiveActivity(LiveActivityKind Kind, string Label = "", string Glyph = "", double Progress = 0,
                                  ImageSource? Artwork = null)
{
    public static LiveActivity None { get; } = new(LiveActivityKind.None);

    /// Extra width on each side of the pill while this activity shows.
    public double Wing => Kind switch
    {
        LiveActivityKind.None => 0,
        LiveActivityKind.Message => 110,
        _ => 56,
    };
}

/// Collects live activities from the services that produce them (message, timers, downloads, media).
/// The pill shows the highest-priority one (see <see cref="LiveActivityKind"/>), whatever order the
/// services started in, as on the Mac: message, then timer, then download, then music. Within a kind the
/// source registered first wins, so music from the system media controls comes before a browser video.
public sealed class LiveActivityHub
{
    public static LiveActivityHub Shared { get; } = new();

    private readonly List<Func<LiveActivity?>> _sources = new();

    /// Raised when a source's activity may have changed; listeners re-read <see cref="Current"/>.
    public event Action? Changed;

    public void Register(Func<LiveActivity?> source)
    {
        _sources.Add(source);
        Refresh();
    }

    public void Refresh() => Changed?.Invoke();

    public LiveActivity Current
    {
        get
        {
            if (!AppSettings.Shared.ShowLiveActivities) return LiveActivity.None;
            var best = LiveActivity.None;
            foreach (var source in _sources)
                if (source() is { Kind: not LiveActivityKind.None } activity
                    && (best.Kind == LiveActivityKind.None || activity.Kind < best.Kind))
                    best = activity;
            return best;
        }
    }
}
