#!/usr/bin/env bash
# Build Follow Through (Release) into ./out. Env: STS2GameDir (default: macOS Steam install).
set -euo pipefail
cd "$(dirname "$0")/.."
GAME_DIR="${STS2GameDir:-$HOME/Library/Application Support/Steam/steamapps/common/Slay the Spire 2}"
dotnet build STS2_FollowThrough.csproj -c Release -o out -p:STS2GameDir="$GAME_DIR"
