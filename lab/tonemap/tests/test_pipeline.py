import numpy as np
import pytest

from tmlab import blix_curves, color, pipeline
from tmlab.gt7 import GT7ToneMapping
from tmlab.pipeline import SceneConvention


def test_default_anchor_puts_scene_white_at_gt7_paper_white():
    # Blix 1.0 -> 250 nits -> frame-buffer 2.5, in every channel of a neutral (Rec.709 white is Rec.2020 white).
    gt7_in = pipeline.to_gt7_input(np.ones(3))
    assert np.allclose(gt7_in, 2.5, atol=1e-3)


def test_exposure_scales_input_linearly():
    a = pipeline.to_gt7_input(np.array([0.2, 0.3, 0.1]), exposure=1.0)
    b = pipeline.to_gt7_input(np.array([0.2, 0.3, 0.1]), exposure=3.0)
    assert np.allclose(b, 3.0 * a)


def test_sdr_output_is_display_linear_zero_one():
    rgb = np.random.default_rng(0).uniform(0.0, 40.0, size=(500, 3))
    out = pipeline.gt7_sdr(rgb)
    assert out.shape == rgb.shape and out.min() >= 0.0 and out.max() <= 1.0


def test_hdr_output_is_nits_and_bounded_by_peak():
    tm = GT7ToneMapping.hdr_display(1000.0)
    rgb = np.random.default_rng(1).uniform(0.0, 200.0, size=(500, 3))
    assert pipeline.gt7_hdr_nits(rgb, tm).max() <= 1000.0 + 1e-6


def test_grey_output_rises_with_anchor():
    grey = np.full(3, 0.18)
    outs = [pipeline.gt7_sdr(grey, conv=SceneConvention(a))[1] for a in (100, 203, 250, 400)]
    assert all(b > a for a, b in zip(outs, outs[1:]))


@pytest.mark.parametrize("curve", [blix_curves.aces, blix_curves.reinhard])
def test_matching_anchor_makes_mid_grey_agree(curve):
    anchor = pipeline.matching_anchor(curve)
    got = pipeline.gt7_sdr(np.full(3, 0.18), conv=SceneConvention(anchor))[1]
    assert abs(got - float(curve(np.array(0.18)))) < 1e-6
