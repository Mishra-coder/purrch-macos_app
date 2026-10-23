using System.IO;
using System.Net.Http;
using System.Text.Json.Serialization;
using System.Windows.Media;
using System.Windows.Threading;
using Purrch.Core;

namespace Purrch.Features.Calendar;

/// A calendar Purrch reads: an iCal link (Google, Outlook, iCloud...) or an .ics file on this PC.
public sealed class CalendarSource
{
    public Guid Id { get; set; } = Guid.NewGuid();
    /// From the feed (X-WR-CALNAME) once it has loaded; empty until then.
    public string Name { get; set; } = "";
    /// An https:// link, or a file path.
    public string Location { get; set; } = "";
    public string Colour { get; set; } = "#0A84FF";
    /// Last successful download of a link.
    public DateTime? Fetched { get; set; }

    [JsonIgnore]
    public bool IsLink => Location.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                          || Location.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    /// The name, or failing that the link's host or the file's name.
    [JsonIgnore]
    public string Title => Name.Length > 0 ? Name
        : IsLink && Uri.TryCreate(Location, UriKind.Absolute, out var uri) ? uri.Host
        : System.IO.Path.GetFileNameWithoutExtension(Location);
}

/// A reminder added in Purrch itself.
public sealed class LocalReminder
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Title { get; set; } = "";
}

/// The 7-day agenda, open reminders and the next event, from iCal links and .ics files.
/// Windows has no calendar API an unpackaged app may use (the appointments store needs package identity),
/// so this reads the iCal links that Google, Outlook and iCloud publish. Downloads are cached in
/// %APPDATA%\Purrch\calendar-cache, so the agenda still shows offline.
public sealed class CalendarService
{
    public static CalendarService Shared { get; } = new();

    private static readonly TimeSpan Stale = TimeSpan.FromMinutes(15);
    private static readonly HttpClient Http = CreateClient();
    private static readonly string CacheFolder = System.IO.Path.Combine(AppPaths.Root, "calendar-cache");

    /// macOS calendar colours, handed out in turn to new calendars.
    private static readonly string[] Palette = { "#0A84FF", "#30D158", "#FF9F0A", "#BF5AF2", "#FF453A", "#FFD60A", "#64D2FF", "#AC8E68" };

    private sealed class Data
    {
        public List<CalendarSource> Sources { get; set; } = new();
        public List<LocalReminder> Reminders { get; set; } = new();
        /// Feed to-dos ticked off in Purrch ("source id:uid"). Feeds are read-only, so they are hidden instead.
        public List<string> DoneTodos { get; set; } = new();
    }

    private readonly Data _data = Store.Load<Data>("calendar");
    private readonly Dictionary<Guid, string> _errors = new();
    private readonly DispatcherTimer _timer = new() { Interval = Stale };
    private List<AgendaEvent> _events = new();
    private List<ReminderItem> _feedTodos = new();
    private DateTime _builtFor;
    private bool _dirty = true;
    private bool _fetching;
    /// A refresh was asked for while links were downloading (a calendar was just added, say).
    private bool _refreshAgain;
    private int _generation;
    private int _users;

    /// Events, reminders, calendars or loading state changed.
    public event Action? Changed;

    private CalendarService()
    {
        _timer.Tick += (_, _) => Refresh();
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Purrch/1.0 (Windows)");
        return client;
    }

    public IReadOnlyList<CalendarSource> Sources => _data.Sources;
    public bool HasSources => _data.Sources.Count > 0;
    /// True until the first agenda has been built, and while links download.
    public bool IsLoading => _fetching || (_dirty && HasSources && _builtFor == default);

    /// Occurrences from the start of today to seven days on, by start time.
    public IReadOnlyList<AgendaEvent> Events => _events;

    /// The next event that hasn't ended yet (not all-day), for the Home widget.
    public AgendaEvent? NextEvent
    {
        get
        {
            var now = DateTime.Now;
            return _events.FirstOrDefault(e => !e.IsAllDay && e.End > now);
        }
    }

