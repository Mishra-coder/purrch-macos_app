import AppKit
import SwiftUI

// MARK: - MediaRemote bridge (private framework, loaded lazily)

/// Thin wrapper around the private MediaRemote framework. Every symbol is optional:
/// if the framework or a function is missing we simply fall back to AppleScript.
private final class MediaRemoteBridge {
    typealias GetInfoFn = @convention(c) (DispatchQueue, @escaping @convention(block) (CFDictionary?) -> Void) -> Void
    typealias SendCommandFn = @convention(c) (UInt32, CFDictionary?) -> Bool
    typealias RegisterFn = @convention(c) (DispatchQueue) -> Void
    typealias GetPIDFn = @convention(c) (DispatchQueue, @escaping @convention(block) (Int32) -> Void) -> Void
    typealias SetElapsedFn = @convention(c) (Double) -> Void

    static let shared = MediaRemoteBridge()

    let getInfo: GetInfoFn?
    let sendCommand: SendCommandFn?
    let register: RegisterFn?
    let getPID: GetPIDFn?
    let setElapsed: SetElapsedFn?

    enum Command: UInt32 {
        case play = 0, pause = 1, togglePlayPause = 2, stop = 3, next = 4, previous = 5
    }

    private init() {
        let url = URL(fileURLWithPath: "/System/Library/PrivateFrameworks/MediaRemote.framework") as CFURL
        guard let bundle = CFBundleCreate(kCFAllocatorDefault, url) else {
            getInfo = nil; sendCommand = nil; register = nil; getPID = nil; setElapsed = nil
            return
        }
        func sym<T>(_ name: String, _ type: T.Type) -> T? {
            guard let ptr = CFBundleGetFunctionPointerForName(bundle, name as CFString) else { return nil }
            return unsafeBitCast(ptr, to: type)
        }
        getInfo = sym("MRMediaRemoteGetNowPlayingInfo", GetInfoFn.self)
        sendCommand = sym("MRMediaRemoteSendCommand", SendCommandFn.self)
        register = sym("MRMediaRemoteRegisterForNowPlayingNotifications", RegisterFn.self)
        getPID = sym("MRMediaRemoteGetNowPlayingApplicationPID", GetPIDFn.self)
        setElapsed = sym("MRMediaRemoteSetElapsedTime", SetElapsedFn.self)
    }

    @discardableResult
    func send(_ command: Command) -> Bool {
        guard let sendCommand else { return false }
        return sendCommand(command.rawValue, nil)
    }
}

/// Snapshot of what is playing, produced off the main thread.
private struct NowPlayingSnapshot {
    enum Source { case mediaRemote, spotify, music }
    var source: Source
    var title: String
    var artist: String
    var album: String
    var duration: Double
    var position: Double
    var isPlaying: Bool
    var sourceApp: String
    var artworkData: Data?
    var artworkURL: String?
}

// MARK: - Service

@MainActor
final class NowPlayingService: ObservableObject {
    static let shared = NowPlayingService()

    @Published var title = ""
    @Published var artist = ""
    @Published var album = ""
    @Published var artwork: NSImage?
    @Published var isPlaying = false
    @Published var duration: Double = 0
    @Published var position: Double = 0
    @Published var sourceApp = ""

    /// True when there is a track to show (playing or paused).
    var hasTrack: Bool { !title.isEmpty }

    private static let spotifyID = "com.spotify.client"
    private static let musicID = "com.apple.Music"

    private let scriptQueue = DispatchQueue(label: "purrch.nowplaying.applescript", qos: .utility)
    private let mrQueue = DispatchQueue(label: "purrch.nowplaying.mediaremote", qos: .utility)
    private var timer: Timer?
    private var started = false
    private var pollInFlight = false
    private var pollStartedAt = Date.distantPast
    private var idleTicks = 0
    private var source: NowPlayingSnapshot.Source?
    private var artworkKey = ""
    /// Position reported by the last poll and when it was taken, for local interpolation.
    private var anchorPosition: Double = 0
    private var anchorDate = Date()
    private var suppressUntil = Date.distantPast

