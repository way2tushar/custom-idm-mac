# IDM 6.43 build 16 — analysis findings

Target: `exe/idman643build16.exe`
SHA-256 `276aa7e4ff7682445b011ebe6ac3920e7eca6ece7d3f6723b1f1cb515164461b`, PE32 GUI (i386), 12.7 MB.

Tools: REA 6.8.0 CLI (`inspect-artifact`, `inspect-pe-resources`,
`analyze-javascript-application`) plus a byte scan for ZIP local headers.
Evidence JSON is in this folder.

## Observed

| # | Finding | Evidence |
|---|---------|----------|
| 1 | The installer is a ~100 KB PE stub with a large appended payload; REA's artifact graph sees no inventoried members (1 node, 0 edges). | `installer-artifact.json` (`ev_939f22…`) |
| 2 | The payload contains loose deflate ZIP entries: `components2/idmcchandler2_64.dll` and three copies of a browser extension (`background.js`, `content.js`, `captured.html`, `images/logo128.png`). The rest, including `IDMan.exe`, is in a proprietary packed format that was not decoded. | byte scan; `extracted/` |
| 3 | The extension talks to the desktop app over a WebSocket at `ws://127.0.0.1:1001` (sub-protocol `plugin.v3.internetdownloadmanager.com`). If that fails, it falls back to `runtime.connectNative("com.tonec.idm")`. Messages use a binary format with numeric field IDs. | `extracted/part3/background.js` (offset ~15271) |
| 4 | Capture uses `webRequest.onBeforeSendHeaders` (to collect request headers and cookies), `onHeadersReceived` (to classify responses), `downloads.onCreated` / `onDeterminingFilename`, and `downloads.setShelfEnabled`. Classification uses a MIME list (archives, executables, media, `application/vnd.apple.mpegurl`, DASH, etc.). | `background.js` `Fb()` |
| 5 | The extension forwards browser proxy settings (`proxy.settings`, socks5 / proxyDNS) to the app, and installs right-click menu items. | `background.js` `Hb()`, `Ab()` |
| 6 | `content.js` runs in pages to find media elements for a download panel. | `extracted/part3/content.js` |

## Inferred / not observed

- **Download engine.** `IDMan.exe` was not unpacked, so its algorithm was not observed.
  The design below follows IDM's publicly documented "dynamic file segmentation":
  split a file into N HTTP Range requests, and when one connection finishes,
  split the largest remaining segment in half and give it the free connection.
  That reuses connections and needs no reassembly step.
- Resume via Range plus validators (ETag / Last-Modified) is standard HTTP practice, not observed in IDM.

## What MacDM builds (original design, no IDM code or protocol reused)

- A native SwiftUI app with a segmented, resumable engine (`DownloadEngine`).
- A loopback-only JSON bridge at `http://127.0.0.1:9614` that accepts only extension origins.
- A Chrome MV3 extension: it intercepts `downloads.onCreated`, adds a right-click menu item,
  and forwards cookies, referrer and user agent. MV3 does not allow blocking `webRequest`.
- Out of scope: licensing/registration code, video-sniffer panel, proxy forwarding, scheduler.