    /// Open to-dos from the feeds (soonest due first), then the ones added in Purrch.
    public IReadOnlyList<ReminderItem> Reminders =>
        _feedTodos.Where(t => !_data.DoneTodos.Contains(DoneKey(t)))
            .OrderBy(t => t.Due ?? DateTime.MaxValue)
            .Concat(_data.Reminders.Select(r => new ReminderItem(r.Id.ToString(), r.Title, null, null)))
            .ToList();

    public string? Error(CalendarSource source) => _errors.GetValueOrDefault(source.Id);

    /// The calendar's colour; null for reminders added in Purrch, which use the accent.
    public Color? ColourOf(Guid? sourceId)
    {
        if (_data.Sources.FirstOrDefault(s => s.Id == sourceId) is not { } source) return null;
        try { return ColorConverter.ConvertFromString(source.Colour) as Color?; }
        catch (FormatException) { return null; }
    }

    /// Views call Start when they appear and Stop when they go; links are checked every 15 minutes meanwhile.
    public void Start()
    {
        if (_users++ == 0) _timer.Start();
        Refresh();
    }

    public void Stop()
    {
        if (_users == 0 || --_users > 0) return;
        _timer.Stop();
    }

    /// Downloads links older than 15 minutes (all of them with force), then rebuilds the agenda if
    /// anything changed or the day has turned. Call on the UI thread.
    public async void Refresh(bool force = false)
    {
        if (_fetching)
        {
            _refreshAgain = true;
            return;
        }
        var due = _data.Sources.Where(s => s.IsLink && (force || s.Fetched is not { } f
            || DateTime.Now - f > Stale || !File.Exists(CachePath(s)))).ToList();
        if (due.Count > 0)
        {
            _fetching = true;
            Changed?.Invoke();
            await Task.WhenAll(due.Select(Download));
            _fetching = false;
        }
        // Files on disk can change at any time and are quick to read, so they always count as changed.
        if (_dirty || _builtFor != DateTime.Today || _data.Sources.Any(s => !s.IsLink)) await Rebuild();
        else if (due.Count > 0) Changed?.Invoke();
        if (_refreshAgain)
        {
            _refreshAgain = false;
            Refresh();
        }
    }

    private async Task Download(CalendarSource source)
    {
        try
        {
            using var response = await Http.GetAsync(source.Location);
            if (!response.IsSuccessStatusCode)
            {
                _errors[source.Id] = $"The server answered {(int)response.StatusCode} {response.ReasonPhrase}";
                return;
            }
            var text = await response.Content.ReadAsStringAsync();
            if (!text.Contains("BEGIN:VCALENDAR", StringComparison.OrdinalIgnoreCase))
            {
                _errors[source.Id] = "This link isn't an iCal calendar";
                return;
            }
            if (!_data.Sources.Contains(source)) return;
            Directory.CreateDirectory(CacheFolder);
            var path = CachePath(source);
            await File.WriteAllTextAsync(path + ".tmp", text);
            File.Move(path + ".tmp", path, overwrite: true);
            source.Fetched = DateTime.Now;
            _errors.Remove(source.Id);
            _dirty = true;
            Save();
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            _errors[source.Id] = File.Exists(CachePath(source)) ? "Offline: showing the last copy" : "Couldn't reach this calendar";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Write($"Calendar cache not saved: {e.Message}");
        }
    }

    /// Parses every calendar off the UI thread; a newer rebuild makes an older one's result moot.
    private async Task Rebuild()
    {
        var generation = ++_generation;
        var from = DateTime.Today;
        var to = from.AddDays(7);
        var inputs = _data.Sources.Select(s => (s.Id, Path: s.IsLink ? CachePath(s) : s.Location, s.IsLink)).ToList();
        var results = await Task.Run(() => inputs.Select(input => Read(input.Id, input.Path, input.IsLink, from, to)).ToList());
        if (generation != _generation) return;

        var events = new List<AgendaEvent>();
        var todos = new List<ReminderItem>();
        foreach (var (id, content, error) in results)
        {
            if (_data.Sources.FirstOrDefault(s => s.Id == id) is not { } source) continue;
            if (error != null) _errors[id] = error;
            else if (!source.IsLink) _errors.Remove(id);
            if (content == null) continue;
            if (source.Name.Length == 0 && content.Name != null)
            {
                source.Name = content.Name;
                Save();
            }
            events.AddRange(content.Events);
            todos.AddRange(content.Todos);
        }
        _events = events.OrderBy(e => e.Start).ThenByDescending(e => e.IsAllDay).ThenBy(e => e.Title).ToList();
        _feedTodos = todos;
        _builtFor = from;
        _dirty = false;
        Changed?.Invoke();
    }

