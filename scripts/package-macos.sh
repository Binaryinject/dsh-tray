#!/bin/bash
# Package the macOS build into a .app bundle and a DMG.
# Usage: package-macos.sh <rid> <version>
set -euo pipefail

RID="$1"
VERSION="${2:-0.1.0}"

PUB="bin/Release/net9.0-macos/$RID/publish"
APP="dsh-tray.app"
DMG="dsh-tray-$RID.dmg"

# 1. Locate the built binary (bare executable or inside a prebuilt .app)
BIN=""
if [ -f "$PUB/dsh-tray" ]; then
  BIN="$PUB/dsh-tray"
elif [ -f "$PUB/$APP/Contents/MacOS/dsh-tray" ]; then
  BIN="$PUB/$APP/Contents/MacOS/dsh-tray"
else
  echo "error: built binary not found under $PUB" >&2
  find "$PUB" -maxdepth 4
  exit 1
fi

# 2. Assemble a fresh .app bundle
rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp "$BIN" "$APP/Contents/MacOS/dsh-tray"
chmod +x "$APP/Contents/MacOS/dsh-tray"

# 3. Info.plist (LSUIElement => menu-bar only, no dock icon)
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

# 4. Icon (.icns) from the high-res master
if [ -f icon-1024.png ]; then
  ICONSET="icon.iconset"
  rm -rf "$ICONSET"
  mkdir "$ICONSET"
  for s in 16 32 128 256 512; do
    sips -z "$s" "$s" icon-1024.png --out "$ICONSET/icon_${s}x${s}.png" >/dev/null
    s2=$((s * 2))
    sips -z "$s2" "$s2" icon-1024.png --out "$ICONSET/icon_${s}x${s}@2x.png" >/dev/null
  done
  iconutil -c icns "$ICONSET" -o "$APP/Contents/Resources/dsh-tray.icns"
fi

# 5. DMG with an Applications symlink (drag-to-install)
rm -f "$DMG"
STAGE="dmg-stage"
rm -rf "$STAGE"
mkdir -p "$STAGE"
cp -R "$APP" "$STAGE/"
ln -s /Applications "$STAGE/Applications"
hdiutil create -volname "dsh-tray" -srcfolder "$STAGE" -ov -format UDZO "$DMG"
echo "created $DMG"
