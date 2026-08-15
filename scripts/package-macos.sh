#!/bin/bash
# Package the macOS build into a .app bundle and a DMG.
# Usage: package-macos.sh <rid> <version>
set -euo pipefail

RID="$1"
VERSION="${2:-0.1.2}"

BASE="bin/Release/net10.0-macos"
APP="dsh-tray.app"
DMG="dsh-tray-$RID.dmg"

echo "== output tree =="
find "$BASE" -maxdepth 5 2>/dev/null || true

rm -rf "$APP"

# 1. Locate the publish output: a prebuilt .app bundle, or a self-contained
#    folder containing the dsh-tray executable (+ its runtime files).
BUNDLE=$(find "$BASE" -type d -name 'dsh-tray.app' 2>/dev/null | head -1)
if [ -n "$BUNDLE" ]; then
  echo "using bundle: $BUNDLE"
  cp -R "$BUNDLE" "$APP"
else
  BIN=$(find "$BASE" -type f -name 'dsh-tray' 2>/dev/null | head -1)
  if [ -z "$BIN" ]; then
    echo "ERROR: neither dsh-tray.app nor dsh-tray binary found under $BASE" >&2
    exit 1
  fi
  echo "using binary dir: $(dirname "$BIN")"
  mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
  cp -R "$(dirname "$BIN")/." "$APP/Contents/MacOS/"
  chmod +x "$APP/Contents/MacOS/dsh-tray"
fi

# 2. Info.plist (LSUIElement => menu-bar only, no dock icon)
mkdir -p "$APP/Contents"
cat > "$APP/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleName</key><string>dsh-tray</string>
  <key>CFBundleDisplayName</key><string>DeepSeek Harness Tray</string>
  <key>CFBundleIdentifier</key><string>io.dshtray.app</string>
  <key>CFBundleVersion</key><string>${VERSION}</string>
  <key>CFBundleShortVersionString</key><string>${VERSION}</string>
  <key>CFBundleExecutable</key><string>dsh-tray</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleIconFile</key><string>dsh-tray</string>
  <key>LSUIElement</key><true/>
</dict>
</plist>
PLIST

# 3. Icon (.icns) from the high-res master — best effort, never fatal
mkdir -p "$APP/Contents/Resources"
(
  set +e
  if [ -f icon-1024.png ] && command -v sips >/dev/null 2>&1 && command -v iconutil >/dev/null 2>&1; then
    ICONSET="icon.iconset"
    rm -rf "$ICONSET"
    mkdir "$ICONSET"
    for s in 16 32 128 256 512; do
      sips -z "$s" "$s" icon-1024.png --out "$ICONSET/icon_${s}x${s}.png" >/dev/null 2>&1
      s2=$((s * 2))
      sips -z "$s2" "$s2" icon-1024.png --out "$ICONSET/icon_${s}x${s}@2x.png" >/dev/null 2>&1
    done
    iconutil -c icns "$ICONSET" -o "$APP/Contents/Resources/dsh-tray.icns" >/dev/null 2>&1
  fi
) || true

# 4. DMG with an Applications symlink (drag-to-install)
rm -f "$DMG"
STAGE="dmg-stage"
rm -rf "$STAGE"
mkdir -p "$STAGE"
cp -R "$APP" "$STAGE/"
ln -sf /Applications "$STAGE/Applications"
hdiutil create -volname "dsh-tray" -srcfolder "$STAGE" -ov -format UDZO "$DMG"
echo "== DMG =="
ls -la "$DMG"
