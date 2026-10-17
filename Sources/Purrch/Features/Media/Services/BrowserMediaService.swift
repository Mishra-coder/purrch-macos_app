import AppKit
import Foundation
import ImageIO
import QuartzCore

/// Browsers whose tabs Purrch can read and control over Apple Events.
enum BrowserKind: String, CaseIterable, Codable {
    case chrome = "Google Chrome"
    case brave = "Brave Browser"
    case edge = "Microsoft Edge"
    case arc = "Arc"
    case vivaldi = "Vivaldi"
    case chromium = "Chromium"
    case safari = "Safari"

    var bundleID: String {
        switch self {
        case .chrome: "com.google.Chrome"
        case .brave: "com.brave.Browser"
        case .edge: "com.microsoft.edgemac"
        case .arc: "company.thebrowser.Browser"
        case .vivaldi: "com.vivaldi.Vivaldi"
        case .chromium: "org.chromium.Chromium"
        case .safari: "com.apple.Safari"
        }
    }

    var shortName: String {
        switch self {
        case .chrome: "Chrome"
        case .brave: "Brave"
        case .edge: "Edge"
        case .safari: "Safari"
        default: rawValue
        }
    }

    var isRunning: Bool { !NSRunningApplication.runningApplications(withBundleIdentifier: bundleID).isEmpty }

    /// Where the user turns on JavaScript from Apple Events.
    var permissionHint: String {
        self == .safari
            ? "Safari › Develop › Allow JavaScript from Apple Events"
            : "\(shortName) › View › Developer › Allow JavaScript from Apple Events"
    }

    /// UI-scripting that ticks "Allow JavaScript from Apple Events" in the browser's menu (if not ticked).
    var enableScript: String {
        let item = "menu item \"Allow JavaScript from Apple Events\""
        let path = self == .safari
            ? "\(item) of menu 1 of menu bar item \"Develop\" of menu bar 1"
            : "\(item) of menu 1 of menu item \"Developer\" of menu 1 of menu bar item \"View\" of menu bar 1"
        return """
        tell application "\(rawValue)" to activate
        delay 0.4
        tell application "System Events"
            tell process "\(rawValue)"
                set mi to \(path)
                if (value of attribute "AXMenuItemMarkChar" of mi) is not "✓" then click mi
                delay 0.5
                -- Safari asks for confirmation.
                try
                    click button "Allow" of sheet 1 of window 1
                end try
                try
                    click button "Allow" of window 1
                end try
            end tell
        end tell
        return "ok"
        """
    }
}

/// Progress of the one-click "let Purrch control this browser" flow.
enum BrowserEnableStatus: Equatable {
    case idle
    /// Waiting for the user to switch Purrch on in Privacy & Security › Accessibility.
    case needsAccessibility
    case working
    case failed(String)
}

/// A YouTube video open in a browser tab.
struct BrowserVideo: Identifiable, Equatable {
    let browser: BrowserKind
    let windowID: Int
    /// Chrome-family: the tab's id. Safari: the tab's index in its window.
    let tabRef: Int
    var url: String
    var title: String
    var channel: String
    var isPlaying: Bool
    var current: Double
    var duration: Double
    /// False when the browser blocks JavaScript from Apple Events; the video is listed but can't be controlled.
    var controllable: Bool

    var id: String { "\(browser.rawValue)-\(windowID)-\(tabRef)" }

    var videoID: String? {
        guard let comps = URLComponents(string: url) else { return nil }
        if let v = comps.queryItems?.first(where: { $0.name == "v" })?.value { return v }
        let parts = comps.path.split(separator: "/")
        if let i = parts.firstIndex(of: "shorts"), i + 1 < parts.count { return String(parts[i + 1]) }
        return nil
    }

    var thumbnailURL: URL? { videoID.flatMap { URL(string: "https://i.ytimg.com/vi/\($0)/mqdefault.jpg") } }
    /// Sharper still for the large players (480x360, letterboxed; crop it to 16:9).
    var largeThumbnailURL: URL? { videoID.flatMap { URL(string: "https://i.ytimg.com/vi/\($0)/hqdefault.jpg") } }
    var progress: Double { duration > 0 ? min(1, current / duration) : 0 }
}

