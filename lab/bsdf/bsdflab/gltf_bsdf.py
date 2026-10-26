"""The glTF 2.0 surface model as a BSDF a path tracer can evaluate and sample: the reference the references are built from.

Appendix B of the glTF specification (the metallic-roughness BRDF) with the extensions that change what a ray does at a surface:

    KHR_materials_ior                   the dielectric's index of refraction (Fresnel's f0 = ((ior - 1) / (ior + 1))^2)
    KHR_materials_specular              the dielectric's f0 tint and f90 (the share of light the grazing Fresnel reaches)
    KHR_materials_transmission          a share of the dielectric's base layer is transmitted: a THIN-WALLED rough BTDF (below)
    KHR_materials_diffuse_transmission  a share of the diffuse base is Lambertian TRANSMISSION, in its own colour

    KHR_materials_clearcoat             a second, smoother GGX layer (ior 1.5) over everything beneath it, weighted by clearcoat x its Fresnel
    KHR_materials_sheen                 the Charlie lobe over the base (engine sheen.glsl's rule: sheen + (1 - max(colour) E(NV)) base)
    KHR_materials_iridescence           the specular Fresnel replaced by a thin film's (Belcour and Barla 2017, as the Khronos viewer evaluates it)
    KHR_materials_anisotropy            the specular lobe's GGX anisotropic: alpha_t = mix(alpha, 1, strength^2) along the rotated tangent
    KHR_materials_volume                (with a thickness) the transmitted share is a refracting interface -- see `solid` below and dielectric.py

Emission is not a lobe: the path tracer adds it at a hit. Nested solids are the path tracer's: `eta_out` is the index of the medium the surface is entered from
(1 in air, the enclosing solid's inside it).

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
    clearcoat: float = 0.0                      # KHR_materials_clearcoat factor and roughness
    clearcoat_roughness: float = 0.0
    sheen_color: tuple = (0.0, 0.0, 0.0)        # KHR_materials_sheen colour and roughness
    sheen_roughness: float = 0.0
    iridescence: float = 0.0                    # KHR_materials_iridescence factor, the film's ior and its thickness in nm
    iridescence_ior: float = 1.3
    iridescence_thickness: float = 400.0
    anisotropy: float = 0.0                     # KHR_materials_anisotropy strength and rotation (radians, from the tangent)
    anisotropy_rotation: float = 0.0
    eta_out: float = 1.0                        # the index of the medium this surface is entered from (nested solids)
    volume: bool = False                        # KHR_materials_volume with a thickness: a SOLID, its transmission a refracting interface (dielectric.py)
    attenuation_color: tuple = (1.0, 1.0, 1.0)  # the medium inside (Beer-Lambert), used by the path tracer
    attenuation_distance: float = float("inf")

    @property
    def alpha(self) -> float:
        return max(self.roughness * self.roughness, 1e-3)

    def interface(self):
        """The solid's surface: a rough dielectric between air and the material's ior, its refracted light tinted by the base colour."""
        from .dielectric import Interface
        return Interface(alpha=self.alpha, eta_out=self.eta_out, eta_in=self.ior, tint=self.base_color)

    @property
    def alpha_coat(self) -> float:
        return max(self.clearcoat_roughness * self.clearcoat_roughness, 1e-3)

    @property
    def alpha_sheen(self) -> float:
        return max(self.sheen_roughness * self.sheen_roughness, 1e-4)

    @property
    def alphas(self):
        """The specular lobe's (alpha_t, alpha_b): along the anisotropy direction, and across it."""
        a = self.alpha
        return (a + (1.0 - a) * self.anisotropy ** 2, a)

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
    """KHR_materials_specular: min(((ior - 1) / (ior + 1))^2 specularColor, 1) specular -- the factor scales f0 as it scales f90."""
    f0 = ((m.ior - 1.0) / (m.ior + 1.0)) ** 2
    return np.minimum(f0 * np.asarray(m.specular_color, float), 1.0) * m.specular


