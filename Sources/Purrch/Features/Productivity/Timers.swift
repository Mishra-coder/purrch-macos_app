import AppKit
import SwiftUI
import UserNotifications

// MARK: - Notifications helper

/// Local notifications. UNUserNotificationCenter throws when the process has no bundle
/// (e.g. `swift run`), so everything is guarded and authorization is requested lazily.
enum TimerNotifier {
    static var available: Bool {
        Bundle.main.bundleIdentifier != nil && Bundle.main.bundleURL.pathExtension == "app"
    }

    static func withAuthorization(_ then: @escaping () -> Void) {
        guard available else { return }
        UNUserNotificationCenter.current().requestAuthorization(options: [.alert, .sound]) { granted, _ in
            if granted { then() }
        }
    }

    static func post(id: String = UUID().uuidString, title: String, body: String) {
        withAuthorization {
            let content = UNMutableNotificationContent()
            content.title = title
            content.body = body
            content.sound = .default
            UNUserNotificationCenter.current().add(UNNotificationRequest(identifier: id, content: content, trigger: nil))
        }
    }

    static func scheduleRepeating(id: String, every seconds: TimeInterval, title: String, body: String) {
        withAuthorization {
            let content = UNMutableNotificationContent()
            content.title = title
            content.body = body
            content.sound = .default
            let trigger = UNTimeIntervalNotificationTrigger(timeInterval: max(60, seconds), repeats: true)
            let center = UNUserNotificationCenter.current()
            center.removePendingNotificationRequests(withIdentifiers: [id])
            center.add(UNNotificationRequest(identifier: id, content: content, trigger: trigger))
        }
    }

    static func cancel(id: String) {
        guard available else { return }
        UNUserNotificationCenter.current().removePendingNotificationRequests(withIdentifiers: [id])
    }
}

// MARK: - Service

enum PomodoroPhase: String, Codable {
    case focus, shortBreak, longBreak

    var title: String {
        switch self {
        case .focus: "Focus"
        case .shortBreak: "Short Break"
        case .longBreak: "Long Break"
        }
    }

    var icon: String {
        switch self {
        case .focus: "brain.head.profile"
        case .shortBreak, .longBreak: "cup.and.saucer.fill"
        }
    }
}

@MainActor
final class TimerService: ObservableObject {
    static let shared = TimerService()

    /// Label for the closed notch ("23:42") while a pomodoro or countdown runs.
    @Published var liveLabel: String?
    @Published var liveIcon = "timer"

    // Pomodoro
    @Published var pomoPhase: PomodoroPhase = .focus
    @Published private(set) var pomoRunning = false
    @Published var focusMinutes = 25 { didSet { pomoSettingsChanged() } }
    @Published var breakMinutes = 5 { didSet { pomoSettingsChanged() } }
    @Published var longBreakMinutes = 15 { didSet { savePrefs() } }
    @Published var completedPomodoros = 0 { didSet { savePrefs() } }
    private var pomoRemainingPaused: Double = 25 * 60
    private var pomoEnd: Date?

    // Countdown
    @Published private(set) var countdownActive = false
    @Published private(set) var countdownRunning = false
    @Published private(set) var countdownTotal: Double = 10 * 60
    @Published var customMinutes = 15
    private var countdownRemainingPaused: Double = 10 * 60
    private var countdownEnd: Date?

    // Stopwatch
    @Published private(set) var stopwatchRunning = false
    @Published private(set) var laps: [Double] = []
    private var stopwatchStart: Date?
    private var stopwatchAccumulated: Double = 0

    // Water
    @Published private(set) var glasses = 0
    @Published var waterGoal = 8 { didSet { saveWater() } }
    @Published var waterEnabled = false {
        didSet { saveWater(); updateWaterReminder() }
    }
    private var waterDay = ""
    static let waterReminderID = "purrch.water"

    private var ticker: Timer?

    private struct Prefs: Codable {
        var focusMinutes = 25, breakMinutes = 5, longBreakMinutes = 15
        var completed = 0, completedDay = ""
    }

    private struct Water: Codable {
        var glasses = 0, goal = 8, enabled = false, day = ""
    }

