using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Identity.Client;
using Purrch.Core;

namespace Purrch.Features.Calendar;

/// Outlook calendars and Microsoft To Do through Microsoft Graph, for personal and work or school accounts.
/// Signs in with MSAL in the system browser (a loopback redirect to http://localhost), using the client ID
/// the user registered in Azure. MSAL's token cache is kept in %APPDATA%\Purrch\msal.cache, encrypted with
/// DPAPI for this Windows user.
public sealed class MicrosoftAccount : CalendarAccount
{
    private const string GraphRoot = "https://graph.microsoft.com/v1.0";

    /// offline_access, openid and profile are added by MSAL itself.
    private static readonly string[] Scopes = { "Calendars.Read", "Tasks.ReadWrite", "User.Read" };
    private static readonly string CachePath = System.IO.Path.Combine(AppPaths.Root, "msal.cache");
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Purrch.msal.cache");
    private static readonly object CacheGate = new();

    private IPublicClientApplication? _app;
    private string _appClientId = "";

    public override AccountKind Kind => AccountKind.Microsoft;
    public override string Title => "Microsoft";
    public override string ClientId => AppSettings.Shared.MicrosoftClientId.Trim();

    public override string? SetupProblem =>
        ClientId.Length == 0 ? "Add your Microsoft client ID in Settings → Integrations."
        : !Guid.TryParse(ClientId, out _) ? "The Microsoft client ID looks like 00000000-0000-0000-0000-000000000000. Check it in Settings → Integrations."
        : null;

    /// One MSAL client per client ID; a new ID in Settings gets a new one.
    private IPublicClientApplication App()
    {
        if (_app != null && _appClientId == ClientId) return _app;
        var app = PublicClientApplicationBuilder.Create(ClientId)
            .WithAuthority(AzureCloudInstance.AzurePublic, AadAuthorityAudience.AzureAdAndPersonalMicrosoftAccount)
            .WithRedirectUri("http://localhost")
            .Build();
        app.UserTokenCache.SetBeforeAccess(ReadCache);
        app.UserTokenCache.SetAfterAccess(WriteCache);
        _app = app;
        _appClientId = ClientId;
        return app;
    }

    public override async Task<string> SignIn(CancellationToken cancel)
    {
        if (SetupProblem is { } problem) throw new AccountException(problem);
        var app = App();
        AuthenticationResult result;
        try
        {
            result = await app.AcquireTokenInteractive(Scopes)
                .WithUseEmbeddedWebView(false)
                .WithPrompt(Prompt.SelectAccount)
                .WithSystemWebViewOptions(new SystemWebViewOptions
                {
                    HtmlMessageSuccess = Page("Signed in to Purrch", "You can close this tab and go back to Purrch."),
                    HtmlMessageError = Page("Sign-in didn't finish", "Go back to Purrch to see why."),
                })
                .ExecuteAsync(cancel);
        }
        catch (MsalException e)
        {
            throw Explain(e);
        }
        // Purrch shows one Microsoft account; forget any other left in the cache.
        foreach (var other in (await app.GetAccountsAsync()).Where(a => a.HomeAccountId?.Identifier != result.Account.HomeAccountId?.Identifier))
            await app.RemoveAsync(other);
        return result.Account.Username ?? "Microsoft account";
    }

    public override async Task SignOut()
    {
        try
        {
            if (SetupProblem == null)
            {
                var app = App();
                foreach (var account in await app.GetAccountsAsync()) await app.RemoveAsync(account);
            }
        }
        catch (MsalException e)
        {
            Log.Write($"Microsoft sign-out: {e.ErrorCode}");
        }
        _app = null;
        lock (CacheGate)
        {
            try { File.Delete(CachePath); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Log.Write($"msal.cache not deleted: {e.Message}"); }
        }
    }

    /// A Graph token from the cache, refreshed by MSAL when it has run out.
    private async Task<string> Token()
    {
        if (SetupProblem is { } problem) throw new AccountException(problem, needsSignIn: true);
        var app = App();
        var account = (await app.GetAccountsAsync()).FirstOrDefault()
                      ?? throw new AccountException("Sign in to Microsoft again.", needsSignIn: true);
        try
        {
            return (await app.AcquireTokenSilent(Scopes, account).ExecuteAsync()).AccessToken;
        }
        catch (MsalUiRequiredException)
        {
            throw new AccountException("Your Microsoft sign-in has expired or was withdrawn. Sign in again.", needsSignIn: true);
        }
        catch (MsalException e)
        {
            throw Explain(e);
        }
    }

