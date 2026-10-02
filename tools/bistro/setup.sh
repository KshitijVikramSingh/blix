#!/usr/bin/env bash
# Amazon Lumberyard Bistro (ORCA, CC-BY 4.0) from its download to a cooked tree Blix reads:
#
#   1. fetch      Bistro_v5_2.zip from developer.nvidia.com/bistro (no login; ~0.9 GB), unless it is there
#   2. convert    each FBX to glTF with Blender headless (tools/bistro/fbx-to-gltf.py): a format step only
#   3. cook       `blix cook project bistro.blixcook`, which holds every scene decision, and the sky probe
#
#   BLIX_BISTRO_ASSETS  the COOKED tree. Required, as BLIX_SPONZA_ASSETS is: no default.
#   BLIX_BISTRO_SRC     the SOURCE tree (zip, FBX, converted glTF), defaulting to "<cooked>-src".
#
# Usage: tools/bistro/setup.sh [exterior|interior ...]   (default: exterior)
set -euo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"
REPO="$(cd "$HERE/../.." && pwd)"
if [[ -z "${BLIX_BISTRO_ASSETS:-}" ]]; then
    echo "bistro: BLIX_BISTRO_ASSETS is not set; it names the COOKED tree." >&2
    exit 1
fi
COOKED="$BLIX_BISTRO_ASSETS"
SRC="${BLIX_BISTRO_SRC:-${COOKED%/}-src}"
PARTS=("${@:-exterior}")
command -v blender >/dev/null || { echo "bistro: Blender is required for the FBX -> glTF step." >&2; exit 1; }

mkdir -p "$SRC"
if [[ ! -d "$SRC/Bistro_v5_2" ]]; then
    [[ -f "$SRC/Bistro_v5_2.zip" ]] || curl -fL -o "$SRC/Bistro_v5_2.zip" https://developer.nvidia.com/bistro
    unzip -q -o "$SRC/Bistro_v5_2.zip" -d "$SRC"
fi

for part in "${PARTS[@]}"; do
    case "$part" in
        exterior) fbx=BistroExterior ;;
        interior) fbx=BistroInterior ;;
        *) echo "bistro: unknown part '$part' (exterior, interior)" >&2; exit 1 ;;
    esac
    out="$SRC/$part/$fbx.gltf"
    if [[ ! -f "$out" || "$HERE/fbx-to-gltf.py" -nt "$out" ]]; then
        rm -rf "$SRC/$part" && mkdir -p "$SRC/$part"
        (cd "$SRC/Bistro_v5_2" && blender -b --python "$HERE/fbx-to-gltf.py" -- "$fbx.fbx" "$out" 2>&1 | grep -E "^\[bistro\]|Error|Traceback")
    fi
done

dotnet build "$REPO/src/Blix.Tools.Cook" -v quiet --nologo >/dev/null
COOK="$REPO/src/Blix.Tools.Cook/bin/Debug/net8.0/Blix.Tools.Cook.dll"
cp "$HERE/bistro.blixcook" "$SRC/bistro.blixcook"
dotnet "$COOK" project "$SRC/bistro.blixcook" --out "$COOKED"
# The exterior's sky (Poly Haven's san_giuseppe_bridge, shipped in the pack), as its .pyscene names it.
dotnet "$COOK" probe "$SRC/Bistro_v5_2/san_giuseppe_bridge_4k.hdr" --env-face=1024 --out "$COOKED"