/// Finds YouTube tabs in running browsers and plays / pauses each one, so several videos can be
/// handled from the notch. Needs the browser's "Allow JavaScript from Apple Events" for control.
@MainActor
final class BrowserMediaService: ObservableObject {
    static let shared = BrowserMediaService()

    @Published private(set) var videos: [BrowserVideo] = []
    /// Browsers that have YouTube tabs open but refuse JavaScript from Apple Events.
    @Published private(set) var blockedBrowsers: Set<BrowserKind> = []
    /// Thumbnail of the video that is playing, for the closed-notch live activity.
    @Published private(set) var playingArtwork: NSImage?

    var anyPlaying: Bool { videos.contains { $0.isPlaying } }
    var playingVideo: BrowserVideo? { videos.first { $0.isPlaying } }

    private let queue = DispatchQueue(label: "purrch.browsermedia", qos: .utility)
    private var timer: Timer?
    private var polling = false
    private var artworkVideoID: String?

    nonisolated private static let sep = "\u{1F}"

    /// Read in the page: play state, position, length, title and channel of the first <video>.
    nonisolated private static let probeJS = """
    (()=>{const v=document.querySelector('video');const ch=document.querySelector('ytd-video-owner-renderer #channel-name a, #owner #channel-name a, ytmusic-player-bar .byline a');\
    if(!v)return '';return JSON.stringify({p:!v.paused&&!v.ended,c:v.currentTime||0,d:isFinite(v.duration)?v.duration:0,\
    t:document.title.replace(/^\\(\\d+\\)\\s*/,'').replace(/ - YouTube( Music)?$/,''),ch:ch?ch.textContent.trim():''})})()
    """

    func start() {
        guard timer == nil else { return }
        timer = Timer.scheduledTimer(withTimeInterval: 2.0, repeats: true) { [weak self] _ in
            MainActor.assumeIsolated { self?.poll() }
        }
        poll()
    }

    // MARK: Polling

    private var lastPoll = Date.distantPast

    func poll() {
        guard !polling else { return }
        if streamingID != nil, Date().timeIntervalSince(lastPoll) < 6 { return }
        lastPoll = Date()
        let running = BrowserKind.allCases.filter(\.isRunning)
        guard !running.isEmpty else {
            if !videos.isEmpty { videos = [] }
            blockedBrowsers = []
            updateArtwork()
            return
        }
        polling = true
        queue.async {
            var found: [BrowserVideo] = []
            var blocked: Set<BrowserKind> = []
            for browser in running {
                let (list, isBlocked) = Self.scan(browser)
                found += list
                if isBlocked { blocked.insert(browser) }
            }
            DispatchQueue.main.async {
                MainActor.assumeIsolated {
                    self.polling = false
                    if found != self.videos { self.videos = found }
                    if blocked != self.blockedBrowsers { self.blockedBrowsers = blocked }
                    self.updateArtwork()
                }
            }
        }
    }

