import SwiftUI
import CoreLocation

// MARK: - Model

/// Cached weather snapshot. Temperatures are stored in Celsius, wind in km/h.
struct WeatherSnapshot: Codable, Equatable {
    struct Hour: Codable, Equatable, Hashable {
        var time: String       // "yyyy-MM-ddTHH:mm" local to the location
        var temperature: Double
        var code: Int
        var isDay: Bool
    }
    struct Day: Codable, Equatable, Hashable {
        var date: String       // "yyyy-MM-dd"
        var min: Double
        var max: Double
        var code: Int
    }
    var place: String
    var fetchedAt: Date
    var temperature: Double
    var apparent: Double
    var code: Int
    var isDay: Bool
    var wind: Double
    var humidity: Double
    var hourly: [Hour]
    var daily: [Day]
}

enum WeatherCode {
    static func symbol(_ code: Int, isDay: Bool = true) -> String {
        switch code {
        case 0: isDay ? "sun.max.fill" : "moon.stars.fill"
        case 1, 2: isDay ? "cloud.sun.fill" : "cloud.moon.fill"
        case 3: "cloud.fill"
        case 45, 48: "cloud.fog.fill"
        case 51...57: "cloud.drizzle.fill"
        case 61, 63, 66: "cloud.rain.fill"
        case 65, 67: "cloud.heavyrain.fill"
        case 71...77, 85, 86: "cloud.snow.fill"
        case 80, 81: isDay ? "cloud.sun.rain.fill" : "cloud.moon.rain.fill"
        case 82: "cloud.heavyrain.fill"
        case 95...99: "cloud.bolt.rain.fill"
        default: "cloud.fill"
        }
    }

    static func text(_ code: Int) -> String {
        switch code {
        case 0: "Clear"
        case 1: "Mostly clear"
        case 2: "Partly cloudy"
        case 3: "Overcast"
        case 45, 48: "Fog"
        case 51...55: "Drizzle"
        case 56, 57: "Freezing drizzle"
        case 61...65: "Rain"
        case 66, 67: "Freezing rain"
        case 71...77: "Snow"
        case 80...82: "Showers"
        case 85, 86: "Snow showers"
        case 95: "Thunderstorm"
        case 96, 99: "Storm with hail"
        default: "—"
        }
    }
}

// MARK: - One-shot location

private final class OneShotLocation: NSObject, CLLocationManagerDelegate {
    private let manager = CLLocationManager()
    private var completion: ((CLLocation?) -> Void)?

    func request(_ completion: @escaping (CLLocation?) -> Void) {
        self.completion = completion
        manager.delegate = self
        manager.desiredAccuracy = kCLLocationAccuracyKilometer
        switch manager.authorizationStatus {
        case .notDetermined:
            manager.requestWhenInUseAuthorization()
        case .denied, .restricted:
            finish(nil)
        default:
            manager.requestLocation()
        }
    }

    func locationManagerDidChangeAuthorization(_ manager: CLLocationManager) {
        guard completion != nil else { return }
        switch manager.authorizationStatus {
        case .notDetermined: break
        case .denied, .restricted: finish(nil)
        default: manager.requestLocation()
        }
    }

    func locationManager(_ manager: CLLocationManager, didUpdateLocations locations: [CLLocation]) {
        finish(locations.last)
    }

    func locationManager(_ manager: CLLocationManager, didFailWithError error: Error) {
        finish(nil)
    }

    private func finish(_ location: CLLocation?) {
        let c = completion
        completion = nil
        DispatchQueue.main.async { c?(location) }
    }
}

// MARK: - Service

@MainActor
final class WeatherService: ObservableObject {
    static let shared = WeatherService()

    @Published private(set) var snapshot: WeatherSnapshot?
    @Published private(set) var isLoading = false
    @Published private(set) var message: String?

    private var locator: OneShotLocation?
    private var timer: Timer?
    private static let refreshInterval: TimeInterval = 20 * 60

    private init() {
        snapshot = Store.load("weather", default: WeatherSnapshot?.none)
    }

    /// Starts the 20-minute refresh cycle and fetches if the cache is stale.
    func start() {
        if timer == nil {
            timer = Timer.scheduledTimer(withTimeInterval: Self.refreshInterval, repeats: true) { [weak self] _ in
                MainActor.assumeIsolated { self?.refresh(force: true) }
            }
        }
        refresh(force: false)
    }

