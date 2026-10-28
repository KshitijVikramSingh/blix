#!/usr/bin/env python3
"""A small glTF bench for the materials the big scenes get wrong: foliage cut-outs, thin glass, panes, cloth, sheen.

One row of stations (the ground comes from `blix khronos --room ground`), each one a thing a scene like San Miguel has hundreds of, cheap to boot and easy to point a camera at:

  leaf-opaque / leaf-mask   a cluster of cards with an RGBA leaf texture, once authored OPAQUE (as the San Miguel export wrote them) and once MASK
  goblet-thin               a lathe goblet, one wall, KHR_materials_transmission 0.3, roughness 0, no volume (San Miguel's material_79 and its kin)
  goblet-double             the same with an inner wall (a real glass has two surfaces: the panes stack)
  carafe                    a taller bottle, double wall, a tint
  pane                      a flat window pane, transmission 1, a plain coloured post behind it
  cloth-dt                  a hanging sheet with KHR_materials_diffuse_transmission (a curtain)
  cloth-sheen               a draped sheet with KHR_materials_sheen
  candle                    a cylinder with a BLEND material (San Miguel's one)

  uv run --with pillow --with numpy python3 tools/testbeds/material-room.py OUT_DIR [leaves,glass,pane,cloth,candle]   # writes OUT_DIR/material-room.gltf (+ .bin, leaf.png)
  (the optional list keeps only those stations, the first at x = 0: `blix gallery` places the groups apart in the Cornell room)

Stations sit at x = 0, 1, 2, ... (spacing 1 m) on z = 0, facing +z; a camera at (3.5, 1.1, 5) looking -z sees them all. Place it with the existing `blix khronos --model` step
(setup.sh does this under BLIX_TESTBED_MATERIAL_ROOM=1).
"""
import json, math, os, struct, sys
import numpy as np
from PIL import Image, ImageDraw

buffers = bytearray()
views, accessors, meshes, nodes, materials = [], [], [], [], []


def add_accessor(data, comp, kind, target=None, minmax=False):
    global buffers
    while len(buffers) % 4:
        buffers.append(0)
    raw = data.tobytes()
    views.append({"buffer": 0, "byteOffset": len(buffers), "byteLength": len(raw), **({"target": target} if target else {})})
    buffers += raw
    acc = {"bufferView": len(views) - 1, "componentType": comp, "count": len(data), "type": kind}
    if minmax:
        acc["min"] = data.min(axis=0).tolist()
        acc["max"] = data.max(axis=0).tolist()
    accessors.append(acc)
    return len(accessors) - 1


def mesh(pos, nrm, uv, idx, material):
    pos = np.asarray(pos, np.float32); nrm = np.asarray(nrm, np.float32); uv = np.asarray(uv, np.float32); idx = np.asarray(idx, np.uint32).reshape(-1)
    prim = {"attributes": {"POSITION": add_accessor(pos, 5126, "VEC3", 34962, True), "NORMAL": add_accessor(nrm, 5126, "VEC3", 34962),
                           "TEXCOORD_0": add_accessor(uv, 5126, "VEC2", 34962)},
            "indices": add_accessor(idx, 5125, "SCALAR", 34963), "material": material}
    meshes.append({"primitives": [prim]})
    return len(meshes) - 1


group_of = {}   # station name prefix -> the --only group it belongs to


def node(name, m, t=(0, 0, 0), rot=None):
    n = {"name": name, "mesh": m, "translation": list(t)}
    if rot:
        n["rotation"] = rot
    nodes.append(n)


def material(name, **kw):
    m = {"name": name, "doubleSided": kw.pop("doubleSided", True)}
    ext = kw.pop("ext", {})
    m["pbrMetallicRoughness"] = kw.pop("pbr")
    m.update(kw)
    if ext:
        m["extensions"] = ext
    materials.append(m)
    return len(materials) - 1


def lathe(profile, segs=40):
    """profile: [(radius, y)] from the bottom up. Smooth normals from the profile's tangent."""
    pos, nrm, uv, idx = [], [], [], []
    n = len(profile)
    for i, (r, y) in enumerate(profile):
        r0, y0 = profile[max(i - 1, 0)]; r1, y1 = profile[min(i + 1, n - 1)]
        tr, ty = r1 - r0, y1 - y0
        l = math.hypot(tr, ty) or 1
        nr, ny = ty / l, -tr / l       # outward for a profile that goes up
        for s in range(segs + 1):
            a = 2 * math.pi * s / segs
            pos.append((r * math.cos(a), y, r * math.sin(a)))
            nrm.append((nr * math.cos(a), ny, nr * math.sin(a)))
            uv.append((s / segs, i / (n - 1)))
    for i in range(n - 1):
        for s in range(segs):
            a = i * (segs + 1) + s; b = a + 1; c = a + segs + 1; d = c + 1
            idx += [a, c, b, b, c, d]
    return pos, nrm, uv, idx