    nonisolated private static func scan(_ browser: BrowserKind) -> ([BrowserVideo], Bool) {
        let js = probeJS.replacingOccurrences(of: "\\", with: "\\\\").replacingOccurrences(of: "\"", with: "\\\"")
        let isYouTube = "(u contains \"youtube.com/watch\" or u contains \"youtube.com/shorts/\" or u contains \"music.youtube.com\")"
        let source: String
        if browser == .safari {
            source = """
            tell application "Safari"
                set out to ""
                set sep to (ASCII character 31)
                repeat with w in windows
                    set i to 0
                    repeat with t in tabs of w
                        set i to i + 1
                        set u to URL of t
                        if u is not missing value and \(isYouTube) then
                            try
                                set r to do JavaScript "\(js)" in t
                            on error
                                set r to "ERR"
                            end try
                            if r is missing value then set r to ""
                            set out to out & (id of w) & sep & i & sep & u & sep & (name of t) & sep & r & linefeed
                        end if
                    end repeat
                end repeat
                return out
            end tell
            """
        } else {
            source = """
            tell application "\(browser.rawValue)"
                set out to ""
                set sep to (ASCII character 31)
                repeat with w in windows
                    repeat with t in tabs of w
                        set u to URL of t
                        if \(isYouTube) then
                            try
                                set r to execute t javascript "\(js)"
                            on error
                                set r to "ERR"
                            end try
                            if r is missing value then set r to ""
                            set out to out & (id of w) & sep & (id of t) & sep & u & sep & (title of t) & sep & r & linefeed
                        end if
                    end repeat
                end repeat
                return out
            end tell
            """
        }
        guard let output = run(source) else { return ([], false) }

        var list: [BrowserVideo] = []
        var blocked = false
        for line in output.split(separator: "\n") {
            let f = line.components(separatedBy: sep)
            guard f.count >= 5, let win = Int(f[0]), let tab = Int(f[1]) else { continue }
            let payload = f[4...].joined(separator: sep)
            var video = BrowserVideo(browser: browser, windowID: win, tabRef: tab, url: f[2],
                                     title: f[3].replacingOccurrences(of: " - YouTube", with: ""),
                                     channel: "", isPlaying: false, current: 0, duration: 0, controllable: false)
            if payload == "ERR" {
                blocked = true
            } else if let data = payload.data(using: .utf8),
                      let json = try? JSONSerialization.jsonObject(with: data) as? [String: Any] {
                video.controllable = true
                video.isPlaying = json["p"] as? Bool ?? false
                video.current = json["c"] as? Double ?? 0
                video.duration = json["d"] as? Double ?? 0
                if let t = json["t"] as? String, !t.isEmpty { video.title = t }
                video.channel = json["ch"] as? String ?? ""
            } else {
                // Page has no <video> yet (still loading); list it as controllable but idle.
                video.controllable = true
            }
            list.append(video)
        }
        return (list, blocked)
    }

    nonisolated private static func run(_ source: String) -> String? {
        var error: NSDictionary?
        let result = NSAppleScript(source: source)?.executeAndReturnError(&error)
        if let error { NSLog("Purrch browser media: \(error[NSAppleScript.errorMessage] ?? error)") }
        return result?.stringValue
    }

    // MARK: Controls

    func toggle(_ video: BrowserVideo) {
        execute(video, js: "(v=>{if(v){v.paused?v.play():v.pause()}})(document.querySelector('video'))", optimistic: { $0.isPlaying.toggle() })
    }

    func pause(_ video: BrowserVideo) {
        execute(video, js: "(v=>{if(v)v.pause()})(document.querySelector('video'))", optimistic: { $0.isPlaying = false })
    }

    func pauseAll() {
        for video in videos where video.isPlaying { pause(video) }
    }

    func seek(_ video: BrowserVideo, by seconds: Double) {
        execute(video, js: "(v=>{if(v)v.currentTime=Math.max(0,v.currentTime+(\(seconds)))})(document.querySelector('video'))",
                optimistic: { $0.current = max(0, $0.current + seconds) })
    }

    func seek(_ video: BrowserVideo, toFraction fraction: Double) {
        let target = max(0, min(1, fraction)) * video.duration
        execute(video, js: "(v=>{if(v)v.currentTime=\(target)})(document.querySelector('video'))",
                optimistic: { $0.current = target })
    }

    func next(_ video: BrowserVideo) {
        execute(video, js: "(b=>{if(b)b.click()})(document.querySelector('.ytp-next-button, ytmusic-player-bar .next-button'))")
    }

