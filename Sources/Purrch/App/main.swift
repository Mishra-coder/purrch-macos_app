import AppKit
import SwiftUI

@MainActor
final class AppDelegate: NSObject, NSApplicationDelegate {
    private var controller: NotchWindowController?
    private var statusItem: NSStatusItem?

    func applicationDidFinishLaunching(_ notification: Notification) {
        setUpMainMenu()
        controller = NotchWindowController()
        setUpStatusItem()
        Services.startBackground()

        // `notifyutil`-style hook for scripts: post "com.mishracoder.purrch.open" with a module id.
        DistributedNotificationCenter.default().addObserver(
            forName: Notification.Name("com.mishracoder.purrch.open"), object: nil, queue: .main
        ) { note in
            MainActor.assumeIsolated {
                let state = NotchState.shared
                if (note.object as? String) == "close" {
                    state.forceClose()
                    return
                }
                if (note.object as? String) == "theater" {
                    let browser = BrowserMediaService.shared
                    guard let video = browser.playingVideo ?? browser.videos.first else { return }
                    state.holdUntilHovered = true
                    state.open(.nowPlaying)
                    state.enterTheater(video.id)
                    return
                }
                if (note.object as? String) == "videopeek" {
                    state.holdUntilHovered = true
                    state.openVideoPeek()
                    return
                }
                state.showSettings = (note.object as? String) == "settings"
                state.present((note.object as? String).flatMap(ModuleID.init(rawValue:)))
            }
        }

        if !AppSettings.shared.hasCompletedOnboarding {
            DispatchQueue.main.asyncAfter(deadline: .now() + 0.6) {
                NotchState.shared.present()
            }
        }
    }

    /// Purrch never shows a menu bar, but text fields only get ⌘C / ⌘V / ⌘X / ⌘A / ⌘Z through
    /// an Edit menu's key equivalents — so install one, hidden.
    private func setUpMainMenu() {
        let main = NSMenu()
        let appItem = NSMenuItem()
        let appMenu = NSMenu()
        appMenu.addItem(withTitle: "Quit Purrch", action: #selector(NSApplication.terminate(_:)), keyEquivalent: "q")
        appItem.submenu = appMenu
        main.addItem(appItem)

        let editItem = NSMenuItem()
        let edit = NSMenu(title: "Edit")
        edit.addItem(withTitle: "Undo", action: Selector(("undo:")), keyEquivalent: "z")
        let redo = edit.addItem(withTitle: "Redo", action: Selector(("redo:")), keyEquivalent: "z")
        redo.keyEquivalentModifierMask = [.command, .shift]
        edit.addItem(.separator())
        edit.addItem(withTitle: "Cut", action: #selector(NSText.cut(_:)), keyEquivalent: "x")
        edit.addItem(withTitle: "Copy", action: #selector(NSText.copy(_:)), keyEquivalent: "c")
        edit.addItem(withTitle: "Paste", action: #selector(NSText.paste(_:)), keyEquivalent: "v")
        let plain = edit.addItem(withTitle: "Paste and Match Style",
                                 action: #selector(NSTextView.pasteAsPlainText(_:)), keyEquivalent: "v")
        plain.keyEquivalentModifierMask = [.command, .option, .shift]
        edit.addItem(withTitle: "Delete", action: #selector(NSText.delete(_:)), keyEquivalent: "")
        edit.addItem(withTitle: "Select All", action: #selector(NSText.selectAll(_:)), keyEquivalent: "a")
        editItem.submenu = edit
        main.addItem(editItem)

        NSApp.mainMenu = main
    }

    private func setUpStatusItem() {
        let item = NSStatusBar.system.statusItem(withLength: NSStatusItem.squareLength)
        item.button?.image = Brand.menuBarImage()
        let menu = NSMenu()
        menu.addItem(withTitle: "Open Notch", action: #selector(openNotch), keyEquivalent: "o").target = self
        menu.addItem(withTitle: "Settings…", action: #selector(openSettings), keyEquivalent: ",").target = self
        menu.addItem(.separator())
        menu.addItem(withTitle: "Quit Purrch", action: #selector(NSApplication.terminate(_:)), keyEquivalent: "q")
        item.menu = menu
        statusItem = item
    }

    @objc private func openNotch() {
        NotchState.shared.present()
    }

    @objc private func openSettings() {
        NotchState.shared.showSettings = true
        NotchState.shared.present()
    }
}

LegacyMigration.run()

MainActor.assumeIsolated {
    let app = NSApplication.shared
    let delegate = AppDelegate()
    app.delegate = delegate
    app.setActivationPolicy(.accessory)
    app.run()
}
