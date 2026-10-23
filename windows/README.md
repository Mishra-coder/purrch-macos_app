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
| 7 | Assistant and Translate (Claude, OpenAI or Gemini with your own key, or a local model through Ollama or Foundry Local) | Done; not yet run on Windows |
| 8 | Emoji, Units, Links, Shelf, Captures, File Converter, Downloads, Message, Ambience, Calendar, Weather | Done; not yet run on Windows |
| 9 | Screen Time and Claude Code | Done; not yet run on Windows |
| 10 | Revenue (Stripe) and Analytics (Plausible), with your own key | Done; not yet run on Windows |
| 11 | Home (greeting, clock, battery, weather and next event, system rings, app launcher, now playing) | Done; not yet run on Windows |
| 12 | YouTube in the browser: Purrch's extension for Chrome, Edge, Brave and Vivaldi, browser video rows, Audio / Video modes, live video (embedded player or frames), Video on hover, theater | Done; not yet run on Windows |

All 24 modules from the Mac app are in the tab bar and built.

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

This writes one `Purrch.exe` (about 84 MB, with .NET included) to `windows/publish/win-x64`. Use `-r win-arm64` instead for Windows on ARM, for example Windows 11 in Parallels or UTM on an Apple Silicon Mac.

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

### Open Purrch from a script or shortcut

Running `Purrch.exe` again with a word hands it to the copy that is already running, like the Mac app's script hook:

| Command | What it does |
| --- | --- |
| `Purrch.exe` | Opens the panel |
| `Purrch.exe timers` (any module: `home`, `clipboard`, `weather`, …) | Opens the panel on that module |
| `Purrch.exe settings` | Opens Settings |
| `Purrch.exe close` | Closes the panel, even when pinned |
| `Purrch.exe theater` | Opens the playing browser video (or the first one open) filling the whole panel |
| `Purrch.exe videopeek` | Opens the small live-video window |

### Connect a browser (YouTube in the browser)

Purrch reads and controls YouTube in Chrome, Edge, Brave and Vivaldi through its own browser extension, the Windows counterpart of the Mac app's Apple Events. Nothing is installed or registered until you ask for it:

1. Open Purrch's **Settings → Behaviour** and click **Connect browser** under **YouTube in the browser**. Purrch writes the extension to `%APPDATA%\Purrch\extension`, writes `%APPDATA%\Purrch\bridge\com.purrch.bridge.json`, and registers that file as the native messaging host `com.purrch.bridge` for Chrome, Edge, Brave, Vivaldi and Chromium under `HKEY_CURRENT_USER` (no admin rights needed). The status says **Waiting for the extension**, and the steps below appear.
2. In the browser, open `chrome://extensions` (`edge://extensions`, `brave://extensions`, `vivaldi://extensions`) and turn on **Developer mode**.
3. Click **Load unpacked** and choose `%APPDATA%\Purrch\extension`. **Open folder** and **Copy path** in Settings help find it.
4. Within a few seconds the status says **Connected · Chrome** (or Edge, …). Repeat steps 2 and 3 in each browser you use.

**Disconnect** deletes the registry entries, the bridge manifest and the extension folder; then remove the extension on the browser's extensions page. If you move `Purrch.exe` to another folder, Settings says **Purrch moved — click Connect again**. Connect again also updates the extension's files after a Purrch update (click the reload arrow on the extensions page afterwards). See [`extension/README.md`](extension/README.md) for how the pieces fit.

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
- [ ] Settings → Permissions: **Open** next to Location, Notifications and Startup apps opens that page of Windows Settings.

**Home**

- [ ] Home shows "Good morning" (afternoon, evening, night) and the time in large digits, with AM/PM beside it on a 12-hour clock and without it on a 24-hour clock. The time ticks over on the minute, and only the digits that change roll in from above.
- [ ] In the Playful theme the hero's colour wash is Home's own colour, also right after switching from another module.
- [ ] The battery chip shows the level and Charging / Plugged in / Battery, green while charging and red below 20%, with a battery icon that fills with the level (with a bolt while charging); on a desktop or VM it shows "AC". Clicking it opens System.
- [ ] The weather chip shows the temperature with a colour icon and the town (or "--" before the first forecast); clicking it opens Weather. Changing **Fahrenheit** in Settings updates it.
- [ ] Leave Purrch running for over 20 minutes without opening the panel: Home's weather chip is already up to date when you open it (the forecast refreshes in the background, as on the Mac).
- [ ] With no calendars, the event chip says "Connect" and opens Calendar. With one, it shows the next event's title and "in 25m", "in 2h 5m", "in 1d" or "Now", in the calendar's colour; with nothing coming up it says "Free".
- [ ] System shows CPU, RAM and Disk rings that match the System module; clicking the card opens System.
- [ ] Launch shows Edge, File Explorer, Notepad, Calculator, PowerShell and Settings with their icons (an app that isn't installed is left out). Hovering grows an icon and shows its name; clicking opens the app and closes the panel. Right-click offers Open, Show in File Explorer and Remove from Launch.
- [ ] The dashed + opens a file picker in the Start menu's Programs folder with the panel staying open; picking shortcuts or .exe files adds them, and the list is the same after restarting Purrch.
- [ ] Now Playing shows the artwork, title and artist with previous, play/pause and next while something plays, and "Nothing playing" otherwise. The arrow opens the Now Playing module.

**Timers**

- [ ] Pomodoro: start, pause, reset and skip work, and the ring empties as time passes. The arrows change the focus and break minutes. In Molten, Tech HUD and Blood Moon the ring's arc glows (the faint track doesn't).
- [ ] Timer: the 5m, 10m and 25m chips start a countdown, and minus and plus change the custom length. Each new number slides down into place and fades in. When it ends, a chime plays, a Windows notification with its sound appears and the panel opens on Timers.
- [ ] Timer and pomodoro notifications show "Purrch" with its icon and stay in the notification centre (Win+N). Clicking one there, also after closing the panel, opens Purrch on Timers. With Purrch quit, clicking a water reminder starts it and opens Timers.
- [ ] While a pomodoro or countdown runs, the closed pill shows the time on the right and an icon on the left, with a soft glow.
- [ ] Stopwatch: start, lap, pause and reset. Laps are listed newest first.
- [ ] Water: tapping drops fills them; a drop that fills springs slightly larger (and glows in glow themes), one that empties shrinks back. The count is still the same after restarting Purrch.
- [ ] With the switch on, a "Drink some water 💧" notification with sound comes every 30 minutes, also after quitting Purrch (for up to eight hours). Restarting Purrch keeps the same rhythm instead of starting the 30 minutes again. Turning the switch off stops them, including those already queued.

