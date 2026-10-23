# Purrch browser extension (Windows)

Lets Purrch for Windows list and control the YouTube videos open in Chrome, Edge, Brave, Vivaldi or Chromium, the way the Mac app does through Apple Events: play / pause, pause all, back and forward 10 seconds, jump along the timeline, next video, show the tab, YouTube's quality, and a live picture for the pill.

It works with YouTube watch pages, Shorts and YouTube Music. Firefox and Safari are not supported.

## Install

Purrch ships these files inside `Purrch.exe` and writes them out only when you ask:

1. In Purrch, open **Settings → Behaviour** and click **Connect browser** under **YouTube in the browser**. Purrch writes the extension to `%APPDATA%\Purrch\extension` and registers itself as the extension's native messaging host for the current Windows user.
2. In the browser, open `chrome://extensions` (Edge: `edge://extensions`, Brave: `brave://extensions`, Vivaldi: `vivaldi://extensions`).
3. Turn on **Developer mode**.
4. Click **Load unpacked** and pick `%APPDATA%\Purrch\extension` (**Copy path** in Purrch's Settings puts it on the clipboard; paste it into the folder picker's address bar).

Within a few seconds Settings says **Connected · Chrome** (or the browser you used). Repeat steps 2 to 4 in each browser you want.

The extension stays loaded unpacked until it is published in the Chrome Web Store and Edge Add-ons. Its ID is pinned by the public `key` in `manifest.json`, so it is always `digijohbadkdamofjlcaobgpppepabem`, which is the only extension Purrch's bridge accepts.

## Remove

Click **Disconnect** in Purrch's Settings (it deletes the registry entries, the bridge manifest and the extension folder), then remove the extension on the browser's extensions page.

## How it works

| File | Role |
| --- | --- |
| `manifest.json` | Manifest V3. Permissions: native messaging, scripting (to reach YouTube tabs that were open before the extension was installed), alarms (to retry while Purrch isn't running). Host access to `www.youtube.com` and `music.youtube.com` only. |
| `page.js` | Runs in the YouTube page itself (`world: "MAIN"`), because YouTube's player API, which knows the quality levels, lives there. Reads the `<video>` (play state, position, length, title, channel), carries out commands, and grabs JPEG frames for the live preview. |
| `relay.js` | Runs in the extension's isolated world and carries messages between `page.js` (DOM events) and the service worker (a port per tab). |
| `background.js` | Service worker. Lists the video tabs with `chrome.tabs.query` and tab events, routes Purrch's commands to the right tab, brings a tab to the front (`tabs.update` and `windows.update`), and connects to Purrch with `chrome.runtime.connectNative('com.purrch.bridge')`, retrying every few seconds (up to a minute) while Purrch isn't running. |

The browser starts `Purrch.exe chrome-extension://digijohbadkdamofjlcaobgpppepabem/` as the native host. That copy doesn't open a window: it relays the browser's length-prefixed JSON messages to the Purrch that is running, over a named pipe that only the same Windows user and session can open, and exits when either side goes away.

Nothing leaves the PC. The extension sends Purrch the tab's address, title, channel, play state and position, and, only while a live preview is on screen and YouTube's own embedded player can't be used, small JPEG frames.
