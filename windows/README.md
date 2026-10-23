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

**Units**

- [ ] Typing "5 km to mi", "100 f in c" or "2 cups -> ml" switches the category and both units by itself and shows the result in large accent digits. A bare "5" converts with the units already picked.
- [ ] The category chips along the top (scroll them sideways with the wheel) switch to that category's usual pair, for example Bars and PSI for Pressure. Every chip has an icon (Volume's is a drawn drop).
- [ ] The two unit menus list "Kilometers (km)" and so on, and swap exchanges them. The category and units are the same after reopening Purrch.
- [ ] The right card lists the value in every unit of the category; the target unit is in the accent colour and the source row is shaded. Clicking a row or the big result copies the number.
- [ ] Very long results shrink to fit instead of being cut off; huge and tiny values show as 1.235e+15.

**Links**

- [ ] Typing words and pressing Enter searches the engine picked in the capsule (Google, DuckDuckGo, Bing or Perplexity) in the default browser. Typing "github.com" or "localhost:3000" turns the magnifier into a globe and Enter opens that address.
- [ ] The six default sites show their favicons; with no internet they show their first letter instead and nothing crashes.
- [ ] Hovering a tile outlines it in the accent and shows edit and remove buttons. Right-click offers Open, Copy Link, Edit… and Remove.
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
- [ ] **Add** picks files with the Open dialog. A file already on the shelf isn't added twice. Folders can be dropped too.
- [ ] Dragging a tile into a folder, an email or a chat box drops the file itself (copied, the original stays where it was). Double-click opens it.
- [ ] Hover shows Share and remove; right-click shows Open, Show in File Explorer, Copy, Share and Remove from Shelf. Copy then Ctrl+V in File Explorer pastes the file; for a picture, Ctrl+V in a chat pastes the image.
- [ ] Share opens the Windows share sheet (Nearby Sharing, Mail, ...). With two or more files, **Share all** sends them together.
- [ ] The shelf is the same after restarting Purrch. A file deleted or moved away in File Explorer disappears from the shelf the next time you open it.

**Captures**

- [ ] Screenshots from Win+PrtScn and the Snipping Tool (Pictures → Screenshots), and recordings from the Snipping Tool (Videos → Screen Recordings) or Game Bar (Videos → Captures) appear newest first. With Pictures in OneDrive this still works.
- [ ] A new screenshot appears within a second or two while the panel is open. All / Screenshots / Recordings filter the grid and show counts.
- [ ] **Screenshot** closes the panel and opens the screen snip. On Windows 11 the snip is saved and shows up; on Windows 10 the snip only goes to the clipboard.
- [ ] Clicking a tile opens the preview: previous / next, Copy, Open, Explorer and Delete work, and clicking outside the picture closes it. A recording shows a play button that opens your video player.
- [ ] Hover shows Copy, Show in File Explorer and Delete. Dragging a tile into a chat or folder drops the file.
- [ ] Delete shows "Moved to Recycle Bin" with **Undo** for 6 seconds; Undo brings it back. After that (or after leaving the tab) the file is in the Recycle Bin, not gone for good.
- [ ] **Select** lets you click several captures, then Copy or Delete them together; Select all / Deselect all work.
- [ ] With no Screenshots folder at all, the tab says "No screenshots yet" and doesn't crash; **Open folder** opens Pictures instead.

**File Converter**

- [ ] Clicking the dashed box opens a file picker; choosing several images shows them as thumbnails, with an x on each and **Clear**. The panel stays open while the picker is up.
- [ ] With the panel pinned, dragging images from File Explorer onto the box highlights it and adds them. Non-image files are ignored.
- [ ] Convert to JPEG, PNG, TIFF, BMP, GIF and PDF. Each result lands next to the original as `name-converted.ext` (`name-converted 2.ext` the second time), and opens in Photos. Several images to PDF make one `name-combined.pdf`, one page per image.
- [ ] The quality slider changes JPEG and PDF size; it greys out for PNG, TIFF and BMP ("Lossless") and GIF ("256 colours"). Max size 1280 makes the longer side 1280 pixels; small images are never enlarged.
- [ ] A PNG with transparency converted to JPEG has a white background, not black. A phone photo taken in portrait stays upright.
- [ ] Each result shows before and after sizes with a green "-x%" (or orange "+x%"). **Show in folder** selects the file in File Explorer; dragging a result into a chat or folder drops the file.
- [ ] A HEIC or WebP file converts if Microsoft's HEIF or Webp Image Extensions are installed, and otherwise shows a clear message instead of failing silently. A renamed text file shows "Couldn't read image".
- [ ] Format, max size and quality are remembered after restarting Purrch.

**Downloads**

