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
# VulkanSponza imports with includeTangents TRUE, so its meshes must be cooked
# that way. They were not, for as long as this script has existed: `cook mesh`
# was called bare, producing a 32-byte vertex where the demo's pipelines all
# declare a 48-byte tangent layout. Nothing compared the two, so the GPU read
# 48-byte strides out of a 32-byte buffer and drew the scene as a fan of grey
# triangles while every count in the log stayed correct. The loader now refuses a
# mismatch by name; this flag is what makes it not have to.
#
# --flip-v used to be here too, and is gone with the loader's y-flip: Sponza was
# the one consumer cancelling a flip the image decoder should never have applied.
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
# Second argument is the pack's material patch, or '-' for none. NAMED rather than discovered:
# a patch that applies because a file happens to sit beside the source is an invisible input, and
# a declared one that has gone missing should stop the cook rather than quietly not apply. One per
# pack because a rule matching nothing is an error and Sponza is four separate glTFs — a shared
# file would fail every curtain rule against the main pack.
cook_pack() {
    dest="$1"; patch="$2"; shift 2
    for candidate in "$@"; do
        dir="$SRC/$candidate"
        [[ -d "$dir" ]] || continue
        gltf=$(find "$dir" -maxdepth 1 -name '*.gltf' | head -1)
        if [[ -z "$gltf" ]]; then
            echo "  $dest: no .gltf in $dir — skipped"
            return 0
        fi
        patch_args=()
        if [[ "$patch" != "-" ]]; then
            if [[ ! -f "$dir/$patch" ]]; then
                echo "  $dest: declares patch '$patch' and it is not in $dir" >&2
                exit 1
            fi
            patch_args=(--patch "$dir/$patch")
        fi
        echo "── $dest  ($(basename "$gltf"))"
        # ${x[@]+"${x[@]}"} rather than "${x[@]}": macOS ships bash 3.2, where expanding an EMPTY
        # array under `set -u` is an unbound-variable error. It failed only for the packs with no
        # patch — so main_sponza and curtains cooked, ivy and trees silently did not.
        dotnet "$COOK" asset "$gltf" --out "$COOKED/$dest" --tangents ${patch_args[@]+"${patch_args[@]}"}
        return 0
    done
    echo "  $dest: not present in $SRC — skipped"
}

cook_pack main_sponza main_sponza.blixpatch main_sponza
cook_pack curtains    curtains.blixpatch    pkg_a_curtains curtains
cook_pack ivy         -                     pkg_b_ivy      pkg_b_ivy1 ivy
cook_pack trees       -                     pkg_c_trees    trees

# The sky probe is separate: it is not referenced by any glTF, so no asset cook
# reaches it. Optional — the demo bakes a procedural sky when it is absent.
mkdir -p "$COOKED/textures"
for hdr in "$SRC"/textures/*.hdr; do
    [[ -f "$hdr" ]] || continue
    echo "── probe  ($(basename "$hdr"))"
    # --out MIRRORS the source's relative path, so the target is the tree root and
    # not its textures/ dir — passing the latter produced textures/textures/.
    dotnet "$COOK" probe "$hdr" --out "$COOKED"
done

# <b>The probe the demo actually LEADS with, and the rotation that earns it.</b> kloppenheim is not
# in textures/ — it ships inside main_sponza — so the loop above never reached it, and for a while
# the only record of how it was made was a command in somebody's shell history. It was chosen
# because its sun sits at elevation 74.5 against the scene's authored 73.1, where goegap's is 46.4;
# --yaw brings the azimuth onto -22.7 as well, which a roll of an equirect can do without tilting
# the horizon. Roughly 1.4 degrees out in total, against 27 for any of the alternatives.
KLOPPENHEIM="$SRC/main_sponza/textures/kloppenheim_05_4k.hdr"
if [[ -f "$KLOPPENHEIM" ]]; then
    echo "── probe  (kloppenheim_05_4k.hdr, --yaw=-31.43)"
    # --env-face=1024 because the source equirect is 4096x2048, so a 90-degree cube face maps to
    # about 1024 texels: below that the background sky is a downsample of authored detail, and the
    # eye catches it wherever a window frames the sky against a hard edge. The prefiltered chains
    # stay at their own sizes — they are convolutions and do not want the resolution.
    dotnet "$COOK" probe "$KLOPPENHEIM" --yaw=-31.43 --env-face=1024 --out "$COOKED"
fi

echo
echo "Cooked tree: $(du -sh "$COOKED" | cut -f1)   (sources: $(du -sh "$SRC" | cut -f1))"
echo "Run with:  BLIX_SPONZA_ASSETS=$COOKED tools/run-vulkan-sponza.sh"
