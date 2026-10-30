"""Radiance .hdr (RGBE) environment maps, and what they say about a scene.

    uv run python -m tmlab.hdri

The decoder follows stb_image's conversion (value = mantissa * 2^(exponent - 136), no +0.5), which is what the engine's
ImageLoader uses, so the lab and the demo read the same numbers. Equirectangular images over-sample the poles, so every
statistic here is weighted by solid angle (cos of the latitude of the row).

The assets are Poly Haven's, CC0, listed in sources/hdris.json with their checksums; tools/fetch_hdris.py fetches them.
"""
from __future__ import annotations

import json
from dataclasses import dataclass
from pathlib import Path

import numpy as np

from . import exposure

LAB = Path(__file__).resolve().parents[1]
MANIFEST = LAB / "sources" / "hdris.json"
DIR = LAB / "data" / "hdri"
LUMA709 = np.array([0.2126, 0.7152, 0.0722])


def read_hdr(path: str | Path) -> np.ndarray:
    """(h, w, 3) float32, top row first, as the file stores it. Handles flat and new-style run-length scanlines."""
    data = Path(path).read_bytes()
    pos = 0

    def line() -> bytes:
        nonlocal pos
        end = data.index(b"\n", pos)
        out = data[pos:end]
        pos = end + 1
        return out

    if not line().startswith(b"#?"):
        raise ValueError(f"{path}: not a Radiance file")
    while line().strip():  # the header ends at a blank line
        pass
    res = line().split()
    if len(res) != 4 or res[0] not in (b"-Y", b"+Y") or res[2] not in (b"+X", b"-X"):
        raise ValueError(f"{path}: unsupported resolution line {res!r}")
    h, w = int(res[1]), int(res[3])

    out = np.empty((h, w, 3), dtype=np.float32)
    scan = np.empty((w, 4), dtype=np.uint8)
    for y in range(h):
        if 8 <= w < 32768 and data[pos] == 2 and data[pos + 1] == 2 and (data[pos + 2] << 8 | data[pos + 3]) == w:
            pos += 4
            for c in range(4):
                x = 0
                while x < w:
                    count = data[pos]
                    pos += 1
                    if count > 128:
                        count -= 128
                        scan[x:x + count, c] = data[pos]
                        pos += 1
                    else:
                        scan[x:x + count, c] = np.frombuffer(data, dtype=np.uint8, count=count, offset=pos)
                        pos += count
                    x += count
        else:
            scan[:] = np.frombuffer(data, dtype=np.uint8, count=4 * w, offset=pos).reshape(w, 4)
            pos += 4 * w
        e = scan[:, 3].astype(np.int32)
        scale = np.where(e > 0, np.ldexp(1.0, e - 136), 0.0)
        out[y] = scan[:, :3] * scale[:, None]
    return out


def solid_angle_weights(h: int, w: int) -> np.ndarray:
    """cos(latitude) per pixel, normalised to sum to 1, for an equirectangular image."""
    lat = np.pi / 2.0 - np.pi * (np.arange(h) + 0.5) / h
    weights = np.repeat(np.cos(lat)[:, None], w, axis=1)
    return weights / weights.sum()


def weighted_quantile(values: np.ndarray, weights: np.ndarray, q) -> np.ndarray:
    order = np.argsort(values)
    cum = np.cumsum(weights[order])
    return np.interp(q, cum / cum[-1], values[order])


def weighted_histogram(rgb: np.ndarray) -> np.ndarray:
    """Solid-angle-weighted, normalised log2-luminance histogram on exposure.EDGES: exposure.log_histogram for an equirect image."""
    h, w, _ = rgb.shape
    y = np.clip(rgb.reshape(-1, 3).astype(np.float64) @ LUMA709, 2.0 ** -14, 2.0 ** 10)
    counts, _ = np.histogram(np.log2(y), bins=exposure.EDGES, weights=solid_angle_weights(h, w).ravel())
    return counts / counts.sum()


def meter_ev(rgb: np.ndarray, trim: float = 0.09) -> float:
    """The trimmed log-average the auto-exposure meters, of an equirect image (brightest `trim` of the solid angle ignored)."""
    from .adaptation import meter_ev as trimmed
    return trimmed(weighted_histogram(rgb), trim)


@dataclass(frozen=True)
class HdriStats:
    id: str
    median: float
    p99_9: float
    maximum: float
    range_stops: float      # p0.1 to p99.9 of luminance, solid-angle weighted
    meter_ev: float         # the trimmed log-average the auto-exposure meters (log2 luminance, brightest 9% ignored)
    rg: float               # luminance-weighted mean R / G
    bg: float               # and B / G: the colour cast, 1.0 = neutral in linear sRGB


def stats(path: str | Path, trim: float = 0.09) -> HdriStats:
    rgb = read_hdr(path).astype(np.float64)
    h, w, _ = rgb.shape
    wt = solid_angle_weights(h, w).ravel()
    flat = rgb.reshape(-1, 3)
    y = np.maximum(flat @ LUMA709, 1e-6)
    lo, med, hi99, hi = weighted_quantile(y, wt, [0.001, 0.5, 0.999, 1.0])
    meter = meter_ev(rgb, trim)   # the histogram meter the demo uses, so the two can be compared
    lum_w = wt * y
    mean = (flat * lum_w[:, None]).sum(axis=0) / lum_w.sum()
    return HdriStats(Path(path).stem.removesuffix("_2k"), float(med), float(hi99), float(y.max()), float(np.log2(hi99 / lo)),
                     meter, float(mean[0] / mean[1]), float(mean[2] / mean[1]))


def manifest() -> list[dict]:
    return json.loads(MANIFEST.read_text())["hdris"]


def main():
    print(f"{'id':20s} {'label K':>7s} {'evs':>4s} | {'median':>8s} {'p99.9':>9s} {'max':>10s} {'range(st)':>9s} {'meter EV':>8s} | {'R/G':>5s} {'B/G':>5s}")
    for it in manifest():
        s = stats(DIR / f"{it['id']}_2k.hdr")
        print(f"{it['id']:20s} {it['whitebalance_k']!s:>7} {it['evs_cap']:>4} | {s.median:8.3f} {s.p99_9:9.1f} {s.maximum:10.0f} {s.range_stops:9.1f} "
              f"{s.meter_ev:8.2f} | {s.rg:5.2f} {s.bg:5.2f}")


if __name__ == "__main__":
    main()
