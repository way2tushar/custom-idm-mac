"""Local test server for the download engine.

Serves one deterministic random file with byte-range support, throttled per connection
so multi-connection speedups are measurable.

  /file.bin     ranges + ETag
  /norange.bin  same bytes, ignores Range (single-stream fallback)
  /archive.zip  same bytes as /file.bin, for extension-based capture
  /             test page linking to the files above (for browser extension tests)

Usage: python3 scripts/range_server.py [--port 8765] [--size-mb 64] [--rate-kb 2048] [--jitter]
"""
import argparse
import hashlib
import os
import random
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

parser = argparse.ArgumentParser()
parser.add_argument("--port", type=int, default=8765)
parser.add_argument("--size-mb", type=int, default=64)
parser.add_argument("--rate-kb", type=int, default=2048, help="per-connection throttle")
parser.add_argument("--jitter", action="store_true", help="give each connection a random 25-100%% of the rate")
args = parser.parse_args()

DATA = random.Random(42).randbytes(args.size_mb * 1024 * 1024)
ETAG = '"%s"' % hashlib.sha256(DATA).hexdigest()[:16]
CHUNK = 64 * 1024
print("sha256", hashlib.sha256(DATA).hexdigest(), flush=True)


PAGE = """<!doctype html><title>MacDM test page</title>
<h1>MacDM test page</h1>
<ul>
  <li><a id="zip" href="/archive.zip">archive.zip (should go to MacDM)</a></li>
  <li><a id="attr" href="/file.bin" download="report.bin">download attribute (should go to MacDM)</a></li>
  <li><a id="page" href="/">this page (should not be captured)</a></li>
</ul>
"""


class Handler(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"

    def log_message(self, *_):
        pass

    def do_GET(self):
        if self.path == "/":
            page = PAGE.encode()
            self.send_response(200)
            self.send_header("Content-Type", "text/html; charset=utf-8")
            self.send_header("Set-Cookie", "session=test123; Path=/")
            self.send_header("Content-Length", str(len(page)))
            self.end_headers()
            self.wfile.write(page)
            return
        if self.path == "/archive.zip":
            self.path = "/file.bin"
        if self.path not in ("/file.bin", "/norange.bin"):
            self.send_error(404)
            return
        start, end = 0, len(DATA) - 1
        header = self.headers.get("Range")
        ranged = header is not None and self.path == "/file.bin"
        if ranged:
            first, _, last = header.removeprefix("bytes=").partition("-")
            start = int(first)
            end = min(int(last), len(DATA) - 1) if last else len(DATA) - 1
            if start > end:
                self.send_error(416)
                return
            self.send_response(206)
            self.send_header("Content-Range", f"bytes {start}-{end}/{len(DATA)}")
        else:
            self.send_response(200)
        self.send_header("Content-Length", str(end - start + 1))
        self.send_header("Content-Type", "application/octet-stream")
        self.send_header("Content-Disposition", 'attachment; filename="test payload.bin"')
        if self.path == "/file.bin":
            self.send_header("Accept-Ranges", "bytes")
            self.send_header("ETag", ETAG)
        self.end_headers()
        rate = args.rate_kb * 1024 * (random.uniform(0.25, 1) if args.jitter else 1)
        position = start
        try:
            while position <= end:
                piece = DATA[position:min(position + CHUNK, end + 1)]
                self.wfile.write(piece)
                position += len(piece)
                time.sleep(len(piece) / rate)
        except (BrokenPipeError, ConnectionResetError):
            pass


ThreadingHTTPServer.daemon_threads = True
ThreadingHTTPServer(("127.0.0.1", args.port), Handler).serve_forever()
