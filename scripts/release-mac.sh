#!/bin/bash
# Build a self-contained Promptile.app for macOS (arm64).
# Output: dist/Promptile.app  and  dist/Promptile.dmg
set -e

RUNTIME="osx-arm64"
CONFIG="Release"
BUNDLE="dist/Promptile.app"
CONTENTS="$BUNDLE/Contents"
MACOS="$CONTENTS/MacOS"
PROJECT="src/Promptile.Host/Promptile.Host.csproj"
WWWROOT="src/Promptile.Host/wwwroot"

echo "→ Cleaning dist/"
rm -rf dist
mkdir -p "$MACOS"

echo "→ Publishing self-contained $RUNTIME build..."
dotnet publish "$PROJECT" \
  -c "$CONFIG" \
  -r "$RUNTIME" \
  --self-contained true \
  --output "$MACOS" \
  /p:SkipBundleAndSign=true

echo "→ Assembling bundle..."
cp src/Promptile.Host/Info.plist "$CONTENTS/"
mkdir -p "$CONTENTS/Resources"
cp src/Promptile.Host/Promptile.icns "$CONTENTS/Resources/"
rsync -a "$WWWROOT/" "$MACOS/wwwroot/"

echo "→ Signing (ad-hoc)..."
bash sign-app.sh "$BUNDLE"

echo "→ Creating DMG..."
hdiutil create \
  -volname "Promptile" \
  -srcfolder "$BUNDLE" \
  -ov -format UDZO \
  "dist/Promptile.dmg"

echo ""
echo "Done."
echo "  App:  $BUNDLE"
echo "  DMG:  dist/Promptile.dmg"
