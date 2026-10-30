"""Procedural glTF scenes for the tonemap lab: small, deterministic, no assets to download.

    uv run python scenes/make_scenes.py [out_dir]

Why not Sponza: it is minutes of boot for one frame. What a tonemapper sees is the HDR radiance field's
statistics (range, saturation, hue spread), and a grid of lit and emissive spheres under Studio's sun and sky
spans them in seconds. Each scene is a few hundred triangles baked into one GLB (no node transforms).

materials_grid   5 x 5: grey and saturated dielectrics across roughness, plus a metal row. Lit by the sun/sky,
                 so it carries the shaded-to-specular range and the lit-colour hue spread.
emissive_ramp    6 saturated hues x 4 emissive strengths (1, 4, 16, 64) on near-black spheres: the shoulder and
                 hue-preservation test, since an emitter is exactly "colour well above white".
"""
from __future__ import annotations

import json
import struct
import sys
from pathlib import Path

import numpy as np


def uv_sphere(center, radius, lat=24, lon=48):
    """A sphere, or an ellipsoid when `radius` is three semi-axes (a flat panel is one with a tiny z)."""
    v = np.linspace(0.0, np.pi, lat + 1)
    u = np.linspace(0.0, 2.0 * np.pi, lon + 1)
    uu, vv = np.meshgrid(u, v)
    n = np.stack([np.sin(vv) * np.cos(uu), np.cos(vv), np.sin(vv) * np.sin(uu)], axis=-1).reshape(-1, 3)
    p = n * np.asarray(radius, dtype=np.float64) + np.asarray(center, dtype=np.float64)
    # Normals of an ellipsoid: the gradient, n / radius, renormalised.
    n = n / np.asarray(radius, dtype=np.float64)
    n = n / np.linalg.norm(n, axis=-1, keepdims=True)
    idx = []
    for i in range(lat):
        for j in range(lon):
            a = i * (lon + 1) + j
            b = a + lon + 1
            idx += [a, b, a + 1, a + 1, b, b + 1]
    return p.astype(np.float32), n.astype(np.float32), np.array(idx, dtype=np.uint32)


def write_glb(path, spheres):
    """spheres: list of (center, radius, material dict)."""
    bin_chunks, views, accessors, meshes, materials, nodes = [], [], [], [], [], []
    offset = 0

    def add(data: bytes, target):
        nonlocal offset
        pad = (-len(data)) % 4
        views.append({"buffer": 0, "byteOffset": offset, "byteLength": len(data), **({"target": target} if target else {})})
        bin_chunks.append(data + b"\0" * pad)
        offset += len(data) + pad
        return len(views) - 1

    for k, (center, radius, mat) in enumerate(spheres):
        p, n, idx = uv_sphere(center, radius)
        vp = add(p.tobytes(), 34962)
        vn = add(n.tobytes(), 34962)
        vi = add(idx.tobytes(), 34963)
        a = len(accessors)
        accessors += [
            {"bufferView": vp, "componentType": 5126, "count": len(p), "type": "VEC3",
             "min": p.min(axis=0).tolist(), "max": p.max(axis=0).tolist()},
            {"bufferView": vn, "componentType": 5126, "count": len(n), "type": "VEC3"},
            {"bufferView": vi, "componentType": 5125, "count": len(idx), "type": "SCALAR"},
        ]
        materials.append(mat)
        meshes.append({"primitives": [{"attributes": {"POSITION": a, "NORMAL": a + 1}, "indices": a + 2, "material": k}]})
        nodes.append({"mesh": k})

    blob = b"".join(bin_chunks)
    doc = {
        "asset": {"version": "2.0", "generator": "blix tonemap lab"},
        "extensionsUsed": ["KHR_materials_emissive_strength"],
        "scene": 0, "scenes": [{"nodes": list(range(len(nodes)))}],
        "nodes": nodes, "meshes": meshes, "materials": materials,
        "accessors": accessors, "bufferViews": views, "buffers": [{"byteLength": len(blob)}],
    }
    js = json.dumps(doc, separators=(",", ":")).encode()
    js += b" " * ((-len(js)) % 4)
    total = 12 + 8 + len(js) + 8 + len(blob)
    with open(path, "wb") as f:
        f.write(struct.pack("<III", 0x46546C67, 2, total))
        f.write(struct.pack("<II", len(js), 0x4E4F534A) + js)
        f.write(struct.pack("<II", len(blob), 0x004E4942) + blob)


