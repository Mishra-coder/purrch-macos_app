import SwiftUI
import AppKit
import EventKit
import UniformTypeIdentifiers

// MARK: - Launcher store

@MainActor
final class LauncherStore: ObservableObject {
    static let shared = LauncherStore()

    @Published private(set) var paths: [String]

    private static let defaults = [
        "/Applications/Safari.app",
        "/System/Applications/Mail.app",
        "/System/Applications/Notes.app",
        "/System/Applications/Calendar.app",
        "/System/Applications/Utilities/Terminal.app",
        "/System/Applications/System Settings.app",
    ]

    private init() {
        let saved = Store.load("launcher", default: [String]?.none)
        paths = (saved ?? Self.defaults).filter { FileManager.default.fileExists(atPath: $0) }
    }

    func add(_ urls: [URL]) {
        for url in urls where !paths.contains(url.path) { paths.append(url.path) }
        Store.save("launcher", paths)
    }

    func remove(_ path: String) {
        paths.removeAll { $0 == path }
        Store.save("launcher", paths)
    }

    func launch(_ path: String) {
        let config = NSWorkspace.OpenConfiguration()
        config.activates = true
        NSWorkspace.shared.openApplication(at: URL(fileURLWithPath: path), configuration: config) { _, error in
            if let error { NSLog("Purrch launch: \(error.localizedDescription)") }
        }
        NotchState.shared.close()
    }

    func presentAddPanel() {
        let state = NotchState.shared
        let wasPinned = state.isPinned
        state.isPinned = true
        let panel = NSOpenPanel()
        panel.title = "Add apps to Launch"
        panel.directoryURL = URL(fileURLWithPath: "/Applications")
        panel.allowedContentTypes = [.application]
        panel.allowsMultipleSelection = true
        panel.canChooseDirectories = false
        panel.treatsFilePackagesAsDirectories = false
        panel.level = .modalPanel
        NSApp.activate(ignoringOtherApps: true)
        panel.begin { [weak self] response in
            MainActor.assumeIsolated {
                if response == .OK { self?.add(panel.urls) }
                state.isPinned = wasPinned
            }
        }
    }
}

// MARK: - Home

struct HomeModule: View {
    @ObservedObject private var monitor = SystemMonitor.shared

    var body: some View {
        VStack(spacing: 10) {
            HomeHero(monitor: monitor)
                .frame(height: 96)
            HStack(spacing: 10) {
                SystemWidget(monitor: monitor)
                LaunchWidget()
                NowPlayingWidget()
            }
        }
        .onAppear { monitor.start() }
        .onDisappear { monitor.stop() }
    }
}

private struct SystemWidget: View {
    @ObservedObject var monitor: SystemMonitor

    var body: some View {
        Card("System", icon: "cpu") {
            HStack(spacing: 0) {
                ring(monitor.cpu, "CPU")
                ring(monitor.memoryFraction, "RAM")
                ring(monitor.diskFraction, "Disk")
            }
            .frame(maxWidth: .infinity, maxHeight: .infinity)
        }
        .contentShape(Rectangle())
        .onTapGesture { NotchState.shared.open(.system) }
    }

    private func ring(_ v: Double, _ label: String) -> some View {
        RingGauge(value: v, center: "\(Int((v * 100).rounded()))", label: label, size: 54)
            .frame(maxWidth: .infinity)
    }
}

private struct LaunchWidget: View {
    @Environment(\.theme) private var theme
    @ObservedObject private var store = LauncherStore.shared

    private let columns = [GridItem(.adaptive(minimum: 34, maximum: 40), spacing: 6)]

