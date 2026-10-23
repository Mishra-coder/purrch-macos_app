// Finds this browser's YouTube tabs, keeps Purrch up to date with each one's video, and carries out Purrch's
// commands. Purrch is reached through native messaging: the browser starts Purrch.exe as a small bridge that
// relays to the copy of Purrch that is running. While Purrch isn't running (or "Connect browser" hasn't been
// clicked in its Settings yet), it tries again now and then.

const HOST = 'com.purrch.bridge';
/** Tabs that are listed in Purrch, as on the Mac: watch pages, Shorts and YouTube Music. */
const VIDEO_PAGES = ['https://www.youtube.com/watch*', 'https://www.youtube.com/shorts/*', 'https://music.youtube.com/*'];
/** Tabs that get the content scripts, so a tab that navigates to a video is ready at once. */
const YOUTUBE = ['https://www.youtube.com/*', 'https://music.youtube.com/*'];

/** The port to Purrch, while connected. */
let native = null;
/** Seconds before the next attempt to reach Purrch; doubles up to a minute, and resets once Purrch answers. */
let retry = 2;
let retryTimer = 0;
/** Content-script ports by tab id, with the last state each one reported. */
const tabs = new Map();
let listTimer = 0;

function browserName() {
  const brands = ((navigator.userAgentData && navigator.userAgentData.brands) || []).map((b) => b.brand);
  if (brands.some((b) => /Edge/.test(b))) return 'Edge';
  if (brands.some((b) => /Brave/.test(b)) || navigator.brave) return 'Brave';
  if (brands.some((b) => /Opera/.test(b))) return 'Opera';
  if (brands.some((b) => /Google Chrome/.test(b))) return 'Chrome';
  // Vivaldi and plain Chromium look the same from here; the bridge names Vivaldi from its process.
  return 'Chromium';
}

function post(message) {
  if (!native) return;
  try {
    native.postMessage(message);
  } catch (e) {
    native = null;
    schedule();
  }
}

function connect() {
  clearTimeout(retryTimer);
  retryTimer = 0;
  if (native) return;
  let port;
  try {
    port = chrome.runtime.connectNative(HOST);
  } catch (e) {
    schedule();
    return;
  }
  native = port;
  port.onMessage.addListener(onPurrch);
  port.onDisconnect.addListener(() => {
    // "Specified native messaging host not found" until Connect browser is clicked; "Native host has exited"
    // when Purrch isn't running or has quit.
    void chrome.runtime.lastError;
    if (native === port) native = null;
    schedule();
  });
  post({ type: 'hello', browser: browserName(), version: chrome.runtime.getManifest().version });
  listSoon(0);
}

function schedule() {
  if (native || retryTimer) return;
  retryTimer = setTimeout(connect, retry * 1000);
  retry = Math.min(60, retry * 2);
}

/** "(3) Song name - YouTube" to "Song name", for tabs whose page hasn't reported yet. */
function cleanTitle(title) {
  return (title || '').replace(/^\(\d+\)\s*/, '').replace(/ - YouTube( Music)?$/, '');
}

/** Sends the whole list of video tabs, a moment after the last change so a burst of events sends once. */
function listSoon(delay = 150) {
  if (listTimer) return;
  listTimer = setTimeout(async () => {
    listTimer = 0;
    if (!native) return;
    let found = [];
    try {
      found = await chrome.tabs.query({ url: VIDEO_PAGES });
    } catch (e) {
      return;
    }
    const videos = found.map((t) => {
      const entry = tabs.get(t.id);
      const s = entry && entry.state;
      return {
        tab: t.id,
        window: t.windowId,
        url: (s && s.url) || t.url || '',
        title: (s && s.title) || cleanTitle(t.title),
        channel: (s && s.channel) || '',
        playing: !!(s && s.playing),
        current: (s && s.current) || 0,
        duration: (s && s.duration) || 0,
        // A tab without the content script (discarded, or still loading) can only be shown, not controlled.
        controllable: !!entry,
        seq: (s && s.seq) || 0,
      };
    });
    post({ type: 'videos', videos });
  }, delay);
}

function onPurrch(m) {
  retry = 2;
  switch (m.type) {
    case 'refresh':
      listSoon(0);
      break;
    case 'command':
    case 'frame': {
      const entry = tabs.get(m.tab);
      if (entry) {
        try {
          entry.port.postMessage(m);
          break;
        } catch (e) {
          tabs.delete(m.tab);
        }
      }
      // Gone: answer anyway, so Purrch's frame loop backs off instead of waiting.
      if (m.type === 'frame') post({ type: 'frame', tab: m.tab, gen: m.gen, img: '', error: true });
      break;
    }
    case 'focus':
      focus(m.tab);
      break;
  }
}

/** Brings the video's tab and its window to the front. */
async function focus(tabId) {
  try {
    const tab = await chrome.tabs.update(tabId, { active: true });
    const win = await chrome.windows.get(tab.windowId);
    await chrome.windows.update(tab.windowId, win.state === 'minimized' ? { state: 'normal', focused: true } : { focused: true });
  } catch (e) {
    // The tab closed in the meantime.
  }
}

chrome.runtime.onConnect.addListener((port) => {
  if (port.name !== 'tab' || !port.sender || !port.sender.tab) return;
  const id = port.sender.tab.id;
  tabs.set(id, { port, state: null });
  port.onMessage.addListener((m) => {
    const entry = tabs.get(id);
    if (!entry || entry.port !== port) return;
    if (m.type === 'state') {
      entry.state = m.state;
      listSoon();
    } else if (m.type === 'frame') {
      post({ ...m, tab: id });
    }
  });
  port.onDisconnect.addListener(() => {
    void chrome.runtime.lastError;
    const entry = tabs.get(id);
    if (entry && entry.port === port) tabs.delete(id);
    listSoon();
  });
  listSoon();
});

chrome.tabs.onUpdated.addListener((id, change) => {
  if (change.url || change.title || change.status || change.discarded !== undefined) listSoon();
});
chrome.tabs.onRemoved.addListener((id) => {
  tabs.delete(id);
  listSoon();
});
chrome.tabs.onReplaced.addListener((added, removed) => {
  tabs.delete(removed);
  listSoon();
});
chrome.tabs.onAttached.addListener(() => listSoon());

/** Puts the content scripts into YouTube tabs that were open before the extension was installed or updated,
 *  and reconnects the ones whose port went away while this worker slept. */
async function adopt() {
  let found = [];
  try {
    found = await chrome.tabs.query({ url: YOUTUBE });
  } catch (e) {
    return;
  }
  for (const t of found) {
    if (t.discarded || tabs.has(t.id)) continue;
    chrome.scripting.executeScript({ target: { tabId: t.id }, files: ['page.js'], world: 'MAIN' }).catch(() => {});
    chrome.scripting.executeScript({ target: { tabId: t.id }, files: ['relay.js'] }).catch(() => {});
  }
}

// A minute's alarm wakes the worker to try Purrch again if the retry timer died with it.
chrome.alarms.create('purrch', { periodInMinutes: 1 });
chrome.alarms.onAlarm.addListener(() => {
  if (!native) connect();
});
chrome.runtime.onInstalled.addListener(adopt);
chrome.runtime.onStartup.addListener(connect);

connect();
adopt();
