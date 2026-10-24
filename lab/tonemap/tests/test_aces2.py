import numpy as np
import pytest

from tmlab import aces2, pipeline


def test_sdr_white_is_display_white_and_mid_grey_is_ten_nits():
    white = aces2.sdr(np.full(3, 1000.0))
    assert np.allclose(white, 1.0, atol=2e-3)                      # saturates at the display's white
    assert aces2.sdr(np.full(3, 0.18))[1] == pytest.approx(0.10, abs=0.005)  # ACES: scene 0.18 -> 10 nits of 100


def test_hdr_peak_is_the_display_peak_in_framebuffer_units():
    for peak in (1000, 4000):
        top = aces2.hdr(np.full(3, 1e4), peak)
        assert np.allclose(top, peak / 100.0, rtol=2e-3)


def test_grey_ramp_is_monotone_and_neutral():
    x = np.exp(np.linspace(np.log(1e-3), np.log(1e3), 300))
    y = aces2.sdr(np.stack([x, x, x], axis=-1))
    assert np.all(np.diff(y[:, 1]) >= -1e-6)
    assert np.allclose(y[:, 0], y[:, 1], atol=2e-3) and np.allclose(y[:, 2], y[:, 1], atol=2e-3)


def test_unsupported_hdr_peak_is_refused():
    with pytest.raises(ValueError):
        aces2.hdr(np.ones(3), 1600)


def test_matching_exposure_makes_mid_grey_agree_with_gt7():
    gt7_mid = float(pipeline.gt7_sdr(np.full(3, 0.18))[1])
    e = aces2.matching_exposure(aces2.sdr, gt7_mid)
    assert aces2.sdr(np.full(3, 0.18 * e))[1] == pytest.approx(gt7_mid, abs=1e-6)
    assert e > 1.0  # ACES is darker at mid grey than GT7's anchor, so the reference needs lifting
