<p align="center">
  <img src="Resources/Branding/Logo.png" width="112" alt="Purrch">
</p>

<h1 align="center">Purrch</h1>

<p align="center">
  The MacBook notch, turned into a small control centre.
</p>

<p align="center">
  <img alt="macOS 14+" src="https://img.shields.io/badge/macOS-14%2B-black">
  <img alt="Swift 5.9" src="https://img.shields.io/badge/Swift-5.9-orange">
  <img alt="SwiftUI" src="https://img.shields.io/badge/UI-SwiftUI-blue">
  <img alt="No dependencies" src="https://img.shields.io/badge/dependencies-none-lightgrey">
</p>

---

Purrch is a native macOS menu-bar-less app that lives in the notch. Hover over the notch and it expands into a panel with 24 small tools; move away and it folds back. While closed, the notch shows live activities: what is playing, a running timer, a download in progress.

The project is plain Swift and SwiftUI with AppKit where SwiftUI falls short. There are no third-party packages, no account, and no telemetry. Data stays on the machine; secrets go to the keychain.

## Download

**[Download Purrch for Mac (.dmg)](https://github.com/Mishra-coder/purrch-macos_app/releases/latest/download/Purrch-1.0.5.dmg)** · Apple Silicon (M1 or later), macOS 14 or later

1. Open the downloaded `Purrch-1.0.5.dmg`.
2. Drag **Purrch** onto the **Applications** folder.
3. Open Purrch from Applications. macOS will say it cannot verify the developer; click **Done**.
4. Open **System Settings → Privacy & Security**, scroll down and click **Open Anyway** next to Purrch, then confirm.

You only need step 4 once. Then hover over the notch to open the panel. Purrch adds itself to your login items the first time it opens, so it starts again after a restart; turn off **Launch at login** in Settings if you don't want that.

What changed in each version: [CHANGELOG.md](CHANGELOG.md).

> Purrch is not notarised by Apple yet, which is why step 4 is needed. If you prefer the Terminal, this does the same thing:
> ```sh
> xattr -dr com.apple.quarantine /Applications/Purrch.app
> ```

## Table of contents

- [Download](#download)
- [Changelog](CHANGELOG.md)
- [Highlights](#highlights)
- [Quick start](#quick-start)
- [Requirements](#requirements)
- [Repository layout](#repository-layout)
- [Architecture](#architecture)
- [Design decisions](#design-decisions)
- [Privacy and permissions](#privacy-and-permissions)
- [Configuration](#configuration)
- [Building for distribution](#building-for-distribution)
- [Development](#development)
- [Troubleshooting](#troubleshooting)
- [Known limitations](#known-limitations)
- [License](#license)

## Highlights

**Shell**

- Hover-to-open panel anchored to the camera housing, with a simulated notch on external or older displays.
- Dock-style module bar: magnification under the pointer, drag to reorder, per-module enable and disable.
- Live activities in the closed notch for media, timers, downloads and a scrolling dot-matrix message.
- Live preview of a playing browser video when hovering, with playback and YouTube quality controls.
- Six themes. Hides automatically over full-screen apps.

**Modules**

| Area | What is included |
| --- | --- |
| Productivity | Timers (Pomodoro, countdown, stopwatch, water reminder), Clipboard history, Notes, Scratchpad, Links, Units, Emoji, Translate (Hinglish or any language, plus Grammarly-style grammar fixes, rewrites, tone and shortening) |
| Media | Now Playing (Music, Spotify), YouTube control across browser tabs, Ambience (ten synthesised soundscapes), Message |
| Files | Shelf for parking files, Captures (recent screenshots and recordings, with preview, multi-select and undoable delete), File Converter (JPEG, PNG, HEIC, PDF), Downloads |
| Insights | Home dashboard, System (CPU, memory, disk, network, battery health), Screen Time, Calendar and Reminders, Weather |
| Integrations | MacMate assistant (Apple on-device model, Claude, OpenAI or Gemini), Claude Code usage, Stripe revenue, Plausible analytics |

## Quick start

```sh
git clone https://github.com/Mishra-coder/purrch-macos_app.git purrch && cd purrch
make run
```

`make run` produces a release build at `build/Purrch.app`, signs it ad hoc and launches it. Hover over the notch to open the panel.

| Target | Description |
| --- | --- |
| `make build` | Debug compile with SwiftPM |
| `make app` | Release `build/Purrch.app` |
| `make run` | Build, then relaunch the app |
| `make dmg` | Drag-to-install image at `build/Purrch-<version>.dmg` |
| `make icon` | Regenerate `Resources/AppIcon.icns` from the mascot artwork |
| `make clean` | Remove `.build/` and `build/` |

## Requirements

| | Minimum |
| --- | --- |
| macOS (run) | 14 Sonoma, Apple Silicon |
| Xcode (build) | 15, Swift 5.9 toolchain |
| MacMate on-device model | macOS 26 with Apple Intelligence enabled |

Release builds are currently arm64 only. Nothing in the code is architecture-specific, but Intel has not been tested.

## Repository layout

```
.
├── Makefile
├── Package.swift
├── Resources
│   ├── Info.plist                 Bundle metadata, privacy usage strings
│   ├── AppIcon.icns               Generated, do not edit by hand
│   └── Branding/                  Mascot and logo artwork
├── scripts
│   ├── build-app.sh               Compile, assemble and sign Purrch.app
│   ├── package-dmg.sh             Create the distributable disk image
│   └── make-icon.swift            Render the icon set from the mascot
└── Sources/Purrch
    ├── App/                       Entry point, AppSettings, Store, Keychain
    ├── Core/                      Notch window, hover state machine, module registry
    ├── Interface/                 Root, collapsed, settings and onboarding views
    ├── DesignSystem/              Theme, Motion, Card and shared controls, notch shape
    ├── Shared/                    File and HTTP helpers
    └── Features/
        ├── Productivity/
        ├── Media/
        ├── Files/
        ├── Insights/
        └── Integrations/
```

Feature folders own their views and, where needed, their services. Nothing under `Features/` imports another feature except through a service's public API (for example, Home reads `NowPlayingService` and `CalendarService`).

## Architecture

### Window

`NotchWindowController` owns one borderless, non-activating `NSPanel` at `.statusBar + 1`, joined to all Spaces. Its frame is fixed and large enough for the expanded panel plus shadow; the visible shape is drawn by SwiftUI inside it. While collapsed the panel ignores mouse events, so the menu bar under it keeps working.

The notch size comes from `NSScreen.auxiliaryTopLeftArea`, `auxiliaryTopRightArea` and `safeAreaInsets`. Without a camera housing, a 190 pt simulated notch is drawn under the menu bar.

### Interaction model

Hover is detected by polling `NSEvent.mouseLocation` at 30 Hz against the shape's current screen rect. Tracking areas were not used because the shape's bounds change every frame while it animates.

```
collapsed  ── pointer rests 160 ms ─────────────────────────►  expanded (or video preview)
expanded   ── pointer outside 350 ms, not pinned, no button ─►  collapsed
collapsed  ── file drag enters the notch ───────────────────►  expanded on Shelf
any        ── Esc, or click in another app ─────────────────►  collapsed
```

`NotchState` holds this state, the selected module and the tab-switch direction. A video in Full notch (theater) is remembered when the panel closes, so hovering again returns to it; Exit or Esc leaves theater. Every transition goes through the curves in `Motion` (`open`, `close`, `pop`, `expoOut`), so the panel, tabs and content move with one consistent feel.

### Modules

`ModuleID` is the single registry of modules: title, SF Symbol, one-line description and view. A module is a SwiftUI view that fits a roughly 748 by 300 pt content area, built from `Card` and the shared controls, and reads colour only from the `theme` environment value. In the Playful theme the shell injects a per-module accent, so module code never branches on theme.

Work that must continue while the panel is closed lives in `@MainActor` services started by `BackgroundServices`: clipboard capture, now playing, browser media, downloads and screen time.

### Browser video

`BrowserMediaService` enumerates YouTube tabs in Chromium-family browsers and Safari over Apple Events and controls each tab's `<video>` with short JavaScript snippets. The hover preview draws the video into an offscreen canvas in the page and returns JPEG frames. Decoding happens off the main thread; frames are assigned directly to a `CALayer` through `FrameSink`, so SwiftUI is not involved per frame. Playback state is folded back into the published model at 2 Hz, and the periodic tab scan pauses while a preview is streaming.

When the video can be embedded, the picture comes from a muted YouTube embed instead, which plays at full frame rate. `EmbeddedPlayerHost` owns that one web view. It starts loading as soon as a browser video plays in Video mode and stays parked, invisible, in the notch window between opens, so the video shows as soon as the panel opens. It follows the tab by adjusting the playback rate for small drift and seeks only for large gaps. If the embed fails, the player falls back to mirrored frames.

### State and storage

| Kind | Mechanism | Location |
| --- | --- | --- |
| Preferences | `AppSettings` over `UserDefaults` | `com.mishracoder.purrch` domain |
| Feature data | `Store`, JSON per feature | `~/Library/Application Support/Purrch` |
| Secrets | `Keychain`, generic passwords | Login keychain, service `com.mishracoder.purrch` |

## Design decisions

**Not sandboxed, not on the Mac App Store.** A window over the menu bar on every Space, Apple Events to other apps and Accessibility-driven setup are not available to sandboxed apps. Distribution is a signed, notarised DMG instead.

**Polling over tracking areas.** A 30 Hz location check is cheap and stays correct while the shape animates. Tracking areas would need rebuilding on every frame of the transition.

**Apple Events for media.** macOS 15.4 stopped exposing the system now-playing information to third-party apps. Music and Spotify are read through AppleScript, browsers through JavaScript from Apple Events. The cost is a one-time Automation prompt per app and, for browsers, a developer setting that Purrch can switch on for the user.

**Synthesised audio.** Ambience generates its ten soundscapes with `AVAudioSourceNode` instead of shipping audio files. The app stays small and the sounds loop without seams.

**No dependencies.** Everything used is in the SDK. Builds are reproducible offline and there is no supply chain to audit.

## Privacy and permissions

Purrch requests a permission only when the feature that needs it is first used.

| Permission | Needed for |
| --- | --- |
| Calendars, Reminders | Calendar, Home |
| Location | Weather; a city can be entered instead |
| Automation | Music, Spotify and browser control |
| Accessibility | Optional one-click enabling of browser JavaScript control |
| Downloads and Desktop folders | Downloads, Captures |
| Notifications | Timer and water reminders |

The clipboard monitor skips items marked concealed or transient by password managers and keeps at most 300 entries; pinned entries are never evicted. Network requests go only to services the user configures: Open-Meteo for weather, the selected AI provider, Stripe and Plausible.

## Configuration

All settings live in the panel (gear icon). Integrations need keys:

| Setting | Notes |
| --- | --- |
| Anthropic API key | MacMate with Claude (`claude-opus-5-5`), optional web search |
| OpenAI, Gemini API keys | MacMate with `gpt-4o-mini` or `gemini-2.0-flash` |
| Stripe key | Use a restricted, read-only key |
| Plausible key, site, host | The host field supports self-hosted instances |
| Weather city | Overrides location lookup |

## Building for distribution

`scripts/build-app.sh` signs ad hoc unless `SIGN_IDENTITY` is set:

```sh
SIGN_IDENTITY="Developer ID Application: Name (TEAMID)" make dmg
xcrun notarytool submit build/Purrch-1.0.5.dmg --keychain-profile <profile> --wait
xcrun stapler staple build/Purrch-1.0.5.dmg
```

With a Developer ID identity the build enables the hardened runtime and a secure timestamp. Bump `CFBundleShortVersionString` and `CFBundleVersion` in `Resources/Info.plist` before packaging; the DMG name is taken from the former.

## Development

**Adding a module**

1. Add a case to `ModuleID` with title, icon, subtitle and view.
2. Put the view in the matching `Features/` folder and build it from `Card`, `IconButton`, `PillButton` and the `theme` environment value.
3. Move anything that must outlive the view into a `@MainActor` service and start it in `BackgroundServices`.
4. To appear in the closed notch, add a case to `LiveActivity` in `CollapsedView`.

**Conventions**

- UI state and services are `@MainActor`. Blocking work (AppleScript, file scans, image decoding) runs on a dedicated queue or a detached task and hops back to the main actor to publish.
- Colours come from `Theme`, motion from `Motion`. No literal colours in feature code except where the colour carries meaning (red for delete, green for charging).
- The package uses the Swift 5 language mode; new code should still compile cleanly under strict concurrency checking.

**Scripting hook**

The app listens for the distributed notification `com.mishracoder.purrch.open`. The object selects what to show: a module identifier such as `timers`, or `settings`, `videopeek`, `theater` or `close`. It is used for UI testing and screenshots.

## Troubleshooting

**macOS asks for permissions again after every build.** Ad-hoc signatures change with each build, so privacy grants do not carry over. Sign with a stable identity (`SIGN_IDENTITY`) to keep them.

**The panel does not close when the pointer leaves.** It is pinned. The pin control in the header shows "Pinned" while active; click it to release.

**Browser videos are listed but cannot be controlled.** Enable "Allow JavaScript from Apple Events" in the browser (Chrome: View, Developer; Safari: Develop), or use the Enable button in the notch, which does it once Accessibility is granted.

**Keyboard shortcuts such as Cmd-V do nothing in text fields.** Text editing commands are routed through the app's hidden Edit menu. If a custom build removes `setUpMainMenu()`, they stop working.

## Known limitations

- Now Playing covers Music, Spotify and browser YouTube. Other players are not visible since macOS 15.4.
- The browser video preview runs at 12 to 24 fps depending on the selected quality, bounded by the Apple Events round trip.
- Full-screen detection uses window geometry and can miss some full-screen modes.
- Release builds target Apple Silicon only.

## License

Copyright © 2026 Devendra Mishra. All rights reserved.