def pbr(color, metallic=0.0, roughness=0.5, emissive=None, strength=None):
    m = {"pbrMetallicRoughness": {"baseColorFactor": [*color, 1.0], "metallicFactor": metallic,
                                  "roughnessFactor": roughness}}
    if emissive is not None:
        m["emissiveFactor"] = list(emissive)
        if strength is not None:
            m["extensions"] = {"KHR_materials_emissive_strength": {"emissiveStrength": strength}}
    return m


def materials_grid():
    # Linear base colours: white, mid grey (0.18 reflectance), and three saturated primaries.
    colors = [(0.9, 0.9, 0.9), (0.18, 0.18, 0.18), (0.8, 0.05, 0.04), (0.05, 0.6, 0.08), (0.04, 0.1, 0.8)]
    out = []
    for r, rough in enumerate((0.1, 0.35, 0.6, 0.9)):
        for c, col in enumerate(colors):
            out.append((((c - 2) * 2.2, -r * 2.2, 0.0), 1.0, pbr(col, 0.0, rough)))
    gold = (1.0, 0.77, 0.34)
    for c, rough in enumerate((0.05, 0.2, 0.4, 0.6, 0.9)):
        out.append((((c - 2) * 2.2, -4 * 2.2, 0.0), 1.0, pbr(gold, 1.0, rough)))
    return out


def emissive_ramp():
    hues = [(1.0, 0.05, 0.03), (1.0, 0.35, 0.02), (0.1, 1.0, 0.1), (0.03, 0.6, 1.0), (0.05, 0.1, 1.0), (1.0, 0.08, 0.8)]
    out = []
    for r, strength in enumerate((1.0, 4.0, 16.0, 64.0)):
        for c, hue in enumerate(hues):
            out.append((((c - 2.5) * 2.2, -r * 2.2, 0.0), 1.0, pbr((0.02, 0.02, 0.02), 0.0, 0.5, hue, strength)))
    return out


def night_lamps():
    """Dark surroundings, light from a wide dim panel, lamps across ~14 stops, neon, and glossy things to catch it.

    The first two scenes are a bright stage with emitters on it; this one is what HDR is for: most of the frame is
    near black, a little is dim and large (the panels), and a little is tiny and far brighter (the lamps).
    """
    warm, cool = (1.0, 0.82, 0.55), (0.55, 0.75, 1.0)
    out = [((-4.5, 2.6, -2.2), (2.2, 1.5, 0.04), pbr((0.02, 0.02, 0.02), 0.0, 0.5, warm, 3.0)),     # a lit window
           ((4.6, 2.3, -2.2), (1.8, 1.1, 0.04), pbr((0.02, 0.02, 0.02), 0.0, 0.5, cool, 1.2))]     # a screen
    for i, strength in enumerate((4.0, 15.0, 60.0, 250.0, 1000.0, 4000.0)):                      # lamps, 10 stops apart
        out.append((((i - 2.5) * 2.0, 0.6, 0.0), 0.3, pbr((0.02, 0.02, 0.02), 0.0, 0.5, warm, strength)))
    neon = [(1.0, 0.05, 0.1), (1.0, 0.45, 0.0), (0.1, 1.0, 0.2), (0.0, 0.7, 1.0), (0.45, 0.1, 1.0)]
    for i, hue in enumerate(neon):
        out.append((((i - 2) * 2.4, -1.1, 0.4), 0.25, pbr((0.02, 0.02, 0.02), 0.0, 0.5, hue, 25.0)))
    glossy = [(pbr((0.5, 0.02, 0.02), 0.0, 0.2)), pbr((1.0, 1.0, 1.0), 1.0, 0.04),
              pbr((1.0, 0.77, 0.34), 1.0, 0.25), pbr((0.9, 0.9, 0.9), 0.0, 0.1), pbr((0.04, 0.1, 0.7), 0.0, 0.2)]
    for i, mat in enumerate(glossy):
        out.append((((i - 2) * 2.4, -3.0, 0.2), 0.9, mat))
    return out


def main():
    out = Path(sys.argv[1]) if len(sys.argv) > 1 else Path("data/scenes")
    out.mkdir(parents=True, exist_ok=True)
    for name, build in (("materials_grid", materials_grid), ("emissive_ramp", emissive_ramp), ("night_lamps", night_lamps)):
        write_glb(out / f"{name}.glb", build())
        print("wrote", out / f"{name}.glb")


if __name__ == "__main__":
    main()
