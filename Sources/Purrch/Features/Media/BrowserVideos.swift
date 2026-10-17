import SwiftUI

/// Every YouTube video open in a browser, each with its own controls.
struct BrowserVideosCard: View {
    @Environment(\.theme) private var theme
    @ObservedObject private var browser = BrowserMediaService.shared

    var body: some View {
        Card("Browser videos", icon: "play.rectangle", accessory: {
            Text("\(browser.videos.count) open")
        }) {
            VStack(spacing: 8) {
                ForEach(Array(browser.blockedBrowsers).sorted(by: { $0.rawValue < $1.rawValue }), id: \.self) { kind in
                    PermissionBanner(browser: kind)
                }
                ScrollView {
                    VStack(spacing: 8) {
                        ForEach(browser.videos) { video in
                            VideoRow(video: video)
                                .transition(.asymmetric(insertion: .move(edge: .top).combined(with: .opacity),
                                                        removal: .opacity))
                        }
                    }
                    .animation(Motion.pop, value: browser.videos.map(\.id))
                }
                .scrollIndicators(.never)
            }
        }
    }
}

/// One-click setup so Purrch can play / pause the browser's videos.
private struct PermissionBanner: View {
    @Environment(\.theme) private var theme
    @ObservedObject private var service = BrowserMediaService.shared
    let browser: BrowserKind

    private var status: BrowserEnableStatus { service.enableStatus[browser] ?? .idle }

    var body: some View {
        HStack(alignment: .top, spacing: 10) {
            Image(systemName: icon)
                .font(.system(size: 16, weight: .semibold))
                .foregroundStyle(.orange)
                .symbolEffect(.pulse, isActive: status == .working || status == .needsAccessibility)
                .frame(width: 22)
            VStack(alignment: .leading, spacing: 4) {
                Text(title).font(.system(size: 12, weight: .semibold))
                ForEach(Array(steps.enumerated()), id: \.offset) { i, step in
                    HStack(alignment: .firstTextBaseline, spacing: 6) {
                        Text("\(i + 1)")
                            .font(.system(size: 9, weight: .bold))
                            .foregroundStyle(theme.background)
                            .frame(width: 14, height: 14)
                            .background(Circle().fill(Color.orange))
                        Text(step).font(.system(size: 11)).foregroundStyle(theme.secondaryText)
                    }
                }
            }
            Spacer(minLength: 6)
            actions
        }
        .padding(10)
        .background(RoundedRectangle(cornerRadius: 12).fill(Color.orange.opacity(0.12)))
        .overlay(RoundedRectangle(cornerRadius: 12).strokeBorder(Color.orange.opacity(0.35), lineWidth: 0.8))
        .animation(Motion.expoOut(0.35), value: status)
    }

    private var icon: String {
        switch status {
        case .needsAccessibility: "lock.shield"
        case .failed: "exclamationmark.triangle.fill"
        default: "hand.tap.fill"
        }
    }

    private var title: String {
        switch status {
        case .idle: "Control \(browser.shortName) videos from the notch"
        case .working: "Turning it on in \(browser.shortName)…"
        case .needsAccessibility: "One more step — allow Purrch"
        case .failed: "Couldn't switch it on automatically"
        }
    }

    private var steps: [String] {
        switch status {
        case .idle, .working:
            ["Click Enable — Purrch ticks the setting in \(browser.shortName) for you.",
             "First time only: macOS asks to allow Purrch in Accessibility."]
        case .needsAccessibility:
            ["In the window that opened, switch on Purrch under Accessibility.",
             "Come back — Purrch finishes by itself."]
        case .failed:
            ["Open \(browser.permissionHint) and tick it.",
             "Then come back here; videos get controls within 2 seconds."]
        }
    }

    @ViewBuilder
    private var actions: some View {
        switch status {
        case .working:
            ProgressView().controlSize(.small)
        case .needsAccessibility:
            PillButton(title: "Open Settings", icon: "gearshape") { service.openAccessibilitySettings() }
        case .idle:
            PillButton(title: "Enable", icon: "bolt.fill", prominent: true) { service.enableControl(for: browser) }
        case .failed:
            PillButton(title: "Try again", icon: "arrow.clockwise", prominent: true) { service.enableControl(for: browser) }
        }
    }
}

