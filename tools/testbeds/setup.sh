#!/usr/bin/env bash
# The correctness scenes: `blix cornell` and `blix thinwall` write glTF, and the cook reads them like any other source.
#
#   BLIX_TESTBED_ASSETS  the COOKED tree (the scene host reads <it>/<scene>). Required: no default.
#   BLIX_TESTBED_SRC     the generated glTF, defaulting to "<cooked>-src".
#
# Usage: tools/testbeds/setup.sh
set -euo pipefail
REPO="$(cd "$(dirname "$0")/../.." && pwd)"
if [[ -z "${BLIX_TESTBED_ASSETS:-}" ]]; then
    echo "testbeds: BLIX_TESTBED_ASSETS is not set; it names the COOKED tree." >&2
    exit 1
fi
COOKED="$BLIX_TESTBED_ASSETS"
SRC="${BLIX_TESTBED_SRC:-${COOKED%/}-src}"
dotnet build "$REPO/src/Blix.Tools.Cook" -v quiet --nologo >/dev/null
# cornell-panel: the Cornell box with a panel hung from the skylight's edge (`--mover panel` swings it across the light).
for scene in cornell cornell-panel thinwall; do
    app="${scene%-panel}"
    extra=()
    [[ "$scene" == *-panel ]] && extra=(--panel)
    "$REPO/blix" run --build "$app" ${extra[@]+"${extra[@]}"} --out "$SRC/$scene/$scene.gltf"
    rm -rf "${COOKED:?}/$scene"
    dotnet "$REPO/src/Blix.Tools.Cook/bin/Debug/net8.0/Blix.Tools.Cook.dll" asset "$SRC/$scene/$scene.gltf" --out "$COOKED/$scene"
    # The lightmap atlas (--lightmap), 4 cm texels.
    dotnet "$REPO/src/Blix.Tools.Cook/bin/Debug/net8.0/Blix.Tools.Cook.dll" lightmap "$COOKED/$scene" --write
done
