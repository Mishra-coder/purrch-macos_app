import SwiftUI

/// One usage-bearing assistant line from a Claude Code transcript.
private struct UsageRecord {
    var key: String?
    var date: Date
    var tokens: Int
    var output: Int
    var toolCalls: Int
    var session: String
}

private struct TranscriptSummary {
    var modified: Date
    var records: [UsageRecord]
    var latest: Date?
    var latestCwd: String?
}

private struct UsageSnapshot {
    var todayTokens = 0
    var todayOutput = 0
    var todayTools = 0
    var todaySessions = 0
    var project: String?
    var daily: [Date: Int] = [:]
    var streak = 0
    var lastActivity: Date?
}

@MainActor
final class ClaudeCodeStats: ObservableObject {
    static let shared = ClaudeCodeStats()

    @Published private(set) var todayTokens = 0
    @Published private(set) var todayOutput = 0
    @Published private(set) var todayTools = 0
    @Published private(set) var todaySessions = 0
    @Published private(set) var project: String?
    @Published private(set) var daily: [Date: Int] = [:]
    @Published private(set) var streak = 0
    @Published private(set) var lastActivity: Date?
    @Published private(set) var hasLoaded = false
    @Published private(set) var isLoading = false

    nonisolated static let root = FileManager.default.homeDirectoryForCurrentUser
        .appendingPathComponent(".claude/projects", isDirectory: true)

    var rootExists: Bool {
        var isDir: ObjCBool = false
        return FileManager.default.fileExists(atPath: Self.root.path, isDirectory: &isDir) && isDir.boolValue
    }

    private var cache: [String: TranscriptSummary] = [:]

    func refresh() async {
        guard !isLoading else { return }
        isLoading = true
        let oldCache = cache
        let (snapshot, newCache) = await Task.detached(priority: .utility) {
            Self.compute(cache: oldCache)
        }.value
        cache = newCache
        todayTokens = snapshot.todayTokens
        todayOutput = snapshot.todayOutput
        todayTools = snapshot.todayTools
        todaySessions = snapshot.todaySessions
        project = snapshot.project
        daily = snapshot.daily
        streak = snapshot.streak
        lastActivity = snapshot.lastActivity
        hasLoaded = true
        isLoading = false
    }

    // MARK: Parsing (off the main thread)

    nonisolated private static func compute(cache: [String: TranscriptSummary]) -> (UsageSnapshot, [String: TranscriptSummary]) {
        let fm = FileManager.default
        let cutoff = Date().addingTimeInterval(-120 * 86_400)
        var newCache: [String: TranscriptSummary] = [:]
        if let enumerator = fm.enumerator(at: root, includingPropertiesForKeys: [.contentModificationDateKey, .isRegularFileKey],
                                          options: [.skipsHiddenFiles]) {
            for case let url as URL in enumerator where url.pathExtension == "jsonl" {
                guard let values = try? url.resourceValues(forKeys: [.contentModificationDateKey, .isRegularFileKey]),
                      values.isRegularFile == true,
                      let modified = values.contentModificationDate, modified >= cutoff else { continue }
                if let cached = cache[url.path], cached.modified == modified {
                    newCache[url.path] = cached
                } else {
                    newCache[url.path] = parse(url, modified: modified)
                }
            }
        }
        return (aggregate(Array(newCache.values)), newCache)
    }

    nonisolated private static func parse(_ url: URL, modified: Date) -> TranscriptSummary {
        var summary = TranscriptSummary(modified: modified, records: [])
        guard let data = try? Data(contentsOf: url, options: .mappedIfSafe) else { return summary }
        let fractional = ISO8601DateFormatter()
        fractional.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        let plain = ISO8601DateFormatter()
        let usageMarker = Data("\"usage\"".utf8)
        let cwdMarker = Data("\"cwd\"".utf8)

        data.withUnsafeBytes { (raw: UnsafeRawBufferPointer) in
            guard let base = raw.baseAddress else { return }
            var start = 0
            let count = raw.count
            while start < count {
                var end = start
                while end < count && raw[end] != 0x0A { end += 1 }
                defer { start = end + 1 }
                guard end > start else { continue }
                let line = Data(bytesNoCopy: UnsafeMutableRawPointer(mutating: base + start),
                                count: end - start, deallocator: .none)
                let hasUsage = line.range(of: usageMarker) != nil
                guard hasUsage || line.range(of: cwdMarker) != nil else { continue }
                guard let obj = try? JSONSerialization.jsonObject(with: line) as? [String: Any],
                      let stamp = obj["timestamp"] as? String,
                      let date = fractional.date(from: stamp) ?? plain.date(from: stamp) else { continue }
                if let cwd = obj["cwd"] as? String, date >= (summary.latest ?? .distantPast) {
                    summary.latest = date
                    summary.latestCwd = cwd
                }
                guard hasUsage, obj["type"] as? String == "assistant",
                      let message = obj["message"] as? [String: Any],
                      let usage = message["usage"] as? [String: Any] else { continue }
                func int(_ k: String) -> Int { (usage[k] as? NSNumber)?.intValue ?? 0 }
                let output = int("output_tokens")
                let total = int("input_tokens") + output + int("cache_creation_input_tokens") + int("cache_read_input_tokens")
                let tools = (message["content"] as? [[String: Any]])?
                    .filter { $0["type"] as? String == "tool_use" }.count ?? 0
                var key: String?
                if let id = message["id"] as? String {
                    key = id + "|" + (obj["requestId"] as? String ?? "")
                }
                summary.records.append(UsageRecord(key: key, date: date, tokens: total, output: output,
                                                   toolCalls: tools, session: obj["sessionId"] as? String ?? url.path))
            }
        }
        return summary
    }