private struct VideoRow: View {
    @Environment(\.theme) private var theme
    @ObservedObject private var browser = BrowserMediaService.shared
    let video: BrowserVideo
    @State private var hovering = false

    var body: some View {
        HStack(spacing: 12) {
            thumbnail
                .contentShape(Rectangle())
                .onTapGesture { if video.controllable { NotchState.shared.enterTheater(video.id) } }
                .help(video.controllable ? "Watch in the notch" : "")
            VStack(alignment: .leading, spacing: 3) {
                Text(video.title)
                    .font(.system(size: 13, weight: .semibold))
                    .foregroundStyle(theme.text)
                    .lineLimit(1)
                HStack(spacing: 5) {
                    if video.isPlaying { MusicBars(color: theme.accent).frame(width: 12, height: 10) }
                    Text([video.channel, video.browser.shortName].filter { !$0.isEmpty }.joined(separator: " · "))
                        .lineLimit(1)
                }
                .font(.system(size: 11))
                .foregroundStyle(theme.secondaryText)
                if video.duration > 0 {
                    HStack(spacing: 8) {
                        ProgressTrack(progress: video.progress) { browser.seek(video, toFraction: $0) }
                            .frame(height: 10)
                        Text("\(Format.duration(video.current)) / \(Format.duration(video.duration))")
                            .font(.system(size: 10, weight: .medium))
                            .monospacedDigit()
                            .foregroundStyle(theme.secondaryText)
                            .fixedSize()
                    }
                }
            }
            Spacer(minLength: 4)
            if video.controllable {
                HStack(spacing: 6) {
                    IconButton(systemName: "gobackward.10", size: 26, help: "Back 10s") { browser.seek(video, by: -10) }
                    IconButton(systemName: video.isPlaying ? "pause.fill" : "play.fill", size: 34, prominent: true,
                               help: video.isPlaying ? "Pause" : "Play") {
                        withAnimation(Motion.pop) { browser.toggle(video) }
                    }
                    .contentTransition(.symbolEffect(.replace))
                    IconButton(systemName: "goforward.10", size: 26, help: "Forward 10s") { browser.seek(video, by: 10) }
                    IconButton(systemName: "forward.end.fill", size: 26, help: "Next video") { browser.next(video) }
                }
            }
            IconButton(systemName: "arrow.up.forward.app", size: 26, help: "Show tab") { browser.focus(video) }
        }
        .padding(8)
        .background(
            RoundedRectangle(cornerRadius: 12, style: .continuous)
                .fill(video.isPlaying ? theme.accent.opacity(0.1) : theme.text.opacity(hovering ? 0.06 : 0.03))
        )
        .overlay(
            RoundedRectangle(cornerRadius: 12, style: .continuous)
                .strokeBorder(video.isPlaying ? theme.accent.opacity(0.55) : theme.border.opacity(0.5), lineWidth: 0.8)
        )
        .onHover { h in withAnimation(Motion.expoOut(0.3)) { hovering = h } }
        .animation(Motion.pop, value: video.isPlaying)
    }

    private var thumbnail: some View {
        AsyncImage(url: video.thumbnailURL) { image in
            image.resizable().aspectRatio(contentMode: .fill)
        } placeholder: {
            ZStack {
                theme.text.opacity(0.08)
                Image(systemName: "play.rectangle.fill").foregroundStyle(theme.secondaryText)
            }
        }
        .frame(width: 96, height: 54)
        .clipShape(RoundedRectangle(cornerRadius: 8, style: .continuous))
        .overlay(alignment: .bottomLeading) {
            if video.duration > 0 {
                GeometryReader { geo in
                    Rectangle().fill(Color.red).frame(width: geo.size.width * video.progress, height: 3)
                        .frame(maxHeight: .infinity, alignment: .bottom)
                }
                .clipShape(RoundedRectangle(cornerRadius: 8, style: .continuous))
            }
        }
        .overlay {
            if !video.isPlaying && video.controllable {
                Image(systemName: "pause.fill")
                    .font(.system(size: 14, weight: .bold))
                    .foregroundStyle(.white)
                    .padding(6)
                    .background(Circle().fill(.black.opacity(0.5)))
            }
        }
    }
}

