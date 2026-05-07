#!/bin/bash
# Ad-hoc signs Promptile.app for local development.
# Usage: sign-app.sh [bundle-path]  (defaults to Debug build)
set -e
BUNDLE="${1:-src/Promptile.Host/bin/Debug/net9.0/Promptile.app}"
MACOS="$BUNDLE/Contents/MacOS"

# Remove non-macOS runtime directories — they confuse codesign
if [ -d "$MACOS/runtimes" ]; then
    find "$MACOS/runtimes" -mindepth 1 -maxdepth 1 -type d \
        ! -name 'osx*' ! -name 'unix*' -exec rm -rf {} +
fi

# Sign every file individually first
find "$BUNDLE" -type f | while read -r f; do
    codesign --force --sign - "$f" 2>/dev/null || true
done

# Sign the bundle
codesign --force --sign - "$BUNDLE"
echo "Signed: $BUNDLE"
