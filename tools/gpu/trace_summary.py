#!/usr/bin/env python3
"""GPU time per render-graph pass from a Metal System Trace (tools/gpu/trace.sh records one).

usage: trace_summary.py TRACE.trace [--frames N]

The Vulkan timestamps bracket encoder submission on a tile-based GPU, not execution, so they miss the raster passes; the
trace's GPU intervals (metal-gpu-intervals) are the execution itself, per encoder and channel (vertex, fragment,
compute). Each encoder is named by the pass whose debug label it ran under (BLIX_GPU_LABELS=1: MoltenVK appends it to the
encoder's label in metal-application-intervals, ":rtgi-train"), else its own label (the Metal tracer's command buffers,
"blix-hwx ..."). Printed per pass: GPU time a frame -- each encoder's channels merged where they overlap (a raster
pass's vertex and fragment work run together) -- and the channels apart; the frame's GPU busy time (all encoders'
intervals merged, the end-of-buffer sync blits apart -- their spans are waits) beside the frames' wall time.
"""
import re
import subprocess
import sys
import xml.etree.ElementTree as ET
from collections import defaultdict


def table(trace, schema):
    xml = subprocess.run(['xcrun', 'xctrace', 'export', '--input', trace, '--xpath',
                          f'/trace-toc/run[@number="1"]/data/table[@schema="{schema}"]'],
                         capture_output=True, text=True, check=True).stdout
    root = ET.fromstring(xml)
    node = root.find('node')
    cols = [c.findtext('mnemonic') for c in node.find('schema').findall('col')]
    byid = {}
    rows = []
    for row in node.findall('row'):
        vals = {}
        for col, el in zip(cols, list(row)):
            if 'ref' in el.attrib:
                el = byid.get(el.attrib['ref'], el)
            elif 'id' in el.attrib:
                byid[el.attrib['id']] = el
            vals[col] = el
        rows.append(vals)
    return rows


def text(el):
    return (el.text or '').strip() if el is not None else ''


def fmt(el):
    return el.attrib.get('fmt', '') if el is not None else ''


def main():
    trace = sys.argv[1]
    names = {}
    for r in table(trace, 'metal-application-intervals'):
        enc = text(r.get('encoder-id'))
        label = fmt(r.get('event-label'))
        if not enc or not label:
            continue
        m = re.search(r' - :([^ (]+)', label)
        names[enc] = m.group(1) if m else label.split(' (')[0]
    passes = defaultdict(lambda: defaultdict(float))
    spans = defaultdict(list)     # encoder -> its channels' intervals
    owner = {}
    frames = set()
    busy = []
    work = []
    for r in table(trace, 'metal-gpu-intervals'):
        enc = text(r.get('encoder-id'))
        dur = int(text(r.get('duration')) or 0)
        start = int(text(r.get('start')) or 0)
        chan = fmt(r.get('channel-name'))
        frame = text(r.get('frame-number'))
        if not enc or dur <= 0:
            continue
        frames.add(frame)
        # An encoder this app never encoded (absent from its application intervals): another process's on the same GPU --
        # most likely the window compositor's (Cornell: ~2.5 ms a frame, 5,000 intervals in 3 s).
        name = names.get(enc, '(not this app: compositor?)')
        owner[enc] = name
        passes[name][chan] += dur
        spans[enc].append((start, start + dur))
        busy.append((start, start + dur))
        # MoltenVK closes each command buffer with blit encoders that carry its fences and events: their spans are the GPU
        # waiting (on the other command buffers, on the Metal tracer's batches), not working -- they overlap the work and
        # summed to more than the frame. Kept apart from the work.
        if not name.startswith('Blit Command'):
            work.append((start, start + dur))

    def merged(iv):
        total, end = 0, -1
        for a, b in sorted(iv):
            if b <= end:
                continue
            total += b - max(a, end)
            end = b
        return total

    per_pass = defaultdict(int)
    for enc, iv in spans.items():
        per_pass[owner[enc]] += merged(iv)
    # Frames by the app's own count: one 'present' pass each. Metal's frame numbers ran ~2 to the app's frame (Cornell: 554
    # in 3 s against its 12.7 ms frames), halving every figure a frame.
    presents = sum(1 for enc, name in owner.items() if name == 'present')
    n = max(presents or len(frames), 1)
    span_ns = (max(b for _, b in busy) - min(a for a, _ in busy)) if busy else 0
    print(f'{n} frames in the trace, {span_ns / n / 1e6:.2f} ms a frame wall: GPU working {merged(work) / n / 1e6:.2f} ms '
          f'(its encoders merged), with the end-of-buffer sync blits {merged(busy) / n / 1e6:.2f} ms')
    print(f'{"pass":<32} {"ms/frame":>9}  {"vertex":>7} {"fragment":>8} {"compute":>8}')
    for name, ns in sorted(per_pass.items(), key=lambda kv: -kv[1]):
        c = passes[name]
        print(f'{name[:32]:<32} {ns / n / 1e6:9.3f}  {c.get("Vertex", 0) / n / 1e6:7.3f} {c.get("Fragment", 0) / n / 1e6:8.3f} '
              f'{c.get("Compute", 0) / n / 1e6:8.3f}')


if __name__ == '__main__':
    main()
