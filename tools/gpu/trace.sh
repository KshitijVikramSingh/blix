#!/usr/bin/env bash
# A Metal System Trace of a running demo, summarised per render-graph pass (trace_summary.py).
#
#   tools/gpu/trace.sh OUT.trace SECONDS WARMUP_SECONDS -- <blix run arguments...>
#
# Starts the demo with BLIX_GPU_LABELS=1 (each pass a debug label, so the trace names its encoders), waits WARMUP_SECONDS,
# attaches xctrace for SECONDS with the Metal Application and GPU instruments only -- the full Metal System Trace
# template's GPU counters slowed Cornell ~4x (8.8 -> 34 ms a frame) and so measured another frame -- then closes the demo
# and prints the per-pass GPU time. Open OUT.trace in Instruments for the timeline itself.
set -euo pipefail
out=$1; seconds=$2; warmup=$3; shift 3
[[ "${1:-}" == "--" ]] && shift
REPO="$(cd "$(dirname "$0")/../.." && pwd)"
rm -rf "$out"
log="${out%.trace}.log"
BLIX_GPU_LABELS=1 "$REPO/blix" run Blix.Demos.VulkanSponza "$@" > "$log" 2>&1 &
launcher=$!
pid=""
for _ in $(seq 1 120); do
    # Anchored to the executable's path: unanchored, the pattern matched the shell that ran this script.
    pid=$(pgrep -f "^$REPO/src/Demos/Blix.Demos.VulkanSponza/bin/[^ ]*/Blix.Demos.VulkanSponza " | head -1 || true)
    [[ -n "$pid" ]] && break
    sleep 1
done
[[ -z "$pid" ]] && { echo "trace: the demo did not start (log: $log)" >&2; exit 1; }
sleep "$warmup"
xcrun xctrace record --instrument 'Metal Application' --instrument 'GPU' --attach "$pid" --time-limit "${seconds}s" --output "$out" > /dev/null
kill "$pid" 2>/dev/null || true
wait "$launcher" 2>/dev/null || true
python3 "$REPO/tools/gpu/trace_summary.py" "$out"
