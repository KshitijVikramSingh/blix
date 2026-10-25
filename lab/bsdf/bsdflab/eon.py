"""EON: the energy-preserving Oren-Nayar diffuse BRDF, as the paper gives it.

    Portsmouth, Kutz, Hill. "EON: A practical energy-preserving rough diffuse BRDF." Journal of Computer Graphics Techniques 14(1), 2025
    (arXiv 2410.18026), with reference GLSL at github.com/portsmouth/EON-diffuse.

What it replaces: the fixed Lambert lobe `albedo / pi`. EON is the Oren-Nayar family (a rough surface of Lambertian microfacets) with
two corrections that matter here: it conserves energy at every roughness (the qualitative Oren-Nayar models are too bright, most of all
backlit), and it adds the multiple-scatter lobe the single-scatter model lacks, so a white rough surface still reflects all the light.

    r      the diffuse roughness, 0..1. r = 0 IS Lambert, exactly.
    rho    the albedo (per channel; everything below is per channel and nothing couples the channels but the formulas' own rho).
    wi, wo unit vectors in the shading frame (z = the normal), pointing away from the surface toward the light and the eye.

The model is  f_EON = f_F + f_F^ms:

    f_F      = (rho / pi) * A * (1 + r * s / t)        the single-scatter "Fujii Oren-Nayar" lobe (Eq. 10)
               A = 1 / (1 + (1/2 - 2/(3 pi)) r),   s = wi.wo - mu_i mu_o,   s/t = s if s <= 0 else s / max(mu_i, mu_o)
    f_F^ms   = (rho_ms / pi) * (1 - E(mu_i)) (1 - E(mu_o)) / (1 - <E>)                                           (Eq. 19)
               rho_ms = rho^2 <E> / (1 - rho (1 - <E>)),   <E> = A (1 + (2/3 - 28/(15 pi)) r)                    (Eq. 15, 18)
               E(mu) = A (1 + r G(mu) / pi), the single-scatter directional albedo per unit rho, with G fitted by a quartic in (1 - mu)

The directional albedo of the whole model (Eq. 20) is  E_EON(mu_o) = rho E(mu_o) + rho_ms (1 - E(mu_o)).  At rho = 1 it is exactly 1 for every
mu_o and r: nothing is lost. For light that arrives from everywhere equally (an irradiance cache, an ambient term) the radiance out is
(E_irradiance / pi) * E_EON(mu_o): the albedo the lobe has toward the eye, in place of the Lambert albedo rho.
"""
from __future__ import annotations

import numpy as np

C1 = 0.5 - 2.0 / (3.0 * np.pi)            # constant1_FON in the paper's listing
C2 = 2.0 / 3.0 - 28.0 / (15.0 * np.pi)    # constant2_FON
G1, G2, G3, G4 = 0.0571085289, 0.491881867, -0.332181442, 0.0714429953   # Eq. 14: G(mu) ~ pi * sum g_k (1 - mu)^k, k = 1..4
EPS = 1e-7


def a_f(r):
    return 1.0 / (1.0 + C1 * np.asarray(r, dtype=np.float64))


def g_over_pi_poly(mu):
    """The quartic fit of G_F / pi, in (1 - mu) (Eq. 14)."""
    m = 1.0 - np.asarray(mu, dtype=np.float64)
    return m * (G1 + m * (G2 + m * (G3 + m * G4)))


def g_f_exact(mu):
    """G_F(theta) = G_q(theta) - (2/3) sin(theta), Eqs. 8 and 13: the exact function the quartic fits. mu = cos(theta)."""
    mu = np.clip(np.asarray(mu, dtype=np.float64), 1e-9, 1.0)
    si = np.sqrt(np.maximum(1.0 - mu * mu, 0.0))
    g_q = si * (np.arccos(mu) - si * mu) + (2.0 / 3.0) * ((si / mu) * (1.0 - si ** 3))
    return g_q - (2.0 / 3.0) * si


def e_f(mu, r, exact: bool = False):
    """Single-scatter directional albedo per unit rho: A (1 + r G / pi). `exact` uses G_F itself rather than the quartic."""
    g = g_f_exact(mu) / np.pi if exact else g_over_pi_poly(mu)
    return a_f(r) * (1.0 + np.asarray(r, dtype=np.float64) * g)


