#!/usr/bin/env bash
# The GI correctness gate: the correctness scenes rendered with the default GI (RTGI) and held against a path trace on
# their triangles.
#
#   BLIX_TESTBED_ASSETS  the cooked scenes (tools/testbeds/setup.sh makes them; this runs it when they are missing).
#
# Three headed runs at 960x540 (a window opens and closes each time): the Cornell box from outside the open front,
# then the thin-wall scene from inside its sealed room and from the open one. Each converges 1500 frames; the judge is
# the GPU path-traced reference at 16,384 paths a point (--gpu-reference, reference_points.comp; the CPU's 1,024 a point
# were a suspect for the run-to-run noise); tools/testbeds/gate.py turns the logs into the verdict (targets and ratchets: see
# there). Exit 0 when every ratchet holds. Logs stay in the directory printed at the end.
#
# Usage: tools/testbeds/gate.sh [extra demo flags...]   e.g. --rtgi-no-volume to price a change against the bars
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
# The corridor's reference runs 262,144 paths a point (4,096 lanes: ~2,200 frames, within the 4,500 cap): at 16,384, two references
# that differ only in their seed sat 0.037 apart there (|diff| over the mean), as far as the field from either, and the
# |error| metric measured the reference; at 262,144, 0.0096 (2026-10-13). The bright scenes keep 16,384.
# Each run shoots as soon as its light is at rest and its reference has all its paths (--shot-when-ready), plus a
# margin; the frame counts below are only the latest it may be.
reference=(--gpu-reference --ref-bounces 32)
paths=(--gpu-ref-paths 16384)
runFlags="${*:-defaults}"   # this gate's extra flags, for the windows' titles
frames=3000
run() {
    local name=$1; shift
    # The window's title names the run (SponzaLoop: BLIX_RUN_LABEL): which of the gate's scenes, with what flags.
    BLIX_RUN_LABEL="gate $name: $runFlags${BLIX_GATHER_SEED:+ [seed $BLIX_GATHER_SEED]}" "$REPO/blix" run ${build[@]+"${build[@]}"} Blix.Demos.VulkanSponza "$@" --no-fog --render-scale 1 --win=960 540 "${reference[@]}" "${paths[@]}" \
        --shot-when-ready --shot-frames=$frames --shot="$LOGS/$name.png" > "$LOGS/$name.log" 2>&1 || {
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
paths=(--gpu-ref-paths 262144 --gpu-ref-lanes 4096)
frames=4500
# And 96 points at the corridor's creases (--gpu-ref-seams): the box joints, where a leak lights first.
run crevices-corridor --scene=crevices-dark --cam 6,1.5,1.5,90,-5 --gpu-ref-seams 96 "$@"
# The corridor's second leg from its turn to its dead end, which the first leg's view never sees: lit only by what turns
# the corner, its light is where a leak through the walls shows -- a texel outside the far wall lit a patch inside, and
# the leg read 1.4-2.2x its path trace while every point above held (2026-10-23).
run crevices-deadend --scene=crevices-dark --cam 8.7,1.3,0.9,0,-8 "$@"
rm -f "$LOGS"/*.png
python3 "$REPO/tools/testbeds/gate.py" "$LOGS"
status=$?
echo "gate: logs in $LOGS"
exit $status