/// Thin seekable progress line.
private struct ProgressTrack: View {
    @Environment(\.theme) private var theme
    let progress: Double
    let onSeek: (Double) -> Void
    @State private var hovering = false

    var body: some View {
        GeometryReader { geo in
            ZStack(alignment: .leading) {
                Capsule().fill(theme.text.opacity(0.15))
                Capsule().fill(theme.accent).frame(width: max(4, geo.size.width * progress))
                    .animation(.linear(duration: 2), value: progress)
            }
            .frame(height: hovering ? 6 : 4)
            .frame(maxHeight: .infinity)
            .contentShape(Rectangle())
            .onTapGesture { location in onSeek(location.x / max(geo.size.width, 1)) }
        }
        .onHover { h in withAnimation(Motion.expoOut(0.2)) { hovering = h } }
    }
}

/// The notch as a small live window onto a browser video (opened from Settings > Behaviour > Video on hover).
struct VideoPeekView: View {
    @EnvironmentObject private var state: NotchState
    @Environment(\.theme) private var theme
    @ObservedObject private var browser = BrowserMediaService.shared
    @State private var shown = false
    @State private var videoID: String?
    @State private var showQuality = false

    private var video: BrowserVideo? {
        browser.videos.first { $0.id == videoID } ?? browser.playingVideo ?? browser.videos.first
    }

    var body: some View {
        VStack(spacing: 8) {
            header
                .frame(height: max(state.notchSize.height, 28))
                .padding(.horizontal, 14)
                .opacity(shown ? 1 : 0)
            if let video {
                LiveVideoPlayer(video: video, showQuality: $showQuality, onToggleSize: {
                    state.expandToFullPanel(.nowPlaying)
                    state.enterTheater(video.id)
                })
                    .padding(.horizontal, 12)
                    .padding(.bottom, 12)
                    .opacity(shown ? 1 : 0)
                    .scaleEffect(shown ? 1 : 0.94, anchor: .top)
            }
        }
        .onAppear {
            videoID = (browser.playingVideo ?? browser.videos.first)?.id
            withAnimation(Motion.expoOut(0.5).delay(0.05)) { shown = true }
        }
    }

    private var header: some View {
        HStack(spacing: 8) {
            if let video {
                Image(systemName: "play.rectangle.fill").foregroundStyle(.red)
                Text(video.browser.shortName).font(.system(size: 12, weight: .semibold))
                if video.isPlaying { MusicBars(color: theme.accent).frame(width: 14, height: 11) }
            }
            Spacer()
            Color.clear.frame(width: state.hasPhysicalNotch ? state.notchSize.width - 60 : 0)
            Spacer()
            if browser.videos.count > 1 {
                IconButton(systemName: "rectangle.stack", size: 24, help: "Next open video") { cycle() }
            }
            IconButton(systemName: "gearshape", size: 24, help: "Quality") {
                withAnimation(Motion.expoOut(0.3)) { showQuality.toggle() }
            }
            .rotationEffect(.degrees(showQuality ? 90 : 0))
            IconButton(systemName: "rectangle.expand.vertical", size: 24, help: "All tools") {
                state.expandToFullPanel(.nowPlaying)
            }
        }
    }

    private func cycle() {
        let list = browser.videos
        guard let i = list.firstIndex(where: { $0.id == video?.id }) else { return }
        videoID = list[(i + 1) % list.count].id
    }
}

