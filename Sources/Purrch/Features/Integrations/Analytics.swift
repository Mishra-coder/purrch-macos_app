import Charts
import SwiftUI

struct AnalyticsBreakdownRow: Identifiable {
    var id: String { name }
    let name: String
    let visitors: Int
}

struct AnalyticsPoint: Identifiable {
    var id: Int { hour }
    let hour: Int
    let visitors: Int
}

@MainActor
final class AnalyticsService: ObservableObject {
    static let shared = AnalyticsService()

    @Published private(set) var live: Int?
    @Published private(set) var visitors = 0
    @Published private(set) var pageviews = 0
    @Published private(set) var bounceRate = 0.0
    @Published private(set) var visitDuration = 0.0
    @Published private(set) var topPages: [AnalyticsBreakdownRow] = []
    @Published private(set) var topSources: [AnalyticsBreakdownRow] = []
    @Published private(set) var hourly: [AnalyticsPoint] = []
    @Published private(set) var error: String?
    @Published private(set) var lastUpdated: Date?
    @Published private(set) var isLoading = false

    func refresh() async {
        let settings = AppSettings.shared
        let key = settings.plausibleKey
        let site = settings.plausibleSite.trimmingCharacters(in: .whitespaces)
        var host = settings.plausibleHost.trimmingCharacters(in: .whitespaces)
        if host.isEmpty { host = "https://plausible.io" }
        if !host.lowercased().hasPrefix("http") { host = "https://" + host }
        while host.hasSuffix("/") { host.removeLast() }
        guard !key.isEmpty, !site.isEmpty, !isLoading else { return }
        isLoading = true
        defer { isLoading = false }

        func url(_ path: String, _ params: [String: String]) -> URL? {
            var components = URLComponents(string: host + path)
            components?.queryItems = [URLQueryItem(name: "site_id", value: site)]
                + params.sorted { $0.key < $1.key }.map { URLQueryItem(name: $0.key, value: $0.value) }
            return components?.url
        }
        guard let liveURL = url("/api/v1/stats/realtime/visitors", [:]),
              let aggregateURL = url("/api/v1/stats/aggregate",
                                     ["period": "day", "metrics": "visitors,pageviews,bounce_rate,visit_duration"]),
              let pagesURL = url("/api/v1/stats/breakdown", ["period": "day", "property": "event:page", "limit": "5"]),
              let sourcesURL = url("/api/v1/stats/breakdown", ["period": "day", "property": "visit:source", "limit": "5"]),
              let seriesURL = url("/api/v1/stats/timeseries", ["period": "day"]) else {
            error = "The Plausible host in Settings isn't a valid URL."
            return
        }
        do {
            async let liveJSON = HTTPJSON.get(liveURL, bearer: key)
            async let aggregateJSON = HTTPJSON.get(aggregateURL, bearer: key)
            async let pagesJSON = HTTPJSON.get(pagesURL, bearer: key)
            async let sourcesJSON = HTTPJSON.get(sourcesURL, bearer: key)
            async let seriesJSON = HTTPJSON.get(seriesURL, bearer: key)

            live = (try await liveJSON as? NSNumber)?.intValue ?? 0
            let aggregate = (try await aggregateJSON as? [String: Any])?["results"] as? [String: Any] ?? [:]
            func metric(_ name: String) -> Double {
                ((aggregate[name] as? [String: Any])?["value"] as? NSNumber)?.doubleValue ?? 0
            }
            visitors = Int(metric("visitors"))
            pageviews = Int(metric("pageviews"))
            bounceRate = metric("bounce_rate")
            visitDuration = metric("visit_duration")
            topPages = Self.rows(try await pagesJSON, property: "page")
            topSources = Self.rows(try await sourcesJSON, property: "source")
            let series = (try await seriesJSON as? [String: Any])?["results"] as? [[String: Any]] ?? []
            hourly = series.enumerated().map { index, item in
                let date = item["date"] as? String ?? ""
                // "2026-10-17 13:00:00" -> 13
                let hour = date.split(separator: " ").last.flatMap { Int($0.prefix(2)) } ?? index
                return AnalyticsPoint(hour: hour, visitors: (item["visitors"] as? NSNumber)?.intValue ?? 0)
            }
            error = nil
            lastUpdated = Date()
        } catch {
            self.error = error.localizedDescription
        }
    }

