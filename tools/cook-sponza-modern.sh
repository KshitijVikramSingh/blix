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
# COOKED is where the runtime-ready .blix* land (the in-repo Assets dir by
# default, or BLIX_SPONZA_ASSETS when set — e.g. an external SSD).
COOKED="${BLIX_SPONZA_ASSETS:-$REPO/src/Demos/Blix.Demos.SponzaModern/Assets}"
# SRC is the raw-source tree to cook FROM. BLIX_SPONZA_SRC, else a "-src" sibling
# of COOKED when it exists (the split layout: sponza-src/ holds .png/.bin/.gltf/
# .hdr, sponza/ holds the cooked output), else COOKED itself (legacy in-place,
# sources + cooked interleaved). When SRC != COOKED the cook writes out-of-place
# via --out, mirroring the source tree's structure into COOKED.
SRC="${BLIX_SPONZA_SRC:-${COOKED%/}-src}"
[[ -d "$SRC" ]] || SRC="$COOKED"

if [[ ! -d "$SRC" ]]; then
    echo "Sponza source dir not found: $SRC" >&2
    echo "Run tools/setup-sponza-modern.sh first." >&2
    exit 1
fi

# OUTDIR is empty for in-place cooking; set to COOKED for out-of-place. The
# wrapper appends --out only when set (keeps empty-array expansion out of the
# way under bash 3.2 + set -u).
OUTDIR=""
if [[ "$SRC" != "$COOKED" ]]; then
    echo "Out-of-place cook: sources '$SRC' -> cooked '$COOKED'"
    OUTDIR="$COOKED"
    mkdir -p "$COOKED"
fi

cook() {
    if [[ -n "$OUTDIR" ]]; then
        dotnet run --project "$REPO/src/Blix.Tools.Cook/Blix.Tools.Cook.csproj" \
            --configuration Release -- "$@" --out "$OUTDIR"
    else
        dotnet run --project "$REPO/src/Blix.Tools.Cook/Blix.Tools.Cook.csproj" \
            --configuration Release -- "$@"
    fi
}

cook textures "$SRC"

HDR="$SRC/textures/sky_hdr.hdr"
if [[ -f "$HDR" ]]; then
    cook probe "$HDR"
else
    echo "No HDR sky at $HDR -- skipping .blixprobe cook."
fi

cook mesh "$SRC"
