"""The demo's C# auto white balance against awb.py: the second copy of the model, held to the first (see test_csharp_parity.py)."""
import os
import subprocess
from pathlib import Path

import numpy as np
import pytest

from tmlab import awb, capture

LAB = Path(__file__).resolve().parents[1]
REPO = LAB.parents[1]
BUILT = (REPO / "src/Demos/Blix.Demos.TonemapLab/bin/Release/net8.0/Blix.Demos.TonemapLab.dll").exists()
pytestmark = pytest.mark.skipif(not BUILT or not (LAB / "data/metering").exists(), reason="needs the Release demo build and the captured metering set")


@pytest.mark.parametrize("frame", ["night_lamps__night__z1.7", "materials_grid__bright__z1.7", "emissive_ramp__dim__z1"])
def test_measure_cast_and_state_match_python(tmp_path, frame):
    path = LAB / "data/metering" / f"{frame}.pfm"
    out = tmp_path / "awb.txt"
    done = subprocess.run([str(REPO / "blix"), "run", "Blix.Demos.TonemapLab", "--awb-selftest", str(path), "--awb-out", str(out)],
                          cwd=REPO, env={**os.environ, "BLIX_CONFIG": "Release"}, capture_output=True, text=True, timeout=300)
    assert done.returncode == 0, done.stdout[-800:] + done.stderr[-800:]
    lines, states = {}, []
    for line in out.read_text().splitlines():
        key, *v = line.split()
        if key == "state": states.append([float(x) for x in v])
        else: lines[key] = [float(x) for x in v]
    p = awb.Awb()
    mean = awb.measure_mean(capture.load(path).rgb, p)
    assert lines["mean"] == pytest.approx(mean, rel=1e-6)
    cast = awb.cast_of(mean, p)
    assert lines["cast"] == pytest.approx(cast, rel=1e-6, abs=1e-9)
    for ev, mired, tint, *g in states:
        s = awb.State(p)
        for _ in range(100): s.step(0.1, cast, ev)
        # the C# constants are floats (0.15f is not 0.15), so the two agree to about 1e-8 relative, not to rounding
        assert mired == pytest.approx(s.mired, rel=1e-6) and tint == pytest.approx(s.tint, abs=1e-6)
        assert g == pytest.approx(list(s.gains()), rel=1e-5)
