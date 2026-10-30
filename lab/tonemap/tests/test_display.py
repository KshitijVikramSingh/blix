import pytest

from tmlab import display

SAMPLE = "screen 0 'Built-in Retina Display': current 3.9980008602142334 potential 16.0 scale 2.0\n"


def test_parse_reads_the_probe_line():
    (s,) = display.parse(SAMPLE)
    assert s.name == "Built-in Retina Display" and s.headroom == pytest.approx(3.998) and s.potential == 16.0 and s.scale == 2.0


def test_implied_ui_white_is_peak_over_headroom():
    (s,) = display.parse(SAMPLE)
    assert s.ui_white_nits(1600.0) == pytest.approx(400.2, abs=0.5)
    assert s.lowest_ui_white_nits(1600.0) == pytest.approx(100.0)


def test_headroom_below_one_is_treated_as_none():
    (s,) = display.parse("screen 0 'x': current 0.5 potential 16.0 scale 2.0")
    assert s.ui_white_nits(1000.0) == 1000.0
