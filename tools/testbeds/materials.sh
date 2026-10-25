#!/usr/bin/env bash
# The material and transmission check: what the camera sees, and the light the GI resolved at its records, held against
# the camera-path reference through the materials (--ref-view; tools/gi-model/view_reference.py) on the scenes made for
# light through things: drapes (a veil and a pleated curtain) and Khronos' diffuse-transmission samples (the plant, the
# teacup on its saucer, the test panels).
#
#   BLIX_TESTBED_ASSETS  the cooked scenes (tools/testbeds/setup.sh makes them; BLIX_TESTBED_KHRONOS=1 there for the
#                        Khronos ones, which it downloads).
#
# Four headed runs at 960x540 (a window opens and closes each time). Each shoots once its light is at rest and the
# reference has all its paths (1,024 a grid point, every 16 px; 32 bounces) and 2,048 records' (--ref-view-records).
# tools/testbeds/materials.py turns the dumps into the verdict (targets and ratchets, as gate.py's). Exit 0 when every
# ratchet holds; the dumps stay in the directory printed at the end.
#
# Usage: tools/testbeds/materials.sh [extra demo flags...]   e.g. --no-transport-own-first to price a change
set -euo pipefail
REPO="$(cd "$(dirname "$0")/../.." && pwd)"
if [[ -z "${BLIX_TESTBED_ASSETS:-}" ]]; then
    echo "materials: BLIX_TESTBED_ASSETS is not set; it names the cooked scenes." >&2
    exit 1
fi
for scene in drapes khronos-plant khronos-teacup khronos-test khronos-alpha; do
    if [[ ! -f "$BLIX_TESTBED_ASSETS/$scene/$scene.blixmesh" ]]; then
        echo "materials: $scene is not cooked; run BLIX_TESTBED_KHRONOS=1 tools/testbeds/setup.sh" >&2
        exit 1
    fi
done
OUT="$(mktemp -d "${TMPDIR:-/tmp}/blix-materials.XXXXXX")"
export BLIX_CONFIG=Release BLIX_DIAG=off
build=(--build)
# The shipped GI (the gate's budget).
gi=(--lightmap --transport-texels --rt-shadows --lod-pixels 0 --split-light --transport-own-fallback --transport-fill
    --far-transport --transport-rays 1024 --live-texels --live-patch-refine)
reference=(--viz 26 --ref-view 16 --ref-bounces 32 --ref-view-records 2048 --view-dump)
run() {
    local name=$1; shift
    # The window's title names the run (SponzaLoop: BLIX_RUN_LABEL): which scene, with what flags.
    BLIX_RUN_LABEL="materials $name: ${*:-defaults}${BLIX_GATHER_SEED:+ [seed $BLIX_GATHER_SEED]}" BLIX_DUMP_DIR="$OUT/$name" "$REPO/blix" run ${build[@]+"${build[@]}"} Blix.Demos.VulkanSponza "${gi[@]}" "$@" --no-fog --render-scale 1 \
        --win=960 540 "${reference[@]}" --shot-when-ready --shot-frames=6000 --shot="$OUT/$name.png" > "$OUT/$name.log" 2>&1 || {
        echo "materials: the $name run failed; its log is $OUT/$name.log" >&2
        tail -5 "$OUT/$name.log" >&2
        exit 1
    }
    build=()
    echo "materials: $name done"
}
run drapes --scene=drapes --cam -0.68,2.46,3.28,10,-5 --ray-lod-error 0 "$@"
run khronos-plant --scene=khronos-plant "$@"
run khronos-teacup --scene=khronos-teacup --cam 0.291,1.958,-0.454,-13.78,-33.62 "$@"
run khronos-test --scene=khronos-test "$@"
# Alpha: Khronos' AlphaBlendModeTest (OPAQUE, MASK at three cutoffs, BLEND): the traced camera view against the raster's depth
# (--ray-view), which alpha-tests exactly (--ray-blend-cutoff: the raster draws BLEND cut out at 0.5, rays stop at its alpha otherwise); materials.py reads the log's "ray view against the raster" line.
run khronos-alpha --scene=khronos-alpha --ray-view --ray-blend-cutoff "$@"
rm -f "$OUT"/*.png
# EON (--diffuse eon) has its own ratchets (materials.py EON_RATCHETS): the reference models EON exactly and the frame approximates it.
case " $* " in *" --diffuse eon "*) export BLIX_MATERIALS_DIFFUSE=eon ;; esac
uv run -q --with numpy python3 "$REPO/tools/testbeds/materials.py" "$OUT"
status=$?
echo "materials: dumps in $OUT"
exit $status
