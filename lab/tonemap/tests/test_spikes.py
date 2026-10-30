"""Aperture-blade spikes: the polygon PSF has the structure spikes.py claims, and the fit constants are what the demo uses."""
import json
import re
from pathlib import Path

import numpy as np
import pytest

from tmlab import spikes as sp

FIT = json.loads((sp.RESULTS / "spike_fit.json").read_text())
DEMO = Path(__file__).resolve().parents[3] / "src/Demos/Blix.Demos.TonemapLab"


@pytest.fixture(scope="module", params=[5, 6, 7, 8])
def measured(request):
    return sp.measure(request.param)


def test_spike_count_is_n_for_even_and_2n_for_odd(measured):
    n = measured["blades"]
    assert measured["spikes"] == (n if n % 2 == 0 else 2 * n)


def test_spikes_point_along_the_edge_normals(measured):
    n = measured["blades"]
    base = np.degrees(np.pi / n + 2 * np.pi * np.arange(n) / n)
    normals = np.concatenate([base, base + 180.0]) % 360.0   # the edge normals and their reverses
    for a in measured["angles_deg"]:
        d = np.abs(((a - normals) + 180.0) % 360.0 - 180.0).min()
        assert d < 1.5, (n, a)


def test_spikes_fall_as_r_minus_2(measured):
    assert measured["slope"] == pytest.approx(-2.0, abs=0.25)


def test_total_spike_amplitude_is_the_same_whatever_the_blade_count():
    totals = [r["amplitude_total"] for r in FIT["rows"]]
    assert max(totals) / min(totals) < 1.25
    assert FIT["amplitude_total_mean"] == pytest.approx(0.17, abs=0.02)


def test_demo_uses_the_fitted_constants():
    src = (DEMO / "Glare.cs").read_text()
    amp = float(re.search(r"SpikeAmplitudeTotal = ([0-9.]+)f", src).group(1))
    knee = float(re.search(r"SpikeKnee = ([0-9.]+)f", src).group(1))
    assert amp == pytest.approx(FIT["amplitude_total_mean"], abs=5e-4)
    assert knee == pytest.approx(FIT["knee_airy_radii"])