    private init() {}

    func start() {
        guard !started else { return }
        started = true
        MediaRemoteBridge.shared.register?(mrQueue)
        let center = NotificationCenter.default
        for name in ["kMRMediaRemoteNowPlayingInfoDidChangeNotification",
                     "kMRMediaRemoteNowPlayingApplicationIsPlayingDidChangeNotification"] {
            center.addObserver(forName: Notification.Name(name), object: nil, queue: .main) { [weak self] _ in
                MainActor.assumeIsolated { self?.poll(force: true) }
            }
        }
        let ws = NSWorkspace.shared.notificationCenter
        for name in [NSWorkspace.didLaunchApplicationNotification, NSWorkspace.didTerminateApplicationNotification] {
            ws.addObserver(forName: name, object: nil, queue: .main) { [weak self] _ in
                MainActor.assumeIsolated {
                    DispatchQueue.main.asyncAfter(deadline: .now() + 1) { self?.poll(force: true) }
                }
            }
        }
        let t = Timer(timeInterval: 1.5, repeats: true) { [weak self] _ in
            MainActor.assumeIsolated { self?.poll(force: false) }
        }
        RunLoop.main.add(t, forMode: .common)
        timer = t
        poll(force: true)
    }

    /// Smoothly interpolated playback position for display.
    func elapsed(at date: Date = Date()) -> Double {
        guard isPlaying else { return position }
        let p = anchorPosition + date.timeIntervalSince(anchorDate)
        return duration > 0 ? min(duration, max(0, p)) : max(0, p)
    }

    // MARK: Controls

    func togglePlayPause() {
        position = elapsed()
        isPlaying.toggle()
        anchorPosition = position
        anchorDate = Date()
        suppressUntil = Date().addingTimeInterval(1.2)
        command(mr: .togglePlayPause, script: "playpause")
    }

