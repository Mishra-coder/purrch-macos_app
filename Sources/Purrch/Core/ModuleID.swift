import SwiftUI

/// Every tool that lives in the notch, in tab-bar order.
enum ModuleID: String, CaseIterable, Identifiable, Codable {
    case home, timers, clipboard, notes, scratchpad, emoji, units, links
    case shelf, captures, converter, downloads, macmate, message, ambience
    case claude, calendar, weather, nowPlaying, system, screenTime, revenue, analytics, translate

    var id: String { rawValue }

    var title: String {
        switch self {
        case .home: "Home"
        case .timers: "Timers"
        case .clipboard: "Clipboard"
        case .notes: "Notes"
        case .scratchpad: "Scratchpad"
        case .emoji: "Emoji"
        case .units: "Units"
        case .links: "Links"
        case .shelf: "Shelf"
        case .captures: "Captures"
        case .converter: "File Converter"
        case .downloads: "Downloads"
        case .macmate: "MacMate AI"
        case .message: "Message"
        case .ambience: "Ambience"
        case .claude: "Claude Code"
        case .calendar: "Calendar"
        case .weather: "Weather"
        case .nowPlaying: "Now Playing"
        case .system: "System"
        case .screenTime: "Screen Time"
        case .revenue: "Revenue"
        case .analytics: "Analytics"
        case .translate: "Translate"
        }
    }

    var icon: String {
        switch self {
        case .home: "house"
        case .timers: "timer"
        case .clipboard: "doc.on.clipboard"
        case .notes: "list.bullet.rectangle"
        case .scratchpad: "scribble"
        case .emoji: "face.smiling"
        case .units: "arrow.left.arrow.right"
        case .links: "link"
        case .shelf: "tray"
        case .captures: "photo.on.rectangle"
        case .converter: "camera.viewfinder"
        case .downloads: "arrow.down.circle"
        case .macmate: "sparkles"
        case .message: "bubble.left.fill"
        case .ambience: "speaker.wave.2"
        case .claude: "chevron.left.forwardslash.chevron.right"
        case .calendar: "calendar"
        case .weather: "cloud.sun"
        case .nowPlaying: "music.note"
        case .system: "cpu"
        case .screenTime: "hourglass"
        case .revenue: "creditcard"
        case .analytics: "chart.line.uptrend.xyaxis"
        case .translate: "character.bubble"
        }
    }

    var subtitle: String {
        switch self {
        case .home: "Glanceable widgets and a quick launcher"
        case .timers: "Pomodoro, countdown, stopwatch, water"
        case .clipboard: "Searchable history of everything you copy"
        case .notes: "Quick lists with a full editor"
        case .scratchpad: "A text box that never forgets"
        case .emoji: "Search and copy with one click"
        case .units: "Instant conversions"
        case .links: "Web search and saved sites"
        case .shelf: "Park files in the notch, drag them out later"
        case .captures: "Your screenshots and recordings, newest first"
        case .converter: "Convert and shrink images locally"
        case .downloads: "Live download progress"
        case .macmate: "Ask anything about your Mac"
        case .message: "Scroll text across the notch"
        case .ambience: "Layer rain, wind and noise"
        case .claude: "Today's Claude Code usage"
        case .calendar: "Seven-day agenda and reminders"
        case .weather: "Now, next 12 hours, this week"
        case .nowPlaying: "Artwork, scrubber and controls"
        case .system: "CPU, memory, disk, network, battery"
        case .screenTime: "App usage over seven days"
        case .revenue: "Today's sales from Stripe"
        case .analytics: "Live visitors from Plausible"
        case .translate: "Translate, fix grammar, change tone"
        }
    }

    var playfulColor: Color {
        let palette: [Color] = [.blue, .orange, .pink, .yellow, .mint, .yellow, .teal, .cyan,
                                .indigo, .purple, .green, .blue, .purple, .pink, .teal,
                                .orange, .red, .cyan, .pink, .green, .indigo, .green, .blue, .mint]
        return palette[(ModuleID.allCases.firstIndex(of: self) ?? 0) % palette.count]
    }

    @MainActor @ViewBuilder
    var view: some View {
        switch self {
        case .home: HomeModule()
        case .timers: TimersModule()
        case .clipboard: ClipboardModule()
        case .notes: NotesModule()
        case .scratchpad: ScratchpadModule()
        case .emoji: EmojiModule()
        case .units: UnitsModule()
        case .links: LinksModule()
        case .shelf: ShelfModule()
        case .captures: CapturesModule()
        case .converter: ConverterModule()
        case .downloads: DownloadsModule()
        case .macmate: MacMateModule()
        case .message: MessageModule()
        case .ambience: AmbienceModule()
        case .claude: ClaudeCodeModule()
        case .calendar: CalendarModule()
        case .weather: WeatherModule()
        case .nowPlaying: NowPlayingModule()
        case .system: SystemModule()
        case .screenTime: ScreenTimeModule()
        case .revenue: RevenueModule()
        case .analytics: AnalyticsModule()
        case .translate: TranslateModule()
        }
    }
}
