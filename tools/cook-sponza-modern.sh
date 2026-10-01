#!/usr/bin/env bash
# Cooks the Khronos "New Sponza" packs into a tree that STANDS ALONE — one
# .blixmesh per pack plus the .blixtex files it references, and nothing else.
# The sources are not needed to run after this, which is the whole point: the
# pack set is ~19 GB and the cooked tree is a fraction of it.
#
#   SRC    the raw packs. BLIX_SPONZA_SRC, else a "-src" sibling of COOKED,
#          else COOKED itself. Laid out as the demo's own (main_sponza/
#          curtains/ ivy/ trees/), which tools/setup-sponza-modern.sh makes,
#          with the project's cook configuration at SRC/sponza.blixcook.
#   COOKED where the cooked tree goes. BLIX_SPONZA_ASSETS, and there is no
#          default — see the refusal below.
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
# <b>No default destination, because the default was worse than a failure.</b> This fell back to
# an in-repo Assets directory belonging to a demo the OpenGL sunset deleted. Run without the
# variable, the script cooked NOTHING — every pack reported "no .gltf … skipped" — then baked a
# sky-visibility volume from whatever stale .blixmesh files it found there, printed a cheerful
# "Cooked tree: 2.4G", and exited 0. A cook that silently writes derived data into a dead tree and
# calls it success is worse than one that stops, because the person running it has no reason to
# look.
if [[ -z "${BLIX_SPONZA_ASSETS:-}" ]]; then
    cat >&2 <<'MSG'
cook-sponza-modern: BLIX_SPONZA_ASSETS is not set.

  It names the COOKED tree — where .blixmesh / .blixtex / .blixprobe / .blixsky are written.
  The raw sources are taken from BLIX_SPONZA_SRC, defaulting to "<cooked>-src".

  e.g.  BLIX_SPONZA_ASSETS=~/blix-assets/sponza tools/cook-sponza-modern.sh

There is deliberately no default: the previous one pointed inside the repo and turned a missing
variable into a successful-looking cook of nothing.
MSG
    exit 2
fi
COOKED="$BLIX_SPONZA_ASSETS"
SRC="${BLIX_SPONZA_SRC:-${COOKED%/}-src}"
[[ -d "$SRC" ]] || SRC="$COOKED"

if [[ ! -d "$SRC" ]]; then
    echo "Sponza source dir not found: $SRC" >&2
    echo "Set BLIX_SPONZA_SRC, or run tools/setup-sponza-modern.sh first." >&2
    exit 1
fi

# <b>Built here, because a stale cook is a silent one.</b> This checked only that the assembly
# EXISTED, and it pins the Debug configuration — so a session that changed the cook and built
# Release re-cooked the whole tree with a binary from before the change, reported success, and
# produced assets missing exactly the thing the change was for. The stamp inside the cooked file
# was the only evidence, and nothing reads the stamp. Building takes a couple of seconds and
# removes the failure mode entirely.
COOK_PROJECT="$REPO/src/Blix.Tools.Cook/Blix.Tools.Cook.csproj"
COOK="$REPO/src/Blix.Tools.Cook/bin/Debug/net8.0/Blix.Tools.Cook.dll"
if ! dotnet build "$COOK_PROJECT" -c Debug -v quiet --nologo >/dev/null; then
    echo "cook-sponza-modern: the cook does not build; nothing was cooked." >&2
    exit 1
fi
if [[ ! -f "$COOK" ]]; then
    echo "cook-sponza-modern: built, but $COOK is missing." >&2
    exit 1
fi

mkdir -p "$COOKED"
echo "Cooking '$SRC' -> '$COOKED'"

