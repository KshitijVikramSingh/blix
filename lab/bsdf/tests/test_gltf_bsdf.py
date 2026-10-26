"""The glTF BSDF against its own claims: Fresnel's numbers, reciprocity, a pdf that integrates, sampling that agrees with evaluation, energy bounds."""
import numpy as np
import pytest

from bsdflab import gltf_bsdf as g
from bsdflab.gltf_bsdf import Material

RNG = np.random.default_rng(11)


def hemisphere_dirs(n, below=False):
    mu = RNG.random(n) * 0.998 + 0.001
    phi = RNG.random(n) * 2 * np.pi
    st = np.sqrt(1 - mu * mu)
    return np.stack([st * np.cos(phi), st * np.sin(phi), -mu if below else mu], axis=-1)


def sphere_dirs(n):
    z = RNG.random(n) * 2 - 1
    phi = RNG.random(n) * 2 * np.pi
    st = np.sqrt(1 - z * z)
    return np.stack([st * np.cos(phi), st * np.sin(phi), z], axis=-1)


def wo_at(cos):
    return np.array([np.sqrt(1 - cos * cos), 0.0, cos])


MATERIALS = {
    "plastic": Material(base_color=(0.7, 0.4, 0.2), roughness=0.5),
    "rough metal": Material(base_color=(0.9, 0.6, 0.3), metallic=1.0, roughness=0.6),
    "half metal": Material(base_color=(0.8, 0.8, 0.8), metallic=0.5, roughness=0.5),
    "frosted pane": Material(base_color=(0.9, 0.95, 1.0), roughness=0.5, transmission=1.0),
    "tinted glass": Material(base_color=(0.5, 0.8, 0.6), roughness=0.4, transmission=0.8, ior=1.45),
    "leaf": Material(base_color=(0.3, 0.5, 0.2), roughness=0.6, diffuse_transmission=0.6, diffuse_transmission_color=(0.5, 0.7, 0.2)),
    "specular ext": Material(base_color=(0.5, 0.5, 0.5), roughness=0.5, specular=0.5, specular_color=(1.0, 0.8, 0.6), ior=1.8),
}


def test_fresnel_at_normal_incidence_is_the_iors_f0():
    for ior, f0 in ((1.5, 0.04), (1.0, 0.0), (2.0, 1 / 9)):
        got = g.fresnel_dielectric(Material(ior=ior), np.array([1.0]))
        assert got == pytest.approx(f0, abs=1e-6)


def test_specular_extension_sets_f0_tint_and_f90():
    m = Material(ior=1.5, specular=0.5, specular_color=(1.0, 0.5, 0.0))
    assert g.fresnel_dielectric(m, np.array([1.0]))[0] == pytest.approx([0.04, 0.02, 0.0], abs=1e-6)
    assert g.fresnel_dielectric(m, np.array([0.0]))[0] == pytest.approx([0.5, 0.5, 0.5], abs=1e-6)


def test_reflecting_lobes_are_reciprocal():
    for name, m in MATERIALS.items():
        a, b = hemisphere_dirs(2000), hemisphere_dirs(2000)
        assert g.eval(m, a, b) == pytest.approx(g.eval(m, b, a), rel=1e-6, abs=1e-9), name


def test_thin_sheet_passes_straight_on_as_it_smooths():
    m = Material(base_color=(1, 1, 1), roughness=0.0, transmission=1.0)
    wo = np.tile(wo_at(0.7), (4000, 1))
    wi, w = g.sample(m, wo, RNG)
    below = wi[:, 2] < 0
    assert below.mean() > 0.5
    spread = np.linalg.norm(wi[below] + wo[below], axis=1)   # wi = -wo, to the alpha floor's blur
    assert np.median(spread) < 0.01 and spread.max() < 0.15


@pytest.mark.parametrize("name", list(MATERIALS))
def test_pdf_integrates_to_the_lobes_it_can_reach(name):
    m = MATERIALS[name]
    n = 600_000
    wo = np.tile(wo_at(0.6), (n, 1))
    wi = sphere_dirs(n)
    total = np.mean(g.pdf(m, wo, wi)) * 4 * np.pi
    p = g.lobe_probabilities(m)
    assert total <= 1.0 + 0.03                # never more than everything
    assert total == pytest.approx(1.0, abs=0.12), (name, total, p)   # a lobe's reflection below the horizon is lost to the density


