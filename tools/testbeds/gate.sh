#!/usr/bin/env bash
# The GI correctness gate: the correctness scenes rendered and held against the CPU path trace on their triangles.
#
#   BLIX_TESTBED_ASSETS  the cooked scenes (tools/testbeds/setup.sh makes them; this runs it when they are missing).
#
# Three headed runs at 960x540 (a window opens and closes each time): the Cornell box from outside the open front,
# then the thin-wall scene from inside its sealed room and from the open one. Each converges 1500 frames; the judge is
# the GPU path-traced reference at 16,384 paths a point (--gpu-reference, reference_points.comp; the CPU's 1,024 a point
# were a suspect for the run-to-run noise); tools/testbeds/gate.py turns the logs into the verdict (targets and ratchets: see
# there). BLIX_GATE_CPU_REFERENCE=1 also runs the CPU reference (--probe-reference) as a cross-check: its lines are
# relabelled, so the verdict stays the GPU's, and each log ends with the two held against each other.
# Exit 0 when every ratchet holds. Logs stay in the directory printed at the end.
#
# Usage: tools/testbeds/gate.sh [extra demo flags...]   e.g. --clipmap-guide 0 to price a change against the bars
set -euo pipefail
REPO="$(cd "$(dirname "$0")/../.." && pwd)"
if [[ -z "${BLIX_TESTBED_ASSETS:-}" ]]; then
    echo "gate: BLIX_TESTBED_ASSETS is not set; it names the cooked correctness scenes." >&2
    exit 1
fi
for scene in cornell thinwall crevices-dark; do
    if [[ ! -f "$BLIX_TESTBED_ASSETS/$scene/$scene.blixmesh" ]]; then
        echo "gate: $scene is not cooked; running tools/testbeds/setup.sh"
        "$REPO/tools/testbeds/setup.sh"
        break
    fi
done
LOGS="$(mktemp -d "${TMPDIR:-/tmp}/blix-gi-gate.XXXXXX")"
export BLIX_CONFIG=Release BLIX_DIAG=off
build=(--build)
# 32 bounces: at the default 3 the path trace stopped short in every room lit by bounce -- 0.73 grey loses a quarter
# of its light each bounce: the thin wall's open room read 1.125 against 3 bounces, 1.000 against 32; the crevices'
# corridor 1.45 and 0.998 (2026-10-13).
# The corridor's reference runs 262,144 paths a point (2,048 lanes, so 4,500 frames hold them): at 16,384, two references
# that differ only in their seed sat 0.037 apart there (|diff| over the mean), as far as the field from either, and the
# |error| metric measured the reference; at 262,144, 0.0096 (2026-10-13). The bright scenes keep 16,384.
reference=(--gpu-reference --ref-bounces 32)
if [[ "${BLIX_GATE_CPU_REFERENCE:-0}" == 1 ]]; then reference+=(--probe-reference); fi
paths=(--gpu-ref-paths 16384)
frames=3000
run() {
    local name=$1; shift
    "$REPO/blix" run ${build[@]+"${build[@]}"} Blix.Demos.VulkanSponza "$@" --no-fog --win=960 540 "${reference[@]}" "${paths[@]}" \
        --shot-frames=$frames --shot="$LOGS/$name.png" > "$LOGS/$name.log" 2>&1 || {
        echo "gate: the $name run failed; its log is $LOGS/$name.log" >&2
        tail -5 "$LOGS/$name.log" >&2
        exit 1
    }
    build=()   # built once; the rest run what that built
    echo "gate: $name done"
}
run cornell --scene=cornell "$@"
run thinwall-sealed --scene=thinwall "$@"
run thinwall-open --scene=thinwall --cam -3.05,3,0,90,0 "$@"
paths=(--gpu-ref-paths 262144 --gpu-ref-lanes 2048)
frames=4500
run crevices-corridor --scene=crevices-dark --cam 6,1.5,1.5,90,-5 "$@"
rm -f "$LOGS"/*.png
python3 "$REPO/tools/testbeds/gate.py" "$LOGS"
status=$?
echo "gate: logs in $LOGS"
exit $status
