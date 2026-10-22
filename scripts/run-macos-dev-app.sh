#!/bin/bash
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
# Use a fixed installation path and identity for permission testing between builds.
OUTPUT="$(mktemp -d)"
bash "$ROOT/scripts/package-macos.sh" 1.0.0 "$OUTPUT"
APP="$(find "$OUTPUT" -type d -name CSharpClipboard.app -print -quit)"
[[ -n "$APP" ]] || exit 1
mkdir -p "$HOME/Applications"
DEST="$HOME/Applications/CSharpClipboard.app"
if pgrep -f "$DEST/Contents/MacOS/CSharpClipboard" >/dev/null; then
  echo 'Quit the running CSharpClipboard app before rebuilding its installed bundle.' >&2
  exit 1
fi
if [[ -d "$DEST" ]]; then mv "$DEST" "$OUTPUT/previous.app"; fi
ditto "$APP" "$DEST"
codesign --verify --deep --strict "$DEST"
open "$DEST"
