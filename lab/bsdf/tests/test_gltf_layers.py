"""The layers over the glTF BSDF: clearcoat, sheen, iridescence, anisotropy and the nested media's index: identities at their off values, the numbers they should
give, reciprocity where the model has it, sampling against evaluation, and no energy gained."""
import numpy as np
import pytest

from bsdflab import dielectric, gltf_bsdf as g
from bsdflab.gltf_bsdf import Material

RNG = np.random.default_rng(23)


def hemisphere_dirs(n):
    mu = RNG.random(n) * 0.998 + 0.001
    phi = RNG.random(n) * 2 * np.pi
    st = np.sqrt(1 - mu * mu)
    return np.stack([st * np.cos(phi), st * np.sin(phi), mu], axis=-1)


def sphere_dirs(n):
    z = RNG.random(n) * 2 - 1
    phi = RNG.random(n) * 2 * np.pi
    st = np.sqrt(1 - z * z)
    return np.stack([st * np.cos(phi), st * np.sin(phi), z], axis=-1)


def wo_at(cos):
    return np.array([np.sqrt(1 - cos * cos), 0.0, cos])


BASE = dict(base_color=(0.7, 0.4, 0.2), roughness=0.5)
LAYERED = {
    "anisotropic": Material(**BASE, anisotropy=0.7, anisotropy_rotation=0.5),
    "anisotropic metal": Material(base_color=(0.9, 0.7, 0.4), metallic=1.0, roughness=0.4, anisotropy=0.9, anisotropy_rotation=1.2),
    "clearcoat": Material(**BASE, clearcoat=1.0, clearcoat_roughness=0.2),
    "half coat": Material(**BASE, clearcoat=0.5, clearcoat_roughness=0.4),
    "sheen": Material(base_color=(0.5, 0.2, 0.2), roughness=0.7, sheen_color=(0.8, 0.8, 0.9), sheen_roughness=0.5),
    "iridescent": Material(base_color=(0.6, 0.6, 0.6), roughness=0.4, iridescence=1.0, iridescence_ior=1.3, iridescence_thickness=400.0),
    "iridescent metal": Material(base_color=(0.9, 0.8, 0.7), metallic=1.0, roughness=0.4, iridescence=0.8, iridescence_ior=1.5, iridescence_thickness=300.0),
    "everything": Material(base_color=(0.5, 0.3, 0.6), roughness=0.45, clearcoat=0.6, clearcoat_roughness=0.15, sheen_color=(0.4, 0.4, 0.4), sheen_roughness=0.6,
                           iridescence=0.5, anisotropy=0.4, anisotropy_rotation=0.3),
}
RECIPROCAL = ["anisotropic", "anisotropic metal", "iridescent", "iridescent metal"]   # sheen's and the coat's darkening of the base read the view's angle only


def test_every_layer_off_is_the_base_model():
    wo, wi = hemisphere_dirs(500), sphere_dirs(500)
    plain = Material(**BASE)
    off = Material(**BASE, clearcoat=0.0, sheen_color=(0, 0, 0), iridescence=0.0, anisotropy=0.0)
    assert g.eval(off, wo, wi) == pytest.approx(g.eval(plain, wo, wi), rel=1e-12)
    sample_off, sample_plain = g.sample(off, wo, np.random.default_rng(5))[1], g.sample(plain, wo, np.random.default_rng(5))[1]
    assert sample_off == pytest.approx(sample_plain)


def test_anisotropy_zero_is_isotropic_and_rotation_turns_the_lobe():
    wo, wi = hemisphere_dirs(2000), hemisphere_dirs(2000)
    iso = g.eval(Material(**BASE), wo, wi)
    assert g.eval(Material(**BASE, anisotropy=0.0, anisotropy_rotation=1.0), wo, wi) == pytest.approx(iso, rel=1e-12)
    # A half vector along the tangent sees the wide roughness, across it the narrow: with the roughness stretched along x, the lobe peaks harder across.
    m = Material(**BASE, anisotropy=0.8)
    ax, ay = m.alphas
    along = g.aniso_d(np.array([np.sin(0.3), 0.0, np.cos(0.3)]), ax, ay, 0.0)
    across = g.aniso_d(np.array([0.0, np.sin(0.3), np.cos(0.3)]), ax, ay, 0.0)
    assert across < along and ax > ay
    turned = g.aniso_d(np.array([0.0, np.sin(0.3), np.cos(0.3)]), ax, ay, np.pi / 2)
    assert turned == pytest.approx(along, rel=1e-9)   # rotating by a quarter turn swaps the axes