/// A browser video mirrored live, with playback controls and a quality sheet.
/// Streams frames while on screen and stops when it disappears.
struct LiveVideoPlayer: View {
    @Environment(\.theme) private var theme
    @EnvironmentObject private var settings: AppSettings
    @ObservedObject private var browser = BrowserMediaService.shared
    let video: BrowserVideo
    @Binding var showQuality: Bool
    /// Smaller title and controls for tight layouts.
    var compact = false
    /// Grow to / shrink from theater mode. When set, clicking the picture expands instead of pausing.
    var onToggleSize: (() -> Void)? = nil
    var isTheater = false
    @ObservedObject private var embed = EmbeddedPlayerHost.shared
    /// Title and controls fade out a few seconds after the pointer stops moving, like YouTube.
    @State private var chromeVisible = true
    @State private var hideChrome: Task<Void, Never>?

    /// Paused, or the quality sheet is open: keep the controls up.
    private var showsChrome: Bool { chromeVisible || !video.isPlaying || showQuality }

    /// Smooth path: YouTube's own player, muted and synced. Falls back to mirrored frames.
    private var usesEmbed: Bool { video.videoID != nil && embed.failedVideoID != video.videoID }
    private var embedReady: Bool { video.videoID != nil && embed.readyVideoID == video.videoID }

    var body: some View {
        ZStack {
            Color.black
            if usesEmbed, let id = video.videoID {
                EmbeddedYouTubePlayer(videoID: id)
                    .opacity(embedReady ? 1 : 0)
                    .animation(.easeOut(duration: 0.3), value: embedReady)
                    .allowsHitTesting(false)
            } else {
                // Frames land on this layer directly; SwiftUI never redraws for them.
                LiveFrameView()
                    .opacity(browser.hasLiveFrame ? 1 : 0)
                    .allowsHitTesting(false)
            }
            if !(usesEmbed ? embedReady : browser.hasLiveFrame) {
                // No picture from the browser yet (or the paused tab released it): show the video's thumbnail.
                // Overlaid on a clear view so the image's own size never stretches the player.
                Color.clear
                    .overlay {
                        AsyncImage(url: video.largeThumbnailURL) { $0.resizable().aspectRatio(contentMode: .fill) } placeholder: {
                            ProgressView().controlSize(.small)
                        }
                    }
                    .clipped()
            }
            if !video.isPlaying {
                Image(systemName: "play.fill")
                    .font(.system(size: compact ? 20 : 26, weight: .bold))
                    .foregroundStyle(.white)
                    .frame(width: compact ? 54 : 66, height: compact ? 54 : 66)
                    .background(Circle().fill(.black.opacity(0.45)))
                    .overlay(Circle().strokeBorder(.white.opacity(0.35), lineWidth: 1))
                    .shadow(color: .black.opacity(0.4), radius: 10)
                    .allowsHitTesting(false)
                    .transition(.scale(scale: 0.6).combined(with: .opacity))
            }
            overlay
                .opacity(showsChrome ? 1 : 0)
                .allowsHitTesting(showsChrome)
            if !showsChrome {
                // With the controls hidden, a thin progress line stays along the bottom edge.
                GeometryReader { geo in
                    Rectangle().fill(Color.red)
                        .frame(width: geo.size.width * video.progress, height: 3)
                        .frame(maxHeight: .infinity, alignment: .bottom)
                }
                .allowsHitTesting(false)
                .transition(.opacity)
            }
            if showQuality {
                qualityPanel
                    .transition(.opacity.combined(with: .scale(scale: 0.96)))
            }
        }
        .aspectRatio(16 / 9, contentMode: .fit)
        .clipShape(RoundedRectangle(cornerRadius: 16, style: .continuous))
        .overlay(RoundedRectangle(cornerRadius: 16, style: .continuous).strokeBorder(theme.border.opacity(0.6), lineWidth: 0.8))
        .contentShape(Rectangle())
        .animation(Motion.pop, value: video.isPlaying)
        .onTapGesture {
            if let onToggleSize, !isTheater { onToggleSize() } else { withAnimation(Motion.pop) { browser.toggle(video) } }
        }
        .help(onToggleSize != nil && !isTheater ? "Click to watch in the whole notch" : "")
        .onContinuousHover { phase in
            switch phase {
            case .active: revealChrome()
            case .ended: scheduleHideChrome(after: 0.8)
            }
        }
        .onAppear {
            browser.setStreamImages(!usesEmbed)
            browser.startFrames(for: video)
            revealChrome()
        }
        .onChange(of: usesEmbed) { _, embedded in
            if !embedded { browser.setStreamImages(true) }
        }
        .onChange(of: video.id) { _, _ in
            browser.setStreamImages(!usesEmbed)
            browser.switchFrames(to: video)
        }
        .onDisappear {
            hideChrome?.cancel()
            browser.stopFrames()
        }
    }

