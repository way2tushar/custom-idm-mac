importScripts("media-utils.js");

// Hands browser downloads to the MacDM app over its loopback bridge.
// If MacDM is not running, the browser keeps the download.

const APP = "http://127.0.0.1:9614";
const DEFAULTS = {
  enabled: true,
  launchApp: true,
  minSizeKB: 0,
  extensions:
    "zip rar 7z tar gz tgz bz2 xz dmg pkg iso img exe msi apk deb rpm " +
    "mp4 mkv mov avi webm m4v mp3 m4a flac wav aac ogg pdf epub torrent bin",
};
const handedOff = new Set(); // URLs we chose to let the browser keep, so we don't loop

async function settings() {
  return { ...DEFAULTS, ...(await chrome.storage.local.get(DEFAULTS)) };
}

// The app's own switches (Options → General), reported by /ping. Unknown while it's not running.
let appSwitches = { capture: true, grabber: true };

async function appAlive() {
  try {
    const response = await fetch(`${APP}/ping`, { signal: AbortSignal.timeout(600) });
    if (response.ok) appSwitches = { ...appSwitches, ...(await response.json()) };
    return response.ok;
  } catch {
    return false;
  }
}

/**
 * Makes sure MacDM is running. If it isn't, opens macdm://launch from the active tab, which
 * asks Chrome to start the app (Chrome shows an "Open MacDM?" prompt, with an "always allow"
 * box), then waits up to 8 seconds for the bridge to answer.
 */
async function ensureApp() {
  if (await appAlive()) return true;
  if (!(await settings()).launchApp) return false;
  const [tab] = await chrome.tabs.query({ active: true, lastFocusedWindow: true });
  if (!tab?.id || !/^https?:/i.test(tab.url || "")) return false;
  await chrome.tabs.update(tab.id, { url: "macdm://launch" }).catch(() => {});
  for (let attempt = 0; attempt < 16; attempt++) {
    await new Promise((resolve) => setTimeout(resolve, 500));
    if (await appAlive()) return true;
  }
  return false;
}

async function cookieHeader(url) {
  try {
    const cookies = await chrome.cookies.getAll({ url });
    return cookies.map((c) => `${c.name}=${c.value}`).join("; ");
  } catch {
    return "";
  }
}

async function sendToApp({ url, filename, referrer, kind, variant, auto }) {
  const body = {
    url,
    auto: !!auto,
    kind: kind || "file",
    variant: variant || null,
    filename: filename || null,
    referrer: referrer || null,
    cookies: await cookieHeader(url),
    userAgent: navigator.userAgent,
  };
  const response = await fetch(`${APP}/download`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(body),
    signal: AbortSignal.timeout(2000),
  });
  return response.ok;
}

function extensionOf(item) {
  const name = item.filename || new URL(item.finalUrl || item.url).pathname;
  const match = /\.([a-z0-9]{1,8})$/i.exec(name);
  return match ? match[1].toLowerCase() : "";
}

/** `types`: MacDM's File Types list while it's running, else this extension's own list. */
function shouldCapture(item, prefs, types) {
  const url = item.finalUrl || item.url;
  if (!/^https?:/i.test(url) || handedOff.has(url)) return false;
  if (item.fileSize > 0 && item.fileSize < prefs.minSizeKB * 1024) return false;
  const wanted = types || prefs.extensions.split(/[\s,]+/).filter(Boolean).map((t) => t.toLowerCase());
  const ext = extensionOf(item);
  // Without an extension yet, fall back to the MIME type: anything that isn't a web page.
  return ext ? wanted.includes(ext) : !/^text\/html/.test(item.mime || "");
}

