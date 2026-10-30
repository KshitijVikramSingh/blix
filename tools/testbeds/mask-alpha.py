#!/usr/bin/env python3
"""Mark the materials whose base colour texture has real transparency as alpha MASK.

Some exports drop the alpha a source carries in the diffuse texture: San Miguel 2 (McGuire archive) has no map_d in its MTL, the leaf cards' cut-out is the PNG's alpha channel, and
the Blender round trip exported every one of them OPAQUE -- the cards draw as whole squares, in the raster and to the rays. This reads a glTF, finds each OPAQUE material whose base
colour texture has more than --share of its pixels under alpha 250, and writes a copy of the glTF with alphaMode MASK and alphaCutoff 0.5 on them (nothing else is touched; the .bin and
the images are linked beside it). Idempotent: MASK and BLEND materials are left as authored.

  uv run --with pillow --with numpy python3 tools/testbeds/mask-alpha.py SRC/scene.gltf OUT_DIR [--share 0.02] [--cutoff 0.5]
"""
import argparse, json, os, sys
import numpy as np
from PIL import Image


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("gltf")
    ap.add_argument("out_dir")
    ap.add_argument("--share", type=float, default=0.02, help="the share of a texture's pixels under alpha 250 that makes its material a cut-out")
    ap.add_argument("--cutoff", type=float, default=0.5)
    a = ap.parse_args()
    src_dir = os.path.dirname(os.path.abspath(a.gltf))
    g = json.load(open(a.gltf))
    transparent = {}   # image path -> the share of its pixels under alpha 250 (None: no alpha channel)

    def share_of(path):
        if path not in transparent:
            im = Image.open(path)
            if "A" in im.getbands() or im.mode in ("LA", "RGBA", "P"):
                alpha = np.asarray(im.convert("RGBA"))[..., 3]
                transparent[path] = float((alpha < 250).mean())
            else:
                transparent[path] = None
        return transparent[path]

    changed = []
    for index, m in enumerate(g.get("materials", [])):
        if m.get("alphaMode", "OPAQUE") != "OPAQUE":
            continue
        tex = m.get("pbrMetallicRoughness", {}).get("baseColorTexture")
        if tex is None:
            continue
        image = g["images"][g["textures"][tex["index"]]["source"]]
        uri = image.get("uri")
        path = os.path.join(src_dir, uri) if uri else None
        if path is None or not os.path.exists(path):
            continue
        s = share_of(path)
        if s is not None and s > a.share:
            m["alphaMode"] = "MASK"
            m["alphaCutoff"] = a.cutoff
            changed.append((index, m.get("name", ""), os.path.basename(path), s))

    os.makedirs(a.out_dir, exist_ok=True)
    for name in os.listdir(src_dir):
        link = os.path.join(a.out_dir, name)
        if name == os.path.basename(a.gltf) or os.path.lexists(link):
            continue
        os.symlink(os.path.join(src_dir, name), link)
    json.dump(g, open(os.path.join(a.out_dir, os.path.basename(a.gltf)), "w"))
    print(f"mask-alpha: {len(changed)} of {len(g.get('materials', []))} materials now MASK (cutoff {a.cutoff}); {os.path.join(a.out_dir, os.path.basename(a.gltf))}")
    for index, name, image, s in changed[:8]:
        print(f"  {index} {name}: {image}, {s:.0%} transparent")
    if len(changed) > 8:
        print(f"  ... and {len(changed) - 8} more")


if __name__ == "__main__":
    sys.exit(main())
