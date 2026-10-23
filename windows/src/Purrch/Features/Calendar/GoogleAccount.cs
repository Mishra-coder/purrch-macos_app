using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Purrch.Core;

namespace Purrch.Features.Calendar;

/// Google Calendar and Google Tasks with the user's own "Desktop app" OAuth client. Signs in with OAuth 2.0
/// for installed apps: PKCE, the system browser, and a one-off listener on 127.0.0.1 for the answer.
/// The refresh token and the client secret are kept in Credential Manager; access tokens only in memory.
public sealed class GoogleAccount : CalendarAccount
{
    /// Credential Manager entry for the OAuth client's secret ("Purrch:google-client-secret").
    public const string SecretName = "google-client-secret";
    private const string RefreshTokenName = "google-refresh-token";

    private const string AuthUrl = "https://accounts.google.com/o/oauth2/v2/auth";
    private const string TokenUrl = "https://oauth2.googleapis.com/token";
    private const string RevokeUrl = "https://oauth2.googleapis.com/revoke";
    private const string CalendarRoot = "https://www.googleapis.com/calendar/v3";
    private const string TasksRoot = "https://tasks.googleapis.com/tasks/v1";
    private const string CalendarScope = "https://www.googleapis.com/auth/calendar.readonly";
    private const string TasksScope = "https://www.googleapis.com/auth/tasks";

    private readonly SemaphoreSlim _tokenGate = new(1, 1);
    private string? _accessToken;
    private string _accessClient = "";
    private DateTime _accessExpires;

    public override AccountKind Kind => AccountKind.Google;
    public override string Title => "Google";
    public override string ClientId => AppSettings.Shared.GoogleClientId.Trim();
    private static string ClientSecret => SecretStore.Get(SecretName).Trim();

    public override string? SetupProblem =>
        ClientId.Length == 0 ? "Add your Google client ID and secret in Settings → Integrations."
        : !ClientId.EndsWith(".apps.googleusercontent.com", StringComparison.OrdinalIgnoreCase)
            ? "A Google client ID ends in .apps.googleusercontent.com. Check it in Settings → Integrations."
        : ClientSecret.Length == 0 ? "Add the Google client secret in Settings → Integrations."
        : null;

    public override async Task<string> SignIn(CancellationToken cancel)
    {
        if (SetupProblem is { } problem) throw new AccountException(problem);
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var state = Base64Url(RandomNumberGenerator.GetBytes(16));
        var redirect = $"http://127.0.0.1:{FreePort()}/";

        using var listener = new HttpListener();
        listener.Prefixes.Add(redirect);
        try { listener.Start(); }
        catch (HttpListenerException e)
        {
            Log.Write($"Google sign-in listener: {e.ErrorCode}");
            throw new AccountException("Purrch couldn't listen for Google's answer on this PC. Try again.");
        }

        var url = $"{AuthUrl}?client_id={Uri.EscapeDataString(ClientId)}&redirect_uri={Uri.EscapeDataString(redirect)}" +
                  $"&response_type=code&scope={Uri.EscapeDataString($"{CalendarScope} {TasksScope} openid email")}" +
                  $"&code_challenge={challenge}&code_challenge_method=S256&state={state}" +
                  "&access_type=offline&prompt=consent";
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (System.ComponentModel.Win32Exception e)
        {
            Log.Write($"Google sign-in: no browser ({e.NativeErrorCode})");
            throw new AccountException("Purrch couldn't open your browser to sign in.");
        }

        // Wait for the browser to come back; ignore anything else that knocks (a favicon request, say).
        string code;
        while (true)
        {
            var context = await listener.GetContextAsync().WaitAsync(cancel);
            var query = context.Request.QueryString;
            if (query["state"] == null && query["error"] == null)
            {
                context.Response.StatusCode = 404;
                context.Response.Close();
                continue;
            }
            var error = query["error"];
            var ok = error == null && query["state"] == state && !string.IsNullOrEmpty(query["code"]);
            await Respond(context, ok ? Page("Signed in to Purrch", "You can close this tab and go back to Purrch.")
                                      : Page("Sign-in didn't finish", "Go back to Purrch to see why."));
            if (query["state"] != state) throw new AccountException("That answer wasn't for Purrch's sign-in. Try again.");
            if (error != null)
                throw error == "access_denied"
                    ? new AccountException("Sign-in was declined, so Purrch can't read this calendar. Sign in again and allow access.")
                    : new AccountException($"Google sign-in failed ({error}).");
            code = query["code"]!;
            break;
        }

        var json = await TokenRequest(new Dictionary<string, string>
        {
            ["code"] = code,
            ["client_id"] = ClientId,
            ["client_secret"] = ClientSecret,
            ["redirect_uri"] = redirect,
            ["grant_type"] = "authorization_code",
            ["code_verifier"] = verifier,
        });
        var refresh = Text(json["refresh_token"]);
        var granted = Text(json["scope"]) ?? "";
        if (!granted.Contains(CalendarScope, StringComparison.Ordinal))
        {
            // Google lets people untick boxes on the consent page; without the calendar there's nothing to show.
            await Revoke(refresh ?? Text(json["access_token"]));
            throw new AccountException("Google didn't give Purrch your calendars. Sign in again and tick \"See your calendars\".");
        }
        if (refresh == null)
            throw new AccountException("Google didn't send a lasting sign-in. Remove Purrch at myaccount.google.com/permissions, then sign in again.");
        SecretStore.Set(RefreshTokenName, refresh);
        Remember(json);
        return Email(Text(json["id_token"])) ?? "Google account";
    }

