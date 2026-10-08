#!/usr/bin/env bash
# The GI correctness gate: the correctness scenes rendered and held against the CPU path trace on their triangles.
#
#   BLIX_TESTBED_ASSETS  the cooked scenes (tools/testbeds/setup.sh makes them; this runs it when they are missing).
#
# Three headed runs at 960x540 (a window opens and closes each time): the Cornell box from outside the open front,
# then the thin-wall scene from inside its sealed room and from the open one. Each converges 1500 frames and runs
# --probe-reference; tools/testbeds/gate.py turns the logs into the verdict (targets and ratchets: see there).
# Exit 0 when every ratchet holds. Logs stay in the directory printed at the end.
#
# Usage: tools/testbeds/gate.sh [extra demo flags...]   e.g. --clipmap-guide 0 to price a change against the bars
set -euo pipefail
REPO="$(cd "$(dirname "$0")/../.." && pwd)"
if [[ -z "${BLIX_TESTBED_ASSETS:-}" ]]; then
    echo "gate: BLIX_TESTBED_ASSETS is not set; it names the cooked correctness scenes." >&2
    exit 1
fi
for scene in cornell thinwall; do
    if [[ ! -f "$BLIX_TESTBED_ASSETS/$scene/$scene.blixmesh" ]]; then
        echo "gate: $scene is not cooked; running tools/testbeds/setup.sh"
        "$REPO/tools/testbeds/setup.sh"
        break
    fi
done
LOGS="$(mktemp -d "${TMPDIR:-/tmp}/blix-gi-gate.XXXXXX")"
export BLIX_CONFIG=Release BLIX_DIAG=off
build=(--build)
run() {
    local name=$1; shift
    "$REPO/blix" run ${build[@]+"${build[@]}"} Blix.Demos.VulkanSponza "$@" --no-fog --win=960 540 --probe-reference \
        --shot-frames=1500 --shot="$LOGS/$name.png" > "$LOGS/$name.log" 2>&1 || {
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
rm -f "$LOGS"/*.png
python3 "$REPO/tools/testbeds/gate.py" "$LOGS"
status=$?
echo "gate: logs in $LOGS"
exit $status