    nonisolated private static func aggregate(_ summaries: [TranscriptSummary]) -> UsageSnapshot {
        let calendar = Calendar.current
        let today = calendar.startOfDay(for: Date())
        var snapshot = UsageSnapshot()
        var seen = Set<String>()
        var sessions = Set<String>()
        var latest = Date.distantPast
        for summary in summaries {
            if let date = summary.latest, date > latest {
                latest = date
                snapshot.project = summary.latestCwd.map { URL(fileURLWithPath: $0).lastPathComponent }
            }
            for record in summary.records {
                let isToday = record.date >= today
                // Tool calls are spread over the streamed copies of a message, so count every line.
                if isToday { snapshot.todayTools += record.toolCalls }
                if let key = record.key {
                    guard seen.insert(key).inserted else { continue }
                }
                let day = calendar.startOfDay(for: record.date)
                snapshot.daily[day, default: 0] += record.tokens
                if isToday {
                    snapshot.todayTokens += record.tokens
                    snapshot.todayOutput += record.output
                    sessions.insert(record.session)
                }
            }
        }
        snapshot.todaySessions = sessions.count
        snapshot.lastActivity = latest == .distantPast ? nil : latest
        var day = (snapshot.daily[today] ?? 0) > 0 ? today : calendar.date(byAdding: .day, value: -1, to: today)!
        while (snapshot.daily[day] ?? 0) > 0 {
            snapshot.streak += 1
            day = calendar.date(byAdding: .day, value: -1, to: day)!
        }
        return snapshot
    }
}

// MARK: - View

struct ClaudeCodeModule: View {
    @Environment(\.theme) private var theme
    @StateObject private var stats = ClaudeCodeStats.shared

    var body: some View {
        Group {
            if !stats.rootExists {
                EmptyState(icon: "chevron.left.forwardslash.chevron.right", title: "No Claude Code data yet",
                           subtitle: "Usage appears here once ~/.claude/projects has transcripts.")
            } else if !stats.hasLoaded {
                VStack(spacing: 8) {
                    ProgressView().controlSize(.small)
                    Text("Reading transcripts…").font(.system(size: 12)).foregroundStyle(theme.secondaryText)
                }
                .frame(maxWidth: .infinity, maxHeight: .infinity)
            } else {
                HStack(spacing: 10) {
                    todayCard.frame(width: 300)
                    streakCard
                }
            }
        }
        .task {
            while !Task.isCancelled {
                if stats.rootExists { await stats.refresh() }
                try? await Task.sleep(for: .seconds(60))
            }
        }
    }

    private var accent: Color { theme.accent(for: .claude) }

    private var todayCard: some View {
        Card("Today", icon: "bolt.fill", accessory: {
            if stats.isLoading { ProgressView().controlSize(.mini) }
        }) {
            VStack(alignment: .leading, spacing: 10) {
                VStack(alignment: .leading, spacing: 0) {
                    Text(CompactNumber.string(Double(stats.todayTokens)))
                        .font(.system(size: 44, weight: .bold, design: .rounded))
                        .foregroundStyle(accent)
                        .themeGlow(theme, radius: 5)
                        .monospacedDigit()
                        .contentTransition(.numericText())
                    Text("TOKENS")
                        .font(.system(size: 10, weight: .semibold))
                        .tracking(1)
                        .foregroundStyle(theme.secondaryText)
                }
                HStack(spacing: 8) {
                    stat(CompactNumber.string(Double(stats.todayOutput)), "Output")
                    stat("\(stats.todayTools)", "Tool calls")
                    stat("\(stats.todaySessions)", "Sessions")
                }
                Spacer(minLength: 0)
                HStack(spacing: 6) {
                    Image(systemName: "folder").font(.system(size: 11))
                    Text(stats.project ?? "No recent project")
                        .font(.system(size: 12, weight: .medium))
                        .lineLimit(1)
                    Spacer()
                    if let last = stats.lastActivity {
                        Text(Format.relative(last) == "now" ? "active now" : "\(Format.relative(last)) ago")
                            .font(.system(size: 11))
                    }
                }
                .foregroundStyle(theme.secondaryText)
            }
        }
    }