    private var loading = true

    private init() {
        let prefs = Store.load("timers", default: Prefs())
        focusMinutes = min(90, max(1, prefs.focusMinutes))
        breakMinutes = min(60, max(1, prefs.breakMinutes))
        longBreakMinutes = prefs.longBreakMinutes
        completedPomodoros = prefs.completedDay == Self.today ? prefs.completed : 0
        pomoRemainingPaused = Double(focusMinutes) * 60

        let water = Store.load("water", default: Water())
        waterGoal = max(1, water.goal)
        waterEnabled = water.enabled
        waterDay = water.day
        glasses = water.day == Self.today ? water.glasses : 0
        loading = false
        if waterEnabled { updateWaterReminder() }
    }

    private static var today: String {
        let f = DateFormatter()
        f.dateFormat = "yyyy-MM-dd"
        return f.string(from: Date())
    }

    // MARK: Pomodoro

    func pomoDuration(_ phase: PomodoroPhase) -> Double {
        switch phase {
        case .focus: Double(focusMinutes) * 60
        case .shortBreak: Double(breakMinutes) * 60
        case .longBreak: Double(longBreakMinutes) * 60
        }
    }

    func pomoRemaining(at date: Date = Date()) -> Double {
        if let pomoEnd { return max(0, pomoEnd.timeIntervalSince(date)) }
        return pomoRemainingPaused
    }

    func pomoProgress(at date: Date = Date()) -> Double {
        let total = pomoDuration(pomoPhase)
        return total > 0 ? 1 - pomoRemaining(at: date) / total : 0
    }

    func pomoStart() {
        guard !pomoRunning else { return }
        pomoEnd = Date().addingTimeInterval(pomoRemainingPaused)
        pomoRunning = true
        refresh()
    }

    func pomoPause() {
        guard pomoRunning else { return }
        pomoRemainingPaused = pomoRemaining()
        pomoEnd = nil
        pomoRunning = false
        refresh()
    }

    func pomoToggle() { pomoRunning ? pomoPause() : pomoStart() }

    func pomoReset() {
        pomoEnd = nil
        pomoRunning = false
        pomoRemainingPaused = pomoDuration(pomoPhase)
        refresh()
    }

    func pomoSkip() { advancePomodoro(completedNaturally: false) }

    private func pomoSettingsChanged() {
        savePrefs()
        if !pomoRunning, !loading { pomoRemainingPaused = pomoDuration(pomoPhase) }
    }

    private func advancePomodoro(completedNaturally: Bool) {
        let wasRunning = pomoRunning
        if pomoPhase == .focus {
            if completedNaturally { completedPomodoros += 1 }
            pomoPhase = (completedPomodoros > 0 && completedPomodoros % 4 == 0 && completedNaturally) ? .longBreak : .shortBreak
        } else {
            pomoPhase = .focus
        }
        pomoRemainingPaused = pomoDuration(pomoPhase)
        pomoEnd = nil
        pomoRunning = false
        // Skipping keeps the flow going; a natural end waits for the user.
        if wasRunning && !completedNaturally { pomoStart() } else { refresh() }
    }

    // MARK: Countdown

    func countdownRemaining(at date: Date = Date()) -> Double {
        if let countdownEnd { return max(0, countdownEnd.timeIntervalSince(date)) }
        return countdownActive ? countdownRemainingPaused : Double(customMinutes) * 60
    }

    func countdownStart(minutes: Int) {
        countdownTotal = Double(max(1, minutes)) * 60
        countdownRemainingPaused = countdownTotal
        countdownActive = true
        countdownEnd = Date().addingTimeInterval(countdownTotal)
        countdownRunning = true
        refresh()
    }

    func countdownToggle() {
        if !countdownActive { countdownStart(minutes: customMinutes); return }
        if countdownRunning {
            countdownRemainingPaused = countdownRemaining()
            countdownEnd = nil
            countdownRunning = false
        } else {
            countdownEnd = Date().addingTimeInterval(countdownRemainingPaused)
            countdownRunning = true
        }
        refresh()
    }

