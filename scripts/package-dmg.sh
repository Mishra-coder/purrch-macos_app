#!/bin/zsh
# Packages build/Purrch.app into a drag-to-install disk image at build/Purrch-<version>.dmg.
# Run scripts/build-app.sh first. For public distribution, sign with a Developer ID
# (SIGN_IDENTITY) and notarize the resulting DMG with `xcrun notarytool submit --wait`.
set -euo pipefail
cd "$(dirname "$0")/.."

APP=build/Purrch.app
[[ -d "$APP" ]] || { echo "error: $APP not found, run scripts/build-app.sh first" >&2; exit 1; }

VERSION=$(/usr/libexec/PlistBuddy -c "Print CFBundleShortVersionString" "$APP/Contents/Info.plist")
DMG="build/Purrch-$VERSION.dmg"
STAGING=$(mktemp -d)
trap 'rm -rf "$STAGING"' EXIT

cp -R "$APP" "$STAGING/"
ln -s /Applications "$STAGING/Applications"
rm -f "$DMG"
diskutil image create from --format UDZO --volumeName "Purrch $VERSION" "$STAGING" "$DMG" >/dev/null 2>&1
echo "Packaged $DMG"
