#!/usr/bin/env bash
# Fetches the glTF conformance corpus declared in third_party/gltf-corpus.manifest.
#
# The manifest is the record; this script is only the download. See its header for why
# the bytes are not committed (conventions §7, and three upstream licences).
#
#   tools/fetch-gltf-corpus.sh                 # everything the manifest declares
#   tools/fetch-gltf-corpus.sh AlphaMask       # only entries whose line matches
#
# Uses raw.githubusercontent.com and NOTHING ELSE. The GitHub contents API looks like the
# obvious way to enumerate a model directory and it is not: 60 requests an hour
# unauthenticated, which this corpus exhausts while it is still being written. Every path
# here is therefore explicit, and a rename upstream surfaces as a 404 naming the file
# rather than as a directory that silently came back empty.
#
# It PLANS every download first and then makes one parallel curl run over reused
# connections. One curl per file is the obvious shape and it costs 15 s of handshake per
# file here — about fifty minutes for this manifest, against under a minute this way.
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "$0")/.." && pwd)"
MANIFEST="$REPO_ROOT/third_party/gltf-corpus.manifest"
DEST="$REPO_ROOT/third_party/gltf-corpus"
FILTER="${1:-}"

# Sources land in SRC_<key> rather than an associative array: macOS ships bash 3.2, where
# `declare -A` is a syntax error, and every other launcher here runs on that bash.
PLAN="$(mktemp -t gltf-corpus-plan)"
CONF="$(mktemp -t gltf-corpus-conf)"
trap 'rm -f "$PLAN" "$CONF"' EXIT

# Records a download rather than performing one. Already-present files are dropped here, so
# re-running is cheap and a partial run resumes.
get() {
    local url="$1" out="$2"
    if [ -s "$out" ]; then return 0; fi
    printf '%s\t%s\n' "$url" "$out" >> "$PLAN"
}

while read -r line; do
    line="${line%%#*}"
    case "$line" in '') continue ;; esac
    # shellcheck disable=SC2086
    set -- $line
    [ "$#" -ge 2 ] || continue
    kind="$1"; name="$2"; shift 2
    a="${1:-}"; b="${2:-}"; c="${3:-}"; d="${4:-}"; e="${5:-}"; f="${6:-}"

    if [ "$kind" = source ]; then eval "SRC_$name=\$a"; continue; fi
    if [ -n "$FILTER" ]; then
        case "$(printf '%s %s' "$kind" "$name" | tr 'A-Z' 'a-z')" in
        *"$(printf '%s' "$FILTER" | tr 'A-Z' 'a-z')"*) ;;
        *) continue ;;
        esac
    fi

    echo "$kind $name"
    case "$kind" in
    sample)
        base="${SRC_SA}/Models/$name"
        # Every Sample-Assets model carries its own LICENSE.md — CC0 for some, CC-BY-4.0
        # for others. Pulling it down beside the model means the licence travels with the
        # bytes instead of being something a person is trusted to go and check.
        get "$base/LICENSE.md" "$DEST/sample-assets/$name/LICENSE.md"
        if [ -n "$a" ]; then variant="$a"; else variant="glTF-Binary"; fi
        if [ -n "$b" ]; then files="$b $c $d $e $f"; else files="$name.glb"; fi
        for fl in $files; do get "$base/$variant/$fl" "$DEST/sample-assets/$name/$fl"; done
        ;;
    generator|negative)
        [ "$kind" = generator ] && kdir=Output/Positive || kdir=Output/Negative
        lo="${a%%-*}"; hi="${a##*-}"
        for i in $(seq "$lo" "$hi"); do
            n=$(printf '%02d' "$i")
            for ext in gltf bin; do
                get "${SRC_AG}/$kdir/$name/${name}_$n.$ext" "$DEST/$kind/$name/${name}_$n.$ext"
            done
        done
        for fl in $b $c $d $e; do
            get "${SRC_AG}/$kdir/$name/$fl" "$DEST/$kind/$name/$fl"
        done
        ;;
    cesium)
        base="${SRC_CS}/Specs/Data/Models/glTF-2.0/$name/$a"
        for fl in $b $c $d $e; do get "$base/$fl" "$DEST/cesium/$name/$fl"; done
        ;;
    *) echo "  unknown manifest kind: $kind" >&2; failed=$((failed + 1)) ;;
    esac
done < "$MANIFEST"

# ── Execute the plan ───────────────────────────────────────────────────────────
planned=$(wc -l < "$PLAN" | tr -d ' ')
if [ "$planned" -gt 0 ]; then
    echo
    echo "fetching $planned file(s)..."
    while IFS="$(printf '\t')" read -r url out; do
        mkdir -p "$(dirname "$out")"
        printf 'url = "%s"\noutput = "%s"\n' "$url" "$out" >> "$CONF"
    done < "$PLAN"
    # --parallel reuses connections and overlaps requests; -f so a 404 is a failure rather
    # than an HTML error page written to disk under the name of a glTF file.
    curl -sfL --parallel --parallel-max 8 --max-time 300 -K "$CONF" || true
fi

# A curl that returns non-zero does not say WHICH file, and --parallel returns one code for
# the batch. The plan is the checklist, so verify against it.
failed=0
while IFS="$(printf '\t')" read -r url out; do
    if [ ! -s "$out" ]; then
        echo "  MISSING $url" >&2
        rm -f "$out"
        failed=$((failed + 1))
    fi
done < "$PLAN"

# ── Locally derived assets ─────────────────────────────────────────────────────
# Expressed as the EDIT, not as committed bytes: a derived asset kept as a blob is a
# fork nobody can re-derive once upstream moves.
#
# MultiUVTest ships a second UV set and no material that names it, so the stock asset
# renders identically whether a reader honours texCoord or always samples set 0. One key
# of difference makes it discriminating.
UV="$DEST/sample-assets/MultiUVTest/MultiUVTest.gltf"
if [ -f "$UV" ] && [ ! -s "$DEST/sample-assets/MultiUVTest/MultiUVTest_uv1.gltf" ]; then
    python3 - "$UV" <<'PY'
import json, sys
p = sys.argv[1]
d = json.load(open(p))
n = 0
for m in d.get("materials", []):
    t = m.get("pbrMetallicRoughness", {}).get("baseColorTexture")
    if t is not None:
        t["texCoord"] = 1
        n += 1
if n == 0:
    sys.exit("MultiUVTest has no baseColorTexture to repoint — upstream changed shape")
out = p.replace("MultiUVTest.gltf", "MultiUVTest_uv1.gltf")
json.dump(d, open(out, "w"), indent=2)
print(f"  derived MultiUVTest_uv1.gltf ({n} material(s) repointed to texCoord 1)")
PY
fi

echo
echo "corpus at $DEST — $((planned - failed)) fetched, $failed missing, $(find "$DEST" -type f | wc -l | tr -d ' ') file(s) total"
[ "$failed" -eq 0 ]
