#!/usr/bin/env bash
# Cooks every PNG/JPEG under the SponzaModern demo's Assets/ directory into
# a side-by-side .blixtex file. Runtime loads .blixtex directly with no
# decode cost (see BlixTex format in Blix.Graphics.Images/BlixTex.cs).
#
# Run once after setup-sponza-modern.sh; rerun whenever you change a PNG.
# Existing .blixtex files older than their source PNG are re-cooked; up-to-
# date ones are skipped.
#
# Run from the repo root.

set -euo pipefail

REPO="$(cd "$(dirname "$0")/.." && pwd)"
# Cooks the same shared-SSD pack dir the setup script populated and the demos
# read at runtime. Defaults to the external SSD; export BLIX_SPONZA_ASSETS to
# override (e.g. the iMac mounting the drive under a different name).
export BLIX_SPONZA_ASSETS="${BLIX_SPONZA_ASSETS:-/Volumes/data bag/blix-assets/sponza}"
ASSETS="$BLIX_SPONZA_ASSETS"

if [[ ! -d "$ASSETS" ]]; then
    echo "Sponza Modern assets dir not found: $ASSETS" >&2
    echo "Run tools/setup-sponza-modern.sh first." >&2
    exit 1
fi

dotnet run --project "$REPO/src/Blix.Tools.Cook/Blix.Tools.Cook.csproj" \
    --configuration Release -- textures "$ASSETS"

HDR="$ASSETS/textures/sky_hdr.hdr"
if [[ -f "$HDR" ]]; then
    dotnet run --project "$REPO/src/Blix.Tools.Cook/Blix.Tools.Cook.csproj" \
        --configuration Release -- probe "$HDR"
else
    echo "No HDR sky at $HDR -- skipping .blixprobe cook."
fi

dotnet run --project "$REPO/src/Blix.Tools.Cook/Blix.Tools.Cook.csproj" \
    --configuration Release -- mesh "$ASSETS"
