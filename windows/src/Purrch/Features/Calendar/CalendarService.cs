using System.ComponentModel;
using System.IO;
using System.Net.Http;
using System.Text.Json;
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

/// A Microsoft or Google account signed in from Purrch.
public sealed class AccountState
{
    public AccountKind Kind { get; set; }
    public string Email { get; set; } = "";
    /// The client ID it signed in with; a different one in Settings needs a new sign-in.
    public string ClientId { get; set; } = "";
    /// Calendars and task lists switched off in the Calendars card, by the provider's id.
    public List<string> Hidden { get; set; } = new();
    /// Last successful read.
    public DateTime? Fetched { get; set; }
}

/// The 7-day agenda, open reminders and the next event, from Microsoft and Google accounts, iCal links
/// and .ics files. Windows has no calendar API an unpackaged app may use (the appointments store needs
/// package identity), so accounts are read through Microsoft Graph and Google's APIs with the user's own
/// client IDs, and other calendars through the iCal links that Google, Outlook and iCloud publish.
/// Downloads and account reads are cached in %APPDATA%\Purrch\calendar-cache, so the agenda still shows offline.
public sealed class CalendarService
{
    private static readonly TimeSpan Stale = TimeSpan.FromMinutes(15);
    /// Accounts are read again when a view opens after this long, and every 5 minutes while one is open.
    private static readonly TimeSpan AccountStale = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan SignInTimeout = TimeSpan.FromMinutes(5);
    private static readonly HttpClient Http = CreateClient();
    private static readonly string CacheFolder = System.IO.Path.Combine(AppPaths.Root, "calendar-cache");
    private static readonly JsonSerializerOptions CacheJson = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// macOS calendar colours, handed out in turn to new calendars and to account calendars without one.
    internal static readonly string[] Palette = { "#0A84FF", "#30D158", "#FF9F0A", "#BF5AF2", "#FF453A", "#FFD60A", "#64D2FF", "#AC8E68" };

    public static CalendarService Shared { get; } = new();

    private sealed class Data
    {
        public List<CalendarSource> Sources { get; set; } = new();
        public List<LocalReminder> Reminders { get; set; } = new();
        /// Feed to-dos ticked off in Purrch ("source id:uid"). Feeds are read-only, so they are hidden instead.
        public List<string> DoneTodos { get; set; } = new();
        /// Signed-in Microsoft and Google accounts. Their tokens are in msal.cache and Credential Manager.
        public List<AccountState> Accounts { get; set; } = new();
    }

