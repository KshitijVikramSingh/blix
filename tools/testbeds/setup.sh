#!/usr/bin/env bash
# The correctness scenes: `blix cornell`, `blix thinwall` and `blix crevices` write glTF, and the cook reads them like any other source.
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
for scene in cornell cornell-panel thinwall crevices crevices-dark drapes sheetslab sheetshell sheetshell-thin; do
    app="${scene%-panel}"
    app="${app%-dark}"
    extra=()
    [[ "$scene" == *-panel ]] && extra=(--panel)
    [[ "$scene" == *-dark ]] && extra=(--albedo 0.25)
    # sheetshell: the sheet slab with a closed shell 1.5 cm thick (light through two walls, as the teacup's saucer).
    [[ "$scene" == sheetshell ]] && { app=sheetslab; extra=(--shell 0.015); }
    [[ "$scene" == sheetshell-thin ]] && { app=sheetslab; extra=(--shell 0.004); }
    "$REPO/blix" run --build "$app" ${extra[@]+"${extra[@]}"} --out "$SRC/$scene/$scene.gltf"
    rm -rf "${COOKED:?}/$scene"
    dotnet "$REPO/src/Blix.Tools.Cook/bin/Debug/net8.0/Blix.Tools.Cook.dll" asset "$SRC/$scene/$scene.gltf" --out "$COOKED/$scene"
    # The lightmap atlas (--lightmap), 4 cm texels.
    dotnet "$REPO/src/Blix.Tools.Cook/bin/Debug/net8.0/Blix.Tools.Cook.dll" lightmap "$COOKED/$scene" --write
done
# crevices-hashed: the dark crevices with no lightmap, so every texel is a hashed one (Sponza's trees and ivy are).
rm -rf "${COOKED:?}/crevices-hashed"
dotnet "$REPO/src/Blix.Tools.Cook/bin/Debug/net8.0/Blix.Tools.Cook.dll" asset "$SRC/crevices-dark/crevices-dark.gltf" --out "$COOKED/crevices-hashed"
# BLIX_TESTBED_KHRONOS=1: Khronos' diffuse-transmission samples (glTF-Sample-Assets, each under its own licence: its
# LICENSE.md beside it), downloaded once into "$SRC/khronos", placed by `blix khronos` and cooked: khronos-plant and
# khronos-teacup in the Cornell room's sunlit shaft, khronos-test on open ground (tools/testbeds/materials.sh judges them).
if [[ "${BLIX_TESTBED_KHRONOS:-0}" == 1 ]]; then
    K="$SRC/khronos"
    for m in DiffuseTransmissionPlant DiffuseTransmissionTeacup DiffuseTransmissionTest AlphaBlendModeTest; do
        if [[ ! -f "$K/$m/$m.glb" ]]; then
            mkdir -p "$K/$m"
            base="https://raw.githubusercontent.com/KhronosGroup/glTF-Sample-Assets/main/Models/$m"
            curl -sSfL -o "$K/$m/$m.glb" "$base/glTF-Binary/$m.glb"
            curl -sSfL -o "$K/$m/LICENSE.md" "$base/LICENSE.md"
        fi
    done
    khronos() {
        local scene=$1; shift
        "$REPO/blix" run --build khronos "$@" --out "$SRC/$scene/$scene.gltf"
        rm -rf "${COOKED:?}/$scene"
        dotnet "$REPO/src/Blix.Tools.Cook/bin/Debug/net8.0/Blix.Tools.Cook.dll" asset "$SRC/$scene/$scene.gltf" --out "$COOKED/$scene"
        dotnet "$REPO/src/Blix.Tools.Cook/bin/Debug/net8.0/Blix.Tools.Cook.dll" lightmap "$COOKED/$scene" --write
    }
    khronos khronos-plant --model "$K/DiffuseTransmissionPlant/DiffuseTransmissionPlant.glb" --scale 1.5 --at -0.7,0,-1.68 --room cornell
    khronos khronos-teacup --model "$K/DiffuseTransmissionTeacup/DiffuseTransmissionTeacup.glb" --scale 8 --at -0.7,0,-1.68 --rest --room cornell
    khronos khronos-test --model "$K/DiffuseTransmissionTest/DiffuseTransmissionTest.glb" --scale 0.5 --at 0,2.13,0 --room ground
    khronos khronos-alpha --model "$K/AlphaBlendModeTest/AlphaBlendModeTest.glb" --scale 0.5 --at 0,0,0 --rest --room ground
    # crytek-sponza: Khronos' Sponza (Crytek's; the CRYENGINE Limited License -- downloaded here, never committed), cooked as
    # it is: the atrium at ~262 k triangles, Sponza's layout for quick runs (SceneProfile.CrytekSponza).
    if [[ ! -f "$K/Sponza/Sponza.gltf" ]]; then
        mkdir -p "$K/Sponza"
        base="https://raw.githubusercontent.com/KhronosGroup/glTF-Sample-Assets/main/Models/Sponza"
        curl -sSfL "https://api.github.com/repos/KhronosGroup/glTF-Sample-Assets/contents/Models/Sponza/glTF" \
            | python3 -c "import json,sys; [print(x['name']) for x in json.load(sys.stdin)]" \
            | while read -r f; do curl -sSfL -o "$K/Sponza/$f" "$base/glTF/$f"; done
        curl -sSfL -o "$K/Sponza/LICENSE.md" "$base/LICENSE.md"
    fi
    rm -rf "${COOKED:?}/crytek-sponza"
    dotnet "$REPO/src/Blix.Tools.Cook/bin/Debug/net8.0/Blix.Tools.Cook.dll" asset "$K/Sponza/Sponza.gltf" --out "$COOKED/crytek-sponza"
    dotnet "$REPO/src/Blix.Tools.Cook/bin/Debug/net8.0/Blix.Tools.Cook.dll" lightmap "$COOKED/crytek-sponza" --write
fi