    /// Show the title and controls, then hide them again once the pointer rests.
    private func revealChrome() {
        if !chromeVisible { withAnimation(.easeOut(duration: 0.2)) { chromeVisible = true } }
        scheduleHideChrome(after: 2.5)
    }

    private func scheduleHideChrome(after delay: Double) {
        hideChrome?.cancel()
        hideChrome = Task { @MainActor in
            try? await Task.sleep(for: .seconds(delay))
            guard !Task.isCancelled else { return }
            withAnimation(.easeOut(duration: 0.4)) { chromeVisible = false }
        }
    }

    private var overlay: some View {
        VStack(spacing: 0) {
            LinearGradient(colors: [.black.opacity(0.75), .clear], startPoint: .top, endPoint: .bottom)
                .frame(height: compact ? 50 : 64)
                .overlay(alignment: .topLeading) {
                    VStack(alignment: .leading, spacing: 2) {
                        Text(video.title).font(.system(size: compact ? 12 : 13, weight: .semibold)).lineLimit(1)
                        if !video.channel.isEmpty {
                            Text(video.channel).font(.system(size: compact ? 10 : 11)).foregroundStyle(.white.opacity(0.7)).lineLimit(1)
                        }
                    }
                    .foregroundStyle(.white)
                    .padding(compact ? 10 : 12)
                }
            Spacer()
            VStack(spacing: compact ? 6 : 8) {
                HStack(spacing: 8) {
                    Text(Format.duration(video.current)).monospacedDigit()
                    PeekScrubber(progress: video.progress) { browser.seek(video, toFraction: $0) }
                        .frame(height: 12)
                    Text(Format.duration(video.duration)).monospacedDigit()
                }
                .font(.system(size: 10, weight: .medium))
                .foregroundStyle(.white.opacity(0.85))
                HStack(spacing: compact ? 10 : 14) {
                    peekButton("gearshape", compact ? 24 : 28) {
                        withAnimation(Motion.expoOut(0.3)) { showQuality.toggle() }
                    }
                    Spacer()
                    peekButton("gobackward.10", compact ? 26 : 30) { browser.seek(video, by: -10) }
                    peekButton(video.isPlaying ? "pause.fill" : "play.fill", compact ? 36 : 42, filled: true) {
                        withAnimation(Motion.pop) { browser.toggle(video) }
                    }
                    peekButton("goforward.10", compact ? 26 : 30) { browser.seek(video, by: 10) }
                    peekButton("forward.end.fill", compact ? 26 : 30) { browser.next(video) }
                    Spacer()
                    peekButton("arrow.up.forward.app", compact ? 24 : 28) { browser.focus(video) }
                    if let onToggleSize {
                        peekButton(isTheater ? "arrow.down.right.and.arrow.up.left" : "arrow.up.left.and.arrow.down.right",
                                   compact ? 24 : 28, action: onToggleSize)
                    }
                }
            }
            .padding(.horizontal, 12)
            .padding(.top, 22)
            .padding(.bottom, compact ? 8 : 10)
            .background(LinearGradient(colors: [.clear, .black.opacity(0.8)], startPoint: .top, endPoint: .bottom))
        }
    }

    private func peekButton(_ icon: String, _ size: CGFloat, filled: Bool = false, action: @escaping () -> Void) -> some View {
        Button(action: action) {
            Image(systemName: icon)
                .font(.system(size: size * 0.4, weight: .bold))
                .foregroundStyle(filled ? .black : .white)
                .contentTransition(.symbolEffect(.replace))
                .frame(width: size, height: size)
                .background(Circle().fill(filled ? .white : .white.opacity(0.16)))
        }
        .buttonStyle(.plain)
    }

