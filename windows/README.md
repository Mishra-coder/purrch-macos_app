# Purrch for Windows

A rewrite of Purrch for Windows in C#, .NET 8 and WPF. Windows laptops have no notch, so Purrch shows a small pill at the top centre of the primary screen. Hover over it and it opens into the panel; move away and it folds back.

The project builds on macOS, but the app only runs on Windows 10 (1809 or later) and Windows 11.

## Status

| Step | What | State |
| --- | --- | --- |
| 1 | Pill window, expand and collapse, tab bar, settings, themes, tray icon, launch at startup | Done; not yet run on Windows |
| 2 | Timers (Pomodoro, countdown, stopwatch, water reminder) | Done; not yet run on Windows |
| 3 | Clipboard history | Done; not yet run on Windows |
| 4 | Notes and Scratchpad | Done; not yet run on Windows |
| 5 | Now Playing (Windows media session) | Done; not yet run on Windows |
| 6 | System (CPU, memory, disk, network, battery) | Done; not yet run on Windows |
| 7 | Assistant and Translate (Claude, OpenAI or Gemini, your own key) | Done; not yet run on Windows |

All 24 modules from the Mac app are in the tab bar. The ones not built yet show a "Coming soon" card.

## Build on a Mac

You need the .NET 8 SDK. It does not need admin rights if you install it into your home folder:

```sh
curl -sSL https://dot.net/v1/dotnet-install.sh -o dotnet-install.sh
bash dotnet-install.sh --channel 8.0 --install-dir ~/.dotnet
~/.dotnet/dotnet build windows/Purrch.sln
```

`EnableWindowsTargeting` in the project file makes the build work outside Windows. A clean build has no errors or warnings.

## Run on Windows

### Option A: build a Windows .exe on the Mac and copy it over

```sh
~/.dotnet/dotnet publish windows/src/Purrch -c Release -r win-x64 --self-contained \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:EnableCompressionInSingleFile=true -o windows/publish/win-x64
```

This writes one `Purrch.exe` (about 79 MB, with .NET included) to `windows/publish/win-x64`. Use `-r win-arm64` instead for Windows on ARM, for example Windows 11 in Parallels or UTM on an Apple Silicon Mac.

