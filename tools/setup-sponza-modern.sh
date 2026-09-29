#!/usr/bin/env bash
# Extracts the Khronos Intel Sponza packs into a SOURCE tree, then hands over to
# tools/cook-sponza-modern.sh, which owns every cook flag. This script decides
# nothing about how a pack is cooked; it only gets the raw files into place.
#
#   BLIX_SPONZA_ASSETS  the COOKED tree the demo reads. Required, as it is for
#                       the cook: there is no default, because the old one was a
#                       directory belonging to a deleted demo.
#   BLIX_SPONZA_SRC     the SOURCE tree, defaulting to "<cooked>-src".
#
# Usage:
#   tools/setup-sponza-modern.sh [SOURCE_ROOT]
#
# SOURCE_ROOT defaults to ~/Downloads. The script handles two layouts:
#   1. Pre-extracted directories (legacy):
#        main_sponza/    pkg_a_curtains/  pkg_b_ivy/  pkg_c_trees/
#        pkg_d_10k_candles/
#   2. Khronos-distributed .zip archives (current):
#        main_sponza.zip pkg_a_curtains.zip pkg_b_ivy1.zip pkg_c_trees.zip
#        pkg_d_10k_candles.zip
#      Each is unzipped to a scratch dir, the runtime files are pulled out,
#      and the scratch dir is deleted before the next pack — keeping peak
#      disk usage to one pack at a time so a 30+GB total set doesn't need
#      30GB of free disk.
#
# Each pack ships glTF + .bin + FBX + USD + .max + multi-format textures.
# We copy only the glTF + binary + PNG/JPG textures the runtime actually
# loads — the FBX/USD/.max variants are skipped (~3x size saving).
#
# Packs without a glTF (pkg_e_knight_anim, pkg_f_flood) are skipped — they
# ship Alembic/USD/blend only and the engine's glTF importer can't load
# them. They'd need a separate Alembic/USD pipeline that doesn't exist yet.
#
# Sky probes are cooked from every .hdr in $BLIX_SPONZA_SRC/textures. None ships
# with the packs; drop the ones you want there (kloppenheim and the Poly Haven
# skies the demo knows by name) before running.
#
# Where the original packs live:
#   https://github.com/KhronosGroup/glTF-Sample-Assets/tree/main/Models/IntelSponza

set -euo pipefail

SOURCE_ROOT="${1:-$HOME/Downloads}"
REPO_ROOT="$(cd "$(dirname "$0")/.." && pwd)"
if [[ -z "${BLIX_SPONZA_ASSETS:-}" ]]; then
    echo "setup-sponza-modern: BLIX_SPONZA_ASSETS is not set. It names the COOKED tree;" >&2
    echo "  the raw packs go to BLIX_SPONZA_SRC, defaulting to \"<cooked>-src\"." >&2
    exit 1
fi
COOKED="$BLIX_SPONZA_ASSETS"
SRC="${BLIX_SPONZA_SRC:-${COOKED%/}-src}"
SCRATCH="$(mktemp -d "${TMPDIR:-/tmp}/blix-sponza-extract.XXXXXX")"
trap 'rm -rf "$SCRATCH"' EXIT

if [[ ! -d "$SOURCE_ROOT" ]]; then
    echo "Source root not found: $SOURCE_ROOT" >&2
    exit 1
fi

mkdir -p "$SRC"

