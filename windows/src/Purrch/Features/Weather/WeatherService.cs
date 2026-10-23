using System.ComponentModel;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Threading;
using Purrch.Core;
using Windows.Devices.Geolocation;

namespace Purrch.Features.Weather;

/// Cached weather snapshot. Temperatures are stored in Celsius, wind in km/h.
public sealed class WeatherSnapshot
{
    public sealed record Hour(string Time, double Temperature, int Code, bool IsDay); // "yyyy-MM-ddTHH:mm", local to the place
    public sealed record Day(string Date, double Min, double Max, int Code);          // "yyyy-MM-dd"

    public string Place { get; set; } = "";
    /// The city from Settings this was fetched for; empty for the PC's location.
    public string City { get; set; } = "";
    public DateTime FetchedAt { get; set; }
    public double Temperature { get; set; }
    public double Apparent { get; set; }
    public int Code { get; set; }
    public bool IsDay { get; set; } = true;
    public double Wind { get; set; }
    public double Humidity { get; set; }
    public List<Hour> Hourly { get; set; } = new();
    public List<Day> Daily { get; set; } = new();
}

/// WMO weather codes, as Open-Meteo reports them.
public static class WeatherCode
{
    /// Colour emoji stand in for the Mac app's multicolour SF Symbols; Segoe has no weather set.
    public static string Emoji(int code, bool isDay = true) => code switch
    {
        0 => isDay ? "\u2600\uFE0F" : "\U0001F319",
        1 or 2 => isDay ? "\U0001F324\uFE0F" : "\u2601\uFE0F",
        3 => "\u2601\uFE0F",
        45 or 48 => "\U0001F32B\uFE0F",
        >= 51 and <= 57 => "\U0001F327\uFE0F",
        61 or 63 or 65 or 66 or 67 or 82 => "\U0001F327\uFE0F",
        (>= 71 and <= 77) or 85 or 86 => "\U0001F328\uFE0F",
        80 or 81 => isDay ? "\U0001F326\uFE0F" : "\U0001F327\uFE0F",
        >= 95 and <= 99 => "\u26C8\uFE0F",
        _ => "\u2601\uFE0F",
    };

    public static string Text(int code) => code switch
    {
        0 => "Clear",
        1 => "Mostly clear",
        2 => "Partly cloudy",
        3 => "Overcast",
        45 or 48 => "Fog",
        >= 51 and <= 55 => "Drizzle",
        56 or 57 => "Freezing drizzle",
        >= 61 and <= 65 => "Rain",
        66 or 67 => "Freezing rain",
        >= 71 and <= 77 => "Snow",
        >= 80 and <= 82 => "Showers",
        85 or 86 => "Snow showers",
        95 => "Thunderstorm",
        96 or 99 => "Storm with hail",
        _ => "—",
    };
}