    /// Brings the video's tab and window to the front.
    func focus(_ video: BrowserVideo) {
        let source: String
        if video.browser == .safari {
            source = """
            tell application "Safari"
                set w to window id \(video.windowID)
                set current tab of w to tab \(video.tabRef) of w
                set index of w to 1
                activate
            end tell
            """
        } else {
            source = """
            tell application "\(video.browser.rawValue)"
                set w to window id \(video.windowID)
                set i to 1
                repeat with t in tabs of w
                    if id of t is \(video.tabRef) then set active tab index of w to i
                    set i to i + 1
                end repeat
                set index of w to 1
                activate
            end tell
            """
        }
        NotchState.shared.close()
        queue.async { _ = Self.run(source) }
    }

    private func execute(_ video: BrowserVideo, js: String, optimistic: ((inout BrowserVideo) -> Void)? = nil) {
        guard video.controllable else { return }
        if let optimistic, let i = videos.firstIndex(where: { $0.id == video.id }) {
            optimistic(&videos[i])
            updateArtwork()
        }
        let escaped = js.replacingOccurrences(of: "\\", with: "\\\\").replacingOccurrences(of: "\"", with: "\\\"")
        let source = video.browser == .safari
            ? "tell application \"Safari\" to do JavaScript \"\(escaped)\" in tab \(video.tabRef) of window id \(video.windowID)"
            : "tell application \"\(video.browser.rawValue)\" to execute (tab id \(video.tabRef) of window id \(video.windowID)) javascript \"\(escaped)\""
        queue.async { [weak self] in
            _ = Self.run(source)
            DispatchQueue.main.asyncAfter(deadline: .now() + 0.35) {
                MainActor.assumeIsolated { self?.poll() }
            }
        }
    }

    // MARK: Live frames

    /// Hands frames straight to a CALayer so a new frame never re-renders SwiftUI.
    let frameSink = FrameSink()
    /// A first frame has arrived for the current stream.
    @Published private(set) var hasLiveFrame = false
    @Published private(set) var liveIsPortrait = false
    /// YouTube's quality levels for the streamed video (e.g. "hd1080") and the one in use.
    @Published private(set) var youtubeQualities: [String] = []
    @Published private(set) var youtubeQuality = ""

    private var streamingID: String?
    /// False while the embedded player draws the picture; the stream then carries playback state only.
    private(set) var wantsImages = true
    /// Latest playback position of the streamed video and when it was read, for keeping a mirror in sync.
    private(set) var streamTime: Double = 0
    private(set) var streamTimeAt = Date.distantPast
    /// Players currently showing the stream.
    private var viewers = 0
    /// Bumped per stream so a superseded frame loop stops by itself.
    private var generation = 0
    private var lastStateSync = Date.distantPast
    private let frameQueue = DispatchQueue(label: "purrch.browsermedia.frames", qos: .userInteractive)
    /// Compiled scripts, touched only on `frameQueue`.
    nonisolated(unsafe) private static var frameScripts: [String: NSAppleScript] = [:]

    /// Draw the current frame into a reused canvas and return it as JPEG, plus playback and quality state.
    /// Playback and quality state only, for when the picture comes from the embedded player instead.
    nonisolated private static let stateJS = """
    (()=>{const v=document.querySelector('video');if(!v)return '';\
    const p=document.getElementById('movie_player');let q='',ql='';\
    try{q=p.getPlaybackQuality();ql=(p.getAvailableQualityLevels()||[]).join(',');}catch(e){}\
    return ['',!v.paused&&!v.ended,v.currentTime||0,isFinite(v.duration)?v.duration:0,q,ql,v.videoWidth,v.videoHeight].join('|');})()
    """

