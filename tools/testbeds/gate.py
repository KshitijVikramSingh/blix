#!/usr/bin/env python3
"""The GI correctness gate's verdict, from --probe-reference logs of the correctness scenes.

usage: gate.py LOG_DIR      (reads cornell.log, thinwall-sealed.log, thinwall-open.log, crevices-corridor.log; the last
                             with --gpu-ref-seams)

Every metric carries two bars:
  target   what correct is. Reported MET or NOT MET; a metric short of it is known work, not a failure.
  ratchet  where the renderer stands today, with a margin. Breaking it FAILS the gate: something got worse.
When a change moves a metric past its ratchet for the better, tighten the ratchet here in the same commit.

The numbers come from the per-sample lines --probe-reference prints ("surface at ... total F ref R"): F is the
incident light the GPU shaded with (all indirect diffuse), R the CPU path trace on the triangles at the same point.
"""
import os
import re
import sys

SAMPLE = re.compile(r'surface  at \S+ .* total (-?[\d.]+) ref (-?[\d.]+)')
# --gpu-ref-seams: points at concave creases, where a leak through the joint lights first.
SEAM = re.compile(r'seam     at \S+ .* total (-?[\d.]+) ref (-?[\d.]+)')


def samples(path, pattern=SAMPLE):
    out = []
    with open(path) as f:
        for line in f:
            m = pattern.search(line)
            if m:
                field, ref = float(m.group(1)), float(m.group(2))
                if field >= 0:
                    out.append((field, ref))
    if not out:
        raise SystemExit(f'gate: {path} has no reference samples -- did the run reach its shot?')
    return out


def mean(values):
    values = list(values)
    return sum(values) / len(values)