**Clipboard**

- [ ] Copying text, a link, a file in File Explorer and a screenshot (Win+Shift+S) each adds a row with the right icon, tag, size, app name and time. Tags are coloured by type, the same as on Shelf (PDF red, ZIP yellow, Folder blue, Link green). A copied folder shows File Explorer's folder icon.
- [ ] A long file name is shortened in the middle ("Screenshot 20…12.png"), so its extension stays visible.
- [ ] Right-click a picture in Edge or Chrome, **Copy image**, on a PNG with a transparent background: the row's thumbnail and a paste into PowerPoint keep the transparency.
- [ ] Something that puts only a link on the clipboard, without text (the UniformResourceLocatorW format), still adds a Link row.
- [ ] Copying the same thing again moves it to the top instead of adding a duplicate.
- [ ] Clicking a row copies it back (a "Copied" mark fades in, then fades out) and Ctrl+V pastes it in another app, including files and images. That copy doesn't add a new row. A link copied back pastes as its address everywhere, and also carries the link format browsers use (UniformResourceLocatorW).
- [ ] Search filters as you type. The pin keeps a row at the top. The bin fades out everything that isn't pinned, then clears it.
- [ ] Right-click shows Copy, Pin, Show in File Explorer (files), Open Link (links) and Delete. The panel stays open while the menu is open. For several files copied together, Show in File Explorer selects all of them.
- [ ] Dragging a row into Notepad, a chat box or a folder drops the text, file or image.
- [ ] A password copied from a password manager (Bitwarden, 1Password, KeePass) is **not** recorded.

**Notes and Scratchpad**

- [ ] Notes: + clears the search and makes a new note; typing a title updates the list on the left. Enter in "Add an item" adds it and keeps the cursor there. Ticking an item strikes it through and greys it with a quick pop of the tick; a ticked item can still be edited. "Clear done" removes ticked items. The toggle switches between checklist and text. Right-click a note to convert or delete it; Delete is red.
- [ ] Misspelled words get a red squiggle in a text note, the Scratchpad and Translate's input, and right-clicking one offers corrections. Titles and checklist items aren't checked.
- [ ] Scratchpad: typing is kept after quitting and reopening Purrch. Text size, monospaced font, Copy all and Clear (two clicks) work. The count shows an emoji like 👍🏽 or an accented letter as one character.
- [ ] After typing in a note, moving the pointer away closes the panel and your next keystrokes go to the app you were in before.

**Now Playing**

