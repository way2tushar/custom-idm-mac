// Shared by both extensions' background scripts (build-app.sh copies it into Safari's).
// Turns raw media URLs into menu entries: file sizes for plain files, and one entry per
// quality for HLS master playlists.

const MIN_MEDIA_BYTES = 300 * 1024; // smaller files are usually interface sounds or previews

/** Total size from a HEAD request, or from a one-byte ranged GET when HEAD is unhelpful. */
async function probeSize(url) {
  const read = (response) =>
    Number((response.headers.get("content-range") || "").split("/")[1]) || Number(response.headers.get("content-length")) || 0;
  try {
    const head = await fetch(url, { method: "HEAD", credentials: "include", signal: AbortSignal.timeout(4000) });
    if (head.ok && read(head)) return read(head);
  } catch {}
  try {
    const controller = new AbortController();
    const response = await fetch(url, { headers: { Range: "bytes=0-0" }, credentials: "include", signal: controller.signal });
    const size = read(response);
    controller.abort();
    return size;
  } catch {
    return 0;
  }
}

function playlistAttributes(text) {
  const result = {};
  for (const match of text.matchAll(/([A-Z0-9-]+)=("[^"]*"|[^,]*)/g)) result[match[1]] = match[2].replace(/^"|"$/g, "");
  return result;
}

/** Qualities in a master playlist, best first; empty for a media playlist. */
function parseMaster(text, base) {
  const variants = [];
  const lines = text.split(/\r?\n/);
  for (let i = 0; i < lines.length; i++) {
    if (!lines[i].startsWith("#EXT-X-STREAM-INF:")) continue;
    const attrs = playlistAttributes(lines[i].slice("#EXT-X-STREAM-INF:".length));
    let uri = "";
    while (++i < lines.length && (!lines[i].trim() || lines[i].startsWith("#")));
    if (i < lines.length) uri = lines[i].trim();
    if (!uri) continue;
    const bandwidth = Number(attrs["BANDWIDTH"] || attrs["AVERAGE-BANDWIDTH"] || 0);
    const height = Number((attrs["RESOLUTION"] || "").split("x")[1]) || 0;
    variants.push({ url: new URL(uri, base).href, bandwidth, height, resolution: attrs["RESOLUTION"] || "" });
  }
  // Same resolution at several bitrates: keep them, but order by height, then bitrate.
  return variants.sort((a, b) => b.height - a.height || b.bandwidth - a.bandwidth);
}

/** Video qualities in a DASH manifest, best first. Text-based: service workers have no DOMParser. */
function parseDash(text) {
  const xmlAttrs = (tag) => Object.fromEntries([...tag.matchAll(/([\w:-]+)="([^"]*)"/g)].map((m) => [m[1], m[2]]));
  const variants = [];
  for (const set of text.split(/<AdaptationSet\b/).slice(1)) {
    const setAttrs = xmlAttrs(set.slice(0, set.indexOf(">")));
    for (const match of set.matchAll(/<Representation\b([^>]*)>/g)) {
      const attrs = { ...setAttrs, ...xmlAttrs(match[1]) };
      const type = attrs.contentType || (attrs.mimeType || "").split("/")[0];
      if (type !== "video") continue;
      const height = Number(attrs.height) || 0;
      variants.push({ bandwidth: Number(attrs.bandwidth) || 0, height,
                      resolution: attrs.width && height ? `${attrs.width}x${height}` : "" });
    }
  }
  return variants.sort((a, b) => b.height - a.height || b.bandwidth - a.bandwidth);
}

function qualityLabel(variant) {
  const mbps = variant.bandwidth ? `${(variant.bandwidth / 1e6).toFixed(1)} Mbps` : "";
  const name = variant.height ? `${variant.height}p` : "Quality";
  return [name, variant.resolution && `(${variant.resolution})`, mbps].filter(Boolean).join(" ");
}

/**
 * Fills in an entry before it is listed. Returns null for entries to hide.
 * `known` holds quality-playlist URLs already listed under a master, so they don't appear twice.
 */
async function describeMedia(entry, known) {
  if (known.has(entry.url)) return null;
  if (["hls", "probe", "dash"].includes(entry.kind)) {
    try {
      const response = await fetch(entry.url, { credentials: "include", signal: AbortSignal.timeout(5000) });
      const text = (await response.text()).trimStart();
      // "probe" entries are only guesses from the address; the content decides.
      if (/<MPD[\s>]/.test(text.slice(0, 4000))) {
        if (/type="dynamic"/.test(text.slice(0, 4000))) return { ...entry, kind: "dash", blocked: "live stream" };
        if (/<ContentProtection\b/.test(text)) return { ...entry, kind: "dash", blocked: "DRM-protected" };
        const variants = parseDash(text).map((v) => ({ bandwidth: v.bandwidth, label: qualityLabel(v), url: entry.url }));
        return { ...entry, kind: "dash", variants };
      }
      if (!text.startsWith("#EXTM3U")) return null;
      entry = { ...entry, kind: "hls" };
      const variants = parseMaster(text, entry.url);
      for (const variant of variants) known.add(variant.url);
      return { ...entry, variants: variants.map((v) => ({ bandwidth: v.bandwidth, label: qualityLabel(v), url: v.url })) };
    } catch {
      return entry.kind === "probe" ? null : entry; // a known stream we couldn't read is still offered
    }
  }
  const size = entry.size || (await probeSize(entry.url));
  if (size && size < MIN_MEDIA_BYTES) return null;
  return { ...entry, size };
}
