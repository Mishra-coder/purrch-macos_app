using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Purrch.Core;

namespace Purrch.Features.Links;

public enum SearchEngine { Google, DuckDuckGo, Bing, Perplexity }

public static class SearchEngineInfo
{
    public static string Title(this SearchEngine e) => e.ToString();

    public static string Url(this SearchEngine e, string query)
    {
        var root = e switch
        {
            SearchEngine.DuckDuckGo => "https://duckduckgo.com/",
            SearchEngine.Bing => "https://www.bing.com/search",
            SearchEngine.Perplexity => "https://www.perplexity.ai/search",
            _ => "https://www.google.com/search",
        };
        return root + "?q=" + Uri.EscapeDataString(query);
    }
}

public sealed class SavedSite
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Title { get; set; } = "";
    public string Url { get; set; } = "";

    [JsonIgnore]
    public string Host => LinkParsing.Url(Url) is { } uri && uri.Host.Length > 0 ? uri.Host : Url;

    public SavedSite Copy() => new() { Id = Id, Title = Title, Url = Url };

    public static List<SavedSite> Defaults() => new()
    {
        new() { Title = "GitHub", Url = "https://github.com" },
        new() { Title = "YouTube", Url = "https://www.youtube.com" },
        new() { Title = "Gmail", Url = "https://mail.google.com" },
        new() { Title = "Claude", Url = "https://claude.ai" },
        new() { Title = "Google Drive", Url = "https://drive.google.com" },
        new() { Title = "Hacker News", Url = "https://news.ycombinator.com" },
    };
}

public static partial class LinkParsing
{
    [GeneratedRegex(@"^[0-9]{1,3}(\.[0-9]{1,3}){3}(:[0-9]+)?$")]
    private static partial Regex Ipv4();

    [GeneratedRegex(@"^[a-z0-9-]+(\.[a-z0-9-]+)*\.[a-z]{2,}(:[0-9]+)?$")]
    private static partial Regex Domain();

    /// Returns a URL if the text looks like an address rather than a search query.
    public static Uri? Url(string raw)
    {
        var text = raw.Trim();
        if (text.Length == 0 || text.Contains(' ')) return null;
        var lower = text.ToLowerInvariant();
        if (lower.StartsWith("http://") || lower.StartsWith("https://") || lower.StartsWith("file://"))
            return Uri.TryCreate(text, UriKind.Absolute, out var direct) ? direct : null;
        var hostPart = lower.Split('/', 2)[0];
        var isLocal = hostPart.StartsWith("localhost") || Ipv4().IsMatch(hostPart);
        if (!isLocal && !Domain().IsMatch(hostPart)) return null;
        return Uri.TryCreate((isLocal ? "http://" : "https://") + text, UriKind.Absolute, out var uri) ? uri : null;
    }

    /// Opens a link in the default browser. A broken file association is logged, not thrown.
    public static void Open(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Log.Write($"Couldn't open {url}: {e.Message}");
        }
    }
}

/// Saved sites and the chosen search engine, kept in %APPDATA%\Purrch\links.json.
public sealed class LinksStore
{
    public static LinksStore Shared { get; } = new();

    private sealed class Data
    {
        public SearchEngine Engine { get; set; } = SearchEngine.Google;
        public List<SavedSite> Sites { get; set; } = SavedSite.Defaults();
    }

    private readonly Data _data = Store.Load<Data>("links");

    public event Action? Changed;

    public IReadOnlyList<SavedSite> Sites => _data.Sites;

    public SearchEngine Engine
    {
        get => _data.Engine;
        set { _data.Engine = value; Save(); }
    }

    /// Replaces the site with the same id, or adds it at the end.
    public void Upsert(SavedSite site)
    {
        var index = _data.Sites.FindIndex(s => s.Id == site.Id);
        if (index >= 0) _data.Sites[index] = site;
        else _data.Sites.Add(site);
        Save();
    }

    public void Remove(SavedSite site)
    {
        _data.Sites.RemoveAll(s => s.Id == site.Id);
        Save();
    }

    private void Save()
    {
        Store.Save("links", _data);
        Changed?.Invoke();
    }
}

/// Site icons from Google's favicon service, kept in memory and in %APPDATA%\Purrch\favicons, so they
/// show at once and offline after the first visit (the Mac app gets the same from its URL cache).
/// A site without one, or no network the first time, leaves the tile showing its first letter.
public static class Favicons
{
    /// Older icons are fetched again, in case the site changed its icon; the old one stays if that fails.
    private static readonly TimeSpan MaxAge = TimeSpan.FromDays(14);
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private static readonly Dictionary<string, Task<ImageSource?>> Cache = new();
    private static readonly string Folder = Path.Combine(AppPaths.Root, "favicons");

    /// Call on the UI thread; the result arrives on it too.
    public static Task<ImageSource?> Get(string host)
    {
        // A failed fetch (offline, say) is tried again the next time the tile is built.
        if (!Cache.TryGetValue(host, out var task) || task is { IsCompletedSuccessfully: true, Result: null })
        {
            task = Fetch(host);
            Cache[host] = task;
        }
        return task;
    }

    private static async Task<ImageSource?> Fetch(string host)
    {
        // "localhost:3000" can't be a file name as it is.
        var file = Path.Combine(Folder, string.Concat(host.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)) + ".png");
        var saved = await Read(file);
        if (saved.Image != null && DateTime.UtcNow - saved.Written < MaxAge) return saved.Image;
        try
        {
            var url = $"https://www.google.com/s2/favicons?domain={Uri.EscapeDataString(host)}&sz=64";
            using var response = await Http.GetAsync(url);
            // Unknown sites get a generic globe with a 404; the letter looks better than that.
            if (!response.IsSuccessStatusCode) return saved.Image;
            var bytes = await response.Content.ReadAsByteArrayAsync();
            if (Decode(bytes) is not { } image) return saved.Image;
            try
            {
                Directory.CreateDirectory(Folder);
                await File.WriteAllBytesAsync(file, bytes);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Log.Write($"Couldn't keep the icon of {host}: {e.Message}");
            }
            return image;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException
                                      or NotSupportedException or ArgumentException or InvalidOperationException)
        {
            return saved.Image;
        }
    }

    private static async Task<(ImageSource? Image, DateTime Written)> Read(string file)
    {
        try
        {
            if (!File.Exists(file)) return (null, default);
            return (Decode(await File.ReadAllBytesAsync(file)), File.GetLastWriteTimeUtc(file));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return (null, default);
        }
    }

    private static ImageSource? Decode(byte[] bytes)
    {
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = new MemoryStream(bytes);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception e) when (e is NotSupportedException or FileFormatException or ArgumentException
                                      or InvalidOperationException or IOException)
        {
            return null;
        }
    }
}