    /// Draws the current frame into a reused canvas and returns it as JPEG, plus playback and quality state.
    /// While paused it sends a frame once per stream (`gen`) instead of re-encoding the same picture, and drops
    /// black frames: Chrome releases a paused background video's picture, and a black JPEG would hide the thumbnail.
    nonisolated private static func frameJS(width: Int, jpeg: Double, gen: Int) -> String {
        """
        (()=>{const v=document.querySelector('video');if(!v||!v.videoWidth)return '';\
        const on=!v.paused&&!v.ended;\
        const p=document.getElementById('movie_player');let q='',ql='';\
        try{q=p.getPlaybackQuality();ql=(p.getAvailableQualityLevels()||[]).join(',');}catch(e){}\
        const tail=[on,v.currentTime||0,isFinite(v.duration)?v.duration:0,q,ql,v.videoWidth,v.videoHeight];\
        const key='\(gen):'+v.currentTime;\
        if(!on&&window.__pk===key)return ['',...tail].join('|');\
        const w=Math.min(\(width),v.videoWidth),h=Math.round(w*v.videoHeight/v.videoWidth);\
        let c=window.__pc;if(!c){c=window.__pc=document.createElement('canvas');}\
        if(c.width!==w||c.height!==h){c.width=w;c.height=h;window.__px=c.getContext('2d',{alpha:false});}\
        (window.__px||c.getContext('2d')).drawImage(v,0,0,w,h);\
        let img=c.toDataURL('image/jpeg',\(String(format: "%.2f", jpeg)));\
        if(!on){try{let s=window.__ps;if(!s){s=window.__ps=document.createElement('canvas');s.width=8;s.height=8;}\
        const x=s.getContext('2d');x.drawImage(v,0,0,8,8);const d=x.getImageData(0,0,8,8).data;let m=0;\
        for(let i=0;i<d.length;i+=4)m=Math.max(m,d[i]+d[i+1]+d[i+2]);if(m<24)img='';}catch(e){}\
        if(img)window.__pk=key;}\
        return [img,...tail].join('|');})()
        """
    }

    /// Start mirroring `video` into the frame sink, paced by the preview quality setting.
    /// A player came on screen. Streaming continues while at least one player is showing, so handing
    /// the video from one player to another (e.g. into theater mode) never drops the stream.
    func startFrames(for video: BrowserVideo) {
        viewers += 1
        switchFrames(to: video)
    }

    /// The player on screen now shows a different video.
    func switchFrames(to video: BrowserVideo) {
        guard video.controllable, streamingID != video.id else { return }
        hasLiveFrame = false
        frameSink.clear()
        youtubeQualities = []
        youtubeQuality = ""
        streamingID = video.id
        generation += 1
        pumpFrame(video, generation)
    }

    /// Where the streamed video should be now: the last reading plus the time since, while playing.
    func estimatedTime(isPlaying: Bool) -> Double {
        guard streamTimeAt != .distantPast else { return streamTime }
        return streamTime + (isPlaying ? Date().timeIntervalSince(streamTimeAt) : 0)
    }

    /// Switch the running stream between frames and state-only.
    func setStreamImages(_ images: Bool) {
        guard images != wantsImages else { return }
        wantsImages = images
        if images { hasLiveFrame = false }
    }

    func stopFrames() {
        viewers = max(0, viewers - 1)
        if viewers == 0 { streamingID = nil }
    }

    func setYouTubeQuality(_ video: BrowserVideo, level: String) {
        youtubeQuality = level
        execute(video, js: "(p=>{if(p){try{p.setPlaybackQualityRange('\(level)','\(level)')}catch(e){}try{p.setPlaybackQuality('\(level)')}catch(e){}}})(document.getElementById('movie_player'))")
    }