# <b>What each pack is cooked with is the project's configuration, not this script's.</b> The split,
# the material rules and the normal-map conventions live in "$SRC/sponza.blixcook", one entry per
# pack, beside the sources it names — NAMED rather than discovered: a rule file that applies because
# it happens to sit beside a source is an invisible input. This used to pass --split and a per-pack
# --patch here, so the decisions about one asset were split between a shell script and four files.
# The configuration names the demo's own directory layout (main_sponza/ curtains/ ivy/ trees/); a
# Khronos download (pkg_a_curtains/ ...) is renamed once by tools/setup-sponza-modern.sh.
CONFIG="$SRC/sponza.blixcook"
if [[ ! -f "$CONFIG" ]]; then
    echo "cook-sponza-modern: no cook configuration at $CONFIG; nothing was cooked." >&2
    exit 1
fi
dotnet "$COOK" project "$CONFIG" --out "$COOKED"

# The sky probe is separate: it is not referenced by any glTF, so no asset cook
# reaches it. Optional — the demo bakes a procedural sky when it is absent.
mkdir -p "$COOKED/textures"
for hdr in "$SRC"/textures/*.hdr; do
    [[ -f "$hdr" ]] || continue
    echo "── probe  ($(basename "$hdr"))"
    # --out MIRRORS the source's relative path, so the target is the tree root and
    # not its textures/ dir — passing the latter produced textures/textures/.
    #
    # --env-face=1024 here and not only on the special case below. Every sky in this
    # directory is a 4096x2048 equirect, so a 90-degree cube face maps to about 1024
    # texels; below that the VISIBLE sky is a downsample of authored detail and the eye
    # catches it wherever a window frames the sky against a hard edge. The flag used to
    # live only on kloppenheim, so whichever sky the demo actually led with got the good
    # treatment by coincidence of being named — a new lead sky was silently cooked at the
    # default and shipped a 4.55 MB probe where the old one had 49.55 MB. The prefiltered
    # chains keep their own sizes: they are convolutions and do not want the resolution.
    dotnet "$COOK" probe "$hdr" --env-face=1024 --out "$COOKED"
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

# <b>The sky-visibility volume, which no script cooked until now.</b> It was baked by hand once and
# the invocation survived only in a shell history — so nothing re-made it when the bakers changed,
# and finding out how it had been produced meant matching the shipped dimensions against CookSky's
# defaults. A cooked artifact nobody can reproduce is a cooked artifact nobody can fix.
#
# LAST, because it reads the .blixtex files above for per-material albedo: bake it before the
# textures and every surface bounces the previous cook's colour.
#
# --rays 512, up from the default 64. Measured against a 4096-ray ground truth: the ray count does
# NOT much improve the average error (0.0163 -> 0.0156), because what is left is the L2 expansion's
# own inability to describe a narrow sky opening seen from the bottom of an arcaded well. What it
# fixes is the ZEROS — 2,326 cells that read exactly 0.000 at 64 rays read a real value at 512, and
# a cell reading zero hands the surfaces around it NO skylight at all. In Sponza's atrium those
# surfaces then take 100% of their ambient light from the bounce instead of 73%, which is how a
# green tree ends up painting a wall. The bake costs 1.4 seconds either way.
echo "── sky    (visibility volume, 512 rays)"
# <b>--albedo 256, matching the occupancy rather than defaulting to half of it.</b> The baker's
# default halves it on the argument that surface colour is low-frequency — true of a wall and false
# of the boundary between a wall and a curtain, which is where the eye looks. At half resolution a
# cell straddling that boundary gets one colour for both, and the wall re-radiates the curtain's
# red: the "leaking colour" visible in the bounce-radiance view. 3 MB becomes ~23 MB, which is half
# of what the probe atlas already spends at this density.
dotnet "$COOK" sky "$COOKED" --occupancy 256 --probes 48 --rays 512 --albedo 256 --out "$COOKED/sponza.blixsky"

echo
echo "Cooked tree: $(du -sh "$COOKED" | cut -f1)   (sources: $(du -sh "$SRC" | cut -f1))"
echo "Run with:  BLIX_SPONZA_ASSETS=$COOKED ./blix run Blix.Demos.VulkanSponza"
