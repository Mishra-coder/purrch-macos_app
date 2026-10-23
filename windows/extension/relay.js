// Carries messages between page.js (in the page's own world, where it can't reach the extension) and the
// extension's service worker, over one port per tab.
(() => {
  // The service worker injects this file again when it starts, to reach tabs whose port it lost while it
  // slept. A copy that is already here just reconnects.
  if (globalThis.__purrchRelay) {
    globalThis.__purrchRelay();
    return;
  }

  let port = null;

  function connect() {
    if (port) return port;
    try {
      port = chrome.runtime.connect({ name: 'tab' });
    } catch (e) {
      // The extension was updated or removed; this copy is orphaned and the new one takes over.
      return null;
    }
    port.onMessage.addListener((m) =>
      document.dispatchEvent(new CustomEvent('purrch:in', { detail: JSON.stringify(m) })));
    port.onDisconnect.addListener(() => {
      void chrome.runtime.lastError;
      port = null;
    });
    // A fresh port: tell the service worker where this tab stands.
    document.dispatchEvent(new CustomEvent('purrch:in', { detail: JSON.stringify({ type: 'report' }) }));
    return port;
  }

  globalThis.__purrchRelay = connect;

  document.addEventListener('purrch:out', (event) => {
    let m;
    try { m = JSON.parse(event.detail); } catch (e) { return; }
    // The service worker may have gone to sleep since the last message; connecting wakes it.
    const p = connect();
    try { if (p) p.postMessage(m); } catch (e) { port = null; }
  });

  connect();
})();