    private static (Guid Id, FeedContent? Content, string? Error) Read(Guid id, string path, bool isLink, DateTime from, DateTime to)
    {
        string text;
        try
        {
            if (!File.Exists(path)) return (id, null, isLink ? null : "File not found");
            text = File.ReadAllText(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return (id, null, "Couldn't read the file");
        }
        try
        {
            return (id, IcsParser.Parse(text, id, from, to), null);
        }
        catch (Exception e)
        {
            // Ical.Net throws a variety of exceptions on malformed input; one bad calendar shouldn't hide the rest.
            Log.Write($"Calendar {id} unreadable: {e.GetType().Name}: {e.Message}");
            return (id, null, "This isn't a calendar Purrch can read");
        }
    }

    // MARK: Calendars

    /// Adds an iCal link (https:// or webcal://) or an .ics file path. Returns why it can't, or null.
    public string? Add(string input)
    {
        var text = input.Trim().Trim('"');
        if (text.Length == 0) return "Paste an iCal link or the path of an .ics file.";
        if (text.StartsWith("webcal://", StringComparison.OrdinalIgnoreCase)) text = "https://" + text["webcal://".Length..];

        string location;
        if (Uri.TryCreate(text, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp))
            location = uri.AbsoluteUri;
        else if (uri is { IsFile: true } && File.Exists(uri.LocalPath))
            location = uri.LocalPath;
        else if (File.Exists(text))
            location = System.IO.Path.GetFullPath(text);
        else
            return "That isn't a link or an .ics file. Links start with https:// or webcal://.";

        if (_data.Sources.Any(s => string.Equals(s.Location, location, StringComparison.OrdinalIgnoreCase)))
            return "That calendar is already added.";
        var used = _data.Sources.Select(s => s.Colour).ToHashSet();
        var colour = Palette.FirstOrDefault(c => !used.Contains(c)) ?? Palette[_data.Sources.Count % Palette.Length];
        _data.Sources.Add(new CalendarSource { Location = location, Colour = colour });
        _dirty = true;
        Save();
        Changed?.Invoke();
        Refresh();
        return null;
    }

    public void Remove(CalendarSource source)
    {
        _data.Sources.Remove(source);
        _errors.Remove(source.Id);
        _data.DoneTodos.RemoveAll(k => k.StartsWith(source.Id + ":", StringComparison.Ordinal));
        try { File.Delete(CachePath(source)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        _dirty = true;
        Save();
        Changed?.Invoke();
        _ = Rebuild();
    }

    // MARK: Reminders

    public void AddReminder(string title)
    {
        var text = title.Trim();
        if (text.Length == 0) return;
        _data.Reminders.Add(new LocalReminder { Title = text });
        Save();
        Changed?.Invoke();
    }

    /// Ticks a reminder off: ones added in Purrch are removed, feed to-dos are hidden from now on.
    public void Complete(ReminderItem item)
    {
        if (item.SourceId == null) _data.Reminders.RemoveAll(r => r.Id.ToString() == item.Id);
        else if (!_data.DoneTodos.Contains(DoneKey(item))) _data.DoneTodos.Add(DoneKey(item));
        Save();
        Changed?.Invoke();
    }

    private static string DoneKey(ReminderItem item) => $"{item.SourceId}:{item.Id}";

    private static string CachePath(CalendarSource source) => System.IO.Path.Combine(CacheFolder, source.Id + ".ics");

    private void Save() => Store.Save("calendar", _data);
}
