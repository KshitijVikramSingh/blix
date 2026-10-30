"""The glare as the GPU actually renders it, against the python model: a single bright pixel in, the PSF out.

A fit having normalised weights does not by itself guarantee that the mipmapped, truncated, zero-padded implementation keeps the
same energy distribution, so this measures it. `--impulse-x/-y/-v` makes the scene black but for one pixel and `--probe` presents
the scene-linear composite untouched; the capture is the impulse response (RGBA16F, V = 4000 scene units). Needs the Release demo.
"""
import json
import os
import subprocess
from pathlib import Path

import numpy as np
import pytest
from scipy.special import ndtr

from tmlab import capture

LAB = Path(__file__).resolve().parents[1]
REPO = LAB.parents[1]
BUILT = (REPO / "src/Demos/Blix.Demos.TonemapLab/bin/Release/net8.0/Blix.Demos.TonemapLab.dll").exists()
pytestmark = pytest.mark.skipif(not BUILT, reason="the TonemapLab demo is not built in Release")

V = 4000.0
GLARE = json.loads((LAB / "results/glare_fit.json").read_text())
SCATTER = json.loads((LAB / "results/scatter_fit.json").read_text())
SPIKES = json.loads((LAB / "results/spike_fit.json").read_text())
W_D, S_D = np.array(GLARE["weights"]), np.array(GLARE["sigmas_in_airy_radii"])
W_S, S_S = np.array(SCATTER["weights"]), np.array(SCATTER["sigmas_deg"])
PX_PER_DEG = 1440 / 70.0   # the demo's default 1280x720 logical window is a 2560x1440 framebuffer at 70 degrees vertical
CASES = {
    "c2": ["--glare", "1", "--scatter", "0", "--spikes", "0", "--airy", "2", "--impulse-x", "1280", "--impulse-y", "720"],
    "e2": ["--glare", "1", "--scatter", "0", "--spikes", "0", "--airy", "2", "--impulse-x", "30", "--impulse-y", "720"],
    "k2": ["--glare", "1", "--scatter", "0", "--spikes", "0", "--airy", "2", "--impulse-x", "30", "--impulse-y", "30"],
    "c4": ["--glare", "1", "--scatter", "0", "--spikes", "0", "--airy", "4", "--impulse-x", "1280", "--impulse-y", "720"],
    "sc": ["--glare", "0", "--scatter", "4", "--spikes", "0", "--impulse-x", "1280", "--impulse-y", "720"],   # 4 is the slider's limit
    "sp": ["--glare", "0", "--scatter", "0", "--spikes", "1", "--blades", "6", "--airy", "2", "--impulse-x", "1280", "--impulse-y", "720"],
}


@pytest.fixture(scope="module")
def images(tmp_path_factory):
    out = tmp_path_factory.mktemp("impulse")
    got = {}
    for name, extra in CASES.items():
        done = subprocess.run([str(REPO / "blix"), "run", "Blix.Demos.TonemapLab", "--gt7", "published", "--probe", "--glare-scale", "1",
                               "--sun-boost", "1", *extra, "--capture", str(out / name), "--capture-frame", "8"],
                              cwd=REPO, env={**os.environ, "BLIX_CONFIG": "Release"}, capture_output=True, text=True, timeout=300)
        assert done.returncode == 0, done.stdout[-600:] + done.stderr[-600:]
        got[name] = capture.load(out / f"{name}.pfm").rgb[..., 1].astype(np.float64)
    return got


def peak_and_radii(img):
    y0, x0 = np.unravel_index(np.argmax(img), img.shape)
    yy, xx = np.mgrid[:img.shape[0], :img.shape[1]]
    return x0, y0, np.hypot(xx - x0, yy - y0)


def diffraction_model(r_px, airy):
    sig = S_D * airy
    identity = sig[0] < 0.9    # the demo draws diffraction term 0 as the scene itself while it is under a pixel wide
    core = W_D[0] if identity else W_D[0] * (1 - np.exp(-r_px ** 2 / (2 * sig[0] ** 2)))
    return core + sum(w * (1 - np.exp(-r_px ** 2 / (2 * s ** 2))) for w, s in zip(W_D[1:], sig[1:]))


@pytest.mark.parametrize("name,airy", [("c2", 2), ("c4", 4)])
def test_diffraction_encircled_energy_matches_the_model(images, name, airy):
    img = images[name]
    _, _, r = peak_and_radii(img)
    assert img.sum() / V == pytest.approx(1.0, abs=0.005)       # energy conserved in the middle of the frame
    for R in (6, 12, 24, 48, 96, 200):
        assert img[r <= R].sum() / V == pytest.approx(diffraction_model(R, airy), rel=0.015), R


@pytest.mark.parametrize("name", ["c2", "e2", "k2"])
def test_energy_near_edges_follows_zero_padding(images, name):
    """Light from outside the frame is absent (zero padding), so a source at the edge keeps only the halo that falls inside."""
    img = images[name]
    x0, y0, _ = peak_and_radii(img)
    h, w = img.shape
    sig = S_D * 2

    def inside(s):
        return (ndtr((w - x0 - 0.5) / s) - ndtr((-x0 - 0.5) / s)) * (ndtr((h - y0 - 0.5) / s) - ndtr((-y0 - 0.5) / s))
    predicted = W_D[0] + sum(wi * inside(s) for wi, s in zip(W_D[1:], sig[1:]))   # term 0 is the scene at this Airy radius
    assert img.sum() / V == pytest.approx(predicted, abs=0.004)
    assert img.sum() / V < 1.0 + 0.002                            # and never more than went in: nothing is invented at an edge


def test_scatter_veil_matches_the_model(images):
    img = images["sc"]
    x0, y0, r = peak_and_radii(img)
    f = 4 * 0.0662     # the amount the demo applies at its slider limit
    sig = S_S * (img.shape[0] / 70.0)      # pixels per degree from the frame actually rendered (70 degrees vertical), not an assumed 1440 rows
    assert img.sum() / V == pytest.approx(1.0, abs=0.005)
    for R in (60, 120, 240, 480, 900):
        measured = (img[r <= R].sum() / V - (1 - f)) / f
        model = sum(w * (1 - np.exp(-R ** 2 / (2 * s ** 2))) for w, s in zip(W_S, sig))
        assert measured == pytest.approx(model, rel=0.06), R


def test_spike_line_density_matches_the_fit(images):
    """Row sums across a spike at distance d from the source follow V * A * a / d^2 (A = the per-spike amplitude, a the Airy radius in px)."""
    img = images["sp"]
    x0, y0, _ = peak_and_radii(img)
    a_per_spike = SPIKES["amplitude_total_mean"] / 6
    for d in (32, 64, 100):
        above = img[y0 + d - 1:y0 + d + 1, x0 - 24:x0 + 25].mean(axis=0).sum()
        below = img[y0 - d - 1:y0 - d + 1, x0 - 24:x0 + 25].mean(axis=0).sum()
        model = V * a_per_spike * 2.0 / d ** 2
        assert 0.5 * (above + below) == pytest.approx(model, rel=0.2), d