def quad(w, h, subdiv=1, curve=0.0):
    """A w x h sheet in the xy plane facing +z; `curve` ripples it along z (a drape)."""
    pos, nrm, uv, idx = [], [], [], []
    nx = max(subdiv, 1)
    for j in range(nx + 1):
        for i in range(nx + 1):
            x = (i / nx - 0.5) * w; y = j / nx * h
            z = curve * math.sin(i / nx * math.pi * 4) * (1 - 0.5 * j / nx)
            pos.append((x, y, z)); nrm.append((0, 0, 1)); uv.append((i / nx, 1 - j / nx))
    for j in range(nx):
        for i in range(nx):
            a = j * (nx + 1) + i; b = a + 1; c = a + nx + 1; d = c + 1
            idx += [a, b, c, b, d, c]
    return pos, nrm, uv, idx


def leaf_texture(path):
    """A 512 px RGBA sprite: a few leaves on a dark ground whose alpha is 0 outside the leaves (like San Miguel's diffuse PNGs)."""
    im = Image.new("RGBA", (512, 512), (40, 70, 25, 0))
    d = ImageDraw.Draw(im)
    rng = np.random.default_rng(3)
    for _ in range(14):
        cx, cy = rng.uniform(60, 450, 2); a = rng.uniform(0, math.pi); L = rng.uniform(70, 110); W = L * 0.38
        pts = []
        for t in np.linspace(0, 2 * math.pi, 28):
            px = math.cos(t) * L; py = math.sin(t) * W * (1 - 0.3 * math.cos(t))
            pts.append((cx + px * math.cos(a) - py * math.sin(a), cy + px * math.sin(a) + py * math.cos(a)))
        g = int(rng.uniform(95, 150))
        d.polygon(pts, fill=(int(g * 0.35), g, int(g * 0.25), 255))
    im.save(path)


