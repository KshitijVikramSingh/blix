#!/usr/bin/env bash
# The vsync-on pacing pass, with repeats, owed since §83.
#
# What this answers that perf-matrix.sh cannot. That matrix runs with vsync OFF and reports what a frame
# COSTS — the right question for deciding where the work is, and the wrong one for deciding whether the game
# feels smooth. With the display in the loop every frame that makes its deadline costs exactly one refresh
# period, so p50 and p95 both read the refresh and tell you nothing; what a player feels is the frames that
# took two periods, and how those are clumped. PerformanceRun estimates the period from the run and reports
# the cadence against it.
#
# And why repeats. Every figure this arc has produced on this machine moves 25-75% under sustained load, and
# §126 spent an afternoon on an "inversion" that turned out to be one sample read from the wrong line. A
# single pacing run is an anecdote about a thermal state. Each case is run N times and the table prints the
# spread as well as the middle, so a claim can be argued with.
#
# Usage:
#   tools/perf-pacing.sh                     3 repeats of each case, 600 frames, Release
#   tools/perf-pacing.sh --repeats 5
#   tools/perf-pacing.sh --config Debug      the build anybody judging from the chair is judging
#   tools/perf-pacing.sh --frames 900
#   tools/perf-pacing.sh --table DIR         re-tabulate without measuring again
set -uo pipefail

REPO_ROOT="$(cd "$(dirname "$0")/.." && pwd)"
PROJECT="$REPO_ROOT/src/RTSGame/RTSGame.csproj"
RID="osx-arm64"
MAP_SEED=1592842292
RELIEF=32

FRAMES=600
REPEATS=3
CONFIG=Release
TABLE_DIR=""
OUT_DIR=""
# <b>Extra flags passed to every case, because "the game" is not one configuration.</b> The defaults draw full
# tree geometry and real cascade casters; the way this project is actually played from the chair is
# --cheap-trees --shadow-proxy, and §127's pacing table is meaningless as a claim about the game without
# saying which. The log directory records it.
EXTRA=""

while [ $# -gt 0 ]; do
    case "$1" in
        --frames) FRAMES="$2"; shift 2 ;;
        --repeats) REPEATS="$2"; shift 2 ;;
        --config) CONFIG="$2"; shift 2 ;;
        --out) OUT_DIR="$2"; shift 2 ;;
        --table) TABLE_DIR="$2"; shift 2 ;;
        --extra) EXTRA="$2"; shift 2 ;;
        *) echo "unknown argument: $1" >&2; exit 2 ;;
    esac
done

PUBLISH_DIR="$REPO_ROOT/src/RTSGame/bin/Publish-$CONFIG"

# <b>Few cases, many repeats.</b> The opposite balance to perf-matrix.sh, and deliberately: that matrix is
# looking for which configuration is expensive and wants coverage, while this one is asking whether a known
# configuration holds its cadence and wants confidence. Three standoffs still, and the wide view panning,
# which §87 measured as the band where the frame peaks.
#
# The names on the left are for the log files and the progress lines; the table below is keyed by the
# fixture's OWN derived label, because that is what the PERFCASE line carries and a table keyed by a name the
# binary never saw is a table that can disagree with its runs. (A --perf-label flag was written and then
# deleted: Program.cs ignores arguments it does not know, so it would have been a lever that was not one —
# the same trap as §121's --flat-heuristic and §126's stale binary.)
#
# case  motion  hour  zoom
CASES=$(cat <<'TABLE'
still-60    still  12 60
still-118   still  12 118
still-240   still  12 240
pan-118     pan    12 118
night-118   still  0  118
TABLE
)