    private static func rows(_ json: Any, property: String) -> [AnalyticsBreakdownRow] {
        let results = (json as? [String: Any])?["results"] as? [[String: Any]] ?? []
        return results.map {
            AnalyticsBreakdownRow(name: ($0[property] as? String).flatMap { $0.isEmpty ? nil : $0 } ?? "Direct / none",
                                  visitors: ($0["visitors"] as? NSNumber)?.intValue ?? 0)
        }
    }
}

struct AnalyticsModule: View {
    @Environment(\.theme) private var theme
    @StateObject private var service = AnalyticsService.shared
    @ObservedObject private var settings = AppSettings.shared

    private var configured: Bool {
        !settings.plausibleKey.isEmpty && !settings.plausibleSite.trimmingCharacters(in: .whitespaces).isEmpty
    }

    var body: some View {
        Group {
            if !configured {
                MissingSettingView(icon: "chart.line.uptrend.xyaxis", title: "Connect Plausible",
                                   subtitle: settings.plausibleKey.isEmpty
                                       ? "Add your Plausible API key and site domain in Settings."
                                       : "Add your site domain (e.g. example.com) in Settings.")
            } else if let error = service.error, service.lastUpdated == nil {
                VStack(spacing: 10) {
                    EmptyState(icon: "exclamationmark.triangle", title: "Couldn't load Plausible", subtitle: error)
                        .frame(maxHeight: 140)
                    HStack {
                        PillButton(title: "Retry", icon: "arrow.clockwise") { Task { await service.refresh() } }
                        PillButton(title: "Open Settings", icon: "gearshape") { NotchState.shared.showSettings = true }
                    }
                }
            } else if service.lastUpdated == nil {
                ProgressView().controlSize(.small).frame(maxWidth: .infinity, maxHeight: .infinity)
            } else {
                HStack(spacing: 10) {
                    VStack(spacing: 10) {
                        statsRow
                        chartCard
                    }
                    VStack(spacing: 10) {
                        listCard("Top pages", icon: "doc.text", rows: service.topPages)
                        listCard("Top sources", icon: "arrow.down.right.circle", rows: service.topSources)
                    }
                    .frame(width: 250)
                }
            }
        }
        .task(id: settings.plausibleSite + settings.plausibleHost + String(settings.plausibleKey.count)) {
            guard configured else { return }
            while !Task.isCancelled {
                await service.refresh()
                try? await Task.sleep(for: .seconds(30))
            }
        }
    }

    private var accent: Color { theme.accent(for: .analytics) }

    private var statsRow: some View {
        HStack(spacing: 8) {
            VStack(alignment: .leading, spacing: 2) {
                HStack(spacing: 6) {
                    PulsingDot()
                    Text("LIVE")
                        .font(.system(size: 10, weight: .bold))
                        .tracking(1)
                        .foregroundStyle(theme.secondaryText)
                }
                Text("\(service.live ?? 0)")
                    .font(.system(size: 34, weight: .bold, design: .rounded))
                    .foregroundStyle(accent)
                    .themeGlow(theme, radius: 5)
                    .monospacedDigit()
                    .contentTransition(.numericText())
            }
            .padding(.horizontal, 12)
            .padding(.vertical, 8)
            .frame(width: 110, alignment: .leading)
            .background(tile)
            metric(CompactNumber.string(Double(service.visitors)), "Visitors")
            metric(CompactNumber.string(Double(service.pageviews)), "Pageviews")
            metric("\(Int(service.bounceRate.rounded()))%", "Bounce")
            metric(Format.duration(service.visitDuration), "Visit")
        }
        .frame(height: 74)
    }