# ---- The thin film (KHR_materials_iridescence) -----------------------------------------------------------------------------------------------
# Belcour and Barla, "A Practical Extension to Microfacet Theory for the Modeling of Varying Iridescence" (SIGGRAPH 2017), in the form the Khronos glTF
# sample viewer evaluates it: a film of index `eta2` and thickness (nm) over a base of Fresnel reflectance f0, seen from air; the interference summed over the
# first two orders with Gaussian-fitted XYZ sensitivities, returned as the Rec.709 reflectance of the whole stack.

_XYZ_TO_REC709 = np.array([[3.2404542, -1.5371385, -0.4985314], [-0.9692660, 1.8760108, 0.0415560], [0.0556434, -0.2040259, 1.0572252]])


def _sensitivity(opd, shift):
    phase = 2.0 * PI * opd * 1e-9
    val = np.array([5.4856e-13, 4.4201e-13, 5.2481e-13])
    pos = np.array([1.6810e06, 1.7953e06, 2.2084e06])
    var = np.array([4.3278e09, 9.3046e09, 6.6121e09])
    xyz = val * np.sqrt(2.0 * PI * var) * np.cos(pos * phase[..., None] + shift) * np.exp(-var * (phase * phase)[..., None])
    xyz[..., 0] += 9.7470e-14 * np.sqrt(2.0 * PI * 4.5282e09) * np.cos(2.2399e06 * phase + shift[..., 0]) * np.exp(-4.5282e09 * phase * phase)
    return (xyz / 1.0685e-7) @ _XYZ_TO_REC709.T


def _smoothstep(a, b, x):
    t = np.clip((x - a) / (b - a), 0.0, 1.0)
    return t * t * (3.0 - 2.0 * t)


def iridescence_fresnel(outside_ior, eta2, cos1, thickness, base_f0):
    """The stack's Rec.709 reflectance at cos1 (n,), for a film `eta2` thick `thickness` nm over a base of reflectance base_f0 (3,) or (n, 3)."""
    cos1 = np.clip(np.asarray(cos1, float), 0.0, 1.0)
    base_f0 = np.broadcast_to(np.asarray(base_f0, float), cos1.shape + (3,))
    iri_ior = outside_ior + (eta2 - outside_ior) * _smoothstep(0.0, 0.03, thickness)
    sin2_t = (outside_ior / iri_ior) ** 2 * (1.0 - cos1 * cos1)
    cos2_sq = 1.0 - sin2_t
    tir = cos2_sq < 0.0
    cos2 = np.sqrt(np.maximum(cos2_sq, 0.0))
    r0 = ((iri_ior - outside_ior) / (iri_ior + outside_ior)) ** 2
    r12 = r0 + (1.0 - r0) * (1.0 - cos1) ** 5
    t121 = 1.0 - r12
    phi12 = PI if iri_ior < outside_ior else 0.0
    phi21 = PI - phi12
    f0 = np.clip(base_f0, 0.0, 0.9999)
    base_ior = (1.0 + np.sqrt(f0)) / (1.0 - np.sqrt(f0))
    r1 = ((base_ior - iri_ior) / (base_ior + iri_ior)) ** 2
    r23 = r1 + (1.0 - r1) * ((1.0 - cos2) ** 5)[..., None]
    phi23 = np.where(base_ior < iri_ior, PI, 0.0)
    opd = 2.0 * iri_ior * thickness * cos2
    phi = phi21 + phi23
    r123 = np.clip(r12[..., None] * r23, 1e-5, 0.9999)
    sq = np.sqrt(r123)
    rs = (t121 * t121)[..., None] * r23 / (1.0 - r123)
    i = r12[..., None] + rs
    cm = rs - t121[..., None]
    for k in (1, 2):
        cm = cm * sq
        i = i + cm * 2.0 * _sensitivity(k * opd, k * phi)
    return np.where(tir[..., None], 1.0, np.maximum(i, 0.0))


