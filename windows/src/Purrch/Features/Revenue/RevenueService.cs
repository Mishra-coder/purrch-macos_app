using System.ComponentModel;
using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Threading;
using Purrch.Core;

namespace Purrch.Features.Revenue;

/// Cached Stripe figures: today's sales and the last seven days, in one currency.
public sealed class RevenueSnapshot
{
    public sealed record Sale(string Id, double Amount, string Label, DateTime Created);
    public sealed record Day(DateTime Date, double Total);

    /// Short hash of the key it was fetched with, so another account's figures are never shown.
    public string Account { get; set; } = "";
    public string Currency { get; set; } = "usd";
    public DateTime FetchedAt { get; set; }
    /// Today's sales, newest first.
    public List<Sale> Sales { get; set; } = new();
    /// Seven days, oldest first, ending with the day it was fetched.
    public List<Day> Week { get; set; } = new();

    public double TodayTotal() => Sales.Sum(s => s.Amount);
}

/// Today's sales from Stripe's charges API, with the user's restricted read-only key.
/// Refreshes every minute while a view shows it; the last result is kept in revenue.json.
public sealed class RevenueService
{
    public static RevenueService Shared { get; } = new();

    /// Credential Manager entry for the key ("Purrch:stripe").
    public const string SecretName = "stripe";

    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(1);
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    /// Currencies Stripe counts in whole units rather than cents.
    private static readonly HashSet<string> ZeroDecimal = new()
    {
        "bif", "clp", "djf", "gnf", "jpy", "kmf", "krw", "mga",
        "pyg", "rwf", "ugx", "vnd", "vuv", "xaf", "xof", "xpf",
    };

    private readonly DispatcherTimer _timer = new() { Interval = RefreshInterval };
    private int _users;
    private int _generation;

    /// The snapshot, loading state or message changed.
    public event Action? Changed;

    public RevenueSnapshot? Snapshot { get; private set; }
    public bool IsLoading { get; private set; }
    public string? Message { get; private set; }
    /// Stripe turned the key down (wrong, revoked or missing Charges: read), so the cache isn't shown.
    public bool KeyRejected { get; private set; }

    public static bool HasKey => SecretStore.Get(SecretName).Trim().Length > 0;

    /// The currency from Settings as Stripe writes it ("usd").
    public static string Currency
    {
        get
        {
            var code = AppSettings.Shared.RevenueCurrency.Trim().ToLowerInvariant();
            return code.Length == 0 ? "usd" : code;
        }
    }

    private RevenueService()
    {
        Snapshot = Store.Load<RevenueSnapshot?>("revenue", null);
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
        if (e.PropertyName != nameof(AppSettings.RevenueCurrency)) return;
        // A new currency wins over a fetch still on its way for the old one.
        _generation++;
        IsLoading = false;
        if (_users > 0) Refresh(force: true);
    }

    /// Call on the UI thread. Without force, figures younger than a minute are kept.
    public async void Refresh(bool force)
    {
        if (IsLoading) return;
        var key = SecretStore.Get(SecretName).Trim();
        var currency = Currency;
        if (key.Length == 0)
        {
            Message = null;
            KeyRejected = false;
            Changed?.Invoke();
            return;
        }
        // The key or currency may have changed while no view was listening.
        var account = Fingerprint(key);
        if (Snapshot is { } old && (old.Account != account || old.Currency != currency)) Snapshot = null;
        if (!force && Snapshot is { } s && s.FetchedAt.Date == DateTime.Today && DateTime.Now - s.FetchedAt < RefreshInterval) return;

        var generation = ++_generation;
        IsLoading = true;
        Changed?.Invoke();

        var (snapshot, message, rejected) = await Load(key, currency);
        if (generation != _generation) return;
        IsLoading = false;
        Message = message;
        KeyRejected = rejected;
        if (snapshot != null)
        {
            snapshot.Account = account;
            Snapshot = snapshot;
            Store.Save("revenue", snapshot);
        }
        Changed?.Invoke();
    }

    private static async Task<(RevenueSnapshot?, string?, bool)> Load(string key, string currency)
    {
        try
        {
            return (await Fetch(key, currency), null, false);
        }
        catch (StripeException e)
        {
            return (null, e.Message, e.KeyRejected);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            return (null, "Couldn't reach Stripe. Check your internet connection.", false);
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException)
        {
            Log.Write($"Revenue: {e.Message}");
            return (null, "Unexpected response from Stripe.", false);
        }
    }

