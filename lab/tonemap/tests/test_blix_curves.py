import re
from pathlib import Path

import numpy as np

from tmlab import blix_curves, color, metrics

REPO = Path(__file__).resolve().parents[3]
GLSL = (REPO / "src/Blix.Shaders/tonemap.glsl").read_text()
CS = (REPO / "src/Blix.Graphics.Images/Tonemap.cs").read_text()


def test_aces_constants_match_both_shipped_copies():
    cs = {k: float(re.search(rf"const float Aces{k} = ([0-9.]+)f;", CS).group(1)) for k in "ABCDE"}
    glsl = {k: float(re.search(rf"float {k.lower()} = ([0-9.]+);", GLSL).group(1)) for k in "ABCDE"}
    lab = dict(zip("ABCDE", (blix_curves.ACES_A, blix_curves.ACES_B, blix_curves.ACES_C,
                             blix_curves.ACES_D, blix_curves.ACES_E)))
    assert cs == glsl == lab


def test_agx_constants_match_both_shipped_copies():
    cs = (float(re.search(r"AgxMinEv = (-?[0-9.]+)f;", CS).group(1)),
          float(re.search(r"AgxMaxEv = (-?[0-9.]+)f;", CS).group(1)))
    glsl = (float(re.search(r"minEv = +(-?[0-9.]+);", GLSL).group(1)),
            float(re.search(r"maxEv = +(-?[0-9.]+);", GLSL).group(1)))
    assert cs == glsl == (blix_curves.AGX_MIN_EV, blix_curves.AGX_MAX_EV)


def test_mode_thresholds_match_the_shader():
    # <0.5 ACES, <1.5 AgX, <2.5 Reinhard, else neutral -- the convention both copies state.
    x = np.array([0.18, 1.0, 4.0])
    assert np.allclose(blix_curves.apply(x, 0.4), blix_curves.aces(x))
    assert np.allclose(blix_curves.apply(x, 1.4), blix_curves.agx(x))
    assert np.allclose(blix_curves.apply(x, 2.4), blix_curves.reinhard(x))
    assert np.allclose(blix_curves.apply(x, 2.5), blix_curves.neutral(x))


def test_simple_known_values():
    assert blix_curves.reinhard(1.0) == 0.5
    assert blix_curves.neutral(3.0) == 1.0 and blix_curves.neutral(-1.0) == 0.0
    assert blix_curves.aces(0.0) == 0.0
    assert blix_curves.aces(1e6) == 1.0


def test_pq_round_trip():
    v = np.array([0.0, 0.01, 0.18, 1.0, 10.0, 100.0])
    assert np.allclose(color.eotf_st2084(color.inverse_eotf_st2084(v)), v, rtol=1e-4, atol=1e-6)


def test_rec709_rec2020_round_trip_and_white():
    rgb = np.random.default_rng(3).uniform(0.0, 4.0, size=(100, 3))
    assert np.allclose(color.rec2020_to_rec709(color.rec709_to_rec2020(rgb)), rgb)
    # White maps to white (rows sum to 1).
    assert np.allclose(color.rec709_to_rec2020(np.ones(3)), np.ones(3), atol=2e-4)


def test_metrics_are_zero_for_identical_and_positive_otherwise():
    a = np.array([[100.0, 80.0, 60.0]])
    assert metrics.delta_e_itp(a, a)[0] == 0.0
    assert metrics.delta_e_itp(a, a * 1.1)[0] > 0.0
    w = np.array([[0.5, 0.4, 0.3]])
    assert metrics.delta_oklab(w, w)[0] == 0.0
    assert metrics.delta_oklab(w, w * 1.2)[0] > 0.0