- [ ] The list shows the 20 newest files in your Downloads folder, also when it has been moved to another drive (File Explorer, Downloads, Properties, Location).
- [ ] Download a big file in Chrome, Edge and Firefox. A "Downloading" row appears within a second with size so far, speed and a moving bar; the closed pill shows a download icon on the left and a filling ring on the right. Two at once show "2 downloads" in the pill.
- [ ] When it finishes, the ring fills for about three seconds, and the file appears under "Just finished" or with a green DONE tag in "Recent". Cancelling a download removes its row without a DONE tag.
- [ ] Firefox: no empty duplicate of the file shows in "Recent" while it downloads.
- [ ] Image rows show a thumbnail and a copy button (Ctrl+V then pastes the picture into Paint or a chat, or the file into a folder); other files show their File Explorer icon. Show in File Explorer, Open and the bin work. Right-click shows the same.
- [ ] The bin, or **Select** then **Delete**, moves files to the Recycle Bin after a few seconds; **Undo** in that time keeps them. Quitting Purrch during those seconds still recycles them.
- [ ] With a timer running, the pill shows the timer instead of the download; with music playing, the download wins.

**Calendar**

- [ ] With no calendars, the left card says "No calendars connected". **Add calendar** opens the Calendars card over the dimmed module.
- [ ] Paste a Google Calendar "Secret address in iCal format" link and press Enter: it appears in the list with a colour dot and, after a moment, the calendar's own name. A `webcal://` link (iCloud public calendar) works too; a normal web page link says "This link isn't an iCal calendar".
- [ ] **Open .ics file…** picks a file exported from Outlook or another app; the panel stays open while the file dialog is up.
- [ ] "Next 7 days" lists events grouped under TODAY (in the accent colour), TOMORROW and then "FRIDAY, 24 OCT", with a coloured bar per calendar, "All day" or "9:00 – 9:30", and a pin icon whose tooltip shows the location. Times match Google/Outlook, including a weekly meeting, a moved or cancelled single occurrence, and an event in another time zone.
- [ ] Turn off Wi-Fi and reopen Purrch: the agenda still shows the last copy, and the Calendars card says "Offline: showing the last copy".
- [ ] Reminders: typing in "Add a reminder" and pressing Enter adds one. Clicking its ring fills it, strikes the title through and the row goes. To-dos in an .ics file (VTODO) show with their due time, red once overdue.
- [ ] Removing a calendar (bin icon) removes its events at once. Everything is still there after quitting and reopening Purrch.

**Weather**

- [ ] With location on (Windows Settings → Privacy & security → Location, including "Let desktop apps access your location"), Weather shows your town, the current temperature with a colour icon, the condition, H and L, and Feels, Wind and Humid along the bottom. Windows 11 may ask first whether Purrch can use your location.
- [ ] "Next 12 hours" starts with "Now" in the accent colour, then hours like "3PM" (or "15" with a 24-hour clock). "This week" starts with "Today" and has a bar per day showing its low-to-high range.
- [ ] With location off, it says "Location is off for desktop apps…" with **Try again** and **Location settings**; the second opens the Windows location page.
- [ ] Settings → Integrations → Weather: typing a city (e.g. "Pune") switches the forecast to it a moment after you stop typing; an unknown city says "Couldn't find …". Clearing it goes back to your location. **Fahrenheit** switches to °F and mph at once.
- [ ] The refresh arrow spins while loading; its tooltip says when the forecast was updated. With no internet, the last forecast stays on screen.

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
| Units category and the two units picked | `%APPDATA%\Purrch\units.json` |
| Links: saved sites and search engine | `%APPDATA%\Purrch\links.json` |
| Message text and whether it shows | `%APPDATA%\Purrch\message.json` |
| Ambience volumes | `%APPDATA%\Purrch\ambience.json` |
| Shelf (paths only; the files stay where they are) | `%APPDATA%\Purrch\shelf.json` |
| Captures (read from, nothing saved) | Pictures → Screenshots, Videos → Screen Recordings, Videos → Captures |
| File Converter format, max size and quality | `%APPDATA%\Purrch\converter.json` |
| Converted images | Next to the originals, as `name-converted.ext` |
| Downloads | Nothing stored; it reads your Downloads folder. Deleted files go to the Recycle Bin |
| Calendar links and files, reminders added in Purrch, feed to-dos ticked off | `%APPDATA%\Purrch\calendar.json`, last download of each link in `calendar-cache\` |
| Last weather forecast (city and °F are in `settings.json`) | `%APPDATA%\Purrch\weather.json` |
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
- The closed pill shows a message first, then a running timer, then a download, then music.
- Chrome, Edge and Firefox don't write the expected size of a download next to the file, so download progress is estimated (as it is for Chrome on the Mac) and the percentage shows as "…".
- Dragging files onto the closed pill opens the Shelf. To drop images on the File Converter, pin the panel first: pressing the mouse in File Explorer to start the drag closes an unpinned panel.
- The exe is not code-signed, so SmartScreen warns on first run.
- Calendar reads iCal links and .ics files, so it shows events but can't change them, and a published link can lag Google or Outlook by a few hours. Windows' own calendar store needs a packaged (MSIX) app.