@pytest.mark.parametrize("name", list(MATERIALS))
@pytest.mark.parametrize("cos", (0.9, 0.4))
def test_sampling_agrees_with_evaluation(name, cos):
    m = MATERIALS[name]
    n = 400_000
    wo = np.tile(wo_at(cos), (n, 1))
    wi, w = g.sample(m, wo, RNG)
    est = w.mean(axis=0)
    wi_u = sphere_dirs(n)
    ref = (g.eval(m, wo, wi_u) * np.abs(wi_u[:, 2])[:, None]).mean(axis=0) * 4 * np.pi
    assert est == pytest.approx(ref, rel=0.05, abs=0.01), (name, cos, est, ref)


@pytest.mark.parametrize("name", list(MATERIALS))
def test_a_white_surface_gives_back_no_more_than_it_gets(name):
    m = MATERIALS[name]
    n = 200_000
    for cos in (0.95, 0.5, 0.15):
        wo = np.tile(wo_at(cos), (n, 1))
        _, w = g.sample(m, wo, RNG)
        albedo = w.mean(axis=0)
        assert albedo.max() <= 1.0 + 0.02, (name, cos, albedo)


def test_a_clear_pane_reflects_and_transmits_nearly_all():
    m = Material(base_color=(1, 1, 1), roughness=0.3, transmission=1.0)
    wo = np.tile(wo_at(0.8), (300_000, 1))
    wi, w = g.sample(m, wo, RNG)
    up, down = w[wi[:, 2] > 0].sum(axis=0) / len(wo), w[wi[:, 2] < 0].sum(axis=0) / len(wo)
    assert (up + down).min() > 0.9
    assert down.min() > 0.8   # F at 0.8 is about 0.04-0.05: most of the light goes through


SOLIDS = {
    "glass": Material(base_color=(0.9, 1.0, 0.95), roughness=0.45, transmission=1.0, volume=True),
    "frosted block": Material(base_color=(1, 1, 1), roughness=0.6, transmission=0.8, volume=True, diffuse_transmission=0.0),
    "tinted metal-ish": Material(base_color=(0.8, 0.9, 1.0), metallic=0.3, roughness=0.4, transmission=0.7, volume=True),
}


@pytest.mark.parametrize("name", list(SOLIDS))
@pytest.mark.parametrize("cos", (0.9, 0.4))
def test_a_solid_samples_as_it_evaluates(name, cos):
    m = SOLIDS[name]
    n = 500_000
    wo = np.tile(wo_at(cos), (n, 1))
    wi, w = g.sample(m, wo, RNG)
    est = w.mean(axis=0)
    u = sphere_dirs(n)
    ref = (g.eval(m, wo, u) * np.abs(u[:, 2])[:, None]).mean(axis=0) * 4 * np.pi
    assert est == pytest.approx(ref, rel=0.06, abs=0.012), (name, cos, est, ref)


def test_a_solid_refracts_where_a_thin_pane_does_not():
    thin = Material(roughness=0.0, transmission=1.0)
    solid = Material(roughness=0.0, transmission=1.0, volume=True)
    wo = np.tile(wo_at(0.6), (20_000, 1))
    wt, _ = g.sample(thin, wo, RNG)
    ws, _ = g.sample(solid, wo, RNG)
    below_t, below_s = wt[wt[:, 2] < 0], ws[ws[:, 2] < 0]
    assert np.linalg.norm(below_t + wo[: len(below_t)], axis=1).mean() < 0.05            # thin: straight through (wi = -wo)
    sin_in = np.sqrt(1 - 0.6 ** 2)
    sin_out = np.hypot(below_s[:, 0], below_s[:, 1])
    assert sin_out.mean() == pytest.approx(sin_in / 1.5, abs=0.02)                        # solid: Snell's law toward the normal