def fresnels(m: Material, vh):
    """(the dielectric's Fresnel, the metal's), rgb (..., 3): KHR_materials_specular's Schlick for the dielectric, base colour's for the metal, each replaced by the film's
    stack in proportion to the iridescence factor."""
    vh = np.clip(np.asarray(vh, float), 0.0, 1.0)
    k = ((1.0 - vh) ** 5)[..., None]
    f0 = f0_dielectric(m)
    fd = f0 + (m.specular - f0) * k
    fm_ = np.asarray(m.base_color, float)
    fm = fm_ + (1.0 - fm_) * k
    if m.iridescence > 0.0:
        i = m.iridescence
        fd = (1.0 - i) * fd + i * iridescence_fresnel(1.0, m.iridescence_ior, vh, m.iridescence_thickness, f0)
        fm = (1.0 - i) * fm + i * iridescence_fresnel(1.0, m.iridescence_ior, vh, m.iridescence_thickness, fm_)
    return fd, fm


def fresnel_dielectric(m: Material, vh):
    return fresnels(m, vh)[0]


def fresnel_metal(m: Material, vh):
    return fresnels(m, vh)[1]


# ---- GGX, isotropic and anisotropic ----------------------------------------------------------------------------------------------------------

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


def _rot(v, r):
    """v with x and y turned by -r: the frame whose x axis is the anisotropy direction."""
    c, s = np.cos(r), np.sin(r)
    out = np.array(v, float, copy=True)
    out[..., 0] = v[..., 0] * c + v[..., 1] * s
    out[..., 1] = -v[..., 0] * s + v[..., 1] * c
    return out


def aniso_d(h, ax, ay, rot):
    hr = _rot(h, rot)
    d = (hr[..., 0] / ax) ** 2 + (hr[..., 1] / ay) ** 2 + hr[..., 2] ** 2
    return 1.0 / (PI * ax * ay * d * d)


def aniso_lambda(w, ax, ay, rot):
    wr = _rot(w, rot)
    c2 = np.maximum(w[..., 2] ** 2, 1e-12)
    return 0.5 * (np.sqrt(1.0 + (ax * ax * wr[..., 0] ** 2 + ay * ay * wr[..., 1] ** 2) / c2) - 1.0)


def _mirror(wi):
    out = np.array(wi, float, copy=True)
    out[..., 2] = np.abs(out[..., 2])
    return out


def spec_brdf(wo, wi, ax, ay=None, rot=0.0):
    """D G2 / (4 NL NV) for wo and wi both above the surface; 0 elsewhere. ay None: isotropic at ax."""
    ay = ax if ay is None else ay
    h = _norm(wo + wi)
    nl, nv, nh = wi[..., 2], wo[..., 2], h[..., 2]
    ok = (nl > 0) & (nv > 0) & (nh > 0)
    safe_l, safe_v = np.where(ok, nl, 1.0), np.where(ok, nv, 1.0)
    g = 1.0 / (1.0 + aniso_lambda(wo, ax, ay, rot) + aniso_lambda(wi, ax, ay, rot))
    return np.where(ok, aniso_d(h, ax, ay, rot) * g / (4.0 * safe_l * safe_v), 0.0)


# ---- Sheen (KHR_materials_sheen), as the engine's sheen.glsl ----------------------------------------------------------------------------------

def charlie_d(nh, alpha):
    sin2h = np.maximum(1.0 - nh * nh, 1e-7)
    return (2.0 + 1.0 / alpha) * sin2h ** (0.5 / alpha) / (2.0 * PI)


def sheen_visibility(nl, nv):
    return np.clip(1.0 / (4.0 * (nl + nv - nl * nv)), 0.0, 1.0)


SHEEN_QUADRATURE = (24, 16)   # midpoint cells in cos(theta) and phi: blix_gltfSheenAlbedo in the GLSL takes the same grid


