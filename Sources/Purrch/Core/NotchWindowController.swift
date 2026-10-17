import AppKit
import Combine
import SwiftUI

/// Borderless panel that can still take keyboard focus for text fields.
final class NotchPanel: NSPanel {
    override var canBecomeKey: Bool { true }
    override var canBecomeMain: Bool { false }
}

/// Owns the notch panel, positions it over the camera housing and decides
/// when to expand or collapse based on the pointer.
@MainActor
final class NotchWindowController {
    private let panel: NotchPanel
    private let state = NotchState.shared
    private let settings = AppSettings.shared
    private var pollTimer: Timer?
    private var fullscreenTimer: Timer?
    private var hoverStart: Date?
    private var leaveStart: Date?
    private var dragChangeCount = NSPasteboard(name: .drag).changeCount
    private var monitors: [Any] = []
    private var cancellables = Set<AnyCancellable>()

    /// Room around the visible shape for its shadow.
    private static let panelSize = CGSize(width: NotchState.expandedSize.width + 60,
                                          height: NotchState.expandedSize.height + 40)

    init() {
        panel = NotchPanel(
            contentRect: NSRect(origin: .zero, size: Self.panelSize),
            styleMask: [.borderless, .nonactivatingPanel],
            backing: .buffered,
            defer: false
        )
        panel.isOpaque = false
        panel.backgroundColor = .clear
        panel.hasShadow = false
        panel.level = .statusBar + 1
        panel.collectionBehavior = [.canJoinAllSpaces, .stationary, .fullScreenAuxiliary, .ignoresCycle]
        panel.isMovable = false
        panel.isReleasedWhenClosed = false
        panel.hidesOnDeactivate = false
        panel.becomesKeyOnlyIfNeeded = false
        panel.ignoresMouseEvents = true
        panel.acceptsMouseMovedEvents = true

        let root = RootView()
            .environmentObject(state)
            .environmentObject(settings)
        let host = NSHostingView(rootView: root)
        host.sizingOptions = []
        panel.contentView = host

        layout()
        panel.orderFrontRegardless()

        NotificationCenter.default.addObserver(
            forName: NSApplication.didChangeScreenParametersNotification, object: nil, queue: .main
        ) { [weak self] _ in
            MainActor.assumeIsolated { self?.layout() }
        }

        state.$isExpanded
            .sink { [weak self] expanded in
                guard let self else { return }
                panel.ignoresMouseEvents = !expanded
                if expanded {
                    panel.makeKey()
                } else {
                    panel.resignKey()
                }
            }
            .store(in: &cancellables)

        startTracking()
    }

    /// The screen that owns the notch: the built-in display when present, else the main one.
    private var targetScreen: NSScreen {
        NSScreen.screens.first { $0.safeAreaInsets.top > 0 } ?? NSScreen.main ?? NSScreen.screens[0]
    }

    private func layout() {
        let screen = targetScreen
        let frame = screen.frame
        if let left = screen.auxiliaryTopLeftArea, let right = screen.auxiliaryTopRightArea,
           screen.safeAreaInsets.top > 0 {
            let width = frame.width - left.width - right.width
            state.notchSize = CGSize(width: width, height: screen.safeAreaInsets.top)
            state.hasPhysicalNotch = true
        } else {
            let menuBar = frame.maxY - screen.visibleFrame.maxY
            state.notchSize = CGSize(width: 190, height: max(24, min(menuBar, 32)))
            state.hasPhysicalNotch = false
        }
        let origin = NSPoint(x: frame.midX - Self.panelSize.width / 2, y: frame.maxY - Self.panelSize.height)
        panel.setFrame(NSRect(origin: origin, size: Self.panelSize), display: true)
    }

    /// Screen rect of the visible notch shape right now.
    private func shapeRect(padding: CGFloat = 0) -> NSRect {
        let frame = targetScreen.frame
        let size = state.currentSize
        return NSRect(x: frame.midX - size.width / 2 - padding,
                      y: frame.maxY - size.height - padding,
                      width: size.width + padding * 2,
                      height: size.height + padding + 1)
    }