    public override async Task SignOut()
    {
        var refresh = SecretStore.Get(RefreshTokenName);
        _accessToken = null;
        SecretStore.Set(RefreshTokenName, "");
        // Withdrawing the grant at Google is best effort: offline, the token is still gone from this PC.
        try { await Revoke(refresh); }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException) { }
    }

    private static async Task Revoke(string? token)
    {
        if (string.IsNullOrEmpty(token)) return;
        await PostForm(RevokeUrl, new Dictionary<string, string> { ["token"] = token });
    }

    /// A live access token, refreshed with the stored refresh token a minute before it runs out.
    private async Task<string> Token()
    {
        if (SetupProblem is { } problem) throw new AccountException(problem, needsSignIn: true);
        await _tokenGate.WaitAsync();
        try
        {
            if (_accessToken != null && _accessClient == ClientId && DateTime.UtcNow < _accessExpires) return _accessToken;
            var refresh = SecretStore.Get(RefreshTokenName);
            if (refresh.Length == 0) throw new AccountException("Sign in to Google again.", needsSignIn: true);
            var json = await TokenRequest(new Dictionary<string, string>
            {
                ["client_id"] = ClientId,
                ["client_secret"] = ClientSecret,
                ["refresh_token"] = refresh,
                ["grant_type"] = "refresh_token",
            });
            Remember(json);
            return _accessToken!;
        }
        finally
        {
            _tokenGate.Release();
        }
    }

    private void Remember(JsonNode json)
    {
        _accessToken = Text(json["access_token"]) ?? throw new AccountException("Google sent no access token. Sign in again.", needsSignIn: true);
        _accessClient = ClientId;
        var seconds = Number(json["expires_in"]);
        _accessExpires = DateTime.UtcNow.AddSeconds(Math.Max(60, seconds > 0 ? seconds : 3600) - 60);
    }

    /// The token endpoint, with its OAuth errors turned into what to do about them.
    private static async Task<JsonNode> TokenRequest(Dictionary<string, string> fields)
    {
        var (ok, json) = await PostForm(TokenUrl, fields);
        if (ok && json != null) return json;
        var error = Text(json?["error"]);
        throw error switch
        {
            "invalid_client" or "unauthorized_client" =>
                new AccountException("Google doesn't accept this client ID or secret. Check them in Settings → Integrations.", needsSignIn: true),
            "invalid_grant" =>
                new AccountException("Your Google sign-in has expired or was withdrawn. Sign in again.", needsSignIn: true),
            "access_denied" or "admin_policy_enforced" or "org_internal" =>
                new AccountException("This Google account isn't allowed to use this client. Add it as a test user on the OAuth consent screen."),
            _ => new AccountException($"Google sign-in failed ({Text(json?["error_description"]) ?? error ?? "no answer"})."),
        };
    }

    // MARK: Calendar and Tasks

    public override async Task<AccountData> Fetch(DateTime from, DateTime to)
    {
        var token = await Token();
        var data = new AccountData();
        var warnings = new List<string>();

        foreach (var c in await Pages($"{CalendarRoot}/users/me/calendarList?maxResults=250", token, "Google Calendar"))
        {
            if (Text(c["id"]) is not { } id || Flag(c["hidden"]) == true || Flag(c["deleted"]) == true) continue;
            data.Calendars.Add(new AccountCalendar
            {
                Id = id,
                Name = Clean(Text(c["summaryOverride"]) ?? Text(c["summary"])),
                Colour = Hex(Text(c["backgroundColor"])) ?? CalendarService.Palette[data.Calendars.Count % CalendarService.Palette.Length],
                Key = KeyFor(id),
            });
        }

        var timeMin = Uri.EscapeDataString(new DateTimeOffset(from).ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture));
        var timeMax = Uri.EscapeDataString(new DateTimeOffset(to).ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture));
        var views = await Task.WhenAll(data.Calendars.Select(async calendar =>
        {
            try
            {
                var url = $"{CalendarRoot}/calendars/{Uri.EscapeDataString(calendar.Id)}/events?timeMin={timeMin}&timeMax={timeMax}" +
                          "&singleEvents=true&orderBy=startTime&maxResults=250";
                return (calendar, Events: await Pages(url, token, "Google Calendar"), Error: (AccountException?)null);
            }
            catch (AccountException e) when (!e.NeedsSignIn)
            {
                return (calendar, Events: new List<JsonNode>(), Error: e);
            }
        }));
        if (views.Length > 0 && views.All(v => v.Error != null)) throw views[0].Error!;
        foreach (var (calendar, events, error) in views)
        {
            if (error != null) warnings.Add($"{calendar.Name}: {error.Message}");
            foreach (var e in events)
                if (Event(e, calendar.Key) is { } item) data.Events.Add(item);
        }

        // Tasks is a separate grant and API: a problem there shouldn't hide the calendars.
        try
        {
            var lists = await Pages($"{TasksRoot}/users/@me/lists?maxResults=100", token, "Google Tasks");
            // Task lists have no colour of their own; carry on round the palette after the calendars.
            var offset = data.Calendars.Count;
            var tasks = await Task.WhenAll(lists.Where(l => Text(l["id"]) != null).Select(async (l, index) =>
            {
                var id = Text(l["id"])!;
                var list = new AccountCalendar
                {
                    Id = id,
                    Name = Clean(Text(l["title"])),
                    Colour = CalendarService.Palette[(offset + index) % CalendarService.Palette.Length],
                    IsTaskList = true,
                    Key = KeyFor(id),
                };
                var url = $"{TasksRoot}/lists/{Uri.EscapeDataString(id)}/tasks?showCompleted=false&showHidden=false&maxResults=100";
                return (list, Tasks: await Pages(url, token, "Google Tasks"));
            }));
            foreach (var (list, items) in tasks)
            {
                data.Calendars.Add(list);
                foreach (var t in items)
                {
                    if (Text(t["id"]) is not { } id || Text(t["status"]) == "completed" || Flag(t["deleted"]) == true) continue;
                    data.Reminders.Add(new ReminderItem(id, Clean(Text(t["title"])), DueDay(Text(t["due"])), list.Key, DueIsDate: true));
                }
            }
        }
        catch (AccountException e) when (!e.NeedsSignIn)
        {
            warnings.Add($"Tasks: {e.Message}");
        }

        data.Warning = warnings.Count > 0 ? string.Join(" ", warnings) : null;
        return data;
    }

    public override async Task Complete(string listId, string taskId)
    {
        var token = await Token();
        var url = $"{TasksRoot}/lists/{Uri.EscapeDataString(listId)}/tasks/{Uri.EscapeDataString(taskId)}";
        await Send(HttpMethod.Patch, url, token, (status, json) => Explain(status, json, "Google Tasks"),
            new JsonObject { ["status"] = "completed" });
    }

    /// Every page of a Google list, following nextPageToken (at most 10 pages).
    private static async Task<List<JsonNode>> Pages(string url, string token, string what)
    {
        var all = new List<JsonNode>();
        string? pageToken = null;
        for (var page = 0; page < 10; page++)
        {
            var next = pageToken == null ? url : $"{url}&pageToken={Uri.EscapeDataString(pageToken)}";
            var json = await Send(HttpMethod.Get, next, token, (status, error) => Explain(status, error, what));
            all.AddRange(Items(json["items"]));
            pageToken = Text(json["nextPageToken"]);
            if (pageToken == null) break;
        }
        return all;
    }

    private static AccountException Explain(HttpStatusCode status, JsonNode? json, string what)
    {
        var error = json?["error"];
        var reason = Text(Items(error?["errors"]).FirstOrDefault()?["reason"]);
        var message = Text(error?["message"]);
        var details = error?["details"]?.ToJsonString() ?? "";
        return (int)status switch
        {
            401 => new AccountException("Your Google sign-in has expired or was withdrawn. Sign in again.", needsSignIn: true),
            403 when reason is "accessNotConfigured" || details.Contains("SERVICE_DISABLED", StringComparison.Ordinal)
                => new AccountException($"Turn on the {what} API for your project in Google Cloud console (APIs & Services → Library)."),
            403 when reason is "insufficientPermissions" || details.Contains("ACCESS_TOKEN_SCOPE_INSUFFICIENT", StringComparison.Ordinal)
                => new AccountException($"Purrch wasn't given {what}. Sign out, sign in again and tick every box."),
            403 when reason is "rateLimitExceeded" or "userRateLimitExceeded" or "quotaExceeded"
                => new AccountException("Google is limiting requests. Purrch tries again in a few minutes."),
            404 => new AccountException($"{what} couldn't find this calendar."),
            429 => new AccountException("Google is limiting requests. Purrch tries again in a few minutes."),
            >= 500 => new AccountException("Google is having trouble right now. Purrch tries again in a few minutes."),
            var other => new AccountException($"{message ?? what + " refused"} (HTTP {other})"),
        };
    }

    private static AgendaEvent? Event(JsonNode e, Guid key)
    {
        if (Text(e["status"]) == "cancelled" || Text(e["id"]) is not { } id) return null;
        var allDay = Text(e["start"]?["date"]) != null;
        if (Time(e["start"]) is not { } start) return null;
        var end = Time(e["end"]) ?? start;
        if (allDay && end <= start) end = start.AddDays(1);
        var location = Text(e["location"])?.Trim();
        return new AgendaEvent($"g:{id}", Clean(Text(e["summary"])), start, end, allDay,
            string.IsNullOrEmpty(location) ? null : location, key);
    }

    /// { dateTime } with an offset becomes local time; { date } (all day, end exclusive) is local midnight.
    private static DateTime? Time(JsonNode? node)
    {
        if (Text(node?["dateTime"]) is { } text
            && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var time))
            return time.LocalDateTime;
        return DueDay(Text(node?["date"]));
    }

    // MARK: Helpers

    /// A port nobody is using right now, for the one-off sign-in listener.
    private static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private static async Task Respond(HttpListenerContext context, string html)
    {
        try
        {
            var bytes = Encoding.UTF8.GetBytes(html);
            context.Response.ContentType = "text/html; charset=utf-8";
            context.Response.ContentLength64 = bytes.Length;
            await context.Response.OutputStream.WriteAsync(bytes);
            context.Response.Close();
        }
        catch (Exception e) when (e is HttpListenerException or ObjectDisposedException or IOException) { }
    }

    /// The email in the ID token Google just sent over TLS from its token endpoint, so its signature isn't checked.
    private static string? Email(string? idToken)
    {
        var parts = idToken?.Split('.');
        if (parts is not { Length: 3 }) return null;
        try
        {
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
            return Text(JsonNode.Parse(Convert.FromBase64String(payload))?["email"]);
        }
        catch (Exception e) when (e is FormatException or JsonException)
        {
            return null;
        }
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
