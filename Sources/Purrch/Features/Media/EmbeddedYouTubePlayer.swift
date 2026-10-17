import SwiftUI
import WebKit
import Combine

/// Plays the same YouTube video as the browser tab, muted, inside the notch, and keeps it in step with
/// the tab (position and play / pause). This gives smooth, full-frame-rate video; sound stays in the
/// browser. The player itself lives in `EmbeddedPlayerHost` and outlives this view, so reopening the
/// notch shows the video at once instead of loading YouTube again.
struct EmbeddedYouTubePlayer: NSViewRepresentable {
    let videoID: String

    func makeNSView(context: Context) -> NSView {
        let container = PassthroughView()
        let host = EmbeddedPlayerHost.shared
        host.prepare(videoID: videoID)
        host.attach(to: container)
        return container
    }

    func updateNSView(_ container: NSView, context: Context) {
        let host = EmbeddedPlayerHost.shared
        if host.videoID != videoID {
            host.prepare(videoID: videoID)
            host.attach(to: container)
        }
    }

    static func dismantleNSView(_ container: NSView, coordinator: ()) {
        EmbeddedPlayerHost.shared.detach(from: container)
    }

    /// Draws only. Clicks go to the SwiftUI controls on top.
    final class PassthroughView: NSView {
        override func hitTest(_ point: NSPoint) -> NSView? { nil }
    }
}

/// The one embedded YouTube player. It loads ahead of time when a browser video is playing in video mode,
/// keeps playing (muted) for a while after the notch closes, and moves into whichever view shows it next.
@MainActor
final class EmbeddedPlayerHost: NSObject, ObservableObject, WKScriptMessageHandler {
    static let shared = EmbeddedPlayerHost()

    /// The loaded video has frames on screen.
    @Published private(set) var readyVideoID: String?
    /// The video can't be embedded (the owner disabled it, or the player never loaded).
    @Published private(set) var failedVideoID: String?

    private(set) var videoID: String?
    private var webView: PassthroughWebView?
    private weak var container: NSView?
    /// Invisible view in the always-on-screen notch window that holds the player while nothing shows it.
    weak var parkingView: NSView?
    private var detachedAt: Date? = Date()
    private var timer: Timer?
    private var loadDeadline: Date?
    private var lastSeek = Date.distantPast
    private var prewarm: AnyCancellable?

    /// After this long out of sight the hidden player pauses instead of following the tab.
    private static let keepWarm: TimeInterval = 60
    /// Size used while parked, so YouTube still picks a sharp quality.
    private static let hiddenSize = NSRect(x: 0, y: 0, width: 1280, height: 720)

    /// Load the playing browser video in advance, so the first open is instant too.
    func startPrewarming() {
        prewarm = BrowserMediaService.shared.$videos
            .receive(on: RunLoop.main)
            .sink { [weak self] videos in
                MainActor.assumeIsolated {
                    guard let self, Self.wantsVideo,
                          let video = videos.first(where: { $0.isPlaying && $0.controllable }),
                          let id = video.videoID, id != self.videoID else { return }
                    self.prepare(videoID: id, start: video.current)
                }
            }
    }

    private static var wantsVideo: Bool {
        UserDefaults.standard.string(forKey: "nowPlaying.mode") == MediaViewMode.video.rawValue
            || AppSettings.shared.videoPeekOnHover
    }

    func prepare(videoID: String, start: Double? = nil) {
        guard videoID != self.videoID else { return }
        self.videoID = videoID
        if container == nil { detachedAt = Date() }
        readyVideoID = nil
        failedVideoID = nil
        loadDeadline = Date().addingTimeInterval(15)
        lastSeek = .distantPast
        let at = start ?? BrowserMediaService.shared.videos.first { $0.videoID == videoID }?.current ?? 0
        let view = webView ?? makeWebView()
        view.loadHTMLString(Self.page(videoID: videoID, start: max(0, Int(at))), baseURL: URL(string: "https://purrch.app"))
        startTimer()
    }

