import SwiftUI

/// Motion curves shared by the whole notch.
enum Motion {
    /// Opening: quick start with a small overshoot, like the panel is stretching out.
    static let open = Animation.spring(response: 0.46, dampingFraction: 0.74)
    /// Closing: fast and settled, no bounce.
    static let close = Animation.spring(response: 0.3, dampingFraction: 0.95)
    /// Expo-out for fades and slides: cubic-bezier(.16, 1, .3, 1).
    static func expoOut(_ duration: Double = 0.5) -> Animation { .timingCurve(0.16, 1, 0.3, 1, duration: duration) }
    /// Small springy pop for selections.
    static let pop = Animation.spring(response: 0.34, dampingFraction: 0.68)
    /// Hover anticipation on the closed notch.
    static let peek = Animation.spring(response: 0.3, dampingFraction: 0.62)
}

/// Shared UI state for the notch window.
@MainActor
final class NotchState: ObservableObject {
    static let shared = NotchState()

    @Published var isExpanded = false
    @Published var isPinned = false
    /// Opened from the menu bar or a script: stay open until the pointer has visited the panel once.
    var holdUntilHovered = false
    @Published var showSettings = false
    @Published var isHidden = false
    /// Pointer is resting on the closed notch, just before it opens.
    @Published var isPeeking = false
    /// Open as a small live-video window (a browser video is playing) instead of the full panel.
    @Published var isVideoPeek = false
    /// A browser video filling the whole expanded panel ("theater"), by video id.
    @Published var theaterVideoID: String?
    @Published var selected: ModuleID = AppSettings.shared.lastModule {
        didSet { AppSettings.shared.lastModule = selected }
    }
    /// +1 when the last tab change moved right, -1 when it moved left. Drives the slide direction.
    @Published var switchDirection: CGFloat = 1

    /// Size of the physical notch (or the simulated one on screens without a notch).
    @Published var notchSize = CGSize(width: 200, height: 32)
    /// True when the screen has a real camera housing.
    @Published var hasPhysicalNotch = false

    static let expandedSize = CGSize(width: 780, height: 420)
    static let videoPeekSize = CGSize(width: 540, height: 352)

    /// Extra width on each side of the notch used by the collapsed live activity.
    @Published var liveActivityWing: CGFloat = 0

    var currentSize: CGSize {
        if isExpanded { return isVideoPeek ? Self.videoPeekSize : Self.expandedSize }
        let peek: CGFloat = isPeeking ? 1 : 0
        return CGSize(width: notchSize.width + liveActivityWing * 2 + peek * 18,
                      height: notchSize.height + peek * 5)
    }

    func open(_ module: ModuleID? = nil) {
        if let module { selected = module }
        isPeeking = false
        withAnimation(Motion.open) {
            isVideoPeek = false
            isExpanded = true
        }
    }

    /// Hover while a browser video plays: show just that video, live.
    func openVideoPeek() {
        isPeeking = false
        withAnimation(Motion.open) {
            isVideoPeek = true
            isExpanded = true
        }
    }

    /// From the video window to the full panel, keeping it open.
    func expandToFullPanel(_ module: ModuleID? = nil) {
        if let module { selected = module }
        withAnimation(Motion.open) { isVideoPeek = false }
    }

    /// Opens without hover, e.g. from the menu bar; closes normally once the pointer has come and gone.
    func present(_ module: ModuleID? = nil) {
        holdUntilHovered = true
        open(module)
    }

    /// Pinned, or watching a video in theater: hover-out and outside clicks leave the panel open.
    var staysOpen: Bool { isPinned || theaterVideoID != nil }

    func close() {
        guard !staysOpen else { return }
        holdUntilHovered = false
        withAnimation(Motion.close) {
            isExpanded = false
            isVideoPeek = false
            showSettings = false
            theaterVideoID = nil
        }
    }

    /// Closes even when pinned or in theater, for explicit requests (close command, screenshot, full screen).
    func forceClose() {
        isPinned = false
        theaterVideoID = nil
        close()
    }

    func select(_ module: ModuleID) {
        let modules = AppSettings.shared.enabledModules
        let from = modules.firstIndex(of: selected) ?? 0
        let to = modules.firstIndex(of: module) ?? 0
        switchDirection = to >= from ? 1 : -1
        withAnimation(Motion.expoOut(0.45)) {
            showSettings = false
            selected = module
        }
    }

    func enterTheater(_ videoID: String) {
        withAnimation(Motion.open) { theaterVideoID = videoID }
    }

    func exitTheater() {
        withAnimation(Motion.expoOut(0.4)) { theaterVideoID = nil }
    }

    func setPeeking(_ peeking: Bool) {
        guard peeking != isPeeking else { return }
        withAnimation(Motion.peek) { isPeeking = peeking }
    }
}
