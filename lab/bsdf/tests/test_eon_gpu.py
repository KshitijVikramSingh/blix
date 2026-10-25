"""The shared GLSL (src/Blix.Shaders/diffuse_eon.glsl) against the python reference, per pixel.

The TonemapLab demo's pattern 4 renders pi * f_EON for a table of angles and roughnesses through the GLSL (`--pattern 4`, raw: no exposure and
no tone map); this recomputes the table in python. Needs the demo built in Release; skips otherwise. The renders are RGBA16F, so agreement is
to a half-float step.
"""
import os
import subprocess
from pathlib import Path

import numpy as np
import pytest

from bsdflab import eon
from bsdflab.pfm import read_pfm

LAB = Path(__file__).resolve().parents[1]
REPO = LAB.parents[1]
BUILT = (REPO / "src/Demos/Blix.Demos.TonemapLab/bin/Release/net8.0/Blix.Demos.TonemapLab.dll").exists()
pytestmark = pytest.mark.skipif(not BUILT, reason="the TonemapLab demo is not built in Release")

RHO = np.array([0.2, 0.5, 0.9])
ROUGHNESS = [0.0, 0.25, 0.5, 0.75, 1.0]
MU_O = [0.1, 0.5, 0.9]
PHI = [0.0, np.pi / 2, np.pi]


@pytest.fixture(scope="module")
def render(tmp_path_factory):
    out = tmp_path_factory.mktemp("eon") / "table"
    env = {k: v for k, v in os.environ.items() if k != "BLIX_HDR"}
    done = subprocess.run([str(REPO / "blix"), "run", "Blix.Demos.TonemapLab", "--pattern", "4", "--exposure", "0", "--capture", str(out),
                           "--capture-frame", "8"], cwd=REPO, env={**env, "BLIX_CONFIG": "Release"}, capture_output=True, text=True, timeout=300)
    assert done.returncode == 0, done.stdout[-600:] + done.stderr[-600:]
    return read_pfm(out.with_suffix(".pfm"))


def expected(h, w, stride):
    ys, xs = np.mgrid[0:h:stride, 0:w:stride]
    u, v = (xs + 0.5) / w, (ys + 0.5) / h
    col = np.floor(np.clip(u, 0, 0.99999) * 256)
    mu_i = 0.02 + 0.98 * col / 255.0
    row = np.clip((v * 45).astype(int), 0, 44)
    ri, rest = row // 9, row % 9
    mi, pi_ = rest // 3, rest % 3
    out = np.zeros(ys.shape + (3,))
    for k in range(ys.size):
        idx = np.unravel_index(k, ys.shape)
        r, mo, phi, mui = ROUGHNESS[ri[idx]], MU_O[mi[idx]], PHI[pi_[idx]], mu_i[idx]
        sin_i, sin_o = np.sqrt(max(1 - mui * mui, 0)), np.sqrt(1 - mo * mo)
        wi = np.array([sin_i * np.cos(phi), sin_i * np.sin(phi), mui])
        wo = np.array([sin_o, 0.0, mo])
        out[idx] = np.pi * eon.f_eon(RHO, r, wi, wo)
    return out


def test_glsl_matches_python_per_pixel(render):
    h, w, _ = render.shape
    stride = 41
    want = expected(h, w, stride)
    got = render[::stride, ::stride].astype(np.float64)
    err = np.abs(got - want)
    assert np.all(err <= np.maximum(2e-3 * np.abs(want), 2e-3)), f"worst {err.max():.5f} of values up to {want.max():.3f}"
    assert want.max() > 1.0    # the table does reach the retroreflective, rough regime where EON differs from Lambert's constant 1


def test_the_r_zero_rows_are_lambert(render):
    h, w, _ = render.shape
    rows = render[: int(h * 9 / 45) - 2:7, ::97]       # the first nine rows are r = 0
    assert np.allclose(rows, RHO, atol=2e-3)