    /// Turns MSAL's errors into what to do about them. Unreachable servers become HttpRequestException,
    /// so they read as "offline" like every other source.
    private static Exception Explain(MsalException e)
    {
        var text = e.Message;
        if (e.InnerException is HttpRequestException || e.ErrorCode is MsalError.RequestTimeout or "service_not_available")
            return new HttpRequestException("Microsoft sign-in unreachable", e);
        if (e.ErrorCode == MsalError.AuthenticationCanceledError)
            return new AccountException("Sign-in was cancelled.");
        if (e.ErrorCode == "access_denied" || text.Contains("AADSTS65004") || text.Contains("consent_required"))
            return new AccountException("Sign-in was declined, so Purrch can't read this calendar. Sign in again and accept.");
        if (text.Contains("AADSTS700016") || e.ErrorCode is "unauthorized_client" or "invalid_client")
            return new AccountException("Microsoft doesn't know this client ID. Check it in Settings → Integrations.", needsSignIn: true);
        if (text.Contains("AADSTS50011"))
            return new AccountException("Add http://localhost as a \"Mobile and desktop applications\" redirect URI in the app registration.");
        if (text.Contains("AADSTS7000218"))
            return new AccountException("Turn on \"Allow public client flows\" in the app registration's Authentication page.");
        if (text.Contains("AADSTS50020") || text.Contains("AADSTS50194") || text.Contains("AADSTS9002331"))
            return new AccountException("The app registration doesn't allow this kind of account. Choose \"Accounts in any organizational directory and personal Microsoft accounts\".");
        Log.Write($"Microsoft sign-in: {e.ErrorCode}");
        return new AccountException($"Microsoft sign-in failed ({e.ErrorCode}).");
    }

    // MARK: Graph

