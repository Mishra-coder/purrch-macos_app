import SwiftUI
import WebKit

/// Plays the same YouTube video as the browser tab, muted, inside the notch, and keeps it in step with
/// the tab (position and play / pause). This gives smooth, full-frame-rate video; sound stays in the
/// browser. Calls `onFailure` when the video can't be embedded (the owner disabled it, or the player
/// never loads), so the caller can fall back to mirroring frames.
struct EmbeddedYouTubePlayer: NSViewRepresentable {
    let videoID: String
    let isPlaying: Bool
    /// Live streams have no stable position to sync to; only play / pause is mirrored.
    let isLive: Bool
    let onReady: () -> Void
    let onFailure: () -> Void

    func makeCoordinator() -> Coordinator { Coordinator(onReady: onReady, onFailure: onFailure) }

    func makeNSView(context: Context) -> WKWebView {
        let config = WKWebViewConfiguration()
        config.mediaTypesRequiringUserActionForPlayback = []
        config.userContentController.add(context.coordinator, name: "purrch")
        let view = PassthroughWebView(frame: .zero, configuration: config)
        view.setValue(false, forKey: "drawsBackground")
        context.coordinator.webView = view
        context.coordinator.load(videoID: videoID, start: BrowserMediaService.shared.estimatedTime(isPlaying: isPlaying))
        context.coordinator.startSync(isPlaying: isPlaying, isLive: isLive)
        return view
    }

    func updateNSView(_ view: WKWebView, context: Context) {
        let c = context.coordinator
        c.onReady = onReady
        c.onFailure = onFailure
        if c.videoID != videoID {
            c.load(videoID: videoID, start: BrowserMediaService.shared.estimatedTime(isPlaying: isPlaying))
        }
        c.isPlaying = isPlaying
        c.isLive = isLive
    }

    static func dismantleNSView(_ view: WKWebView, coordinator: Coordinator) {
        coordinator.stop()
        view.configuration.userContentController.removeScriptMessageHandler(forName: "purrch")
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

    @MainActor
    final class Coordinator: NSObject, WKScriptMessageHandler {
        var onReady: () -> Void
        var onFailure: () -> Void
        weak var webView: WKWebView?
        var videoID = ""
        var isPlaying = true
        var isLive = false
        private var timer: Timer?
        private var ready = false
        private var failed = false
        private var loadDeadline: Date?
        private var lastSeek = Date.distantPast

        init(onReady: @escaping () -> Void, onFailure: @escaping () -> Void) {
            self.onReady = onReady
            self.onFailure = onFailure
        }

        func load(videoID: String, start: Double) {
            self.videoID = videoID
            ready = false
            failed = false
            loadDeadline = Date().addingTimeInterval(8)
            webView?.loadHTMLString(Self.page(videoID: videoID, start: max(0, Int(start))),
                                    baseURL: URL(string: "https://purrch.app"))
        }

        func startSync(isPlaying: Bool, isLive: Bool) {
            self.isPlaying = isPlaying
            self.isLive = isLive
            timer = Timer.scheduledTimer(withTimeInterval: 0.5, repeats: true) { [weak self] _ in
                MainActor.assumeIsolated { self?.sync() }
            }
        }

        func stop() {
            timer?.invalidate()
            timer = nil
        }

        /// Nudge the embedded player toward the tab: same play state, and the same position within ~1.2 s.
        private func sync() {
            guard !failed else { return }
            if !ready {
                if let deadline = loadDeadline, Date() > deadline { fail() }
                return
            }
            let service = BrowserMediaService.shared
            let target = service.estimatedTime(isPlaying: isPlaying)
            let canSeek = !isLive && Date().timeIntervalSince(lastSeek) > 3
            if canSeek { lastSeek = Date() }
            webView?.evaluateJavaScript("window.purrchSync && purrchSync(\(target), \(isPlaying), \(canSeek))")
        }

        private func fail() {
            guard !failed else { return }
            failed = true
            stop()
            onFailure()
        }

        func userContentController(_ controller: WKUserContentController, didReceive message: WKScriptMessage) {
            guard let body = message.body as? String else { return }
            if body == "ready" {
                ready = true
                onReady()
            } else if body.hasPrefix("error") {
                // 2, 5, 100, 101, 150, 153: bad id, HTML5 error, removed, embedding disabled, config error.
                fail()
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
            function reveal(){if(revealed)return;revealed=true;setTimeout(function(){post('ready')},250)}
            window.purrchSync=function(t,play,seek){
              if(!player||!player.getPlayerState)return;
              var s=player.getPlayerState();
              var d=Math.abs(player.getCurrentTime()-t);if(seek&&d>1.2&&d<600)player.seekTo(t,true);
              if(play&&s!==1&&s!==3)player.playVideo();
              if(!play&&s===1)player.pauseVideo();
            };
            </script>
            <script src="https://www.youtube.com/iframe_api"></script>
            </body></html>
            """
        }
    }
}
