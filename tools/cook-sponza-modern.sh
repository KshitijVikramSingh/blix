#!/usr/bin/env bash
# Cooks the Khronos "New Sponza" packs into a tree that STANDS ALONE — one
# .blixmesh per pack plus the .blixtex files it references, and nothing else.
# The sources are not needed to run after this, which is the whole point: the
# pack set is ~19 GB and the cooked tree is a fraction of it.
#
#   SRC    the raw packs. BLIX_SPONZA_SRC, else a "-src" sibling of COOKED,
#          else COOKED itself. Accepts either the Khronos download names
#          (main_sponza/ pkg_a_curtains/ pkg_b_ivy/ pkg_c_trees/) or the
#          demo's own (main_sponza/ curtains/ ivy/ trees/).
#   COOKED where the cooked tree goes. BLIX_SPONZA_ASSETS, else the in-repo
#          Assets dir.
#
# Run from anywhere.
#
# ── The flags are not optional ──────────────────────────────────────────────
# VulkanSponza imports with flipTextureV and includeTangents both TRUE, so its
# meshes must be cooked that way. They were not, for as long as this script has
# existed: `cook mesh` was called bare, producing a 32-byte V-unflipped vertex
# where the demo's pipelines all declare a 48-byte tangent layout. Nothing
# compared the two, so the GPU read 48-byte strides out of a 32-byte buffer and
# drew the scene as a fan of grey triangles while every count in the log stayed
# correct. The loader now refuses a mismatch by name; these flags are what makes
# it not have to.
#
# ── Why `cook asset` and not `cook textures` + `cook mesh` ──────────────────
# Those two sweep a DIRECTORY. Main Sponza ships 137 texture files and its own
# glTF references 72 of them, so a sweep spends a quarter of its time and a
# quarter of the output on images nothing will ever sample. `cook asset`
# follows the asset's own references instead, and cooks the textures BEFORE
# the mesh so the mesh records where each image actually landed.

set -euo pipefail

REPO="$(cd "$(dirname "$0")/.." && pwd)"
COOKED="${BLIX_SPONZA_ASSETS:-$REPO/src/Demos/Blix.Demos.SponzaModern/Assets}"
SRC="${BLIX_SPONZA_SRC:-${COOKED%/}-src}"
[[ -d "$SRC" ]] || SRC="$COOKED"

if [[ ! -d "$SRC" ]]; then
    echo "Sponza source dir not found: $SRC" >&2
    echo "Set BLIX_SPONZA_SRC, or run tools/setup-sponza-modern.sh first." >&2
    exit 1
fi

COOK="$REPO/src/Blix.Tools.Cook/bin/Debug/net8.0/Blix.Tools.Cook.dll"
if [[ ! -f "$COOK" ]]; then
    echo "Build the cook first: dotnet build $REPO/src/Blix.Tools.Cook/Blix.Tools.Cook.csproj" >&2
    exit 1
fi

mkdir -p "$COOKED"
echo "Cooking '$SRC' -> '$COOKED'"

# Each pack: the directory the demo expects, then the source names it may have.
# Two names because the Khronos downloads and the demo's layout disagree, and
# renaming 19 GB to satisfy a glob is not a thing to ask of anyone.
cook_pack() {
    dest="$1"; shift
    for candidate in "$@"; do
        dir="$SRC/$candidate"
        [[ -d "$dir" ]] || continue
        gltf=$(find "$dir" -maxdepth 1 -name '*.gltf' | head -1)
        if [[ -z "$gltf" ]]; then
            echo "  $dest: no .gltf in $dir — skipped"
            return 0
        fi
        echo "── $dest  ($(basename "$gltf"))"
        dotnet "$COOK" asset "$gltf" --out "$COOKED/$dest" --tangents --flip-v
        return 0
    done
    echo "  $dest: not present in $SRC — skipped"
}

cook_pack main_sponza main_sponza
cook_pack curtains    pkg_a_curtains curtains
cook_pack ivy         pkg_b_ivy      pkg_b_ivy1 ivy
cook_pack trees       pkg_c_trees    trees

# The sky probe is separate: it is not referenced by any glTF, so no asset cook
# reaches it. Optional — the demo bakes a procedural sky when it is absent.
mkdir -p "$COOKED/textures"
for hdr in "$SRC"/textures/*.hdr; do
    [[ -f "$hdr" ]] || continue
    echo "── probe  ($(basename "$hdr"))"
    dotnet "$COOK" probe "$hdr" --out "$COOKED/textures"
done

echo
echo "Cooked tree: $(du -sh "$COOKED" | cut -f1)   (sources: $(du -sh "$SRC" | cut -f1))"
echo "Run with:  BLIX_SPONZA_ASSETS=$COOKED tools/run-vulkan-sponza.sh"