    func refresh(force: Bool) {
        guard !isLoading else { return }
        if !force, let s = snapshot, Date().timeIntervalSince(s.fetchedAt) < Self.refreshInterval { return }
        isLoading = true
        message = nil
        let city = AppSettings.shared.weatherCity.trimmingCharacters(in: .whitespacesAndNewlines)
        if !city.isEmpty {
            Task { await geocodeAndFetch(city) }
        } else {
            let locator = OneShotLocation()
            self.locator = locator
            locator.request { [weak self] location in
                MainActor.assumeIsolated {
                    guard let self else { return }
                    self.locator = nil
                    if let location {
                        Task { await self.fetch(lat: location.coordinate.latitude,
                                                lon: location.coordinate.longitude,
                                                place: nil, location: location) }
                    } else {
                        self.fail("Location unavailable. Set a city in Settings → Weather.")
                    }
                }
            }
        }
    }

    private func fail(_ text: String) {
        isLoading = false
        message = text
    }

    private struct GeoResponse: Decodable {
        struct Result: Decodable { let name: String; let latitude: Double; let longitude: Double; let country_code: String? }
        let results: [Result]?
    }

    private func geocodeAndFetch(_ city: String) async {
        var comps = URLComponents(string: "https://geocoding-api.open-meteo.com/v1/search")!
        comps.queryItems = [.init(name: "name", value: city), .init(name: "count", value: "1")]
        do {
            let (data, _) = try await URLSession.shared.data(from: comps.url!)
            let geo = try JSONDecoder().decode(GeoResponse.self, from: data)
            guard let r = geo.results?.first else { return fail("Couldn't find “\(city)”. Check the city in Settings.") }
            await fetch(lat: r.latitude, lon: r.longitude, place: r.name, location: nil)
        } catch {
            fail("Couldn't reach the weather service.")
        }
    }

    private struct ForecastResponse: Decodable {
        struct Current: Decodable {
            let time: String
            let temperature_2m: Double
            let apparent_temperature: Double
            let weather_code: Int
            let wind_speed_10m: Double
            let relative_humidity_2m: Double
            let is_day: Int
        }
        struct Hourly: Decodable {
            let time: [String]
            let temperature_2m: [Double]
            let weather_code: [Int]
            let is_day: [Int]
        }
        struct Daily: Decodable {
            let time: [String]
            let weather_code: [Int]
            let temperature_2m_max: [Double]
            let temperature_2m_min: [Double]
        }
        let current: Current
        let hourly: Hourly
        let daily: Daily
    }

    private func fetch(lat: Double, lon: Double, place: String?, location: CLLocation?) async {
        var comps = URLComponents(string: "https://api.open-meteo.com/v1/forecast")!
        comps.queryItems = [
            .init(name: "latitude", value: String(format: "%.4f", lat)),
            .init(name: "longitude", value: String(format: "%.4f", lon)),
            .init(name: "current", value: "temperature_2m,apparent_temperature,weather_code,wind_speed_10m,relative_humidity_2m,is_day"),
            .init(name: "hourly", value: "temperature_2m,weather_code,is_day"),
            .init(name: "daily", value: "weather_code,temperature_2m_max,temperature_2m_min"),
            .init(name: "timezone", value: "auto"),
            .init(name: "forecast_days", value: "7"),
        ]
        do {
            let (data, _) = try await URLSession.shared.data(from: comps.url!)
            let r = try JSONDecoder().decode(ForecastResponse.self, from: data)

            var placeName = place ?? ""
            if placeName.isEmpty, let location {
                let marks = try? await CLGeocoder().reverseGeocodeLocation(location)
                placeName = marks?.first?.locality ?? marks?.first?.name ?? "Current location"
            }

            let hourPrefix = String(r.current.time.prefix(13)) + ":00"
            let startIndex = r.hourly.time.firstIndex { $0 >= hourPrefix } ?? 0
            let endIndex = min(r.hourly.time.count, startIndex + 12)
            let hours = (startIndex..<endIndex).map { i in
                WeatherSnapshot.Hour(time: r.hourly.time[i], temperature: r.hourly.temperature_2m[i],
                                     code: r.hourly.weather_code[i], isDay: r.hourly.is_day[i] == 1)
            }
            let days = r.daily.time.indices.map { i in
                WeatherSnapshot.Day(date: r.daily.time[i], min: r.daily.temperature_2m_min[i],
                                    max: r.daily.temperature_2m_max[i], code: r.daily.weather_code[i])
            }
            let snap = WeatherSnapshot(place: placeName, fetchedAt: Date(),
                                       temperature: r.current.temperature_2m, apparent: r.current.apparent_temperature,
                                       code: r.current.weather_code, isDay: r.current.is_day == 1,
                                       wind: r.current.wind_speed_10m, humidity: r.current.relative_humidity_2m,
                                       hourly: hours, daily: days)
            snapshot = snap
            Store.save("weather", snap)
            isLoading = false
            message = nil
        } catch {
            fail("Couldn't load the forecast.")
        }
    }

