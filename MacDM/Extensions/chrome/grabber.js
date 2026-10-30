// Video grabber, shared by the Chrome and Safari extensions (build-app.sh copies it into Safari's).
// Finds media on the page and shows a "Download with MacDM" button over videos on hover.

const api = globalThis.browser ?? globalThis.chrome;
const MEDIA_URL = /\.(m3u8|mp4|m4v|webm|mov|mkv|mp3|m4a|ogg|oga|flac|wav)(\?|#|$)/i;
const SEGMENT_URL = /\.(ts|m4s|m4f|cmfv|cmfa)(\?|#|$)/i;
// Playlists without a .m3u8 ending (e.g. /manifest?format=hls); checked by content in the background.
const MAYBE_PLAYLIST = /m3u8|mpegurl|\.mpd(\?|#|$)|\/(manifest|playlist|master)\b|format=(hls|dash)/i;
const NOT_PLAYLIST = /\.(js|css|json|webmanifest|png|jpe?g|gif|svg|webp|woff2?|html?)(\?|#|$)/i;
const seen = new Set();

if (window === window.top) api.runtime.sendMessage({ type: "page-start" }).catch(() => {});

function report(url, kind) {
  if (!url || !/^https?:/i.test(url) || seen.has(url) || SEGMENT_URL.test(url)) return;
  seen.add(url);
  api.runtime.sendMessage({
    type: "media-found",
    url,
    kind: kind || (/\.m3u8(\?|#|$)/i.test(url) ? "hls" : "file"),
    title: document.title,
  }).catch(() => {});
}

function scanElements() {
  for (const element of document.querySelectorAll("video, audio")) {
    report(element.currentSrc || element.src);
    for (const source of element.querySelectorAll("source")) report(source.src);
  }
}

// Players built on Media Source Extensions load playlists with fetch/XHR; those show up as resources.
try {
  new PerformanceObserver((list) => {
    for (const entry of list.getEntries()) {
      if (MEDIA_URL.test(entry.name)) report(entry.name);
      else if (MAYBE_PLAYLIST.test(entry.name) && !NOT_PLAYLIST.test(new URL(entry.name).pathname)) report(entry.name, "probe");
    }
  }).observe({ type: "resource", buffered: true });
} catch {}

for (const event of ["loadedmetadata", "play"]) document.addEventListener(event, scanElements, true);
document.addEventListener("DOMContentLoaded", scanElements);

// ----- Download button over videos -----

let host, button, menu, currentVideo, hideTimer;

// MacDM's Options can turn the button off; re-checked at most every 10 seconds.
let grabberEnabled = true;
let grabberCheckedAt = 0;
function refreshGrabberSwitch() {
  if (Date.now() - grabberCheckedAt < 10000) return;
  grabberCheckedAt = Date.now();
  api.runtime.sendMessage({ type: "grabber-enabled" }).then((on) => {
    grabberEnabled = on !== false;
    if (!grabberEnabled && host) host.wrap.style.display = "none";
  }).catch(() => {});
}

function ensureButton() {
  if (host) return;
  host = document.createElement("macdm-grabber");
  const shadow = host.attachShadow({ mode: "closed" });
  shadow.innerHTML = `
    <style>
      :host { all: initial; }
      .wrap { position: fixed; z-index: 2147483647; display: none; font: 12px -apple-system, system-ui, sans-serif; }
      button.main { display: flex; align-items: center; gap: 6px; padding: 6px 10px; border: 0; border-radius: 6px;
        background: rgba(20, 20, 20, 0.85); color: #fff; cursor: pointer; box-shadow: 0 2px 8px rgba(0,0,0,.35); }
      button.main:hover { background: #0a84ff; }
      .menu { margin-top: 4px; max-width: 420px; background: #fff; color: #111; border-radius: 6px;
        box-shadow: 0 6px 20px rgba(0,0,0,.35); overflow: hidden; display: none; }
      .menu button { display: block; width: 100%; text-align: left; padding: 7px 10px; border: 0; background: none;
        color: inherit; font: inherit; cursor: pointer; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
      .menu button:hover { background: #0a84ff; color: #fff; }
      .tag { font-weight: 600; margin-right: 6px; }
      .note { padding: 7px 10px; color: #666; }
    </style>
    <div class="wrap">
      <button class="main"><span>⬇</span><span>Download with MacDM</span></button>
      <div class="menu"></div>
    </div>`;
  const wrap = shadow.querySelector(".wrap");
  button = shadow.querySelector("button.main");
  menu = shadow.querySelector(".menu");
  button.addEventListener("click", (event) => { event.stopPropagation(); openMenu(); });
  wrap.addEventListener("mouseenter", () => clearTimeout(hideTimer));
  wrap.addEventListener("mouseleave", scheduleHide);
  host.wrap = wrap;
  document.documentElement.appendChild(host);
}

function place(video) {
  ensureButton();
  currentVideo = video;
  const rect = video.getBoundingClientRect();
  host.wrap.style.top = `${Math.max(4, rect.top + 8)}px`;
  host.wrap.style.left = `${Math.max(4, rect.right - 190)}px`;
  host.wrap.style.display = "block";
  clearTimeout(hideTimer);
}

function scheduleHide() {
  clearTimeout(hideTimer);
  hideTimer = setTimeout(() => {
    if (host) { host.wrap.style.display = "none"; menu.style.display = "none"; }
  }, 2500);
}

function videoAt(x, y) {
  for (const video of document.querySelectorAll("video")) {
    const r = video.getBoundingClientRect();
    if (r.width >= 200 && r.height >= 110 && x >= r.left && x <= r.right && y >= r.top && y <= r.bottom) return video;
  }
  return null;
}

// Sites often cover the video with controls, so hit-test by position rather than event target.
document.addEventListener("mousemove", (event) => {
  const video = videoAt(event.clientX, event.clientY);
  if (video) refreshGrabberSwitch();
  if (video && grabberEnabled) place(video);
  else if (host && host.wrap.style.display === "block") scheduleHide();
}, { passive: true });

async function openMenu() {
  scanElements();
  const media = (await api.runtime.sendMessage({ type: "media-list" }).catch(() => [])) || [];
  const own = currentVideo?.currentSrc;
  // The hovered video's own file first, then streams, then other files.
  media.sort((a, b) => (b.url === own) - (a.url === own) || (b.kind === "hls") - (a.kind === "hls"));
  menu.textContent = "";
  if (media.length === 0) {
    const note = document.createElement("div");
    note.className = "note";
    // YouTube (and its embeds) delivers video in its own protected format, which MacDM doesn't download.
    const onYouTube = /(^|\.)(youtube\.com|youtube-nocookie\.com|youtu\.be)$/i.test(location.hostname);
    note.textContent = onYouTube
      ? "YouTube videos can't be downloaded with MacDM."
      : currentVideo?.currentSrc?.startsWith("blob:")
      ? "No downloadable stream found yet. Play the video for a few seconds, then try again."
      : "No downloadable media found on this page.";
    menu.appendChild(note);
  }
  for (const entry of media) {
    // A multi-quality stream gets one row per quality.
    const choices = entry.blocked
      ? [{ tag: "Stream", text: `${entry.blocked}, can't be downloaded`, disabled: true }]
      : entry.variants?.length
      ? entry.variants.map((v) => ({ tag: entry.kind === "dash" ? "DASH" : "HLS", text: v.label, variant: v.bandwidth,
                                     quality: v.label.split(" ")[0] }))
      : [{ tag: entry.kind === "hls" ? "Stream" : (entry.url.match(MEDIA_URL)?.[1] || "File").toUpperCase(),
           text: `${entry.size ? formatSize(entry.size) + " · " : ""}${shortName(entry.url)}` }];
    for (const choice of choices) {
      const item = document.createElement("button");
      const tag = document.createElement("span");
      tag.className = "tag";
      tag.textContent = choice.tag;
      item.append(tag, choice.text);
      item.title = entry.url;
      item.disabled = !!choice.disabled;
      if (choice.disabled) item.style.opacity = "0.5";
      item.addEventListener("click", async (event) => {
        event.stopPropagation();
        menu.style.display = "none";
        const ok = await api.runtime.sendMessage({
          type: "download-media", url: entry.url, kind: entry.kind, title: document.title,
          variant: choice.variant, quality: choice.quality,
        }).catch(() => false);
        flash(ok ? "Sent to MacDM" : "MacDM is not running");
      });
      menu.appendChild(item);
    }
  }
  menu.style.display = "block";
}

function flash(text) {
  const label = button.lastElementChild;
  const original = "Download with MacDM";
  label.textContent = text;
  setTimeout(() => { label.textContent = original; }, 2000);
}

function shortName(url) {
  try {
    const path = new URL(url).pathname.split("/").filter(Boolean);
    return decodeURIComponent(path.slice(-2).join("/")) || url;
  } catch { return url; }
}

function formatSize(bytes) {
  const units = ["B", "KB", "MB", "GB"];
  let value = bytes, unit = 0;
  while (value >= 1024 && unit < units.length - 1) { value /= 1024; unit++; }
  return `${value.toFixed(unit ? 1 : 0)} ${units[unit]}`;
}