/// Forecast from Open-Meteo (free, no key) for the city in Settings, or for where this PC is.
/// Refreshes every 20 minutes while a view shows it; the last forecast is kept in weather.json.
public sealed class WeatherService
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(20);
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    public static WeatherService Shared { get; } = new();

    private readonly DispatcherTimer _timer = new() { Interval = RefreshInterval };
    private int _users;
    private int _generation;

    /// The snapshot, loading state or message changed, or the units did.
    public event Action? Changed;

    public WeatherSnapshot? Snapshot { get; private set; }
    public bool IsLoading { get; private set; }
    public string? Message { get; private set; }
    /// Windows refused location (it's off, or off for desktop apps), so a city is needed.
    public bool LocationDenied { get; private set; }

    private WeatherService()
    {
        Snapshot = Store.Load<WeatherSnapshot?>("weather", null);
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
        switch (e.PropertyName)
        {
            case nameof(AppSettings.WeatherCity):
                // A new city wins over a fetch still on its way for the old one.
                _generation++;
                IsLoading = false;
                Refresh(force: true);
                break;
            case nameof(AppSettings.UseFahrenheit):
                Changed?.Invoke();
                break;
        }
    }

    /// Call on the UI thread. Without force, a forecast younger than 20 minutes is kept.
    public async void Refresh(bool force)
    {
        if (IsLoading) return;
        var city = AppSettings.Shared.WeatherCity.Trim();
        // The city may have changed while no view was listening; a forecast for the old one is stale.
        if (!force && Snapshot is { } s && s.City == city && DateTime.Now - s.FetchedAt < RefreshInterval) return;
        var generation = ++_generation;
        IsLoading = true;
        Message = null;
        Changed?.Invoke();

        var (snapshot, message) = await Load(city);
        if (generation != _generation) return;
        IsLoading = false;
        Message = message;
        if (snapshot != null)
        {
            snapshot.City = city;
            Snapshot = snapshot;
            Store.Save("weather", snapshot);
        }
        Changed?.Invoke();
    }

    private async Task<(WeatherSnapshot?, string?)> Load(string city)
    {
        try
        {
            if (city.Length > 0)
            {
                var url = $"https://geocoding-api.open-meteo.com/v1/search?name={Uri.EscapeDataString(city)}&count=1";
                var geo = JsonSerializer.Deserialize<GeoResponse>(await Http.GetStringAsync(url), Json);
                if (geo?.Results is not [var r, ..]) return (null, $"Couldn't find “{city}”. Check the city in Settings.");
                return (await Fetch(r.Latitude, r.Longitude, r.Name), null);
            }

            LocationDenied = false;
            if (await Locate() is not { } here)
                return (null, LocationDenied
                    ? "Location is off for desktop apps. Set a city in Settings → Integrations."
                    : "Location unavailable. Set a city in Settings → Integrations.");
            var forecast = await Fetch(here.Lat, here.Lon, null);
            forecast.Place = await PlaceName(here.Lat, here.Lon);
            return (forecast, null);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            return (null, city.Length > 0 ? "Couldn't reach the weather service." : "Couldn't load the forecast.");
        }
        catch (Exception e) when (e is JsonException or NotSupportedException or ArgumentOutOfRangeException)
        {
            Log.Write($"Weather: {e.Message}");
            return (null, "Couldn't load the forecast.");
        }
    }

    /// One position fix through Windows location, if it is on and Purrch may use it.
    private async Task<(double Lat, double Lon)?> Locate()
    {
        try
        {
            var access = await Geolocator.RequestAccessAsync();
            if (access != GeolocationAccessStatus.Allowed)
            {
                LocationDenied = access == GeolocationAccessStatus.Denied;
                return null;
            }
            var locator = new Geolocator { DesiredAccuracyInMeters = 1000 };
            var position = await locator.GetGeopositionAsync(TimeSpan.FromMinutes(10), TimeSpan.FromSeconds(10))
                .AsTask().WaitAsync(TimeSpan.FromSeconds(15));
            var p = position.Coordinate.Point.Position;
            return (p.Latitude, p.Longitude);
        }
        catch (Exception e)
        {
            // Location service stopped, no fix in time, or denied while asking (UnauthorizedAccessException).
            Log.Write($"Weather location unavailable: {e.Message}");
            LocationDenied = e is UnauthorizedAccessException;
            return null;
        }
    }

    /// Town name for a position. Windows has no keyless reverse geocoder for desktop apps, so this asks
    /// BigDataCloud's free client endpoint; without it the card says "Current location".
    private static async Task<string> PlaceName(double lat, double lon)
    {
        try
        {
            var url = string.Create(CultureInfo.InvariantCulture,
                $"https://api.bigdatacloud.net/data/reverse-geocode-client?latitude={lat:0.####}&longitude={lon:0.####}&localityLanguage=en");
            var place = JsonSerializer.Deserialize<PlaceResponse>(await Http.GetStringAsync(url),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            var name = new[] { place?.City, place?.Locality }.FirstOrDefault(n => !string.IsNullOrWhiteSpace(n));
            return name ?? "Current location";
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException)
        {
            return "Current location";
        }
    }

    private async Task<WeatherSnapshot> Fetch(double lat, double lon, string? place)
    {
        var url = string.Create(CultureInfo.InvariantCulture,
                      $"https://api.open-meteo.com/v1/forecast?latitude={lat:0.0000}&longitude={lon:0.0000}") +
            "&current=temperature_2m,apparent_temperature,weather_code,wind_speed_10m,relative_humidity_2m,is_day" +
            "&hourly=temperature_2m,weather_code,is_day" +
            "&daily=weather_code,temperature_2m_max,temperature_2m_min" +
            "&timezone=auto&forecast_days=7";
        var r = JsonSerializer.Deserialize<ForecastResponse>(await Http.GetStringAsync(url), Json);
        if (r?.Current is null || r.Hourly is null || r.Daily is null) throw new JsonException("Incomplete forecast");

        // The next 12 hours, starting with the current one ("2026-10-23T14:00").
        var hourPrefix = r.Current.Time[..Math.Min(13, r.Current.Time.Length)] + ":00";
        var start = Math.Max(0, r.Hourly.Time.FindIndex(t => string.CompareOrdinal(t, hourPrefix) >= 0));
        var end = Math.Min(r.Hourly.Time.Count, start + 12);
        var hours = Enumerable.Range(start, Math.Max(0, end - start)).Select(i =>
            new WeatherSnapshot.Hour(r.Hourly.Time[i], r.Hourly.Temperature2m[i], r.Hourly.WeatherCode[i], r.Hourly.IsDay[i] == 1)).ToList();
        var days = r.Daily.Time.Select((d, i) =>
            new WeatherSnapshot.Day(d, r.Daily.Temperature2mMin[i], r.Daily.Temperature2mMax[i], r.Daily.WeatherCode[i])).ToList();

        return new WeatherSnapshot
        {
            Place = place ?? "",
            FetchedAt = DateTime.Now,
            Temperature = r.Current.Temperature2m,
            Apparent = r.Current.ApparentTemperature,
            Code = r.Current.WeatherCode,
            IsDay = r.Current.IsDay == 1,
            Wind = r.Current.WindSpeed10m,
            Humidity = r.Current.RelativeHumidity2m,
            Hourly = hours,
            Daily = days,
        };
    }

    // MARK: Formatting

    public static string Temp(double celsius)
    {
        var value = AppSettings.Shared.UseFahrenheit ? celsius * 9 / 5 + 32 : celsius;
        return $"{(int)Math.Round(value, MidpointRounding.AwayFromZero)}°";
    }

    public static string Wind(double kmh) => AppSettings.Shared.UseFahrenheit
        ? $"{(int)Math.Round(kmh * 0.621371, MidpointRounding.AwayFromZero)} mph"
        : $"{(int)Math.Round(kmh, MidpointRounding.AwayFromZero)} km/h";

    // MARK: Responses

    private sealed record GeoResponse(List<GeoResult>? Results);
    private sealed record GeoResult(string Name, double Latitude, double Longitude);
    private sealed record PlaceResponse(string? City, string? Locality);

    private sealed record ForecastResponse(CurrentBlock Current, HourlyBlock Hourly, DailyBlock Daily);

    private sealed record CurrentBlock(
        string Time,
        [property: JsonPropertyName("temperature_2m")] double Temperature2m,
        double ApparentTemperature,
        int WeatherCode,
        [property: JsonPropertyName("wind_speed_10m")] double WindSpeed10m,
        [property: JsonPropertyName("relative_humidity_2m")] double RelativeHumidity2m,
        int IsDay);

    private sealed record HourlyBlock(
        List<string> Time,
        [property: JsonPropertyName("temperature_2m")] List<double> Temperature2m,
        List<int> WeatherCode,
        List<int> IsDay);

    private sealed record DailyBlock(
        List<string> Time,
        List<int> WeatherCode,
        [property: JsonPropertyName("temperature_2m_max")] List<double> Temperature2mMax,
        [property: JsonPropertyName("temperature_2m_min")] List<double> Temperature2mMin);
}