    private func pumpFrame(_ video: BrowserVideo, _ gen: Int) {
        guard streamingID == video.id, gen == generation else { return }
        let quality = AppSettings.shared.previewQuality
        let images = wantsImages
        let js = (images ? Self.frameJS(width: quality.width, jpeg: quality.jpeg, gen: gen) : Self.stateJS)
            .replacingOccurrences(of: "\\", with: "\\\\").replacingOccurrences(of: "\"", with: "\\\"")
        let source = video.browser == .safari
            ? "tell application \"Safari\" to do JavaScript \"\(js)\" in tab \(video.tabRef) of window id \(video.windowID)"
            : "tell application \"\(video.browser.rawValue)\" to execute (tab id \(video.tabRef) of window id \(video.windowID)) javascript \"\(js)\""
        let key = video.id
        let scriptKey = "\(key)-\(quality.rawValue)-\(gen)-\(images)"
        let interval = 1.0 / quality.fps

        frameQueue.async { [weak self] in
            let started = Date()
            let script = Self.frameScripts[scriptKey] ?? {
                // A new stream or quality: earlier compiled scripts are dead.
                Self.frameScripts.removeAll()
                let s = NSAppleScript(source: source)
                s?.compileAndReturnError(nil)
                if let s { Self.frameScripts[scriptKey] = s }
                return s
            }()
            var error: NSDictionary?
            let raw = script?.executeAndReturnError(&error).stringValue ?? ""
            let parts = raw.split(separator: "|", omittingEmptySubsequences: false)

            // Decode here, off the main thread, so drawing the layer is just a pointer swap.
            var image: CGImage?
            if let first = parts.first, let comma = first.firstIndex(of: ","),
               let data = Data(base64Encoded: String(first[first.index(after: comma)...])),
               let src = CGImageSourceCreateWithData(data as CFData, nil) {
                image = CGImageSourceCreateImageAtIndex(src, 0, [kCGImageSourceShouldCacheImmediately: true] as CFDictionary)
            }
            let playing = parts.count > 1 ? parts[1] == "true" : nil
            let current = parts.count > 2 ? Double(parts[2]) : nil
            let duration = parts.count > 3 ? Double(parts[3]) : nil
            let level = parts.count > 4 ? String(parts[4]) : ""
            let levels = parts.count > 5 ? parts[5].split(separator: ",").map(String.init) : []
            let portrait = parts.count > 7 ? (Double(parts[7]) ?? 0) > (Double(parts[6]) ?? 1) : false
            // A paused picture doesn't change; checking twice a second is enough.
            let wait = !images ? 0.25 : (playing == false ? 0.5 : max(0.01, interval - Date().timeIntervalSince(started)))

            DispatchQueue.main.async {
                MainActor.assumeIsolated {
                    guard let self, self.streamingID == key, gen == self.generation else { return }
                    if let current {
                        self.streamTime = current
                        self.streamTimeAt = Date()
                    }
                    if let image {
                        self.frameSink.show(image)
                        if !self.hasLiveFrame { self.hasLiveFrame = true }
                    }
                    // Playback state for the controls: twice a second is plenty.
                    if Date().timeIntervalSince(self.lastStateSync) > 0.5 {
                        self.lastStateSync = Date()
                        if portrait != self.liveIsPortrait { self.liveIsPortrait = portrait }
                        if !levels.isEmpty, levels != self.youtubeQualities { self.youtubeQualities = levels }
                        if !level.isEmpty, level != self.youtubeQuality { self.youtubeQuality = level }
                        if let i = self.videos.firstIndex(where: { $0.id == key }) {
                            var v = self.videos[i]
                            if let playing { v.isPlaying = playing }
                            if let current { v.current = current }
                            if let duration, duration > 0 { v.duration = duration }
                            if v != self.videos[i] { self.videos[i] = v }
                        }
                    }
                    // Back off when the browser refuses (tab closed, permission revoked).
                    let delay = error == nil ? wait : 1.0
                    DispatchQueue.main.asyncAfter(deadline: .now() + delay) {
                        MainActor.assumeIsolated {
                            guard let current = self.videos.first(where: { $0.id == key }) else {
                                self.streamingID = nil
                                return
                            }
                            self.pumpFrame(current, gen)
                        }
                    }
                }
            }
        }
    }

    // MARK: One-click enable

    @Published private(set) var enableStatus: [BrowserKind: BrowserEnableStatus] = [:]
    private var trustTimer: Timer?

