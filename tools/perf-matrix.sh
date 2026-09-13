#!/usr/bin/env bash
# The stage-0 camera and daylight matrix, run as one command and tabulated from the runs themselves.
#
# Why a script and not a person with a stopwatch: the matrix is two dozen sealed runs, and every wrong
# number this stage has produced so far came from a figure recalled from a scrolling log rather than
# recorded. Each case here runs the published binary with --perf-run, which ignores live input and leaves
# the diagnostics panels out of the frame, and prints one machine-readable PERFCASE line at the end. The
# table at the bottom is generated from those lines, so the plan's numbers and the runs cannot disagree.
#
# What the cases cover, and why these and not the full cross product (4 motions x 3 lights x 3 standoffs x
# fog would be 72 runs and about half an hour of stolen window focus):
#
#   sweep    every camera motion at every standoff, at noon with fog on. Camera motion is the axis that
#            rebuilds work — ground chunks, cover placement, cascade fitting — so it gets full coverage.
#   light    dusk and night at the two standoffs that matter, still and panning. Hearths, smoke and the
#            veil only cost anything once the sun is down, and only the wide views can afford to be
#            surprised.
#   fog      the ablation, still and at noon. Fog is a whole-frame gate on what is drawn at all, so its
#            cost is read against the sweep's own still cases rather than against a motion of its own.
#
# Each run opens a window and takes focus for a few seconds. That is unavoidable — the thing being measured
# is a swapchain presenting frames — so expect the screen to flicker between cases and do not drive the
# machine while it runs, or you are measuring your own mouse.
#
# Usage:
#   tools/perf-matrix.sh                        all groups, 600 frames each
#   tools/perf-matrix.sh --frames 300           shorter runs
#   tools/perf-matrix.sh --config Release       measure the optimised build instead of the played one
#   tools/perf-matrix.sh --only sweep           one group (sweep|light|fog), or any regex over case names
#   tools/perf-matrix.sh --list                 print the cases and exit
#   tools/perf-matrix.sh --table DIR            re-tabulate an earlier run's logs without measuring again
set -uo pipefail

REPO_ROOT="$(cd "$(dirname "$0")/.." && pwd)"
PROJECT="$REPO_ROOT/src/RTSGame/RTSGame.csproj"
PUBLISH_DIR="$REPO_ROOT/src/RTSGame/bin/Publish"
RID="osx-arm64"

# <b>One map for every case, pinned.</b> The default village seed is what the first landing was measured on,
# and a matrix taken across different maps is a matrix about maps. 0x5EED1234.
MAP_SEED=1592842292
RELIEF=32

FRAMES=600
ONLY=""
LIST_ONLY=0
TABLE_DIR=""
OUT_DIR=""
# <b>Which build the envelope is argued from is a real question, so it is a flag.</b> Debug is what
# run-rts-game.sh launches and therefore what anybody judging the game from the chair is judging; Release is
# what a player would run. The two differ almost entirely in the CPU build phases — the node sweep, the
# cover resolve — and not at all in what the GPU was handed, so a case measured in one configuration cannot
# be quoted in the other. The matrix records which it was in the log directory's own name.
CONFIG=Debug

while [ $# -gt 0 ]; do
    case "$1" in
        --frames) FRAMES="$2"; shift 2 ;;
        --config) CONFIG="$2"; shift 2 ;;
        --only) ONLY="$2"; shift 2 ;;
        --out) OUT_DIR="$2"; shift 2 ;;
        --table) TABLE_DIR="$2"; shift 2 ;;
        --list) LIST_ONLY=1; shift ;;
        *) echo "unknown argument: $1" >&2; exit 2 ;;
    esac
done

# group  motion  hour  zoom  fog
CASES=$(cat <<'TABLE'
sweep still  12 24  on
sweep still  12 60  on
sweep still  12 118 on
sweep pan    12 24  on
sweep pan    12 60  on
sweep pan    12 118 on
sweep rotate 12 24  on
sweep rotate 12 60  on
sweep rotate 12 118 on
sweep zoom   12 118 on
light still  19 60  on
light still  19 118 on
light still  0  60  on
light still  0  118 on
light pan    19 118 on
light pan    0  118 on
fog   still  12 24  off
fog   still  12 60  off
fog   still  12 118 off
fog   still  0  118 off
TABLE
)

