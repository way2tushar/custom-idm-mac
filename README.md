# MacDM & WinDM

Multi-connection download managers for **macOS** (MacDM, native SwiftUI) and **Windows** (WinDM, .NET Windows Forms),
with browser extensions that hand downloads and page videos to the app.

The project started from a study of how Internet Download Manager integrates with browsers, using the
[REA](https://github.com/morluto/rea) reverse-engineering toolkit (research notes are kept locally, outside
this repository). Both apps are original implementations: no IDM code, icons,
branding or wire protocol is reused, and IDM's installer is not part of this repository.

## Features

| | MacDM (macOS) | WinDM (Windows) |
|---|---|---|
| Multi-connection downloads with dynamic segmentation (up to 32 connections; a finished connection takes over half of the largest remaining range) | ✅ | ✅ |
| Pause / resume, including after quitting (resume state is validated against size, ETag and Last-Modified) | ✅ | ✅ |
| HLS streams: quality choice, AES-128, byte ranges, separate audio merged with ffmpeg | ✅ | ✅ |
| DASH streams: quality choice, SegmentTemplate/Timeline/List, separate audio merged with ffmpeg | ✅ | ✅ |
| Speed limiter (global and per download), manual proxy | ✅ | ✅ |
| Categories, main queue, scheduler (start/stop times, daily days, "when done" action) | ✅ | ✅ |
| Options: General, Save To (per-category folders), File Types (types, excluded sites/addresses), Connection, Downloads, Proxy | ✅ | ✅ |
| Duplicate warning (save under a new name or replace) | ✅ | ✅ |
| Runs in the background (menu bar / notification area), launch at login | ✅ | ✅ |
| Offers to install ffmpeg on first launch | Homebrew | winget |
| Browser extensions | Safari, Chrome, Edge, Brave, Arc | Chrome, Edge |

Not supported, by design: YouTube, DRM-protected video (Netflix, Disney+, FairPlay/Widevine), and live-stream recording.

## Repository layout

```
MacDM/                      macOS app (Swift Package Manager; builds with the Command Line Tools)
  Sources/DownloadEngine/   segmented + HLS/DASH engine (Swift)
  Sources/MacDM/            SwiftUI app: windows, menu bar, browser bridge, options
  Sources/macdm-cli/        command-line engine for testing
  Extensions/chrome/        Chrome/Edge/Brave extension (also used by WinDM); grabber.js and
                            media-utils.js are shared with the Safari extension
  Extensions/safari/        Safari web extension (resources + native handler)
  scripts/                  build-app.sh, make-icon.swift, range_server.py (local test server)
WinDM/                      Windows app (.NET 10)
  src/WinDM.Engine/         segmented + HLS/DASH engine (C#, cross-platform)
  src/WinDM.Core/           settings, download manager, queue/scheduler, browser bridge (cross-platform)
  src/WinDM.Cli/            command-line engine and headless bridge for testing on any OS
  src/WinDM/                Windows Forms app: windows, tray icon, options, scheduler
  scripts/publish.sh        builds dist/WinDM-win-x64.zip
```

## How the browser integration works

The app listens on `http://127.0.0.1:9614` (loopback only). Both apps speak the same small JSON protocol, so the
same Chrome/Edge extension works with either:

- `GET /ping` returns the app's switches and File Types rules.
- `POST /download` takes `url`, `filename`, `referrer`, `cookies`, `userAgent`, `kind` (`file`/`hls`/`dash`),
  `variant` (chosen quality) and `auto`.
  - Requests with a web-page origin are refused (403).
  - Automatic captures from excluded sites or addresses are declined (409), and the browser keeps those downloads.
- If the app isn't running, the extension opens `macdm://launch`, which both apps register, and then retries.

The extensions capture browser downloads by file type and add **Download with MacDM** to the right-click menu.
Over videos they show a **Download with MacDM** button listing every detected file and stream quality. Detection
uses page elements and resource timing; Chrome also uses response headers. Safari doesn't expose a downloads API,
so its extension captures clicks on download links instead.

## Building

### MacDM (macOS 14+, Apple silicon)

Needs the Xcode Command Line Tools; full Xcode isn't required.

```bash
cd MacDM
./scripts/build-app.sh
ditto build/MacDM.app ~/Applications/MacDM.app && open ~/Applications/MacDM.app
```

- **Safari:** turn on Settings → Advanced → *Show features for web developers*, then Settings → Developer →
  *Allow unsigned extensions*, then tick MacDM under Extensions. Safari ignores extensions from apps on an external
  volume, so install the app on the startup disk.
- **Chrome:** open `chrome://extensions`, turn on *Developer mode*, click *Load unpacked*, and choose
  `MacDM/Extensions/chrome`.
- More details are in [`MacDM/README.md`](MacDM/README.md).

### WinDM (Windows 10/11, x64)

Builds on macOS, Linux or Windows with the .NET 10 SDK; the Windows app is cross-compiled.

```bash
cd WinDM
./scripts/publish.sh            # or: ./scripts/publish.sh win-arm64
```

This produces `WinDM/dist/WinDM-win-x64.zip`, containing a single self-contained `WinDM.exe`, the browser
extension, and [`INSTALL.txt`](WinDM/INSTALL.txt) with setup steps (SmartScreen, ffmpeg, loading the extension).

## Testing

Both engines have a command-line tool and share a local test server. The server's `--jitter` option gives each
connection a random speed, so range splitting gets exercised.

```bash
python3 MacDM/scripts/range_server.py --port 8766 --size-mb 64 --rate-kb 2048 --jitter
# macOS engine
cd MacDM && swift build && .build/debug/macdm-cli http://127.0.0.1:8766/file.bin --connections 8
# Windows engine (runs on any OS)
cd WinDM && dotnet run --project src/WinDM.Cli -- http://127.0.0.1:8766/file.bin --connections 8
dotnet run --project src/WinDM.Cli -- --bridge     # headless browser bridge
```

Verified on macOS for both engines:

- 64 MB multi-connection downloads with range splitting, checked by hash.
- Pause and resume, including across restarts.
- Servers without range support.
- The speed limiter.
- HLS: master playlists, AES-128, and separate-audio qualities.
- DASH, in timeline and fixed-duration layouts.
- The bridge: 403 for web pages, 409 for exclusions, file and stream handoffs.

The WinDM window itself compiles on macOS but has only been run on Windows by the user.

## Notes

- Downloads that the browser hands over keep its cookies, referrer and user agent. Safari extensions can only read
  cookies visible to the page, not HttpOnly ones.
- App data:
  - macOS: `~/Library/Application Support/MacDM`
  - Windows: `%APPDATA%\WinDM`
- Partial downloads live in a hidden folder inside the download folder: `.macdm` on macOS, `.windm` on Windows.