    /// YouTube's own resolution plus how sharp / smooth the notch preview is.
    private var qualityPanel: some View {
        VStack(alignment: .leading, spacing: 12) {
            HStack {
                Text("Quality").font(.system(size: 14, weight: .bold))
                Spacer()
                peekButton("xmark", 24) { withAnimation(Motion.expoOut(0.3)) { showQuality = false } }
            }
            VStack(alignment: .leading, spacing: 6) {
                Text("YOUTUBE").font(.system(size: 10, weight: .semibold)).tracking(1).foregroundStyle(.white.opacity(0.6))
                if browser.youtubeQualities.isEmpty {
                    Text("This player doesn't offer quality choices.")
                        .font(.system(size: 11)).foregroundStyle(.white.opacity(0.7))
                } else {
                    FlowChips(items: browser.youtubeQualities, selected: browser.youtubeQuality,
                              label: YouTubeQualityLabel.text) { level in
                        browser.setYouTubeQuality(video, level: level)
                    }
                }
            }
            VStack(alignment: .leading, spacing: 6) {
                Text("NOTCH PREVIEW").font(.system(size: 10, weight: .semibold)).tracking(1).foregroundStyle(.white.opacity(0.6))
                FlowChips(items: PreviewQuality.allCases.map(\.rawValue), selected: settings.previewQuality.rawValue,
                          label: { PreviewQuality(rawValue: $0)?.title ?? $0 }) { raw in
                    if let q = PreviewQuality(rawValue: raw) { settings.previewQuality = q }
                }
                Text("Smooth moves more frames, Sharp shows more detail.")
                    .font(.system(size: 10)).foregroundStyle(.white.opacity(0.55))
            }
            Spacer(minLength: 0)
        }
        .foregroundStyle(.white)
        .padding(14)
        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
        .background(.ultraThinMaterial.opacity(0.95))
        .background(Color.black.opacity(0.55))
        .contentShape(Rectangle())
        .onTapGesture {}
    }
}

/// Audio: the list of open videos with controls. Video: the chosen one playing live, with the others beside it.
enum MediaViewMode: String {
    case audio, video
}

/// Now Playing's browser section with the Audio / Video switch.
struct BrowserMediaSection: View {
    @Environment(\.theme) private var theme
    @ObservedObject private var browser = BrowserMediaService.shared
    @AppStorage("nowPlaying.mode") private var modeRaw = MediaViewMode.audio.rawValue
    @State private var selectedID: String?
    @State private var showQuality = false
    /// Music app card shown beside the list in audio mode, if any.
    var musicCard: AnyView?

    private var mode: MediaViewMode { MediaViewMode(rawValue: modeRaw) ?? .audio }
    private var selected: BrowserVideo? {
        browser.videos.first { $0.id == selectedID } ?? browser.playingVideo ?? browser.videos.first
    }

    var body: some View {
        Group {
            if mode == .video, let video = selected, video.controllable {
                videoLayout(video)
                    .transition(.opacity.combined(with: .scale(scale: 0.97)))
            } else {
                VStack(spacing: 8) {
                    HStack(spacing: 10) {
                        modePicker
                        Spacer()
                        if browser.anyPlaying {
                            PillButton(title: "Pause all", icon: "pause.fill") { browser.pauseAll() }
                                .transition(.opacity)
                        }
                    }
                    HStack(spacing: 12) {
                        if let musicCard { musicCard.frame(width: 230) }
                        BrowserVideosCard()
                    }
                    .frame(maxHeight: .infinity)
                }
                .transition(.opacity)
            }
        }
        .animation(Motion.expoOut(0.4), value: modeRaw)
    }