def main():
    out = sys.argv[1] if len(sys.argv) > 1 else "material-room"
    only = sys.argv[2].split(",") if len(sys.argv) > 2 else None   # --only groups: leaves, glass, pane, cloth, candle (a gallery places them apart)
    os.makedirs(out, exist_ok=True)
    leaf_texture(os.path.join(out, "leaf.png"))
    base = lambda c, r=0.5, m=0.0: {"baseColorFactor": [*c, 1], "roughnessFactor": r, "metallicFactor": m}

    # 0 and 1: leaf cards, the same texture, OPAQUE then MASK
    tex = {"images": [{"uri": "leaf.png"}], "samplers": [{"magFilter": 9729, "minFilter": 9987}], "textures": [{"source": 0, "sampler": 0}]}
    leaf_pbr = {"baseColorTexture": {"index": 0}, "roughnessFactor": 0.8, "metallicFactor": 0.0}
    m_leaf_op = material("leaf-opaque", pbr=leaf_pbr)
    m_leaf_mask = material("leaf-mask", pbr=leaf_pbr, alphaMode="MASK", alphaCutoff=0.5)
    m_trunk = material("trunk", pbr=base((0.25, 0.16, 0.1), 0.9))
    m_post = material("post", pbr=base((0.8, 0.15, 0.1), 0.7))
    m_post2 = material("post2", pbr=base((0.15, 0.3, 0.8), 0.7))
    # glass: thin, smooth, no volume
    glass = lambda name, t, tint=(1, 1, 1), ior=1.5: material(name, pbr=base((*tint,), 0.0), ext={"KHR_materials_transmission": {"transmissionFactor": t}, "KHR_materials_ior": {"ior": ior}})
    m_gob = glass("goblet", 0.3)
    m_gob_in = glass("goblet-inner", 0.3)
    m_car = glass("carafe", 0.37, (0.85, 1.0, 0.9))
    m_pane = glass("pane", 1.0)
    m_cloth = material("cloth-dt", pbr=base((0.85, 0.25, 0.2), 0.9), ext={"KHR_materials_diffuse_transmission": {"diffuseTransmissionFactor": 0.6, "diffuseTransmissionColorFactor": [1.0, 0.45, 0.35]}})
    m_sheen = material("cloth-sheen", pbr=base((0.2, 0.1, 0.35), 0.9), ext={"KHR_materials_sheen": {"sheenColorFactor": [0.9, 0.6, 1.0], "sheenRoughnessFactor": 0.4}})
    m_candle = material("candle", pbr=base((0.95, 0.9, 0.75), 0.5), alphaMode="BLEND")

    # stations
    def leaf_cluster(x, mat, name):
        trunk = lathe([(0.03, 0), (0.025, 0.7)], 8)
        node(name + "-trunk", mesh(*trunk, m_trunk), (x, 0, 0))
        pos, nrm, uv, idx = quad(0.5, 0.5)
        mi = mesh(pos, nrm, uv, idx, mat)
        for k, (dx, dy, yaw) in enumerate([(0, 0.55, 0), (0.06, 0.8, 60), (-0.08, 1.0, 120), (0.04, 0.7, 30), (-0.05, 0.9, 90)]):
            h = math.radians(yaw) / 2
            node(f"{name}-card{k}", mi, (x + dx, dy, 0.02 * k), [0, math.sin(h), 0, math.cos(h)])
    leaf_cluster(0.0, m_leaf_op, "leaf-opaque")
    leaf_cluster(1.0, m_leaf_mask, "leaf-mask")

    gob = [(0.0, 0.0), (0.05, 0.0), (0.012, 0.012), (0.012, 0.08), (0.06, 0.15), (0.075, 0.2), (0.07, 0.28)]
    node("goblet-thin", mesh(*lathe(gob), m_gob), (2.0, 0, 0))
    node("goblet-double", mesh(*lathe(gob), m_gob), (2.4, 0, 0))
    inner = [(r * 0.92, y + 0.008) for r, y in gob[3:]]
    inner = [(0.0, 0.03)] + [(0.012, 0.03)] + inner
    node("goblet-double-in", mesh(*lathe(inner), m_gob_in), (2.4, 0, 0))
    car = [(0, 0), (0.07, 0), (0.08, 0.03), (0.085, 0.16), (0.06, 0.22), (0.025, 0.28), (0.022, 0.34), (0.03, 0.36)]
    node("carafe", mesh(*lathe(car), m_car), (2.8, 0, 0))
    node("carafe-in", mesh(*lathe([(r * 0.93, y + 0.006) for r, y in car[1:]]), m_car), (2.8, 0, 0))

    # pane with a red post behind it and a blue post beside it (so the tint and the edge are readable)
    pos, nrm, uv, idx = quad(0.5, 0.6)
    node("pane", mesh(pos, nrm, uv, idx, m_pane), (3.5, 0.2, 0.15))
    node("post-behind", mesh(*lathe([(0.04, 0), (0.04, 0.6)], 12), m_post), (3.5, 0, -0.2))
    node("post-beside", mesh(*lathe([(0.04, 0), (0.04, 0.6)], 12), m_post2), (3.85, 0, 0.15))

    pos, nrm, uv, idx = quad(0.5, 0.9, 16, 0.03)
    node("cloth-dt", mesh(pos, nrm, uv, idx, m_cloth), (4.5, 0.15, 0))
    pos, nrm, uv, idx = quad(0.5, 0.9, 16, 0.03)
    node("cloth-sheen", mesh(pos, nrm, uv, idx, m_sheen), (5.3, 0.15, 0))
    node("candle", mesh(*lathe([(0.02, 0), (0.02, 0.12)], 16), m_candle), (6.0, 0, 0))

    groups = {"leaf": "leaves", "goblet": "glass", "carafe": "glass", "pane": "pane", "post": "pane", "cloth": "cloth", "candle": "candle"}
    if only:
        nodes[:] = [n for n in nodes if groups.get(n["name"].split("-")[0]) in only]
        x0 = min(n["translation"][0] for n in nodes)
        for n in nodes:
            n["translation"][0] -= x0
    # the glTF puts a default sun-lit ground: the engine places it, so no lights here.
    g = {"asset": {"version": "2.0", "generator": "material-room.py"}, "scene": 0, "scenes": [{"nodes": list(range(len(nodes)))}],
         "nodes": nodes, "meshes": meshes, "materials": materials, "accessors": accessors, "bufferViews": views,
         "buffers": [{"uri": "material-room.bin", "byteLength": len(buffers)}], **tex,
         "extensionsUsed": ["KHR_materials_transmission", "KHR_materials_ior", "KHR_materials_diffuse_transmission", "KHR_materials_sheen"]}
    open(os.path.join(out, "material-room.bin"), "wb").write(buffers)
    json.dump(g, open(os.path.join(out, "material-room.gltf"), "w"))
    print(f"material-room: {len(nodes)} nodes, {len(materials)} materials -> {os.path.join(out, 'material-room.gltf')}")


if __name__ == "__main__":
    main()
