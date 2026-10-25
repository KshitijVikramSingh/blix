"""Motion blur specs (see motionblur.py): energy kept, contrast kept, and what the sample count costs."""
import numpy as np
import pytest

from tmlab import motionblur as mb


def lamp_scan(n=400, lamp=5000.0, ambient=1.0, width=3):
    scan = np.full(n, ambient)
    scan[n // 2: n // 2 + width] = lamp
    return scan


def test_sample_positions_are_centred_and_even():
    s = mb.sweep_samples(8)
    assert s.mean() == pytest.approx(0.0, abs=1e-12)
    assert np.diff(s) == pytest.approx(1 / 8)
    assert abs(s).max() < 0.5


def test_smear_conserves_the_lamps_light():
    scan = lamp_scan()
    blurred = mb.blur_1d(scan, sweep_px=40.0)
    assert blurred.sum() == pytest.approx(scan.sum(), rel=1e-3)


def test_zero_sweep_is_identity():
    scan = lamp_scan()
    assert mb.blur_1d(scan, 0.0) == pytest.approx(scan)


def test_linear_accumulation_keeps_a_streak_brighter_than_display_averaging():
    """A 5000x lamp smeared over 40 px: tone-map AFTER averaging radiance gives a bright streak; averaging the tone-mapped
    values (what a display-referred blur does) gives a much dimmer one, because the tone map has already clipped the lamp."""
    scan = lamp_scan()
    tone = lambda x: x / (1.0 + x)     # any compressive map shows it; Reinhard is the simplest
    linear_first = tone(mb.blur_1d(scan, 40.0))
    display_first = mb.blur_1d(tone(scan), 40.0)
    mid = slice(200 - 15, 200 + 15)
    assert linear_first[mid].mean() > 1.2 * display_first[mid].mean()
    assert linear_first[mid].mean() > 0.6    # still a bright streak
    assert display_first[mid].mean() < 0.55  # a 3-px lamp clipped to <= 1 then spread over 40 px