    func next() {
        command(mr: .next, script: "next track")
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.5) { [weak self] in self?.poll(force: true) }
    }

    func previous() {
        command(mr: .previous, script: "previous track")
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.5) { [weak self] in self?.poll(force: true) }
    }

    func seek(to seconds: Double) {
        let target = duration > 0 ? min(max(0, seconds), duration) : max(0, seconds)
        position = target
        anchorPosition = target
        anchorDate = Date()
        suppressUntil = Date().addingTimeInterval(1.2)
        switch source {
        case .spotify, .music:
            let app = source == .spotify ? "Spotify" : "Music"
            runControlScript("tell application \"\(app)\" to set player position to \(String(format: "%.2f", target))")
        default:
            MediaRemoteBridge.shared.setElapsed?(target)
        }
    }

    private func command(mr: MediaRemoteBridge.Command, script: String) {
        switch source {
        case .spotify:
            runControlScript("tell application \"Spotify\" to \(script)")
        case .music:
            runControlScript("tell application \"Music\" to \(script)")
        default:
            if !MediaRemoteBridge.shared.send(mr) {
                if Self.isRunning(Self.spotifyID) {
                    runControlScript("tell application \"Spotify\" to \(script)")
                } else if Self.isRunning(Self.musicID) {
                    runControlScript("tell application \"Music\" to \(script)")
                }
            }
        }
    }

    private func runControlScript(_ source: String) {
        scriptQueue.async {
            var error: NSDictionary?
            NSAppleScript(source: source)?.executeAndReturnError(&error)
        }
    }

    // MARK: Polling

    private static func isRunning(_ bundleID: String) -> Bool {
        !NSRunningApplication.runningApplications(withBundleIdentifier: bundleID).isEmpty
    }

    private func poll(force: Bool) {
        if pollInFlight, Date().timeIntervalSince(pollStartedAt) < 6 { return }
        let spotify = Self.isRunning(Self.spotifyID)
        let music = Self.isRunning(Self.musicID)
        // With no known player and nothing showing, only check occasionally.
        if !force, !spotify, !music, !hasTrack {
            idleTicks += 1
            if idleTicks % 4 != 0 { return }
        }
        idleTicks = 0
        pollInFlight = true
        pollStartedAt = Date()

        let bridge = MediaRemoteBridge.shared
        let queue = mrQueue
        guard let getInfo = bridge.getInfo else {
            pollScripts(spotify: spotify, music: music)
            return
        }
        getInfo(mrQueue) { [weak self] dict in
            let info = (dict as NSDictionary?) as? [String: Any] ?? [:]
            let title = info["kMRMediaRemoteNowPlayingInfoTitle"] as? String ?? ""
            guard !title.isEmpty else {
                DispatchQueue.main.async { self?.pollScripts(spotify: spotify, music: music) }
                return
            }
            let rate = (info["kMRMediaRemoteNowPlayingInfoPlaybackRate"] as? NSNumber)?.doubleValue ?? 0
            var elapsed = (info["kMRMediaRemoteNowPlayingInfoElapsedTime"] as? NSNumber)?.doubleValue ?? 0
            if rate > 0, let stamp = info["kMRMediaRemoteNowPlayingInfoTimestamp"] as? Date {
                elapsed += Date().timeIntervalSince(stamp) * rate
            }
            var snap = NowPlayingSnapshot(
                source: .mediaRemote,
                title: title,
                artist: info["kMRMediaRemoteNowPlayingInfoArtist"] as? String ?? "",
                album: info["kMRMediaRemoteNowPlayingInfoAlbum"] as? String ?? "",
                duration: (info["kMRMediaRemoteNowPlayingInfoDuration"] as? NSNumber)?.doubleValue ?? 0,
                position: elapsed,
                isPlaying: rate > 0,
                sourceApp: "",
                artworkData: info["kMRMediaRemoteNowPlayingInfoArtworkData"] as? Data,
                artworkURL: nil)
            let finish: (NowPlayingSnapshot) -> Void = { s in
                DispatchQueue.main.async { self?.apply(s) }
            }
            if let getPID = bridge.getPID {
                getPID(queue) { pid in
                    DispatchQueue.main.async {
                        snap.sourceApp = NSRunningApplication(processIdentifier: pid)?.localizedName ?? "Media"
                        self?.apply(snap)
                    }
                }
            } else {
                snap.sourceApp = "Media"
                finish(snap)
            }
        }
    }

    private func pollScripts(spotify: Bool, music: Bool) {
        guard spotify || music else {
            apply(nil)
            return
        }
        scriptQueue.async { [weak self] in
            var best: NowPlayingSnapshot?
            if spotify, let s = Self.querySpotify() { best = s }
            if music, let m = Self.queryMusic() {
                // Prefer whichever is actually playing.
                if best == nil || (!(best!.isPlaying) && m.isPlaying) { best = m }
            }
            let result = best
            DispatchQueue.main.async { self?.apply(result) }
        }
    }

    private nonisolated static let sep = "\u{1F}"

    private nonisolated static func run(_ source: String) -> NSAppleEventDescriptor? {
        var error: NSDictionary?
        guard let script = NSAppleScript(source: source) else { return nil }
        let result = script.executeAndReturnError(&error)
        return error == nil ? result : nil
    }

    private nonisolated static func number(_ s: String) -> Double {
        Double(s.trimmingCharacters(in: .whitespaces).replacingOccurrences(of: ",", with: ".")) ?? 0
    }

    private nonisolated static func querySpotify() -> NowPlayingSnapshot? {
        let src = """
        tell application "Spotify"
            set s to player state as string
            if s is "stopped" then return "stopped"
            set t to current track
            set d to (ASCII character 31)
            return s & d & (name of t) & d & (artist of t) & d & (album of t) & d & ((duration of t) / 1000) & d & (player position) & d & (artwork url of t)
        end tell
        """
        guard let text = run(src)?.stringValue, text != "stopped" else { return nil }
        let p = text.components(separatedBy: sep)
        guard p.count >= 7, !p[1].isEmpty else { return nil }
        return NowPlayingSnapshot(source: .spotify, title: p[1], artist: p[2], album: p[3],
                                  duration: number(p[4]), position: number(p[5]),
                                  isPlaying: p[0] == "playing", sourceApp: "Spotify",
                                  artworkData: nil, artworkURL: p[6])
    }

    private nonisolated static func queryMusic() -> NowPlayingSnapshot? {
        let src = """
        tell application "Music"
            set s to player state as string
            if s is "stopped" then return "stopped"
            set t to current track
            set d to (ASCII character 31)
            return s & d & (name of t) & d & (artist of t) & d & (album of t) & d & (duration of t) & d & (player position)
        end tell
        """
        guard let text = run(src)?.stringValue, text != "stopped" else { return nil }
        let p = text.components(separatedBy: sep)
        guard p.count >= 6, !p[1].isEmpty else { return nil }
        return NowPlayingSnapshot(source: .music, title: p[1], artist: p[2], album: p[3],
                                  duration: number(p[4]), position: number(p[5]),
                                  isPlaying: p[0] == "playing", sourceApp: "Music",
                                  artworkData: nil, artworkURL: nil)
    }

    private nonisolated static func musicArtwork() -> Data? {
        run("tell application \"Music\" to return data of artwork 1 of current track")?.data
    }

    private func apply(_ snap: NowPlayingSnapshot?) {
        pollInFlight = false
        guard let snap else {
            if hasTrack {
                title = ""; artist = ""; album = ""; artwork = nil
                isPlaying = false; duration = 0; position = 0; sourceApp = ""
                artworkKey = ""; source = nil
            }
            return
        }
        source = snap.source
        if title != snap.title { title = snap.title }
        if artist != snap.artist { artist = snap.artist }
        if album != snap.album { album = snap.album }
        if sourceApp != snap.sourceApp { sourceApp = snap.sourceApp }
        if duration != snap.duration { duration = snap.duration }
        // Right after a local action the player may still report the old state.
        if Date() >= suppressUntil {
            if isPlaying != snap.isPlaying { isPlaying = snap.isPlaying }
            position = snap.position
            anchorPosition = snap.position
            anchorDate = Date()
        }

        let key = "\(snap.sourceApp)|\(snap.title)|\(snap.artist)|\(snap.album)|\(snap.artworkData?.count ?? 0)"
        guard key != artworkKey else { return }
        artworkKey = key
        if let data = snap.artworkData {
            artwork = NSImage(data: data)
        } else if let urlString = snap.artworkURL, let url = URL(string: urlString), !urlString.isEmpty {
            URLSession.shared.dataTask(with: url) { [weak self] data, _, _ in
                guard let data else { return }
                DispatchQueue.main.async {
                    guard let self, self.artworkKey == key else { return }
                    self.artwork = NSImage(data: data)
                }
            }.resume()
        } else if snap.source == .music {
            scriptQueue.async { [weak self] in
                let data = Self.musicArtwork()
                DispatchQueue.main.async {
                    guard let self, self.artworkKey == key else { return }
                    self.artwork = data.flatMap(NSImage.init(data:))
                }
            }
        } else {
            artwork = nil
        }
    }
}

