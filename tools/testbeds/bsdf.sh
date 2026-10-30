#!/usr/bin/env bash
# The material samples under the GI, judged as the gate judges a scene: the frame (RTGI, the default GI) against the GPU path trace at the
# reference's points (--gpu-reference, reference_wave.comp) with --ref-bsdf, so the reference's paths shade every hit with the full glTF BSDF
# (Blix.Shaders/bsdf_gltf.glsl: GGX metal and dielectric, ior, specular, thin and solid transmission, diffuse transmission, clearcoat, sheen,
# iridescence, anisotropy, emission) and see the panes the frame's rays pass. At each point the frame's incident light (what RTGI hands the
# lit pass: all the indirect diffuse) is held against the reference's irradiance there; tools/testbeds/bsdf_view.py groups the points by the
# material they sit on, so each sample reads as its own row: how well RTGI carries the light the materials around a point send it.
#
# What it judges, and what not: the light ARRIVING at the points (bounced off a metal, through a teacup's wall or a curtain, off a sheen
# chair) -- not the lit pass's own response to it at the camera (the pane's reflection, the coat's highlight). Points are not placed on
# transmitting panes (their pixels' light is the surface's behind them: SponzaLoop.GpuReference.cs), so a glass sample reads through the
# floor and walls round it, not its own surface.
#
#   BLIX_TESTBED_ASSETS  the cooked scenes (tools/testbeds/setup.sh with BLIX_TESTBED_KHRONOS=1; the gallery: tools/testbeds/gallery.sh).
#   BLIX_BSDF_WINDOW     the window, WxH (default 960x540, the gate's); the reference's points are a 24x24 grid of its pixels.
#   BLIX_BSDF_PATHS      the reference's paths a point (default 16384, the gate's bright scenes').
#   BLIX_REF_CACHE       as the gate: the reference kept on disk by what decides it, and read back on a rerun.
#
# Usage: tools/testbeds/bsdf.sh [scene...] [-- extra demo flags]
#   scenes: transmission dragon coat aniso sheen irid lamp (Khronos' BSDF samples on open ground), drapes teacup plant (cloth and diffuse
#           transmission in the Cornell room), gallery (every sample in the Cornell room), room (material-room), vase, leaves (Habel's).
#   default: transmission coat aniso sheen irid drapes teacup plant gallery.
# Exit 0 when every run reached its shot; the verdict is the table (no ratchets yet: the first runs set them).
set -euo pipefail
REPO="$(cd "$(dirname "$0")/../.." && pwd)"
: "${BLIX_TESTBED_ASSETS:?BLIX_TESTBED_ASSETS names the cooked scenes}"
scenes=(); extra=()
while [[ $# -gt 0 ]]; do
    if [[ "$1" == -- ]]; then shift; extra=("$@"); break; fi
    scenes+=("$1"); shift
done
[[ ${#scenes[@]} -eq 0 ]] && scenes=(transmission coat aniso sheen irid drapes teacup plant gallery)
OUT="$(mktemp -d "${TMPDIR:-/tmp}/blix-bsdf.XXXXXX")"
WINDOW="${BLIX_BSDF_WINDOW:-960x540}"
PATHS="${BLIX_BSDF_PATHS:-16384}"
export BLIX_CONFIG=Release BLIX_DIAG=off
build=(--build)
cam() {   # the camera per scene: the sample filling most of the view, so most of the grid's points sit on it
    case "$1" in
        transmission) echo "--cam 0,0.55,2.0,0,-5" ;;
        dragon) echo "--cam 0,0.9,3.5,0,-5" ;;
        coat) echo "--cam 0,6,25,0,0" ;;
        aniso) echo "--cam 0,0.5,1.5,0,-20" ;;
        sheen) echo "--cam 0,0.6,2.5,0,-5" ;;
        irid) echo "--cam 0,1.0,3.5,0,-5" ;;
        lamp) echo "--cam 0,0.8,3.0,0,-5" ;;
        drapes) echo "--cam -0.68,2.46,3.28,10,-5 --ray-lod-error 0" ;;
        teacup) echo "--cam 0.291,1.958,-0.454,-13.78,-33.62" ;;
        *) echo "" ;;   # the scene's own start camera (plant, gallery, room, vase, leaves)
    esac
}
scene_name() {
    case "$1" in
        drapes) echo drapes ;;
        gallery) echo material-gallery ;;
        room) echo material-room ;;
        vase) echo khronos-glassvase ;;
        leaves) echo habel-leaves ;;
        *) echo "khronos-$1" ;;
    esac
}
for s in "${scenes[@]}"; do
    name="$(scene_name "$s")"
    if [[ ! -f "$BLIX_TESTBED_ASSETS/$name/$name.blixmesh" ]]; then
        echo "bsdf: $name is not cooked (BLIX_TESTBED_KHRONOS=1 tools/testbeds/setup.sh; the gallery: tools/testbeds/gallery.sh)" >&2
        exit 1
    fi
done
for s in "${scenes[@]}"; do
    # shellcheck disable=SC2046
    BLIX_RUN_LABEL="bsdf $s" "$REPO/blix" run ${build[@]+"${build[@]}"} Blix.Demos.VulkanSponza --scene="$(scene_name "$s")" $(cam "$s") \
        --render-scale 1 --win=${WINDOW%x*} ${WINDOW#*x} \
        --gpu-reference --ref-bsdf --ref-bounces 16 --gpu-ref-paths "$PATHS" \
        --shot-when-ready --shot-frames=4500 --shot="$OUT/$s.png" ${extra[@]+"${extra[@]}"} > "$OUT/$s.log" 2>&1 || {
        echo "bsdf: $s failed; $OUT/$s.log" >&2; tail -5 "$OUT/$s.log" >&2; exit 1; }
    build=()   # built once; the rest run what that built
    echo "bsdf: $s done"
done
uv run -q --with numpy --with pillow python3 "$REPO/tools/testbeds/bsdf_view.py" "$OUT" "${scenes[@]}"
echo "bsdf: logs and pictures in $OUT"