case_name() { echo "$2-${3}h-${4}m-fog$5"; }

if [ "$LIST_ONLY" -eq 1 ]; then
    echo "$CASES" | while read -r group motion hour zoom fog; do
        [ -z "${group:-}" ] && continue
        printf '%-6s %s\n' "$group" "$(case_name "$group" "$motion" "$hour" "$zoom" "$fog")"
    done
    exit 0
fi

# <b>Tabulation is separable from measurement.</b> Reading a table wrong is not a reason to spend ten more
# minutes of window focus re-measuring what is already on disk.
tabulate() {
    local dir="$1"
    local lines
    lines=$(grep -h '^PERFCASE ' "$dir"/*.log 2>/dev/null)
    if [ -z "$lines" ]; then
        echo "no PERFCASE lines under $dir — did every run fail?" >&2
        return 1
    fi

    echo
    echo "=== stage-0 camera and daylight matrix ($dir) ==="
    echo
    printf '%-26s %5s %7s %7s %7s %8s %7s %9s %9s %-22s %6s\n' \
        case vsync p50 p95 max startup tick scene-tri cast-tri "cast near/mid/far" trees
    printf '%-26s %5s %7s %7s %7s %8s %7s %9s %9s %-22s %6s\n' \
        -------------------------- ----- ------- ------- ------- -------- ------- --------- --------- ---------------------- ------
    echo "$lines" | awk '
        {
            delete f
            for (i = 2; i <= NF; i++) { split($i, kv, "="); f[kv[1]] = kv[2] }
            printf "%-26s %5s %7.1f %7.1f %7.1f %8.1f %7.3f %9s %9s %-22s %6d\n",
                f["case"], f["vsync"], f["frame_p50"], f["frame_p95"], f["frame_max"], f["startup_max"], f["tick_ms"],
                sprintf("%.0fk", f["scene_tris"] / 1000),
                sprintf("%.0fk", f["cast_tris"] / 1000),
                sprintf("%.0fk/%.0fk/%.0fk", f["cast_tris_near"] / 1000, f["cast_tris_mid"] / 1000, f["cast_tris_far"] / 1000),
                f["trees"]
        }' | sort
    echo
    echo "where the frame went (p50 ms, outside = acquire/submit/GPU wait):"
    echo
    printf '%-26s %8s %8s %6s %8s %8s %8s\n' case frame update fog render outside nodes
    printf '%-26s %8s %8s %6s %8s %8s %8s\n' \
        -------------------------- -------- -------- ------ -------- -------- --------
    echo "$lines" | awk '
        {
            delete f
            for (i = 2; i <= NF; i++) { split($i, kv, "="); f[kv[1]] = kv[2] }
            printf "%-26s %8.1f %8.1f %6.1f %8.1f %8.1f %8.1f\n",
                f["case"], f["frame_p50"], f["update_p50"], f["fog_p50"], f["render_p50"],
                f["outside_p50"], f["nodes_p50"]
        }' | sort
    echo
    echo "ms are wall-clock frame times: p50/p95/max over the steady window, startup = worst warm-up frame."
    echo "vsync off means the figure is what the frame cost; vsync on means it is what the display allowed."
    echo "tick is the simulation's own average total. Triangle figures are submitted, not unique."
}

if [ -n "$TABLE_DIR" ]; then
    tabulate "$TABLE_DIR"
    exit $?
fi

prefix=$(brew --prefix 2>/dev/null || echo "/opt/homebrew")
if [ ! -f "$prefix/lib/libvulkan.dylib" ]; then
    echo "libvulkan.dylib not found under $prefix/lib — install the Blix Vulkan prerequisites first." >&2
    exit 1
fi
if [ -z "${DOTNET_ROOT:-}" ] && [ -d "$prefix/opt/dotnet@8/libexec" ]; then
    export DOTNET_ROOT="$prefix/opt/dotnet@8/libexec"
fi
if [ -d "$prefix/opt/dotnet@8/bin" ]; then
    export PATH="$prefix/opt/dotnet@8/bin:$PATH"
fi

# <b>Published once, not once per case.</b> run-rts-game.sh publishes on every launch, which is right for a
# person iterating on a shader and wrong here: twenty publishes is four minutes of the matrix spent proving
# the same binary twenty times, and a rebuild between cases would mean the cases were not all the same
# binary. The shader copy below is the same guard that script documents — the publish compiles its own
# SPIR-V and bin/Debug's copies win, so a stale Debug build launches yesterday's shaders.
echo "building ($CONFIG) ..."
dotnet build "$PROJECT" -c "$CONFIG" --nologo -v:q || exit 1
echo "publishing self-contained ($RID, $CONFIG) ..."
dotnet publish "$PROJECT" -c "$CONFIG" -r "$RID" --self-contained true -o "$PUBLISH_DIR" --nologo -v:q || exit 1
BUILD_SHADERS="$REPO_ROOT/src/RTSGame/bin/$CONFIG/net8.0/$RID/Shaders"
[ -d "$BUILD_SHADERS" ] || BUILD_SHADERS="$REPO_ROOT/src/RTSGame/bin/$CONFIG/net8.0/Shaders"
if [ -d "$BUILD_SHADERS" ]; then
    mkdir -p "$PUBLISH_DIR/Shaders"
    cp -p "$BUILD_SHADERS"/*.spv "$PUBLISH_DIR/Shaders/" 2>/dev/null || true
fi

export DYLD_FALLBACK_LIBRARY_PATH="$prefix/lib:${DYLD_FALLBACK_LIBRARY_PATH:-}"
export VK_ICD_FILENAMES="$prefix/etc/vulkan/icd.d/MoltenVK_icd.json"
export VK_LAYER_PATH="$prefix/share/vulkan/explicit_layer.d"

if [ -z "$OUT_DIR" ]; then
    OUT_DIR="$REPO_ROOT/perf/$(date +%Y%m%d-%H%M%S)-$(echo "$CONFIG" | tr '[:upper:]' '[:lower:]')"
fi
mkdir -p "$OUT_DIR"
echo "logs: $OUT_DIR"

# A case that hangs must not hang the matrix. Generous, because map generation and first presentation are
# themselves seconds and a wide night view is the slowest thing here.
DEADLINE=180

failed=0
ran=0
while read -r group motion hour zoom fog; do
    [ -z "${group:-}" ] && continue
    name=$(case_name "$group" "$motion" "$hour" "$zoom" "$fog")
    if [ -n "$ONLY" ] && ! echo "$group $name" | grep -Eq "$ONLY"; then
        continue
    fi

    log="$OUT_DIR/$name.log"
    args=(--village --perf-run --frames "$FRAMES" --mapseed "$MAP_SEED" --relief-amplitude "$RELIEF"
          --zoom "$zoom" --perf-camera "$motion" --perf-hour "$hour")
    [ "$fog" = "off" ] && args+=(--nofog)

    printf '  %-26s ' "$name"
    "$PUBLISH_DIR/RTSGame" "${args[@]}" >"$log" 2>&1 &
    pid=$!
    waited=0
    while kill -0 "$pid" 2>/dev/null; do
        sleep 1
        waited=$((waited + 1))
        if [ "$waited" -ge "$DEADLINE" ]; then
            echo "TIMEOUT after ${DEADLINE}s"
            kill -9 "$pid" 2>/dev/null
            break
        fi
    done
    wait "$pid" 2>/dev/null
    status=$?
    ran=$((ran + 1))

    if [ "$waited" -ge "$DEADLINE" ]; then
        failed=$((failed + 1))
    elif line=$(grep -m1 '^PERFCASE ' "$log"); then
        p50=$(echo "$line" | tr ' ' '\n' | awk -F= '$1=="frame_p50"{print $2}')
        p95=$(echo "$line" | tr ' ' '\n' | awk -F= '$1=="frame_p95"{print $2}')
        echo "p50 ${p50} ms, p95 ${p95} ms"
    else
        echo "NO REPORT (exit $status) — see $log"
        failed=$((failed + 1))
    fi
done <<< "$CASES"

tabulate "$OUT_DIR"

echo
if [ "$failed" -eq 0 ]; then
    echo "matrix: $ran cases, all reported"
else
    echo "matrix: $failed of $ran cases produced no report"
fi
exit "$failed"
