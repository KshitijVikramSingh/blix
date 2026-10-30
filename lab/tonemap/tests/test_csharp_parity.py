"""The demo's C# adaptation and meter against adaptation.py: the second copy of the model, held to the first.

Both entry points run without a window (`--adaptation-selftest`, `--meter-selftest` exit before one is made), so this needs
only the demo built in Release (`dotnet build src/Demos/Blix.Demos.TonemapLab -c Release`); it skips otherwise.
"""
import json
import os
import subprocess
from pathlib import Path

import numpy as np
import pytest

from tmlab import adaptation as ad
from tmlab import capture, exposure

LAB = Path(__file__).resolve().parents[1]
REPO = LAB.parents[1]
BUILT = (REPO / "src/Demos/Blix.Demos.TonemapLab/bin/Release/net8.0/Blix.Demos.TonemapLab.dll").exists()


def run_demo(*args: str):
    done = subprocess.run([str(REPO / "blix"), "run", "Blix.Demos.TonemapLab", *args], cwd=REPO,
                          env={**os.environ, "BLIX_CONFIG": "Release"}, capture_output=True, text=True, timeout=300)
    assert done.returncode == 0, done.stdout[-800:] + done.stderr[-800:]


pytestmark = pytest.mark.skipif(not BUILT, reason="the TonemapLab demo is not built in Release")

SCENES = {
    "tunnel": (0.05, 800, lambda t: 0.0 if t < 10.0 else (-8.0 if t < 18.0 else 0.0)),
    "deep_night": (2.0, 1800, lambda t: 0.0 if t < 10.0 else -20.0),
}


def test_adaptation_steps_match_python(tmp_path):
    out = tmp_path / "adaptation.json"
    run_demo("--adaptation-selftest", str(out))
    got = json.loads(out.read_text())
    for name, (dt, n, level) in SCENES.items():
        S, A = ad.simulate(np.array([level(i * dt) for i in range(n)]), dt, ad.Adaptation())
        assert np.max(np.abs(np.array(got[name]["S"]) - S)) < 1e-6, name
        assert np.max(np.abs(np.array(got[name]["A"]) - A)) < 1e-6, name
    # and the 12 EV span really is in the C# too: a 20 EV drop leaves the short-term level at 12 EV below the start
    assert min(got["deep_night"]["S"]) == pytest.approx(-12.0, abs=1e-6)


@pytest.mark.skipif(not (LAB / "data/metering").exists(), reason="needs the captured metering set")
@pytest.mark.parametrize("frame", ["night_lamps__night__z1.7", "materials_grid__bright__z1.7", "emissive_ramp__dim__z1"])
def test_trimmed_meter_matches_python(tmp_path, frame):
    out = tmp_path / "meter.txt"
    run_demo("--meter-selftest", str(LAB / "data/metering" / f"{frame}.pfm"), "--meter-out", str(out))
    got = dict(tuple(map(float, line.split())) for line in out.read_text().splitlines())
    hist = exposure.log_histogram(capture.load(LAB / "data/metering" / f"{frame}.pfm").rgb)
    for trim, value in got.items():
        assert value == pytest.approx(ad.meter_ev(hist, trim), abs=1e-6), (frame, trim)
