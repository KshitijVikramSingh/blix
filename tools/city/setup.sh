#!/usr/bin/env bash
# The scale scene: `blix city` writes a seeded glTF city, and the cook reads it like any other source.
#
#   BLIX_CITY_ASSETS  the COOKED tree (the scene host reads <it>/city). Required: no default.
#   BLIX_CITY_SRC     the generated glTF, defaulting to "<cooked>-src".
#
# Usage: tools/city/setup.sh [blix city flags...]    e.g. --blocks 32 --seed 7
set -euo pipefail
REPO="$(cd "$(dirname "$0")/../.." && pwd)"
if [[ -z "${BLIX_CITY_ASSETS:-}" ]]; then
    echo "city: BLIX_CITY_ASSETS is not set; it names the COOKED tree." >&2
    exit 1
fi
COOKED="$BLIX_CITY_ASSETS"
SRC="${BLIX_CITY_SRC:-${COOKED%/}-src}"
"$REPO/blix" run --build city "$@" --out "$SRC/city.gltf"
dotnet build "$REPO/src/Blix.Tools.Cook" -v quiet --nologo >/dev/null
rm -rf "$COOKED/city"
dotnet "$REPO/src/Blix.Tools.Cook/bin/Debug/net8.0/Blix.Tools.Cook.dll" asset "$SRC/city.gltf" --out "$COOKED/city"
