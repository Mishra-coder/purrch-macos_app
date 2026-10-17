import SwiftUI

/// What the closed notch shows on either side of the camera.
enum LiveActivity: Equatable {
    case none
    case message(String)
    case timer(label: String, icon: String)
    case download(name: String, progress: Double)
    case music

    /// Identity of the activity kind, so icons pop in/out when it changes but not on every tick.
    var kind: String {
        switch self {
        case .none: "none"
        case .message: "message"
        case .timer: "timer"
        case .download: "download"
        case .music: "music"
        }
    }

    var wing: CGFloat {
        switch self {
        case .none: 0
        case .message: 110
        case .timer, .download, .music: 56
        }
    }
}

struct CollapsedView: View {
    @EnvironmentObject private var state: NotchState
    @EnvironmentObject private var settings: AppSettings
    @ObservedObject private var player = NowPlayingService.shared
    @ObservedObject private var timers = TimerService.shared
    @ObservedObject private var downloads = DownloadsService.shared
    @ObservedObject private var browser = BrowserMediaService.shared
    @Environment(\.theme) private var theme

    private var activity: LiveActivity {
        guard settings.showLiveActivities else { return .none }
        if settings.messageEnabled, !settings.messageText.isEmpty { return .message(settings.messageText) }
        if let label = timers.liveLabel { return .timer(label: label, icon: timers.liveIcon) }
        if let progress = downloads.activeProgress {
            return .download(name: downloads.activeName ?? "", progress: progress)
        }
        if player.isPlaying || browser.anyPlaying { return .music }
        return .none
    }

    var body: some View {
        let activity = activity
        HStack(spacing: 0) {
            leading(activity)
                .id("lead-" + activity.kind)
                .transition(.scale(scale: 0.3).combined(with: .opacity))
                .frame(width: activity.wing, alignment: .leading)
                .padding(.leading, activity == .none ? 0 : 12)
            Spacer(minLength: 0)
            trailing(activity)
                .id("trail-" + activity.kind)
                .transition(.scale(scale: 0.3).combined(with: .opacity))
                .frame(width: activity.wing, alignment: .trailing)
                .padding(.trailing, activity == .none ? 0 : 12)
        }
        .animation(Motion.pop, value: activity.kind)
        .frame(height: state.notchSize.height)
        .overlay {
            if case .message(let text) = activity {
                DotMatrixMarquee(text: text, color: theme.accent)
                    .frame(height: state.notchSize.height - 8)
                    .padding(.horizontal, 10)
                    .padding(.leading, state.hasPhysicalNotch ? 0 : 0)
            }
        }
        .onAppear { state.liveActivityWing = activity.wing }
        .onChange(of: activity) { _, new in state.liveActivityWing = new.wing }
    }

    @ViewBuilder
    private func leading(_ activity: LiveActivity) -> some View {
        switch activity {
        case .music:
            Group {
                if let art = player.isPlaying ? player.artwork : browser.playingArtwork {
                    Image(nsImage: art).resizable().aspectRatio(contentMode: .fill)
                } else {
                    Image(systemName: "music.note").foregroundStyle(theme.accent)
                }
            }
            .frame(width: 20, height: 20)
            .clipShape(RoundedRectangle(cornerRadius: 5))
        case .timer(_, let icon):
            Image(systemName: icon).font(.system(size: 13, weight: .semibold)).foregroundStyle(theme.accent)
        case .download:
            Image(systemName: "arrow.down.circle.fill").font(.system(size: 14)).foregroundStyle(theme.accent)
        default:
            EmptyView()
        }
    }

    @ViewBuilder
    private func trailing(_ activity: LiveActivity) -> some View {
        switch activity {
        case .music:
            MusicBars(color: theme.accent)
                .frame(width: 18, height: 14)
        case .timer(let label, _):
            Text(label)
                .font(.system(size: 12, weight: .semibold, design: .rounded))
                .monospacedDigit()
                .foregroundStyle(theme.text)
        case .download(_, let progress):
            ZStack {
                Circle().stroke(theme.text.opacity(0.2), lineWidth: 2.5)
                Circle().trim(from: 0, to: progress)
                    .stroke(theme.accent, style: StrokeStyle(lineWidth: 2.5, lineCap: .round))
                    .rotationEffect(.degrees(-90))
            }
            .frame(width: 16, height: 16)
        default:
            EmptyView()
        }
    }
}

/// Animated equaliser bars shown while music plays.
struct MusicBars: View {
    var color: Color

    var body: some View {
        TimelineView(.animation(minimumInterval: 1.0 / 30.0)) { context in
            let t = context.date.timeIntervalSinceReferenceDate
            HStack(alignment: .center, spacing: 2) {
                ForEach(0..<4, id: \.self) { i in
                    let h = 0.3 + 0.7 * abs(sin(t * (2.2 + Double(i) * 0.7) + Double(i)))
                    Capsule().fill(color).frame(width: 2.5).frame(maxHeight: .infinity).scaleEffect(y: h)
                }
            }
        }
    }
}

/// Text scrolling across the notch, drawn as a grid of lit dots.
struct DotMatrixMarquee: View {
    let text: String
    let color: Color
    @State private var textWidth: CGFloat = 200

    var body: some View {
        GeometryReader { geo in
            TimelineView(.animation) { context in
                let speed: CGFloat = 40
                let span = geo.size.width + textWidth
                let t = CGFloat(context.date.timeIntervalSinceReferenceDate)
                let x = geo.size.width - (t * speed).truncatingRemainder(dividingBy: span)
                Text(text.uppercased())
                    .font(.system(size: geo.size.height * 0.9, weight: .black, design: .monospaced))
                    .fixedSize()
                    .foregroundStyle(color)
                    .background(GeometryReader { g in
                        Color.clear.onAppear { textWidth = g.size.width }
                            .onChange(of: text) { _, _ in textWidth = g.size.width }
                    })
                    .offset(x: x)
                    .frame(maxHeight: .infinity)
            }
            .mask(DotGrid(spacing: 2.6))
            .shadow(color: color.opacity(0.8), radius: 3)
        }
        .clipped()
    }
}

struct DotGrid: View {
    var spacing: CGFloat

    var body: some View {
        Canvas { ctx, size in
            var y: CGFloat = spacing / 2
            while y < size.height {
                var x: CGFloat = spacing / 2
                while x < size.width {
                    ctx.fill(Path(ellipseIn: CGRect(x: x - 0.9, y: y - 0.9, width: 1.8, height: 1.8)), with: .color(.white))
                    x += spacing
                }
                y += spacing
            }
        }
    }
}