    // MARK: Formatting

    func temp(_ celsius: Double) -> String {
        let value = AppSettings.shared.useFahrenheit ? celsius * 9 / 5 + 32 : celsius
        return "\(Int(value.rounded()))°"
    }

    func wind(_ kmh: Double) -> String {
        AppSettings.shared.useFahrenheit ? "\(Int((kmh * 0.621371).rounded())) mph" : "\(Int(kmh.rounded())) km/h"
    }
}

// MARK: - View

struct WeatherModule: View {
    @Environment(\.theme) private var theme
    @EnvironmentObject private var settings: AppSettings
    @ObservedObject private var service = WeatherService.shared

    var body: some View {
        Group {
            if let s = service.snapshot {
                HStack(spacing: 10) {
                    nowCard(s).frame(width: 230)
                    VStack(spacing: 10) {
                        hourlyCard(s)
                        dailyCard(s)
                    }
                }
            } else {
                Card("Weather", icon: "cloud.sun") {
                    VStack(spacing: 12) {
                        if service.isLoading {
                            ProgressView().controlSize(.small)
                            Text("Loading forecast…").font(.system(size: 12)).foregroundStyle(theme.secondaryText)
                        } else {
                            EmptyState(icon: "location.slash", title: "No weather yet",
                                       subtitle: service.message ?? "Set a city in Settings → Weather, or allow location access.")
                                .frame(maxHeight: 130)
                            PillButton(title: "Try again", icon: "arrow.clockwise", prominent: true) {
                                service.refresh(force: true)
                            }
                        }
                    }
                    .frame(maxWidth: .infinity, maxHeight: .infinity)
                }
            }
        }
        .onAppear { service.start() }
        .onChange(of: settings.weatherCity) { _, _ in service.refresh(force: true) }
    }

    private func nowCard(_ s: WeatherSnapshot) -> some View {
        Card(s.place.isEmpty ? "Now" : s.place, icon: "location.fill", accessory: {
            Button { service.refresh(force: true) } label: {
                Image(systemName: "arrow.clockwise")
                    .rotationEffect(.degrees(service.isLoading ? 360 : 0))
                    .animation(service.isLoading ? .linear(duration: 1).repeatForever(autoreverses: false) : .default,
                               value: service.isLoading)
            }
            .buttonStyle(.plain)
            .help("Updated \(Format.relative(s.fetchedAt)) ago")
        }) {
            VStack(alignment: .leading, spacing: 6) {
                HStack(alignment: .center, spacing: 10) {
                    Image(systemName: WeatherCode.symbol(s.code, isDay: s.isDay))
                        .symbolRenderingMode(.multicolor)
                        .font(.system(size: 40))
                    Text(service.temp(s.temperature))
                        .font(.system(size: 44, weight: .bold, design: .rounded))
                        .foregroundStyle(theme.text)
                        .monospacedDigit()
                }
                Text(WeatherCode.text(s.code))
                    .font(.system(size: 14, weight: .semibold))
                    .foregroundStyle(theme.text)
                if let today = s.daily.first {
                    Text("H \(service.temp(today.max))  L \(service.temp(today.min))")
                        .font(.system(size: 12)).foregroundStyle(theme.secondaryText).monospacedDigit()
                }
                Spacer(minLength: 0)
                HStack(spacing: 0) {
                    detail("thermometer.medium", "Feels", service.temp(s.apparent))
                    detail("wind", "Wind", service.wind(s.wind))
                    detail("humidity", "Humid", "\(Int(s.humidity))%")
                }
            }
        }
    }