    var body: some View {
        Card("Launch", icon: "square.grid.2x2") {
            ScrollView(.vertical, showsIndicators: false) {
                LazyVGrid(columns: columns, spacing: 6) {
                    ForEach(store.paths, id: \.self) { path in
                        AppIconButton(path: path)
                    }
                    Button { store.presentAddPanel() } label: {
                        Image(systemName: "plus")
                            .font(.system(size: 13, weight: .semibold))
                            .foregroundStyle(theme.secondaryText)
                            .frame(width: 32, height: 32)
                            .background(RoundedRectangle(cornerRadius: 8, style: .continuous)
                                .strokeBorder(theme.border, style: StrokeStyle(lineWidth: 1, dash: [3, 3])))
                    }
                    .buttonStyle(.plain)
                    .help("Add app")
                }
            }
        }
    }
}

private struct AppIconButton: View {
    let path: String
    @State private var hovering = false

    var body: some View {
        Button { LauncherStore.shared.launch(path) } label: {
            Image(nsImage: NSWorkspace.shared.icon(forFile: path))
                .resizable()
                .interpolation(.high)
                .frame(width: 32, height: 32)
                .scaleEffect(hovering ? 1.12 : 1)
                .animation(.spring(response: 0.25, dampingFraction: 0.6), value: hovering)
        }
        .buttonStyle(.plain)
        .onHover { hovering = $0 }
        .help(FileManager.default.displayName(atPath: path).replacingOccurrences(of: ".app", with: ""))
        .contextMenu {
            Button("Open") { LauncherStore.shared.launch(path) }
            Button("Show in Finder") { NSWorkspace.shared.activateFileViewerSelecting([URL(fileURLWithPath: path)]) }
            Divider()
            Button("Remove from Launch", role: .destructive) { LauncherStore.shared.remove(path) }
        }
    }
}

private struct NowPlayingWidget: View {
    @Environment(\.theme) private var theme
    @ObservedObject private var player = NowPlayingService.shared
    @ObservedObject private var browser = BrowserMediaService.shared

    var body: some View {
        Card("Now Playing", icon: "music.note", accessory: {
            Button { NotchState.shared.select(.nowPlaying) } label: { Image(systemName: "arrow.up.right") }
                .buttonStyle(.plain)
        }) {
            if player.title.isEmpty, let video = browser.playingVideo ?? browser.videos.first {
                videoBody(video)
            } else if player.title.isEmpty {
                EmptyState(icon: "music.note", title: "Nothing playing", subtitle: "Music, Spotify or YouTube")
            } else {
                VStack(alignment: .leading, spacing: 8) {
                    HStack(spacing: 10) {
                        Group {
                            if let art = player.artwork {
                                Image(nsImage: art).resizable().aspectRatio(contentMode: .fill)
                            } else {
                                ZStack {
                                    theme.text.opacity(0.1)
                                    Image(systemName: "music.note").foregroundStyle(theme.secondaryText)
                                }
                            }
                        }
                        .frame(width: 40, height: 40)
                        .clipShape(RoundedRectangle(cornerRadius: 8, style: .continuous))
                        VStack(alignment: .leading, spacing: 2) {
                            Text(player.title)
                                .font(.system(size: 13, weight: .semibold))
                                .foregroundStyle(theme.text)
                                .lineLimit(1)
                            Text(player.artist)
                                .font(.system(size: 11))
                                .foregroundStyle(theme.secondaryText)
                                .lineLimit(1)
                        }
                    }
                    HStack(spacing: 10) {
                        IconButton(systemName: "backward.fill", size: 26) { player.previous() }
                        IconButton(systemName: player.isPlaying ? "pause.fill" : "play.fill", size: 30, prominent: true) {
                            player.togglePlayPause()
                        }
                        IconButton(systemName: "forward.fill", size: 26) { player.next() }
                    }
                    .frame(maxWidth: .infinity)
                }
            }
        }
    }

