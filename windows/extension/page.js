// Runs inside the YouTube page itself (the MAIN world), because YouTube's own player, which knows the quality
// levels, is only reachable from there. It reads and drives the page's <video> with the same probe, controls
// and frame grabber as the Mac app, and talks to relay.js (in the extension's world) through DOM events that
// carry JSON text.
(() => {
  // Injected again after the extension is updated or reloaded; one copy per page is enough.
  if (window.__purrchPage) return;
  window.__purrchPage = true;

  /** The newest command from Purrch handled here. Every report carries it, so Purrch can tell a report sent
   *  before its command arrived (stale) from one sent after (fresh). */
  let seq = 0;
  let reportTimer = 0;
  let lastReport = '';
  let lastSent = 0;

  const send = (message) =>
    document.dispatchEvent(new CustomEvent('purrch:out', { detail: JSON.stringify(message) }));
  const video = () => document.querySelector('video');
  const player = () => document.getElementById('movie_player');
  const isVideoPage = () =>
    location.hostname === 'music.youtube.com' || /^\/(watch|shorts\/)/.test(location.pathname);

  /** Play state, position, length, title and channel of the first <video>, as the Mac app reads them. */
  function probe() {
    const v = video();
    const ch = document.querySelector('ytd-video-owner-renderer #channel-name a, #owner #channel-name a, ytmusic-player-bar .byline a');
    const title = document.title.replace(/^\(\d+\)\s*/, '').replace(/ - YouTube( Music)?$/, '');
    return {
      url: location.href,
      title,
      channel: ch ? ch.textContent.trim() : '',
      // No <video> yet (still loading): listed, but idle.
      playing: !!v && !v.paused && !v.ended,
      current: v ? v.currentTime || 0 : 0,
      duration: v && isFinite(v.duration) ? v.duration : 0,
      seq,
    };
  }

  /** Tells Purrch what changed. Discrete changes go at once; the position while playing at most every 2 s,
   *  which is enough because Purrch extrapolates between reports. Nothing goes when nothing changed, unless
   *  `force`d (Purrch asked) or 10 s have passed. */
  function report(urgent, force) {
    if (reportTimer) {
      if (!urgent) return;
      clearTimeout(reportTimer);
    }
    reportTimer = setTimeout(() => {
      reportTimer = 0;
      const state = isVideoPage() ? probe() : null;
      const text = JSON.stringify(state && { ...state, current: Math.round(state.current) });
      const now = Date.now();
      if (!force && text === lastReport && now - lastSent < 10000) return;
      lastReport = text;
      lastSent = now;
      send({ type: 'state', state });
    }, urgent ? 0 : 2000);
  }

  // Media events don't bubble, but they do pass the document on the way down, whichever <video> YouTube uses.
  for (const name of ['play', 'pause', 'ended', 'seeked', 'durationchange', 'loadedmetadata', 'emptied', 'ratechange']) {
    document.addEventListener(name, () => report(true), true);
  }
  document.addEventListener('timeupdate', () => report(false), true);
  // YouTube is a single-page app: a new video arrives without a page load.
  document.addEventListener('yt-navigate-finish', () => report(true));
  new MutationObserver(() => report(true)).observe(document.head || document.documentElement,
    { subtree: true, childList: true, characterData: true });

  function command(m) {
    seq = Math.max(seq, m.seq || 0);
    const v = video();
    switch (m.action) {
      case 'toggle':
        if (v) v.paused ? v.play().catch(() => {}) : v.pause();
        break;
      case 'pause':
        if (v) v.pause();
        break;
      case 'seekBy':
        if (v) v.currentTime = Math.max(0, v.currentTime + m.value);
        break;
      case 'seekTo':
        if (v) v.currentTime = m.value;
        break;
      case 'next': {
        const b = document.querySelector('.ytp-next-button, ytmusic-player-bar .next-button');
        if (b) b.click();
        break;
      }
      case 'quality': {
        const p = player();
        if (p) {
          try { p.setPlaybackQualityRange(m.value, m.value); } catch (e) {}
          try { p.setPlaybackQuality(m.value); } catch (e) {}
        }
        break;
      }
    }
    report(true, true);
  }

  let canvas, context, sampler;
  /** Stream generation and position of the last paused frame sent, so a still picture goes once per stream. */
  let pausedKey = '';

  /** One frame for the notch's live preview, plus playback and quality state. With images off (YouTube's
   *  embedded player draws the picture in Purrch instead) only the state goes. While paused it sends the
   *  picture once per stream instead of encoding the same one again, and drops black frames: the browser
   *  releases a paused background video's picture, and a black JPEG would hide the thumbnail. */
  function frame(m) {
    const v = video();
    const reply = { type: 'frame', gen: m.gen, img: '' };
    if (!v || !v.videoWidth) return send(reply);
    const on = !v.paused && !v.ended;
    let quality = '';
    let qualities = [];
    try {
      quality = player().getPlaybackQuality();
      qualities = player().getAvailableQualityLevels() || [];
    } catch (e) {}
    Object.assign(reply, {
      playing: on,
      current: v.currentTime || 0,
      duration: isFinite(v.duration) ? v.duration : 0,
      quality,
      qualities,
      width: v.videoWidth,
      height: v.videoHeight,
      seq,
    });
    if (!m.images) return send(reply);
    const key = m.gen + ':' + v.currentTime;
    if (!on && pausedKey === key) return send(reply);

    const w = Math.min(m.width, v.videoWidth);
    const h = Math.round(w * v.videoHeight / v.videoWidth);
    if (!canvas) canvas = document.createElement('canvas');
    if (canvas.width !== w || canvas.height !== h) {
      canvas.width = w;
      canvas.height = h;
      context = canvas.getContext('2d', { alpha: false });
    }
    context.drawImage(v, 0, 0, w, h);
    let image = canvas.toDataURL('image/jpeg', m.jpeg);
    if (!on) {
      try {
        if (!sampler) {
          sampler = document.createElement('canvas');
          sampler.width = 8;
          sampler.height = 8;
        }
        const x = sampler.getContext('2d');
        x.drawImage(v, 0, 0, 8, 8);
        const d = x.getImageData(0, 0, 8, 8).data;
        let brightest = 0;
        for (let i = 0; i < d.length; i += 4) brightest = Math.max(brightest, d[i] + d[i + 1] + d[i + 2]);
        if (brightest < 24) image = '';
      } catch (e) {}
      if (image) pausedKey = key;
    }
    reply.img = image;
    send(reply);
  }

  document.addEventListener('purrch:in', (event) => {
    let m;
    try { m = JSON.parse(event.detail); } catch (e) { return; }
    if (m.type === 'command') command(m);
    else if (m.type === 'frame') frame(m);
    else if (m.type === 'report') report(true, true);
  });

  report(true, true);
})();
