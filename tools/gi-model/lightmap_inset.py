# /// script
# requires-python = ">=3.10"
# dependencies = ["numpy"]
# ///
"""How far the lightmap's edge inset moves a texel's record point off where it lies, on a mesh's triangles (uv run
tools/gi-model/lightmap_inset.py mesh.gltf [mesh name]).

SponzaLoop.Lightmap.cs (Inset): each barycentric weight floored at the inset over the triangle's height across from its
vertex. On a sliver -- a strip of a folded curtain, 2-8 cm wide and metres long -- both long edges are within the inset of
every point, their floors take 1/3 each, and every texel near the far tip is dragged to a third of the way along: metres.
The alternative: inset only a triangle's chart-boundary edges (those no other triangle shares), where the surface ends or
turns -- every crease is one -- and leave the edges between neighbouring strips of one surface alone."""
import json, struct, sys
from pathlib import Path
import numpy as np

INSET = 0.01


def load(path, name):
    g = json.loads(Path(path).read_text())
    blob = (Path(path).parent / g["buffers"][0]["uri"]).read_bytes()
    def acc(i, comps):
        a = g["accessors"][i]; v = g["bufferViews"][a["bufferView"]]
        off = v.get("byteOffset", 0) + a.get("byteOffset", 0)
        fmt = {5126: "f", 5125: "I", 5123: "H"}[a["componentType"]]
        n = a["count"] * comps
        return np.array(struct.unpack_from(f"<{n}{fmt}", blob, off)).reshape(a["count"], comps)
    for m in g["meshes"]:
        if m["name"] != name: continue
        p = m["primitives"][0]
        return acc(p["attributes"]["POSITION"], 3), acc(p["indices"], 1).ravel().reshape(-1, 3)
    raise SystemExit(f"no mesh {name}")


def inset(pa, pb, pc, w, floors):
    two = np.linalg.norm(np.cross(pb - pa, pc - pa))
    f = np.array([min(INSET * np.linalg.norm(e) / max(two, 1e-12), 1 / 3) if on else 0.0
                  for e, on in zip((pc - pb, pa - pc, pb - pa), floors)])
    r = np.maximum(w - f, 0.0)
    rest = 1 - f.sum()
    r = r * rest / r.sum() if r.sum() > 1e-12 else np.full(3, rest / 3)
    q = f + r
    return q[0] * pa + q[1] * pb + q[2] * pc


def inset_geometric(pa, pb, pc, p, floors):
    """Pushed straight in from each inset edge it is nearer than the inset (in the triangle's plane), never past the
    middle of a sliver too narrow for both: at most the inset, wherever the triangle is long."""
    n = np.cross(pb - pa, pc - pa); n /= np.linalg.norm(n)
    corners = (pa, pb, pc)
    q = p.copy()
    for _ in range(2):
        for i, on in enumerate(floors):
            if not on: continue
            a, b, opp = corners[(i + 1) % 3], corners[(i + 2) % 3], corners[i]
            e = (b - a) / np.linalg.norm(b - a)
            inward = np.cross(n, e)
            if np.dot(opp - a, inward) < 0: inward = -inward
            d = np.dot(q - a, inward)
            if d < INSET: q = q + (INSET - d) * inward
    return q


if __name__ == "__main__":
    pos, tri = load(sys.argv[1], sys.argv[2] if len(sys.argv) > 2 else "curtain")
    edges = {}
    for t in tri:
        for a, b in ((t[1], t[2]), (t[2], t[0]), (t[0], t[1])):
            edges[tuple(sorted((a, b)))] = edges.get(tuple(sorted((a, b))), 0) + 1
    moves = {"every edge (now)": [], "chart-boundary edges only": [], "boundary, pushed in (proposed)": []}
    rng = np.random.default_rng(1)
    for t in tri:
        pa, pb, pc = pos[t]
        boundary = [edges[tuple(sorted(e))] == 1 for e in ((t[1], t[2]), (t[2], t[0]), (t[0], t[1]))]
        for _ in range(64):
            w = rng.dirichlet((1, 1, 1))
            p = w[0] * pa + w[1] * pb + w[2] * pc
            moves["every edge (now)"].append(np.linalg.norm(inset(pa, pb, pc, w, [True] * 3) - p))
            moves["chart-boundary edges only"].append(np.linalg.norm(inset(pa, pb, pc, w, boundary) - p))
            moves["boundary, pushed in (proposed)"].append(np.linalg.norm(inset_geometric(pa, pb, pc, p, boundary) - p))
    print(f"{len(tri)} triangles of {sys.argv[2] if len(sys.argv) > 2 else 'curtain'}; a record point's move, in cm:")
    for k, v in moves.items():
        v = np.array(v) * 100
        print(f"   {k:<28} median {np.median(v):6.2f}   p90 {np.percentile(v, 90):6.2f}   max {v.max():7.2f}")
