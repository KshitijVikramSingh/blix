"""The demo's GT7 shader against gt7.py: the same hue ramp through both, compared per pixel.

The GLSL (src/Blix.Shaders/gt7.glsl, shared with the engine) is a third copy of arithmetic that already exists in the
published C++ sample and in gt7.py, so, like Tonemap.cs against tonemap.glsl, it has to answer to something. `--selftest`
renders the ramp to frame-buffer values in an RGBA16F target; this reads the result and recomputes it.

The renders are made by the demo, which opens a window for a few seconds. They are read from data/selftest if present;
BLIX_PARITY=1 renders any that are missing (needs the demo built in Release); otherwise the test skips.
"""
import json
import os
import subprocess
from pathlib import Path

import numpy as np
import pytest

from tmlab import capture, pipeline
from tmlab.fit import _HUES709
from tmlab.gt7 import GT7Params, GT7ToneMapping

LAB = Path(__file__).resolve().parents[1]
REPO = LAB.parents[1]
SELFTEST = LAB / "data" / "selftest"
HUES = np.vstack([_HUES709, [1.0, 1.0, 1.0]])  # the shader's seven rows: six hues, then grey
TUNED = GT7Params(**json.loads((LAB / "results" / "hdr_tuned.json").read_text())["params"])
SETS = {"tuned": TUNED, "published": GT7Params()}


def render(name: str, peak: int) -> Path | None:
    path = SELFTEST / f"glsl_{name}_{peak}.pfm"
    if path.exists():
        return path
    if os.environ.get("BLIX_PARITY") != "1" or not (REPO / "blix").exists():
        return None
    SELFTEST.mkdir(parents=True, exist_ok=True)
    done = subprocess.run([str(REPO / "blix"), "run", "Blix.Demos.TonemapLab", "--selftest", str(path), "--peak", str(peak), "--gt7", name],
                          cwd=REPO, env={**os.environ, "BLIX_CONFIG": "Release"}, capture_output=True, text=True, timeout=300)
    return path if done.returncode == 0 and path.exists() else None


@pytest.mark.parametrize("name", ["tuned", "published"])
@pytest.mark.parametrize("peak", [1000, 4000])
def test_glsl_gt7_matches_python(name, peak):
    path = render(name, peak)
    if path is None:
        pytest.skip("no selftest render (run the demo with --selftest, or set BLIX_PARITY=1)")
    cap = capture.load(path)
    got = cap.rgb[::8, ::8].astype(np.float64)
    h, w, _ = cap.rgb.shape
    ys, xs = np.mgrid[0:h:8, 0:w:8]
    u, v = (xs + 0.5) / w, (ys + 0.5) / h
    row = np.clip((v * 7).astype(int), 0, 6)
    scene = HUES[row] * (10.0 ** (-3.0 + 6.0 * u))[..., None]
    want = GT7ToneMapping.hdr_display(float(peak), SETS[name])(pipeline.to_gt7_input(scene))
    # The target is RGBA16F, so agreement is to a half-float step: about 1e-3 relative, and an absolute floor near zero.
    err = np.abs(got - want)
    assert np.all(err <= np.maximum(0.003 * np.abs(want), 0.002)), f"worst {err.max():.5f}"