def sheen_albedo(mu_o, sheen_roughness, grid=SHEEN_QUADRATURE):
    """E(NV, roughness): the Charlie lobe's directional albedo, the integral of D V cos over the hemisphere, by a midpoint grid (the glTF spec ships a table)."""
    alpha = max(sheen_roughness * sheen_roughness, 1e-4)
    mu_o = np.atleast_1d(np.asarray(mu_o, float))
    nmu, nphi = grid
    mu = (np.arange(nmu) + 0.5) / nmu
    phi = (np.arange(nphi) + 0.5) / nphi * 2.0 * PI
    mm, pp = np.meshgrid(mu, phi, indexing="ij")
    st = np.sqrt(1.0 - mm * mm)
    wi = np.stack([st * np.cos(pp), st * np.sin(pp), mm], axis=-1).reshape(-1, 3)
    out = np.zeros(mu_o.shape)
    for k, m_o in enumerate(mu_o):
        wo = np.array([np.sqrt(max(1.0 - m_o * m_o, 0.0)), 0.0, m_o])
        h = _norm(wo[None] + wi)
        f = charlie_d(h[:, 2], alpha) * sheen_visibility(wi[:, 2], m_o) * wi[:, 2]
        out[k] = f.sum() * (1.0 / nmu) * (2.0 * PI / nphi)
    return out


def eval(m: Material, wo, wi):
    """f(wo, wi), rgb (..., 3), without the cosine. wo must be above the surface (z > 0)."""
    wo, wi = np.asarray(wo, float), np.asarray(wi, float)
    wm = _mirror(wi)
    h = _norm(wo + wm)
    vh = _dot(wo, h)
    below = wi[..., 2] < 0
    bc = np.asarray(m.base_color, float)
    fr, fmetal = fresnels(m, vh)
    t, dt = m.transmission, m.diffuse_transmission
    ax, ay = m.alphas
    rot = m.anisotropy_rotation

    # Light above the surface: the specular layer, and the base's reflecting part.
    spec = spec_brdf(wo, wi, ax, ay, rot)[..., None]
    diffuse = eon.f_eon(bc, m.diffuse_roughness, np.where(below[..., None], wo, wi), wo)
    dielectric_up = fr * spec + (1.0 - fr) * ((1.0 - t) * (1.0 - dt) * diffuse)
    metal_up = fmetal * spec
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
    base = np.where(below[..., None], down, up)

    # Sheen over the base: its lobe added, the base darkened by what the sheen takes (sheen.glsl's rule; only the view's albedo, as the engine has it).
    sc = np.asarray(m.sheen_color, float)
    if sc.max() > 0.0:
        uniq, inverse = np.unique(np.round(wo[..., 2].reshape(-1), 6), return_inverse=True)   # a table's rows share their view angles
        e = sheen_albedo(uniq, m.sheen_roughness)[inverse].reshape(wo.shape[:-1])
        nl = np.maximum(wi[..., 2], 0.0)
        fs = np.where((wi[..., 2] > 0)[..., None], sc * (charlie_d(np.clip(h[..., 2], 0.0, 1.0), m.alpha_sheen) * sheen_visibility(nl, wo[..., 2]))[..., None], 0.0)
        base = fs + (1.0 - sc.max() * e)[..., None] * base

    # The clear coat over all of it: a smooth GGX layer of ior 1.5, weighted by the factor and its Fresnel at the half vector; what is under it is darkened by the coat's
    # reflectance toward the VIEW (Fresnel at N.V), as the Khronos viewer and the engine's sheen rule have it. The spec's fresnel_mix read literally weighs the base by
    # 1 - F(V.H) too, which gains energy at grazing (a plastic under a rough coat gave back 1.12 at cos 0.15): V.H of a diffuse bounce is far from N.V, and the coat
    # takes F(N.V) of the light whichever way it leaves.
    cc = m.clearcoat
    if cc > 0.0:
        fc = 0.04 + 0.96 * (1.0 - np.clip(vh, 0.0, 1.0)) ** 5
        fc_view = 0.04 + 0.96 * (1.0 - np.clip(wo[..., 2], 0.0, 1.0)) ** 5
        spec_c = spec_brdf(wo, wi, m.alpha_coat)[..., None]
        base = (1.0 - cc * fc_view)[..., None] * base + (cc * fc)[..., None] * spec_c
    return base


