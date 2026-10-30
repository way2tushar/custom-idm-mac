// Safari has no downloads API, so capture happens in content.js (link clicks) and the
// context menu here. Everything goes to the native handler inside MacDM.app.

const DEFAULTS = {
  enabled: true,
  extensions:
    "zip rar 7z tar gz tgz bz2 xz dmg pkg iso img exe msi apk deb rpm " +
    "mp4 mkv mov avi webm m4v mp3 m4a flac wav aac ogg pdf epub torrent bin",
};

/** The app's capture/grabber switches; both on when MacDM isn't running, so it can be started. */
async function appSwitches() {
  try {
    const status = await native({ type: "status" });
    return status?.alive
      ? { capture: status.capture !== false, grabber: status.grabber !== false, types: Array.isArray(status.types) ? status.types : null }
      : { capture: true, grabber: true, types: null };
  } catch {
    return { capture: true, grabber: true };
  }
}

async function settings() {
  const local = { ...DEFAULTS, ...(await browser.storage.local.get(DEFAULTS)) };
  const app = await appSwitches();
  // While MacDM runs, its Options → File Types list replaces this extension's own.
  return { ...local, enabled: local.enabled && app.capture, extensions: app.types ? app.types.join(" ") : local.extensions };
}

// The application identifier argument is ignored by Safari: messages always reach the containing app's extension.
function native(message) {
  return browser.runtime.sendNativeMessage("com.macdm.app", message);
}

async function download({ url, referrer, cookies, filename, kind, variant, auto }) {
  try {
    const reply = await native({ type: "download", url, referrer, cookies, filename, kind: kind || "file", variant: variant || null,
                                 auto: !!auto,
                                 userAgent: navigator.userAgent });
    return reply?.accepted === true;
  } catch {
    return false;
  }
}

browser.runtime.onInstalled.addListener(() => {
  browser.contextMenus.create({ id: "macdm-link", title: "Download with MacDM", contexts: ["link"] });
  browser.contextMenus.create({ id: "macdm-media", title: "Download with MacDM", contexts: ["video", "audio", "image"] });
});

browser.contextMenus.onClicked.addListener((info) => {
  const url = info.linkUrl || info.srcUrl;
  if (url) download({ url, referrer: info.pageUrl });
});

// ----- Video grabber -----
// Safari offers no response-header hook, so media comes from grabber.js (elements and resource timing).
const mediaByTab = new Map();

function titleFilename(title, url, kind) {
  const base = (title || "video").replace(/[\\/:*?"<>|]+/g, "-").replace(/\s+/g, " ").trim().slice(0, 120) || "video";
  if (kind === "hls") return base;
  const ext = (new URL(url).pathname.match(/\.([a-z0-9]{2,5})$/i) || [])[1];
  return ext ? `${base}.${ext}` : base;
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

function setBadge(tabId) {
  const count = mediaByTab.get(tabId)?.length || 0;
  browser.action.setBadgeText({ tabId, text: count ? String(count) : "" }).catch?.(() => {});
}

browser.runtime.onMessage.addListener((message, sender) => {
  const tabId = message?.tabId ?? sender.tab?.id;
  switch (message?.type) {
    case "page-start":
      mediaByTab.delete(tabId);
      setBadge(tabId);
      return;
    case "grabber-enabled":
      return appSwitches().then((app) => app.grabber);
    case "media-found": {
      const list = mediaByTab.get(tabId) || [];
      const existing = list.find((item) => item.url === message.url);
      if (!existing) list.push({ url: message.url, kind: message.kind, size: 0 });
      else if (existing.kind === "probe" && message.kind !== "probe") existing.kind = message.kind;
      mediaByTab.set(tabId, list.slice(-50));
      setBadge(tabId);
      return;
    }
    case "media-list":
      return describeAll(mediaByTab.get(tabId) || []).then((shown) => {
        mediaByTab.set(tabId, shown);
        setBadge(tabId);
        return shown;
      });
    case "download-media": {
      const title = message.quality ? `${message.title} ${message.quality}` : message.title;
      return download({ url: message.url, kind: message.kind, variant: message.variant, referrer: sender.tab?.url,
                        filename: titleFilename(title, message.url, message.kind) });
    }
  }
});

browser.runtime.onMessage.addListener((message) => {
  switch (message?.type) {
    case "download":
      return download(message);
    case "settings":
      return settings();
    case "status":
      return native({ type: "status" }).catch(() => ({ alive: false }));
  }
});