    /// Ticks "Allow JavaScript from Apple Events" in the browser for the user. Needs Accessibility once;
    /// if it's missing, asks macOS for it and carries on by itself as soon as it is granted.
    func enableControl(for browser: BrowserKind) {
        guard AXIsProcessTrusted() else {
            let options = [kAXTrustedCheckOptionPrompt.takeUnretainedValue() as String: true] as CFDictionary
            _ = AXIsProcessTrustedWithOptions(options)
            enableStatus[browser] = .needsAccessibility
            waitForAccessibility(then: browser)
            return
        }
        enableStatus[browser] = .working
        let source = browser.enableScript
        queue.async { [weak self] in
            var error: NSDictionary?
            NSAppleScript(source: source)?.executeAndReturnError(&error)
            let message = (error?[NSAppleScript.errorMessage] as? String)
            DispatchQueue.main.async {
                MainActor.assumeIsolated {
                    guard let self else { return }
                    if let message {
                        self.enableStatus[browser] = .failed(message)
                    } else {
                        self.enableStatus[browser] = .idle
                        // Bring the notch back with the controls now working.
                        NotchState.shared.present(.nowPlaying)
                        DispatchQueue.main.asyncAfter(deadline: .now() + 0.5) {
                            MainActor.assumeIsolated { self.poll() }
                        }
                    }
                }
            }
        }
    }

    func openAccessibilitySettings() {
        if let url = URL(string: "x-apple.systempreferences:com.apple.preference.security?Privacy_Accessibility") {
            NSWorkspace.shared.open(url)
        }
    }

    private func waitForAccessibility(then browser: BrowserKind) {
        trustTimer?.invalidate()
        let started = Date()
        trustTimer = Timer.scheduledTimer(withTimeInterval: 1, repeats: true) { [weak self] timer in
            MainActor.assumeIsolated {
                guard let self else { timer.invalidate(); return }
                if AXIsProcessTrusted() {
                    timer.invalidate()
                    self.enableControl(for: browser)
                } else if Date().timeIntervalSince(started) > 180 {
                    timer.invalidate()
                    if self.enableStatus[browser] == .needsAccessibility { self.enableStatus[browser] = .idle }
                }
            }
        }
    }

    // MARK: Artwork

    private func updateArtwork() {
        let video = playingVideo
        guard video?.videoID != artworkVideoID else { return }
        artworkVideoID = video?.videoID
        guard let url = video?.thumbnailURL else {
            playingArtwork = nil
            return
        }
        let wanted = artworkVideoID
        URLSession.shared.dataTask(with: url) { data, _, _ in
            let image = data.flatMap(NSImage.init(data:))
            DispatchQueue.main.async {
                MainActor.assumeIsolated {
                    if self.artworkVideoID == wanted { self.playingArtwork = image }
                }
            }
        }.resume()
    }
}

/// Receives frames outside SwiftUI and puts them straight on a layer.
@MainActor
final class FrameSink {
    private weak var layer: CALayer?
    private var last: CGImage?

    func attach(_ layer: CALayer) {
        self.layer = layer
        layer.contents = last
    }

    func show(_ image: CGImage) {
        last = image
        CATransaction.begin()
        CATransaction.setDisableActions(true)
        layer?.contents = image
        CATransaction.commit()
    }

    func clear() {
        last = nil
        layer?.contents = nil
    }
}

/// How the hover preview trades sharpness for smoothness.
enum PreviewQuality: String, CaseIterable, Identifiable {
    case smooth, balanced, sharp
    var id: String { rawValue }
    var title: String {
        switch self {
        case .smooth: "Smooth"
        case .balanced: "Balanced"
        case .sharp: "Sharp"
        }
    }
    var width: Int {
        switch self {
        case .smooth: 480
        case .balanced: 720
        case .sharp: 1080
        }
    }
    var jpeg: Double {
        switch self {
        case .smooth: 0.6
        case .balanced: 0.72
        case .sharp: 0.85
        }
    }
    var fps: Double {
        switch self {
        case .smooth: 24
        case .balanced: 18
        case .sharp: 12
        }
    }
}

enum YouTubeQualityLabel {
    static func text(_ level: String) -> String {
        switch level {
        case "highres": "4K+"
        case "hd2160": "2160p"
        case "hd1440": "1440p"
        case "hd1080": "1080p"
        case "hd720": "720p"
        case "large": "480p"
        case "medium": "360p"
        case "small": "240p"
        case "tiny": "144p"
        case "auto": "Auto"
        default: level
        }
    }
}
