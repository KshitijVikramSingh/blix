# /// script
# requires-python = ">=3.10"
# dependencies = ["numpy"]
# ///
"""The light-agnostic record's stage 1, held against what it stands beside (uv run tools/gi-model/h4_record_check.py DUMP ...).

--lean-shade --texel-h4 writes every gather pass's H4 per channel into texel_h4.f32x16 (texel_nodes_lean.comp): per
texel [0..2] each channel's (a, bx, by, bz) in the record's frame (its smooth normal's), [3] the same rays' plain mean
irradiance and their passes, summed over the passes of the slice that last gathered it. Checks, on texels a gather wrote:
  fit      the H4 irradiance at the record's normal (a + bz) against the same rays' plain mean -- the fit's own error
  parts    the plain mean against today's parts (sky + sun x its irradiance), where a gather wrote both last -- the same
           light from the same passes, any gap a pass set apart (a refine's passes after a reset) or a stage that overwrote
  tilt     the H4 shape's tangent slope (bx, by over a + bz, luminance) against today's detail tilt (texel.glsl's
           packing): one direction, the same signs where both are strong
"""
import json
import sys
from pathlib import Path

import numpy as np

LUM = np.array([.2126, .7152, .0722])


def u9(w):
    w = w.astype(np.int64)
    sc = np.exp2((w >> 27) - 24.0)
    return np.stack([(w & 511) * sc, ((w >> 9) & 511) * sc, ((w >> 18) & 511) * sc], -1)


def main():
    for dump in [a for a in sys.argv[1:] if not a.startswith('--')]:
        d = sorted(Path(dump).glob('view-*'))[-1]
        m = json.loads((d / 'meta.json').read_text())
        n = m['texelCount']
        h4 = np.frombuffer((d / 'texel_h4.f32x16').read_bytes(), np.float32).reshape(-1, 4, 4)[:n].astype(float)
        t = np.frombuffer((d / 'texels.f32x8').read_bytes(), np.float32).reshape(-1, 8)[:n]
        live = np.abs(t[:, 3]) >= 2
        passes = h4[:, 3, 3]
        got = live & (passes > 0)
        prov = np.frombuffer((d / 'provenance.u8').read_bytes(), np.uint8)[:n]
        sky_code, sun_code = prov >> 4, prov & 15
        gathered = got & np.isin(sky_code, (1, 2, 3, 4)) & np.isin(sun_code, (1, 2, 3, 4))
        print(f'\n== {d.parent.name}: {n:,} texels, {live.sum():,} live; with H4 {got.sum():,} '
              f'({got.sum() / max(live.sum(), 1):.0%}); last written by a gather, both parts: {gathered.sum():,}; '
              f'passes median {np.median(passes[got]):.0f}')
        coef = h4[:, :3, :] / np.maximum(passes, 1)[:, None, None]          # [texel][channel](a, bx, by, bz)
        plain = h4[:, 3, :3] / np.maximum(passes, 1)[:, None]
        e_fit = coef[:, :, 0] + coef[:, :, 3]                                # H4 at the record's normal, rgb
        lp, lf = plain @ LUM, e_fit @ LUM
        q = got & (lp > 1e-7)
        r = lf[q] / lp[q]
        print(f'  fit:   H4 at the normal / the plain mean, luminance: median {np.median(r):.4f}, p10 {np.percentile(r, 10):.4f}, '
              f'p90 {np.percentile(r, 90):.4f}; worst channel |ratio - 1| median '
              f'{np.median(np.abs(e_fit[q] / np.maximum(plain[q], 1e-12) - 1).max(1)):.4f}')
        sky = u9(np.frombuffer((d / 'sky.u32x2').read_bytes(), np.uint32).reshape(n, 2)[:, 0])
        sun = u9(np.frombuffer((d / 'sun.u32x2').read_bytes(), np.uint32).reshape(n, 2)[:, 0])
        irr = np.array(m['sunIrradiance'], float)
        parts = sky + sun * irr[None]
        q = gathered & (lp > 1e-7)
        r = (parts[q] @ LUM) / lp[q]
        print(f'  parts: today\'s sky + sun / the plain mean, luminance: median {np.median(r):.4f}, p10 {np.percentile(r, 10):.4f}, '
              f'p90 {np.percentile(r, 90):.4f}; within 1%: {np.mean(np.abs(r - 1) < 0.01):.0%}, within 5%: {np.mean(np.abs(r - 1) < 0.05):.0%}')
        det = np.frombuffer((d / 'detail.u32').read_bytes(), np.uint32)[:n].astype(np.int64)
        present = (det >> 24) == 255
        tx = ((det & 255).astype(np.int8 if False else np.int64))
        tx = np.where(tx > 127, tx - 256, tx) / 127.0 * 2.0
        ty = ((det >> 8) & 255)
        ty = np.where(ty > 127, ty - 256, ty) / 127.0 * 2.0
        lum_a = (coef[:, :, 0] @ LUM) + (coef[:, :, 3] @ LUM)
        hx = (coef[:, :, 1] @ LUM) / np.maximum(lum_a, 1e-12)
        hy = (coef[:, :, 2] @ LUM) / np.maximum(lum_a, 1e-12)
        q = got & present & (lp > 1e-7)
        strong = q & (np.hypot(tx, ty) > 0.2) & (np.hypot(hx, hy) > 0.2)
        cosang = (tx * hx + ty * hy)[strong] / (np.hypot(tx, ty)[strong] * np.hypot(hx, hy)[strong])
        corr = np.corrcoef(np.r_[tx[q], ty[q]], np.r_[hx[q], hy[q]])[0, 1] if q.sum() > 10 else float('nan')
        print(f'  tilt:  today\'s tilt against H4\'s slope over {q.sum():,} texels with both: correlation {corr:.3f}; '
              f'where both are strong ({strong.sum():,}): cos of the angle between median {np.median(cosang):.3f}, '
              f'opposed {np.mean(cosang < 0):.1%}; slope magnitude H4 / tilt median {np.median(np.hypot(hx, hy)[strong] / np.hypot(tx, ty)[strong]):.2f}')


if __name__ == '__main__':
    main()
