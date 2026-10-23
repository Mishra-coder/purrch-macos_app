using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Purrch.Features.Calendar;

/// The accounts Purrch can sign in to, the Windows stand-ins for the Mac's system calendars.
public enum AccountKind { Microsoft, Google }

/// A calendar or task list in a signed-in account.
public sealed class AccountCalendar
{
    /// The provider's own id, used to read and write it.
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Colour { get; set; } = "#0A84FF";
    /// A To Do or Google Tasks list rather than a calendar.
    public bool IsTaskList { get; set; }
    /// Stands for the calendar in AgendaEvent.SourceId and ReminderItem.SourceId.
    public Guid Key { get; set; }
}

/// What an account holds for the coming week. Also the offline copy in calendar-cache.
public sealed class AccountData
{
    public List<AccountCalendar> Calendars { get; set; } = new();
    public List<AgendaEvent> Events { get; set; } = new();
    public List<ReminderItem> Reminders { get; set; } = new();
    /// A part that failed while the rest loaded (tasks not allowed, say).
    public string? Warning { get; set; }
}

/// A problem worth showing as it is: a wrong client ID, consent refused, an expired sign-in.
public sealed class AccountException(string message, bool needsSignIn = false) : Exception(message)
{
    /// Only signing in again helps, so Purrch stops asking the server until then.
    public bool NeedsSignIn { get; } = needsSignIn;
}

/// Sign-in, reading and completing tasks for one kind of account, with the user's own app registration.
/// Tokens never reach the log or Purrch's JSON files.
public abstract class CalendarAccount
{
    protected static readonly HttpClient Http = CreateClient();

    public abstract AccountKind Kind { get; }
    public abstract string Title { get; }

    /// What is missing before it can sign in (no client ID, say), or null.
    public abstract string? SetupProblem { get; }

    /// The client ID signed in with, so changing it in Settings asks for a new sign-in.
    public abstract string ClientId { get; }

    /// Opens the browser to sign in and returns the account's email address.
    public abstract Task<string> SignIn(CancellationToken cancel);

    /// Forgets the sign-in on this PC (and withdraws it where the provider allows). Never throws.
    public abstract Task SignOut();

    /// Calendars, events overlapping [from, to) and open tasks.
    public abstract Task<AccountData> Fetch(DateTime from, DateTime to);

    /// Marks a task done in the account.
    public abstract Task Complete(string listId, string taskId);

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Purrch/1.0 (Windows)");
        return client;
    }

    /// The same Guid for the same calendar every time, so hidden calendars and colours survive restarts.
    protected Guid KeyFor(string id) =>
        new(MD5.HashData(Encoding.UTF8.GetBytes($"{Kind}:{id}")));

    /// Sends a request with the access token and returns the JSON answer (an empty object for 204).
    /// Failures become an <see cref="AccountException"/> through explain(status, error JSON).
    protected static async Task<JsonNode> Send(HttpMethod method, string url, string token,
                                               Func<HttpStatusCode, JsonNode?, AccountException> explain,
                                               JsonNode? body = null, Action<HttpRequestMessage>? configure = null)
    {
        using var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (body != null) request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        configure?.Invoke(request);
        using var response = await Http.SendAsync(request);
        var raw = await response.Content.ReadAsStringAsync();
        JsonNode? json = null;
        try { json = raw.Length == 0 ? null : JsonNode.Parse(raw); }
        catch (JsonException) { }
        if (!response.IsSuccessStatusCode) throw explain(response.StatusCode, json);
        return json ?? new JsonObject();
    }

    /// Posts a form (the OAuth token endpoints) and returns the JSON answer, successful or not.
    protected static async Task<(bool Ok, JsonNode? Json)> PostForm(string url, IEnumerable<KeyValuePair<string, string>> fields)
    {
        using var content = new FormUrlEncodedContent(fields);
        using var response = await Http.PostAsync(url, content);
        var raw = await response.Content.ReadAsStringAsync();
        JsonNode? json = null;
        try { json = raw.Length == 0 ? null : JsonNode.Parse(raw); }
        catch (JsonException) { }
        return (response.IsSuccessStatusCode, json);
    }

    /// The page the browser shows when sign-in comes back to Purrch.
    protected static string Page(string title, string detail) =>
        "<!doctype html><html><head><meta charset=\"utf-8\"><title>Purrch</title></head>" +
        "<body style=\"font-family:'Segoe UI',sans-serif;background:#111;color:#eee;display:flex;align-items:center;" +
        "justify-content:center;height:90vh\"><div style=\"text-align:center\">" +
        $"<h2>{WebUtility.HtmlEncode(title)}</h2><p style=\"color:#aaa\">{WebUtility.HtmlEncode(detail)}</p></div></body></html>";

    /// A calendar colour as #RRGGBB, or null when it isn't one.
    protected static string? Hex(string? colour)
    {
        if (colour is not { Length: 7 } c || c[0] != '#') return null;
        return int.TryParse(c[1..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _) ? c.ToUpperInvariant() : null;
    }

    /// A due date that only has a day: the day as written, whatever zone it was stored in.
    protected static DateTime? DueDay(string? text)
    {
        if (text is not { Length: >= 10 }) return null;
        return DateTime.TryParseExact(text[..10], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)
            ? DateTime.SpecifyKind(day, DateTimeKind.Local)
            : null;
    }

    protected static string Clean(string? text) =>
        string.IsNullOrWhiteSpace(text) ? "Untitled" : text.Trim().ReplaceLineEndings(" ");

    // MARK: JSON

    protected static string? Text(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    protected static bool? Flag(JsonNode? node) => node is JsonValue v && v.TryGetValue<bool>(out var b) ? b : null;

    protected static double Number(JsonNode? node) => node is JsonValue v && v.TryGetValue<double>(out var d) ? d : 0;

    protected static IEnumerable<JsonNode> Items(JsonNode? array) =>
        (array as JsonArray)?.OfType<JsonNode>() ?? Enumerable.Empty<JsonNode>();
}
