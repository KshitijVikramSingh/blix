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
# A full template, not `mktemp -t <prefix>`. BSD mktemp treats the argument as a prefix and
# adds its own randomness; GNU mktemp -- which is what Git Bash ships, and therefore what a
# Windows CI runner uses -- reads it as the template itself and refuses it with "too few X's in
# template". Naming the directory and the X's works the same way on both.
PLAN="$(mktemp "${TMPDIR:-/tmp}/gltf-corpus-plan.XXXXXX")"
CONF="$(mktemp "${TMPDIR:-/tmp}/gltf-corpus-conf.XXXXXX")"
trap 'rm -f "$PLAN" "$CONF"' EXIT

# Records a download rather than performing one. Already-present files are dropped here, so
# re-running is cheap and a partial run resumes.
get() {
    local url="$1" out="$2"
    if [ -s "$out" ]; then return 0; fi
    printf '%s\t%s\n' "$url" "$out" >> "$PLAN"
}

while read -r line; do
    # Belt as well as braces. .gitattributes pins this file to LF, which is the fix; this is
    # here because a manifest can also be edited on Windows, pasted, or generated, and a
    # parser that silently builds broken URLs from an invisible byte is a bad way to find out.
    line="${line%$'\r'}"
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
    # Under Git Bash the curl on PATH is a NATIVE Windows binary. MSYS rewrites POSIX paths in
    # command-line ARGUMENTS, and does not touch paths inside a -K config file, so an output of
    # /d/a/blix/... is read as \d\a\blix\... on the current drive and every write fails. `-sf`
    # then says nothing, which is how this cost a CI round: correct URLs, zero files.
    # cygpath -m gives D:/a/blix/..., which curl accepts and which needs no escaping. One call,
    # used as a prefix, because 207 process spawns to learn the same answer is silly.
    dest_for_curl="$DEST"
    if command -v cygpath >/dev/null 2>&1; then dest_for_curl="$(cygpath -m "$DEST")"; fi

    while IFS="$(printf '\t')" read -r url out; do
        mkdir -p "$(dirname "$out")"
        printf 'url = "%s"\noutput = "%s"\n' "$url" "$dest_for_curl${out#"$DEST"}" >> "$CONF"
    done < "$PLAN"
    # --parallel reuses connections and overlaps requests; -f so a 404 is a failure rather
    # than an HTML error page written to disk under the name of a glTF file.
    conf_for_curl="$CONF"
    if command -v cygpath >/dev/null 2>&1; then conf_for_curl="$(cygpath -m "$CONF")"; fi
    curl -sfL --parallel --parallel-max 8 --max-time 300 -K "$conf_for_curl" || true
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

# RiggedSimple carries one skinned cylinder and nothing else, so no rig in the corpus has a static
# part or an attachment whose material is anything but opaque. This adds quads that differ only in
# alpha, so a reader that ignores alpha on them draws all of them solid:
#   static  mask_kept     MASK, alpha 0.7 against a 0.5 cutoff — must survive
#   static  mask_dropped  MASK, alpha 0.3 — must be discarded, and cast nothing
#   static  blended       BLEND, alpha 0.5 — must be translucent, and cast nothing
#   joint   attach_kept / attach_dropped — the same pair riding the bone (shot --attach both)
# One quad kept beside one dropped is what separates "the cutout works" from "the part was not drawn".
RS="$DEST/sample-assets/RiggedSimple/RiggedSimple.glb"
if [ -f "$RS" ] && [ ! -s "$DEST/sample-assets/RiggedSimple/RiggedSimple_cutout.gltf" ]; then
    python3 - "$RS" <<'PY'
import base64, json, struct, sys
p = sys.argv[1]
raw = open(p, "rb").read()
json_len = struct.unpack("<I", raw[12:16])[0]
d = json.loads(raw[20:20 + json_len])
bin_at = 20 + json_len
bin_len = struct.unpack("<I", raw[bin_at:bin_at + 4])[0]
blob = bytearray(raw[bin_at + 8:bin_at + 8 + bin_len])
if len(d.get("skins", [])) != 1 or len(d.get("buffers", [])) != 1:
    sys.exit("RiggedSimple changed shape upstream — one skin and one buffer expected")

def view(data, target):
    while len(blob) % 4: blob.append(0)
    d["bufferViews"].append({"buffer": 0, "byteOffset": len(blob), "byteLength": len(data), "target": target})
    blob.extend(data)
    return len(d["bufferViews"]) - 1

# One quad, 2 x 3 in its node's XY plane, facing +Z; shared by every node that places it.
pos = [(-1, 0, 0), (1, 0, 0), (1, 3, 0), (-1, 3, 0)]
positions = view(b"".join(struct.pack("<3f", *v) for v in pos), 34962)
normals = view(struct.pack("<3f", 0, 0, 1) * 4, 34962)
indices = view(struct.pack("<6H", 0, 1, 2, 0, 2, 3), 34963)
d["accessors"] += [
    {"bufferView": positions, "componentType": 5126, "count": 4, "type": "VEC3", "min": [-1, 0, 0], "max": [1, 3, 0]},
    {"bufferView": normals, "componentType": 5126, "count": 4, "type": "VEC3"},
    {"bufferView": indices, "componentType": 5123, "count": 6, "type": "SCALAR"},
]
pa, na, ia = len(d["accessors"]) - 3, len(d["accessors"]) - 2, len(d["accessors"]) - 1

def material(name, mode, alpha, colour):
    m = {"name": name, "alphaMode": mode, "doubleSided": True,
         "pbrMetallicRoughness": {"baseColorFactor": [*colour, alpha], "metallicFactor": 0.0}}
    if mode == "MASK": m["alphaCutoff"] = 0.5
    d["materials"].append(m)
    d["meshes"].append({"name": name, "primitives": [{"attributes": {"POSITION": pa, "NORMAL": na}, "indices": ia, "material": len(d["materials"]) - 1}]})
    return len(d["meshes"]) - 1

def node(name, mesh, translation, parent=None):
    d["nodes"].append({"name": name, "mesh": mesh, "translation": translation})
    at = len(d["nodes"]) - 1
    if parent is None: d["scenes"][0]["nodes"].append(at)
    else: d["nodes"][parent].setdefault("children", []).append(at)

node("mask_kept", material("mask_kept", "MASK", 0.7, (0.9, 0.2, 0.2)), [3, 0, 0])
node("mask_dropped", material("mask_dropped", "MASK", 0.3, (0.2, 0.2, 0.9)), [5.5, 0, 0])
node("blended", material("blended", "BLEND", 0.5, (0.9, 0.8, 0.1)), [8, 0, 0])
joint = d["skins"][0]["joints"][0]
node("attach_kept", material("attach_kept", "MASK", 0.7, (0.9, 0.2, 0.9)), [0, 0, 2], joint)
node("attach_dropped", material("attach_dropped", "MASK", 0.3, (0.2, 0.9, 0.9)), [0, 0, -2], joint)

d["buffers"] = [{"byteLength": len(blob), "uri": "data:application/octet-stream;base64," + base64.b64encode(bytes(blob)).decode()}]
out = p.replace("RiggedSimple.glb", "RiggedSimple_cutout.gltf")
json.dump(d, open(out, "w"), indent=1)
print("  derived RiggedSimple_cutout.gltf (3 static quads, 2 attachments, differing only in alpha)")
PY
fi

echo
echo "corpus at $DEST — $((planned - failed)) fetched, $failed missing, $(find "$DEST" -type f | wc -l | tr -d ' ') file(s) total"
[ "$failed" -eq 0 ]
