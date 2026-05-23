#!/usr/bin/env bash
# Populates src/Blix.Demos.SponzaModern/Assets/ from a local copy of the
# Khronos Intel Sponza repo packs.
#
# Usage:
#   tools/setup-sponza-modern.sh [SOURCE_ROOT]
#
# SOURCE_ROOT defaults to ~/Downloads. Expected layout under SOURCE_ROOT:
#   main_sponza/        (the main scene)
#   pkg_a_curtains/     (optional add-on)
#   pkg_b_ivy/          (optional add-on)
#   pkg_c_trees/        (optional add-on)
#
# Each pack ships glTF + .bin + FBX + USD + .max + multi-format textures.
# We copy only the glTF + binary + PNG/JPG textures the runtime actually
# loads -- the FBX/USD/.max variants are skipped (~2x size saving).
#
# Where the original packs live:
#   https://github.com/KhronosGroup/glTF-Sample-Assets/tree/main/Models/IntelSponza

set -euo pipefail

SOURCE_ROOT="${1:-$HOME/Downloads}"
DEST="$(cd "$(dirname "$0")/.." && pwd)/src/Blix.Demos.SponzaModern/Assets"

if [[ ! -d "$SOURCE_ROOT" ]]; then
    echo "Source root not found: $SOURCE_ROOT" >&2
    exit 1
fi

mkdir -p "$DEST"

copy_pack() {
    local src="$1"
    local dest="$2"
    if [[ ! -d "$src" ]]; then
        echo "  skipping (not present): $src"
        return 0
    fi
    mkdir -p "$dest"
    # glTF + .bin from the pack root. Use rsync instead of cp because
    # APFS-on-Mac's cp does clonefile-style metadata copies for large files
    # that can land as zero-byte sparse artefacts on the destination volume.
    # rsync's default "fully read source, fully write dest" copy is slower
    # but reliable.
    rsync -a --no-links "$src"/*.gltf "$src"/*.bin "$dest/" 2>/dev/null || true
    # textures subdirectory if present
    if [[ -d "$src/textures" ]]; then
        mkdir -p "$dest/textures"
        rsync -a --no-links \
            --include="*.png" --include="*.jpg" --include="*.jpeg" --exclude="*" \
            "$src/textures/" "$dest/textures/"
    fi
    echo "  copied: $(basename "$src") -> $(basename "$dest")"
}

echo "Setting up Sponza Modern assets from: $SOURCE_ROOT"
copy_pack "$SOURCE_ROOT/main_sponza"    "$DEST/main_sponza"
copy_pack "$SOURCE_ROOT/pkg_a_curtains" "$DEST/curtains"
copy_pack "$SOURCE_ROOT/pkg_b_ivy"      "$DEST/ivy"
copy_pack "$SOURCE_ROOT/pkg_c_trees"    "$DEST/trees"

# Reuse the Walkthrough's HDR sky probe (kloppenheim) so the new demo can
# bake IBL probes from frame one. Skip if it isn't present.
WALK_HDR="$(cd "$(dirname "$0")/.." && pwd)/src/Blix.Demos.Walkthrough/Assets/textures/sky_hdr.hdr"
if [[ -f "$WALK_HDR" ]]; then
    mkdir -p "$DEST/textures"
    cp -p "$WALK_HDR" "$DEST/textures/sky_hdr.hdr"
    echo "  copied: walkthrough sky_hdr.hdr"
fi

du -sh "$DEST" 2>/dev/null | awk '{print "Total: " $1}'
echo "Done."
