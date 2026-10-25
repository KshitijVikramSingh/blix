"""The demo's HDRI loader against hdri.py: two Radiance decoders (stb in C#, hdri.py's own) and two solid-angle meters must agree.

Runs the windowless `--hdri-selftest` of the Release-built demo; skips if it, or the fetched HDRIs, are missing.
"""
import os
import subprocess
from pathlib import Path

import numpy as np
import pytest

from tmlab import hdri

LAB = Path(__file__).resolve().parents[1]
REPO = LAB.parents[1]
BUILT = (REPO / "src/Demos/Blix.Demos.TonemapLab/bin/Release/net8.0/Blix.Demos.TonemapLab.dll").exists()
FILES = {p.stem.removesuffix("_2k"): p for p in (LAB / "data/hdri").glob("*_2k.hdr")}

pytestmark = pytest.mark.skipif(not BUILT or not FILES, reason="needs the Release demo build and tools/fetch_hdris.py's data")


@pytest.mark.parametrize("name", [n for n in ("cannon", "moonless_golf", "studio_small_09") if n in FILES])
def test_loader_and_meter_match_python(tmp_path, name):
    out = tmp_path / "hdri.txt"
    done = subprocess.run([str(REPO / "blix"), "run", "Blix.Demos.TonemapLab", "--hdri-selftest", str(FILES[name]), "--hdri-out", str(out)],
                          cwd=REPO, env={**os.environ, "BLIX_CONFIG": "Release"}, capture_output=True, text=True, timeout=300)
    assert done.returncode == 0, done.stdout[-800:] + done.stderr[-800:]
    rgb = hdri.read_hdr(FILES[name])
    for line in out.read_text().splitlines():
        key, *v = line.split()
        if key == "size":
            assert (int(v[0]), int(v[1])) == (rgb.shape[1], rgb.shape[0])
        elif key == "meter":
            assert float(v[0]) == pytest.approx(hdri.meter_ev(rgb.astype(np.float64)), abs=1e-5)
        elif key == "pixel":
            x, y = int(v[0]), int(v[1])
            assert np.allclose(np.array(v[2:], dtype=np.float64), rgb[y, x], rtol=1e-6, atol=1e-9), (x, y)