    func countdownCancel() {
        countdownEnd = nil
        countdownRunning = false
        countdownActive = false
        countdownTotal = Double(customMinutes) * 60
        refresh()
    }

    func adjustCustom(by minutes: Int) {
        customMinutes = min(180, max(1, customMinutes + minutes))
        if !countdownActive { countdownTotal = Double(customMinutes) * 60 }
    }

    // MARK: Stopwatch

    func stopwatchElapsed(at date: Date = Date()) -> Double {
        stopwatchAccumulated + (stopwatchStart.map { date.timeIntervalSince($0) } ?? 0)
    }

    func stopwatchToggle() {
        if let start = stopwatchStart {
            stopwatchAccumulated += Date().timeIntervalSince(start)
            stopwatchStart = nil
            stopwatchRunning = false
        } else {
            stopwatchStart = Date()
            stopwatchRunning = true
        }
        refresh()
    }

    func stopwatchLap() {
        guard stopwatchRunning else { return }
        laps.insert(stopwatchElapsed(), at: 0)
        if laps.count > 99 { laps.removeLast() }
    }

    func stopwatchReset() {
        stopwatchStart = nil
        stopwatchAccumulated = 0
        stopwatchRunning = false
        laps = []
        refresh()
    }

    // MARK: Water

    func setGlasses(_ count: Int) {
        rolloverWaterDay()
        glasses = min(max(0, count), waterGoal)
        saveWater()
    }

    /// Tapping the n-th drop fills up to it; tapping the last filled drop empties it.
    func tapDrop(_ index: Int) {
        rolloverWaterDay()
        setGlasses(index + 1 == glasses ? index : index + 1)
    }

    private func rolloverWaterDay() {
        if waterDay != Self.today {
            waterDay = Self.today
            glasses = 0
        }
    }

    private func updateWaterReminder() {
        guard !loading else { return }
        if waterEnabled {
            TimerNotifier.scheduleRepeating(id: Self.waterReminderID, every: 30 * 60,
                                            title: "Drink some water 💧",
                                            body: "Time for a glass. Tap a drop in Purrch to log it.")
        } else {
            TimerNotifier.cancel(id: Self.waterReminderID)
        }
    }

    private func saveWater() {
        guard !loading else { return }
        Store.save("water", Water(glasses: glasses, goal: waterGoal, enabled: waterEnabled, day: waterDay))
    }

    private func savePrefs() {
        guard !loading else { return }
        Store.save("timers", Prefs(focusMinutes: focusMinutes, breakMinutes: breakMinutes,
                                   longBreakMinutes: longBreakMinutes, completed: completedPomodoros,
                                   completedDay: Self.today))
    }

    // MARK: Ticking

    private var needsTicker: Bool { pomoRunning || countdownRunning || stopwatchRunning }

    /// Starts or stops the single shared timer and updates the live label.
    private func refresh() {
        if needsTicker, ticker == nil {
            let t = Timer(timeInterval: 0.1, repeats: true) { [weak self] _ in
                MainActor.assumeIsolated { self?.tick() }
            }
            RunLoop.main.add(t, forMode: .common)
            ticker = t
        } else if !needsTicker {
            ticker?.invalidate()
            ticker = nil
        }
        updateLive()
    }

    private func tick() {
        let now = Date()
        if pomoRunning, pomoRemaining(at: now) <= 0 {
            let finished = pomoPhase
            advancePomodoro(completedNaturally: true)
            finish(title: finished == .focus ? "Focus session done" : "Break is over",
                   body: finished == .focus ? "Nice work. Time for a \(pomoPhase.title.lowercased())."
                                            : "Ready for another focus session?")
        }
        if countdownRunning, countdownRemaining(at: now) <= 0 {
            countdownEnd = nil
            countdownRunning = false
            countdownActive = false
            countdownRemainingPaused = 0
            refresh()
            finish(title: "Timer finished", body: "Your \(Format.duration(countdownTotal)) timer is up.")
        }
        updateLive()
    }

    private func finish(title: String, body: String) {
        NSSound(named: "Glass")?.play()
        TimerNotifier.post(title: title, body: body)
        NotchState.shared.open(.timers)
    }

