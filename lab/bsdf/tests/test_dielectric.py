"""The refracting interface and the volume: Fresnel's numbers, total internal reflection, the radiance-BTDF's reciprocity, sampling against evaluation, Beer-Lambert."""
import numpy as np
import pytest

from bsdflab import dielectric as d
from bsdflab.dielectric import Interface

RNG = np.random.default_rng(5)


def sphere_dirs(n):
    z = RNG.random(n) * 2 - 1
    phi = RNG.random(n) * 2 * np.pi
    st = np.sqrt(1 - z * z)
    return np.stack([st * np.cos(phi), st * np.sin(phi), z], axis=-1)


def wo_at(cos, inside=False):
    return np.array([np.sqrt(1 - cos * cos), 0.0, -cos if inside else cos])


def test_fresnel_normal_incidence_and_critical_angle():
    assert d.fresnel_exact(np.array(1.0), 1.0, 1.5) == pytest.approx(0.04, abs=1e-6)
    assert d.fresnel_exact(np.array(1.0), 1.5, 1.0) == pytest.approx(0.04, abs=1e-6)
    crit = np.arcsin(1 / 1.5)
    assert d.fresnel_exact(np.array(np.cos(crit) + 1e-4), 1.5, 1.0) > 0.85   # just inside the critical angle it climbs steeply (a square root): most is reflected
    assert d.fresnel_exact(np.array(np.cos(crit - 0.2)), 1.5, 1.0) < 0.5                              # well inside it: refracts
    assert d.fresnel_exact(np.array(0.2), 1.5, 1.0) == 1.0                                            # past it: total internal reflection


def test_a_smooth_interface_reflects_four_percent_and_passes_the_rest_at_normal_incidence():
    i = Interface(alpha=1e-3)
    wo = np.tile(wo_at(0.9999), (60_000, 1))
    wi, w = d.sample(i, wo, RNG)
    up = wi[:, 2] > 0
    assert w[up].sum(axis=0)[0] / len(wo) == pytest.approx(0.04, abs=0.01)
    # Radiance entering glass is brighter by eta^2: the transmitted weight is (1 - F) / eta_in^2 * eta_out^2 ... measured from outside the view side is
    # the OUTSIDE, so a ray from the viewer in air sees light from inside the glass scaled by (eta_out / eta_in)^2.
    assert w[~up].sum(axis=0)[0] / len(wo) == pytest.approx(0.96 * (1.0 / 1.5) ** 2, abs=0.02)


def test_total_internal_reflection_from_inside_at_grazing():
    i = Interface(alpha=1e-3)
    wo = np.tile(wo_at(0.3, inside=True), (20_000, 1))   # from the glass, 73 degrees off the normal: beyond the 41.8 degree critical angle
    wi, w = d.sample(i, wo, RNG)
    assert (wi[:, 2] < 0).mean() > 0.99                  # stays inside
    assert w.sum(axis=0)[0] / len(wo) == pytest.approx(1.0, abs=0.03)


@pytest.mark.parametrize("inside", (False, True))
def test_the_radiance_btdf_obeys_generalised_reciprocity(inside):
    i = Interface(alpha=0.3, eta_in=1.5)
    n = 4000
    wo = np.tile(wo_at(0.7, inside), (n, 1))
    wi = sphere_dirs(n)
    wi = wi * np.where(wi[:, 2:3] * wo[:, 2:3] < 0, 1.0, -1.0)   # on the opposite side: the transmission lobe
    f1 = d.eval(i, wo, wi)[:, 0]
    f2 = d.eval(i, wi, wo)[:, 0]
    eta_wo = 1.5 if inside else 1.0
    eta_wi = 1.0 if inside else 1.5
    ok = (f1 > 1e-9) & (f2 > 1e-9)
    assert ok.sum() > 100
    assert (f1[ok] / eta_wo ** 2) == pytest.approx(f2[ok] / eta_wi ** 2, rel=1e-6)


@pytest.mark.parametrize("alpha", (0.25, 0.5))
@pytest.mark.parametrize("cos,inside", ((0.9, False), (0.4, False), (0.9, True), (0.6, True)))
def test_sampling_agrees_with_evaluation(alpha, cos, inside):
    i = Interface(alpha=alpha, tint=(1.0, 0.8, 0.6))
    n = 500_000
    wo = np.tile(wo_at(cos, inside), (n, 1))
    wi, w = d.sample(i, wo, RNG)
    est = w.mean(axis=0)
    u = sphere_dirs(n)
    ref = (d.eval(i, wo, u) * np.abs(u[:, 2])[:, None]).mean(axis=0) * 4 * np.pi
    assert est == pytest.approx(ref, rel=0.06, abs=0.01), (alpha, cos, inside, est, ref)


def test_pdf_integrates_to_at_most_one():
    i = Interface(alpha=0.4)
    for inside in (False, True):
        n = 500_000
        wo = np.tile(wo_at(0.6, inside), (n, 1))
        total = np.mean(d.pdf(i, wo, sphere_dirs(n))) * 4 * np.pi
        assert 0.85 < total < 1.03, (inside, total)


def test_beer_lambert():
    c, dist = (0.5, 0.25, 1.0), 2.0
    assert d.transmittance(c, dist, dist) == pytest.approx(c, rel=1e-9)               # the colour is what is left after the attenuation distance
    assert d.transmittance(c, dist, 2 * dist) == pytest.approx(np.array(c) ** 2, rel=1e-9)
    assert d.transmittance(c, float("inf"), 100.0) == pytest.approx([1, 1, 1])
    assert d.transmittance(c, dist, 0.0) == pytest.approx([1, 1, 1])