    private func startTracking() {
        pollTimer = Timer.scheduledTimer(withTimeInterval: 1.0 / 30.0, repeats: true) { [weak self] _ in
            MainActor.assumeIsolated { self?.tick() }
        }
        fullscreenTimer = Timer.scheduledTimer(withTimeInterval: 1.0, repeats: true) { [weak self] _ in
            MainActor.assumeIsolated { self?.checkFullscreen() }
        }

        let clickHandler: (NSEvent) -> Void = { [weak self] _ in
            MainActor.assumeIsolated { self?.handleClick() }
        }
        if let m = NSEvent.addGlobalMonitorForEvents(matching: .leftMouseDown, handler: clickHandler) {
            monitors.append(m)
        }
        if let m = NSEvent.addLocalMonitorForEvents(matching: .keyDown, handler: { [weak self] event in
            // Escape closes the panel.
            if event.keyCode == 53, let self {
                MainActor.assumeIsolated {
                    if self.state.theaterVideoID != nil {
                        self.state.exitTheater()
                    } else if self.state.isExpanded {
                        self.state.isPinned = false
                        self.state.close()
                    }
                }
                return nil
            }
            return event
        }) {
            monitors.append(m)
        }
    }

    private func handleClick() {
        let mouse = NSEvent.mouseLocation
        if !state.isExpanded {
            if shapeRect(padding: 4).contains(mouse), !state.isHidden { state.open() }
        } else if !shapeRect().contains(mouse) {
            // Click outside the panel (in another app) collapses it.
            state.close()
        }
    }

    private func tick() {
        guard !state.isHidden else { state.setPeeking(false); return }
        let mouse = NSEvent.mouseLocation
        let buttonsDown = NSEvent.pressedMouseButtons != 0

        if !state.isExpanded {
            leaveStart = nil
            // Dragging a file onto the notch opens the shelf.
            let dragCount = NSPasteboard(name: .drag).changeCount
            if buttonsDown, dragCount != dragChangeCount, shapeRect(padding: 14).contains(mouse) {
                dragChangeCount = dragCount
                state.open(.shelf)
                return
            }
            if !buttonsDown { dragChangeCount = dragCount }

            // The notch swells a little under the pointer before it opens (or to invite a click).
            let over = !buttonsDown && shapeRect(padding: 6).contains(mouse)
            state.setPeeking(over)

            guard settings.openOnHover, over else {
                hoverStart = nil
                return
            }
            if let start = hoverStart {
                if Date().timeIntervalSince(start) > 0.16 {
                    hoverStart = nil
                    if settings.videoPeekOnHover, BrowserMediaService.shared.playingVideo?.controllable == true {
                        state.openVideoPeek()
                    } else {
                        state.open()
                    }
                }
            } else {
                hoverStart = Date()
            }
        } else {
            hoverStart = nil
            if shapeRect(padding: 18).contains(mouse) { state.holdUntilHovered = false }
            if state.isPinned || state.holdUntilHovered || buttonsDown || shapeRect(padding: 18).contains(mouse) {
                leaveStart = nil
                return
            }
            guard settings.openOnHover else { return }
            if let start = leaveStart {
                if Date().timeIntervalSince(start) > 0.35 { leaveStart = nil; state.close() }
            } else {
                leaveStart = Date()
            }
        }
    }

    /// Hide while another app covers the notch's screen in full screen.
    private func checkFullscreen() {
        guard settings.hideInFullscreen else {
            if state.isHidden { setHidden(false) }
            return
        }
        let screen = targetScreen
        let ownPID = ProcessInfo.processInfo.processIdentifier
        let frontPID = NSWorkspace.shared.frontmostApplication?.processIdentifier
        var covered = false
        if let list = CGWindowListCopyWindowInfo([.optionOnScreenOnly, .excludeDesktopElements], kCGNullWindowID)
            as? [[String: Any]] {
            for info in list {
                guard let pid = info[kCGWindowOwnerPID as String] as? pid_t, pid != ownPID, pid == frontPID,
                      (info[kCGWindowLayer as String] as? Int) == 0,
                      let bounds = info[kCGWindowBounds as String] as? [String: CGFloat] else { continue }
                let w = bounds["Width"] ?? 0, h = bounds["Height"] ?? 0, y = bounds["Y"] ?? 1
                if w >= screen.frame.width, h >= screen.frame.height, y <= 0 { covered = true; break }
            }
        }
        if covered != state.isHidden { setHidden(covered) }
    }

    private func setHidden(_ hidden: Bool) {
        state.isHidden = hidden
        if hidden { state.forceClose() }
        panel.alphaValue = hidden ? 0 : 1
    }
}
