#!/usr/bin/env python3
"""The material and transmission check's verdict, from the view dumps tools/testbeds/materials.sh makes.

usage: materials.py OUT_DIR      (reads OUT_DIR/<scene>/view-*/ for drapes, khronos-plant, khronos-teacup, khronos-test)

Two measures a scene, both against the camera-path reference (tools/gi-model/view_reference.py):
  view     what the camera sees (--viz 26) against the path trace through the material, by the surface under each grid
           point: opaque, two-sided, letting light through -- the ratio of their sums
  records  the GI's resolved light at lightmap records against the indirect light traced there (--ref-view-records):
           opaque ones (the units held together), and the faces of surfaces letting light through -- their ratio of sums,
           and their excess (the light over the trace's, over all of it: a leak -- the teacup's far sides 0.155 before the
           couplings' and the unknown hits' fixes, 0.04 after, opaque records' ~0.009 the noise; 2026-10-22)
Every metric carries two bars, as gate.py's: a target (what correct is; NOT MET is known work) and a ratchet (where the
renderer stands today, with a margin; breaking it FAILS). Tighten a ratchet in the commit that moves a metric past it.
A dump whose reference did not finish fails outright: a part of the paths measures the reference.
"""
import importlib.util
import sys
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location("view_reference", REPO / "tools" / "gi-model" / "view_reference.py")
vr = importlib.util.module_from_spec(spec)
spec.loader.exec_module(vr)

THROUGH = "lets light through"
FIRST, SECOND = "lets light through, a pair's first", "lets light through, a pair's second"

# (scene, measure, class, statistic, target, ratchet). Statistics: "ratio" the ratio of sums (bars on |x - 1|), "excess"
# the records' light over the trace's over all of it (bars on x).
# 2026-10-24, the reference stepping 1 mm off a hit (was 1 cm) and the patches' sheets by their own triangle: the
# teacup's records excess 0.037 / 0.035 (0.046 / 0.062 before), ratios 1.003 / 0.998. Then the teacup resting on the floor
# (blix khronos --rest; it stood 1 cm into it) and the pixels' sun rays lifted within their room: excess 0.030 / 0.030.
# Today's (2026-10-22): drapes -1.5% / -0.7%; the plant -0.9% / +1.3% (its leaves textured: the reference's per-row means,
# view_reference.py); the teacup -1.3% / -1.9% (records +0.5% / -0.9%, excess 0.04); the test -1.5% / -4.1% / +2.2%.
BARS = [
    ("drapes", "view", "opaque", "ratio", 0.02, 0.03),
    ("drapes", "view", THROUGH, "ratio", 0.02, 0.03),
    ("drapes", "records", "opaque", "ratio", 0.01, 0.02),
    ("khronos-plant", "view", "opaque", "ratio", 0.02, 0.03),
    ("khronos-plant", "view", THROUGH, "ratio", 0.03, 0.05),
    ("khronos-plant", "records", "opaque", "ratio", 0.01, 0.02),
    ("khronos-teacup", "view", "opaque", "ratio", 0.02, 0.03),
    ("khronos-teacup", "view", THROUGH, "ratio", 0.03, 0.05),
    ("khronos-teacup", "records", "opaque", "ratio", 0.01, 0.02),
    ("khronos-teacup", "records", FIRST, "ratio", 0.03, 0.04),
    ("khronos-teacup", "records", SECOND, "ratio", 0.03, 0.04),
    ("khronos-teacup", "records", FIRST, "excess", 0.02, 0.04),
    ("khronos-teacup", "records", SECOND, "excess", 0.02, 0.04),
    ("khronos-teacup", "records", "opaque", "excess", 0.015, 0.02),
    ("khronos-test", "view", "opaque", "ratio", 0.02, 0.03),
    ("khronos-test", "view", "two-sided", "ratio", 0.03, 0.05),
    ("khronos-test", "view", THROUGH, "ratio", 0.03, 0.05),
    ("khronos-test", "records", "opaque", "ratio", 0.01, 0.02),
]

# --diffuse eon (BLIX_MATERIALS_DIFFUSE=eon, which materials.sh sets): the reference evaluates EON at every hit and at the camera's (reference_wave.comp), and the
# frame's EON approximates it two ways the reference does not: the lit shader multiplies an irradiance by EON's albedo toward the eye (exact for a cosine-distributed
# field, off where the light is concentrated: a room seen from inside has its light on the camera's side, and EON retroreflects), and the transport bounces with the
# hemispherical albedo. Each opaque view ratio is 2-8% under, the records 1.6-1.9% over. These are the ratchets where that stands (2026-10-25), targets unchanged;
# the khronos-test two-sided ratio (0.81, 0.95 in Lambert) is the one NOT explained yet. Missing keys keep BARS' ratchet.
EON_RATCHETS = {
    ("drapes", "view", "opaque", "ratio"): 0.10,
    ("drapes", "records", "opaque", "ratio"): 0.025,
    ("khronos-plant", "view", "opaque", "ratio"): 0.065,
    ("khronos-plant", "records", "opaque", "ratio"): 0.03,
    ("khronos-teacup", "records", "opaque", "ratio"): 0.03,
    ("khronos-teacup", "records", FIRST, "excess"): 0.05,
    ("khronos-teacup", "records", SECOND, "excess"): 0.05,
    ("khronos-teacup", "records", "opaque", "excess"): 0.03,
    ("khronos-test", "view", "two-sided", "ratio"): 0.22,
    ("khronos-test", "view", THROUGH, "ratio"): 0.08,
}