// MARK: - View

struct NowPlayingModule: View {
    @Environment(\.theme) private var theme
    @ObservedObject private var player = NowPlayingService.shared
    @ObservedObject private var browser = BrowserMediaService.shared

    var body: some View {
        Group {
            if !browser.videos.isEmpty || !browser.blockedBrowsers.isEmpty {
                BrowserMediaSection(musicCard: player.hasTrack ? AnyView(compactTrack) : nil)
            } else if player.hasTrack {
                content
            } else {
                Card("Now Playing", icon: "music.note") {
                    EmptyState(icon: "music.note",
                               title: "Nothing playing",
                               subtitle: "Play something in Music, Spotify or YouTube")
                }
            }
        }
        .animation(Motion.expoOut(0.45), value: browser.videos.isEmpty)
        .onAppear {
            player.start()
            browser.poll()
        }
    }

    /// Small music card shown beside the browser video list.
    private var compactTrack: some View {
        VStack(spacing: 10) {
            artworkView.frame(width: 120, height: 120)
            VStack(spacing: 2) {
                Text(player.title).font(.system(size: 14, weight: .bold)).lineLimit(1)
                Text(player.artist).font(.system(size: 12)).foregroundStyle(theme.secondaryText).lineLimit(1)
            }
            HStack(spacing: 12) {
                IconButton(systemName: "backward.fill", size: 30, help: "Previous") { player.previous() }
                IconButton(systemName: player.isPlaying ? "pause.fill" : "play.fill", size: 40, prominent: true,
                           help: player.isPlaying ? "Pause" : "Play") { player.togglePlayPause() }
                IconButton(systemName: "forward.fill", size: 30, help: "Next") { player.next() }
            }
        }
        .padding(12)
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .background(backdrop)
        .clipShape(RoundedRectangle(cornerRadius: 18, style: .continuous))
        .overlay(RoundedRectangle(cornerRadius: 18, style: .continuous).strokeBorder(theme.border, lineWidth: 0.8))
    }

