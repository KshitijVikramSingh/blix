"""The shared GLSL (src/Blix.Shaders/bsdf_gltf.glsl) against the python reference, per pixel.

The TonemapLab demo's patterns 6 and 7 render the BSDF's evaluation and densities for a table of materials, view angles and light directions
(`--pattern 6|7`, raw); this recomputes the tables in python. Needs the demo built in Release (and a GPU); skips otherwise. The renders are RGBA16F,
so agreement is to a half-float step, and the narrow lobes' peaks are compared with a relative tolerance of a few thousandths of the table's largest value.
"""
import os
import subprocess
from pathlib import Path

import numpy as np
import pytest

from bsdflab import dielectric, gltf_bsdf as g
from bsdflab.gltf_bsdf import Material
from bsdflab.pfm import read_pfm

LAB = Path(__file__).resolve().parents[1]
REPO = LAB.parents[1]
BUILT = (REPO / "src/Demos/Blix.Demos.TonemapLab/bin/Release/net8.0/Blix.Demos.TonemapLab.dll").exists()
pytestmark = pytest.mark.skipif(not BUILT, reason="the TonemapLab demo is not built in Release")

MATERIALS = [
    Material(base_color=(0.7, 0.4, 0.2), roughness=0.5),
    Material(base_color=(0.9, 0.6, 0.3), metallic=1.0, roughness=0.6),
    Material(base_color=(0.8, 0.8, 0.8), metallic=0.5, roughness=0.5),
    Material(base_color=(0.9, 0.95, 1.0), roughness=0.5, transmission=1.0),
    Material(base_color=(0.5, 0.8, 0.6), roughness=0.4, transmission=0.8, ior=1.45),
    Material(base_color=(0.3, 0.5, 0.2), roughness=0.6, diffuse_transmission=0.6, diffuse_transmission_color=(0.5, 0.7, 0.2), diffuse_roughness=0.5),
    Material(base_color=(0.5, 0.5, 0.5), roughness=0.5, specular=0.5, specular_color=(1.0, 0.8, 0.6), ior=1.8),
    Material(base_color=(0.9, 1.0, 0.95), roughness=0.45, transmission=1.0, volume=True),
]
VIEW = [0.9, 0.5, 0.2]
TINT = (1.0, 0.8, 0.6)


def render(tmp_path_factory, pattern):
    out = tmp_path_factory.mktemp(f"gltf{pattern}") / "table"
    env = {k: v for k, v in os.environ.items() if k != "BLIX_HDR"}
    done = subprocess.run([str(REPO / "blix"), "run", "Blix.Demos.TonemapLab", "--pattern", str(pattern), "--exposure", "0", "--capture", str(out),
                           "--capture-frame", "8"], cwd=REPO, env={**env, "BLIX_CONFIG": "Release"}, capture_output=True, text=True, timeout=300)
    assert done.returncode == 0, done.stdout[-600:] + done.stderr[-600:]
    return read_pfm(out.with_suffix(".pfm"))


def cells(h, w, stride):
    ys, xs = np.mgrid[0:h:stride, 0:w:stride]
    u, v = (xs + 0.5) / w, (ys + 0.5) / h
    col = np.floor(np.clip(u, 0, 0.99999) * 256)
    wi_z = -0.99 + 1.98 * col / 255.0
    row = np.clip((v * 48).astype(int), 0, 47)
    return ys, wi_z, row


def directions(wi_z, cos_o, side):
    sin_i = np.sqrt(np.maximum(1 - wi_z ** 2, 0))
    wi = np.array([sin_i * np.cos(0.6), sin_i * np.sin(0.6), wi_z])
    wo = np.array([np.sqrt(1 - cos_o ** 2), 0.0, -cos_o if side else cos_o])
    return wi, wo


def tolerance(want):
    return np.maximum(4e-3 * np.abs(want), 4e-3 * np.abs(want).max() * 0.05 + 2e-3)


@pytest.fixture(scope="module")
def eval_table(tmp_path_factory):
    return render(tmp_path_factory, 6)


@pytest.fixture(scope="module")
def pdf_table(tmp_path_factory):
    return render(tmp_path_factory, 7)


def test_glsl_eval_matches_python_per_pixel(eval_table):
    h, w, _ = eval_table.shape
    stride = 29
    ys, wi_z, row = cells(h, w, stride)
    got = eval_table[::stride, ::stride].astype(np.float64)
    for k in np.ndindex(ys.shape):
        r = row[k]
        mi, rest = r // 6, r % 6
        vi, side = rest // 2, rest % 2
        wi, wo = directions(wi_z[k], VIEW[vi], side)
        want = np.zeros(3) if side else np.pi * abs(wi[2]) * g.eval(MATERIALS[mi], wo[None], wi[None])[0]
        err = np.abs(got[k] - want)
        assert np.all(err <= tolerance(want)), (mi, vi, side, wi_z[k], got[k], want)


def test_glsl_densities_match_python_per_pixel(pdf_table):
    h, w, _ = pdf_table.shape
    stride = 29
    ys, wi_z, row = cells(h, w, stride)
    got = pdf_table[::stride, ::stride].astype(np.float64)
    for k in np.ndindex(ys.shape):
        r = row[k]
        mi, rest = r // 6, r % 6
        vi, side = rest // 2, rest % 2
        wi, wo = directions(wi_z[k], VIEW[vi], side)
        m = MATERIALS[mi]
        iface = dielectric.Interface(alpha=m.alpha, eta_out=1.0, eta_in=m.ior, tint=TINT)
        want = np.array([0.0 if side else g.pdf(m, wo[None], wi[None])[0],
                         abs(wi[2]) * dielectric.eval(iface, wo[None], wi[None])[0][1],
                         dielectric.pdf(iface, wo[None], wi[None])[0]])
        err = np.abs(got[k] - want)
        assert np.all(err <= tolerance(want)), (mi, vi, side, wi_z[k], got[k], want)
