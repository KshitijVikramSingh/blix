#!/usr/bin/env python3
"""Habel, Kusternig and Wimmer's measured leaves (TU Wien, 2007) as glTF cards: a foliage bench with real translucency colour and a real cut-out.

The zip ("Physically Based Real-Time Translucency for Leaves", Leaf textures) has three leaves, each with a front albedo whose alpha is the leaf's outline, a front translucency
colour (`*_front_t`: the colour of the light that comes through), a back albedo and a thickness map. The page states no licence, so setup.sh downloads it into the testbed assets
and nothing from it is committed.

Each leaf becomes two cards, back to back in x: one facing the viewer (+z), one yaw-flipped (so the sun is behind it), each a quad tilted 15 degrees:
  base colour = front albedo, alphaMode MASK (the leaf outline); KHR_materials_diffuse_transmission with the translucency texture as the colour, factor 0.7;
  a third card per leaf in the `-opaque` material (alpha dropped, as an export that loses the PNG's alpha would write it).

  uv run --with pillow --with numpy python3 tools/testbeds/habel-leaves.py LEAF_TEXTURES_DIR OUT_DIR   # writes OUT_DIR/habel-leaves.gltf
"""
import json, math, os, sys
import numpy as np
from PIL import Image

SIZE = 1024
buffers = bytearray()
views, accessors = [], []


def accessor(data, comp, kind, target, minmax=False):
    while len(buffers) % 4:
        buffers.append(0)
    raw = data.tobytes()
    views.append({"buffer": 0, "byteOffset": len(buffers), "byteLength": len(raw), "target": target})
    buffers.extend(raw)
    a = {"bufferView": len(views) - 1, "componentType": comp, "count": len(data), "type": kind}
    if minmax:
        a["min"], a["max"] = data.min(axis=0).tolist(), data.max(axis=0).tolist()
    accessors.append(a)
    return len(accessors) - 1


def main():
    src, out = sys.argv[1], sys.argv[2]
    os.makedirs(out, exist_ok=True)
    images, textures, materials, meshes, nodes = [], [], [], [], []

    def png(name, im):
        path = os.path.join(out, name)
        im.save(path)
        images.append({"uri": name})
        textures.append({"source": len(images) - 1, "sampler": 0})
        return len(textures) - 1

    # one unit card: x in [-0.25, 0.25], y in [0, 0.5], facing +z (the leaf's image is square, so the card is)
    pos = np.array([(-0.25, 0, 0), (0.25, 0, 0), (-0.25, 0.5, 0), (0.25, 0.5, 0)], np.float32)
    nrm = np.array([(0, 0, 1)] * 4, np.float32)
    uv = np.array([(0, 1), (1, 1), (0, 0), (1, 0)], np.float32)
    idx = np.array([0, 1, 2, 1, 3, 2], np.uint32)
    prim = {"attributes": {"POSITION": accessor(pos, 5126, "VEC3", 34962, True), "NORMAL": accessor(nrm, 5126, "VEC3", 34962), "TEXCOORD_0": accessor(uv, 5126, "VEC2", 34962)},
            "indices": accessor(idx, 5125, "SCALAR", 34963)}
    tilt = math.radians(15) / 2
    for i, leaf in enumerate(["Leaf1", "Leaf2", "Leaf3"]):
        front = Image.open(os.path.join(src, leaf, f"{leaf}_front.tga")).convert("RGBA").resize((SIZE, SIZE), Image.LANCZOS)
        trans = Image.open(os.path.join(src, leaf, f"{leaf}_front_t.tga")).convert("RGB").resize((SIZE, SIZE), Image.LANCZOS)
        flat = front.copy()
        flat.putalpha(255)
        t_front, t_trans, t_flat = png(f"{leaf}_front.png", front), png(f"{leaf}_trans.png", trans), png(f"{leaf}_flat.png", flat)
        common = {"roughnessFactor": 0.7, "metallicFactor": 0.0}
        masked = len(materials)
        materials.append({"name": f"{leaf}-mask", "doubleSided": True, "alphaMode": "MASK", "alphaCutoff": 0.5, "pbrMetallicRoughness": {"baseColorTexture": {"index": t_front}, **common},
                          "extensions": {"KHR_materials_diffuse_transmission": {"diffuseTransmissionFactor": 0.7, "diffuseTransmissionColorTexture": {"index": t_trans}}}})
        materials.append({"name": f"{leaf}-opaque", "doubleSided": True, "pbrMetallicRoughness": {"baseColorTexture": {"index": t_flat}, **common}})
        meshes.append({"primitives": [{**prim, "material": masked}]})
        meshes.append({"primitives": [{**prim, "material": masked + 1}]})
        x = i * 0.7
        for k, (dx, yaw) in enumerate([(0.0, 0.0), (0.3, math.pi)]):    # facing the viewer, then with its back to him (the sun in front of the card)
            h = yaw / 2
            q = np.array([math.sin(tilt) * math.cos(h), math.sin(h) * math.cos(tilt), -math.sin(tilt) * math.sin(h), math.cos(tilt) * math.cos(h)])
            nodes.append({"name": f"{leaf}-{'front' if k == 0 else 'back'}", "mesh": len(meshes) - 2, "translation": [x + dx, 0.15, 0.0], "rotation": q.tolist()})
        nodes.append({"name": f"{leaf}-opaque", "mesh": len(meshes) - 1, "translation": [x + 0.55, 0.15, 0.0]})
    g = {"asset": {"version": "2.0", "generator": "habel-leaves.py"}, "scene": 0, "scenes": [{"nodes": list(range(len(nodes)))}], "nodes": nodes, "meshes": meshes,
         "materials": materials, "textures": textures, "images": images, "samplers": [{"magFilter": 9729, "minFilter": 9987}], "accessors": accessors, "bufferViews": views,
         "buffers": [{"uri": "habel-leaves.bin", "byteLength": len(buffers)}], "extensionsUsed": ["KHR_materials_diffuse_transmission"]}
    open(os.path.join(out, "habel-leaves.bin"), "wb").write(buffers)
    json.dump(g, open(os.path.join(out, "habel-leaves.gltf"), "w"))
    print(f"habel-leaves: {len(nodes)} cards, {len(materials)} materials -> {os.path.join(out, 'habel-leaves.gltf')}")


if __name__ == "__main__":
    sys.exit(main())
