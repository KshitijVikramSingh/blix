#!/usr/bin/env bash
# The RTS game's gate: the five headless runs that have to be green before a change lands.
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

run "self-test" --selftest
run "a year of settlement" --settlement --years 1
run "a settlement under raid" --raidtest --minutes 6 --every 45
# <b>The same year again, on ground the generator actually makes.</b> The three legs above run on flat ground,
# which is where every economic constant was measured and is therefore the control: it says whether a change
# broke the economy. This one says whether the economy survives the terrain the game generates — woodland
# coupled to biome, path cost to surface, climb charged per edge — and it is a different question. First run of
# it came out at 270 grain per person per year against a nominal 270, so the coupling costs nothing; the point
# of keeping it is that nobody would notice when it starts to.
run "a year on generated terrain" --settlement --years 1 --relief-amplitude 30
# <b>Every map the panel can ask for, which is not the map this gate used to test.</b> The leg above generates
# terrain at exactly one point — one archetype, one region, thirty metres — and that was the whole of the
# generator's coverage while the map was chosen by a flag: the tested set and the reachable set were the same
# set. A control panel hands that choice to whoever is holding the mouse, and the reachable set becomes the
# whole product space. The first thing anybody did with the relief dial was turn it to four metres, where a
# river's depth floor of 1.1 m crosses its ceiling of amplitude x 0.17 and Math.Clamp throws.
#
# Cheap because it stops at terrain and country rather than founding a settlement: 55 maps in 40 seconds.
run "every map the panel can ask for" --mapsweep

echo
if [ "$failed" -eq 0 ]; then
    echo "gate: all five green"
else
    echo "gate: $failed of 5 FAILED"
fi
exit "$failed"