- [ ] Play a song in Spotify, a YouTube video in Edge or Chrome, and something in the Media Player app. Each shows its title, artist, artwork and app name.
- [ ] Play/pause, previous and next control that app. Clicking or dragging along the progress bar jumps in the track (Spotify and Media Player; many websites don't allow it).
- [ ] Clicking play/pause flips the button, the PAUSED label and the bars at once, with no flicker back while the app catches up. If the app ignores it, the button shows the real state again after about a second.
- [ ] The artwork springs slightly smaller on pause and back to full size on play.
- [ ] The wide equaliser at the top right moves while playing and sits low and still when paused. With the panel closed or on another module, Purrch's CPU in Task Manager drops back (it only animates while visible).
- [ ] Pause Spotify, then play a YouTube video: the module switches to the video even if Windows still treats Spotify as current, and the controls act on the video.
- [ ] While music plays, the closed pill shows the artwork on the left and moving bars on the right. A running timer takes priority over music.
- [ ] With nothing playing, the module says "Nothing playing".

**YouTube in the browser**

- [ ] Before clicking anything: `HKCU\Software\Google\Chrome\NativeMessagingHosts` (and Edge's, Brave's, Vivaldi's, Chromium's) has no `com.purrch.bridge`, and `%APPDATA%\Purrch` has no `extension` or `bridge` folder, also after restarting Purrch.
- [ ] Settings → Behaviour → YouTube in the browser says **Not connected**. **Connect browser** creates the folders and the five registry keys, and the status turns to **Waiting for the extension** with the four steps, **Open folder** and **Copy path**.
- [ ] Loading the folder unpacked in Chrome shows the extension with ID `digijohbadkdamofjlcaobgpppepabem` and the Purrch icon, with no errors. Within a few seconds Settings says **Connected · Chrome**. Do the same in Edge: **Connected · Chrome, Edge**. Brave and Vivaldi show their own names.
- [ ] Quit Purrch from the tray with the browser open, then start it again: within a minute the status is Connected again, without touching the browser. No `Purrch.exe` from the bridge is left in Task Manager after quitting the browser, and none opens a window.
- [ ] Copy `Purrch.exe` to another folder and run that copy: Settings says **Purrch moved — click Connect again**; clicking it fixes it.
- [ ] **Disconnect** removes the keys and both folders, and the status says Not connected; the browser shows the extension as broken until you remove it.
- [ ] With Chrome or Edge open (a window, not just Edge's background process) and no extension connected, Now Playing shows a banner "Control YouTube in Chrome or Edge from the pill" with **Set up** (opens Settings → Behaviour) and an x that hides it until Purrch restarts. With no browser window open, no banner.
- [ ] Open three YouTube tabs (a watch page, a Short, and YouTube Music) across two windows. Now Playing shows the Audio / Video switch and a "Browser videos · 3 open" card with a row each: thumbnail, title, channel · browser, a progress line with times, back 10 s, play / pause, forward 10 s, next, and show tab. A Spotify track playing at the same time shows as a small card on the left.
- [ ] Play / pause flips at once and the tab follows; the playing row is tinted in the accent with moving bars. Back / forward 10 s, clicking along the line, and next video work. **Pause all** pauses every playing tab.
- [ ] Show tab brings that tab and its window to the front (also from a minimised window) and closes the panel.
- [ ] A tab that is still loading or was discarded by the browser (Memory Saver) is listed but only offers Show tab.
- [ ] Video mode (switch at the top; it's remembered after restarting Purrch): the playing video fills the left with title and channel, a scrubber, the controls and a quality gear; the sidebar lists the open videos (clicking one switches), **Pause all** and **Full notch**. The picture is smooth (YouTube's own player, muted) and stays in step with the tab; pausing in the browser pauses it within a second. Sound comes only from the browser.
- [ ] The controls fade out 2.5 s after the pointer stops moving (0.8 s after it leaves) and come back on movement; a thin red line stays at the bottom. Paused, they stay.
- [ ] The quality gear opens a sheet with YouTube's levels (1080p, 720p, …; picking one changes the tab's quality) and Notch preview Smooth / Balanced / Sharp.
- [ ] A video whose owner disabled embedding (or Windows 10 without the WebView2 Runtime) still shows a live picture, from the browser's frames, with the thumbnail until the first frame. Sharp is crisper, Smooth moves more.
- [ ] Clicking the picture (or **Full notch**, or the expand button) opens theater: the video fills the panel, which grows taller, with an **Exit** pill. Esc leaves theater first, then a second Esc closes the panel. Moving away closes the panel; hovering again brings back theater. Closing the tab ends theater. `Purrch.exe theater` opens it from a script.
- [ ] Settings → Behaviour → **Video on hover**: with a video playing, hovering the pill opens a small 540×352 window with the live video, its browser's name, a switch for other open videos, quality, and "All tools" (the full panel on Now Playing). Clicking the picture opens theater. With nothing playing, hovering opens the panel as usual. `Purrch.exe videopeek` opens it from a script.
- [ ] Closed pill: while a browser video plays (and no music app does), the pill shows the video's thumbnail on the left and moving bars on the right.
- [ ] Home's Now Playing card: with no music app playing, it shows the browser video's thumbnail with a red progress line, title, channel and play / pause; clicking the thumbnail opens Now Playing in theater.
- [ ] With Video mode or Video on hover on, opening the panel shows the picture at once (it loads ahead); after five minutes with the panel closed, the hidden player pauses and Purrch's CPU drops back.
- [ ] At 100%, 125% and 150% scaling the video sits exactly inside its rounded frame, also during the panel's open animation and theater's grow, with no square corners showing.

**System**

- [ ] CPU load and its 60-second chart match Task Manager → Performance roughly. Memory, and free space on the Windows drive, match too.
- [ ] Downloading something large shows the download rate and the chart rising.
- [ ] On a laptop: battery %, Charging / Plugged in / On battery, health and time remaining look right; unplugging updates within a second or two. On a desktop or VM it says "No battery".
- [ ] The Battery card's icon follows the level and shows a bolt while charging. In Molten, Tech HUD and Blood Moon the battery bar glows.
- [ ] Cycles matches `powercfg /batteryreport` ("Cycle count"), or shows "—" when the battery doesn't report one. It is read once a minute, so the module doesn't stutter.
- [ ] While charging the last stat says "To full" with a time that shrinks as it charges; on battery it says "Remaining".
- [ ] Machine shows uptime, Thermal, the Windows version and the PC name. Thermal says Nominal with a green dot normally; under a long full-load run on a hot laptop it may say Fair (yellow) or Serious (orange). Its tooltip says it's estimated from CPU throttling. If Windows won't say, it shows "Normal" with a grey dot.

**Assistant**

- [ ] Settings → AI: paste a Claude, OpenAI or Gemini key; "Saved" appears and the key shows up in Credential Manager → Windows Credentials as `Purrch:anthropic` / `openai` / `gemini`. Clearing the field removes it. The four provider pills (Claude, OpenAI, Gemini, Local (on-device)) wrap onto a second line if the panel is narrow. Under **Local (on-device)**, **Endpoint** defaults to `http://localhost:11434` and a blank **Model** means the first model Ollama has (else `llama3.2`); both are kept in `settings.json`, and Local needs no key.
- [ ] With no key, the Assistant says which key is missing and **Open Settings** opens Settings on AI. Pasting the key there swaps the message for the chat without reopening the Assistant.
- [ ] With a key, the suggestion chips and typed questions get answers; **bold** and `code` render. Text in any bubble can be selected with the mouse and copied with Ctrl+C; right-click → Copy copies the selection, or the whole message when nothing is selected. The mouse wheel still scrolls the chat over a bubble.
- [ ] While an answer is on its way the send button shows "…" and does nothing when clicked; it turns back into the arrow when the answer arrives.
- [ ] Asking "What PC am I on?" names the maker and model (e.g. "Dell Inc. XPS 13 9310"; a self-built PC may give the motherboard maker, or "unknown PC").
- [ ] The provider capsule switches between Claude, OpenAI, Gemini and Local (on-device); for Claude, "Web search" lets it answer questions about recent events. A question that needs several searches comes back as one answer rather than "Ask again to continue".
- [ ] A wrong key gives a clear "rejected the API key" message, and no internet gives "Couldn't reach ...".
- [ ] Local (on-device), with [Ollama](https://ollama.com) installed and `ollama pull llama3.2` done: Settings → AI says "Ollama is running · 1 model" and the Assistant answers with no key and no internet. Quitting Ollama: the status says "Ollama isn't running — …" and a question gets the same advice. Typing a model that isn't pulled (e.g. `qwen3`) in **Model** says "Model qwen3 isn't pulled — run `ollama pull qwen3`."
- [ ] Optional: with Foundry Local running, putting its address (shown by `foundry service status`) in **Endpoint** and a model it has loaded in **Model** makes Local answer through it; the status line says "The local server is running · N models".

**Translate**

- [ ] Typing "kal milte hain yaar" with "Detect language" translates it to English after a short pause and shows "Detected: Hinglish". Changing the target language translates again.
- [ ] Swap (with a fixed source language) swaps the languages and moves the translation into the input.
- [ ] Fix grammar, Improve, Tone (with a tone menu) and Shorten run on the button or Ctrl+Enter; changed words are highlighted and Accept puts the result in the input. Copy and Clear work.
- [ ] Ctrl+Enter also runs after clicking into the result pane or on the header, not only in the input.
- [ ] Part of a result can be selected and copied with Ctrl+C, and the changed-word highlighting stays.
- [ ] Switching mode fades and slides in the mode's name and its controls (languages, tone menu).
- [ ] The stats count "Bring fruit, e.g. apples. Mr. Smith paid 3.5 dollars." as 2 sentences.
- [ ] The provider capsule can use a different provider from the Assistant, including Local; a missing key shows where to add it, and pasting the key in Settings (with Translate still open behind it) brings the panes back straight away.

**Emoji**

- [ ] On Windows 10, no emoji in any category shows as an empty box (newer emoji that Windows 10 can't draw are left out); Windows 11 shows them all.
- [ ] Flags: on a Windows set to another display language (e.g. German), hovering a flag shows the country in that language ("Flag Deutschland De"), and searching for it in that language finds it.

**Units**

- [ ] Typing "5 km to mi", "100 f in c" or "2 cups -> ml" switches the category and both units by itself and shows the result in large accent digits. A bare "5" converts with the units already picked.
- [ ] The category chips along the top (scroll them sideways with the wheel) switch to that category's usual pair, for example Bars and PSI for Pressure. Every chip has an icon (Volume's is a drawn drop).
- [ ] The two unit menus list "Kilometers (km)" and so on, and swap exchanges them. The category and units are the same after reopening Purrch.
- [ ] The right card lists the value in every unit of the category; the target unit is in the accent colour and the source row is shaded. Clicking a row or the big result copies the number. Dragging across the big result selects part of it instead (Ctrl+C copies the selection).
- [ ] Very long results shrink to fit instead of being cut off; huge and tiny values show as 1.235e+15.

**Links**

- [ ] Typing words and pressing Enter searches the engine picked in the capsule (Google, DuckDuckGo, Bing or Perplexity) in the default browser. Typing "github.com" or "localhost:3000" turns the magnifier into a globe and Enter opens that address.
- [ ] The six default sites show their favicons; with no internet the first time they show their first letter instead and nothing crashes. After that the icons show at once after a restart, also offline, and `%APPDATA%\Purrch\favicons` holds one file per site.
- [ ] Hovering a tile outlines it in the accent and shows edit and remove buttons. Right-click offers Open, Copy Link, Edit… and Remove (in red).
- [ ] + opens "Add site" over the dimmed grid; Add stays disabled until the address looks valid, Enter in the address field saves, and an empty title becomes the host without "www.". Edits and the engine choice are kept after reopening Purrch.

**Message**

- [ ] Typing in the box updates the count (up to 80) and the preview, which scrolls the text in capitals as glowing dots. Clicking a preset fills the box and turns **Show in pill** on.
- [ ] With **Show in pill** on, the closed pill widens and the message scrolls across it smoothly, also while a timer runs or music plays (the message wins). Hovering the pill doesn't make the text jump back to the start.
- [ ] Turning it off, or switching off **Live activities** in Settings, brings back the timer or music. With an empty box the switch can't be turned on.
- [ ] The text and the switch are the same after quitting and reopening Purrch.

**Ambience**

- [ ] Each of the ten tiles plays its sound and fades in over about a second; tapping again fades it out. Several can play at once, and the header says how many.
- [ ] Each tile's slider and the master slider change the volume smoothly, with no clicks or crackle; the mouse wheel nudges them. Volumes are kept after a restart.
- [ ] **Stop all** fades everything out. Sounds keep playing with the panel closed or on another module.
- [ ] With no speakers or headphones (or audio turned off in Device Manager), tapping a tile does nothing and Purrch doesn't crash; `purrch.log` says "no audio output".
- [ ] Quitting from the tray while sounds play stops them at once. A few seconds after everything stops, Purrch's CPU use in Task Manager drops back to about zero.

**Shelf**

- [ ] Dragging a file from File Explorer onto the closed pill opens the panel on Shelf; dropping it there adds a tile with its thumbnail (pictures, videos, PDFs) or icon, a type tag, its name and size. While dragging, the area shows a dashed outline and "Release to add".
- [ ] **Add** opens a small menu: **Add files…** picks files with the Open dialog, **Add folder…** picks one or more folders. The panel stays open while the menu and the dialog are up. A file already on the shelf isn't added twice. Folders can be dropped too.
- [ ] Dragging a tile into a folder, an email or a chat box drops the file itself (copied, the original stays where it was). Double-click opens it.
- [ ] Hover shows Share and remove; right-click shows Open, Show in File Explorer, Copy, Share and Remove from Shelf. Copy then Ctrl+V in File Explorer pastes the file; for a picture, Ctrl+V in a chat pastes the image.
- [ ] Share opens the Windows share sheet (Nearby Sharing, Mail, ...). With two or more files, **Share all** sends them together.
- [ ] Removing a tile (x or the menu) shrinks and fades it, and the tiles after it slide over; **Clear** fades them all out.
- [ ] The shelf is the same after restarting Purrch. Rename a parked file, or move it to another folder on the same drive: the tile follows it (new name, still opens) the next time you open Shelf. Deleting it (Recycle Bin) or moving it to another drive removes it from the shelf.

**Captures**

- [ ] Screenshots from Win+PrtScn and the Snipping Tool (Pictures → Screenshots), and recordings from the Snipping Tool (Videos → Screen Recordings) or Game Bar (Videos → Captures) appear newest first. With Pictures in OneDrive this still works.
- [ ] A new screenshot appears within a second or two while the panel is open, growing in from smaller. All / Screenshots / Recordings filter the grid and show counts; switching shrinks away the tiles that go and pops in the ones that come.
- [ ] **Screenshot** closes the panel and opens the screen snip. The snip always ends up as one file in Pictures → Screenshots: on Windows 11 the Snipping Tool saves it, and on Windows 10 (or with the Snipping Tool's auto-save off) Purrch saves it as `Screenshot 2026-10-23 141503.png`. There is never a second copy. With no Screenshots folder yet, it is created. Cancelling the snip saves nothing.
- [ ] With no captures, the tab mentions Win+PrtScn and Win+Shift+S.
- [ ] Clicking a tile opens the preview: previous / next (the picture cross-fades), Copy, Open, Explorer and Delete work, and clicking outside the picture closes it. A recording shows a play button that opens your video player.
- [ ] Hover shows Copy, Show in File Explorer and Delete. Dragging a tile into a chat or folder drops the file.
- [ ] Delete shrinks the tile away and shows "Moved to Recycle Bin" with **Undo** for 6 seconds; Undo brings it back. After that (or after leaving the tab) the file is in the Recycle Bin, not gone for good.
- [ ] **Select** lets you click several captures, then Copy or Delete them together; Select all / Deselect all work.
- [ ] With no Screenshots folder at all, the tab says "No screenshots yet" and doesn't crash; **Open folder** opens Pictures instead.

**File Converter**

- [ ] Clicking the dashed box opens a file picker; choosing several images shows them as thumbnails, with an x on each and **Clear**. The panel stays open while the picker is up.
- [ ] With the panel pinned, dragging images from File Explorer onto the box highlights it and adds them. Non-image files are ignored. A camera RAW file (.cr2, .nef, .arw, .dng) or another format with a codec installed (Raw Image Extension from the Store) is accepted too, even though the picker's "Images" filter doesn't list it.
- [ ] Convert to JPEG, PNG, HEIC, TIFF, BMP, GIF and PDF. Each result lands next to the original as `name-converted.ext` (`name-converted 2.ext` the second time), and opens in Photos. Several images to PDF make one `name-combined.pdf`, one page per image.
- [ ] HEIC: with HEVC Video Extensions installed, the result opens in Photos and a lower quality makes it smaller. Without them, the result says "HEIC needs HEVC Video Extensions from the Microsoft Store." with a **Get HEVC** button that opens the Store page for HEVC Video Extensions from Device Manufacturer.
- [ ] The quality slider changes JPEG, HEIC and PDF size; it greys out for PNG, TIFF and BMP ("Lossless") and GIF ("256 colours"). Max size 1280 makes the longer side 1280 pixels; small images are never enlarged.
- [ ] Click the slider (or Tab to it): an accent ring shows on the knob, Left/Right move 1%, Page Up/Down 10%, Home/End to 10% and 100%; the mouse wheel moves it 5% a notch. Narrator reads it as "Quality, slider, 80".
- [ ] A PNG with transparency converted to JPEG has a white background, not black. A phone photo taken in portrait stays upright.
- [ ] New results spring in at the top. Each shows before and after sizes with a green "-x%" (or orange "+x%"); a long name loses its middle, not its extension. **Show in folder** selects the file in File Explorer; dragging a result into a chat or folder drops the file.
- [ ] A HEIC or WebP file converts if Microsoft's HEIF or Webp Image Extensions are installed, and otherwise shows a clear message instead of failing silently. A renamed text file shows "Couldn't read image".
- [ ] Format, max size and quality are remembered after restarting Purrch.

**Downloads**

- [ ] The list shows the 20 newest files in your Downloads folder, also when it has been moved to another drive (File Explorer, Downloads, Properties, Location).
- [ ] Download a big file in Chrome, Edge and Firefox. A "Downloading" row appears within a second with size so far, speed and a moving bar; the closed pill shows a download icon on the left and a filling ring on the right. Two at once show "2 downloads" in the pill.
- [ ] Chrome, Edge, Brave and Vivaldi (any profile): within a few seconds the row shows the real file name (not "Unconfirmed 123456"), a real percentage, "120 MB of 480 MB", and time left, and the pill's ring matches the browser's own progress. Firefox shows "…" and an estimated bar, as before.
- [ ] When it finishes, the ring fills for about three seconds, and the file appears under "Just finished" or with a green DONE tag in "Recent". Cancelling a download removes its row without a DONE tag.
- [ ] Firefox: no empty duplicate of the file shows in "Recent" while it downloads.
- [ ] Image rows show a thumbnail and a copy button (Ctrl+V then pastes the picture into Paint or a chat, or the file into a folder); other files show their File Explorer icon and folders a real folder icon, sharp at 150% and 200% scaling. Long names lose their middle, so the extension stays visible. Show in File Explorer, Open and the bin work. Right-click shows the same.
- [ ] The bin, or **Select** then **Delete**, shrinks the rows away and the list closes up; the files go to the Recycle Bin after a few seconds, and **Undo** in that time keeps them. Quitting Purrch during those seconds still recycles them.
- [ ] **Select** grows a check circle in on each row and the buttons give way; ticking a row pops its check; **Done** fades the buttons back.
- [ ] If the Downloads folder can't be read (for example, its location points at a removed drive), the tab says "Can't read Downloads" with **Open in File Explorer** and **Try again**.
- [ ] With a timer running, the pill shows the timer instead of the download; with music playing, the download wins.

**Calendar**

- [ ] With no calendars, the left card says "No calendars connected". **Add calendar** opens the Calendars card over the dimmed module.
- [ ] Paste a Google Calendar "Secret address in iCal format" link and press Enter: it appears in the list with a colour dot and, after a moment, the calendar's own name. A `webcal://` link (iCloud public calendar) works too; a normal web page link says "This link isn't an iCal calendar".
- [ ] **Open .ics file…** picks a file exported from Outlook or another app; the panel stays open while the file dialog is up.
- [ ] "Next 7 days" lists events grouped under TODAY (in the accent colour), TOMORROW and then "FRIDAY, 24 OCT", with a coloured bar per calendar, "All day" or "9:00 – 9:30", and a pin icon whose tooltip shows the location. Times match Google/Outlook, including a weekly meeting, a moved or cancelled single occurrence, and an event in another time zone.
- [ ] Turn off Wi-Fi and reopen Purrch: the agenda still shows the last copy, and the Calendars card says "Offline: showing the last copy".
- [ ] Reminders: typing in "Add a reminder" and pressing Enter adds one. Clicking its ring fills it, strikes the title through and the row goes. To-dos in an .ics file (VTODO) show with their due time, red once overdue.
- [ ] Removing a calendar (bin icon) removes its events at once. Everything is still there after quitting and reopening Purrch.

**Calendar: Microsoft and Google accounts**

Purrch has no client IDs of its own, so register one app with each provider first (both are free):

- Microsoft: [portal.azure.com](https://portal.azure.com) → **App registrations** → **New registration**. Any name; **Supported account types**: "Accounts in any organizational directory and personal Microsoft accounts"; **Redirect URI**: platform "Public client/native (mobile & desktop)", `http://localhost`. Then **API permissions** → **Add a permission** → **Microsoft Graph** → **Delegated permissions**: `Calendars.Read`, `Tasks.ReadWrite`, `offline_access`, `User.Read`. Copy the **Application (client) ID** from **Overview**. No client secret is needed.
- Google: [console.cloud.google.com](https://console.cloud.google.com) → create a project → **APIs & Services** → **Library**: enable **Google Calendar API** and **Google Tasks API**. **OAuth consent screen**: External, fill in the app name and your email, and add your Google address under **Test users**. **Credentials** → **Create credentials** → **OAuth client ID** → application type **Desktop app**. Copy the client ID and the client secret.

- [ ] Settings → Integrations → Calendar shows the steps above, **Microsoft client ID**, **Google client ID** and **Google client secret**, each with a **Sign in** button. With no client ID, the row says "Add your Microsoft client ID in Settings → Integrations." A Microsoft ID that isn't a GUID, or a Google ID not ending in `.apps.googleusercontent.com`, says so.
- [ ] **Sign in** next to Microsoft opens the default browser on Microsoft's sign-in page; the row says "Finish signing in in your browser…" with **Cancel**. After signing in (a personal outlook.com account and a work or school account both work), the browser says "Signed in to Purrch" and the row shows "Microsoft · you@outlook.com" with "3 calendars, 2 task lists" and **Sign out**. `%APPDATA%\Purrch\msal.cache` exists and is not readable text.
- [ ] **Sign in** next to Google does the same through Google's page (click through "Google hasn't verified this app", then allow calendar and tasks). The row shows the Google address. Credential Manager has `Purrch:google-client-secret` and `Purrch:google-refresh-token`.
- [ ] The Calendars card (gear icon in Calendar, or **Add calendar**) lists Microsoft and Google with the account, then each calendar and task list in its own colour with a switch; task lists say "Tasks". Turning a calendar off removes its events from the agenda at once, and it stays off after a restart. iCal calendars are still listed below.
- [ ] "Next 7 days" shows account events with iCal events, sorted together, with each calendar's colour bar, the right times (also for an event created in another time zone), all-day events as "All day", and the location pin. A recurring Outlook or Google meeting shows each occurrence; a cancelled occurrence doesn't show.
- [ ] Reminders lists open To Do tasks and Google Tasks in their list's colour with the due day ("Today", "Tomorrow", "Fri 24 Oct"), red once the day has passed, sorted with .ics to-dos by due date. Ticking one strikes it through, the row goes, and the task is completed in Outlook/To Do or Google Tasks.
- [ ] Ticking a task with Wi-Fi off: the row goes, then comes back, and a Windows notification says "Reminder not completed" with the reason.
- [ ] A new event in Outlook or Google Calendar shows within 5 minutes with the Calendar tab open, or straight away when the panel is opened a minute or more after the last read. Home's event chip shows account events too, and with only an account signed in it no longer says "Connect".
- [ ] Turn off Wi-Fi and reopen Purrch: account events stay, and the account row says "Offline: showing the last copy".
- [ ] Errors are shown in the account row: declining consent says "Sign-in was declined…"; a made-up Microsoft client ID says "Microsoft doesn't know this client ID…"; a wrong Google secret says "Google doesn't accept this client ID or secret…"; Google Tasks API not enabled says "Turn on the Google Tasks API…" while calendars still load; revoking Purrch at account.microsoft.com or myaccount.google.com/permissions says "…expired or was withdrawn. Sign in again." with **Sign in**. Changing a client ID in Settings asks to sign in again.
- [ ] **Sign out** removes the account's events and calendars at once, deletes `msal.cache` (Microsoft) or the Google refresh token, and Purrch no longer appears at myaccount.google.com/permissions. `purrch.log` contains no tokens or secrets.

**Weather**

- [ ] With location on (Windows Settings → Privacy & security → Location, including "Let desktop apps access your location"), Weather shows your town, the current temperature with a colour icon, the condition, H and L, and Feels, Wind and Humid along the bottom. Windows 11 may ask first whether Purrch can use your location.
- [ ] "Next 12 hours" starts with "Now" in the accent colour, then hours like "3PM" (or "15" with a 24-hour clock). "This week" starts with "Today" and has a bar per day showing its low-to-high range.
- [ ] With location off, it says "Location is off for desktop apps…" with **Try again** and **Location settings**; the second opens the Windows location page.
- [ ] Settings → Integrations → Weather: typing a city (e.g. "Pune") switches the forecast to it a moment after you stop typing; an unknown city says "Couldn't find …". Clearing it goes back to your location. **Fahrenheit** switches to °F and mph at once.
- [ ] The refresh arrow spins while loading; its tooltip says when the forecast was updated. With no internet, the last forecast stays on screen.

**Screen Time**

- [ ] After a few minutes of normal use, "Top apps today" lists the apps you used with their File Explorer icon, a friendly name ("Google Chrome", "Visual Studio Code", "File Explorer"), the time, and a bar relative to the top app. The header says how many apps.
- [ ] Store apps (Settings, Calculator, Photos) are counted as themselves, not as "Application Frame Host".
- [ ] Two different `python.exe` (say from two virtual environments, or a Store and a python.org install) are listed separately.
- [ ] After updating from an older build, apps already in `screentime.json` keep today's time instead of starting again from zero, and nothing is lost from earlier days.
- [ ] The big total and today's bar grow while the panel stays open on Screen Time (it updates every 5 seconds). Switching between two apps for a minute each splits the time between them.
- [ ] Leave the PC untouched for more than 2 minutes, or lock it with Win+L: that time is not counted. Watching a video without touching anything also stops counting after 2 minutes, as on the Mac.
- [ ] Sleep and wake the PC: the time asleep is not counted.
- [ ] Let the display turn off (Settings → System → Power, screen off after 1 minute) while a video keeps the PC awake, or press the monitor's own power-saving shortcut: the time with the screen dark is not counted.
- [ ] The week chart shows seven bars with weekday letters, today's in the full accent, hour lines and labels on the right, and "Avg" in the header is the average of the days that have any time.
- [ ] Quit from the tray and reopen: today's numbers are still there. On a new day the list starts empty and yesterday's bar stays. Days older than a week drop off.
- [ ] Task Manager shows Purrch's CPU at about zero while it tracks in the background.

**Claude Code**

- [ ] Without Claude Code (no `%USERPROFILE%\.claude\projects` folder), the tab says "No Claude Code data yet" and doesn't crash.
- [ ] With Claude Code used today, the tab says "Reading transcripts…" briefly, then shows today's tokens in large accent digits, Output, Tool calls and Sessions, the folder name of the project used last, and "active now" or "5m ago".
- [ ] Tokens are input, output, cache-write and cache-read added up, each message counted once: quitting and reopening Purrch, or resuming a session with `claude --resume`, doesn't make today's total jump.
- [ ] "Last 16 weeks" shows a grid with a square per day, busier days in a stronger accent, today outlined, and future days of this week empty. Hovering a square shows its date and tokens. The header shows the day streak with a lit flame, and the footer the total and active days.
- [ ] While a Claude Code session runs, the numbers go up within a minute with the panel open on Claude Code; a small spinning arrow in the Today card shows while it reads.
- [ ] With months of transcripts, the first read takes a few seconds at most and the panel stays responsive; later reads are quick.

**Revenue**

- [ ] With no Stripe key, Revenue says "Connect Stripe"; **Open Settings** opens Settings on Integrations.
- [ ] Settings → Integrations → Revenue (Stripe): paste a restricted key (`rk_live_...` or `rk_test_...`) with Charges: Read. "Saved" appears and the key shows up in Credential Manager → Windows Credentials as `Purrch:stripe`. Clearing the field says "Removed" and deletes it.
- [ ] With a test-mode key and a few test payments made today, "Today" shows the total in large accent digits ("$1,250", or "$12.50" when there are cents) and "3 sales · USD". "7 days" has a bar per day with the weekday's first letter, today's bar brighter and the week's total on the right; hovering a bar shows "Monday: $40".
- [ ] "Sales" lists today's payments newest first, with the customer's email (or the description, or the name), the time and the amount. A fully refunded payment is left out; a partly refunded one shows what was kept. No payments today says "No sales yet today".
- [ ] Changing Currency to `eur` (or `jpy`, which has no cents) shows only payments in that currency, with its symbol, after a moment.
- [ ] A made-up key says "Stripe rejected the key…"; a restricted key without Charges: Read says "This key can't read charges…". Both offer **Try again** and **Open Settings**.
- [ ] A new test payment appears within about a minute while the tab is open; the refresh arrow in "Sales" fetches at once and spins meanwhile.
- [ ] Turn off Wi-Fi and reopen Purrch: the last figures stay on screen, with a warning sign next to the time; its tooltip says "Couldn't reach Stripe… Showing the last copy." With no copy yet, the Sales card says "Couldn't load Stripe".

**Analytics**

- [ ] With no Plausible key or no site, Analytics says "Connect Plausible" and which one is missing; **Open Settings** opens Settings on Integrations.
- [ ] Settings → Integrations → Analytics (Plausible): paste a Stats API key (Plausible → Account settings → API keys), and type the site domain exactly as it is in Plausible (e.g. `example.com`). The key shows up in Credential Manager as `Purrch:plausible`; clearing it removes it.
- [ ] The top row shows LIVE with a pulsing green dot and the visitors of the last few minutes in large accent digits, then today's Visitors, Pageviews, Bounce and Visit (average length like "1:24"). Large numbers show as "12.3K".
- [ ] "Today by hour" has a bar per hour from 00 to 23, the current hour brighter, with light grid lines and counts on the left. "Top pages" and "Top sources" list five rows each with a shaded bar for their share; visits with no referrer show as "Direct / none".
- [ ] Open the site in another browser: LIVE goes up within about 30 seconds while the tab is open.
- [ ] A self-hosted Plausible: putting its address in Host (with or without `https://`) loads its stats; a wrong key or a site the key can't see shows Plausible's own message followed by "Check Settings → Integrations.", and a host with no Plausible says "No Plausible Stats API at this host". **Retry** and **Open Settings** are offered.
- [ ] Turn off Wi-Fi and reopen Purrch: the last stats stay on screen, with a warning sign before the time in "Today by hour" whose tooltip says "Couldn't reach plausible.io…".

**Tray, startup and full screen**

- [ ] Tray icon: left-click opens the panel. Right-click shows **Open Purrch**, **Settings** and **Quit Purrch**, and each one works.
- [ ] Opening the exe a second time opens the panel of the copy that is already running, and doesn't start a second copy.
- [ ] Release build: after the first run, Purrch is listed under Task Manager → Startup apps and starts again after signing out and back in. Turning **Launch at startup** off in Settings removes it, and it stays off.
- [ ] A full-screen YouTube video (F11 or the video's full-screen button), a full-screen game and a PowerPoint slideshow all hide the pill. It comes back when they leave full screen. A normal maximised window must **not** hide it.

## Where Purrch keeps things

| What | Where |
| --- | --- |
| Settings | `%APPDATA%\Purrch\settings.json` |
| Home's Launch apps | `%APPDATA%\Purrch\launcher.json` |
| Translate languages, mode and provider | `%APPDATA%\Purrch\translate.json` |
| Timer lengths, pomodoros done, water | `%APPDATA%\Purrch\timers.json`, `water.json` |
| Clipboard history | `%APPDATA%\Purrch\clipboard.json`, images in `clipboard-images\` |
| Notes, Scratchpad | `%APPDATA%\Purrch\notes.json`, `scratchpad.json` |
| Units category and the two units picked | `%APPDATA%\Purrch\units.json` |
| Links: saved sites and search engine | `%APPDATA%\Purrch\links.json` |
| Links: site icons | `%APPDATA%\Purrch\favicons\` |
| Water reminders still to come (up to 16) | Windows' notification schedule |
| Message text and whether it shows | `%APPDATA%\Purrch\message.json` |
| Ambience volumes | `%APPDATA%\Purrch\ambience.json` |
| Shelf (paths and file IDs only; the files stay where they are) | `%APPDATA%\Purrch\shelf.json` |
| Captures (read from; the Screenshot button's snip is saved to Pictures → Screenshots when Windows doesn't save it) | Pictures → Screenshots, Videos → Screen Recordings, Videos → Captures |
| File Converter format, max size and quality | `%APPDATA%\Purrch\converter.json` |
| Converted images | Next to the originals, as `name-converted.ext` |
| Downloads | Nothing stored; it reads your Downloads folder, and while a Chromium download runs, a copy of the browser's `History` in `%TEMP%` that is deleted straight after. Deleted files go to the Recycle Bin |
| Calendar links and files, reminders added in Purrch, feed to-dos ticked off, signed-in accounts (email and calendars switched off, no tokens) | `%APPDATA%\Purrch\calendar.json`, last download of each link in `calendar-cache\` |
| Last read of each Microsoft and Google account (calendars, the week's events, open tasks) | `%APPDATA%\Purrch\calendar-cache\microsoft.json`, `google.json` |
| Microsoft and Google client IDs | `%APPDATA%\Purrch\settings.json` |
| Microsoft sign-in (MSAL's token cache, encrypted with DPAPI for your Windows user) | `%APPDATA%\Purrch\msal.cache` |
| Last weather forecast (city and °F are in `settings.json`) | `%APPDATA%\Purrch\weather.json` |
| Screen Time: seconds per app per day for the last 7 days, and app names and exe paths | `%APPDATA%\Purrch\screentime.json` |
| Claude Code (read from, nothing saved) | `%USERPROFILE%\.claude\projects` |
| Last Stripe figures (the currency is in `settings.json`, the key in Credential Manager) | `%APPDATA%\Purrch\revenue.json` |
| Last Plausible stats (site and host are in `settings.json`, the key in Credential Manager) | `%APPDATA%\Purrch\analytics.json` |
| Now Playing mode (Audio / Video), Video on hover, preview quality | `%APPDATA%\Purrch\settings.json` |
| Browser extension (only after **Connect browser**) | `%APPDATA%\Purrch\extension\` |
| Native messaging host manifest (only after **Connect browser**) | `%APPDATA%\Purrch\bridge\com.purrch.bridge.json` |
| Native messaging host registration (only after **Connect browser**) | `HKCU\Software\Google\Chrome`, `Microsoft\Edge`, `BraveSoftware\Brave-Browser`, `Vivaldi` and `Chromium`, each `\NativeMessagingHosts\com.purrch.bridge` |
| Embedded YouTube player page (written when the live video is first needed) | `%APPDATA%\Purrch\player\player.html` |
| Embedded player's WebView2 cache and cookies | `%LOCALAPPDATA%\Purrch\WebView2\` |
| Log (startup and unhandled errors) | `%APPDATA%\Purrch\purrch.log` |
| API keys, the Google client secret and Google sign-in | Windows Credential Manager → Windows Credentials, entries named `Purrch:<name>` (`anthropic`, `openai`, `gemini`, `stripe`, `plausible`, `google-client-secret`, `google-refresh-token`) |
| Launch at startup | `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`, value `Purrch` |

To reset Purrch completely, turn off the water reminder in Timers, sign out of Microsoft and Google in Settings → Integrations, click **Disconnect** under Settings → Behaviour → YouTube in the browser (if connected), quit Purrch from the tray, delete `%APPDATA%\Purrch`, `%LOCALAPPDATA%\Purrch` and the `Purrch:` entries in Credential Manager, and turn off **Purrch** in Task Manager → Startup apps (or delete the Run value).

## Project layout

```
windows/
  Purrch.sln
  extension/               the browser extension (Manifest V3), embedded in Purrch.exe for Connect browser
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
| `BrowserMediaService` (Apple Events) | `Features/Media/BrowserMediaService`, with `BrowserBridge` (native messaging over a named pipe), `BrowserSetup` (Connect browser) and the extension in `windows/extension` |
| `EmbeddedYouTubePlayer` (WKWebView) | `Features/Media/EmbeddedPlayerHost` (WebView2CompositionControl) |
| `BrowserVideos` (`LiveVideoPlayer`, `VideoPeekView`, `VideoTheaterView`) | `Features/Media/LiveVideoPlayer`, `Features/Media/BrowserVideos`, `Interface/VideoViews` |

## Known limitations

- The pill always sits on the primary screen.
- Opening the panel from the tray, a second launch or the welcome screen gives it the keyboard straight away. Opening it by hovering doesn't, because on Windows that would take focus from the app you are in; click inside first to type. When the panel closes, keyboard focus goes back to the app you were in.
- The closed pill shows a message first, then a running timer, then a download, then music.
- Firefox doesn't record the expected size of a download anywhere Purrch can read, so its progress is estimated and the percentage shows as "…". Chrome, Edge, Brave and Vivaldi show real progress from their history, which they update a few seconds after a download starts (the estimate covers those seconds). Other Chromium browsers (Opera, Arc, Chrome Beta) are estimated too.
- HEIC output uses Windows' own HEIF encoder, which needs HEVC Video Extensions from the Microsoft Store (the free "from Device Manufacturer" listing, which the button opens). Without it the File Converter says so and links to the Store.
- The Shelf follows a renamed or moved file only on the same drive (as NTFS file IDs do); moved to another drive, it drops off the shelf.
- Dragging files onto the closed pill opens the Shelf. To drop images on the File Converter, pin the panel first: pressing the mouse in File Explorer to start the drag closes an unpinned panel.
- The exe is not code-signed, so SmartScreen warns on first run.
- Water reminders are queued with Windows eight hours ahead, and topped up while Purrch runs. If Purrch isn't started for longer than that, they pause until it runs again.
- Calendar reads Microsoft (Outlook calendars and To Do) and Google (Calendar and Tasks) accounts with client IDs you register yourself (see "Calendar: Microsoft and Google accounts" above); Purrch ships none. One account of each kind at a time. Events are read-only, as on the Mac; only ticking off a task writes back. iCal links and .ics files still work alongside, but show events without changing them, and a published link can lag Google or Outlook by a few hours. Windows' own calendar store needs a packaged (MSIX) app.
- While a Google OAuth consent screen is in **Testing**, Google ends the sign-in after 7 days and Purrch asks to sign in again. **Publish app** on the consent screen keeps it; Google then shows an "unverified app" warning at sign-in, which is fine for your own use.
- Screen Time counts the app in front, so a window you are only reading or watching counts until the 2-minute idle limit, as on the Mac. Store apps are grouped by their app ID and other apps by the exe's full path. Store apps recorded by an older build stay under their old exe-name entry for the days already recorded.
- Windows doesn't tell apps how hot the machine is, so System's **Thermal** row is estimated from CPU throttling (how far the processor's speed limit, or its speed under load, sits below its maximum). It never says Critical, and a PC that throttles for power rather than heat shows the same.
- The battery cycle count comes from the battery driver through WMI; many drivers don't keep one, and then it shows "—".
- Claude Code reads only `%USERPROFILE%\.claude\projects`, as the Mac app reads `~/.claude/projects`; a `CLAUDE_CONFIG_DIR` set elsewhere isn't followed. A transcript line still being written is counted on the next read, a minute later.
- Revenue counts charges in the one currency set in Settings, and reads at most 500 charges from the last seven days, as the Mac app does.
- The Mac app's default AI provider, Apple's on-device model, has no Windows equivalent that an unpackaged app can use (Windows' own Phi Silica model needs a packaged MSIX app and a Copilot+ PC). **Local (on-device)** stands in for it: it needs [Ollama](https://ollama.com) or Microsoft Foundry Local installed and running, with a model pulled, and its answers are only as good as that model. Claude stays the default provider on Windows. Local keeps the chat history by sending it with each question, as the cloud providers do.
- YouTube in the browser works with Chrome, Edge, Brave, Vivaldi and Chromium through Purrch's extension. Firefox and Safari are not supported. Arc, Opera and other Chromium browsers may load the extension but aren't registered as hosts.
- The extension is loaded unpacked (Developer mode) until it is published in the Chrome Web Store and Edge Add-ons; Chrome may remind you about developer-mode extensions at startup. It must be loaded once per browser profile.
- The smooth picture is YouTube's embedded player in WebView2, muted and kept in step with the tab, as on the Mac. It needs the WebView2 Runtime (built into Windows 11; most Windows 10 PCs have it). Without it, or for videos whose owner disabled embedding, the picture comes from frames the extension grabs from the tab, which are less smooth and may freeze while the tab is in the background (the browser stops drawing hidden videos).
- The embedded player is a `WebView2CompositionControl`: the pill window is transparent (layered), where the ordinary WebView2 control doesn't draw at all, and the composition control draws into WPF so it gets rounded corners and WPF controls on top. Copying its frames costs some CPU while a live video is on screen.
- While nothing in Purrch shows a video, the browser reports positions every couple of seconds, so the rows' times move in steps; the live player gets them several times a second.
- Analytics uses Plausible's Stats API v1, the same as the Mac app. "Live" is Plausible's realtime count of visitors in the last 5 minutes.
