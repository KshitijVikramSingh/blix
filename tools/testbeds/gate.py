#!/usr/bin/env python3
"""The GI correctness gate's verdict, from --probe-reference logs of the correctness scenes.

usage: gate.py LOG_DIR      (reads cornell.log, thinwall-sealed.log, thinwall-open.log, crevices-corridor.log)

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


def samples(path):
    out = []
    with open(path) as f:
        for line in f:
            m = SAMPLE.search(line)
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
         lambda v: v <= 0.05, '<= 0.05', lambda v: v <= 0.06, '<= 0.06',
         'how far each corridor point is, over its mean light: the gather\'s noise there (2026-10-13: 0.036)'),
    ]

    failed = 0
    print(f'GI correctness gate ({len(cornell)} / {len(sealed)} / {len(lit)} / {len(corridor)} samples):')
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