    public override async Task<AccountData> Fetch(DateTime from, DateTime to)
    {
        var token = await Token();
        var data = new AccountData();
        var warnings = new List<string>();

        var calendars = await Pages($"{GraphRoot}/me/calendars?$select=id,name,hexColor&$top=100", token, "your calendars");
        foreach (var c in calendars)
        {
            if (Text(c["id"]) is not { } id) continue;
            data.Calendars.Add(new AccountCalendar
            {
                Id = id,
                Name = Clean(Text(c["name"])),
                Colour = Hex(Text(c["hexColor"])) ?? CalendarService.Palette[data.Calendars.Count % CalendarService.Palette.Length],
                Key = KeyFor(id),
            });
        }

        var start = Uri.EscapeDataString(new DateTimeOffset(from).ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture));
        var end = Uri.EscapeDataString(new DateTimeOffset(to).ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture));
        var views = await Task.WhenAll(data.Calendars.Select(async calendar =>
        {
            try
            {
                var url = $"{GraphRoot}/me/calendars/{Uri.EscapeDataString(calendar.Id)}/calendarView?startDateTime={start}&endDateTime={end}" +
                          "&$select=id,subject,start,end,isAllDay,isCancelled,location&$orderby=start/dateTime&$top=100";
                return (calendar, Events: await Pages(url, token, "your calendar"), Error: (AccountException?)null);
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

        // To Do is separate: a work account without it should still show its calendars.
        try
        {
            var lists = await Pages($"{GraphRoot}/me/todo/lists", token, "Microsoft To Do");
            // To Do lists have no colour of their own; carry on round the palette after the calendars.
            var offset = data.Calendars.Count;
            var tasks = await Task.WhenAll(lists.Where(l => Text(l["id"]) != null).Select(async (l, index) =>
            {
                var id = Text(l["id"])!;
                var list = new AccountCalendar
                {
                    Id = id,
                    Name = Clean(Text(l["displayName"])),
                    Colour = CalendarService.Palette[(offset + index) % CalendarService.Palette.Length],
                    IsTaskList = true,
                    Key = KeyFor(id),
                };
                var url = $"{GraphRoot}/me/todo/lists/{Uri.EscapeDataString(id)}/tasks?$filter={Uri.EscapeDataString("status ne 'completed'")}&$top=100";
                return (list, Tasks: await Pages(url, token, "Microsoft To Do"));
            }));
            foreach (var (list, items) in tasks)
            {
                data.Calendars.Add(list);
                foreach (var t in items)
                {
                    if (Text(t["id"]) is not { } id || Text(t["status"]) == "completed") continue;
                    data.Reminders.Add(new ReminderItem(id, Clean(Text(t["title"])), DueDay(Text(t["dueDateTime"]?["dateTime"])),
                        list.Key, DueIsDate: true));
                }
            }
        }
        catch (AccountException e) when (!e.NeedsSignIn)
        {
            warnings.Add($"To Do: {e.Message}");
        }

        data.Warning = warnings.Count > 0 ? string.Join(" ", warnings) : null;
        return data;
    }

    public override async Task Complete(string listId, string taskId)
    {
        var token = await Token();
        var url = $"{GraphRoot}/me/todo/lists/{Uri.EscapeDataString(listId)}/tasks/{Uri.EscapeDataString(taskId)}";
        await Send(HttpMethod.Patch, url, token, (status, json) => Explain(status, json, "Microsoft To Do"),
            new JsonObject { ["status"] = "completed" });
    }

    /// Every page of a Graph list, following @odata.nextLink (at most 10 pages).
    private static async Task<List<JsonNode>> Pages(string url, string token, string what)
    {
        var all = new List<JsonNode>();
        string? next = url;
        for (var page = 0; page < 10 && next != null; page++)
        {
            var json = await Send(HttpMethod.Get, next, token, (status, error) => Explain(status, error, what), configure: PreferLocalZone);
            all.AddRange(Items(json["value"]));
            next = Text(json["@odata.nextLink"]);
        }
        return all;
    }

    /// Graph answers in the zone asked for; Windows zone names are what it expects.
    private static void PreferLocalZone(HttpRequestMessage request) =>
        request.Headers.TryAddWithoutValidation("Prefer", $"outlook.timezone=\"{TimeZoneInfo.Local.Id}\"");

    private static AccountException Explain(HttpStatusCode status, JsonNode? json, string what)
    {
        var code = Text(json?["error"]?["code"]);
        var message = Text(json?["error"]?["message"]);
        return (int)status switch
        {
            401 => new AccountException("Your Microsoft sign-in has expired or was withdrawn. Sign in again.", needsSignIn: true),
            403 => new AccountException($"Purrch isn't allowed to read {what}. Check the app registration's API permissions, then sign in again."),
            404 when code is "MailboxNotEnabledForRESTAPI" or "MailboxNotSupportedForRESTAPI"
                => new AccountException($"This account has no mailbox, so no {what}."),
            429 => new AccountException("Microsoft is busy. Purrch tries again in a few minutes."),
            >= 500 => new AccountException("Microsoft Graph is having trouble right now. Purrch tries again in a few minutes."),
            var other => new AccountException($"{message ?? code ?? "Microsoft Graph refused"} (HTTP {other})"),
        };
    }

    private static AgendaEvent? Event(JsonNode e, Guid key)
    {
        if (Flag(e["isCancelled"]) == true || Text(e["id"]) is not { } id) return null;
        var allDay = Flag(e["isAllDay"]) == true;
        if (Time(e["start"], allDay) is not { } start) return null;
        var end = Time(e["end"], allDay) ?? start;
        if (allDay && end <= start) end = start.AddDays(1);
        var location = Text(e["location"]?["displayName"])?.Trim();
        return new AgendaEvent($"ms:{id}", Clean(Text(e["subject"])), start, end, allDay,
            string.IsNullOrEmpty(location) ? null : location, key);
    }

    /// Graph's { dateTime, timeZone } in local time. All-day events run midnight to midnight wherever you are.
    private static DateTime? Time(JsonNode? node, bool allDay)
    {
        if (Text(node?["dateTime"]) is not { } text
            || !DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var time)) return null;
        if (allDay) return DateTime.SpecifyKind(time.Date, DateTimeKind.Local);
        var zone = Text(node?["timeZone"]) ?? "UTC";
        if (zone == TimeZoneInfo.Local.Id) return DateTime.SpecifyKind(time, DateTimeKind.Local);
        if (zone is "UTC" or "tzone://Microsoft/Utc") return DateTime.SpecifyKind(time, DateTimeKind.Utc).ToLocalTime();
        try
        {
            return TimeZoneInfo.ConvertTime(DateTime.SpecifyKind(time, DateTimeKind.Unspecified),
                TimeZoneInfo.FindSystemTimeZoneById(zone), TimeZoneInfo.Local);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException or ArgumentException)
        {
            return DateTime.SpecifyKind(time, DateTimeKind.Local);
        }
    }

    // MARK: Token cache

    private static void ReadCache(TokenCacheNotificationArgs args)
    {
        lock (CacheGate)
        {
            try
            {
                var bytes = File.Exists(CachePath)
                    ? ProtectedData.Unprotect(File.ReadAllBytes(CachePath), Entropy, DataProtectionScope.CurrentUser)
                    : null;
                args.TokenCache.DeserializeMsalV3(bytes, shouldClearExistingCache: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or CryptographicException or MsalClientException)
            {
                // Unreadable (another Windows user's copy, say): start again, which asks for a new sign-in.
                Log.Write($"msal.cache unreadable: {e.GetType().Name}");
            }
        }
    }

    private static void WriteCache(TokenCacheNotificationArgs args)
    {
        if (!args.HasStateChanged) return;
        lock (CacheGate)
        {
            try
            {
                var bytes = ProtectedData.Protect(args.TokenCache.SerializeMsalV3(), Entropy, DataProtectionScope.CurrentUser);
                File.WriteAllBytes(CachePath + ".tmp", bytes);
                File.Move(CachePath + ".tmp", CachePath, overwrite: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or CryptographicException)
            {
                Log.Write($"msal.cache not saved: {e.GetType().Name}");
            }
        }
    }
}