    private static async Task<RevenueSnapshot> Fetch(string key, string currency)
    {
        var today = DateTime.Today;
        var weekStart = today.AddDays(-6);
        var divisor = ZeroDecimal.Contains(currency) ? 1.0 : 100.0;
        var sales = new List<RevenueSnapshot.Sale>();
        foreach (var charge in await FetchCharges(weekStart, key))
        {
            if (Text(charge["status"]) != "succeeded"
                || Flag(charge["paid"]) == false
                || Flag(charge["refunded"]) == true
                || Text(charge["currency"])?.ToLowerInvariant() != currency) continue;
            var amount = Number(charge["amount"]) - Number(charge["amount_refunded"]);
            var created = DateTimeOffset.FromUnixTimeSeconds((long)Number(charge["created"])).LocalDateTime;
            var billing = charge["billing_details"];
            var label = new[] { Text(billing?["email"]), Text(charge["receipt_email"]), Text(charge["description"]), Text(billing?["name"]) }
                .FirstOrDefault(t => !string.IsNullOrEmpty(t)) ?? "Payment";
            sales.Add(new RevenueSnapshot.Sale(Text(charge["id"]) ?? Guid.NewGuid().ToString(), amount / divisor, label, created));
        }

        return new RevenueSnapshot
        {
            Currency = currency,
            FetchedAt = DateTime.Now,
            Sales = sales.Where(s => s.Created >= today).ToList(),
            Week = Enumerable.Range(0, 7).Select(i =>
            {
                var day = weekStart.AddDays(i);
                return new RevenueSnapshot.Day(day, sales.Where(s => s.Created >= day && s.Created < day.AddDays(1)).Sum(s => s.Amount));
            }).ToList(),
        };
    }

    /// Charges created since the start of the week, newest first, so today's always arrive in the first page.
    private static async Task<List<JsonNode>> FetchCharges(DateTime since, string key)
    {
        var all = new List<JsonNode>();
        string? startingAfter = null;
        var from = new DateTimeOffset(since).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        for (var page = 0; page < 5; page++)
        {
            var url = $"https://api.stripe.com/v1/charges?created%5Bgte%5D={from}&limit=100"
                      + (startingAfter == null ? "" : $"&starting_after={Uri.EscapeDataString(startingAfter)}");
            var json = await Get(url, key);
            var data = (json["data"] as JsonArray)?.OfType<JsonNode>().ToList() ?? new List<JsonNode>();
            all.AddRange(data);
            if (Flag(json["has_more"]) != true || data.Count == 0 || Text(data[^1]["id"]) is not { } last) break;
            startingAfter = last;
        }
        return all;
    }

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
            var message = Text(json?["error"]?["message"]);
            throw (int)response.StatusCode switch
            {
                401 => new StripeException("Stripe rejected the key. Check it in Settings → Integrations.", true),
                403 => new StripeException("This key can't read charges. Give it Charges: Read in the Stripe dashboard.", true),
                429 => new StripeException("Stripe is rate limiting this key. Purrch tries again in a minute.", false),
                >= 500 => new StripeException("Stripe is having trouble right now. Purrch tries again in a minute.", false),
                var status => new StripeException($"{message ?? response.ReasonPhrase} (HTTP {status})", false),
            };
        }
        return json ?? throw new JsonException("Empty response");
    }

    private static string Fingerprint(string key) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..12];

    // MARK: JSON

    private static string? Text(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static double Number(JsonNode? node) => node is JsonValue v && v.TryGetValue<double>(out var d) ? d : 0;

    private static bool? Flag(JsonNode? node) => node is JsonValue v && v.TryGetValue<bool>(out var b) ? b : null;

    // MARK: Formatting

    private static readonly Dictionary<string, string> Symbols = new();

    /// "$1,250" or "$12.50": cents only when there are some, like the Mac app.
    public static string Money(double value, string currency)
    {
        var format = (NumberFormatInfo)CultureInfo.CurrentCulture.NumberFormat.Clone();
        format.CurrencySymbol = Symbol(currency);
        return value.ToString(Math.Round(value) == value ? "C0" : "C2", format);
    }

    /// The symbol for an ISO currency code, from a culture that uses it; the code itself if none does.
    private static string Symbol(string currency)
    {
        var code = currency.ToUpperInvariant();
        if (Symbols.TryGetValue(code, out var known)) return known;
        string? symbol = null;
        try
        {
            if (new RegionInfo(CultureInfo.CurrentCulture.Name).ISOCurrencySymbol == code)
                symbol = CultureInfo.CurrentCulture.NumberFormat.CurrencySymbol;
        }
        catch (ArgumentException) { }
        symbol ??= CultureInfo.GetCultures(CultureTypes.SpecificCultures)
            .Select(c =>
            {
                try { return new RegionInfo(c.Name); }
                catch (ArgumentException) { return null; }
            })
            .FirstOrDefault(r => r?.ISOCurrencySymbol == code)?.CurrencySymbol;
        symbol ??= code + " ";
        Symbols[code] = symbol;
        return symbol;
    }

    private sealed class StripeException(string message, bool keyRejected) : Exception(message)
    {
        public bool KeyRejected { get; } = keyRejected;
    }
}
