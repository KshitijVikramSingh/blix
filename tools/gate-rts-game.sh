#!/usr/bin/env bash
# The RTS game's gate: the four headless runs that have to be green before a change lands.
#
# They cover different failure modes on purpose, and none of them subsumes another:
#
#   --selftest   82 assertions, seconds each. Catches a broken rule.
#   --settlement a simulated year. Catches an economy that no longer feeds itself — which no
#                assertion can, because "the settlement starves in the fourth season" is a
#                property of a year and not of a tick.
#   --settlement --relief-amplitude 30
#                the same year on generated terrain. Catches an economy that only works on a plain.
#   --raidtest   a settlement under raid. Catches a defence that stops defending, and it exists
#                because Stage E shipped without it and every single thing it got wrong was a
#                thing this would have said out loud.
#
# Conservation is checked every tick of all three, so a unit of grain going missing fails
# whichever run was unlucky enough to be holding it.
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

echo
if [ "$failed" -eq 0 ]; then
    echo "gate: all four green"
else
    echo "gate: $failed of 3 FAILED"
fi
exit "$failed"
