"""EON against its own claims: Lambert at r = 0, reciprocity, energy conservation at every roughness, and the closed forms against the numbers."""
import numpy as np
import pytest

from bsdflab import eon

RNG = np.random.default_rng(7)


def directions(n):
    mu = RNG.random(n) * 0.999 + 0.001
    phi = RNG.random(n) * 2 * np.pi
    st = np.sqrt(1 - mu * mu)
    return np.stack([st * np.cos(phi), st * np.sin(phi), mu], axis=-1)


def test_r_zero_is_lambert_exactly():
    wi, wo = directions(500), directions(500)
    for rho in (0.1, 0.5, 1.0):
        assert eon.f_eon(rho, 0.0, wi, wo) == pytest.approx(rho / np.pi, abs=1e-6)


def test_reciprocity_and_positivity():
    wi, wo = directions(2000), directions(2000)
    for r in (0.2, 0.6, 1.0):
        for rho in (0.3, 0.8, 1.0):
            a, b = eon.f_eon(rho, r, wi, wo), eon.f_eon(rho, r, wo, wi)
            assert a == pytest.approx(b, rel=1e-9, abs=1e-12)
            assert np.all(a >= 0.0)


def test_the_quartic_fit_follows_the_exact_function():
    mu = np.linspace(0.02, 1.0, 200)
    exact, fit = eon.g_f_exact(mu) / np.pi, eon.g_over_pi_poly(mu)
    assert np.max(np.abs(exact - fit)) < 2e-3          # the paper's polynomial: a few parts per thousand of G / pi
    assert eon.g_f_exact(1.0) == pytest.approx(0.0, abs=1e-6)


def test_the_average_albedo_closed_form_is_the_cosine_average():
    mu = np.linspace(1e-4, 1.0, 20001)
    for r in (0.0, 0.4, 1.0):
        numeric = 2.0 * np.trapezoid(eon.e_f(mu, r, exact=True) * mu, mu)
        assert eon.avg_e_f(r) == pytest.approx(numeric, abs=2e-4)


@pytest.mark.parametrize("r", [0.25, 0.6, 1.0])
@pytest.mark.parametrize("mu_o", [0.1, 0.5, 0.95])
def test_single_scatter_albedo_matches_its_closed_form(r, mu_o):
    """The single-scatter lobe's quadrature albedo is rho * E(mu_o) with the EXACT G: the closed form is derived from it, not fitted."""
    rho = 1.0
    numeric = eon.albedo_numeric(lambda wi, wo: eon.f_single_scatter(rho, r, wi, wo), mu_o, 192, 192)
    assert numeric == pytest.approx(eon.e_f(mu_o, r, exact=True), abs=2e-3)


@pytest.mark.parametrize("r", [0.0, 0.3, 0.7, 1.0])
@pytest.mark.parametrize("mu_o", [0.05, 0.3, 0.7, 1.0])
def test_a_white_surface_conserves_energy_at_every_roughness(r, mu_o):
    """rho = 1: the whole model reflects all of the light, to the fit's accuracy; no roughness loses or invents energy."""
    numeric = eon.albedo_numeric(lambda wi, wo: eon.f_eon(1.0, r, wi, wo), mu_o, 192, 192)
    assert numeric == pytest.approx(1.0, abs=6e-3)
    assert eon.albedo_closed_form(1.0, r, mu_o) == pytest.approx(1.0, abs=1e-12)


@pytest.mark.parametrize("rho", [0.2, 0.5, 0.8])
@pytest.mark.parametrize("r", [0.4, 1.0])
def test_albedo_numeric_matches_eq_20_and_never_exceeds_the_albedo_by_much(rho, r):
    for mu_o in (0.1, 0.5, 0.9):
        numeric = eon.albedo_numeric(lambda wi, wo: eon.f_eon(rho, r, wi, wo), mu_o, 192, 192)
        assert numeric == pytest.approx(eon.albedo_closed_form(rho, r, mu_o), abs=6e-3)
        assert numeric <= 1.0


def test_roughness_brightens_the_grazing_and_backscatter_directions_and_dims_nothing_overall():
    """The visible character: a rough diffuse surface is flatter and retroreflects (brighter toward the light's own direction at grazing)."""
    wo = np.array([np.sin(1.2), 0.0, np.cos(1.2)])          # a grazing eye
    retro = wo.copy()                                        # light from the eye's own direction
    lam = eon.f_lambert(1.0)
    assert eon.f_eon(1.0, 1.0, retro[None], wo[None])[0] > 1.3 * lam
    # and the hemisphere's average albedo for a mid-grey stays close to Lambert's: roughness redistributes light, it does not remove it
    mu = np.linspace(0.02, 1.0, 50)
    mean_rough = np.trapezoid(eon.albedo_closed_form(0.5, 1.0, mu) * mu, mu) * 2
    assert mean_rough == pytest.approx(0.5, abs=0.06)


def test_per_channel_albedo_is_independent_per_channel():
    wi, wo = directions(50), directions(50)
    rgb = np.array([0.2, 0.5, 0.8])
    got = eon.f_eon(rgb, 0.7, wi, wo)
    for c in range(3):
        assert got[:, c] == pytest.approx(eon.f_eon(float(rgb[c]), 0.7, wi, wo), rel=1e-12)


@pytest.mark.parametrize("r", [0.0, 0.3, 0.7, 1.0])
@pytest.mark.parametrize("rho", [0.1, 0.4, 0.7, 1.0])
def test_hemispherical_albedo_is_the_cosine_average_of_the_directional_albedo(rho, r):
    mu = np.linspace(1e-5, 1.0, 40001)
    numeric = 2.0 * np.trapezoid(eon.albedo_closed_form(rho, r, mu) * mu, mu)
    assert eon.hemispherical_albedo(rho, r) == pytest.approx(numeric, abs=3e-3)


def test_hemispherical_albedo_is_lambert_at_r_zero_and_lossy_for_a_rough_grey():
    assert eon.hemispherical_albedo(0.37, 0.0) == pytest.approx(0.37, abs=1e-12)
    assert eon.hemispherical_albedo(1.0, 0.9) == pytest.approx(1.0, abs=1e-12)        # nothing lost for a white surface
    assert eon.hemispherical_albedo(0.5, 1.0) < 0.5 - 0.03                            # but a mid grey that rough reflects less than Lambert
    assert np.all(eon.hemispherical_albedo(np.linspace(0.01, 0.99, 50), 0.8) <= np.linspace(0.01, 0.99, 50) + 1e-12)
