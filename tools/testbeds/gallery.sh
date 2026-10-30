#!/usr/bin/env bash
# material-gallery: the Cornell room (6 m, a 1.5 m skylight, red and green walls) with every material sample we have in it at once -- foliage cut-outs, thin glass, a pane, cloth,
# the Khronos dragon, sheen chair, teacup, plant, clear coat, transmission and iridescence samples -- so the GI (colour bleeding off the walls, the sun's shaft, shade lit by bounce)
# and the materials are seen working together, not each alone on flat ground.
#
#   BLIX_TESTBED_ASSETS  the COOKED tree (required), as setup.sh
#   BLIX_TESTBED_SRC     the generated glTF, defaulting to "<cooked>-src"
#   BLIX_TESTBED_KHRONOS=1 setup.sh has downloaded the Khronos samples into "$SRC/khronos" (and the glass vase)
#
# Usage: tools/testbeds/gallery.sh        then   blix run Blix.Demos.VulkanSponza --scene=material-gallery --rtgi
set -euo pipefail
REPO="$(cd "$(dirname "$0")/../.." && pwd)"
[[ -n "${BLIX_TESTBED_ASSETS:-}" ]] || { echo "gallery: BLIX_TESTBED_ASSETS is not set; it names the COOKED tree." >&2; exit 1; }
COOKED="$BLIX_TESTBED_ASSETS"
SRC="${BLIX_TESTBED_SRC:-${COOKED%/}-src}"
K="$SRC/khronos"
G="$SRC/material-gallery-parts"
for g in leaves glass tinted pane cloth candle; do
    uv run --with pillow --with numpy python3 "$REPO/tools/testbeds/material-room.py" "$G/$g" "$g" >/dev/null
done
m() { echo "$K/$1/$1.glb"; }
# x, z on the floor, the room x, z in [-3, 3] with the open front at +z; the sun's shaft lands near (-0.7, -1.7).
"$REPO/blix" run --build gallery \
    --put "$G/leaves/material-room.gltf,1,0.1,0,-0.8" \
    --put "$G/cloth/material-room.gltf,1,-1.4,0,-2.4" \
    --put "$G/tinted/material-room.gltf,1,-1.4,0,-1.5" \
    --put "$G/glass/material-room.gltf,1,-0.05,0,-1.7" \
    --put "$G/pane/material-room.gltf,1,0.9,0,-2.2" \
    --put "$G/candle/material-room.gltf,1,1.9,0,-2.2" \
    --put "$(m DiffuseTransmissionPlant),1.5,-2.3,0,-2.2" \
    --put "$(m DiffuseTransmissionTeacup),5,-2.2,0,-0.8,rest" \
    --put "$(m DragonAttenuation),0.17,1.9,0,-0.6,rest" \
    --put "$(m SheenChair),1.4,2.1,0,0.9,rest,yaw=-35" \
    --put "$(m ClearCoatTest),0.09,0.6,0,-2.75,rest" \
    --put "$(m TransmissionTest),0.8,-1.8,0,1.2,rest,yaw=25" \
    --put "$K/GlassVaseFlowers/GlassVaseFlowers.glb,2.5,0.5,0,-0.3,rest" \
    --put "$(m IridescenceLamp),1.5,1.0,0,0.4,rest" \
    --out "$SRC/material-gallery/material-gallery.gltf" | grep -v "^gallery: .*its own size" || true
rm -rf "${COOKED:?}/material-gallery"
"$REPO/blix" run --build cook asset "$SRC/material-gallery/material-gallery.gltf" --out "$COOKED/material-gallery"
