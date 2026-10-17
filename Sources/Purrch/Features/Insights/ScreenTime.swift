import SwiftUI
import Combine
import AppKit
import Charts
import CoreGraphics

/// Tracks how long each frontmost app is used, per day. Persists to Store("screentime"), keeps 7 days.
@MainActor
final class ScreenTimeService: ObservableObject {
    static let shared = ScreenTimeService()

    /// "yyyy-MM-dd" -> bundle ID -> seconds
    @Published private(set) var days: [String: [String: Double]]

    private var current: String?
    private var lastTick = Date()
    private var screensAsleep = false
    private var sessionLocked = false
    private var timer: Timer?
    private var started = false
    private var unsavedTicks = 0
    private var observers: [NSObjectProtocol] = []

    private static let idleLimit: TimeInterval = 120
    private static let keepDays = 7

    static let dayFormatter: DateFormatter = {
        let f = DateFormatter()
        f.locale = Locale(identifier: "en_US_POSIX")
        f.dateFormat = "yyyy-MM-dd"
        return f
    }()

    private init() {
        days = Store.load("screentime", default: [String: [String: Double]]())
    }

    func start() {
        guard !started else { return }
        started = true
        prune()
        current = NSWorkspace.shared.frontmostApplication?.bundleIdentifier
        lastTick = Date()

        let ws = NSWorkspace.shared.notificationCenter
        observers.append(ws.addObserver(forName: NSWorkspace.didActivateApplicationNotification, object: nil, queue: .main) { [weak self] note in
            let id = (note.userInfo?[NSWorkspace.applicationUserInfoKey] as? NSRunningApplication)?.bundleIdentifier
            MainActor.assumeIsolated { self?.switchTo(id) }
        })
        let flags: [(Notification.Name, Bool?, Bool?)] = [
            (NSWorkspace.screensDidSleepNotification, true, nil),
            (NSWorkspace.screensDidWakeNotification, false, nil),
            (NSWorkspace.sessionDidResignActiveNotification, nil, true),
            (NSWorkspace.sessionDidBecomeActiveNotification, nil, false),
        ]
        for (name, asleep, locked) in flags {
            observers.append(ws.addObserver(forName: name, object: nil, queue: .main) { [weak self] _ in
                MainActor.assumeIsolated { self?.setState(asleep: asleep, locked: locked) }
            })
        }
        let dnc = DistributedNotificationCenter.default()
        observers.append(dnc.addObserver(forName: .init("com.apple.screenIsLocked"), object: nil, queue: .main) { [weak self] _ in
            MainActor.assumeIsolated { self?.setState(asleep: nil, locked: true) }
        })
        observers.append(dnc.addObserver(forName: .init("com.apple.screenIsUnlocked"), object: nil, queue: .main) { [weak self] _ in
            MainActor.assumeIsolated { self?.setState(asleep: nil, locked: false) }
        })
        observers.append(NotificationCenter.default.addObserver(forName: NSApplication.willTerminateNotification, object: nil, queue: .main) { [weak self] _ in
            MainActor.assumeIsolated { self?.accumulate(); self?.save() }
        })

        timer = Timer.scheduledTimer(withTimeInterval: 5, repeats: true) { [weak self] _ in
            MainActor.assumeIsolated {
                guard let self else { return }
                self.accumulate()
                self.unsavedTicks += 1
                if self.unsavedTicks >= 6 { self.save() }
            }
        }
    }

    private func setState(asleep: Bool?, locked: Bool?) {
        accumulate()
        if let asleep { screensAsleep = asleep }
        if let locked { sessionLocked = locked }
    }

    private func switchTo(_ bundleID: String?) {
        accumulate()
        current = bundleID
    }

    private var isUserActive: Bool {
        guard !screensAsleep, !sessionLocked else { return false }
        let anyEvent = CGEventType(rawValue: ~0) ?? .null
        let idle = CGEventSource.secondsSinceLastEventType(.combinedSessionState, eventType: anyEvent)
        return idle < Self.idleLimit
    }

    private func accumulate() {
        let now = Date()
        let elapsed = min(now.timeIntervalSince(lastTick), 10)
        lastTick = now
        guard elapsed > 0, let current, isUserActive else { return }
        let key = Self.dayFormatter.string(from: now)
        days[key, default: [:]][current, default: 0] += elapsed
    }

    private func save() {
        unsavedTicks = 0
        prune()
        Store.save("screentime", days)
    }

    private func prune() {
        let cutoff = Calendar.current.date(byAdding: .day, value: -(Self.keepDays - 1),
                                           to: Calendar.current.startOfDay(for: Date())) ?? Date()
        let cutoffKey = Self.dayFormatter.string(from: cutoff)
        let stale = days.keys.filter { $0 < cutoffKey }
        if !stale.isEmpty { stale.forEach { days.removeValue(forKey: $0) } }
    }

    // MARK: Queries

    var todayKey: String { Self.dayFormatter.string(from: Date()) }

    var todayTotal: TimeInterval { (days[todayKey] ?? [:]).values.reduce(0, +) }

    /// Apps used today, most used first.
    var todayApps: [(bundleID: String, seconds: TimeInterval)] {
        (days[todayKey] ?? [:]).map { ($0.key, $0.value) }.sorted { $0.1 > $1.1 }
    }

    /// The last 7 days (oldest first) with total seconds.
    var week: [(date: Date, seconds: TimeInterval)] {
        let cal = Calendar.current
        let today = cal.startOfDay(for: Date())
        return (0..<Self.keepDays).reversed().compactMap { offset in
            guard let d = cal.date(byAdding: .day, value: -offset, to: today) else { return nil }
            let total = (days[Self.dayFormatter.string(from: d)] ?? [:]).values.reduce(0, +)
            return (d, total)
        }
    }
}

