#!/bin/bash
# Builds dist/WinDM-win-x64.zip: a single self-contained WinDM.exe (no .NET install needed on the PC)
# plus the browser extension. Runs on macOS, Linux or Windows (Git Bash) with the .NET 10 SDK.
# Usage: scripts/publish.sh [runtime]   e.g. win-x64 (default) or win-arm64
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
RUNTIME="${1:-win-x64}"
OUT="$ROOT/dist/$RUNTIME"
cd "$ROOT"

rm -rf "$OUT"
dotnet publish src/WinDM -c Release -r "$RUNTIME" --self-contained true \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true \
  -p:DebugType=none -o "$OUT/WinDM"

# The Chrome/Edge extension is shared with MacDM; it talks to WinDM over the same local bridge.
cp -R ../MacDM/Extensions/chrome "$OUT/Browser Extension"
cp "$ROOT/INSTALL.txt" "$OUT/INSTALL.txt"

rm -f "$ROOT/dist/WinDM-$RUNTIME.zip"
(cd "$OUT" && zip -qr "$ROOT/dist/WinDM-$RUNTIME.zip" WinDM "Browser Extension" INSTALL.txt)
echo "Built $ROOT/dist/WinDM-$RUNTIME.zip"
