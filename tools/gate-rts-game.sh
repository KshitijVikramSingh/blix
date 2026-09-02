#!/usr/bin/env bash
# The RTS game's gate. Three quick runs by default; --years adds the two simulated years.
#
# They cover different failure modes on purpose, and none of them subsumes another:
#
#   --selftest   83 assertions, seconds each. Catches a broken rule.
#   --settlement a simulated year. Catches an economy that no longer feeds itself — which no
#                assertion can, because "the settlement starves in the fourth season" is a
#                property of a year and not of a tick.
#   --settlement --relief-amplitude 30
#                the same year on generated terrain. Catches an economy that only works on a plain.
#   --raidtest   a settlement under raid. Catches a defence that stops defending, and it exists
#                because Stage E shipped without it and every single thing it got wrong was a
#                thing this would have said out loud.
#   --mapsweep   every map the panel can ask for. Catches a generator that only works at the one
#                setting the leg above happens to use, which is what it did: the relief dial at
#                four metres threw an exception in the first minute anybody had the dial.
#
# Conservation is checked every tick of the three settlement runs, so a unit of grain going
# missing fails whichever run was unlucky enough to be holding it.
set -uo pipefail

REPO_ROOT="$(cd "$(dirname "$0")/.." && pwd)"
PROJECT="$REPO_ROOT/src/RTSGame/RTSGame.csproj"

echo "building ..."
dotnet build "$PROJECT" -c Debug --nologo -v:q || exit 1

failed=0
run() {
    local name="$1"; shift
    echo
    echo "=== $name ==="
    if dotnet run --project "$PROJECT" -c Debug --no-build -- "$@"; then
        echo "--- $name OK"
    else
        echo "--- $name FAILED"
        failed=$((failed + 1))
    fi
}

# <b>Two tiers, because two of these legs are minutes-scale simulations and three are not.</b> A simulated
# year steps 162,000 ticks and sweeps every node on each of them for conservation; at four thousand nodes that
# is minutes, and at thirty thousand it is a good deal more. Gating a comment change on it means either waiting
# or — what actually happened — not running the gate at all, which is worse than a slower gate.
#
# So the default is the tier that answers in a couple of minutes, and the years are opt-in with --years. What
# each tier is FOR:
#
#   default   a broken rule, a defence that stops defending, a generator that only works at one setting.
#             Everything whose failure is a property of a tick or of a single run.
#   --years   an economy that stops feeding itself, which no assertion can catch because "the settlement
#             starves in the fourth season" is a property of a year. Run before landing anything that touches
#             rates, jobs, hauling or construction — and in downtime otherwise.
#
# <b>The split is a scheduling decision, not a licence.</b> The year legs are the only thing that has ever
# caught an economy regression, and §51 exists because Stage E shipped without the raid leg. Skipping them
# before an economy change is skipping the only instrument that would say so.
years=0
for arg in "$@"; do
    case "$arg" in
        --years|--full) years=1 ;;
    esac
done

run "self-test" --selftest
run "a settlement under raid" --raidtest --minutes 6 --every 45
# <b>The same year again, on ground the generator actually makes.</b> The three legs above run on flat ground,
# which is where every economic constant was measured and is therefore the control: it says whether a change
# broke the economy. This one says whether the economy survives the terrain the game generates — woodland
# coupled to biome, path cost to surface, climb charged per edge — and it is a different question. First run of
# it came out at 270 grain per person per year against a nominal 270, so the coupling costs nothing; the point
# of keeping it is that nobody would notice when it starts to.
if [ "$years" -eq 1 ]; then
    run "a year of settlement" --settlement --years 1
    run "a year on generated terrain" --settlement --years 1 --relief-amplitude 30
fi
# <b>Every map the panel can ask for, which is not the map this gate used to test.</b> The leg above generates
# terrain at exactly one point — one archetype, one region, thirty metres — and that was the whole of the
# generator's coverage while the map was chosen by a flag: the tested set and the reachable set were the same
# set. A control panel hands that choice to whoever is holding the mouse, and the reachable set becomes the
# whole product space. The first thing anybody did with the relief dial was turn it to four metres, where a
# river's depth floor of 1.1 m crosses its ceiling of amplitude x 0.17 and Math.Clamp throws.
#
# Cheap because it stops at terrain and country rather than founding a settlement: 55 maps in 40 seconds.
run "every map the panel can ask for" --mapsweep
# <b>Two settlements on one map, which is the precondition for a second player.</b> §130: the first version of
# this found the earlier-founded settlement inert — Populate dresses the whole map, so founding a second one
# re-forested the first one's cleared ground and walled its houses off from its granary eleven metres away.
# Cheap (a fraction of a year) and it guards a seam that only appears when the routine runs twice.
run "two settlements on one map" --twovillages --years 0.1
# <b>And one of them driven by a rule-bot, from a standing start.</b> §132: the bot's settlement is stripped of
# every assignment before it takes over, so the leg asks whether a bot can put an idle settlement to work
# through the same queued commands a person has — and the pass condition is the year leg's own, that it feeds
# itself and the books balance.
run "a bot runs a settlement" --twovillages --years 0.1 --bot

echo
legs=5
[ "$years" -eq 1 ] && legs=7
if [ "$failed" -eq 0 ]; then
    if [ "$years" -eq 1 ]; then
        echo "gate: all $legs green, years included"
    else
        echo "gate: all $legs green — years NOT run, use --years before an economy change"
    fi
else
    echo "gate: $failed of $legs FAILED"
fi
exit "$failed"