chrome.downloads.onCreated.addListener(async (item) => {
  const prefs = await settings();
  if (!prefs.enabled || item.state !== "in_progress") return;
  const alive = await appAlive();
  // Turned off in the app: leave browser downloads alone.
  if (alive && appSwitches.capture === false) return;
  if (!shouldCapture(item, prefs, alive && Array.isArray(appSwitches.types) ? appSwitches.types : null)) return;

  // Pause first so the browser doesn't race ahead while we ask the app.
  await chrome.downloads.pause(item.id).catch(() => {});
  const url = item.finalUrl || item.url;
  let accepted = false;
  if (await ensureApp()) {
    // The app answers 409 for sites/addresses excluded in Options → File Types; the browser keeps those.
    accepted = await sendToApp({
      url,
      auto: true,
      filename: item.filename ? item.filename.split(/[\\/]/).pop() : null,
      referrer: item.referrer,
    }).catch(() => false);
  }
  if (accepted) {
    await chrome.downloads.cancel(item.id).catch(() => {});
    await chrome.downloads.erase({ id: item.id }).catch(() => {});
  } else {
    handedOff.add(url);
    await chrome.downloads.resume(item.id).catch(() => {});
  }
});

chrome.runtime.onInstalled.addListener(() => {
  chrome.contextMenus.create({ id: "macdm-link", title: "Download with MacDM", contexts: ["link"] });
  chrome.contextMenus.create({ id: "macdm-media", title: "Download with MacDM", contexts: ["video", "audio", "image"] });
});

chrome.contextMenus.onClicked.addListener(async (info) => {
  const url = info.linkUrl || info.srcUrl;
  if (!url) return;
  const ok = (await ensureApp()) && (await sendToApp({ url, referrer: info.pageUrl }).catch(() => false));
  if (!ok) chrome.downloads.download({ url }); // app not running: fall back to the browser
});

// ----- Video grabber -----
// Media found on each tab, from the page script (elements, resource timing) and from
// response headers here. Kept in session storage because the service worker can be unloaded.

