#!/usr/bin/env bash
# Build and install Follow Through into the game's local mods/ folder (macOS layout).
# Refuses while the game runs: overwriting a loaded mod DLL in place crashes the game. The DLL is staged
# next to its destination and moved into place atomically.
set -euo pipefail
cd "$(dirname "$0")/.."
GAME_DIR="${STS2GameDir:-$HOME/Library/Application Support/Steam/steamapps/common/Slay the Spire 2}"
MODS_DIR="${MODS_DIR:-$GAME_DIR/SlayTheSpire2.app/Contents/MacOS/mods}"

if pgrep -x "Slay the Spire 2" >/dev/null || pgrep -f "SlayTheSpire2.app/Contents/MacOS" >/dev/null; then
  echo "Slay the Spire 2 is running; quit it first (mods load at startup)." >&2
  exit 1
fi

./scripts/build.sh
mkdir -p "$MODS_DIR"
cp out/STS2_FollowThrough.dll "$MODS_DIR/.STS2_FollowThrough.dll.staged"
cp mod_manifest.json "$MODS_DIR/.STS2_FollowThrough.json.staged"
mv -f "$MODS_DIR/.STS2_FollowThrough.dll.staged" "$MODS_DIR/STS2_FollowThrough.dll"
mv -f "$MODS_DIR/.STS2_FollowThrough.json.staged" "$MODS_DIR/STS2_FollowThrough.json"
echo "Installed to $MODS_DIR"