    private var content: some View {
        HStack(spacing: 22) {
            artworkView
                .frame(width: 236, height: 236)
            VStack(alignment: .leading, spacing: 0) {
                HStack(spacing: 8) {
                    Tag(text: player.sourceApp.isEmpty ? "Media" : player.sourceApp, color: theme.accent)
                    if player.isPlaying {
                        MusicBars(color: theme.accent).frame(width: 18, height: 14)
                    } else {
                        Text("PAUSED")
                            .font(.system(size: 9, weight: .bold)).tracking(0.8)
                            .foregroundStyle(theme.secondaryText)
                    }
                    Spacer()
                    BigBars(active: player.isPlaying, color: theme.accent)
                        .frame(width: 120, height: 28)
                }
                Spacer(minLength: 10)
                Text(player.title)
                    .font(.system(size: 24, weight: .bold, design: .rounded))
                    .foregroundStyle(theme.text)
                    .lineLimit(1)
                Text(player.artist)
                    .font(.system(size: 15, weight: .medium))
                    .foregroundStyle(theme.text.opacity(0.85))
                    .lineLimit(1)
                    .padding(.top, 2)
                if !player.album.isEmpty {
                    Text(player.album)
                        .font(.system(size: 12))
                        .foregroundStyle(theme.secondaryText)
                        .lineLimit(1)
                        .padding(.top, 2)
                }
                Spacer(minLength: 12)
                Scrubber(player: player)
                Spacer(minLength: 12)
                HStack(spacing: 18) {
                    Spacer()
                    IconButton(systemName: "backward.fill", size: 44, help: "Previous") { player.previous() }
                    IconButton(systemName: player.isPlaying ? "pause.fill" : "play.fill", size: 58, prominent: true,
                               help: player.isPlaying ? "Pause" : "Play") { player.togglePlayPause() }
                    IconButton(systemName: "forward.fill", size: 44, help: "Next") { player.next() }
                    Spacer()
                }
            }
            .padding(.vertical, 6)
        }
        .padding(16)
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .background(backdrop)
        .clipShape(RoundedRectangle(cornerRadius: 20, style: .continuous))
        .overlay(RoundedRectangle(cornerRadius: 20, style: .continuous).strokeBorder(theme.border, lineWidth: 0.8))
    }

    private var artworkView: some View {
        ZStack {
            RoundedRectangle(cornerRadius: 18, style: .continuous).fill(theme.text.opacity(0.08))
            if let art = player.artwork {
                Image(nsImage: art).resizable().aspectRatio(contentMode: .fill)
            } else {
                Image(systemName: "music.note")
                    .font(.system(size: 64, weight: .light))
                    .foregroundStyle(theme.accent)
            }
        }
        .clipShape(RoundedRectangle(cornerRadius: 18, style: .continuous))
        .shadow(color: .black.opacity(0.5), radius: 14, y: 6)
        .scaleEffect(player.isPlaying ? 1 : 0.94)
        .animation(.spring(response: 0.45, dampingFraction: 0.7), value: player.isPlaying)
    }