# Returns the path to a usable pack source — either an existing directory
# under SOURCE_ROOT, or a freshly-extracted scratch directory from a sibling
# .zip. Caller is responsible for cleanup; the scratch directory is wiped
# by the EXIT trap when the script ends.
#
# Args: pack_name [zip_name]
#   pack_name : directory name expected inside the archive (and previously
#               accepted by the legacy SOURCE_ROOT/<pack_name>/ layout).
#   zip_name  : optional override when the archive's stem differs from the
#               extracted directory name (pkg_b_ivy1.zip → pkg_b_ivy/).
locate_pack() {
    local pack="$1"
    local zip_base="${2:-$pack}"
    if [[ -d "$SOURCE_ROOT/$pack" ]]; then
        echo "$SOURCE_ROOT/$pack"
        return 0
    fi
    local zip="$SOURCE_ROOT/$zip_base.zip"
    if [[ ! -f "$zip" ]]; then
        return 1
    fi
    local out="$SCRATCH/$pack"
    echo "  extracting $(basename "$zip") ..." >&2
    # -qq: very quiet. Errors still go to stderr.
    unzip -qq "$zip" -d "$SCRATCH"
    if [[ ! -d "$out" ]]; then
        # Some zips wrap their contents in a directory matching the zip stem
        # rather than the pack name (pkg_b_ivy1.zip happens to extract to
        # pkg_b_ivy/ already, so this branch is mostly defensive). Look for
        # the first contained directory and use it.
        local first
        # -print -quit, not `| head -n 1`: see tools/lab-baseline.sh on head, SIGPIPE
        # and pipefail. find stopping itself leaves nothing writing into a closed pipe.
        first="$(find "$SCRATCH" -mindepth 1 -maxdepth 1 -type d -print -quit)"
        if [[ -n "$first" ]]; then
            mv "$first" "$out"
        else
            echo "  WARN: extracted $zip but found no pack directory inside" >&2
            return 1
        fi
    fi
    echo "$out"
}

copy_pack() {
    local pack="$1"
    local dest_name="$2"
    local zip_base="${3:-$pack}"
    local src
    if ! src="$(locate_pack "$pack" "$zip_base")"; then
        echo "  skipping (no source dir or zip): $pack"
        return 0
    fi
    local dest="$SRC/$dest_name"
    mkdir -p "$dest"
    # glTF + .bin from the pack root. rsync (not cp) because APFS-on-Mac's
    # cp does clonefile-style metadata copies for large files that can land
    # as zero-byte sparse artefacts on the destination volume. rsync's
    # default "fully read source, fully write dest" copy is slower but
    # reliable.
    rsync -a --no-links "$src"/*.gltf "$src"/*.bin "$dest/" 2>/dev/null || true
    # textures subdirectory if present
    if [[ -d "$src/textures" ]]; then
        mkdir -p "$dest/textures"
        rsync -a --no-links \
            --include="*.png" --include="*.jpg" --include="*.jpeg" --exclude="*" \
            "$src/textures/" "$dest/textures/"
    fi
    echo "  copied: $pack -> $dest_name"
    # Streaming extract: drop the scratch copy as soon as we've pulled the
    # runtime bits, so the next pack's extraction has room on a tight disk.
    if [[ "$src" == "$SCRATCH/"* ]]; then
        rm -rf "$src"
    fi
}

echo "Setting up Sponza Modern assets from: $SOURCE_ROOT"

# Pack inventory (Khronos Intel Sponza distribution as of 2025). The first
# four are the original packs the engine has always supported; the fifth
# (candles) is new but uses the same Combined.gltf + Combined.bin shape.
#
# zip_base differs from pack name only where the upstream archive stem
# doesn't match the directory it expands to (pkg_b_ivy1.zip → pkg_b_ivy/).
copy_pack "main_sponza"        "main_sponza" "main_sponza"
copy_pack "pkg_a_curtains"     "curtains"    "pkg_a_curtains"
copy_pack "pkg_b_ivy"          "ivy"         "pkg_b_ivy1"
copy_pack "pkg_c_trees"        "trees"       "pkg_c_trees"
copy_pack "pkg_d_10k_candles"  "candles"     "pkg_d_10k_candles"

# Cooking is the cook script's job, so the flags live in one place. It reads the
# same two variables; SRC is passed explicitly so a defaulted one agrees. Not exec,
# so the EXIT trap still removes the scratch directory.
BLIX_SPONZA_SRC="$SRC" "$REPO_ROOT/tools/cook-sponza-modern.sh"