    private func detail(_ icon: String, _ label: String, _ value: String) -> some View {
        VStack(alignment: .leading, spacing: 2) {
            Label(label.uppercased(), systemImage: icon)
                .font(.system(size: 9, weight: .semibold))
                .foregroundStyle(theme.secondaryText)
                .labelStyle(.titleAndIcon)
            Text(value)
                .font(.system(size: 12, weight: .semibold, design: .rounded))
                .foregroundStyle(theme.text)
                .monospacedDigit()
                .lineLimit(1)
                .minimumScaleFactor(0.8)
        }
        .frame(maxWidth: .infinity, alignment: .leading)
    }

    private func hourlyCard(_ s: WeatherSnapshot) -> some View {
        Card("Next 12 hours", icon: "clock") {
            HStack(spacing: 0) {
                ForEach(Array(s.hourly.enumerated()), id: \.offset) { i, h in
                    VStack(spacing: 5) {
                        Text(i == 0 ? "Now" : hourLabel(h.time))
                            .font(.system(size: 10, weight: .semibold))
                            .foregroundStyle(i == 0 ? theme.accent : theme.secondaryText)
                        Image(systemName: WeatherCode.symbol(h.code, isDay: h.isDay))
                            .symbolRenderingMode(.multicolor)
                            .font(.system(size: 16))
                            .frame(height: 20)
                        Text(service.temp(h.temperature))
                            .font(.system(size: 13, weight: .semibold, design: .rounded))
                            .foregroundStyle(theme.text)
                            .monospacedDigit()
                    }
                    .frame(maxWidth: .infinity)
                }
            }
            .frame(maxHeight: .infinity)
        }
    }

    private func dailyCard(_ s: WeatherSnapshot) -> some View {
        let lo = s.daily.map(\.min).min() ?? 0
        let hi = s.daily.map(\.max).max() ?? 1
        return Card("This week", icon: "calendar") {
            HStack(spacing: 0) {
                ForEach(Array(s.daily.enumerated()), id: \.offset) { i, d in
                    VStack(spacing: 4) {
                        Text(i == 0 ? "Today" : dayLabel(d.date))
                            .font(.system(size: 10, weight: .semibold))
                            .foregroundStyle(i == 0 ? theme.accent : theme.secondaryText)
                        Image(systemName: WeatherCode.symbol(d.code))
                            .symbolRenderingMode(.multicolor)
                            .font(.system(size: 15))
                            .frame(height: 18)
                        Text(service.temp(d.max))
                            .font(.system(size: 12, weight: .semibold, design: .rounded))
                            .foregroundStyle(theme.text).monospacedDigit()
                        rangeBar(d, lo: lo, hi: hi)
                        Text(service.temp(d.min))
                            .font(.system(size: 11, design: .rounded))
                            .foregroundStyle(theme.secondaryText).monospacedDigit()
                    }
                    .frame(maxWidth: .infinity)
                }
            }
            .frame(maxHeight: .infinity)
        }
    }

    private func rangeBar(_ d: WeatherSnapshot.Day, lo: Double, hi: Double) -> some View {
        let span = max(hi - lo, 1)
        let height: CGFloat = 20
        let top = CGFloat((hi - d.max) / span) * height
        let bar = max(3, CGFloat((d.max - d.min) / span) * height)
        return ZStack(alignment: .top) {
            Capsule().fill(theme.text.opacity(0.08)).frame(width: 4, height: height)
            Capsule().fill(theme.accent).frame(width: 4, height: bar).offset(y: top)
        }
        .frame(height: height, alignment: .top)
    }

    private func hourLabel(_ time: String) -> String {
        // "yyyy-MM-ddTHH:mm" -> "HH"
        guard let tIndex = time.firstIndex(of: "T") else { return time }
        let hh = Int(time[time.index(after: tIndex)...].prefix(2)) ?? 0
        let df = DateFormatter()
        df.setLocalizedDateFormatFromTemplate("j")
        var comps = DateComponents()
        comps.hour = hh
        guard let date = Calendar.current.date(from: comps) else { return "\(hh)" }
        return df.string(from: date).replacingOccurrences(of: " ", with: "")
    }

    private func dayLabel(_ date: String) -> String {
        let parser = DateFormatter()
        parser.locale = Locale(identifier: "en_US_POSIX")
        parser.timeZone = TimeZone(identifier: "UTC")
        parser.dateFormat = "yyyy-MM-dd"
        guard let d = parser.date(from: date) else { return date }
        let out = DateFormatter()
        out.timeZone = TimeZone(identifier: "UTC")
        out.setLocalizedDateFormatFromTemplate("EEE")
        return out.string(from: d)
    }
}
