#!/usr/bin/env bash
# Resize the viewer's window while it runs, and report whether it survived.
#
# <b>The instrument for a class of bug nothing else here can see.</b> A swapchain resize
# reallocates every matchSwapchain-sized graph resource and rebuilds the framebuffers that point at
# them, and getting that wrong fails LATER — vkAcquireNextImageKHR with ErrorDeviceLost, several
# frames on, with no validation message and no stack that names the resize. The lab baseline cannot
# see it: every capture is a fixed-size bounded run that never resizes.
#
# Drives the window through System Events, so it needs Accessibility permission for the terminal.
#
#   tools/check-resize.sh <rig> <clip>
#
# Found it the first time on 2026-09-17: multisampled depth came back SINGLE-SAMPLED from a resize,
# against a render pass that still expected multisample.
set -uo pipefail

REPO_ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$REPO_ROOT"

RIG="${1:-src/Demos/Blix.Demos.Runner/Assets/models/Rogue.glb}"
CLIP="${2:-Running_A}"
LOG="$(mktemp -t blix-resize)"

tools/run-lab.sh --rig "$RIG" --clip "$CLIP" --instances 3 --frames 1200 > "$LOG" 2>&1 &
APP=$!
sleep 12
for wh in "900 600" "1500 950" "640 480" "1280 800" "1000 700"; do
    # shellcheck disable=SC2086
    set -- $wh
    osascript -e "tell application \"System Events\" to tell process \"Blix.Tools.View\" to set size of window 1 to {$1, $2}" 2>/dev/null
    sleep 2
done
wait $APP; code=$?
lost=$(grep -c DeviceLost "$LOG")
echo "resize check: exit=$code device-lost=$lost frames=$(grep -o '\[diag\] f[0-9]*' "$LOG" | tail -1)"
rm -f "$LOG"
[ "$code" -eq 0 ] && [ "$lost" -eq 0 ]
