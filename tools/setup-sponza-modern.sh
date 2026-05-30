#!/usr/bin/env bash
# Populates src/Blix.Demos.SponzaModern/Assets/ from a local copy of the
# Khronos Intel Sponza repo packs.
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
# Where the original packs live:
#   https://github.com/KhronosGroup/glTF-Sample-Assets/tree/main/Models/IntelSponza

set -euo pipefail

SOURCE_ROOT="${1:-$HOME/Downloads}"
REPO_ROOT="$(cd "$(dirname "$0")/.." && pwd)"
DEST="$REPO_ROOT/src/Blix.Demos.SponzaModern/Assets"
SCRATCH="$(mktemp -d -t blix-sponza-extract.XXXXXX)"
trap 'rm -rf "$SCRATCH"' EXIT

if [[ ! -d "$SOURCE_ROOT" ]]; then
    echo "Source root not found: $SOURCE_ROOT" >&2
    exit 1
fi

mkdir -p "$DEST"

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
        first="$(find "$SCRATCH" -mindepth 1 -maxdepth 1 -type d | head -n 1)"
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
    local dest="$DEST/$dest_name"
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

# Reuse the Walkthrough's HDR sky probe (kloppenheim) so the new demo can
# bake IBL probes from frame one. Skip if it isn't present.
WALK_HDR="$REPO_ROOT/src/Blix.Demos.Walkthrough/Assets/textures/sky_hdr.hdr"
if [[ -f "$WALK_HDR" ]]; then
    mkdir -p "$DEST/textures"
    cp -p "$WALK_HDR" "$DEST/textures/sky_hdr.hdr"
    echo "  copied: walkthrough sky_hdr.hdr"
fi

# Cook the HDR sky into a .blixprobe (real GGX-prefiltered specular +
# irradiance + BRDF LUT) so VulkanSponza gets proper IBL instead of the
# procedural-sky fallback. Prefer autumn_field (has a sun → high contrast for
# crisp shadows; the demo aligns its directional sun to it) → rogland overcast
# (sunless ambient) → sky_hdr. Best-effort: skipped if dotnet/HDR is missing,
# and the demo falls back to the procedural bake when no probe is present.
# (autumn_field_4k.hdr / rogland_overcast_4k.hdr are from Poly Haven; drop into
# $DEST/textures.)
HDR=""
for cand in autumn_field_4k rogland_overcast_4k sky_hdr; do
    if [[ -f "$DEST/textures/$cand.hdr" ]]; then HDR="$DEST/textures/$cand.hdr"; break; fi
done
if [[ -n "$HDR" ]] && command -v dotnet >/dev/null 2>&1; then
    echo "Cooking IBL probe from $(basename "$HDR") ..."
    COOK="$REPO_ROOT/src/Blix.Tools.Cook/Blix.Tools.Cook.csproj"
    if dotnet build "$COOK" -c Debug --nologo -v:q >/dev/null 2>&1; then
        COOK_DLL="$REPO_ROOT/src/Blix.Tools.Cook/bin/Debug/net8.0/Blix.Tools.Cook.dll"
        dotnet "$COOK_DLL" probe "$HDR" --env-face=512 --prefilter-base=256 --prefilter-mips=5 \
            && echo "  cooked: $(basename "${HDR%.hdr}.blixprobe")" \
            || echo "  (probe cook failed — demo will use procedural IBL)"
    else
        echo "  (cook tool build failed — demo will use procedural IBL)"
    fi
fi

# Cook the scene textures to BC7/BC5 .blixtex siblings (multi-mip). The demo
# loads these with no decode (44s of stb_image decode -> ~1ms lazy index) and
# ~3-4x less GPU memory than RGBA8; it falls back to runtime PNG/JPEG decode
# for any texture without a .blixtex. The cook re-cooks only stale outputs.
COOK="$REPO_ROOT/src/Blix.Tools.Cook/Blix.Tools.Cook.csproj"
if dotnet build "$COOK" -c Release --nologo -v:q >/dev/null 2>&1; then
    echo "Cooking textures to BC7/BC5 .blixtex (multi-mip) ..."
    dotnet run --project "$COOK" -c Release -- textures "$DEST" \
        || echo "  (texture cook failed — demo will runtime-decode PNG/JPEG)"
    # Cook geometry to .blixmesh (tangent layout + meshoptimizer LOD chains).
    # The demo loads cooked vertex/index data (skips glTF accessor walking) and
    # — once LOD selection lands — picks a triangle level by distance. Falls
    # back to runtime glTF import for any .gltf without a .blixmesh.
    echo "Cooking geometry to .blixmesh (tangent + LOD chains) ..."
    dotnet run --project "$COOK" -c Release -- mesh "$DEST" --tangents \
        || echo "  (mesh cook failed — demo will runtime-import glTF)"
else
    echo "  (cook tool build failed — demo will runtime-decode textures)"
fi

du -sh "$DEST" 2>/dev/null | awk '{print "Total: " $1}'
echo "Done."
