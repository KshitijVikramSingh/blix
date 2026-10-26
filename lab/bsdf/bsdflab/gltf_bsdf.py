"""The glTF 2.0 surface model as a BSDF a path tracer can evaluate and sample: the reference the references are built from.

Appendix B of the glTF specification (the metallic-roughness BRDF) with the extensions that change what a ray does at a surface:

    KHR_materials_ior                   the dielectric's index of refraction (Fresnel's f0 = ((ior - 1) / (ior + 1))^2)
    KHR_materials_specular              the dielectric's f0 tint and f90 (the share of light the grazing Fresnel reaches)
    KHR_materials_transmission          a share of the dielectric's base layer is transmitted: a THIN-WALLED rough BTDF (below)
    KHR_materials_diffuse_transmission  a share of the diffuse base is Lambertian TRANSMISSION, in its own colour

What it is not yet: clearcoat, sheen, iridescence and anisotropy (their lobes stack on this one), and volumes (a transmitting solid with
an interior: a true refracting interface and Beer-Lambert attenuation). Those are the next layers and their tests go beside these.

The spec's structure, written out (all lobes in the local frame, +z the shading normal, wo toward the viewer, wi toward the light):

    metal      = conductor_fresnel(f0 = base_color, f90 = 1, specular_brdf(alpha))
    dielectric = fresnel_mix(base = mix(mix(diffuse(base_color), diffuse_transmission(dt_color), DT), specular_btdf(base_color, alpha_t), T),
                             layer = specular_brdf(alpha))
    material   = mix(dielectric, metal, metallic)

fresnel_mix weighs the layer by F(V.H) and the base by 1 - F(V.H), with H the half vector of the (reflection) lobe -- for a light BELOW the surface
(wi.z < 0, the transmitting lobes) H is taken from the light's mirror image across the surface, which is the spec's thin-walled rule: the BTDF is the
reflection lobe read at the mirrored light, so a smooth sheet passes the ray straight on (wi = -wo) and a rough one blurs it by the same distribution.
The sheet's roughness is scaled by its ior for that blur (Khronos' sample viewer: clamp(2 ior - 2, 0, 1) x roughness), because a ray through two
interfaces of a slab is spread less than one reflected off a single one.

Everything is isotropic. alpha = roughness^2 (the spec's perceptual roughness). D is GGX; G the height-correlated Smith.

    eval(m, wo, wi)       f(wo, wi), rgb, without the cosine
    pdf(m, wo, wi)        the density `sample` draws wi at
    sample(m, wo, rng)    wi, weight = f |cos| / pdf, per row of wo; one lobe is picked per sample, so the estimator is the mixture's

Held by tests/test_gltf_bsdf.py: the Fresnel numbers, reciprocity of the reflecting lobes, the pdf integrating to its lobes' probabilities, sample against
eval (the mean weight equals the integral of f cos), energy bounds, and the thin sheet passing straight through as it smooths.
"""
from __future__ import annotations

from dataclasses import dataclass

import numpy as np

from . import eon

PI = np.pi


@dataclass(frozen=True)
class Material:
    base_color: tuple = (1.0, 1.0, 1.0)
    metallic: float = 0.0
    roughness: float = 0.5                      # perceptual; alpha = roughness ** 2
    ior: float = 1.5
    specular: float = 1.0                       # KHR_materials_specular factor (f90 of the dielectric)
    specular_color: tuple = (1.0, 1.0, 1.0)
    transmission: float = 0.0                   # KHR_materials_transmission factor
    diffuse_transmission: float = 0.0           # KHR_materials_diffuse_transmission factor
    diffuse_transmission_color: tuple = (1.0, 1.0, 1.0)
    diffuse_roughness: float = 0.0              # EON's roughness (0: Lambert)
    volume: bool = False                        # KHR_materials_volume with a thickness: a SOLID, its transmission a refracting interface (dielectric.py)
    attenuation_color: tuple = (1.0, 1.0, 1.0)  # the medium inside (Beer-Lambert), used by the path tracer
    attenuation_distance: float = float("inf")

    @property
    def alpha(self) -> float:
        return max(self.roughness * self.roughness, 1e-3)

    def interface(self):
        """The solid's surface: a rough dielectric between air and the material's ior, its refracted light tinted by the base colour."""
        from .dielectric import Interface
        return Interface(alpha=self.alpha, eta_out=1.0, eta_in=self.ior, tint=self.base_color)

    @property
    def alpha_transmission(self) -> float:
        """The thin sheet's lobe: the roughness scaled by the ior, then squared."""
        r = float(np.clip(self.roughness * np.clip(self.ior * 2.0 - 2.0, 0.0, 1.0), 0.0, 1.0))
        return max(r * r, 1e-3)


