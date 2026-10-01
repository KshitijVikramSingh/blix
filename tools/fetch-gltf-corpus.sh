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

# A URL as ASCII: every byte outside printable ASCII percent-encoded (the UTF-8 bytes, as RFC 3986
# says), everything else untouched. Byte-wise under LC_ALL=C, so it works in macOS's bash 3.2 too.
ascii_url() {
    local in="$1" outurl="" c i
    local LC_ALL=C
    for ((i = 0; i < ${#in}; i++)); do
        c="${in:i:1}"
        case "$c" in
        [\ -~]) outurl="$outurl$c" ;;
        # printf reads a high byte as a signed char; the mask makes it the byte.
        *) outurl="$outurl$(printf '%%%02X' $(( $(printf '%d' "'$c") & 255 )))" ;;
        esac
    done
    printf '%s' "$outurl"
}

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

    # Names outside ASCII (Unicode❤♻Test) cost a Windows CI round: the native curl reads a -K file's
    # paths in the ANSI code page, so the download landed under a mangled name and the checklist below
    # found nothing. So curl only ever sees ASCII: the URL percent-encoded (RFC 3986, UTF-8 bytes) and
    # every file written to a numbered name in a staging folder, then moved into place by this shell,
    # which handles Unicode names on every platform.
    staging="$DEST/.downloading"
    rm -rf "$staging"
    mkdir -p "$staging"
    n=0
    while IFS="$(printf '\t')" read -r url out; do
        mkdir -p "$(dirname "$out")"
        n=$((n + 1))
        printf 'url = "%s"\noutput = "%s"\n' "$(ascii_url "$url")" "$dest_for_curl/.downloading/$n" >> "$CONF"
    done < "$PLAN"
    # --parallel reuses connections and overlaps requests; -f so a 404 is a failure rather
    # than an HTML error page written to disk under the name of a glTF file.
    conf_for_curl="$CONF"
    if command -v cygpath >/dev/null 2>&1; then conf_for_curl="$(cygpath -m "$CONF")"; fi
    curl -sfL --parallel --parallel-max 8 --max-time 300 -K "$conf_for_curl" || true

    n=0
    while IFS="$(printf '\t')" read -r url out; do
        n=$((n + 1))
        if [ -s "$staging/$n" ]; then mv -f "$staging/$n" "$out"; fi
    done < "$PLAN"
    rm -rf "$staging"
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

# Generated tangents use "the texture coordinates associated with the normal texture" (glTF 2.0
# §3.7.2.1) — its texCoord, not set 0. No corpus file generates a frame over set 1, so this one does:
# NormalTangentMirrorTest with its authored TANGENT dropped, a TEXCOORD_1 that is TEXCOORD_0 turned a
# quarter (u1 = v0, v1 = 1 - u0), and the normal texture pointed at it. A frame built over the wrong
# set is then 90 degrees out, which no tolerance hides. Written as a self-contained .gltf (the glb's
# binary chunk becomes buffer 0, the new set buffer 1, both data URIs).
NT="$DEST/sample-assets/NormalTangentMirrorTest/NormalTangentMirrorTest.glb"
if [ -f "$NT" ] && [ ! -s "$DEST/sample-assets/NormalTangentMirrorTest/NormalTangentMirrorTest_normaluv1.gltf" ]; then
    python3 - "$NT" <<'PY'
import base64, json, struct, sys
p = sys.argv[1]
b = open(p, "rb").read()
jlen = struct.unpack("<I", b[12:16])[0]
d = json.loads(b[20:20 + jlen])
bin_at = 20 + jlen
blen = struct.unpack("<I", b[bin_at:bin_at + 4])[0]
blob = b[bin_at + 8:bin_at + 8 + blen]
d["buffers"][0] = {"byteLength": len(blob), "uri": "data:application/octet-stream;base64," + base64.b64encode(blob).decode()}
uv1 = bytearray()
turned = 0
for mesh in d["meshes"]:
    for prim in mesh["primitives"]:
        a = prim["attributes"]
        if "TANGENT" not in a or "TEXCOORD_0" not in a:
            continue
        del a["TANGENT"]
        acc = d["accessors"][a["TEXCOORD_0"]]
        if acc["componentType"] != 5126 or acc.get("sparse"):
            sys.exit("TEXCOORD_0 is not plain float — upstream changed shape")
        view = d["bufferViews"][acc["bufferView"]]
        stride = view.get("byteStride", 8)
        base = view.get("byteOffset", 0) + acc.get("byteOffset", 0)
        start = len(uv1)
        for k in range(acc["count"]):
            u, v = struct.unpack_from("<2f", blob, base + k * stride)
            uv1 += struct.pack("<2f", v, 1.0 - u)
        d["bufferViews"].append({"buffer": 1, "byteOffset": start, "byteLength": len(uv1) - start})
        d["accessors"].append({"bufferView": len(d["bufferViews"]) - 1, "componentType": 5126, "count": acc["count"], "type": "VEC2"})
        a["TEXCOORD_1"] = len(d["accessors"]) - 1
        turned += 1
if turned == 0:
    sys.exit("NormalTangentMirrorTest has no tangent-bearing primitive to rework — upstream changed shape")
d["buffers"].append({"byteLength": len(uv1), "uri": "data:application/octet-stream;base64," + base64.b64encode(bytes(uv1)).decode()})
for m in d["materials"]:
    if "normalTexture" in m:
        m["normalTexture"]["texCoord"] = 1
out = p.replace("NormalTangentMirrorTest.glb", "NormalTangentMirrorTest_normaluv1.gltf")
json.dump(d, open(out, "w"))
print(f"  derived NormalTangentMirrorTest_normaluv1.gltf ({turned} primitive(s): no TANGENT, normal map over a quarter-turned TEXCOORD_1)")
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

# No rig in the corpus has more than 128 bones — Studio's old palette cap — so this makes one whose
# vertices are weighted by joints past it: 300 inert joints go in front of RiggedSimple's two, and its
# JOINTS_0 is rewritten (+300, as unsigned shorts). A correct palette draws it exactly as RiggedSimple;
# a capped one refuses it, and one that truncated an index would collapse the cylinder.
RB="$DEST/sample-assets/RiggedSimple/RiggedSimple.glb"
if [ -f "$RB" ] && [ ! -s "$DEST/sample-assets/RiggedSimple/RiggedSimple_bones300.gltf" ]; then
    python3 - "$RB" <<'PY'
import base64, json, struct, sys
p = sys.argv[1]
raw = open(p, "rb").read()
json_len = struct.unpack("<I", raw[12:16])[0]
d = json.loads(raw[20:20 + json_len])
bin_at = 20 + json_len
blob = bytearray(raw[bin_at + 8:bin_at + 8 + struct.unpack("<I", raw[bin_at:bin_at + 4])[0]])
EXTRA = 300
skin = d["skins"][0]
if len(d.get("skins", [])) != 1 or len(skin["joints"]) != 2:
    sys.exit("RiggedSimple changed shape upstream — one skin of two joints expected")

def view(data, target=None):
    while len(blob) % 4: blob.append(0)
    v = {"buffer": 0, "byteOffset": len(blob), "byteLength": len(data)}
    if target: v["target"] = target
    d["bufferViews"].append(v)
    blob.extend(data)
    return len(d["bufferViews"]) - 1

def read(accessor):
    a = d["accessors"][accessor]
    bv = d["bufferViews"][a["bufferView"]]
    n = {"SCALAR": 1, "VEC4": 4, "MAT4": 16}[a["type"]]
    fmt = {5126: "f", 5123: "H", 5121: "B"}[a["componentType"]]
    size = struct.calcsize(fmt) * n
    stride = bv.get("byteStride", size)
    at = bv.get("byteOffset", 0) + a.get("byteOffset", 0)
    return [struct.unpack_from("<" + fmt * n, blob, at + k * stride) for k in range(a["count"])]

# The inert joints: identity nodes beside the root joint, so every root hangs from the same node.
root_parent = next(i for i, n in enumerate(d["nodes"]) if skin["joints"][0] in n.get("children", []))
first = len(d["nodes"])
for k in range(EXTRA):
    d["nodes"].append({"name": f"inert_{k}"})
d["nodes"][root_parent]["children"] += list(range(first, first + EXTRA))
skin["joints"] = list(range(first, first + EXTRA)) + skin["joints"]

ibm = read(skin["inverseBindMatrices"])
identity = (1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1)
ibm_view = view(b"".join(struct.pack("<16f", *m) for m in [identity] * EXTRA + ibm))
d["accessors"].append({"bufferView": ibm_view, "componentType": 5126, "count": EXTRA + len(ibm), "type": "MAT4"})
skin["inverseBindMatrices"] = len(d["accessors"]) - 1

for mesh in d["meshes"]:
    for prim in mesh["primitives"]:
        joints = read(prim["attributes"]["JOINTS_0"])
        v = view(b"".join(struct.pack("<4H", *(j + EXTRA for j in q)) for q in joints), 34962)
        d["accessors"].append({"bufferView": v, "componentType": 5123, "count": len(joints), "type": "VEC4"})
        prim["attributes"]["JOINTS_0"] = len(d["accessors"]) - 1

# Animation channels target nodes, which did not move; they stay valid as they are.
d["buffers"] = [{"byteLength": len(blob), "uri": "data:application/octet-stream;base64," + base64.b64encode(bytes(blob)).decode()}]
out = p.replace("RiggedSimple.glb", "RiggedSimple_bones300.gltf")
json.dump(d, open(out, "w"), indent=1)
print(f"  derived RiggedSimple_bones300.gltf ({EXTRA + 2} joints, vertices on joints {EXTRA} and {EXTRA + 1})")
PY
fi

# No corpus file animates a node that is not a joint but sits between joints or above them with a CHANGING
# value (BrainStem's three such tracks hold still), so nothing proved the animated hierarchy follows one.
# This puts a translation track on RiggedSimple's Armature (above the skeleton root) and inserts a non-joint
# "Spacer" between its two joints with a rotation track. glTF allows both, and both must move the skin.
RA="$DEST/sample-assets/RiggedSimple/RiggedSimple.glb"
if [ -f "$RA" ] && [ ! -s "$DEST/sample-assets/RiggedSimple/RiggedSimple_animatednodes.gltf" ]; then
    python3 - "$RA" <<'PY'
import base64, json, math, struct, sys
p = sys.argv[1]
raw = open(p, "rb").read()
json_len = struct.unpack("<I", raw[12:16])[0]
d = json.loads(raw[20:20 + json_len])
bin_at = 20 + json_len
blob = bytearray(raw[bin_at + 8:bin_at + 8 + struct.unpack("<I", raw[bin_at:bin_at + 4])[0]])
joints = d["skins"][0]["joints"]
names = [n.get("name") for n in d["nodes"]]
if names.count("Armature") != 1 or len(joints) != 2:
    sys.exit("RiggedSimple changed shape upstream — an Armature node and two joints expected")
armature, root, child = names.index("Armature"), joints[0], joints[1]
if child not in d["nodes"][root].get("children", []):
    sys.exit("RiggedSimple changed shape upstream — the second joint should be a child of the first")

# The Spacer: between the joints, identity at rest, so the rest pose is unchanged.
spacer = len(d["nodes"])
d["nodes"].append({"name": "Spacer", "children": [child]})
d["nodes"][root]["children"] = [spacer if c == child else c for c in d["nodes"][root]["children"]]

def view(data):
    while len(blob) % 4: blob.append(0)
    d["bufferViews"].append({"buffer": 0, "byteOffset": len(blob), "byteLength": len(data)})
    blob.extend(data)
    return len(d["bufferViews"]) - 1

def accessor(data, count, kind, lo=None, hi=None):
    a = {"bufferView": view(data), "componentType": 5126, "count": count, "type": kind}
    if lo is not None: a["min"], a["max"] = lo, hi
    d["accessors"].append(a)
    return len(d["accessors"]) - 1

times = [0.0, 0.5, 1.0, 1.5, 2.0]
t = accessor(struct.pack("<5f", *times), 5, "SCALAR", [0.0], [2.0])
moves = accessor(b"".join(struct.pack("<3f", x, 0.0, 0.0) for x in [0.0, 1.0, 0.0, -1.0, 0.0]), 5, "VEC3")
turns = accessor(b"".join(struct.pack("<4f", 0.0, 0.0, math.sin(a / 2), math.cos(a / 2))
                          for a in [0.0, 0.6, 0.0, -0.6, 0.0]), 5, "VEC4")
d.setdefault("animations", []).append({
    "name": "nodes",
    "samplers": [{"input": t, "output": moves}, {"input": t, "output": turns}],
    "channels": [{"sampler": 0, "target": {"node": armature, "path": "translation"}},
                 {"sampler": 1, "target": {"node": spacer, "path": "rotation"}}],
})
d["buffers"] = [{"byteLength": len(blob), "uri": "data:application/octet-stream;base64," + base64.b64encode(bytes(blob)).decode()}]
out = p.replace("RiggedSimple.glb", "RiggedSimple_animatednodes.gltf")
json.dump(d, open(out, "w"), indent=1)
print("  derived RiggedSimple_animatednodes.gltf (Armature translated, a non-joint Spacer rotated between the joints)")
PY
fi

# A mirrored skin: RiggedSimple under a new scene root scaled (-1, 1, 1). Mirroring reverses winding, so a
# renderer that culls back faces must flip its front face for the skin as it does for a rigid part. The
# cylinder is symmetric under this mirror, so at rest it must draw as the unmirrored one does.
RM="$DEST/sample-assets/RiggedSimple/RiggedSimple.glb"
if [ -f "$RM" ] && [ ! -s "$DEST/sample-assets/RiggedSimple/RiggedSimple_mirrored.gltf" ]; then
    python3 - "$RM" <<'PY'
import base64, json, struct, sys
p = sys.argv[1]
raw = open(p, "rb").read()
json_len = struct.unpack("<I", raw[12:16])[0]
d = json.loads(raw[20:20 + json_len])
bin_at = 20 + json_len
blob = raw[bin_at + 8:bin_at + 8 + struct.unpack("<I", raw[bin_at:bin_at + 4])[0]]
scene = d["scenes"][d.get("scene", 0)]
mirror = len(d["nodes"])
d["nodes"].append({"name": "Mirror", "scale": [-1.0, 1.0, 1.0], "children": list(scene["nodes"])})
scene["nodes"] = [mirror]
d["buffers"] = [{"byteLength": len(blob), "uri": "data:application/octet-stream;base64," + base64.b64encode(bytes(blob)).decode()}]
out = p.replace("RiggedSimple.glb", "RiggedSimple_mirrored.gltf")
json.dump(d, open(out, "w"), indent=1)
print("  derived RiggedSimple_mirrored.gltf (the whole scene under a (-1, 1, 1) scale)")
PY
fi

# glTF 2.0 §5.27: an inverseBindMatrices accessor MUST have AT LEAST one element per joint; the joints
# consume the first n in order and the rest are legal and unread. No corpus file has spare ones, so
# RiggedSimple gets a third, a 100 m translation: a reader that demands exactly n refuses the file, and
# one that takes the wrong n draws the skin a hundred metres away.
RI="$DEST/sample-assets/RiggedSimple/RiggedSimple.glb"
if [ -f "$RI" ] && [ ! -s "$DEST/sample-assets/RiggedSimple/RiggedSimple_extraibm.gltf" ]; then
    python3 - "$RI" <<'PY'
import base64, json, struct, sys
p = sys.argv[1]
raw = open(p, "rb").read()
json_len = struct.unpack("<I", raw[12:16])[0]
d = json.loads(raw[20:20 + json_len])
bin_at = 20 + json_len
blob = raw[bin_at + 8:bin_at + 8 + struct.unpack("<I", raw[bin_at:bin_at + 4])[0]]
skin = d["skins"][0]
acc = d["accessors"][skin["inverseBindMatrices"]]
view = d["bufferViews"][acc["bufferView"]]
start = view.get("byteOffset", 0) + acc.get("byteOffset", 0)
ibms = bytearray(blob[start:start + 64 * acc["count"]])
ibms += struct.pack("<16f", 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 100, 0, 0, 1)
d["buffers"] = [
    {"byteLength": len(blob), "uri": "data:application/octet-stream;base64," + base64.b64encode(bytes(blob)).decode()},
    {"byteLength": len(ibms), "uri": "data:application/octet-stream;base64," + base64.b64encode(bytes(ibms)).decode()},
]
d["bufferViews"].append({"buffer": 1, "byteOffset": 0, "byteLength": len(ibms)})
d["accessors"].append({"bufferView": len(d["bufferViews"]) - 1, "componentType": 5126, "count": acc["count"] + 1, "type": "MAT4"})
skin["inverseBindMatrices"] = len(d["accessors"]) - 1
out = p.replace("RiggedSimple.glb", "RiggedSimple_extraibm.gltf")
json.dump(d, open(out, "w"), indent=1)
print(f"  derived RiggedSimple_extraibm.gltf ({acc['count'] + 1} inverse binds for {len(skin['joints'])} joints)")
PY
fi

# KHR_mesh_quantization is accepted (MeshRecipe.ReadExtensions) and no corpus file uses it, so the claim
# had no instrument. NormalTangentTest re-encoded the way an optimiser writes it: POSITION as unsigned
# shorts with the dequantisation (per-axis scale, then offset) on the mesh's node, NORMAL as normalized
# bytes, TEXCOORD_0 as normalized unsigned shorts, each padded to a 4-byte stride. The extension is
# REQUIRED, as it must be: a reader that ignores it would draw the raw integers. The scale is uniform,
# because the node transform also reaches the normals.
NQ="$DEST/sample-assets/NormalTangentTest/NormalTangentTest.glb"
if [ -f "$NQ" ] && [ ! -s "$DEST/sample-assets/NormalTangentTest/NormalTangentTest_quantized.gltf" ]; then
    python3 - "$NQ" <<'PY'
import base64, json, struct, sys
p = sys.argv[1]
b = open(p, "rb").read()
jlen = struct.unpack("<I", b[12:16])[0]
d = json.loads(b[20:20 + jlen])
bin_at = 20 + jlen
blen = struct.unpack("<I", b[bin_at:bin_at + 4])[0]
blob = b[bin_at + 8:bin_at + 8 + blen]
d["buffers"][0] = {"byteLength": len(blob), "uri": "data:application/octet-stream;base64," + base64.b64encode(blob).decode()}
if len(d["meshes"]) != 1 or len(d["meshes"][0]["primitives"]) != 1:
    sys.exit("NormalTangentTest is not one mesh of one primitive — upstream changed shape")
users = [n for n in d["nodes"] if n.get("mesh") == 0]
if len(users) != 1 or any(k in users[0] for k in ("matrix", "translation", "rotation", "scale")):
    sys.exit("NormalTangentTest's mesh node is not a single untransformed node — upstream changed shape")
prim = d["meshes"][0]["primitives"][0]
def floats(key, width):
    acc = d["accessors"][prim["attributes"][key]]
    if acc["componentType"] != 5126 or acc.get("sparse"):
        sys.exit(f"{key} is not plain float — upstream changed shape")
    view = d["bufferViews"][acc["bufferView"]]
    stride = view.get("byteStride", 4 * width)
    base = view.get("byteOffset", 0) + acc.get("byteOffset", 0)
    return [struct.unpack_from(f"<{width}f", blob, base + k * stride) for k in range(acc["count"])]
pos, nrm, uv = floats("POSITION", 3), floats("NORMAL", 3), floats("TEXCOORD_0", 2)
if min(min(t) for t in uv) < 0 or max(max(t) for t in uv) > 1:
    sys.exit("TEXCOORD_0 leaves [0, 1]; normalized shorts cannot hold it — upstream changed shape")
lo = [min(v[i] for v in pos) for i in range(3)]
hi = [max(v[i] for v in pos) for i in range(3)]
# ONE scale for all three axes: the node transform reaches the normals too (through its inverse transpose),
# so a per-axis scale would bend every normal; a uniform one leaves their directions alone.
step = max(hi[i] - lo[i] for i in range(3)) / 65535.0
scale = [step, step, step]
out = bytearray()
def view_of(data, stride):
    while len(out) % 4: out.append(0)
    start = len(out)
    out.extend(data)
    d["bufferViews"].append({"buffer": 1, "byteOffset": start, "byteLength": len(data), "byteStride": stride, "target": 34962})
    return len(d["bufferViews"]) - 1
qp = [tuple(max(0, min(65535, round((v[i] - lo[i]) / scale[i]))) for i in range(3)) for v in pos]
data = b"".join(struct.pack("<3H2x", *q) for q in qp)
d["accessors"].append({"bufferView": view_of(data, 8), "componentType": 5123, "count": len(qp), "type": "VEC3",
                       "min": [min(q[i] for q in qp) for i in range(3)], "max": [max(q[i] for q in qp) for i in range(3)]})
prim["attributes"]["POSITION"] = len(d["accessors"]) - 1
data = b"".join(struct.pack("<3bx", *(max(-127, min(127, round(c * 127))) for c in v)) for v in nrm)
d["accessors"].append({"bufferView": view_of(data, 4), "componentType": 5120, "normalized": True, "count": len(nrm), "type": "VEC3"})
prim["attributes"]["NORMAL"] = len(d["accessors"]) - 1
data = b"".join(struct.pack("<2H", *(max(0, min(65535, round(c * 65535))) for c in v)) for v in uv)
d["accessors"].append({"bufferView": view_of(data, 4), "componentType": 5123, "normalized": True, "count": len(uv), "type": "VEC2"})
prim["attributes"]["TEXCOORD_0"] = len(d["accessors"]) - 1
users[0]["translation"] = lo
users[0]["scale"] = scale
d["buffers"].append({"byteLength": len(out), "uri": "data:application/octet-stream;base64," + base64.b64encode(bytes(out)).decode()})
for key in ("extensionsUsed", "extensionsRequired"):
    d[key] = sorted(set(d.get(key, [])) | {"KHR_mesh_quantization"})
dst = p.replace("NormalTangentTest.glb", "NormalTangentTest_quantized.gltf")
json.dump(d, open(dst, "w"))
print(f"  derived NormalTangentTest_quantized.gltf ({len(qp)} vertices: POSITION u16 + node dequantisation, NORMAL i8n, TEXCOORD_0 u16n)")
PY
fi

# Morph targets are refused only where they take effect: a nonzero default weight (mesh or node) or a
# weights animation. With every weight zero and nothing driving them the base mesh IS the render, so such
# a file loads, its targets recorded as unread. Every corpus morph file moves its targets, so this is the
# one that must load: SimpleMorph with its weights zeroed and its animation dropped.
SM="$DEST/sample-assets/SimpleMorph/SimpleMorph.gltf"
if [ -f "$SM" ] && [ ! -s "$DEST/sample-assets/SimpleMorph/SimpleMorph_static.gltf" ]; then
    python3 - "$SM" <<'PY'
import json, sys
p = sys.argv[1]
d = json.load(open(p))
if not any("targets" in prim for m in d["meshes"] for prim in m["primitives"]):
    sys.exit("SimpleMorph has no morph targets — upstream changed shape")
for m in d["meshes"]:
    if "weights" in m:
        m["weights"] = [0.0] * len(m["weights"])
for n in d["nodes"]:
    if "weights" in n:
        n["weights"] = [0.0] * len(n["weights"])
d.pop("animations", None)
json.dump(d, open(p.replace("SimpleMorph.gltf", "SimpleMorph_static.gltf"), "w"))
print("  derived SimpleMorph_static.gltf (morph targets kept, every weight zero, no animation)")
PY
fi

echo
echo "corpus at $DEST — $((planned - failed)) fetched, $failed missing, $(find "$DEST" -type f | wc -l | tr -d ' ') file(s) total"
[ "$failed" -eq 0 ]
