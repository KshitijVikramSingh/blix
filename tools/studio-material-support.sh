#!/usr/bin/env bash
# studio-material-support.sh — which KHR_materials_* extensions Studio DRAWS, measured, not read off the code.
#
# The reader stores every extension's values on PbrMaterial (PbrMaterialExtensions). A field existing is not the
# extension being drawn, so this asks the renderer: for each extension, one corpus asset that uses it is copied,
# the extension stripped from every material (and from extensionsUsed/Required), and both shot with `blix shot`.
# Studio's renders are deterministic (a shot repeated is byte-identical), so the verdict is a byte comparison:
#
#   draws    the PNGs differ: Studio's output depends on the extension
#   ignores  the PNGs are identical
#
# And a CONTROL per asset, because "identical" is also what a material off screen would give: the same file with
# the extension's materials painted red must differ from the original, or the row is reported as unmeasured.
#
# Usage: tools/studio-material-support.sh        (needs the corpus: tools/fetch-gltf-corpus.sh)
set -u
REPO_ROOT="$(cd "$(dirname "$0")/.." && pwd)"
C="$REPO_ROOT/third_party/gltf-corpus/sample-assets"
[ -d "$C" ] || { echo "no corpus at $C — run tools/fetch-gltf-corpus.sh" >&2; exit 2; }
WORK="$(mktemp -d "${TMPDIR:-/tmp}/studio-material-support.XXXXXX")"
trap 'rm -rf "$WORK"' EXIT

# Variants of a source file: with.gltf (as authored, a .glb's binary embedded), without.gltf (the extension
# stripped), red.gltf (the extension's materials painted red: the visibility control).
derive() {
    python3 - "$1" "$2" "$3" <<'PY'
import base64, json, os, shutil, struct, sys, copy
src, ext, out = sys.argv[1], sys.argv[2], sys.argv[3]
os.makedirs(out, exist_ok=True)
for f in os.listdir(os.path.dirname(src)):
    p = os.path.join(os.path.dirname(src), f)
    if os.path.isfile(p): shutil.copy(p, out)
if src.endswith(".glb"):
    b = open(src, "rb").read(); n = struct.unpack_from("<I", b, 12)[0]; d = json.loads(b[20:20 + n])
    at = 20 + n; blen = struct.unpack_from("<I", b, at)[0]; blob = b[at + 8:at + 8 + blen]
    d["buffers"][0] = {"byteLength": len(blob), "uri": "data:application/octet-stream;base64," + base64.b64encode(blob).decode()}
else:
    d = json.load(open(src))
json.dump(d, open(f"{out}/with.gltf", "w"))
red = copy.deepcopy(d)
for m in red.get("materials", []):
    if ext in m.get("extensions", {}):
        m.setdefault("pbrMetallicRoughness", {})["baseColorFactor"] = [1, 0, 0, 1]
        m["pbrMetallicRoughness"].pop("baseColorTexture", None)
json.dump(red, open(f"{out}/red.gltf", "w"))
hit = 0
for m in d.get("materials", []):
    if ext in m.get("extensions", {}):
        del m["extensions"][ext]; hit += 1
for key in ("extensionsUsed", "extensionsRequired"):
    if key in d:
        d[key] = [e for e in d[key] if e != ext]
        if not d[key]: del d[key]
json.dump(d, open(f"{out}/without.gltf", "w"))
print(hit)
PY
}

echo "| Extension | Asset | Materials | Studio |"
echo "| --- | --- | --- | --- |"
while read -r ext file; do
    dir="$WORK/$ext"
    hit=$(derive "$C/$file" "$ext" "$dir")
    for v in with without red; do
        "$REPO_ROOT/blix" shot --model "$dir/$v.gltf" --out "$dir/$v.png" > "$dir/$v.log" 2>&1 \
            || { echo "| $ext | $file | $hit | shot failed ($v) |"; continue 2; }
    done
    if cmp -s "$dir/with.png" "$dir/red.png"; then verdict="UNMEASURED (its materials are not on screen)"
    elif cmp -s "$dir/with.png" "$dir/without.png"; then verdict="ignores"
    else verdict="draws"; fi
    echo "| \`$ext\` | $file | $hit | $verdict |"
done <<'LIST'
KHR_materials_emissive_strength CompareEmissiveStrength/CompareEmissiveStrength.glb
KHR_materials_transmission CompareTransmission/CompareTransmission.glb
KHR_materials_volume CompareVolume/CompareVolume.glb
KHR_materials_ior CompareIor/CompareIor.glb
KHR_materials_clearcoat ClearCoatTest/ClearCoatTest.glb
KHR_materials_sheen CompareSheen/CompareSheen.glb
KHR_materials_specular CompareSpecular/CompareSpecular.glb
KHR_materials_iridescence CompareIridescence/CompareIridescence.glb
KHR_materials_anisotropy CompareAnisotropy/CompareAnisotropy.glb
KHR_materials_diffuse_transmission DiffuseTransmissionTeacup/DiffuseTransmissionTeacup.glb
KHR_materials_dispersion CompareDispersion/CompareDispersion.glb
KHR_materials_unlit UnlitTest/UnlitTest.glb
LIST
