"""The glare fit: a Gaussian sum against the Airy pattern's ring-averaged envelope (see glare.py)."""
import json

import numpy as np
import pytest

from tmlab import glare

FIT = json.loads((glare.RESULTS / "glare_fit.json").read_text())
W, S = np.array(FIT["weights"]), np.array(FIT["sigmas_in_airy_radii"])


def test_airy_energy_and_encircled_energy():
    x = np.linspace(1e-6, 4000.0, 4_000_001)
    energy = np.trapezoid(2 * np.pi * x * glare.airy(x), x)   # tends to 4 pi (minus the tail): J1 pattern, unit peak
    assert energy == pytest.approx(4.0 * np.pi, rel=2e-3)
    assert glare.encircled_energy(1.0) == pytest.approx(0.8378, abs=2e-3)   # 83.8% inside the first dark ring
    assert glare.encircled_energy(1e4) == pytest.approx(1.0, abs=1e-3)


def test_envelope_integrates_to_one_and_falls_as_r_minus_3():
    r = np.geomspace(0.02, 5000.0, 20000)
    total = np.trapezoid(2 * np.pi * r * glare.airy_ring_average(r), r)
    assert total == pytest.approx(1.0, abs=0.05)   # the window average smears the core a little: it is an envelope, not the pattern
    a, b = 30.0, 300.0
    slope = np.log10(glare.airy_ring_average(b) / glare.airy_ring_average(a)) / np.log10(b / a)
    assert slope == pytest.approx(-3.0, abs=0.05)


def test_saved_fit_is_normalised_and_accurate():
    assert W.sum() == pytest.approx(1.0, abs=1e-9)
    assert np.all(np.diff(S) > 0)
    assert FIT["max_abs_log10_error"] < 0.25      # within a factor 1.8 everywhere from 0.05 to 400 Airy radii
    assert FIT["max_encircled_energy_error"] < 0.02
    r = glare.GRID
    err = np.log10(glare.gaussian_sum(r, W, S)) - np.log10(glare.airy_ring_average(r))
    assert np.abs(err).max() == pytest.approx(FIT["max_abs_log10_error"], abs=1e-6)


def test_more_terms_fit_no_worse():
    errs = [glare.report(*glare.fit(n, starts=8))["max_abs_log10_error"] for n in (3, 4, 5)]
    assert errs[0] > errs[1] > errs[2]


def test_gaussian_sum_has_unit_energy():
    r = np.geomspace(1e-3, 3000.0, 20000)
    assert np.trapezoid(2 * np.pi * r * glare.gaussian_sum(r, W, S), r) == pytest.approx(1.0, abs=1e-3)
