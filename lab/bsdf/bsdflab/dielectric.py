"""A rough refracting interface and the volume behind it: what a transmitting SOLID does at its surface (KHR_materials_volume), where gltf_bsdf's
thin sheet only passes the ray straight on.

The interface is Walter, Marschner, Li and Torrance, "Microfacet Models for Refraction through Rough Surfaces" (EGSR 2007): GGX normals, the exact
unpolarised dielectric Fresnel, reflection with F and refraction with 1 - F along Snell's law at the microfacet's normal. Its transmission lobe is
the BTDF for RADIANCE (its eta_o^2 numerator is the radiance scaling across the interface: a ray entering glass is brighter by eta^2, which the
ray leaving it takes back), so a path tracer applies nothing more at a crossing.

The frame is the surface's, +z toward the OUTSIDE medium (index eta_out) and the solid's inside below (eta_in). wo is the direction toward the viewer on
either side; the lobes read it by flipping the frame (and swapping the indices) when it points inside.

    eval(i, wo, wi)    f(wo, wi), the transmitted part tinted by `tint` (the base colour, per KHR_materials_transmission)
    pdf(i, wo, wi)     the density `sample` draws wi at
    sample(i, wo, rng) wi and the weight f |cos| / pdf; the Fresnel coin is thrown on the sampled microfacet

and the medium: Beer-Lambert's attenuation coefficient from KHR_materials_volume's colour and distance, sigma = -ln(colour) / distance per channel, and the
fraction left after a path of length d, exp(-sigma d). A distance of infinity (the spec's default) attenuates nothing.
"""
from __future__ import annotations

from dataclasses import dataclass

import numpy as np

from .gltf_bsdf import _dot, _norm, _sample_vndf, g1, g2, ggx_d

PI = np.pi


@dataclass(frozen=True)
class Interface:
    alpha: float = 0.05
    eta_out: float = 1.0
    eta_in: float = 1.5
    tint: tuple = (1.0, 1.0, 1.0)


def fresnel_exact(cos_i, eta_i, eta_t):
    """Unpolarised dielectric Fresnel for light arriving at cos_i from the medium eta_i into eta_t; 1 beyond the critical angle."""
    cos_i = np.clip(cos_i, 0.0, 1.0)
    sin2_t = (eta_i / eta_t) ** 2 * (1.0 - cos_i * cos_i)
    tir = sin2_t >= 1.0
    cos_t = np.sqrt(np.maximum(1.0 - sin2_t, 0.0))
    rs = (eta_i * cos_i - eta_t * cos_t) / np.maximum(eta_i * cos_i + eta_t * cos_t, 1e-12)
    rp = (eta_t * cos_i - eta_i * cos_t) / np.maximum(eta_t * cos_i + eta_i * cos_t, 1e-12)
    return np.where(tir, 1.0, 0.5 * (rs * rs + rp * rp))


def _frame(i: Interface, wo, wi):
    """Flip so wo is above, swapping the indices; returns (wo, wi, eta_view, eta_other, flipped)."""
    flip = wo[..., 2] < 0
    s = np.where(flip, -1.0, 1.0)[..., None]
    eta_v = np.where(flip, i.eta_in, i.eta_out)
    eta_o = np.where(flip, i.eta_out, i.eta_in)
    return wo * s, wi * s, eta_v, eta_o, flip


def _half(wo, wi, eta_v, eta_l):
    """The (generalised) half vector of the pair, +z, and whether the pair is a transmission."""
    refl = wi[..., 2] > 0
    hr = _norm(wo + wi)
    ht = -(eta_l[..., None] * wi + eta_v[..., None] * wo)
    ht = ht / np.maximum(np.linalg.norm(ht, axis=-1, keepdims=True), 1e-12)
    h = np.where(refl[..., None], hr, ht)
    h = np.where((h[..., 2] < 0)[..., None], -h, h)
    return h, refl