def lobe_probabilities(m: Material):
    """The seven lobes `sample` picks among: specular reflection, diffuse reflection, thin transmission, diffuse transmission, the solid's interface, the clear coat,
    the sheen."""
    t, dt, k = m.transmission, m.diffuse_transmission, m.metallic
    solid = m.volume and t > 0.0
    w = np.array([(0.2 if not solid else 0.2 * (1 - t)) + 0.8 * k, (1 - k) * (1 - t) * (1 - dt), 0.0 if solid else (1 - k) * t, (1 - k) * (1 - t) * dt,
                  (1 - k) * t if solid else 0.0, 0.25 * m.clearcoat, 0.3 * max(m.sheen_color)], float)
    return w / w.sum()


def _vndf_pdf(wo, wi_reflect, ax, ay=None, rot=0.0):
    """The density of the visible-normal sampled reflection: G1(wo) D(h) / (4 NV)."""
    ay = ax if ay is None else ay
    h = _norm(wo + wi_reflect)
    nv = np.maximum(wo[..., 2], 1e-9)
    g1o = 1.0 / (1.0 + aniso_lambda(wo, ax, ay, rot))
    return g1o * aniso_d(h, ax, ay, rot) * (h[..., 2] > 0) / (4.0 * nv)


def pdf(m: Material, wo, wi):
    wo, wi = np.asarray(wo, float), np.asarray(wi, float)
    p = lobe_probabilities(m)
    wm = _mirror(wi)
    below = wi[..., 2] < 0
    ax, ay = m.alphas
    cosine = np.abs(wi[..., 2]) / PI
    up = (p[0] * _vndf_pdf(wo, wm, ax, ay, m.anisotropy_rotation) + p[1] * cosine + p[5] * _vndf_pdf(wo, wm, m.alpha_coat) + p[6] * cosine)
    down = p[2] * _vndf_pdf(wo, wm, m.alpha_transmission) + p[3] * cosine
    if p[4] > 0.0:
        from . import dielectric
        pi_ = p[4] * dielectric.pdf(m.interface(), wo, wi)
        up, down = up + pi_, down + pi_
    return np.where(below, down, up)


def _cosine(n, rng):
    u1, u2 = rng.random(n), rng.random(n)
    r, phi = np.sqrt(u1), 2 * PI * u2
    return np.stack([r * np.cos(phi), r * np.sin(phi), np.sqrt(np.maximum(0.0, 1 - u1))], axis=-1)


def _sample_vndf(wo, ax, rng, ay=None, rot=0.0):
    """Heitz 2018: a microfacet normal from the distribution of normals visible from wo (anisotropic: alphas ax along the rotated x axis, ay across it)."""
    ay = ax if ay is None else ay
    n = wo.shape[0]
    wr = _rot(wo, rot)
    vh = _norm(np.stack([ax * wr[:, 0], ay * wr[:, 1], wr[:, 2]], axis=-1))
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
    hr = _norm(np.stack([ax * nh[:, 0], ay * nh[:, 1], np.maximum(0.0, nh[:, 2])], axis=-1))
    return _rot(hr, -rot)


def sample(m: Material, wo, rng):
    """One direction per row of wo (n, 3): wi (n, 3) and the weight f |cos| / pdf (n, 3). A direction below the surface is a transmitted one."""
    wo = np.asarray(wo, float)
    n = wo.shape[0]
    p = lobe_probabilities(m)
    lobe = rng.choice(7, size=n, p=p)
    wi = np.zeros((n, 3))
    valid = np.ones(n, bool)
    ax, ay = m.alphas
    for k, a_x, a_y, rot in ((0, ax, ay, m.anisotropy_rotation), (2, m.alpha_transmission, m.alpha_transmission, 0.0), (5, m.alpha_coat, m.alpha_coat, 0.0)):
        sel = lobe == k
        if sel.any():
            h = _sample_vndf(wo[sel], a_x, rng, a_y, rot)
            r = 2.0 * _dot(wo[sel], h)[:, None] * h - wo[sel]
            valid[np.flatnonzero(sel)[r[:, 2] <= 0]] = False   # a reflection below the horizon is no sample: the density counts only those above it
            if k == 2:
                r = r * np.array([1.0, 1.0, -1.0])
            wi[sel] = r
    for k in (1, 6):
        sel = lobe == k
        if sel.any():
            wi[sel] = _cosine(int(sel.sum()), rng)
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
