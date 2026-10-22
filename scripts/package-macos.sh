#!/bin/bash
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
VERSION="${1:-1.0.0}"
[[ "$VERSION" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]] || { echo 'Invalid version' >&2; exit 1; }
[[ "$(uname -s)" == Darwin ]] || { echo 'macOS with Xcode 26+ required' >&2; exit 1; }
[[ "$(uname -m)" == arm64 ]] || { echo 'The supported package target is macOS arm64' >&2; exit 1; }
OUTPUT="${2:-$ROOT/artifacts}"
mkdir -p "$OUTPUT"
STAGE="$(mktemp -d "$OUTPUT/macos.XXXXXX")"
APP="$STAGE/CSharpClipboard.app"
ZIP="$OUTPUT/CSharpClipboard-$VERSION-osx-arm64-adhoc.zip"
[[ ! -e "$ZIP" ]] || { echo "Package already exists: $ZIP" >&2; exit 1; }
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Frameworks" "$APP/Contents/Resources/managed"
dotnet restore "$ROOT/src/CSharpClipboard.App" -r osx-arm64 --disable-parallel
dotnet publish "$ROOT/src/CSharpClipboard.App" -c Release -r osx-arm64 --self-contained true --no-restore -m:1 -p:Version="$VERSION" -p:DebugType=None -o "$STAGE/publish"
xcrun swiftc -swift-version 5 -emit-library -O -target arm64-apple-macosx26.0 -module-name CSharpClipboardBridge \
  "$ROOT/native/macos/ClipboardBridge.swift" -o "$APP/Contents/Frameworks/libCSharpClipboardBridge.dylib" \
  -framework AppKit -framework ApplicationServices -framework Carbon -framework ServiceManagement
cp "$ROOT/native/macos/Info.plist" "$APP/Contents/Info.plist"
/usr/libexec/PlistBuddy -c "Set :CFBundleVersion $VERSION" "$APP/Contents/Info.plist"
/usr/libexec/PlistBuddy -c "Set :CFBundleShortVersionString $VERSION" "$APP/Contents/Info.plist"
ICONSET="$STAGE/AppIcon.iconset"
mkdir -p "$ICONSET"
xcrun swift "$ROOT/native/macos/MakeIcon.swift" "$STAGE/icon.png"
for size in 16 32 128 256 512; do
  sips -z "$size" "$size" "$STAGE/icon.png" --out "$ICONSET/icon_${size}x${size}.png" >/dev/null
  double=$((size * 2))
  sips -z "$double" "$double" "$STAGE/icon.png" --out "$ICONSET/icon_${size}x${size}@2x.png" >/dev/null
done
iconutil -c icns "$ICONSET" -o "$APP/Contents/Resources/AppIcon.icns"
cp "$ROOT/docs/ThirdPartyNotices.md" "$APP/Contents/Resources/ThirdPartyNotices.md"
python3 "$ROOT/scripts/copy-package-notices.py" "$ROOT/src/CSharpClipboard.App/obj/project.assets.json" "$APP/Contents/Resources/notices"
# Managed data physically belongs in Resources; native libraries belong in Frameworks.
# Relative links preserve .NET apphost's base-directory probing without duplicating files.
for file in "$STAGE/publish/"*; do
  name="$(basename "$file")"
  if [[ "$name" == CSharpClipboard.App ]]; then
    cp "$file" "$APP/Contents/MacOS/CSharpClipboard"
  # The apphost resolves these three bootstrap files to establish its base directory.
  # Keeping them as symlinks into Resources makes a self-contained app look for its
  # runtime libraries relative to Resources rather than Contents/MacOS.
  elif [[ "$name" == CSharpClipboard.App.dll || "$name" == CSharpClipboard.App.deps.json || "$name" == CSharpClipboard.App.runtimeconfig.json ]]; then
    cp "$file" "$APP/Contents/MacOS/$name"
  elif file -b "$file" | grep -q 'Mach-O'; then
    cp "$file" "$APP/Contents/Frameworks/$name"
    ln -s "../Frameworks/$name" "$APP/Contents/MacOS/$name"
  else
    cp -R "$file" "$APP/Contents/Resources/managed/$name"
    ln -s "../Resources/managed/$name" "$APP/Contents/MacOS/$name"
  fi
done
chmod +x "$APP/Contents/MacOS/CSharpClipboard"
for file in "$APP/Contents/Frameworks/"*; do
  if file -b "$file" | grep -q 'Mach-O'; then codesign --force --sign - "$file"; fi
done
# These are physical apphost bootstrap files in MacOS, so sign them before sealing
# the enclosing bundle just like the nested native code.
for file in CSharpClipboard.App.dll CSharpClipboard.App.deps.json CSharpClipboard.App.runtimeconfig.json; do
  codesign --force --sign - "$APP/Contents/MacOS/$file"
done
codesign --force --sign - "$APP"
codesign --verify --deep --strict --verbose=2 "$APP"
codesign -d -r- "$APP" 2> "$STAGE/designated-requirement.txt"
"$APP/Contents/MacOS/CSharpClipboard" --version
"$APP/Contents/MacOS/CSharpClipboard" --bridge-check
ditto -c -k --sequesterRsrc --keepParent "$APP" "$ZIP"
shasum -a 256 "$ZIP" > "$ZIP.sha256"
echo "$APP"
echo "$ZIP"