def median(values):
    values = sorted(values)
    return values[len(values) // 2]


def main():
    logs = sys.argv[1]
    cornell = samples(os.path.join(logs, 'cornell.log'))
    sealed = samples(os.path.join(logs, 'thinwall-sealed.log'))
    lit = samples(os.path.join(logs, 'thinwall-open.log'))
    corridor = samples(os.path.join(logs, 'crevices-corridor.log'))
    seams = samples(os.path.join(logs, 'crevices-corridor.log'), SEAM)
    corridor_ref = mean(r for _, r in corridor)

    cornell_ref = mean(r for _, r in cornell)
    floor = 0.02 * cornell_ref
    open_field = mean(f for f, _ in lit)

    # (name, value, target test, target text, ratchet test, ratchet text, what it says)
    metrics = [
        ('cornell energy', mean(f for f, _ in cornell) / cornell_ref,
         lambda v: abs(v - 1) <= 0.03, '|x - 1| <= 0.03', lambda v: abs(v - 1) <= 0.03, '|x - 1| <= 0.03',
         'mean indirect light over the path trace: is light created or lost (2026-10-13: 0.993)'),
        ('cornell median ratio', median(f / r for f, r in cornell if r > floor),
         lambda v: 0.95 <= v <= 1.05, '0.95-1.05', lambda v: 0.95 <= v <= 1.05, '0.95-1.05',
         'the typical point, field over truth (2026-10-13: 0.997)'),
        ('cornell rel |error|', mean(abs(f - r) for f, r in cornell) / cornell_ref,
         lambda v: v <= 0.10, '<= 0.10', lambda v: v <= 0.05, '<= 0.05',
         'how far each point is, over the mean light (2026-10-08: 0.171 / 0.172 / 0.171; 2026-10-13: 0.021)'),
        ('thinwall leak', mean(f for f, _ in sealed) / open_field,
         lambda v: v <= 0.01, '<= 0.01', lambda v: v <= 0.01, '<= 0.01',
         'light in the sealed room over the open room\'s: all of it crossed a 0.1 m wall (2026-10-08: 0.092-0.111;'
         ' 2026-10-13: 0.0008-0.0015 over five runs)'),
        ('thinwall open energy', open_field / mean(r for _, r in lit),
         lambda v: abs(v - 1) <= 0.05, '|x - 1| <= 0.05', lambda v: abs(v - 1) <= 0.03, '|x - 1| <= 0.03',
         'the open room against its path trace, so a leak fix that darkens everything shows (2026-10-08: 0.889-0.898 against'
         ' a 3-bounce trace; 2026-10-13: 1.000 against 32)'),
        ('crevices corridor energy', mean(f for f, _ in corridor) / corridor_ref,
         lambda v: abs(v - 1) <= 0.03, '|x - 1| <= 0.03', lambda v: abs(v - 1) <= 0.05, '|x - 1| <= 0.05',
         'the dark crevices\' L corridor, lit by bounce through a doorway alone: shade from rare paths (2026-10-13: 0.986)'),
        ('crevices corridor |error|', mean(abs(f - r) for f, r in corridor) / corridor_ref,
         lambda v: v <= 0.015, '<= 0.015', lambda v: v <= 0.035, '<= 0.035',
         'how far each corridor point is, over its mean light: the gather\'s noise there (2026-10-13: 0.036 against a 16,384-path '
         'reference, itself 0.026 off; against 262,144 (0.007 off): the gather 0.021, the split cook 0.025)'),
        # The seams were judged by their excess alone (Sum max(F - R, 0) over Sum R, ratchet 0.03): built when a leak put
        # 6% of light into the creases (2026-10-16), by 2026-10-22 it read the noise's bright half and any bright bias --
        # the gather losing a dark bias (seam |error| 0.0735 -> 0.0560, bias -2.2% -> +0.5% over four changes) read worse.
        # Now the error both ways, the bias (energy), and a leak as a leak looks: light far above its point.
        ('crevices seam |error|', sum(abs(f - r) for f, r in seams) / sum(r for _, r in seams),
         lambda v: v <= 0.04, '<= 0.04', lambda v: v <= 0.07, '<= 0.07',
         'how far the corridor\'s crease points are, both ways, over their light (2026-10-22: 0.056; the excess and deficit '
         'were 0.026 / 0.048 on 2026-10-21)'),
        ('crevices seam leak', sum(f - 1.25 * r for f, r in seams if f > 1.25 * r) / sum(r for _, r in seams),
         lambda v: v <= 0.001, '<= 0.001', lambda v: v <= 0.005, '<= 0.005',
         'crease light more than 25% above its point\'s path trace, over the creases\' light: a texel centred where a box '
         'rests on a face gathered half inside the box, its sky-lit outside (2026-10-16: excess 0.063 -> 0.015, records '
         'moved off contacts; 2026-10-22: 0.0023, one crease bottom at +31-41%)'),
        ('crevices seam energy', mean(f for f, _ in seams) / mean(r for _, r in seams),
         lambda v: abs(v - 1) <= 0.02, '|x - 1| <= 0.02', lambda v: abs(v - 1) <= 0.05, '|x - 1| <= 0.05',
         'the creases\' light over the path trace\'s: the texels beside a buried one answer for it (2026-10-16: 0.886; '
         '2026-10-22: 1.005)'),
    ]

    failed = 0
    print(f'GI correctness gate ({len(cornell)} / {len(sealed)} / {len(lit)} / {len(corridor)} + {len(seams)} seam samples):')
    for name, value, target, target_text, ratchet, ratchet_text, what in metrics:
        held = ratchet(value)
        met = target(value)
        failed += not held
        print(f'  {"ok  " if held else "FAIL"} {name:22s} {value:8.4f}   target {target_text:16s} {"MET" if met else "not met":8s}'
              f' ratchet {ratchet_text:16s}  -- {what}')
    if failed:
        print(f'gate: {failed} ratchet(s) broken -- the GI got worse on a correctness scene.')
        return 1
    print('gate: every ratchet holds.')
    return 0


if __name__ == '__main__':
    sys.exit(main())