def _norm(v):
    return v / np.linalg.norm(v, axis=-1, keepdims=True)


def _dot(a, b):
    return np.sum(a * b, axis=-1)


def f0_dielectric(m: Material):
    f0 = ((m.ior - 1.0) / (m.ior + 1.0)) ** 2
    return np.minimum(f0 * np.asarray(m.specular_color, float), 1.0)


def fresnel_dielectric(m: Material, vh):
    """KHR_materials_specular's Schlick: f0 + (f90 - f0)(1 - V.H)^5, rgb."""
    f0 = f0_dielectric(m)
    return f0 + (m.specular - f0) * ((1.0 - np.clip(vh, 0.0, 1.0)) ** 5)[..., None]


def fresnel_metal(m: Material, vh):
    f0 = np.asarray(m.base_color, float)
    return f0 + (1.0 - f0) * ((1.0 - np.clip(vh, 0.0, 1.0)) ** 5)[..., None]


def ggx_d(nh, a):
    d = nh * nh * (a * a - 1.0) + 1.0
    return a * a / (PI * d * d)


def smith_lambda(c, a):
    c2 = np.maximum(c * c, 1e-12)
    return 0.5 * (np.sqrt(1.0 + a * a * (1.0 - c2) / c2) - 1.0)


def g2(nl, nv, a):
    return 1.0 / (1.0 + smith_lambda(nl, a) + smith_lambda(nv, a))


def g1(nv, a):
    return 1.0 / (1.0 + smith_lambda(nv, a))


def _mirror(wi):
    out = np.array(wi, float, copy=True)
    out[..., 2] = np.abs(out[..., 2])
    return out


def spec_brdf(wo, wi, a):
    """D G2 / (4 NL NV) for wo and wi both above the surface; 0 elsewhere."""
    h = _norm(wo + wi)
    nl, nv, nh = wi[..., 2], wo[..., 2], h[..., 2]
    ok = (nl > 0) & (nv > 0) & (nh > 0)
    safe_l, safe_v = np.where(ok, nl, 1.0), np.where(ok, nv, 1.0)
    return np.where(ok, ggx_d(nh, a) * g2(safe_l, safe_v, a) / (4.0 * safe_l * safe_v), 0.0)


def eval(m: Material, wo, wi):
    """f(wo, wi), rgb (..., 3), without the cosine. wo must be above the surface (z > 0)."""
    wo, wi = np.asarray(wo, float), np.asarray(wi, float)
    wm = _mirror(wi)
    h = _norm(wo + wm)
    vh = _dot(wo, h)
    below = wi[..., 2] < 0
    bc = np.asarray(m.base_color, float)
    fr = fresnel_dielectric(m, vh)
    t, dt = m.transmission, m.diffuse_transmission

    # Light above the surface: the specular layer, and the base's reflecting part.
    spec = spec_brdf(wo, wi, m.alpha)[..., None]
    diffuse = eon.f_eon(bc, m.diffuse_roughness, np.where(below[..., None], wo, wi), wo)
    dielectric_up = fr * spec + (1.0 - fr) * ((1.0 - t) * (1.0 - dt) * diffuse)
    metal_up = fresnel_metal(m, vh) * spec
    solid = m.volume and t > 0.0
    if solid:
        # A solid: the transmitted share is a refracting interface (reflection and refraction both), not a thin sheet's BTDF.
        from . import dielectric
        fi = dielectric.eval(m.interface(), wo, wi)
        dielectric_up = (1.0 - t) * (fr * spec + (1.0 - fr) * ((1.0 - dt) * diffuse)) + t * np.where(below[..., None], 0.0, fi)
    up = (1.0 - m.metallic) * dielectric_up + m.metallic * metal_up

    # Light below: the thin sheet's BTDF (the reflection lobe at the mirrored light, rougher by the ior's factor) and the diffuse transmission.
    btdf = spec_brdf(wo, wm, m.alpha_transmission)[..., None] * bc * t
    dtl = np.asarray(m.diffuse_transmission_color, float) * ((1.0 - t) * dt / PI)
    down = (1.0 - m.metallic) * (1.0 - fr) * (btdf + dtl)
    if solid:
        down = (1.0 - m.metallic) * ((1.0 - fr) * dtl + t * np.where(below[..., None], fi, 0.0))
    return np.where(below[..., None], down, up)


def lobe_probabilities(m: Material):
    """The five lobes `sample` picks among: specular reflection, diffuse reflection, thin transmission, diffuse transmission, the solid's interface."""
    t, dt, k = m.transmission, m.diffuse_transmission, m.metallic
    solid = m.volume and t > 0.0
    w = np.array([(0.2 if not solid else 0.2 * (1 - t)) + 0.8 * k, (1 - k) * (1 - t) * (1 - dt), 0.0 if solid else (1 - k) * t, (1 - k) * (1 - t) * dt,
                  (1 - k) * t if solid else 0.0], float)
    return w / w.sum()