    private func updateLive() {
        var label: String?
        var icon = "timer"
        if countdownRunning {
            label = Format.duration(ceil(countdownRemaining()))
            icon = "timer"
        } else if pomoRunning {
            label = Format.duration(ceil(pomoRemaining()))
            icon = pomoPhase.icon
        }
        if label != liveLabel { liveLabel = label }
        if icon != liveIcon { liveIcon = icon }
    }
}

// MARK: - View

struct TimersModule: View {
    var body: some View {
        HStack(spacing: 10) {
            PomodoroCard()
            CountdownCard()
            VStack(spacing: 10) {
                StopwatchCard()
                WaterCard()
            }
        }
    }
}

private struct PomodoroCard: View {
    @Environment(\.theme) private var theme
    @ObservedObject private var timers = TimerService.shared

    var body: some View {
        Card("Pomodoro", icon: "circle.dashed", accessory: {
            Text("\(timers.completedPomodoros) done")
        }) {
            VStack(spacing: 10) {
                TimelineView(.animation(minimumInterval: 0.1, paused: !timers.pomoRunning)) { context in
                    let remaining = timers.pomoRemaining(at: context.date)
                    let progress = timers.pomoProgress(at: context.date)
                    ZStack {
                        Circle().stroke(theme.text.opacity(0.12), lineWidth: 8)
                        Circle()
                            .trim(from: 0, to: max(0.002, 1 - progress))
                            .stroke(theme.accent, style: StrokeStyle(lineWidth: 8, lineCap: .round))
                            .rotationEffect(.degrees(-90))
                            .themeGlow(theme, radius: 6)
                        VStack(spacing: 2) {
                            Text(Format.duration(ceil(remaining)))
                                .font(.system(size: 34, weight: .bold, design: .rounded))
                                .monospacedDigit()
                                .foregroundStyle(theme.text)
                            Text(timers.pomoPhase.title)
                                .font(.system(size: 12, weight: .medium))
                                .foregroundStyle(theme.secondaryText)
                        }
                    }
                    .frame(width: 136, height: 136)
                }
                .frame(maxWidth: .infinity)
                .padding(.top, 2)

                HStack(spacing: 14) {
                    IconButton(systemName: timers.pomoRunning ? "pause.fill" : "play.fill", size: 34,
                               prominent: !timers.pomoRunning, help: timers.pomoRunning ? "Pause" : "Start") {
                        timers.pomoToggle()
                    }
                    IconButton(systemName: "arrow.counterclockwise", size: 34, help: "Reset") { timers.pomoReset() }
                    IconButton(systemName: "forward.end.fill", size: 34, help: "Skip") { timers.pomoSkip() }
                }
                .frame(maxWidth: .infinity)

                Spacer(minLength: 0)

                HStack(spacing: 6) {
                    MiniStepper(value: $timers.focusMinutes, range: 1...90, step: 5)
                    Text("\(timers.focusMinutes)m focus")
                    Text("/").foregroundStyle(theme.secondaryText.opacity(0.6))
                    MiniStepper(value: $timers.breakMinutes, range: 1...60, step: 1)
                    Text("\(timers.breakMinutes)m break")
                }
                .font(.system(size: 11, weight: .medium))
                .foregroundStyle(theme.secondaryText)
                .frame(maxWidth: .infinity, alignment: .leading)
            }
        }
    }
}

/// Compact chevron up/down stepper.
private struct MiniStepper: View {
    @Environment(\.theme) private var theme
    @Binding var value: Int
    let range: ClosedRange<Int>
    var step = 1

    var body: some View {
        VStack(spacing: 0) {
            Button {
                value = min(range.upperBound, value < step ? step : value + step)
            } label: {
                Image(systemName: "chevron.up").frame(width: 14, height: 9)
            }
            Button {
                value = max(range.lowerBound, value - step)
            } label: {
                Image(systemName: "chevron.down").frame(width: 14, height: 9)
            }
        }
        .buttonStyle(.plain)
        .font(.system(size: 8, weight: .bold))
        .foregroundStyle(theme.secondaryText)
        .contentShape(Rectangle())
    }
}