tabulate() {
    local dir="$1"
    local lines
    lines=$(grep -h '^PERFCASE ' "$dir"/*.log 2>/dev/null | grep -v 'refresh_ms=0.00')
    if [ -z "$lines" ]; then
        echo "no PERFCASE lines under $dir — did every run fail?" >&2
        return 1
    fi

    echo
    echo "=== vsync-on pacing ($dir) ==="
    echo
    printf '%-24s %3s %8s %14s %8s %8s %8s %6s %s\n' \
        case n refresh "on time %" "two %" "3+ %" "per frame" "fps" "worst run"
    printf '%-24s %3s %8s %14s %8s %8s %8s %6s %s\n' \
        ------------------------ --- -------- -------------- -------- -------- -------- ------ ---------
    # Mean and range per case, from the runs themselves. A mean with no spread beside it is the thing this
    # script exists to stop producing.
    echo "$lines" | awk '
        {
            delete f
            for (i = 2; i <= NF; i++) { split($i, kv, "="); f[kv[1]] = kv[2] }
            c = f["case"]
            sub(/-r[0-9]+$/, "", c)
            n[c]++
            refresh[c] += f["refresh_ms"]
            ont[c] += f["pace_ontime"]
            if (!(c in onlo) || f["pace_ontime"] < onlo[c]) onlo[c] = f["pace_ontime"]
            if (!(c in onhi) || f["pace_ontime"] > onhi[c]) onhi[c] = f["pace_ontime"]
            two[c] += f["pace_doubled"]
            if (!(c in twolo) || f["pace_doubled"] < twolo[c]) twolo[c] = f["pace_doubled"]
            if (!(c in twohi) || f["pace_doubled"] > twohi[c]) twohi[c] = f["pace_doubled"]
            wor[c] += f["pace_worse"]
            if (!(c in wlo) || f["pace_worse"] < wlo[c]) wlo[c] = f["pace_worse"]
            if (!(c in whi) || f["pace_worse"] > whi[c]) whi[c] = f["pace_worse"]
            if (f["pace_worstrun"] + 0 > run[c] + 0) run[c] = f["pace_worstrun"]
            per[c] += f["pace_periods"]
            refreshOne = f["refresh_ms"]
        }
        END {
            for (c in n) {
                periods = per[c] / n[c]
                printf "%-24s %3d %8.2f %5.1f (%4.1f-%4.1f) %8.1f %8.1f %8.2f %6.1f %d\n",
                    c, n[c], refresh[c] / n[c],
                    100 * ont[c] / n[c], 100 * onlo[c], 100 * onhi[c],
                    100 * two[c] / n[c],
                    100 * wor[c] / n[c],
                    periods,
                    1000 / ((refresh[c] / n[c]) * periods),
                    run[c]
            }
        }' | sort
    echo
    echo "refresh is the monitor's own reported period — check it: 16.7 ms is sixty hertz, 8.3 is a hundred"
    echo "and twenty. A run that cannot read it reports no cadence rather than a flattering one."
    echo "brackets are the range over repeats, not a confidence interval — n is small on purpose."
    echo "worst late run is the longest consecutive stretch of frames that missed, over all repeats."
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
    tag=$(echo "$EXTRA" | tr -d ' -' | tr '[:upper:]' '[:lower:]')
    OUT_DIR="$REPO_ROOT/perf/$(date +%Y%m%d-%H%M%S)-pacing-$(echo "$CONFIG" | tr '[:upper:]' '[:lower:]')${tag:+-$tag}"
fi
mkdir -p "$OUT_DIR"
echo "logs: $OUT_DIR"
echo "this takes the window's focus for a few seconds per run — do not drive the machine while it runs."

DEADLINE=180
failed=0

run_case() {  # name motion hour zoom extra...
    local name="$1" motion="$2" hour="$3" zoom="$4"; shift 4
    local log="$OUT_DIR/$name.log"
    "$PUBLISH_DIR/RTSGame" --village --perf-run --perf-vsync --frames "$FRAMES" \
        --mapseed "$MAP_SEED" --relief-amplitude "$RELIEF" \
        --zoom "$zoom" --perf-camera "$motion" --perf-hour "$hour" "$@" >"$log" 2>&1 &
    local pid=$! waited=0
    while kill -0 "$pid" 2>/dev/null; do
        sleep 1
        waited=$((waited + 1))
        if [ "$waited" -ge "$DEADLINE" ]; then kill -9 "$pid" 2>/dev/null; return 1; fi
    done
    wait "$pid" 2>/dev/null
    grep -q '^PERFCASE ' "$log"
}

# <b>The display's period comes from the display.</b> Three attempts at deriving it from the game's own
# frames went wrong in three different ways — a tenth percentile said 33 ms when every frame was a double, a
# minimum picked up 14.18 ms of jitter on a 16.67 ms panel, and a "cheap case" median read 63 ms once the
# machine was busy. Silk reports the monitor's video mode, the fixture prints what it got, and nothing here
# has to guess. --perf-refresh still overrides it, for measuring against a deadline the panel does not have.
# <b>Repeats interleaved, not blocked.</b> Running case A three times and then case B three times measures
# the drift between the two blocks and attributes it to the cases; this is the ABBA rule the frame work
# earned in §83, applied to a script so nobody has to remember it.
for r in $(seq 1 "$REPEATS"); do
    while read -r name motion hour zoom; do
        [ -z "${name:-}" ] && continue
        log="$OUT_DIR/$name-r$r.log"
        printf '  %-12s r%-2s ' "$name" "$r"
        # Unquoted on purpose: --extra is a list of flags, not one argument.
        # shellcheck disable=SC2086
        if run_case "$name-r$r" "$motion" "$hour" "$zoom" $EXTRA; then
            grep -oE 'pace_ontime=[0-9.]+' "$log" | head -1
        else
            echo "NO RESULT (see $log)"
            failed=$((failed + 1))
        fi
    done <<< "$CASES"
done

tabulate "$OUT_DIR"
[ "$failed" -gt 0 ] && echo "$failed run(s) produced nothing"
exit 0