# The alpha scene (khronos-alpha: Khronos' AlphaBlendModeTest, --ray-view): the traced camera view against the raster's depth, which
# alpha-tests exactly. Shares of all pixels that disagree, from the log's "ray view against the raster" line: where a ray's alpha
# test (a per-triangle coin, or a per-hit texel test) lands the pixel somewhere the raster's does not. (statistic, target, ratchet.)
ALPHA_BARS = [
    ("foliage", 0.3, 3.5),      # 3.21 with the per-triangle coverage coin (2026-10-25)
    ("elsewhere", 0.3, 3.2),    # 2.86: the coin's speckle, and the 0.75-cutoff panel's discarded half that rays still meet
]


def alpha_view(out):
    """The ray view's disagreement shares for the alpha scene, or None if its log has no line."""
    import re
    log = out / "khronos-alpha.log"
    if not log.exists(): return None
    hit = None
    for line in log.read_text(errors="replace").splitlines():
        if "ray view against the raster" in line: hit = line
    if hit is None: return None
    m = re.search(r"agree within 1% on ([\d.]+)%.*?disagree at alpha-tested foliage ([\d.]+)%, glass ([\d.]+)%, depth edges ([\d.]+)%, elsewhere ([\d.]+)%", hit)
    if m is None: return None
    agree, foliage, glass, edge, other = map(float, m.groups())
    return {"agree": agree, "foliage": foliage, "glass": glass, "edges": edge, "elsewhere": other}


def main():
    import os
    eon = os.environ.get("BLIX_MATERIALS_DIFFUSE") == "eon"
    out = Path(sys.argv[1])
    dumps = {}
    failed = False
    for scene in sorted({b[0] for b in BARS}):
        found = sorted((out / scene).glob("view-*"))
        if not found:
            print(f"FAIL {scene}: no view dump in {out / scene} -- did the run reach its shot?")
            failed = True
            continue
        r = vr.measure(found[-1])
        if r["frames"][0] < r["frames"][1]:
            print(f"FAIL {scene}: the reference had {r['frames'][0]} of its {r['frames'][1]} frames at the dump")
            failed = True
        dumps[scene] = r
    print("Material and transmission check (the camera-path reference, tools/gi-model/view_reference.py):")
    for scene, measure, cls, stat, target, ratchet in BARS:
        if eon: ratchet = EON_RATCHETS.get((scene, measure, cls, stat), ratchet)
        if scene not in dumps: continue
        row = dumps[scene]["classes" if measure == "view" else "records"].get(cls)
        if row is None:
            print(f"  FAIL {scene} {measure} {cls}: no points of the class")
            failed = True
            continue
        if stat == "ratio":
            x = row[3]; dev = abs(x - 1)
        else:
            x = row[7]; dev = x
        ok = dev <= ratchet
        failed |= not ok
        label = f"{scene} {measure} {cls} {stat}"
        print(f"  {'ok  ' if ok else 'FAIL'} {label:62s} {x:7.3f}  target {target:.2f} {'MET    ' if dev <= target else 'not met'}  ratchet {ratchet:.2f}  ({row[0]} points)")
    alpha = alpha_view(out)
    if alpha is None:
        print("  FAIL khronos-alpha ray view: no 'ray view against the raster' line in its log")
        failed = True
    else:
        print(f"  khronos-alpha ray view: agree {alpha['agree']:.2f}%, foliage {alpha['foliage']:.2f}%, glass {alpha['glass']:.2f}%, edges {alpha['edges']:.2f}%, elsewhere {alpha['elsewhere']:.2f}%")
        for stat, target, ratchet in ALPHA_BARS:
            x = alpha[stat]
            ok = x <= ratchet
            failed |= not ok
            print(f"  {'ok  ' if ok else 'FAIL'} {'khronos-alpha ray view ' + stat + ' (% of pixels)':62s} {x:7.2f}  target {target:.2f} {'MET    ' if x <= target else 'not met'}  ratchet {ratchet:.2f}")
    print("materials: every ratchet holds." if not failed else "materials: FAILED.")
    sys.exit(1 if failed else 0)


if __name__ == "__main__":
    main()
