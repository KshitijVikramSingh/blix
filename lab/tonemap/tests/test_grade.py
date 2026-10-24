import numpy as np
import pytest

from tmlab import aces2, grade, pipeline


def test_identity_grade_changes_nothing():
    rgb = np.random.default_rng(0).uniform(0.0, 5.0, size=(200, 3))
    assert np.allclose(grade.Grade().apply(rgb), rgb, atol=1e-9)


def test_saturation_zero_is_luma_and_contrast_pivots_at_mid_grey():
    rgb = np.array([[0.4, 0.2, 0.1]])
    grey = grade.Grade(saturation=0.0).apply(rgb)
    assert np.allclose(grey[0, 0], grey[0, 1]) and np.allclose(grey[0, 1], grey[0, 2])
    pivot = np.full((1, 3), 0.18)
    assert np.allclose(grade.Grade(contrast=1.3).apply(pivot), pivot)           # mid grey is the pivot
    assert grade.Grade(contrast=1.3).apply(np.full((1, 3), 1.0))[0, 1] > 1.0   # brighter goes brighter


def test_contrast_keeps_hue():
    rgb = np.array([[0.8, 0.4, 0.2]])
    out = grade.Grade(contrast=1.4).apply(rgb)
    assert np.allclose(out[0] / out[0, 1], rgb[0] / rgb[0, 1])  # the channel ratios are unchanged


def test_stratified_sample_covers_the_bright_tail():
    rng = np.random.default_rng(1)
    frame = rng.uniform(0.01, 0.3, size=(64, 64, 3))
    frame[:3, :3] = 50.0  # a few very bright pixels in a mostly dull frame
    sample = grade.stratified_sample(frame, per_bin=40)
    assert (sample @ grade.LUMA709 > 10).sum() == 9  # all nine: the top 0.5% stratum is taken whole (a uniform draw of 280 would hold ~0.6)


def test_fit_recovers_a_known_grade():
    # A target made by GT7 under a known grade must be recoverable (the fit sees only the target).
    rng = np.random.default_rng(2)
    pixels = np.exp(rng.normal(np.log(0.2), 1.0, size=(1500, 3)))
    truth = grade.Grade(ev=0.6, wb_r=0.15, wb_b=-0.1, contrast=1.12, saturation=1.25)
    tonemap = lambda x: pipeline.gt7_sdr(x)
    target = lambda x: tonemap(truth.apply(x))
    fitted, loss = grade.fit_grade(pixels, tonemap, target, starts=4)
    assert loss < 1e-4
    assert fitted.ev == pytest.approx(truth.ev, abs=0.1) and fitted.saturation == pytest.approx(truth.saturation, abs=0.1)