def eval(i: Interface, wo, wi):
    wo, wi = np.asarray(wo, float), np.asarray(wi, float)
    wo, wi, eta_v, eta_other, _ = _frame(i, wo, wi)
    refl = wi[..., 2] > 0
    eta_l = np.where(refl, eta_v, eta_other)
    h, _ = _half(wo, wi, eta_v, eta_l)
    nv, nl, nh = wo[..., 2], np.abs(wi[..., 2]), h[..., 2]
    vh, lh = _dot(wo, h), _dot(wi, h)
    a = i.alpha
    d, gg = ggx_d(np.maximum(nh, 0.0), a), g2(np.maximum(nl, 1e-9), np.maximum(nv, 1e-9), a)
    f = fresnel_exact(vh, eta_v, eta_other)
    # Reflection needs the microfacet to face both directions; refraction needs them on opposite sides of it.
    ok_r = refl & (vh > 0) & (lh > 0) & (nv > 0) & (nh > 0)
    fr = np.where(ok_r, f * d * gg / (4.0 * nv * np.maximum(nl, 1e-9)), 0.0)
    denom = (eta_l * lh + eta_v * vh) ** 2
    ok_t = (~refl) & (vh > 0) & (lh < 0) & (nv > 0) & (nh > 0) & (denom > 1e-12)
    ft = np.where(ok_t, np.abs(lh) * vh / (nv * np.maximum(nl, 1e-9)) * eta_v ** 2 * (1.0 - f) * gg * d / np.maximum(denom, 1e-12), 0.0)
    return np.where(refl, fr, ft)[..., None] * np.where(refl[..., None], 1.0, np.asarray(i.tint, float))


def pdf(i: Interface, wo, wi):
    wo, wi = np.asarray(wo, float), np.asarray(wi, float)
    wo, wi, eta_v, eta_other, _ = _frame(i, wo, wi)
    refl = wi[..., 2] > 0
    eta_l = np.where(refl, eta_v, eta_other)
    h, _ = _half(wo, wi, eta_v, eta_l)
    nv, nh = np.maximum(wo[..., 2], 1e-9), h[..., 2]
    vh, lh = _dot(wo, h), _dot(wi, h)
    a = i.alpha
    f = fresnel_exact(vh, eta_v, eta_other)
    dv = g1(nv, a) * ggx_d(np.maximum(nh, 0.0), a) * np.maximum(vh, 0.0) / nv      # the visible normals' density at h
    pr = np.where(refl & (vh > 0) & (lh > 0), f * dv / np.maximum(4.0 * vh, 1e-12), 0.0)
    denom = (eta_l * lh + eta_v * vh) ** 2
    pt = np.where((~refl) & (vh > 0) & (lh < 0) & (denom > 1e-12), (1.0 - f) * dv * eta_l ** 2 * np.abs(lh) / np.maximum(denom, 1e-12), 0.0)
    return np.where(refl, pr, pt)


def _refract(wo, h, eta_v, eta_l):
    """Snell: the direction below the surface light arrives from, for the view direction wo at the microfacet h; None-like zeros on TIR."""
    c = _dot(wo, h)
    k = eta_v / eta_l
    sin2 = k * k * (1.0 - c * c)
    ok = sin2 < 1.0
    ct = np.sqrt(np.maximum(1.0 - sin2, 0.0))
    return (k * -wo + (k * c - ct)[..., None] * h) * -1.0, ok


def sample(i: Interface, wo, rng):
    wo = np.asarray(wo, float)
    n = wo.shape[0]
    flip = wo[:, 2] < 0
    s = np.where(flip, -1.0, 1.0)[:, None]
    wf = wo * s
    eta_v = np.where(flip, i.eta_in, i.eta_out)
    eta_o = np.where(flip, i.eta_out, i.eta_in)
    h = _sample_vndf(wf, i.alpha, rng)
    vh = _dot(wf, h)
    f = fresnel_exact(vh, eta_v, eta_o)
    reflect = rng.random(n) < f
    wr = 2.0 * vh[:, None] * h - wf
    # Transmitted: light arrives from below, along the refraction of -wo through h (Snell, eta_v -> eta_o).
    k = eta_v / eta_o
    c = vh
    sin2 = k * k * (1.0 - c * c)
    ct = np.sqrt(np.maximum(1.0 - sin2, 0.0))
    wt = -k[:, None] * wf + (k * c - ct)[:, None] * h
    wi = np.where(reflect[:, None], wr, wt)
    valid = np.where(reflect, wr[:, 2] > 0, (wt[:, 2] < 0) & (sin2 < 1.0))
    wi = wi * s
    d = pdf(i, wo, wi)
    fval = eval(i, wo, wi)
    weight = np.where((valid & (d > 0))[:, None], fval * np.abs(wi[:, 2])[:, None] / np.maximum(d, 1e-30)[:, None], 0.0)
    return wi, weight


def attenuation_coefficient(color, distance):
    """KHR_materials_volume: sigma_t per channel from attenuationColor and attenuationDistance (infinity: none)."""
    color = np.clip(np.asarray(color, float), 1e-6, 1.0)
    if not np.isfinite(distance):
        return np.zeros(3)
    return -np.log(color) / distance


def transmittance(color, distance, length):
    """What is left of the light after `length` through the medium (Beer-Lambert)."""
    return np.exp(-attenuation_coefficient(color, distance) * length)