def avg_e_f(r):
    """The cosine-weighted average of E over the hemisphere (Eq. 15)."""
    return a_f(r) * (1.0 + C2 * np.asarray(r, dtype=np.float64))


def f_lambert(rho):
    return np.asarray(rho, dtype=np.float64) / np.pi


def _cosines(wi, wo):
    wi, wo = np.asarray(wi, dtype=np.float64), np.asarray(wo, dtype=np.float64)
    return wi[..., 2], wo[..., 2], np.sum(wi * wo, axis=-1)


def _scale(rho_term, factor):
    """rho_term (scalar, or per-channel (3,)) times a per-direction factor of any shape: (...) -> (...) or (..., 3)."""
    rho_term = np.asarray(rho_term, dtype=np.float64)
    return rho_term * factor[..., None] if rho_term.ndim else rho_term * factor


def f_single_scatter(rho, r, wi, wo):
    mu_i, mu_o, dot = _cosines(wi, wo)
    s = dot - mu_i * mu_o
    s_over_t = np.where(s > 0.0, s / np.maximum(np.maximum(mu_i, mu_o), EPS), s)
    return _scale(np.asarray(rho, dtype=np.float64) / np.pi, a_f(r) * (1.0 + r * s_over_t))


def f_multiple_scatter(rho, r, wi, wo):
    mu_i, mu_o, _ = _cosines(wi, wo)
    avg = avg_e_f(r)
    rho = np.asarray(rho, dtype=np.float64)
    rho_ms = rho * rho * avg / (1.0 - rho * (1.0 - avg))
    shape = np.maximum(EPS, 1.0 - e_f(mu_i, r)) * np.maximum(EPS, 1.0 - e_f(mu_o, r)) / np.maximum(EPS, 1.0 - avg)
    return _scale(rho_ms / np.pi, shape)


def f_eon(rho, r, wi, wo):
    """The EON BRDF, f(wi, wo): scalar or per-channel rho, any shape of (wi, wo) vectors (last axis 3)."""
    return f_single_scatter(rho, r, wi, wo) + f_multiple_scatter(rho, r, wi, wo)


def albedo_closed_form(rho, r, mu_o, exact: bool = False):
    """E_EON(mu_o) = rho E + rho_ms (1 - E) (Eq. 20): the fraction of light from mu_o's direction the model reflects, any incident distribution
    cosine-weighted (equivalently: radiance out per unit of uniform incident radiance)."""
    rho = np.asarray(rho, dtype=np.float64)
    avg = avg_e_f(r)
    rho_ms = rho * rho * avg / (1.0 - rho * (1.0 - avg))
    e = e_f(mu_o, r, exact)
    return rho * e + rho_ms * (1.0 - e)


def hemisphere(n_mu: int = 96, n_phi: int = 96):
    """Quadrature over the upper hemisphere in (mu, phi): Gauss-Legendre in mu, uniform in phi. Returns (wi (n,3), weight (n,)) with the
    weights integrating dOmega (they sum to 2 pi)."""
    x, w = np.polynomial.legendre.leggauss(n_mu)
    mu = 0.5 * (x + 1.0)
    wmu = 0.5 * w
    phi = (np.arange(n_phi) + 0.5) * (2.0 * np.pi / n_phi)
    st = np.sqrt(1.0 - mu * mu)
    wi = np.stack([np.outer(st, np.cos(phi)), np.outer(st, np.sin(phi)), np.outer(mu, np.ones(n_phi))], axis=-1).reshape(-1, 3)
    weight = np.outer(wmu, np.full(n_phi, 2.0 * np.pi / n_phi)).reshape(-1)
    return wi, weight


def albedo_numeric(f, mu_o: float, n_mu: int = 96, n_phi: int = 96):
    """The directional albedo of a BRDF callable f(wi, wo) by quadrature: the integral of f cos(theta_i) dOmega_i (per channel if f is)."""
    wi, weight = hemisphere(n_mu, n_phi)
    wo = np.array([np.sqrt(1.0 - mu_o * mu_o), 0.0, mu_o])
    vals = f(wi, np.broadcast_to(wo, wi.shape))
    w = wi[:, 2] * weight
    return np.sum(vals * (w[:, None] if vals.ndim == 2 else w), axis=0)
