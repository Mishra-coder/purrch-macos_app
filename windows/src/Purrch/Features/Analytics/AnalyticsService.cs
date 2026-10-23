using System.ComponentModel;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Threading;
using Purrch.Core;

namespace Purrch.Features.Analytics;

/// Cached Plausible figures for one site: live visitors and today so far.
public sealed class AnalyticsSnapshot
{
    public sealed record Row(string Name, int Visitors);
    public sealed record Point(int Hour, int Visitors);

    public string Site { get; set; } = "";
    public string Host { get; set; } = "";
    /// Short hash of the key it was fetched with, so another account's figures are never shown.
    public string Account { get; set; } = "";
    public DateTime FetchedAt { get; set; }
    public int Live { get; set; }
    public int Visitors { get; set; }
    public int Pageviews { get; set; }
    public double BounceRate { get; set; }
    /// Average visit length in seconds.
    public double VisitDuration { get; set; }
    public List<Row> TopPages { get; set; } = new();
    public List<Row> TopSources { get; set; } = new();
    public List<Point> Hourly { get; set; } = new();
}

/// Live visitors and today's stats from Plausible's Stats API (plausible.io or a self-hosted server),
/// with the user's API key. Refreshes every 30 seconds while a view shows it; the last result is kept
/// in analytics.json.
public sealed class AnalyticsService
{
    /// Credential Manager entry for the key ("Purrch:plausible").
    public const string SecretName = "plausible";

    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(30);
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    public static AnalyticsService Shared { get; } = new();

    private readonly DispatcherTimer _timer = new() { Interval = RefreshInterval };
    private int _users;
    private int _generation;

    /// The snapshot, loading state or message changed.
    public event Action? Changed;

    public AnalyticsSnapshot? Snapshot { get; private set; }
    public bool IsLoading { get; private set; }
    public string? Message { get; private set; }
    /// Plausible turned the key down, so the cache isn't shown.
    public bool KeyRejected { get; private set; }

    public static bool HasKey => SecretStore.Get(SecretName).Trim().Length > 0;

    public static string Site => AppSettings.Shared.PlausibleSite.Trim();

    /// The host from Settings with a scheme and no trailing slash; plausible.io when blank.
    public static string Host
    {
        get
        {
            var host = AppSettings.Shared.PlausibleHost.Trim();
            if (host.Length == 0) host = "https://plausible.io";
            if (!host.StartsWith("http", StringComparison.OrdinalIgnoreCase)) host = "https://" + host;
            return host.TrimEnd('/');
        }
    }

    private AnalyticsService()
    {
        Snapshot = Store.Load<AnalyticsSnapshot?>("analytics", null);
        _timer.Tick += (_, _) => Refresh(force: true);
        AppSettings.Shared.PropertyChanged += OnSettingsChanged;
    }

    /// Views call Start when they appear and Stop when they go. Fetches straight away if the cache is stale.
    public void Start()
    {
        if (_users++ == 0) _timer.Start();
        Refresh(force: false);
    }