1. Copy `Purrch.exe` to the PC, for example into `C:\Users\<you>\Apps\Purrch\`. Keep it in that folder, because launch at startup remembers this path.
2. Double-click it. The exe is not signed yet, so SmartScreen will say "Windows protected your PC". Click **More info**, then **Run anyway**.
3. A black pill appears at the top centre of the screen, and the Purrch cat appears in the notification area (it may be under the **^** arrow).

### Option B: build on the Windows PC

```powershell
winget install Microsoft.DotNet.SDK.8
git clone https://github.com/Mishra-coder/purrch-macos_app.git purrch
cd purrch\windows
dotnet run --project src\Purrch
```

`dotnet run` makes a Debug build. Debug builds never add themselves to startup, so your startup list doesn't get a path inside `bin\Debug`.

## What to test on Windows

None of this has run on a real Windows machine yet. Please go through the list and report anything that is off, with a screenshot if you can, and include `%APPDATA%\Purrch\purrch.log` if something crashed.

**Pill and panel**

- [ ] The pill sits at the top centre of the primary screen, is crisp (not blurry), and stays above other windows.
- [ ] Hovering the pill makes it grow slightly, and after a moment it opens into the panel with a small bounce.
- [ ] Moving the pointer away closes it after about a third of a second. Clicking in another app closes it straight away.
- [ ] Clicking the empty area around the pill still reaches the window underneath, for example browser tabs.
- [ ] The pin in the header keeps the panel open. Esc closes it, even when pinned.
- [ ] Purrch does not appear in the taskbar or in Alt+Tab.
- [ ] Repeat at 100%, 125% and 150% display scaling, and with a second monitor. The pill should stay on the primary screen.

**Tabs, settings and themes**

- [ ] On first run a welcome screen appears. **Get started** brings up the tab bar.
- [ ] All tab icons show as icons, not empty boxes. On Windows 10 they come from Segoe MDL2 Assets, on Windows 11 from Segoe Fluent Icons.
- [ ] Icons grow under the pointer and show their name. Resting on one for a moment selects it. Dragging one sideways reorders the tabs, and the order is still the same after quitting and reopening Purrch.
- [ ] Each of the six themes recolours the panel at once. Playful gives each module its own colour.
- [ ] Settings → Modules: turning modules off removes them from the tab bar, and **Reset order** works. Home can't be turned off.
- [ ] Settings → Behaviour: with **Open on hover** off, the panel only opens on click.

**Timers**

- [ ] Pomodoro: start, pause, reset and skip work, and the ring empties as time passes. The arrows change the focus and break minutes.
- [ ] Timer: the 5m, 10m and 25m chips start a countdown, and minus and plus change the custom length. When it ends, a chime plays, a Windows notification appears and the panel opens on Timers.
- [ ] While a pomodoro or countdown runs, the closed pill shows the time on the right and an icon on the left, with a soft glow.
- [ ] Stopwatch: start, lap, pause and reset. Laps are listed newest first.
- [ ] Water: tapping drops fills them, and the count is still the same after restarting Purrch. With the switch on, a reminder notification comes every 30 minutes.

**Clipboard**

- [ ] Copying text, a link, a file in File Explorer and a screenshot (Win+Shift+S) each adds a row with the right icon, tag, size, app name and time.
- [ ] Copying the same thing again moves it to the top instead of adding a duplicate.
- [ ] Clicking a row copies it back (a "Copied" mark appears) and Ctrl+V pastes it in another app, including files and images. That copy doesn't add a new row.
- [ ] Search filters as you type. The pin keeps a row at the top. The bin clears everything that isn't pinned.
- [ ] Right-click shows Copy, Pin, Show in File Explorer (files), Open Link (links) and Delete. The panel stays open while the menu is open.
- [ ] Dragging a row into Notepad, a chat box or a folder drops the text, file or image.
- [ ] A password copied from a password manager (Bitwarden, 1Password, KeePass) is **not** recorded.

**Notes and Scratchpad**

- [ ] Notes: + makes a new note; typing a title updates the list on the left. Enter in "Add an item" adds it and keeps the cursor there. Ticking an item strikes it through; "Clear done" removes ticked items. The toggle switches between checklist and text. Right-click a note to convert or delete it.
- [ ] Scratchpad: typing is kept after quitting and reopening Purrch. Text size, monospaced font, Copy all and Clear (two clicks) work.
- [ ] After typing in a note, moving the pointer away closes the panel and your next keystrokes go to the app you were in before.

**Now Playing**

- [ ] Play a song in Spotify, a YouTube video in Edge or Chrome, and something in the Media Player app. Each shows its title, artist, artwork and app name.
- [ ] Play/pause, previous and next control that app. Clicking or dragging along the progress bar jumps in the track (Spotify and Media Player; many websites don't allow it).
- [ ] While music plays, the closed pill shows the artwork on the left and moving bars on the right. A running timer takes priority over music.
- [ ] With nothing playing, the module says "Nothing playing".

**System**

- [ ] CPU load and its 60-second chart match Task Manager → Performance roughly. Memory, and free space on the Windows drive, match too.
- [ ] Downloading something large shows the download rate and the chart rising.
- [ ] On a laptop: battery %, Charging / Plugged in / On battery, health and time remaining look right; unplugging updates within a second or two. On a desktop or VM it says "No battery".
- [ ] Machine shows uptime, the Windows version and the PC name.

**Assistant**

- [ ] Settings → AI: paste a Claude, OpenAI or Gemini key; "Saved" appears and the key shows up in Credential Manager → Windows Credentials as `Purrch:anthropic` / `openai` / `gemini`. Clearing the field removes it.
- [ ] With no key, the Assistant says which key is missing. With a key, the suggestion chips and typed questions get answers; **bold** and `code` render, and right-click copies a reply.
- [ ] The provider capsule switches between Claude, OpenAI and Gemini; for Claude, "Web search" lets it answer questions about recent events.
- [ ] A wrong key gives a clear "rejected the API key" message, and no internet gives "Couldn't reach ...".

**Translate**

- [ ] Typing "kal milte hain yaar" with "Detect language" translates it to English after a short pause and shows "Detected: Hinglish". Changing the target language translates again.
- [ ] Swap (with a fixed source language) swaps the languages and moves the translation into the input.
- [ ] Fix grammar, Improve, Tone (with a tone menu) and Shorten run on the button or Ctrl+Enter; changed words are highlighted and Accept puts the result in the input. Copy and Clear work.
- [ ] The provider capsule can use a different provider from the Assistant; a missing key shows where to add it.

**Tray, startup and full screen**

- [ ] Tray icon: left-click opens the panel. Right-click shows **Open Purrch**, **Settings** and **Quit Purrch**, and each one works.
- [ ] Opening the exe a second time opens the panel of the copy that is already running, and doesn't start a second copy.
- [ ] Release build: after the first run, Purrch is listed under Task Manager → Startup apps and starts again after signing out and back in. Turning **Launch at startup** off in Settings removes it, and it stays off.
- [ ] A full-screen YouTube video (F11 or the video's full-screen button), a full-screen game and a PowerPoint slideshow all hide the pill. It comes back when they leave full screen. A normal maximised window must **not** hide it.

## Where Purrch keeps things

| What | Where |
| --- | --- |
| Settings | `%APPDATA%\Purrch\settings.json` |
| Translate languages, mode and provider | `%APPDATA%\Purrch\translate.json` |
| Timer lengths, pomodoros done, water | `%APPDATA%\Purrch\timers.json`, `water.json` |
| Clipboard history | `%APPDATA%\Purrch\clipboard.json`, images in `clipboard-images\` |
| Notes, Scratchpad | `%APPDATA%\Purrch\notes.json`, `scratchpad.json` |
| Log (startup and unhandled errors) | `%APPDATA%\Purrch\purrch.log` |
| API keys | Windows Credential Manager → Windows Credentials, entries named `Purrch:<name>` |
| Launch at startup | `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`, value `Purrch` |

To reset Purrch completely, quit it from the tray, delete `%APPDATA%\Purrch`, and turn off **Purrch** in Task Manager → Startup apps (or delete the Run value).

## Project layout

```
windows/
  Purrch.sln
  src/Purrch/
    App.xaml(.cs)          single instance, crash log, creates the window and tray icon
    Core/                  settings, panel state, modules, live activities, Win32 calls,
                           Credential Manager, startup registration
    DesignSystem/          themes, motion curves, the pill shape, shared styles and UI helpers
    Interface/             pill window, closed and open views, tab bar, settings, welcome, tray icon
    Assets/                app icon, tray icon, mascot
```

How it maps to the Mac app:

| Mac (`Sources/Purrch`) | Windows (`windows/src/Purrch`) |
| --- | --- |
| `NotchWindowController` | `Interface/NotchWindow` |
| `NotchState`, `Motion` | `Core/NotchState`, `DesignSystem/Motion` |
| `AppSettings` (UserDefaults and keychain) | `Core/AppSettings` (JSON) and `Core/SecretStore` (Credential Manager) |
| `Theme`, `NotchShape` | `DesignSystem/Theme`, `DesignSystem/NotchSurface` |
| `RootView`, `CollapsedView`, `TabBar`, `SettingsView` | the views with the same names in `Interface/` |
| SF Symbols | Segoe Fluent Icons / Segoe MDL2 Assets glyphs (`ModuleInfo.Glyph`) |
| MacMate AI | Assistant |

## Known limitations

- The pill always sits on the primary screen.
- Clicking inside the open panel makes Purrch the active window. When the panel closes, keyboard focus goes back to the app you were in.
- The closed pill shows running timers. Music and downloads come with Now Playing (step 5) and Downloads.
- The exe is not code-signed, so SmartScreen warns on first run.