private struct CountdownCard: View {
    @Environment(\.theme) private var theme
    @ObservedObject private var timers = TimerService.shared
    private let presets = [5, 10, 25]

    var body: some View {
        Card("Timer", icon: "timer", accessory: {
            if !timers.countdownActive {
                HStack(spacing: 4) {
                    Button { timers.adjustCustom(by: -1) } label: { Image(systemName: "minus") }
                    Text("\(timers.customMinutes)m").monospacedDigit().frame(minWidth: 26)
                    Button { timers.adjustCustom(by: 1) } label: { Image(systemName: "plus") }
                }
                .buttonStyle(.plain)
            }
        }) {
            VStack(spacing: 10) {
                TimelineView(.animation(minimumInterval: 1 / 30, paused: !timers.countdownRunning)) { context in
                    let remaining = timers.countdownRemaining(at: context.date)
                    let total = timers.countdownActive ? timers.countdownTotal : Double(timers.customMinutes) * 60
                    let progress = total > 0 ? 1 - remaining / total : 0
                    VStack(spacing: 10) {
                        Text(Format.duration(ceil(remaining)))
                            .font(.system(size: 48, weight: .bold, design: .rounded))
                            .monospacedDigit()
                            .foregroundStyle(theme.text)
                            .contentTransition(.numericText(countsDown: true))
                        Waveform(progress: progress, time: context.date.timeIntervalSinceReferenceDate,
                                 animating: timers.countdownRunning, color: theme.accent, dim: theme.text)
                            .frame(height: 40)
                    }
                }
                .frame(maxWidth: .infinity)
                .padding(.top, 4)

                Spacer(minLength: 0)

                HStack(spacing: 8) {
                    ForEach(presets, id: \.self) { m in
                        let selected = timers.countdownActive && Int(timers.countdownTotal / 60) == m
                        Button { timers.countdownStart(minutes: m) } label: {
                            Text("\(m)m")
                                .font(.system(size: 12, weight: .semibold))
                                .foregroundStyle(selected ? theme.background : theme.text)
                                .padding(.horizontal, 12).padding(.vertical, 5)
                                .background(Capsule().fill(selected ? theme.accent : theme.text.opacity(0.1)))
                        }
                        .buttonStyle(.plain)
                    }
                }
                .frame(maxWidth: .infinity)

                HStack(spacing: 8) {
                    if timers.countdownActive {
                        PillButton(title: timers.countdownRunning ? "Pause" : "Resume",
                                   icon: timers.countdownRunning ? "pause.fill" : "play.fill", prominent: true) {
                            timers.countdownToggle()
                        }
                        PillButton(title: "Cancel", icon: "xmark") { timers.countdownCancel() }
                    } else {
                        PillButton(title: "Start \(timers.customMinutes)m", icon: "play.fill", prominent: true) {
                            timers.countdownToggle()
                        }
                    }
                }
                .frame(maxWidth: .infinity)
            }
        }
    }
}

/// Symmetric bar waveform; bars left of the playhead are lit.
private struct Waveform: View {
    var progress: Double
    var time: Double
    var animating: Bool
    var color: Color
    var dim: Color
    private let count = 44

    var body: some View {
        Canvas { ctx, size in
            let gap: CGFloat = 1.6
            let barW = max(1, (size.width - gap * CGFloat(count - 1)) / CGFloat(count))
            let lit = Int((Double(count) * min(1, max(0, progress))).rounded(.down))
            for i in 0..<count {
                let d = Double(i)
                // Fixed "speech-like" envelope plus a gentle shimmer while running.
                var amp = 0.25 + 0.75 * abs(sin(d * 0.45) * cos(d * 0.17 + 1.3))
                if animating { amp *= 0.85 + 0.15 * sin(time * 5 + d * 0.8) }
                let h = max(3, size.height * amp)
                let x = CGFloat(i) * (barW + gap)
                let rect = CGRect(x: x, y: (size.height - h) / 2, width: barW, height: h)
                let isLit = i < lit
                ctx.fill(Path(roundedRect: rect, cornerRadius: barW / 2),
                         with: .color(isLit ? color : dim.opacity(0.22)))
            }
        }
    }
}