    /// A YouTube video from the browser, when no music app is playing.
    private func videoBody(_ video: BrowserVideo) -> some View {
        VStack(alignment: .leading, spacing: 8) {
            AsyncImage(url: video.thumbnailURL) { $0.resizable().aspectRatio(contentMode: .fill) } placeholder: {
                theme.text.opacity(0.08)
            }
            .frame(maxWidth: .infinity)
            .frame(height: 70)
            .clipShape(RoundedRectangle(cornerRadius: 10, style: .continuous))
            .contentShape(Rectangle())
            .onTapGesture {
                guard video.controllable else { return }
                NotchState.shared.select(.nowPlaying)
                NotchState.shared.enterTheater(video.id)
            }
            .help(video.controllable ? "Watch in the notch" : "")
            .overlay(alignment: .bottomLeading) {
                GeometryReader { geo in
                    Capsule().fill(Color.red).frame(width: geo.size.width * video.progress, height: 3)
                        .frame(maxHeight: .infinity, alignment: .bottom)
                }
                .clipShape(RoundedRectangle(cornerRadius: 10, style: .continuous))
            }
            HStack(spacing: 8) {
                VStack(alignment: .leading, spacing: 1) {
                    Text(video.title).font(.system(size: 12, weight: .semibold)).foregroundStyle(theme.text).lineLimit(1)
                    Text(video.channel.isEmpty ? video.browser.shortName : video.channel)
                        .font(.system(size: 10.5)).foregroundStyle(theme.secondaryText).lineLimit(1)
                }
                Spacer(minLength: 4)
                if video.controllable {
                    IconButton(systemName: video.isPlaying ? "pause.fill" : "play.fill", size: 28, prominent: true) {
                        browser.toggle(video)
                    }
                }
            }
        }
    }
}

/// Greeting, the time, and three glanceable chips: battery, weather and the next event.
private struct HomeHero: View {
    @Environment(\.theme) private var theme
    @Environment(\.moduleTint) private var moduleTint
    @ObservedObject var monitor: SystemMonitor
    @ObservedObject private var calendar = CalendarService.shared
    @ObservedObject private var weather = WeatherService.shared

    var body: some View {
        let tint = moduleTint ?? theme.accent
        TimelineView(.periodic(from: .now, by: 1)) { context in
            HStack(spacing: 14) {
                VStack(alignment: .leading, spacing: 2) {
                    Text(greeting(for: context.date))
                        .font(.system(size: 12, weight: .medium))
                        .foregroundStyle(theme.secondaryText)
                    HStack(alignment: .firstTextBaseline, spacing: 4) {
                        Text(context.date.formatted(.dateTime.hour(.defaultDigits(amPM: .omitted)).minute()))
                            .font(.system(size: 44, weight: .bold, design: .rounded))
                            .monospacedDigit()
                            .contentTransition(.numericText())
                        Text(context.date.formatted(.dateTime.hour(.defaultDigits(amPM: .abbreviated))).filter(\.isLetter))
                            .font(.system(size: 14, weight: .semibold, design: .rounded))
                            .foregroundStyle(theme.secondaryText)
                    }
                    .foregroundStyle(theme.text)
                }
                Spacer(minLength: 8)
                HStack(spacing: 8) {
                    batteryChip
                    weatherChip
                    eventChip(now: context.date)
                }
            }
            .padding(.horizontal, 18)
            .frame(maxWidth: .infinity, maxHeight: .infinity)
            .background(
                ZStack {
                    CardSurface()
                    RoundedRectangle(cornerRadius: 20, style: .continuous)
                        .fill(LinearGradient(colors: [tint.opacity(0.22), tint.opacity(0.02)],
                                             startPoint: .leading, endPoint: .trailing))
                }
            )
        }
        .onAppear {
            calendar.refresh()
            weather.start()
        }
    }

    private func greeting(for date: Date) -> String {
        switch Calendar.current.component(.hour, from: date) {
        case 5..<12: "Good morning"
        case 12..<17: "Good afternoon"
        case 17..<22: "Good evening"
        default: "Good night"
        }
    }

