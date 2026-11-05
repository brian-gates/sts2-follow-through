#!/usr/bin/env bash
# Build Follow Through (Release) and stage the Steam Workshop workspace in dist/workshop/:
#   workshop.json  (from workshop/workshop.json)
#   image.png      (preview, from workshop/preview.jpg)
#   content/       (STS2_FollowThrough.dll + STS2_FollowThrough.json, exactly what subscribers get)
#   mod_id.txt     (copied from workshop/mod_id.txt once the item exists, so uploads update it)
# It does NOT upload unless you pass --upload. Uploading uses MegaCrit's sts2-mod-uploader and talks to
# Steam directly, so it must run locally with the Steam client open and logged in (never in CI).
#
# Env: STS2GameDir (game install; default: per-OS Steam path in the csproj)
#      MODUPLOADER (uploader CLI; default ./tools/ModUploader)
set -euo pipefail
cd "$(dirname "$0")/.."

UPLOAD=0
[[ "${1:-}" == "--upload" ]] && UPLOAD=1

WS="dist/workshop"
echo "==> Building Release"
if [[ -n "${STS2GameDir:-}" ]]; then
  dotnet build STS2_FollowThrough.csproj -c Release -o out -p:STS2GameDir="$STS2GameDir"
else
  dotnet build STS2_FollowThrough.csproj -c Release -o out
fi

echo "==> Staging $WS"
rm -rf "$WS"
mkdir -p "$WS/content"
cp out/STS2_FollowThrough.dll "$WS/content/STS2_FollowThrough.dll"
cp mod_manifest.json "$WS/content/STS2_FollowThrough.json"
cp workshop/workshop.json "$WS/workshop.json"
# The uploader expects image.png; Steam wants the preview under 1 MB.
if command -v sips >/dev/null; then sips -s format png workshop/preview.jpg --out "$WS/image.png" >/dev/null
elif command -v magick >/dev/null; then magick workshop/preview.jpg "$WS/image.png"
else echo "need sips (macOS) or ImageMagick to make image.png" >&2; exit 1; fi
size=$(wc -c < "$WS/image.png" | tr -d ' ')
(( size < 1048576 )) || { echo "image.png is $size bytes; Steam wants < 1 MB" >&2; exit 1; }
[[ -f workshop/mod_id.txt ]] && cp workshop/mod_id.txt "$WS/mod_id.txt"

# Guard: never ship debug injection settings or a config file.
if grep -q "debug_inject" "$WS/content/STS2_FollowThrough.json"; then echo "debug keys in manifest?" >&2; exit 1; fi

echo "    $(cd "$WS" && find . -type f | sort | tr '\n' ' ')"

UPLOADER="${MODUPLOADER:-./tools/ModUploader}"
ABS_WS="$(cd "$WS" && pwd)"
if (( UPLOAD )); then
  [[ -x "$UPLOADER" ]] || { echo "ModUploader not found at $UPLOADER (set MODUPLOADER)" >&2; exit 1; }
  echo "==> Uploading via $UPLOADER (Steam must be running and logged in)"
  # Steamworks reads steam_appid.txt from the working directory and loads libsteam_api next to the binary.
  UPLOADER_DIR="$(cd "$(dirname "$UPLOADER")" && pwd)"
  ( cd "$UPLOADER_DIR" && "./$(basename "$UPLOADER")" upload -w "$ABS_WS" )
  if [[ -f "$WS/mod_id.txt" ]]; then
    cp "$WS/mod_id.txt" workshop/mod_id.txt
    echo "==> Workshop item id: $(cat workshop/mod_id.txt) (saved to workshop/mod_id.txt; commit it)"
  fi
else
  cat <<EOM

==> Staged, not uploaded. To publish (Steam client open and logged in):
      ./scripts/package-workshop.sh --upload
    which runs: (cd <uploader dir> && ./ModUploader upload -w "$ABS_WS")
    First upload creates a NEW Workshop item and writes $WS/mod_id.txt; the script copies it to
    workshop/mod_id.txt. Commit that file so later uploads update the same item.
EOM
fi