    public void Stop()
    {
        if (_users == 0 || --_users > 0) return;
        _timer.Stop();
    }

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(AppSettings.PlausibleSite) or nameof(AppSettings.PlausibleHost))) return;
        // A new site wins over a fetch still on its way for the old one.
        _generation++;
        IsLoading = false;
        if (_users > 0) Refresh(force: true);
    }

    /// Call on the UI thread. Without force, figures younger than 30 seconds are kept.
    public async void Refresh(bool force)
    {
        if (IsLoading) return;
        var key = SecretStore.Get(SecretName).Trim();
        var site = Site;
        var host = Host;
        if (key.Length == 0 || site.Length == 0)
        {
            Message = null;
            KeyRejected = false;
            Changed?.Invoke();
            return;
        }
        // The key, site or host may have changed while no view was listening.
        var account = Fingerprint(key);
        if (Snapshot is { } old && (old.Account != account || old.Site != site || old.Host != host)) Snapshot = null;
        if (!force && Snapshot is { } s && s.FetchedAt.Date == DateTime.Today && DateTime.Now - s.FetchedAt < RefreshInterval) return;

        var generation = ++_generation;
        IsLoading = true;
        Changed?.Invoke();

        var (snapshot, message, rejected) = await Load(key, site, host);
        if (generation != _generation) return;
        IsLoading = false;
        Message = message;
        KeyRejected = rejected;
        if (snapshot != null)
        {
            snapshot.Account = account;
            Snapshot = snapshot;
            Store.Save("analytics", snapshot);
        }
        Changed?.Invoke();
    }

    private static async Task<(AnalyticsSnapshot?, string?, bool)> Load(string key, string site, string host)
    {
        if (!Uri.TryCreate(host, UriKind.Absolute, out var root) || root.Scheme is not ("http" or "https"))
            return (null, "The Plausible host in Settings isn't a valid URL.", false);
        try
        {
            return (await Fetch(key, site, host), null, false);
        }
        catch (PlausibleException e)
        {
            return (null, e.Message, e.KeyRejected);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            return (null, $"Couldn't reach {root.Host}. Check your internet connection.", false);
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or UriFormatException)
        {
            Log.Write($"Analytics: {e.Message}");
            return (null, "Unexpected response from Plausible.", false);
        }
    }

    private static async Task<AnalyticsSnapshot> Fetch(string key, string site, string host)
    {
        string Url(string path, string query) => $"{host}{path}?site_id={Uri.EscapeDataString(site)}{query}";

        var live = Get(Url("/api/v1/stats/realtime/visitors", ""), key);
        var aggregate = Get(Url("/api/v1/stats/aggregate", "&period=day&metrics=visitors,pageviews,bounce_rate,visit_duration"), key);
        var pages = Get(Url("/api/v1/stats/breakdown", "&period=day&property=event:page&limit=5"), key);
        var sources = Get(Url("/api/v1/stats/breakdown", "&period=day&property=visit:source&limit=5"), key);
        var series = Get(Url("/api/v1/stats/timeseries", "&period=day"), key);
        await Task.WhenAll(live, aggregate, pages, sources, series);

        var results = (await aggregate)["results"];
        double Metric(string name) => Number(results?[name]?["value"]);
        var hourly = ((await series)["results"] as JsonArray)?.OfType<JsonNode>().Select((item, index) =>
        {
            // "2026-10-17 13:00:00" -> 13
            var date = Text(item["date"]) ?? "";
            var time = date.Split(' ').LastOrDefault() ?? "";
            var hour = time.Length >= 2 && int.TryParse(time.AsSpan(0, 2), out var h) ? h : index;
            return new AnalyticsSnapshot.Point(hour, (int)Number(item["visitors"]));
        }).ToList() ?? new List<AnalyticsSnapshot.Point>();

        return new AnalyticsSnapshot
        {
            Site = site,
            Host = host,
            FetchedAt = DateTime.Now,
            Live = (int)Number(await live),
            Visitors = (int)Metric("visitors"),
            Pageviews = (int)Metric("pageviews"),
            BounceRate = Metric("bounce_rate"),
            VisitDuration = Metric("visit_duration"),
            TopPages = Rows(await pages, "page"),
            TopSources = Rows(await sources, "source"),
            Hourly = hourly,
        };
    }

    private static List<AnalyticsSnapshot.Row> Rows(JsonNode json, string property) =>
        (json["results"] as JsonArray)?.OfType<JsonNode>().Select(r =>
        {
            var name = Text(r[property]);
            return new AnalyticsSnapshot.Row(string.IsNullOrEmpty(name) ? "Direct / none" : name, (int)Number(r["visitors"]));
        }).ToList() ?? new List<AnalyticsSnapshot.Row>();

    private static async Task<JsonNode> Get(string url, string key)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var response = await Http.SendAsync(request);
        var raw = await response.Content.ReadAsStringAsync();
        JsonNode? json = null;
        try { json = JsonNode.Parse(raw); }
        catch (JsonException) { }

        if (!response.IsSuccessStatusCode)
        {
            var message = Text(json?["error"]) ?? Text(json?["message"]);
            throw (int)response.StatusCode switch
            {
                // Plausible says 401 both for a bad key and for a site the key can't see; its message tells which.
                401 => new PlausibleException($"{message?.TrimEnd('.') ?? "Plausible rejected the API key"}. Check Settings → Integrations.", true),
                404 when message == null => new PlausibleException("No Plausible Stats API at this host. Check the host in Settings.", false),
                429 => new PlausibleException("Plausible is rate limiting this key. Purrch tries again shortly.", false),
                >= 500 => new PlausibleException("Plausible is having trouble right now. Purrch tries again shortly.", false),
                var status => new PlausibleException($"{message ?? response.ReasonPhrase} (HTTP {status})", false),
            };
        }
        return json ?? throw new JsonException("Empty response");
    }

    private static string Fingerprint(string key) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..12];

    // MARK: JSON

    private static string? Text(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static double Number(JsonNode? node) => node is JsonValue v && v.TryGetValue<double>(out var d) ? d : 0;

    // MARK: Formatting

    /// 842_200_000 -> "842.2M".
    public static string Compact(double value)
    {
        var v = Math.Abs(value);
        var sign = value < 0 ? "-" : "";
        string F(double x, string suffix)
        {
            var s = x >= 100 ? x.ToString("0") : x.ToString("0.0");
            return sign + (s.EndsWith(".0") || s.EndsWith(",0") ? s[..^2] : s) + suffix;
        }
        if (v >= 1e12) return F(v / 1e12, "T");
        if (v >= 1e9) return F(v / 1e9, "B");
        if (v >= 1e6) return F(v / 1e6, "M");
        if (v >= 1e3) return F(v / 1e3, "K");
        return sign + (int)v;
    }

    private sealed class PlausibleException(string message, bool keyRejected) : Exception(message)
    {
        public bool KeyRejected { get; } = keyRejected;
    }
}