// MARK: - App metadata cache

@MainActor
enum AppInfoCache {
    private static var names: [String: String] = [:]
    private static var icons: [String: NSImage] = [:]

    static func name(_ bundleID: String) -> String {
        if let n = names[bundleID] { return n }
        var result = bundleID.components(separatedBy: ".").last ?? bundleID
        if let url = NSWorkspace.shared.urlForApplication(withBundleIdentifier: bundleID) {
            result = FileManager.default.displayName(atPath: url.path)
            if result.hasSuffix(".app") { result = String(result.dropLast(4)) }
        }
        names[bundleID] = result
        return result
    }

    static func icon(_ bundleID: String) -> NSImage {
        if let i = icons[bundleID] { return i }
        let image: NSImage
        if let url = NSWorkspace.shared.urlForApplication(withBundleIdentifier: bundleID) {
            image = NSWorkspace.shared.icon(forFile: url.path)
        } else {
            image = NSImage(systemSymbolName: "app.dashed", accessibilityDescription: nil) ?? NSImage()
        }
        icons[bundleID] = image
        return image
    }
}

enum DurationText {
    static func short(_ seconds: TimeInterval) -> String {
        let m = Int(seconds) / 60
        if m >= 60 { return "\(m / 60)h \(m % 60)m" }
        if m >= 1 { return "\(m)m" }
        return "<1m"
    }
}

// MARK: - View

struct ScreenTimeModule: View {
    @Environment(\.theme) private var theme
    @ObservedObject private var service = ScreenTimeService.shared
    /// Ticks the view while open so totals update live.
    @State private var tick = Date()
    private let refresh = Timer.publish(every: 5, on: .main, in: .common).autoconnect()

    var body: some View {
        HStack(spacing: 10) {
            overviewCard.frame(width: 330)
            topAppsCard
        }
        .onReceive(refresh) { tick = $0 }
    }

    private var overviewCard: some View {
        let week = service.week
        let average = week.map(\.seconds).reduce(0, +) / Double(max(1, week.filter { $0.seconds > 0 }.count))
        return Card("Screen Time", icon: "hourglass", accessory: { Text("Avg \(DurationText.short(average))") }) {
            VStack(alignment: .leading, spacing: 8) {
                VStack(alignment: .leading, spacing: 0) {
                    Text(DurationText.short(service.todayTotal))
                        .font(.system(size: 34, weight: .bold, design: .rounded))
                        .foregroundStyle(theme.text)
                        .monospacedDigit()
                    Text("today").font(.system(size: 12)).foregroundStyle(theme.secondaryText)
                }
                Chart(week, id: \.date) { day in
                    BarMark(x: .value("Day", day.date, unit: .day),
                            y: .value("Hours", day.seconds / 3600))
                        .foregroundStyle(Calendar.current.isDateInToday(day.date)
                                         ? theme.accent : theme.accent.opacity(0.4))
                        .cornerRadius(4)
                }
                .chartXAxis {
                    AxisMarks(values: .stride(by: .day)) { _ in
                        AxisValueLabel(format: .dateTime.weekday(.narrow), centered: true)
                            .foregroundStyle(theme.secondaryText)
                    }
                }
                .chartYAxis {
                    AxisMarks(position: .trailing, values: .automatic(desiredCount: 3)) { value in
                        AxisGridLine().foregroundStyle(theme.border.opacity(0.5))
                        AxisValueLabel {
                            if let h = value.as(Double.self) { Text("\(Int(h))h") }
                        }
                        .foregroundStyle(theme.secondaryText)
                    }
                }
                .frame(maxHeight: .infinity)
            }
        }
    }

    private var topAppsCard: some View {
        let apps = service.todayApps
        let top = apps.first?.seconds ?? 1
        return Card("Top apps today", icon: "square.grid.2x2", accessory: { Text("\(apps.count) apps") }) {
            if apps.isEmpty {
                EmptyState(icon: "hourglass", title: "Nothing yet", subtitle: "App usage appears here as you work")
            } else {
                ScrollView(.vertical, showsIndicators: false) {
                    VStack(spacing: 8) {
                        ForEach(apps.prefix(20), id: \.bundleID) { app in
                            HStack(spacing: 10) {
                                Image(nsImage: AppInfoCache.icon(app.bundleID))
                                    .resizable()
                                    .frame(width: 22, height: 22)
                                VStack(alignment: .leading, spacing: 3) {
                                    HStack {
                                        Text(AppInfoCache.name(app.bundleID))
                                            .font(.system(size: 12, weight: .medium))
                                            .foregroundStyle(theme.text)
                                            .lineLimit(1)
                                        Spacer()
                                        Text(DurationText.short(app.seconds))
                                            .font(.system(size: 12, weight: .semibold, design: .rounded))
                                            .foregroundStyle(theme.secondaryText)
                                            .monospacedDigit()
                                    }
                                    GeometryReader { geo in
                                        ZStack(alignment: .leading) {
                                            Capsule().fill(theme.text.opacity(0.08))
                                            Capsule().fill(theme.accent)
                                                .frame(width: max(4, geo.size.width * CGFloat(app.seconds / top)))
                                        }
                                    }
                                    .frame(height: 4)
                                }
                            }
                        }
                    }
                    .padding(.trailing, 2)
                }
            }
        }
    }
}
