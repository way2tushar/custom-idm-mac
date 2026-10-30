# MacDM

A native macOS download manager with multi-connection, resumable downloads, plus
Chrome and Safari extensions that send browser downloads to it.

The design came from studying how Internet Download Manager integrates with
browsers (see [`../analysis/FINDINGS.md`](../analysis/FINDINGS.md)). The code is
original: no IDM code, assets, branding or wire protocol is reused.

## How it works

- **Dynamic segmentation** (`Sources/DownloadEngine/SegmentedDownload.swift`).
  A one-byte ranged GET finds the size, range support, ETag and filename. The file
  is split into up to *N* ranges, each on its own TCP connection (one `URLSession`
  per range, so HTTP/2 doesn't merge them onto one connection). When a range
  finishes, its connection takes over the second half of the largest range still
  downloading, so connections stay busy until the end.
- **Resume.** Progress is saved to `<download folder>/.macdm/<id>.json` next to a
  sparse `.part` file. A download resumes only if size, ETag and Last-Modified
  still match; otherwise it starts over. Servers without range support fall back
  to a single stream, and those downloads restart from zero.
- **Browser bridge** (`Sources/MacDM/LocalServer.swift`). The app listens on
  `127.0.0.1:9614` only and accepts requests from `chrome-extension://` or
  `moz-extension://` origins, or with no origin (local tools). Requests with a
  web-page origin get a 403.

## Build

Requires macOS 14+ and the Xcode Command Line Tools (full Xcode is not needed).

```bash
./scripts/build-app.sh
```

This produces `build/MacDM.app`, with the Safari extension embedded at
`Contents/PlugIns/`. Install it on the startup disk and open it once so macOS
registers the extension:

```bash
ditto build/MacDM.app ~/Applications/MacDM.app && open ~/Applications/MacDM.app
```

Safari ignores the extension while the app runs from an external volume
(pluginkit lists it with an `F` flag and Safari leaves it out of its list).

On first launch, if `ffmpeg` isn't installed, MacDM opens a **Video Tools** window that
installs it with Homebrew when you click **Install** (Options → General shows the same status).
To preview that window on a Mac that has ffmpeg:
`open ~/Applications/MacDM.app --args -MacDMSimulateMissingFFmpeg YES`.

`scripts/make-icon.swift` draws the app icon; `Resources/MacDM.icns` and
`Extensions/chrome/icons/` are generated from it.

Command-line engine, handy for testing:

```bash
swift build && .build/debug/macdm-cli https://example.com/big.zip --connections 8 --output ~/Downloads
```

## Running in the background

Closing the window doesn't quit MacDM. Downloads continue, the browser bridge keeps
answering, and the menu bar icon (a download arrow) shows progress and quick actions. By
default the Dock icon hides while the window is closed. Options → General has these
settings, plus **Launch MacDM at login**. Quit from the menu bar icon or with ⌘Q.
Downloads interrupted by quitting resume the next time they start.

If MacDM isn't running, the extensions start it. Chrome opens `macdm://launch` (Chrome
asks once whether to allow MacDM; tick "always allow") and then hands the download over
with its cookies. Safari's native handler launches the app directly.

## Chrome (and Edge, Brave, Arc)

1. Open `chrome://extensions` and turn on **Developer mode**.
2. Click **Load unpacked** and choose `Extensions/chrome`.

The extension catches downloads as they start (`downloads.onCreated`). When the
file type matches its list and MacDM is running, it cancels the browser's copy and
sends the URL, cookies, referrer and user agent to MacDM. If MacDM isn't running,
the browser keeps the download. The right-click menu also has **Download with
MacDM**. The popup sets file types and a minimum size.

## Safari

Safari's extension API has no downloads interface, so the Safari version catches
**clicks on download links** (matching file types, or `<a download>`), and adds
**Download with MacDM** to the right-click menu. Links go through the extension's
native handler to the app. If MacDM isn't running, the handler launches it with a
`macdm://` link.

The build is ad-hoc signed, so Safari has to allow unsigned extensions:

1. Safari > Settings > Advanced: turn on **Show features for web developers**.
2. Develop menu: turn on **Allow Unsigned Extensions**. Safari turns this off each
   time it quits.
3. Safari > Settings > Extensions: enable **MacDM**, and allow it on all websites.

To avoid step 2, sign with a Developer ID (`MACDM_SIGN_IDENTITY="Developer ID
Application: …" ./scripts/build-app.sh`), and notarize the app if you distribute
it.

Limits: only cookies visible to the page are forwarded (Safari extensions can't
read HttpOnly cookies), and downloads started by JavaScript or form posts aren't
caught.

## Video grabber

Both extensions run `Extensions/chrome/grabber.js` on every page (the build copies it into
the Safari extension). It finds media in three ways:

- `<video>`/`<audio>` elements and their `<source>` tags
- resources the page loads, such as `.m3u8` playlists fetched by script players
- in Chrome only, network responses with a video, audio or HLS content type

Hovering a video shows **Download with MacDM** at its top-right corner, with a menu of what
was found. The toolbar popup lists the same media, and the badge shows how many items were
found. Files are named after the page title.

In the app, direct files use the multi-connection engine. HLS streams
(`Sources/DownloadEngine/StreamDownload.swift`) work like this:

- MacDM picks the highest-bandwidth variant and downloads its segments in parallel.
- Standard AES-128 segments are decrypted.
- Finished segments stay on disk, so pausing resumes where it left off.
- At the end the segments are joined in order. fMP4 streams become `.mp4`; MPEG-TS streams
  are converted to `.mp4` with `ffmpeg` (no re-encoding) when it is installed, and kept as
  `.ts` otherwise.

Not supported: DRM-protected video (SAMPLE-AES, FairPlay, Widevine), DASH (`.mpd`),
recording live streams, and sites that deliver audio and video as separate streams (such as
YouTube). Players that only expose `blob:` URLs work only if the stream's playlist shows up
as a page resource.

To make a test stream:

```bash
ffmpeg -f lavfi -i testsrc=size=640x360:rate=25 -f lavfi -i sine -t 20 -c:v libx264 -g 50 -c:a aac -shortest source.mp4
ffmpeg -i source.mp4 -c copy -f hls -hls_time 2 -hls_playlist_type vod stream/index.m3u8
```

## Test server

`scripts/range_server.py` serves a throttled, deterministic file with range
support. `/norange.bin` ignores ranges, and `--jitter` gives each connection a
random speed so range splitting gets exercised:

```bash
python3 scripts/range_server.py --size-mb 64 --rate-kb 2048 --jitter
```
