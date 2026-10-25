"""The scene target stores light / 32 so a sun far above half-float range survives (see the README). What that costs in the dark."""
import numpy as np
import pytest

SCALE = 32.0
SUN_CAP_NITS = 5.0e8      # scene.frag's cap on the sun's radiance
SCENE_UNIT_NITS = 250.0


def rel_step(x: float, scale: float) -> float:
    """Spacing to the next half-float above x/scale, relative to it."""
    h = np.float16(x / scale)
    return float((np.nextafter(h, np.float16(np.inf)) - h) / h)


def test_the_sun_cap_fits_the_storage():
    assert 65504.0 * SCALE * SCENE_UNIT_NITS >= SUN_CAP_NITS     # 5.2e8 nits storable
    assert 65504.0 * SCENE_UNIT_NITS < SUN_CAP_NITS               # unscaled it would not


def test_storage_is_as_precise_as_unscaled_down_to_half_a_nit():
    for x in (2e-3, 1e-2, 1e-1, 1.0, 1e2, 1e3):                   # 0.5 nits and up
        assert rel_step(x, SCALE) < 0.002, x
        assert rel_step(x, SCALE) == pytest.approx(rel_step(x, 1.0), rel=1.5)


def test_what_the_scaling_costs_below_that():
    """Below 2e-3 scene units (0.5 nits) the values fall into half-float's subnormal range: a measured, accepted cost."""
    assert rel_step(3e-4, SCALE) < 0.01      # 0.075 nits: under 1% steps
    assert 0.01 < rel_step(1e-4, SCALE) < 0.03   # 0.025 nits: about 2%
    assert rel_step(1e-5, SCALE) > 0.1       # 0.0025 nits: coarse, and invisible: the auto-exposure's window cannot raise it that far
