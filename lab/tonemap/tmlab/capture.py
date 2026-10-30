"""Load Blix HDR captures: a PFM of raw scene radiance plus its .pfm.json (written under BLIX_SHOT_HDR=1).

The radiance is linear, before exposure and tonemap, in Blix's scene units, and Rec.709 primaries (the sidecar says
"assumed": nothing in the engine states it, but albedo comes from sRGB textures and the PNG path encodes with the
sRGB OETF). The exposure the engine WOULD have applied is in the sidecar, so a lab run can reproduce the PNG.
"""
from __future__ import annotations

import json
from dataclasses import dataclass
from pathlib import Path

import numpy as np


@dataclass(frozen=True)
class Capture:
    name: str
    rgb: np.ndarray  # (h, w, 3) float32, linear Rec.709, scene units, top row first
    meta: dict

    @property
    def exposure(self) -> float:
        return float(self.meta.get("exposure", 1.0))

    def luminance(self) -> np.ndarray:
        return self.rgb @ np.array([0.2126, 0.7152, 0.0722], dtype=np.float32)

    def downsampled(self, factor: int) -> "Capture":
        """Box-average by an integer factor: fitting does not need 3.7 megapixels, and averaging keeps radiance honest."""
        h, w, _ = self.rgb.shape
        h2, w2 = h // factor, w // factor
        small = self.rgb[: h2 * factor, : w2 * factor].reshape(h2, factor, w2, factor, 3).mean(axis=(1, 3))
        return Capture(self.name, small, self.meta)


def read_pfm(path: str | Path) -> np.ndarray:
    """RGB PFM -> (h, w, 3) float32, top row first."""
    with open(path, "rb") as f:
        if f.readline().strip() != b"PF":
            raise ValueError(f"{path}: not an RGB PFM")
        w, h = (int(v) for v in f.readline().split())
        scale = float(f.readline())
        data = np.frombuffer(f.read(), dtype="<f4" if scale < 0 else ">f4")
    if data.size != w * h * 3:
        raise ValueError(f"{path}: expected {w * h * 3} floats, found {data.size}")
    return np.flipud(data.reshape(h, w, 3)).astype(np.float32)  # PFM stores the bottom row first


def write_pfm(path: str | Path, rgb: np.ndarray, meta: dict | None = None) -> None:
    """The writer for what `read_pfm` reads (and Blix.Graphics.Images.PfmWriter writes): little-endian, bottom row first."""
    h, w, _ = rgb.shape
    path = Path(path)
    path.write_bytes(f"PF\n{w} {h}\n-1.0\n".encode() + np.flipud(rgb).astype("<f4").tobytes())
    if meta is not None:
        path.with_suffix(".pfm.json").write_text(json.dumps(meta))


def load(pfm_path: str | Path) -> Capture:
    pfm_path = Path(pfm_path)
    sidecar = pfm_path.with_suffix(".pfm.json")
    meta = json.loads(sidecar.read_text()) if sidecar.exists() else {}
    return Capture(pfm_path.stem, read_pfm(pfm_path), meta)


def summary(cap: Capture) -> dict:
    """What a tonemapper sees: the range and where the pixels sit in it, in stops from mid grey (0.18)."""
    y = cap.luminance()
    y = y[y > 0]
    stops = np.log2(y / 0.18)
    pct = np.percentile(y, [1, 50, 99, 99.9])
    return {
        "name": cap.name, "size": cap.rgb.shape[1::-1], "exposure": cap.exposure,
        "lum_p1": float(pct[0]), "lum_median": float(pct[1]), "lum_p99": float(pct[2]), "lum_p99_9": float(pct[3]),
        "lum_max": float(y.max()), "range_stops_p1_p99_9": float(np.log2(pct[3] / pct[0])),
        "stops_median": float(np.median(stops)),
        "clipped_at_1_pct": float(100.0 * np.mean(cap.luminance() > 1.0)),
        "nan_or_inf": int(np.count_nonzero(~np.isfinite(cap.rgb))),
    }
