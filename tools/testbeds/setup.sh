#!/usr/bin/env bash
# The correctness scenes: `blix cornell` (and later siblings) write glTF, and the cook reads them like any other source.
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
"$REPO/blix" run --build cornell --out "$SRC/cornell/cornell.gltf"
dotnet build "$REPO/src/Blix.Tools.Cook" -v quiet --nologo >/dev/null
rm -rf "$COOKED/cornell"
dotnet "$REPO/src/Blix.Tools.Cook/bin/Debug/net8.0/Blix.Tools.Cook.dll" asset "$SRC/cornell/cornell.gltf" --out "$COOKED/cornell"
