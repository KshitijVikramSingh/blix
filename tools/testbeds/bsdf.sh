#!/usr/bin/env bash
# The BSDF reference's pictures (the frame's lit pass beside them, and how the two agree): Khronos' material samples (tools/testbeds/setup.sh with BLIX_TESTBED_KHRONOS=1), each rendered by the frame (--shot) and by the
# reference's camera paths with the glTF BSDF (--ref-bsdf, --ref-view; tools/testbeds/bsdf_view.py turns the dump into a picture). The frame's lit pass does not yet
# shade the layers the reference does, so the pair shows what it is missing; --material "name:key=value" overrides a material on any of them.
#
#   BLIX_TESTBED_ASSETS  the cooked scenes.
#   BLIX_BSDF_GI         the GI flags instead of the cooked lightmap's ("--rtgi" on a tree that has it).
#   BLIX_BSDF_WINDOW     the window, WxH (default 1280x720); the reference's grid is every BLIX_BSDF_STRIDE-th pixel of it (default 4).
#
# Usage: tools/testbeds/bsdf.sh [scene...] [-- extra demo flags]    scenes: transmission dragon coat aniso sheen irid lamp, and drapes teacup plant for cloth and diffuse transmission (default: the first seven)
set -euo pipefail
REPO="$(cd "$(dirname "$0")/../.." && pwd)"
: "${BLIX_TESTBED_ASSETS:?BLIX_TESTBED_ASSETS names the cooked scenes}"
scenes=(); extra=()
while [[ $# -gt 0 ]]; do
    if [[ "$1" == -- ]]; then shift; extra=("$@"); break; fi
    scenes+=("$1"); shift
done
[[ ${#scenes[@]} -eq 0 ]] && scenes=(transmission dragon coat aniso sheen irid lamp)
OUT="$(mktemp -d "${TMPDIR:-/tmp}/blix-bsdf.XXXXXX")"
WINDOW="${BLIX_BSDF_WINDOW:-1280x720}"; STRIDE="${BLIX_BSDF_STRIDE:-4}"
export BLIX_CONFIG=Release BLIX_DIAG=off
gi=(--lightmap --transport-texels --rt-shadows --lod-pixels 0 --split-light --transport-own-fallback --transport-fill --far-transport --transport-rays 1024 --live-texels --live-patch-refine)
[[ -n "${BLIX_BSDF_GI:-}" ]] && read -r -a gi <<< "$BLIX_BSDF_GI"
build=(--build)
cam() {   # the picture's camera per scene
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
        plant) echo "" ;;
    esac
}
scene_name() { case "$1" in drapes) echo drapes ;; *) echo "khronos-$1" ;; esac; }
for s in "${scenes[@]}"; do
    mkdir -p "$OUT/$s"
    # shellcheck disable=SC2046
    BLIX_DUMP_DIR="$OUT/$s" "$REPO/blix" run ${build[@]+"${build[@]}"} Blix.Demos.VulkanSponza --scene="$(scene_name "$s")" $(cam "$s") "${gi[@]}" --no-fog --render-scale 1 --win=${WINDOW%x*} ${WINDOW#*x} \
        --ref-view $STRIDE --ref-view-lanes 16 --ref-view-paths 256 --ref-bounces 16 --ref-bsdf --view-dump \
        --shot-when-ready --shot-frames=6000 --shot="$OUT/$s.png" ${extra[@]+"${extra[@]}"} > "$OUT/$s.log" 2>&1 || { echo "bsdf: $s failed; $OUT/$s.log" >&2; tail -5 "$OUT/$s.log" >&2; exit 1; }
    build=()
    uv run -q --with numpy python3 "$REPO/tools/testbeds/bsdf_view.py" "$OUT/$s"/view-* "$OUT/$s-reference.png"
done
echo "bsdf: pictures in $OUT"
