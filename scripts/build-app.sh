#!/bin/zsh
# Builds Purrch.app into ./build and signs it.
#
#   scripts/build-app.sh [release|debug]
#
# Signing identity: set SIGN_IDENTITY to a "Developer ID Application: ..." identity for
# distribution builds. Without it the app is signed ad hoc, which is fine for local use but
# makes macOS re-ask for privacy permissions after every rebuild.
set -euo pipefail
cd "$(dirname "$0")/.."

CONFIG=${1:-release}
APP=build/Purrch.app
MASCOT=Resources/Branding/Mascot.png
ICON=Resources/AppIcon.icns

swift build -c "$CONFIG"
BIN="$(swift build -c "$CONFIG" --show-bin-path)/Purrch"

# Regenerate the icon whenever the mascot artwork changes.
if [[ ! -f "$ICON" || "$MASCOT" -nt "$ICON" ]]; then
  ICONSET=build/AppIcon.iconset
  rm -rf "$ICONSET"
  swift scripts/make-icon.swift "$ICONSET" "$MASCOT" Resources/Branding/Logo.png
  iconutil -c icns "$ICONSET" -o "$ICON"
  rm -rf "$ICONSET"
fi

rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp "$BIN" "$APP/Contents/MacOS/Purrch"
cp Resources/Info.plist "$APP/Contents/Info.plist"
cp "$ICON" "$APP/Contents/Resources/AppIcon.icns"
cp "$MASCOT" "$APP/Contents/Resources/Mascot.png"

if [[ -n "${SIGN_IDENTITY:-}" ]]; then
  codesign --force --options runtime --timestamp --sign "$SIGN_IDENTITY" "$APP"
else
  codesign --force --sign - "$APP"
fi
codesign --verify --strict "$APP"
echo "Built $APP ($CONFIG)"