    private var modePicker: some View {
        HStack(spacing: 2) {
            ForEach([MediaViewMode.audio, .video], id: \.self) { m in
                let on = mode == m
                Button {
                    modeRaw = m.rawValue
                } label: {
                    Label(m == .audio ? "Audio" : "Video", systemImage: m == .audio ? "waveform" : "play.rectangle.fill")
                        .font(.system(size: 12, weight: .semibold))
                        .foregroundStyle(on ? theme.background : theme.text.opacity(0.8))
                        .padding(.horizontal, 12)
                        .padding(.vertical, 5)
                        .background { if on { Capsule().fill(theme.accent) } }
                        .contentShape(Capsule())
                }
                .buttonStyle(.plain)
            }
        }
        .padding(3)
        .background(Capsule().fill(theme.text.opacity(0.07)))
    }

    /// The video takes all the height it can; controls and the other videos sit in a slim sidebar.
    private func videoLayout(_ video: BrowserVideo) -> some View {
        HStack(alignment: .top, spacing: 12) {
            LiveVideoPlayer(video: video, showQuality: $showQuality, compact: true,
                            onToggleSize: { NotchState.shared.enterTheater(video.id) })
                .frame(maxWidth: .infinity, maxHeight: .infinity)

            VStack(alignment: .leading, spacing: 8) {
                modePicker
                if browser.anyPlaying {
                    Button { browser.pauseAll() } label: {
                        Label("Pause all", systemImage: "pause.fill")
                            .font(.system(size: 12, weight: .semibold))
                            .foregroundStyle(theme.text)
                            .frame(maxWidth: .infinity)
                            .padding(.vertical, 6)
                            .background(Capsule().fill(theme.text.opacity(0.08)))
                    }
                    .buttonStyle(.plain)
                    .transition(.opacity)
                }
                Text(browser.videos.count == 1 ? "Open video" : "Open videos · \(browser.videos.count)")
                    .font(.system(size: 11, weight: .semibold))
                    .foregroundStyle(theme.secondaryText)
                    .padding(.top, 2)
                ScrollView {
                    VStack(spacing: 6) {
                        ForEach(browser.videos) { v in
                            videoChoice(v, active: v.id == video.id)
                        }
                    }
                }
                .scrollIndicators(.never)
                Button { NotchState.shared.enterTheater(video.id) } label: {
                    Label("Full notch", systemImage: "arrow.up.left.and.arrow.down.right")
                        .font(.system(size: 12, weight: .semibold))
                        .foregroundStyle(theme.background)
                        .frame(maxWidth: .infinity)
                        .padding(.vertical, 7)
                        .background(Capsule().fill(theme.accent))
                }
                .buttonStyle(.plain)
            }
            .frame(width: 196)
        }
    }


    private func videoChoice(_ v: BrowserVideo, active: Bool) -> some View {
        Button {
            withAnimation(Motion.expoOut(0.3)) { selectedID = v.id }
        } label: {
            HStack(spacing: 8) {
                AsyncImage(url: v.thumbnailURL) { $0.resizable().aspectRatio(contentMode: .fill) } placeholder: {
                    theme.text.opacity(0.08)
                }
                .frame(width: 48, height: 28)
                .clipShape(RoundedRectangle(cornerRadius: 6, style: .continuous))
                VStack(alignment: .leading, spacing: 1) {
                    Text(v.title).font(.system(size: 11, weight: .semibold)).foregroundStyle(theme.text).lineLimit(1)
                    HStack(spacing: 4) {
                        if v.isPlaying { MusicBars(color: theme.accent).frame(width: 10, height: 8) }
                        Text(v.channel.isEmpty ? v.browser.shortName : v.channel).lineLimit(1)
                    }
                    .font(.system(size: 10))
                    .foregroundStyle(theme.secondaryText)
                }
                Spacer(minLength: 0)
            }
            .padding(6)
            .background(RoundedRectangle(cornerRadius: 10, style: .continuous)
                .fill(active ? theme.accent.opacity(0.16) : theme.text.opacity(0.04)))
            .overlay(RoundedRectangle(cornerRadius: 10, style: .continuous)
                .strokeBorder(active ? theme.accent.opacity(0.6) : .clear, lineWidth: 0.8))
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
    }
}

/// Seek bar over the live video.
private struct PeekScrubber: View {
    let progress: Double
    let onSeek: (Double) -> Void
    @State private var hovering = false