    private readonly Data _data = Store.Load<Data>("calendar");
    private readonly Dictionary<Guid, string> _errors = new();
    private readonly DispatcherTimer _timer = new() { Interval = RefreshInterval };
    private readonly List<CalendarAccount> _accounts = new() { new MicrosoftAccount() };
    /// Each account's last read, also kept in calendar-cache so it shows offline.
    private readonly Dictionary<AccountKind, AccountData> _accountData = new();
    private readonly Dictionary<AccountKind, string> _accountErrors = new();
    /// Accounts whose sign-in has expired or whose client ID changed; they aren't asked again until signed in.
    private readonly HashSet<AccountKind> _needsSignIn = new();
    private readonly Dictionary<AccountKind, CancellationTokenSource> _signingIn = new();
    /// Account tasks being ticked off ("calendar key:task id"), hidden until the write succeeds or fails.
    private readonly HashSet<string> _completing = new();
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
        foreach (var state in _data.Accounts)
            if (LoadAccountCache(state.Kind) is { } data) _accountData[state.Kind] = data;
        AppSettings.Shared.PropertyChanged += OnSettingsChanged;
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Purrch/1.0 (Windows)");
        return client;
    }

    public IReadOnlyList<CalendarSource> Sources => _data.Sources;
    /// iCal links or files, or a signed-in account.
    public bool HasSources => _data.Sources.Count > 0 || _data.Accounts.Count > 0;
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

    /// Open to-dos from the feeds and accounts (soonest due first), then the ones added in Purrch.
    public IReadOnlyList<ReminderItem> Reminders =>
        _feedTodos.Where(t => !_data.DoneTodos.Contains(DoneKey(t)) && !_completing.Contains(DoneKey(t)))
            .OrderBy(t => t.Due ?? DateTime.MaxValue)
            .Concat(_data.Reminders.Select(r => new ReminderItem(r.Id.ToString(), r.Title, null, null)))
            .ToList();

    public string? Error(CalendarSource source) => _errors.GetValueOrDefault(source.Id);

    /// The calendar's colour; null for reminders added in Purrch, which use the accent.
    public Color? ColourOf(Guid? sourceId)
    {
        var colour = _data.Sources.FirstOrDefault(s => s.Id == sourceId)?.Colour ?? AccountCalendarOf(sourceId)?.Calendar.Colour;
        if (colour == null) return null;
        try { return ColorConverter.ConvertFromString(colour) as Color?; }
        catch (FormatException) { return null; }
    }

    /// Views call Start when they appear and Stop when they go. Meanwhile accounts are read every 5 minutes
    /// and links every 15.
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

    /// Downloads links older than 15 minutes and reads accounts not read in the last minute (all of them
    /// with force), then rebuilds the agenda if anything changed or the day has turned. Call on the UI thread.
    public async void Refresh(bool force = false)
    {
        if (_fetching)
        {
            _refreshAgain = true;
            return;
        }
        var due = _data.Sources.Where(s => s.IsLink && (force || s.Fetched is not { } f
            || DateTime.Now - f > Stale || !File.Exists(CachePath(s)))).ToList();
        var accounts = _data.Accounts.Where(a => !_needsSignIn.Contains(a.Kind) && !_signingIn.ContainsKey(a.Kind)
            && (force || a.Fetched is not { } f || DateTime.Now - f > AccountStale || f.Date != DateTime.Today)).ToList();
        if (due.Count > 0 || accounts.Count > 0)
        {
            _fetching = true;
            Changed?.Invoke();
            await Task.WhenAll(due.Select(Download).Concat(accounts.Select(Read)));
            _fetching = false;
        }
        // Files on disk can change at any time and are quick to read, so they always count as changed.
        if (_dirty || _builtFor != DateTime.Today || _data.Sources.Any(s => !s.IsLink)) await Rebuild();
        else if (due.Count > 0 || accounts.Count > 0) Changed?.Invoke();
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
        foreach (var state in _data.Accounts)
        {
            if (!_accountData.TryGetValue(state.Kind, out var data)) continue;
            var hidden = data.Calendars.Where(c => state.Hidden.Contains(c.Id)).Select(c => c.Key).ToHashSet();
            // The copy may be from an earlier day; keep only what overlaps this week, as the feeds do.
            events.AddRange(data.Events.Where(e => !hidden.Contains(e.SourceId)
                && e.Start < to && (e.End > from || (e.End == e.Start && e.Start >= from))));
            todos.AddRange(data.Reminders.Where(r => r.SourceId is not { } key || !hidden.Contains(key)));
        }
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

    /// Ticks a reminder off: ones added in Purrch are removed, account tasks are completed in the account
    /// (and come back with a notification if that fails), feed to-dos are hidden from now on.
    public void Complete(ReminderItem item)
    {
        if (AccountCalendarOf(item.SourceId) is { } owner)
        {
            CompleteInAccount(item, owner.State, owner.Calendar);
            return;
        }
        if (item.SourceId == null) _data.Reminders.RemoveAll(r => r.Id.ToString() == item.Id);
        else if (!_data.DoneTodos.Contains(DoneKey(item))) _data.DoneTodos.Add(DoneKey(item));
        Save();
        Changed?.Invoke();
    }

    /// Hides the task at once, as the Mac does, and brings it back if the account refuses.
    private async void CompleteInAccount(ReminderItem item, AccountState state, AccountCalendar list)
    {
        var key = DoneKey(item);
        if (!_completing.Add(key)) return;
        Changed?.Invoke();
        var account = Account(state.Kind);
        try
        {
            await account.Complete(list.Id, item.Id);
            if (_accountData.TryGetValue(state.Kind, out var data)
                && data.Reminders.RemoveAll(r => r.Id == item.Id && r.SourceId == item.SourceId) > 0)
                SaveAccountCache(state.Kind, data);
            _feedTodos.RemoveAll(r => r.Id == item.Id && r.SourceId == item.SourceId);
        }
        catch (Exception e) when (e is AccountException or HttpRequestException or TaskCanceledException or JsonException)
        {
            var reason = e is AccountException ? e.Message : $"Couldn't reach {account.Title}.";
            Toasts.Show("Reminder not completed", $"\u201C{item.Title}\u201D is still open in {list.Name}. {reason}", ModuleId.Calendar);
        }
        _completing.Remove(key);
        Changed?.Invoke();
    }

    private static string DoneKey(ReminderItem item) => $"{item.SourceId}:{item.Id}";

    private static string CachePath(CalendarSource source) => System.IO.Path.Combine(CacheFolder, source.Id + ".ics");

    private void Save() => Store.Save("calendar", _data);

    // MARK: Accounts

    public IReadOnlyList<CalendarAccount> Accounts => _accounts;

    /// The signed-in account's email address, or null.
    public string? Email(AccountKind kind) => State(kind)?.Email;

    /// True while the browser is open for signing in.
    public bool IsSigningIn(AccountKind kind) => _signingIn.ContainsKey(kind);

    /// Why the account can't be read or signed in to; null when all is well.
    public string? AccountError(AccountKind kind) => _accountErrors.GetValueOrDefault(kind);

    /// A part of the account that didn't load while the rest did (To Do or Tasks, one shared calendar).
    public string? AccountWarning(AccountKind kind) => State(kind) != null ? _accountData.GetValueOrDefault(kind)?.Warning : null;

    /// The sign-in has expired, was withdrawn or belongs to another client ID.
    public bool NeedsSignIn(AccountKind kind) => _needsSignIn.Contains(kind);

    /// The account's calendars and task lists from its last read.
    public IReadOnlyList<AccountCalendar> CalendarsOf(AccountKind kind) =>
        State(kind) != null && _accountData.TryGetValue(kind, out var data) ? data.Calendars : Array.Empty<AccountCalendar>();

    public bool IsHidden(AccountKind kind, AccountCalendar calendar) => State(kind)?.Hidden.Contains(calendar.Id) == true;

    /// Shows or hides one calendar or task list in the agenda and reminders.
    public void SetHidden(AccountKind kind, AccountCalendar calendar, bool hidden)
    {
        if (State(kind) is not { } state || hidden == state.Hidden.Contains(calendar.Id)) return;
        if (hidden) state.Hidden.Add(calendar.Id);
        else state.Hidden.Remove(calendar.Id);
        Save();
        _ = Rebuild();
    }

    private CalendarAccount Account(AccountKind kind) => _accounts.First(a => a.Kind == kind);

    private AccountState? State(AccountKind kind) => _data.Accounts.FirstOrDefault(a => a.Kind == kind);

    /// The account calendar or task list a key stands for.
    private (AccountState State, AccountCalendar Calendar)? AccountCalendarOf(Guid? key)
    {
        if (key == null) return null;
        foreach (var state in _data.Accounts)
            if (_accountData.TryGetValue(state.Kind, out var data) && data.Calendars.FirstOrDefault(c => c.Key == key) is { } calendar)
                return (state, calendar);
        return null;
    }

    /// Opens the browser to sign in; the outcome shows through Email and AccountError. Signing in again
    /// while the browser is still waiting starts over.
    public async void SignIn(AccountKind kind)
    {
        var account = Account(kind);
        if (_signingIn.Remove(kind, out var earlier)) earlier.Cancel();
        if (account.SetupProblem is { } problem)
        {
            _accountErrors[kind] = problem;
            Changed?.Invoke();
            return;
        }
        using var cancel = new CancellationTokenSource(SignInTimeout);
        _signingIn[kind] = cancel;
        _accountErrors.Remove(kind);
        Changed?.Invoke();
        string? email = null;
        string? error = null;
        try
        {
            email = await account.SignIn(cancel.Token);
        }
        catch (AccountException e)
        {
            error = e.Message;
        }
        catch (OperationCanceledException)
        {
            error = "Sign-in timed out. Try again.";
        }
        catch (HttpRequestException)
        {
            error = $"Couldn't reach {account.Title}. Check your internet connection.";
        }
        catch (JsonException)
        {
            error = $"Unexpected answer from {account.Title}. Try again.";
        }
        // A newer attempt, or Cancel, has taken over; it reports for itself.
        if (!_signingIn.TryGetValue(kind, out var current) || current != cancel) return;
        _signingIn.Remove(kind);

        if (email != null)
        {
            var previous = State(kind);
            var same = previous != null && string.Equals(previous.Email, email, StringComparison.OrdinalIgnoreCase);
            _data.Accounts.RemoveAll(a => a.Kind == kind);
            _data.Accounts.Add(new AccountState
            {
                Kind = kind,
                Email = email,
                ClientId = account.ClientId,
                // The same account keeps the calendars it had switched off.
                Hidden = same ? previous!.Hidden : new(),
            });
            if (!same) ForgetAccountData(kind);
            _needsSignIn.Remove(kind);
            _dirty = true;
            Save();
        }
        else if (error != null)
        {
            _accountErrors[kind] = error;
        }
        Changed?.Invoke();
        if (email != null) Refresh(force: true);
    }

    /// Stops waiting for the browser.
    public void CancelSignIn(AccountKind kind)
    {
        if (!_signingIn.Remove(kind, out var cancel)) return;
        cancel.Cancel();
        Changed?.Invoke();
    }

    /// Forgets the account and its events on this PC; Google is also asked to withdraw the sign-in.
    public async void SignOut(AccountKind kind)
    {
        CancelSignIn(kind);
        _data.Accounts.RemoveAll(a => a.Kind == kind);
        ForgetAccountData(kind);
        _accountErrors.Remove(kind);
        _needsSignIn.Remove(kind);
        _dirty = true;
        Save();
        Changed?.Invoke();
        _ = Rebuild();
        await Account(kind).SignOut();
    }

    /// Reads one account. Errors stay with the account, and its last copy keeps showing meanwhile.
    private async Task Read(AccountState state)
    {
        var account = Account(state.Kind);
        if (account.SetupProblem is { } problem)
        {
            _accountErrors[state.Kind] = problem;
            return;
        }
        if (!string.Equals(state.ClientId, account.ClientId, StringComparison.OrdinalIgnoreCase))
        {
            _accountErrors[state.Kind] = $"The {account.Title} client ID changed in Settings. Sign in again.";
            _needsSignIn.Add(state.Kind);
            return;
        }
        try
        {
            var data = await account.Fetch(DateTime.Today, DateTime.Today.AddDays(7));
            // Signed out, or into another account, while this was on its way.
            if (!_data.Accounts.Contains(state)) return;
            _accountData[state.Kind] = data;
            state.Fetched = DateTime.Now;
            _accountErrors.Remove(state.Kind);
            SaveAccountCache(state.Kind, data);
            _dirty = true;
            Save();
        }
        catch (AccountException e)
        {
            _accountErrors[state.Kind] = e.Message;
            if (e.NeedsSignIn) _needsSignIn.Add(state.Kind);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            _accountErrors[state.Kind] = _accountData.ContainsKey(state.Kind) ? "Offline: showing the last copy" : $"Couldn't reach {account.Title}";
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException)
        {
            Log.Write($"{account.Title} calendar unreadable: {e.GetType().Name}");
            _accountErrors[state.Kind] = $"Unexpected answer from {account.Title}. Purrch tries again in a few minutes.";
        }
    }

    /// A new client ID may fix a rejected one, so the account is tried again.
    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        AccountKind? kind = e.PropertyName switch
        {
            nameof(AppSettings.MicrosoftClientId) => AccountKind.Microsoft,
            _ => null,
        };
        if (kind is not { } changed) return;
        _needsSignIn.Remove(changed);
        _accountErrors.Remove(changed);
        Changed?.Invoke();
        if (_users > 0) Refresh();
    }

    private void ForgetAccountData(AccountKind kind)
    {
        _accountData.Remove(kind);
        try { File.Delete(AccountCachePath(kind)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    private static string AccountCachePath(AccountKind kind) =>
        System.IO.Path.Combine(CacheFolder, kind.ToString().ToLowerInvariant() + ".json");

    private static AccountData? LoadAccountCache(AccountKind kind)
    {
        try
        {
            var path = AccountCachePath(kind);
            return File.Exists(path) ? JsonSerializer.Deserialize<AccountData>(File.ReadAllText(path), CacheJson) : null;
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException or NotSupportedException)
        {
            Log.Write($"Calendar {kind} cache unreadable: {e.Message}");
            return null;
        }
    }

    private static void SaveAccountCache(AccountKind kind, AccountData data)
    {
        try
        {
            Directory.CreateDirectory(CacheFolder);
            var path = AccountCachePath(kind);
            File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(data, CacheJson));
            File.Move(path + ".tmp", path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Write($"Calendar {kind} cache not saved: {e.Message}");
        }
    }
}