    private func stat(_ value: String, _ label: String) -> some View {
        VStack(alignment: .leading, spacing: 2) {
            Text(value)
                .font(.system(size: 17, weight: .semibold, design: .rounded))
                .monospacedDigit()
                .foregroundStyle(theme.text)
            Text(label.uppercased())
                .font(.system(size: 9, weight: .semibold))
                .tracking(0.6)
                .foregroundStyle(theme.secondaryText)
        }
        .padding(.horizontal, 9)
        .padding(.vertical, 6)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(RoundedRectangle(cornerRadius: 10, style: .continuous).fill(theme.text.opacity(0.06)))
    }

    private var streakCard: some View {
        Card("Last 16 weeks", icon: "square.grid.3x3.fill", accessory: {
            HStack(spacing: 4) {
                Image(systemName: "flame.fill").foregroundStyle(stats.streak > 0 ? accent : theme.secondaryText)
                Text("\(stats.streak) day streak").fontWeight(.semibold)
            }
        }) {
            StreakGrid(daily: stats.daily, accent: accent)
        }
    }
}

private struct StreakGrid: View {
    @Environment(\.theme) private var theme
    let daily: [Date: Int]
    let accent: Color
    private let weeks = 16

    var body: some View {
        let calendar = Calendar.current
        let today = calendar.startOfDay(for: Date())
        let weekStart = calendar.dateInterval(of: .weekOfYear, for: today)?.start ?? today
        let firstColumn = calendar.date(byAdding: .weekOfYear, value: -(weeks - 1), to: weekStart) ?? weekStart
        let peak = max(1, daily.filter { $0.key >= firstColumn }.values.max() ?? 1)
        let total = daily.filter { $0.key >= firstColumn }.values.reduce(0, +)
        let activeDays = daily.filter { $0.key >= firstColumn && $0.value > 0 }.count

        VStack(alignment: .leading, spacing: 8) {
            GeometryReader { geo in
                let spacing: CGFloat = 4
                let side = max(6, min((geo.size.width - spacing * CGFloat(weeks - 1)) / CGFloat(weeks),
                                      (geo.size.height - spacing * 6) / 7))
                HStack(alignment: .top, spacing: spacing) {
                    ForEach(0..<weeks, id: \.self) { column in
                        VStack(spacing: spacing) {
                            ForEach(0..<7, id: \.self) { row in
                                let day = calendar.date(byAdding: .day, value: column * 7 + row, to: firstColumn)!
                                cell(day: day, today: today, peak: peak)
                                    .frame(width: side, height: side)
                            }
                        }
                    }
                }
                .frame(maxWidth: .infinity, maxHeight: .infinity)
            }
            HStack(spacing: 10) {
                Text("\(CompactNumber.string(Double(total))) tokens · \(activeDays) active days")
                Spacer()
                HStack(spacing: 3) {
                    Text("Less")
                    ForEach([0.0, 0.25, 0.5, 0.75, 1.0], id: \.self) { level in
                        RoundedRectangle(cornerRadius: 2)
                            .fill(level == 0 ? theme.text.opacity(0.08) : accent.opacity(0.2 + 0.8 * level))
                            .frame(width: 9, height: 9)
                    }
                    Text("More")
                }
            }
            .font(.system(size: 10))
            .foregroundStyle(theme.secondaryText)
        }
    }

    @ViewBuilder
    private func cell(day: Date, today: Date, peak: Int) -> some View {
        let value = daily[day] ?? 0
        let shape = RoundedRectangle(cornerRadius: 3, style: .continuous)
        if day > today {
            shape.fill(Color.clear)
        } else if value == 0 {
            shape.fill(theme.text.opacity(0.08))
                .overlay(day == today ? shape.strokeBorder(accent.opacity(0.6), lineWidth: 1) : nil)
                .help(Self.label(day, value))
        } else {
            // Square-root scale so one huge day doesn't wash out the rest.
            let level = sqrt(Double(value) / Double(peak))
            shape.fill(accent.opacity(0.2 + 0.8 * level))
                .overlay(day == today ? shape.strokeBorder(theme.text.opacity(0.8), lineWidth: 1) : nil)
                .help(Self.label(day, value))
        }
    }

    private static func label(_ day: Date, _ value: Int) -> String {
        "\(day.formatted(.dateTime.weekday(.abbreviated).month(.abbreviated).day())): \(CompactNumber.string(Double(value))) tokens"
    }
}
