# Purrch for Windows: starting prompt

Paste everything below the line into a new Claude Code session opened in the `Notchyff` repo.

---

I want to build Purrch for Windows. Purrch is my macOS notch app in this repo (Swift/SwiftUI, sources in `Sources/Purrch/`, features listed in `README.md`). The Windows version is a rewrite, not a port: Swift and AppKit don't run on Windows, and Windows laptops have no notch.

**Where the code goes**

- All Windows work lives in the `windows/` folder of this repo. Don't touch the macOS code in `Sources/`, `Resources/` or `scripts/` unless I ask.
- Add a `windows/.gitignore` for `bin/`, `obj/`, `.vs/` and `*.user`.
- Follow the Git rules in `CLAUDE.md`: Conventional Commits with a `windows` scope (for example `feat(windows): add top pill window`), one commit per real stage of work, no AI attribution, and push only when I ask.

**Default decisions** (use these unless I say otherwise when we start)

- Stack: C# with .NET 8 and WPF. No Electron.
- Shell: a small pill at the top center of the primary screen that stays on top of other windows. Hovering the pill (or clicking it, as a setting) expands it into the panel; moving away folds it back. While closed, the pill shows live activities: what is playing, a running timer, a download in progress. It hides over full-screen apps.
- Tab bar of modules with enable/disable and drag-to-reorder, plus a settings page and themes, like the Mac app.
- Tray icon with Open, Settings and Quit.
- Launch at startup is on after the first run, and the user can turn it off in Settings. This matches the Mac app's behaviour in 1.0.5.
- Settings are stored in `%APPDATA%\Purrch`. API keys go in Windows Credential Manager, never in plain files.

**First version (MVP)**, in this order:

1. Pill window, expand/collapse, tab bar, settings, tray icon, launch at startup.
2. Timers (Pomodoro, countdown, stopwatch).
3. Clipboard history.
4. Notes and Scratchpad.
5. Now Playing via the Windows media session API (Spotify, browser, any app).
6. System (CPU, memory, disk, network, battery).
7. AI assistant and Translate, using Claude, OpenAI or Gemini with the user's own key. Translate works the same as on Mac: Hinglish or any language, plus grammar fixes, rewrite, tone and shorten.

Later, one at a time: Emoji, Units, Links, Shelf, Captures, File Converter, Downloads, Message, Ambience, Calendar, Weather, Screen Time, Claude Code usage, Stripe revenue, Plausible analytics.

**Testing**

I develop on a Mac, so the app can be built but not run here. Make sure `dotnet build` works with `EnableWindowsTargeting`. Tell me clearly which things need testing on a Windows PC or VM, and give me the exact steps to run the app there.

**How to start**

Read `README.md` and skim `Sources/Purrch/` so the Windows app matches the Mac app's look and features. Then show me a short plan for step 1 and ask me anything you need: stack, MVP scope, or whether I have a Windows machine for testing. Wait for my answers, then build step 1.