    private var batteryChip: some View {
        let b = monitor.battery
        let percent = Int((b.level * 100).rounded())
        return HeroChip(icon: b.present ? b.symbolName : "powerplug.fill",
                        iconColor: b.isCharging ? .green : (b.level < 0.2 && b.present ? .red : theme.text),
                        value: b.present ? "\(percent)%" : "AC",
                        caption: b.isCharging ? "Charging" : (b.onAC ? "Plugged in" : "Battery"))
            .onTapGesture { NotchState.shared.select(.system) }
    }

    @ViewBuilder
    private var weatherChip: some View {
        if let w = weather.snapshot {
            HeroChip(icon: WeatherCode.symbol(w.code, isDay: w.isDay), iconColor: nil, multicolor: true,
                     value: weather.temp(w.temperature), caption: w.place.isEmpty ? "Weather" : w.place)
                .onTapGesture { NotchState.shared.select(.weather) }
        } else {
            HeroChip(icon: "cloud.sun.fill", iconColor: nil, multicolor: true, value: "--", caption: "Weather")
                .onTapGesture { NotchState.shared.select(.weather) }
        }
    }

    @ViewBuilder
    private func eventChip(now: Date) -> some View {
        if !calendar.eventsAuthorized {
            HeroChip(icon: "calendar.badge.plus", iconColor: theme.accent, value: "Connect", caption: "Calendar")
                .onTapGesture { calendar.requestAccess() }
        } else if let event = calendar.nextEvent {
            HeroChip(icon: "calendar", iconColor: Color(nsColor: event.calendar?.color ?? .systemGray),
                     value: countdown(event, now: now), caption: event.title ?? "Event", wide: true)
                .onTapGesture { NotchState.shared.select(.calendar) }
        } else {
            HeroChip(icon: "calendar.badge.checkmark", iconColor: .green, value: "Free", caption: "No events")
                .onTapGesture { NotchState.shared.select(.calendar) }
        }
    }

    private func countdown(_ event: EKEvent, now: Date) -> String {
        if event.startDate <= now { return "Now" }
        let minutes = Int(event.startDate.timeIntervalSince(now) / 60)
        if minutes < 60 { return "in \(max(1, minutes))m" }
        if minutes < 24 * 60 { return "in \(minutes / 60)h \(minutes % 60)m" }
        return "in \(minutes / (24 * 60))d"
    }
}

/// Small stat tile used in the Home hero.
private struct HeroChip: View {
    @Environment(\.theme) private var theme
    let icon: String
    let iconColor: Color?
    var multicolor = false
    let value: String
    let caption: String
    var wide = false
    @State private var hovering = false

    var body: some View {
        VStack(alignment: .leading, spacing: 3) {
            Image(systemName: icon)
                .symbolRenderingMode(multicolor ? .multicolor : .monochrome)
                .font(.system(size: 13, weight: .semibold))
                .foregroundStyle(iconColor ?? theme.text)
            Text(value)
                .font(.system(size: 15, weight: .bold, design: .rounded))
                .foregroundStyle(theme.text)
                .monospacedDigit()
                .lineLimit(1)
            Text(caption)
                .font(.system(size: 10.5))
                .foregroundStyle(theme.secondaryText)
                .lineLimit(1)
        }
        .padding(.horizontal, 12)
        .padding(.vertical, 9)
        .frame(width: wide ? 132 : 92, alignment: .leading)
        .background(
            RoundedRectangle(cornerRadius: 14, style: .continuous)
                .fill(theme.text.opacity(hovering ? 0.1 : 0.06))
        )
        .overlay(RoundedRectangle(cornerRadius: 14, style: .continuous).strokeBorder(theme.text.opacity(0.08), lineWidth: 0.6))
        .scaleEffect(hovering ? 1.03 : 1)
        .contentShape(Rectangle())
        .onHover { h in withAnimation(Motion.expoOut(0.3)) { hovering = h } }
    }
}