private struct StopwatchCard: View {
    @Environment(\.theme) private var theme
    @ObservedObject private var timers = TimerService.shared

    var body: some View {
        Card("Stopwatch", icon: "stopwatch") {
            VStack(alignment: .leading, spacing: 6) {
                HStack(spacing: 8) {
                    TimelineView(.animation(minimumInterval: 0.03, paused: !timers.stopwatchRunning)) { context in
                        Text(Self.format(timers.stopwatchElapsed(at: context.date)))
                            .font(.system(size: 28, weight: .bold, design: .rounded))
                            .monospacedDigit()
                            .foregroundStyle(theme.text)
                            .lineLimit(1)
                            .minimumScaleFactor(0.7)
                    }
                    Spacer(minLength: 2)
                    IconButton(systemName: "flag.fill", size: 28, help: "Lap") { timers.stopwatchLap() }
                    IconButton(systemName: "arrow.counterclockwise", size: 28, help: "Reset") { timers.stopwatchReset() }
                    IconButton(systemName: timers.stopwatchRunning ? "pause.fill" : "play.fill", size: 28,
                               prominent: !timers.stopwatchRunning) { timers.stopwatchToggle() }
                }
                if timers.laps.isEmpty {
                    Text("Flag to record laps")
                        .font(.system(size: 11))
                        .foregroundStyle(theme.secondaryText)
                } else {
                    ScrollView(showsIndicators: false) {
                        VStack(spacing: 2) {
                            ForEach(Array(timers.laps.enumerated()), id: \.offset) { idx, lap in
                                HStack {
                                    Text("Lap \(timers.laps.count - idx)")
                                    Spacer()
                                    Text(Self.format(lap)).monospacedDigit()
                                }
                                .font(.system(size: 11, weight: .medium))
                                .foregroundStyle(idx == 0 ? theme.text : theme.secondaryText)
                            }
                        }
                    }
                }
            }
        }
    }

    static func format(_ t: Double) -> String {
        let cs = Int((t * 100).rounded(.down))
        let h = cs / 360_000, m = (cs / 6000) % 60, s = (cs / 100) % 60, c = cs % 100
        if h > 0 { return String(format: "%d:%02d:%02d.%02d", h, m, s, c) }
        return String(format: "%d:%02d.%02d", m, s, c)
    }
}

private struct WaterCard: View {
    @Environment(\.theme) private var theme
    @ObservedObject private var timers = TimerService.shared

    var body: some View {
        Card("Drink Water", icon: "drop.fill") {
            VStack(alignment: .leading, spacing: 7) {
                HStack {
                    Text("\(timers.glasses) of \(timers.waterGoal) glasses today")
                        .font(.system(size: 12, weight: .medium))
                        .foregroundStyle(theme.text)
                    Spacer()
                    Toggle("", isOn: $timers.waterEnabled)
                        .labelsHidden()
                        .toggleStyle(.switch)
                        .controlSize(.mini)
                        .tint(theme.accent)
                        .help("Remind me every 30 minutes")
                }
                HStack(spacing: 6) {
                    ForEach(0..<timers.waterGoal, id: \.self) { i in
                        let filled = i < timers.glasses
                        Button { withAnimation(.spring(response: 0.3)) { timers.tapDrop(i) } } label: {
                            Image(systemName: filled ? "drop.fill" : "drop")
                                .font(.system(size: 15, weight: .medium))
                                .foregroundStyle(filled ? theme.accent : theme.secondaryText)
                                .scaleEffect(filled ? 1.08 : 1)
                                .themeGlow(filled ? theme : Theme.make(.minimal), radius: 3)
                        }
                        .buttonStyle(.plain)
                    }
                }
                Text(timers.waterEnabled ? "Tap a drop after each glass. Reminding you every 30 min."
                                         : "Tap a drop after each glass. Turn on for 30 min reminders.")
                    .font(.system(size: 10))
                    .foregroundStyle(theme.secondaryText)
                    .lineLimit(2)
                    .fixedSize(horizontal: false, vertical: true)
            }
        }
    }
}