def _vndf_pdf(wo, wi_reflect, a):
    """The density of the visible-normal sampled reflection: G1(wo) D(h) / (4 NV)."""
    h = _norm(wo + wi_reflect)
    nv = np.maximum(wo[..., 2], 1e-9)
    return g1(nv, a) * ggx_d(np.maximum(h[..., 2], 0.0), a) / (4.0 * nv)


def pdf(m: Material, wo, wi):
    wo, wi = np.asarray(wo, float), np.asarray(wi, float)
    p = lobe_probabilities(m)
    wm = _mirror(wi)
    below = wi[..., 2] < 0
    up = p[0] * _vndf_pdf(wo, wm, m.alpha) + p[1] * np.abs(wi[..., 2]) / PI
    down = p[2] * _vndf_pdf(wo, wm, m.alpha_transmission) + p[3] * np.abs(wi[..., 2]) / PI
    if p[4] > 0.0:
        from . import dielectric
        pi_ = p[4] * dielectric.pdf(m.interface(), wo, wi)
        up, down = up + pi_, down + pi_
    return np.where(below, down, up)


def _cosine(n, rng):
    u1, u2 = rng.random(n), rng.random(n)
    r, phi = np.sqrt(u1), 2 * PI * u2
    return np.stack([r * np.cos(phi), r * np.sin(phi), np.sqrt(np.maximum(0.0, 1 - u1))], axis=-1)


def _sample_vndf(wo, a, rng):
    """Heitz 2018: a microfacet normal from the distribution of normals visible from wo."""
    n = wo.shape[0]
    vh = _norm(np.stack([a * wo[:, 0], a * wo[:, 1], wo[:, 2]], axis=-1))
    lensq = vh[:, 0] ** 2 + vh[:, 1] ** 2
    t1 = np.where(lensq[:, None] > 0, np.stack([-vh[:, 1], vh[:, 0], np.zeros(n)], axis=-1) / np.sqrt(np.maximum(lensq, 1e-20))[:, None],
                  np.array([1.0, 0.0, 0.0]))
    t2 = np.cross(vh, t1)
    u1, u2 = rng.random(n), rng.random(n)
    r = np.sqrt(u1)
    phi = 2 * PI * u2
    p1, p2 = r * np.cos(phi), r * np.sin(phi)
    s = 0.5 * (1.0 + vh[:, 2])
    p2 = (1.0 - s) * np.sqrt(np.maximum(0.0, 1.0 - p1 * p1)) + s * p2
    nh = p1[:, None] * t1 + p2[:, None] * t2 + np.sqrt(np.maximum(0.0, 1.0 - p1 * p1 - p2 * p2))[:, None] * vh
    return _norm(np.stack([a * nh[:, 0], a * nh[:, 1], np.maximum(0.0, nh[:, 2])], axis=-1))


def sample(m: Material, wo, rng):
    """One direction per row of wo (n, 3): wi (n, 3) and the weight f |cos| / pdf (n, 3). A direction below the surface is a transmitted one."""
    wo = np.asarray(wo, float)
    n = wo.shape[0]
    p = lobe_probabilities(m)
    lobe = rng.choice(5, size=n, p=p)
    wi = np.zeros((n, 3))
    valid = np.ones(n, bool)
    for k, alpha in ((0, m.alpha), (2, m.alpha_transmission)):
        sel = lobe == k
        if sel.any():
            h = _sample_vndf(wo[sel], alpha, rng)
            r = 2.0 * _dot(wo[sel], h)[:, None] * h - wo[sel]
            valid[np.flatnonzero(sel)[r[:, 2] <= 0]] = False   # a reflection below the horizon is no sample: the density counts only those above it
            if k == 2:
                r = r * np.array([1.0, 1.0, -1.0])
            wi[sel] = r
    diffuse_up = lobe == 1
    if diffuse_up.any():
        wi[diffuse_up] = _cosine(int(diffuse_up.sum()), rng)
    diffuse_down = lobe == 3
    if diffuse_down.any():
        wi[diffuse_down] = _cosine(int(diffuse_down.sum()), rng) * np.array([1.0, 1.0, -1.0])
    iface = lobe == 4
    if iface.any():
        from . import dielectric
        wi_i, ok = dielectric.sample_direction(m.interface(), wo[iface], rng)
        wi[iface] = wi_i
        valid[np.flatnonzero(iface)[~ok]] = False
    d = pdf(m, wo, wi)
    f = eval(m, wo, wi)
    weight = np.where(((d > 0) & valid)[:, None], f * np.abs(wi[:, 2])[:, None] / np.maximum(d, 1e-30)[:, None], 0.0)
    return wi, weight