    private var tile: some View {
        RoundedRectangle(cornerRadius: 14, style: .continuous)
            .fill(theme.surface)
            .overlay(RoundedRectangle(cornerRadius: 14, style: .continuous)
                .strokeBorder(theme.border, lineWidth: theme.glow ? 1.2 : 0.8))
    }

    private func metric(_ value: String, _ label: String) -> some View {
        VStack(alignment: .leading, spacing: 3) {
            Text(label.uppercased())
                .font(.system(size: 9, weight: .semibold))
                .tracking(0.8)
                .foregroundStyle(theme.secondaryText)
            Text(value)
                .font(.system(size: 20, weight: .semibold, design: .rounded))
                .monospacedDigit()
                .foregroundStyle(theme.text)
                .lineLimit(1)
                .minimumScaleFactor(0.6)
        }
        .padding(.horizontal, 10)
        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .leading)
        .background(tile)
    }

    private var chartCard: some View {
        Card("Today by hour", icon: "chart.bar.xaxis", accessory: {
            if service.isLoading { ProgressView().controlSize(.mini) }
            else if let updated = service.lastUpdated { Text(updated, style: .time) }
        }) {
            let currentHour = Calendar.current.component(.hour, from: Date())
            Chart(service.hourly) { point in
                BarMark(x: .value("Hour", point.hour), y: .value("Visitors", point.visitors), width: .ratio(0.7))
                    .foregroundStyle(point.hour == currentHour ? accent : accent.opacity(0.5))
                    .cornerRadius(2)
            }
            .chartXScale(domain: -0.5...23.5)
            .chartXAxis {
                AxisMarks(values: [0, 6, 12, 18, 23]) { value in
                    AxisValueLabel {
                        if let hour = value.as(Int.self) { Text(String(format: "%02d", hour)) }
                    }
                    .foregroundStyle(theme.secondaryText)
                }
            }
            .chartYAxis {
                AxisMarks(position: .leading, values: .automatic(desiredCount: 3)) { _ in
                    AxisGridLine().foregroundStyle(theme.text.opacity(0.08))
                    AxisValueLabel().foregroundStyle(theme.secondaryText)
                }
            }
            .font(.system(size: 9))
        }
    }

    private func listCard(_ title: String, icon: String, rows: [AnalyticsBreakdownRow]) -> some View {
        let peak = max(1, rows.map(\.visitors).max() ?? 1)
        return Card(title, icon: icon) {
            if rows.isEmpty {
                Text("No data yet today")
                    .font(.system(size: 11))
                    .foregroundStyle(theme.secondaryText)
            } else {
                VStack(spacing: 3) {
                    ForEach(rows.prefix(5)) { row in
                        HStack(spacing: 6) {
                            Text(row.name)
                                .font(.system(size: 11))
                                .foregroundStyle(theme.text)
                                .lineLimit(1)
                                .truncationMode(.middle)
                            Spacer(minLength: 4)
                            Text(CompactNumber.string(Double(row.visitors)))
                                .font(.system(size: 11, weight: .semibold))
                                .monospacedDigit()
                                .foregroundStyle(theme.text)
                        }
                        .padding(.horizontal, 6)
                        .padding(.vertical, 2)
                        .background(alignment: .leading) {
                            GeometryReader { geo in
                                RoundedRectangle(cornerRadius: 4)
                                    .fill(accent.opacity(0.16))
                                    .frame(width: geo.size.width * CGFloat(row.visitors) / CGFloat(peak))
                            }
                        }
                    }
                }
            }
        }
    }
}

private struct PulsingDot: View {
    @State private var pulse = false

    var body: some View {
        ZStack {
            Circle()
                .fill(Color.green.opacity(0.5))
                .frame(width: 8, height: 8)
                .scaleEffect(pulse ? 2.4 : 1)
                .opacity(pulse ? 0 : 0.8)
            Circle().fill(Color.green).frame(width: 8, height: 8)
        }
        .frame(width: 10, height: 10)
        .onAppear {
            withAnimation(.easeOut(duration: 1.4).repeatForever(autoreverses: false)) { pulse = true }
        }
    }
}
