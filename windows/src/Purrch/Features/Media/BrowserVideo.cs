using System.Net.Http;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Purrch.Core;

namespace Purrch.Features.Media;

/// A YouTube video open in a browser tab, as Purrch's extension reports it.
public sealed record BrowserVideo
{
    /// The bridge connection (one per browser profile) the tab belongs to.
    public required int Connection { get; init; }
    /// "Chrome", "Edge", "Brave", "Vivaldi" or "Chromium".
    public required string Browser { get; init; }
    public required int TabId { get; init; }
    public required int WindowId { get; init; }
    public string Url { get; init; } = "";
    public string Title { get; init; } = "";
    public string Channel { get; init; } = "";
    public bool IsPlaying { get; init; }
    public double Current { get; init; }
    public double Duration { get; init; }
    /// False while the tab has no content script (discarded, or still loading); it can only be shown.
    public bool Controllable { get; init; }

    public string Id => $"{Connection}-{TabId}";

    /// "dQw4w9WgXcQ" from a watch, Shorts or YouTube Music address.
    public string? VideoId
    {
        get
        {
            if (!Uri.TryCreate(Url, UriKind.Absolute, out var uri)) return null;
            foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = pair.Split('=', 2);
                if (parts.Length == 2 && parts[0] == "v" && parts[1].Length > 0) return Uri.UnescapeDataString(parts[1]);
            }
            var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var shorts = Array.IndexOf(segments, "shorts");
            return shorts >= 0 && shorts + 1 < segments.Length ? segments[shorts + 1] : null;
        }
    }

    public string? ThumbnailUrl => VideoId is { } id ? $"https://i.ytimg.com/vi/{id}/mqdefault.jpg" : null;
    /// Sharper still for the large players (480x360, letterboxed; crop it to 16:9).
    public string? LargeThumbnailUrl => VideoId is { } id ? $"https://i.ytimg.com/vi/{id}/hqdefault.jpg" : null;
    public double Progress => Duration > 0 ? Math.Min(1, Current / Duration) : 0;
}

/// Audio: the list of open videos with controls. Video: the chosen one playing live, with the others beside it.
public enum MediaViewMode { Audio, Video }

/// How the hover preview trades sharpness for smoothness when it shows the extension's frames.
public enum PreviewQuality { Smooth, Balanced, Sharp }

public static class PreviewQualityInfo
{
    public static string Title(this PreviewQuality q) => q.ToString();

    public static int Width(this PreviewQuality q) => q switch
    {
        PreviewQuality.Smooth => 480,
        PreviewQuality.Sharp => 1080,
        _ => 720,
    };

    public static double Jpeg(this PreviewQuality q) => q switch
    {
        PreviewQuality.Smooth => 0.6,
        PreviewQuality.Sharp => 0.85,
        _ => 0.72,
    };

    public static double Fps(this PreviewQuality q) => q switch
    {
        PreviewQuality.Smooth => 24,
        PreviewQuality.Sharp => 12,
        _ => 18,
    };
}

public static class YouTubeQualityLabel
{
    public static string Text(string level) => level switch
    {
        "highres" => "4K+",
        "hd2160" => "2160p",
        "hd1440" => "1440p",
        "hd1080" => "1080p",
        "hd720" => "720p",
        "large" => "480p",
        "medium" => "360p",
        "small" => "240p",
        "tiny" => "144p",
        "auto" => "Auto",
        _ => level,
    };
}

/// YouTube thumbnails, downloaded once per address and kept for the session.
public static class VideoThumbnails
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private static readonly Dictionary<string, Task<ImageSource?>> Cache = new();

    /// The picture at `url`, or null when it can't be fetched. Call on the UI thread.
    public static Task<ImageSource?> Load(string? url)
    {
        if (string.IsNullOrEmpty(url)) return Task.FromResult<ImageSource?>(null);
        if (!Cache.TryGetValue(url, out var task))
        {
            task = Fetch(url);
            Cache[url] = task;
        }
        return task;
    }

    /// Sets `apply` with the picture once it arrives (at once when it is already here).
    public static async void Into(string? url, Action<ImageSource> apply)
    {
        if (await Load(url) is { } image) apply(image);
    }

    private static async Task<ImageSource?> Fetch(string url)
    {
        try
        {
            var bytes = await Http.GetByteArrayAsync(url);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = new System.IO.MemoryStream(bytes);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or NotSupportedException
                                       or System.IO.IOException or InvalidOperationException)
        {
            // Offline, or the video has no thumbnail; try again next time it is asked for.
            Cache.Remove(url);
            Log.Write($"Thumbnail {url}: {e.Message}");
            return null;
        }
    }
}
