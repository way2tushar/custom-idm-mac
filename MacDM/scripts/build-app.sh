#!/bin/bash
# Builds build/MacDM.app (with the embedded Safari web extension) using only the
# Command Line Tools: SwiftPM for the app, swiftc for the extension, ad-hoc signing.
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
OUT="$ROOT/build"
APP="$OUT/MacDM.app"
APPEX="$APP/Contents/PlugIns/MacDM Safari Extension.appex"
BUNDLE_ID="com.macdm.app"
VERSION="1.0.0"
ARCH="$(uname -m)"

cd "$ROOT"
swift build -c release --product MacDM

rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources" "$APPEX/Contents/MacOS" "$APPEX/Contents/Resources"
cp ".build/release/MacDM" "$APP/Contents/MacOS/MacDM"
cp Resources/MacDM.icns "$APP/Contents/Resources/MacDM.icns"

cat > "$APP/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleName</key><string>MacDM</string>
  <key>CFBundleDisplayName</key><string>MacDM</string>
  <key>CFBundleIdentifier</key><string>$BUNDLE_ID</string>
  <key>CFBundleExecutable</key><string>MacDM</string>
  <key>CFBundleIconFile</key><string>MacDM</string>
  <key>CFBundleInfoDictionaryVersion</key><string>6.0</string>
  <key>CFBundleSupportedPlatforms</key><array><string>MacOSX</string></array>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleShortVersionString</key><string>$VERSION</string>
  <key>CFBundleVersion</key><string>1</string>
  <key>LSMinimumSystemVersion</key><string>14.0</string>
  <key>LSApplicationCategoryType</key><string>public.app-category.utilities</string>
  <key>NSHighResolutionCapable</key><true/>
  <key>CFBundleURLTypes</key>
  <array>
    <dict>
      <key>CFBundleURLName</key><string>MacDM download link</string>
      <key>CFBundleURLSchemes</key><array><string>macdm</string></array>
    </dict>
  </array>
</dict>
</plist>
PLIST

# Safari web extension: a sandboxed app extension whose resources are the web extension files.
swiftc -O -parse-as-library -application-extension \
  -module-name MacDMSafariExtension \
  -target "$ARCH-apple-macos14.0" \
  -framework SafariServices -framework AppKit \
  -Xlinker -e -Xlinker _NSExtensionMain \
  Extensions/safari/SafariWebExtensionHandler.swift \
  -o "$APPEX/Contents/MacOS/MacDM Safari Extension"
cp Extensions/safari/Resources/* "$APPEX/Contents/Resources/"
cp Extensions/chrome/grabber.js Extensions/chrome/media-utils.js "$APPEX/Contents/Resources/" # shared with Chrome
cp -R Extensions/chrome/icons "$APPEX/Contents/Resources/"

cat > "$APPEX/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleName</key><string>MacDM Safari Extension</string>
  <key>CFBundleDisplayName</key><string>MacDM</string>
  <key>CFBundleIdentifier</key><string>$BUNDLE_ID.safari-extension</string>
  <key>CFBundleExecutable</key><string>MacDM Safari Extension</string>
  <key>CFBundleInfoDictionaryVersion</key><string>6.0</string>
  <key>CFBundleSupportedPlatforms</key><array><string>MacOSX</string></array>
  <key>CFBundlePackageType</key><string>XPC!</string>
  <key>CFBundleShortVersionString</key><string>$VERSION</string>
  <key>CFBundleVersion</key><string>1</string>
  <key>LSMinimumSystemVersion</key><string>14.0</string>
  <key>NSExtension</key>
  <dict>
    <key>NSExtensionPointIdentifier</key><string>com.apple.Safari.web-extension</string>
    <key>NSExtensionPrincipalClass</key><string>MacDMSafariExtension.SafariWebExtensionHandler</string>
  </dict>
  <key>NSAppTransportSecurity</key>
  <dict><key>NSAllowsLocalNetworking</key><true/></dict>
</dict>
</plist>
PLIST

cat > "$OUT/extension.entitlements" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>com.apple.security.app-sandbox</key><true/>
  <key>com.apple.security.network.client</key><true/>
</dict>
</plist>
PLIST

# Sign inside-out. Ad-hoc signatures need Safari's "Allow Unsigned Extensions" (see README).
SIGN_ID="${MACDM_SIGN_IDENTITY:--}"
codesign --force --sign "$SIGN_ID" --entitlements "$OUT/extension.entitlements" "$APPEX"
codesign --force --sign "$SIGN_ID" "$APP"
codesign --verify --deep --strict "$APP"

echo "Built $APP"