def test_the_coat_reflects_four_percent_at_normal_incidence_over_a_black_base():
    m = Material(base_color=(0, 0, 0), roughness=1.0, clearcoat=1.0, clearcoat_roughness=0.0, specular=0.0)
    wo = np.tile(wo_at(0.9999), (200_000, 1))
    wi, w = g.sample(m, wo, RNG)
    assert w.sum(axis=0)[0] / len(wo) == pytest.approx(0.04, abs=0.006)


def test_the_film_at_zero_thickness_is_the_plain_fresnel_at_normal_incidence():
    # Not at oblique angles: the viewer's formulation gives the film's own interface Schlick's (1 - cos)^5 even where its ior matches the air, so a thickness-0 film
    # adds reflectance off the normal. At normal incidence it is the base's f0 to within the clamp on R12 R23 (0.01).
    for f0 in ((0.04, 0.04, 0.04), (0.9, 0.6, 0.3)):
        film = g.iridescence_fresnel(1.0, 1.3, np.array([1.0]), 0.0, np.array(f0))[0]
        assert film == pytest.approx(f0, abs=0.01), (f0, film)


def test_the_coat_alone_is_reciprocal():
    m = Material(base_color=(0, 0, 0), roughness=1.0, specular=0.0, clearcoat=1.0, clearcoat_roughness=0.3)
    a, b = hemisphere_dirs(2000), hemisphere_dirs(2000)
    assert g.eval(m, a, b) == pytest.approx(g.eval(m, b, a), rel=1e-5, abs=1e-9)


def test_the_film_is_a_reflectance_that_varies_with_thickness():
    cos = np.full(40, 0.9)
    thick = np.linspace(100, 700, 40)
    out = np.array([g.iridescence_fresnel(1.0, 1.3, cos[:1], t, np.array([0.04, 0.04, 0.04]))[0] for t in thick])
    assert out.min() >= 0.0 and out.max() <= 1.0
    assert (out.max(axis=0) - out.min(axis=0)).min() > 0.02   # each channel swings with the film
    assert np.abs(out[:, 0] - out[:, 2]).max() > 0.02          # and not in step: the colours part


def test_sheen_albedo_converges_and_is_a_fraction():
    for r in (0.3, 0.6, 1.0):
        coarse = g.sheen_albedo(np.array([0.2, 0.6, 0.95]), r)
        fine = g.sheen_albedo(np.array([0.2, 0.6, 0.95]), r, grid=(96, 64))
        assert np.all((fine > 0) & (fine < 1))
        assert coarse == pytest.approx(fine, rel=0.04, abs=0.005), (r, coarse, fine)


@pytest.mark.parametrize("name", RECIPROCAL)
def test_reciprocal_layers(name):
    m = LAYERED[name]
    a, b = hemisphere_dirs(2000), hemisphere_dirs(2000)
    assert g.eval(m, a, b) == pytest.approx(g.eval(m, b, a), rel=1e-5, abs=1e-9)


@pytest.mark.parametrize("name", list(LAYERED))
@pytest.mark.parametrize("cos", (0.9, 0.4))
def test_layers_sample_as_they_evaluate(name, cos):
    m = LAYERED[name]
    n = 500_000
    wo = np.tile(wo_at(cos), (n, 1))
    wi, w = g.sample(m, wo, RNG)
    est = w.mean(axis=0)
    u = sphere_dirs(n)
    ref = (g.eval(m, wo, u) * np.abs(u[:, 2])[:, None]).mean(axis=0) * 4 * np.pi
    assert est == pytest.approx(ref, rel=0.07, abs=0.012), (name, cos, est, ref)


@pytest.mark.parametrize("name", list(LAYERED))
def test_layers_give_back_no_more_than_they_get(name):
    m = LAYERED[name]
    n = 200_000
    for cos in (0.95, 0.5, 0.15):
        _, w = g.sample(m, np.tile(wo_at(cos), (n, 1)), RNG)
        assert w.mean(axis=0).max() <= 1.0 + 0.03, (name, cos, w.mean(axis=0))


def test_a_nested_interface_uses_the_enclosing_medium():
    water_to_glass = dielectric.Interface(alpha=1e-3, eta_out=1.33, eta_in=1.5)
    assert dielectric.fresnel_exact(np.array(1.0), 1.33, 1.5) == pytest.approx(((1.5 - 1.33) / (1.5 + 1.33)) ** 2, abs=1e-6)
    m = Material(roughness=0.0, transmission=1.0, volume=True, eta_out=1.33)
    wo = np.tile(wo_at(0.6), (20_000, 1))
    wi, _ = g.sample(m, wo, RNG)
    below = wi[wi[:, 2] < 0]
    sin_in = np.sqrt(1 - 0.6 ** 2)
    assert np.hypot(below[:, 0], below[:, 1]).mean() == pytest.approx(sin_in * 1.33 / 1.5, abs=0.02)   # Snell between 1.33 and 1.5
    assert water_to_glass.eta_out == 1.33
