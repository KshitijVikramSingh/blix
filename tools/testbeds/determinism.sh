#!/usr/bin/env bash
# The GI's determinism: each scene rendered twice under RTGI, shot at rest, the two shots held against each other byte
# for byte (tools/testbeds/determinism.py). A run's light must not hang on when its background tasks finish nor on the
# order threads meet in -- a comparison of two variants measured noise otherwise (the crevices' corridor |error| 0.0161
# and 0.0176 for one build, 2026-10-22).
#
#   BLIX_TESTBED_ASSETS  the cooked scenes (tools/testbeds/setup.sh).
#
# Exit 0 when every pair matches.
#
# Usage: tools/testbeds/determinism.sh [extra demo flags...]
set -euo pipefail
REPO="$(cd "$(dirname "$0")/../.." && pwd)"
if [[ -z "${BLIX_TESTBED_ASSETS:-}" ]]; then
    echo "determinism: BLIX_TESTBED_ASSETS is not set; it names the cooked scenes." >&2
    exit 1
fi
OUT="$(mktemp -d "${TMPDIR:-/tmp}/blix-determinism.XXXXXX")"
export BLIX_CONFIG=Release BLIX_DIAG=off
build=(--build)
gi=(--rtgi --rtgi-volume --rt-shadows --lod-pixels 0)
run() {
    local name=$1; shift
    "$REPO/blix" run ${build[@]+"${build[@]}"} Blix.Demos.VulkanSponza "${gi[@]}" "$@" --no-fog --render-scale 1 \
        --win=960 540 --shot-when-ready --shot-frames=3000 --shot="$OUT/$name.png" > "$OUT/$name.log" 2>&1 || {
        echo "determinism: the $name run failed; its log is $OUT/$name.log" >&2
        tail -5 "$OUT/$name.log" >&2
        exit 1
    }
    build=()
}
pair() {
    local name=$1; shift
    run "$name-a" "$@"
    run "$name-b" "$@"
    echo "determinism: $name run twice"
}
pair crevices --scene=crevices-dark --cam 6,1.5,1.5,90,-5 ${@+"$@"}
pair teacup --scene=khronos-teacup --cam 0.291,1.958,-0.454,-13.78,-33.62 ${@+"$@"}
status=0
uv run -q --with numpy --with pillow python3 "$REPO/tools/testbeds/determinism.py" "$OUT" crevices teacup || status=$?
echo "determinism: shots in $OUT"
exit $status