    func attach(to container: NSView) {
        guard let view = webView else { return }
        self.container = container
        detachedAt = nil
        view.removeFromSuperview()
        view.frame = container.bounds
        view.autoresizingMask = [.width, .height]
        container.addSubview(view)
        startTimer()
        // Let the frame stream report a fresh position first, then catch up straight away.
        lastSeek = .distantPast
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.4) { [weak self] in self?.sync() }
    }

    func detach(from container: NSView) {
        // A newer view may already have taken the player (e.g. while switching into theater).
        guard self.container === container, let view = webView else { return }
        self.container = nil
        detachedAt = Date()
        park(view)
    }

    /// Off-window web views are treated as hidden and stop playing, so keep it in the notch window.
    private func park(_ view: NSView) {
        view.removeFromSuperview()
        view.autoresizingMask = []
        view.frame = Self.hiddenSize
        parkingView?.addSubview(view)
    }

    private func makeWebView() -> PassthroughWebView {
        let config = WKWebViewConfiguration()
        config.mediaTypesRequiringUserActionForPlayback = []
        config.userContentController.add(self, name: "purrch")
        let view = PassthroughWebView(frame: Self.hiddenSize, configuration: config)
        view.setValue(false, forKey: "drawsBackground")
        webView = view
        park(view)
        return view
    }

    private func startTimer() {
        guard timer == nil else { return }
        timer = Timer.scheduledTimer(withTimeInterval: 0.5, repeats: true) { [weak self] _ in
            MainActor.assumeIsolated { self?.sync() }
        }
    }

    private func stopTimer() {
        timer?.invalidate()
        timer = nil
    }

    /// Nudge the embedded player toward the tab: same play state, and close to the same position.
    /// Small drift is closed by playing slightly faster or slower; only a large gap seeks, and rarely,
    /// because every seek re-buffers and would show up as a stutter.
    private func sync() {
        guard let videoID, failedVideoID != videoID else { return }
        if readyVideoID != videoID {
            if let deadline = loadDeadline, Date() > deadline { fail() }
            // YouTube sometimes drops the autoplay request and sits unstarted; ask again.
            webView?.evaluateJavaScript("window.purrchKick && purrchKick()")
            return
        }
        let service = BrowserMediaService.shared
        let video = service.videos.first { $0.videoID == videoID }
        let shown = container != nil
        if let detachedAt, Date().timeIntervalSince(detachedAt) > Self.keepWarm {
            // Out of sight for a while: pause and rest until a view shows it again.
            webView?.evaluateJavaScript("window.purrchSync && purrchSync(0, false, false, true)")
            stopTimer()
            return
        }
        let playing = video?.isPlaying ?? false
        // Positions are only fresh while a view streams this video; hidden, follow play / pause only.
        let isLive = (video?.duration ?? 0) == 0 || !shown
        let target = service.estimatedTime(isPlaying: playing)
        let canSeek = Date().timeIntervalSince(lastSeek) > 8
        webView?.evaluateJavaScript("window.purrchSync && purrchSync(\(target), \(playing), \(canSeek), \(isLive))") { [weak self] result, _ in
            if (result as? String) == "seek" { self?.lastSeek = Date() }
        }
    }

    private func fail() {
        guard let videoID, failedVideoID != videoID else { return }
        failedVideoID = videoID
    }

    func userContentController(_ controller: WKUserContentController, didReceive message: WKScriptMessage) {
        guard let body = message.body as? String else { return }
        if body.hasPrefix("ready:") {
            let id = String(body.dropFirst(6))
            if id == videoID { readyVideoID = id }
        } else if body.hasPrefix("error") {
            // 2, 5, 100, 101, 150, 153: bad id, HTML5 error, removed, embedding disabled, config error.
            fail()
        }
    }

    /// Draws only. Clicks go to the SwiftUI controls on top.
    final class PassthroughWebView: WKWebView {
        override func hitTest(_ point: NSPoint) -> NSView? { nil }
        override var acceptsFirstResponder: Bool { false }
        // No hover tracking either, so YouTube never shows its own hover chrome.
        override func updateTrackingAreas() {
            trackingAreas.forEach(removeTrackingArea)
        }
    }

    private static func page(videoID: String, start: Int) -> String {
        """
        <!doctype html><html><head><meta name="viewport" content="width=device-width">
        <style>html,body{margin:0;height:100%;background:#000;overflow:hidden}
        /* The player is taller than the view: YouTube's title bar and end-screen strip land in the
           letterbox above and below the picture, which is cropped away. */
        #p,iframe{position:fixed;left:0;top:-70px;width:100%;height:calc(100% + 140px);border:0;pointer-events:none}</style>
        </head><body><div id="p"></div>
        <script>
        function post(m){window.webkit.messageHandlers.purrch.postMessage(m)}
        var player;
        function onYouTubeIframeAPIReady(){
          player=new YT.Player('p',{videoId:'\(videoID)',
            playerVars:{autoplay:1,mute:1,controls:0,disablekb:1,fs:0,rel:0,iv_load_policy:3,playsinline:1,cc_load_policy:0,
                        modestbranding:1,start:\(start),origin:'https://purrch.app'},
            events:{onReady:function(e){e.target.mute();e.target.playVideo()},
                    onStateChange:function(e){if(e.data===1||e.data===2||e.data===0)reveal()},
                    onError:function(e){post('error:'+e.data)}}});
        }
        // "Ready" only once real frames are on screen; until then the caller keeps showing the
        // thumbnail instead of YouTube's black loading screen.
        var revealed=false;
        function reveal(){if(revealed)return;revealed=true;setTimeout(function(){post('ready:\(videoID)')},250)}
        function rate(r){var a=player.getAvailablePlaybackRates?player.getAvailablePlaybackRates():[1];
          if(a.indexOf(r)<0)r=1;if(player.getPlaybackRate()!==r)player.setPlaybackRate(r)}
        window.purrchKick=function(){
          if(!player||!player.getPlayerState)return;
          var s=player.getPlayerState();if(s===-1||s===5){player.mute();player.playVideo()}
        };
        window.purrchSync=function(t,play,seek,live){
          if(!player||!player.getPlayerState)return '';
          var s=player.getPlayerState();
          if(!play){rate(1);if(s===1)player.pauseVideo();return ''}
          if(s===3)return '';
          if(s!==1){player.playVideo();return ''}
          if(live){rate(1);return ''}
          var d=t-player.getCurrentTime(),a=Math.abs(d);
          // Seek a little ahead: the browser keeps playing while this player buffers.
          if(a>2.5&&a<600&&seek){rate(1);player.seekTo(t+0.6,true);return 'seek'}
          rate(a<0.3?1:(d>0?1.25:0.75));return '';
        };
        </script>
        <script src="https://www.youtube.com/iframe_api"></script>
        </body></html>
        """
    }
}

