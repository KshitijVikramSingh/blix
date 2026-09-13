#!/usr/bin/env bash
# Turn a rigged character plus a folder of single-animation files into one .glb the
# game can load, then report what bound.
#
#   tools/cook-character.sh art/villager villager_animated
#
# expects:
#   art/villager/base.fbx      the rigged character (or base.glb)
#   art/villager/clips/*.fbx   one animation per file, named as you want the clip named
#
# writes:
#   src/RTSGame/Assets/models/<name>.glb
#
# Clip names are the file names. What the game does with them is data, not code:
# src/RTSGame/Rendering/CharacterClips.cs lists the names each on-screen action will
# accept, so "Walking.fbx" and "Walk.fbx" both bind to BodyAction.Walk. Rename a file
# or add a name there — either works, and neither is a code change to the renderer.
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "$0")/.." && pwd)"
SOURCE_DIR="${1:-}"
NAME="${2:-villager_animated}"

if [ -z "$SOURCE_DIR" ]; then
    echo "usage: tools/cook-character.sh <source-dir> [output-name]" >&2
    echo "  <source-dir>/base.fbx and <source-dir>/clips/*.fbx" >&2
    exit 2
fi

BLENDER="${BLENDER:-}"
if [ -z "$BLENDER" ]; then
    for candidate in \
        /Applications/Blender.app/Contents/MacOS/Blender \
        "$(command -v blender 2>/dev/null || true)"
    do
        if [ -n "$candidate" ] && [ -x "$candidate" ]; then BLENDER="$candidate"; break; fi
    done
fi

if [ -z "$BLENDER" ]; then
    cat >&2 <<'MSG'
Blender not found, and this pipeline needs it.

  brew install --cask blender

Why it is required rather than a lighter converter: this has two jobs, not one.
Mixamo exports FBX, one file per animation, and the importer wants ONE file with one
skin and every clip on it — so something has to import N rigs, keep one, and carry N
actions across. FBX2glTF and gltf-transform each do half of that. Blender also
retargets, which is what using a CC0 animation library authored on another skeleton
would need.

Set BLENDER=/path/to/blender to override the search.
MSG
    exit 1
fi

BASE=""
for ext in fbx glb gltf; do
    if [ -f "$SOURCE_DIR/base.$ext" ]; then BASE="$SOURCE_DIR/base.$ext"; break; fi
done
if [ -z "$BASE" ]; then
    echo "no base.fbx / base.glb / base.gltf under '$SOURCE_DIR'" >&2
    exit 1
fi

OUT="$REPO_ROOT/src/RTSGame/Assets/models/$NAME.glb"
echo "Blender: $BLENDER"
echo "base:    $BASE"
echo "clips:   $SOURCE_DIR/clips"
echo "out:     $OUT"
echo

"$BLENDER" --background --python "$REPO_ROOT/tools/character_merge.py" -- \
    --base "$BASE" \
    --clips "$SOURCE_DIR/clips" \
    --out "$OUT" \
    "${@:3}"

echo
echo "--- what the importer sees ---"
dotnet run --project "$REPO_ROOT/src/Blix.Tools.Cook/Blix.Tools.Cook.csproj" -c Release -v:q \
    -- inspect "$OUT" | head -6
echo
echo "Now boot it — the game prints which clip bound to which action, and what is missing:"
echo "  tools/run-rts-game.sh --release --village   # look for 'bodies: clips bound'"