const MEDIA_TYPE = /^(video|audio)\/|mpegurl/i;
const SEGMENT = /\.(ts|m4s|m4f|cmfv|cmfa)(\?|#|$)|^video\/(mp2t|iso\.segment)/i;
const MIN_FILE_BYTES = 300 * 1024; // smaller media responses are usually stream fragments or previews

async function mediaFor(tabId) {
  const key = `media:${tabId}`;
  return (await chrome.storage.session.get(key))[key] || [];
}

// Reports arrive in bursts; storage updates run one at a time so none overwrites another.
let storageQueue = Promise.resolve();
function serialized(task) {
  const run = storageQueue.then(task, task);
  storageQueue = run.catch(() => {});
  return run;
}

// A guess from the address ("probe") never replaces what the response headers established.
const KIND_RANK = { probe: 0, file: 1, hls: 2, dash: 2 };

function addMedia(tabId, entry) {
  if (tabId < 0) return;
  return serialized(async () => {
    const key = `media:${tabId}`;
    const list = await mediaFor(tabId);
    const existing = list.find((item) => item.url === entry.url);
    if (existing) {
      const kind = (KIND_RANK[entry.kind] ?? 0) > (KIND_RANK[existing.kind] ?? 0) ? entry.kind : existing.kind;
      Object.assign(existing, Object.fromEntries(Object.entries(entry).filter(([, v]) => v)), { kind });
    } else {
      list.push(entry);
    }
    await chrome.storage.session.set({ [key]: list.slice(-50) });
    chrome.action.setBadgeText({ tabId, text: String(list.length) }).catch(() => {});
    chrome.action.setBadgeBackgroundColor({ tabId, color: "#0a84ff" }).catch(() => {});
  });
}

/** Entries to show: masters expanded into qualities, their quality playlists and tiny files hidden. */
async function describeAll(list) {
  const known = new Set();
  const streams = list.filter((entry) => ["hls", "probe", "dash"].includes(entry.kind));
  const files = list.filter((entry) => !["hls", "probe", "dash"].includes(entry.kind));
  // Qualities of masters described earlier still hide their own playlists.
  for (const entry of streams) for (const variant of entry.variants || []) known.add(variant.url);
  const described = [];
  for (const entry of streams) described.push(entry.described ? entry : await describeMedia(entry, known));
  // Masters seen later can still cover playlists described earlier.
  const shownStreams = described.filter((entry) => entry && (entry.variants?.length || !known.has(entry.url)));
  const shownFiles = await Promise.all(files.map((entry) => entry.described ? entry : describeMedia(entry, known)));
  return [...shownStreams, ...shownFiles.filter(Boolean)].map((entry) => ({ ...entry, described: true }));
}

function clearMedia(tabId) {
  return serialized(async () => {
    await chrome.storage.session.remove(`media:${tabId}`);
    chrome.action.setBadgeText({ tabId, text: "" }).catch(() => {});
  });
}

chrome.webRequest.onResponseStarted.addListener(
  (details) => {
    if (details.tabId < 0 || details.statusCode >= 400) return;
    const header = (name) => details.responseHeaders?.find((h) => h.name.toLowerCase() === name)?.value || "";
    const type = header("content-type").toLowerCase();
    if (/dash\+xml/.test(type) || /\.mpd(\?|#|$)/i.test(details.url)) {
      addMedia(details.tabId, { url: details.url, kind: "dash", size: 0, type });
      return;
    }
    const isPlaylist = /mpegurl/.test(type) || /\.m3u8(\?|#|$)/i.test(details.url);
    if (!isPlaylist && !MEDIA_TYPE.test(type)) return;
    if (SEGMENT.test(details.url) || SEGMENT.test(type)) return;
    // Size of the whole file: from Content-Range on partial responses, else Content-Length.
    const total = Number(header("content-range").split("/")[1]) || Number(header("content-length")) || 0;
    if (!isPlaylist && total && total < MIN_FILE_BYTES) return;
    addMedia(details.tabId, { url: details.url, kind: isPlaylist ? "hls" : "file", size: isPlaylist ? 0 : total, type });
  },
  { urls: ["<all_urls>"], types: ["media", "xmlhttprequest", "other"] },
  ["responseHeaders"]
);

chrome.tabs.onRemoved.addListener((tabId) => clearMedia(tabId));

function titleFilename(title, url, kind) {
  const base = (title || "video").replace(/[\\/:*?"<>|]+/g, "-").replace(/\s+/g, " ").trim().slice(0, 120) || "video";
  if (kind === "hls") return base; // MacDM picks .mp4/.ts once the stream is joined
  const ext = (new URL(url).pathname.match(/\.([a-z0-9]{2,5})$/i) || [])[1];
  return ext ? `${base}.${ext}` : base;
}

async function downloadMedia({ url, kind, title, variant, quality }, tab) {
  const name = titleFilename(quality ? `${title} ${quality}` : title, url, kind);
  const ok = (await ensureApp()) && (await sendToApp({ url, kind, variant, filename: name, referrer: tab?.url }).catch(() => false));
  if (!ok && kind !== "hls") chrome.downloads.download({ url }); // let the browser have plain files
  return ok;
}

chrome.runtime.onMessage.addListener((message, sender, reply) => {
  const tabId = message?.tabId ?? sender.tab?.id;
  switch (message?.type) {
    case "page-start":
      clearMedia(tabId);
      return;
    case "grabber-enabled":
      // When MacDM isn't running the button still shows, so it can start the app.
      appAlive().then((alive) => reply(!alive || appSwitches.grabber !== false));
      return true;
    case "media-found":
      addMedia(tabId, { url: message.url, kind: message.kind, size: 0 });
      return;
    case "media-list":
      serialized(async () => {
        const shown = await describeAll(await mediaFor(tabId));
        await chrome.storage.session.set({ [`media:${tabId}`]: shown });
        chrome.action.setBadgeText({ tabId, text: shown.length ? String(shown.length) : "" }).catch(() => {});
        return shown;
      }).then(reply);
      return true;
    case "download-media":
      downloadMedia(message, sender.tab).then(reply);
      return true;
  }
});

chrome.runtime.onMessage.addListener((message, _sender, reply) => {
  if (message?.type === "status") {
    appAlive().then((alive) => reply({ alive }));
    return true;
  }
  if (message?.type === "settings") {
    settings().then(reply);
    return true;
  }
});
