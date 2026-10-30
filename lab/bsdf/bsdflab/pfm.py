"""A minimal reader for the RGB PFM files the demos write (three little-endian floats a pixel, bottom row first)."""
from pathlib import Path

import numpy as np


def read_pfm(path) -> np.ndarray:
    """(h, w, 3) float32, top row first."""
    data = Path(path).read_bytes()
    pos = 0
    fields = []
    while len(fields) < 4:
        end = data.index(b"\n", pos)
        fields += data[pos:end].split()
        pos = end + 1
    kind, w, h, scale = fields[0], int(fields[1]), int(fields[2]), float(fields[3])
    assert kind == b"PF", f"{path}: not an RGB PFM"
    dtype = "<f4" if scale < 0 else ">f4"
    return np.frombuffer(data, dtype=dtype, count=w * h * 3, offset=pos).reshape(h, w, 3)[::-1].astype(np.float32)
