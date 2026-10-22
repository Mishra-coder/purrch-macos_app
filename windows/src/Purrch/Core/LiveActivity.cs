using System.Windows.Media;

namespace Purrch.Core;

public enum LiveActivityKind { None, Timer, Download, Music }

/// What the closed pill shows on either side: a running timer, a download, or music.
public sealed record LiveActivity(LiveActivityKind Kind, string Label = "", string Glyph = "", double Progress = 0,
                                  ImageSource? Artwork = null)
{
    public static LiveActivity None { get; } = new(LiveActivityKind.None);

    /// Extra width on each side of the pill while this activity shows.
    public double Wing => Kind == LiveActivityKind.None ? 0 : 56;
}

/// Collects live activities from the services that produce them (timers, downloads, media).
/// Sources are asked in the order they registered; the first one with something to show wins.
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
            foreach (var source in _sources)
                if (source() is { Kind: not LiveActivityKind.None } activity) return activity;
            return LiveActivity.None;
        }
    }
}