    var body: some View {
        GeometryReader { geo in
            ZStack(alignment: .leading) {
                Capsule().fill(.white.opacity(0.25))
                Capsule().fill(Color.red).frame(width: max(4, geo.size.width * progress))
            }
            .frame(height: hovering ? 6 : 4)
            .frame(maxHeight: .infinity)
            .contentShape(Rectangle())
            .onTapGesture { location in onSeek(location.x / max(geo.size.width, 1)) }
        }
        .onHover { h in withAnimation(Motion.expoOut(0.2)) { hovering = h } }
    }
}

/// Row of selectable capsules that wraps onto new lines.
private struct FlowChips: View {
    let items: [String]
    let selected: String
    let label: (String) -> String
    let onSelect: (String) -> Void

    var body: some View {
        LazyVGrid(columns: [GridItem(.adaptive(minimum: 62), spacing: 6)], alignment: .leading, spacing: 6) {
            ForEach(items, id: \.self) { item in
                let on = item == selected
                Button { withAnimation(Motion.pop) { onSelect(item) } } label: {
                    Text(label(item))
                        .font(.system(size: 11, weight: .semibold))
                        .foregroundStyle(on ? .black : .white)
                        .frame(maxWidth: .infinity)
                        .padding(.vertical, 5)
                        .background(Capsule().fill(on ? Color.white : Color.white.opacity(0.14)))
                }
                .buttonStyle(.plain)
            }
        }
    }
}

/// Layer-backed view the frame sink draws into.
private struct LiveFrameView: NSViewRepresentable {
    /// Draws only; clicks fall through to the SwiftUI controls on top, so one click is enough.
    final class PassthroughView: NSView {
        override func hitTest(_ point: NSPoint) -> NSView? { nil }
    }

    func makeNSView(context: Context) -> NSView {
        let view = PassthroughView()
        view.wantsLayer = true
        let layer = CALayer()
        layer.contentsGravity = .resizeAspect
        layer.backgroundColor = NSColor.black.cgColor
        layer.minificationFilter = .trilinear
        layer.magnificationFilter = .linear
        view.layer = layer
        BrowserMediaService.shared.frameSink.attach(layer)
        return view
    }

    func updateNSView(_ nsView: NSView, context: Context) {}
}

/// A browser video filling the whole expanded notch. Esc or the shrink button returns.
struct VideoTheaterView: View {
    @EnvironmentObject private var state: NotchState
    @Environment(\.theme) private var theme
    @ObservedObject private var browser = BrowserMediaService.shared
    @State private var showQuality = false

    var body: some View {
        if let video = browser.videos.first(where: { $0.id == state.theaterVideoID }) {
            VStack(spacing: 6) {
                HStack(spacing: 8) {
                    Image(systemName: "play.rectangle.fill").foregroundStyle(.red)
                    Text(video.browser.shortName).font(.system(size: 12, weight: .semibold))
                    if video.isPlaying { MusicBars(color: theme.accent).frame(width: 14, height: 11) }
                    Spacer()
                    Color.clear.frame(width: state.hasPhysicalNotch ? state.notchSize.width - 40 : 0)
                    Spacer()
                    PillButton(title: "Exit", icon: "arrow.down.right.and.arrow.up.left") { state.exitTheater() }
                        .help("Back to Now Playing (Esc)")
                }
                .frame(height: max(state.notchSize.height, 28))
                .padding(.horizontal, 18)

                LiveVideoPlayer(video: video, showQuality: $showQuality,
                                onToggleSize: { state.exitTheater() }, isTheater: true)
                    .frame(maxWidth: .infinity, maxHeight: .infinity)
                    .padding(.horizontal, 12)
                    .padding(.bottom, 12)
            }
            .foregroundStyle(theme.text)
        } else {
            // The tab was closed while watching.
            Color.clear.onAppear { state.exitTheater() }
        }
    }
}
