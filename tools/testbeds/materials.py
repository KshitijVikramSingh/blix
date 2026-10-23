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
# teacup's records excess 0.037 / 0.035 (0.046 / 0.062 before), ratios 1.003 / 0.998.
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
    ("khronos-teacup", "records", FIRST, "excess", 0.02, 0.045),
    ("khronos-teacup", "records", SECOND, "excess", 0.02, 0.045),
    ("khronos-teacup", "records", "opaque", "excess", 0.015, 0.02),
    ("khronos-test", "view", "opaque", "ratio", 0.02, 0.03),
    ("khronos-test", "view", "two-sided", "ratio", 0.03, 0.05),
    ("khronos-test", "view", THROUGH, "ratio", 0.03, 0.05),
    ("khronos-test", "records", "opaque", "ratio", 0.01, 0.02),
]


def main():
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
    print("materials: every ratchet holds." if not failed else "materials: FAILED.")
    sys.exit(1 if failed else 0)


if __name__ == "__main__":
    main()