    private var backdrop: some View {
        ZStack {
            theme.surface
            if let art = player.artwork {
                Image(nsImage: art)
                    .resizable()
                    .aspectRatio(contentMode: .fill)
                    .blur(radius: 50)
                    .opacity(0.45)
                    .scaleEffect(1.4)
            } else {
                RadialGradient(colors: [theme.accent.opacity(0.18), .clear],
                               center: .topLeading, startRadius: 10, endRadius: 400)
            }
            LinearGradient(colors: [theme.background.opacity(0.1), theme.background.opacity(0.55)],
                           startPoint: .top, endPoint: .bottom)
        }
    }
}

/// Draggable progress bar with elapsed / remaining labels.
private struct Scrubber: View {
    @Environment(\.theme) private var theme
    @ObservedObject var player: NowPlayingService
    @State private var dragValue: Double?
    @State private var hovering = false

    var body: some View {
        TimelineView(.periodic(from: .now, by: 0.25)) { context in
            let elapsed = dragValue ?? player.elapsed(at: context.date)
            let total = max(player.duration, 0.001)
            let fraction = player.duration > 0 ? min(1, max(0, elapsed / total)) : 0
            VStack(spacing: 6) {
                GeometryReader { geo in
                    let w = geo.size.width
                    ZStack(alignment: .leading) {
                        Capsule().fill(theme.text.opacity(0.15))
                        Capsule().fill(theme.accent)
                            .frame(width: max(0, w * fraction))
                            .themeGlow(theme, radius: 4)
                        Circle()
                            .fill(theme.text)
                            .frame(width: hovering || dragValue != nil ? 14 : 10)
                            .offset(x: w * fraction - (hovering || dragValue != nil ? 7 : 5))
                            .shadow(color: .black.opacity(0.4), radius: 2)
                    }
                    .frame(height: hovering || dragValue != nil ? 7 : 5)
                    .frame(maxHeight: .infinity)
                    .contentShape(Rectangle())
                    .gesture(
                        DragGesture(minimumDistance: 0)
                            .onChanged { v in
                                guard player.duration > 0 else { return }
                                dragValue = min(1, max(0, v.location.x / max(w, 1))) * player.duration
                            }
                            .onEnded { v in
                                guard player.duration > 0 else { return }
                                let target = min(1, max(0, v.location.x / max(w, 1))) * player.duration
                                player.seek(to: target)
                                dragValue = nil
                            }
                    )
                    .onHover { hovering = $0 }
                    .animation(.easeOut(duration: 0.15), value: hovering)
                }
                .frame(height: 16)
                HStack {
                    Text(Format.duration(elapsed))
                    Spacer()
                    Text(player.duration > 0 ? "-" + Format.duration(max(0, player.duration - elapsed)) : "--:--")
                }
                .font(.system(size: 11, weight: .medium, design: .rounded))
                .monospacedDigit()
                .foregroundStyle(theme.secondaryText)
            }
        }
    }
}

/// Wide animated equaliser shown next to the track info.
private struct BigBars: View {
    var active: Bool
    var color: Color
    private let count = 18

    var body: some View {
        TimelineView(.animation(minimumInterval: 1 / 20, paused: !active)) { context in
            let t = context.date.timeIntervalSinceReferenceDate
            HStack(alignment: .bottom, spacing: 3) {
                ForEach(0..<count, id: \.self) { i in
                    let d = Double(i)
                    let h = active
                        ? 0.18 + 0.82 * abs(sin(t * (1.6 + d.truncatingRemainder(dividingBy: 5) * 0.55) + d * 0.9)
                                             * cos(t * 0.7 + d * 0.33))
                        : 0.12
                    Capsule()
                        .fill(color.opacity(0.35 + 0.65 * h))
                        .frame(maxWidth: .infinity)
                        .frame(height: max(3, 28 * h))
                }
            }
            .frame(maxHeight: .infinity, alignment: .bottom)
        }
    }
}
