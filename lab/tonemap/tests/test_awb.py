"""Auto white balance: the lab's specs for it, written before the constants were chosen (see awb.py for the model)."""
from dataclasses import replace

import numpy as np
import pytest

from tmlab import awb

P = awb.Awb()


def scene(kelvin: float, seed: int = 0, n: int = 48, level: float = 0.18, tint: float = 0.0):
    """Reflectances under a light of `kelvin`: a spread of greys and pastel surfaces, lit so that neutral = light colour."""
    rng = np.random.default_rng(seed)
    refl = np.clip(0.5 + rng.normal(0, 0.12, (n, n, 3)), 0.05, 1.0) * np.exp(rng.normal(0, 0.5, (n, n, 1)))
    light = awb._light_rgb(1e6 / kelvin) * np.array([1.0, 1.0 + tint, 1.0])
    return refl * light * level


def settle(frames, p=P, seconds=60.0, dt=0.1, start=None, ev=-3.0):
    mean = awb.measure_mean(frames, p)
    return awb.simulate([(mean, ev)] * int(seconds / dt), dt, p, start)


def test_d65_gains_are_identity_and_keep_luminance():
    assert awb.gains(awb.D65_MIRED, 0.0) == pytest.approx([1, 1, 1], abs=1e-9)
    for k in (2700, 5000, 9000):
        g = awb.gains(1e6 / k, 0.05)
        assert g @ awb.LUMA == pytest.approx(1.0)


@pytest.mark.parametrize("kelvin", [2700, 3200, 5000, 6504, 8000, 10000])
def test_measured_cast_recovers_the_light(kelvin):
    m, t = awb.cast_of(awb.measure_mean(scene(kelvin), P), P)
    assert m == pytest.approx(1e6 / kelvin, abs=15.0)   # mired: equal steps look about equal. The saturation discount
    # reads a strong cast a little low (a cast saturates every pixel, so all are discounted): the safe side of the error
    assert abs(t) < 0.04


def test_neutral_daylight_stays_put():
    trace = settle(scene(6504))
    assert abs(trace[-1][0] - awb.D65_MIRED) < 8 and abs(trace[-1][1]) < 0.03


def test_partial_adaptation_removes_most_not_all_of_the_cast():
    """Tungsten: a neutral ends up closer to white than it started but still warm; strength 1 would be neutral."""
    trace = settle(scene(3000))
    state = awb.State(P)
    state.reset(*trace[-1])
    out = awb._light_rgb(1e6 / 3000) * state.gains()
    warm_before = awb._light_rgb(1e6 / 3000)
    cast = lambda c: np.log(c[0] / c[2])   # red over blue, 0 = neutral
    # the estimate may sit a little under the true light (virtual light, trim) but never over; the cast removed is
    # between half and 0.9 of it
    removed = 1.0 - cast(out) / cast(warm_before)
    assert 0.45 < removed < 0.9
    assert cast(out) > 0.05   # still visibly warm


def test_strength_zero_is_off_and_one_is_full():
    off = replace(P, strength=0.0)
    s = awb.State(off); s.reset(1e6 / 3000, 0.05)
    assert s.gains() == pytest.approx([1, 1, 1], abs=1e-9)
    full = replace(P, strength=1.0, virtual=0.0)
    s = awb.State(full); s.reset(1e6 / 3000, 0.0)
    out = awb._light_rgb(1e6 / 3000) * s.gains()
    assert out / out[1] == pytest.approx([1, 1, 1], abs=1e-6)


def test_step_change_is_a_first_order_response_without_overshoot():
    trace = settle(scene(3000), seconds=40.0, start=(awb.D65_MIRED, 0.0))
    final = trace[-1][0]
    delta = final - awb.D65_MIRED
    one_tau = trace[int(P.tau / 0.1) - 1][0] - awb.D65_MIRED
    assert one_tau / delta == pytest.approx(1 - np.exp(-1), abs=0.03)
    assert np.all(np.diff(trace[:, 0]) >= -1e-9)           # monotone toward the new light
    assert np.max(trace[:, 0]) <= final + 1e-6             # never past it


def test_estimate_respects_the_limits():
    p = P
    for kelvin in (1700, 25000):
        trace = settle(scene(kelvin, tint=0.6), p)
        assert 1e6 / p.kelvin_lo + 1e-6 >= trace[:, 0].max() and trace[:, 0].min() >= 1e6 / p.kelvin_hi - 1e-6
        assert np.abs(trace[:, 1]).max() <= p.tint_limit + 1e-9


def test_a_dark_scene_is_not_corrected():
    """At night the frame's colour says nothing about the light: the estimate relaxes to D65 whatever the frame's cast."""
    trace = settle(scene(2500), ev=-12.0, start=(1e6 / 2500, 0.05), seconds=120.0)
    assert abs(trace[-1][0] - awb.D65_MIRED) < 3 and abs(trace[-1][1]) < 0.005


def test_virtual_light_keeps_a_floor_in_bright_scenes():
    trace = settle(scene(3000), seconds=200.0)
    # never reaches the full measured shift: (1 - virtual) of it
    shift = (trace[-1][0] - awb.D65_MIRED) / (awb.cast_of(awb.measure_mean(scene(3000), P), P)[0] - awb.D65_MIRED)
    assert shift == pytest.approx(1.0 - P.virtual, abs=0.02)


def test_a_green_meadow_is_not_read_as_a_green_light():
    """80% foliage under D65: saturation discounting and the tint limit keep the correction small."""
    rng = np.random.default_rng(3)
    n = 48
    refl = np.where(rng.random((n, n, 1)) < 0.8, np.array([0.12, 0.35, 0.08]), np.array([0.4, 0.4, 0.4])) * np.exp(rng.normal(0, 0.3, (n, n, 1)))
    trace = settle(refl * 0.3)
    applied_tint = P.strength * trace[-1][1]
    assert abs(applied_tint) < 0.06
    assert abs(P.strength * (trace[-1][0] - awb.D65_MIRED)) < 40   # under ~ 500 K at D65


def test_trim_ignores_a_lamp():
    frame = scene(6504)
    plain = awb.cast_of(awb.measure_mean(frame, P), P)
    frame = frame.copy(); frame[:4, :4] = np.array([40.0, 12.0, 1.0])   # a sodium lamp over 0.7% of the frame
    lamped = awb.cast_of(awb.measure_mean(frame, P), P)
    assert lamped[0] == pytest.approx(plain[0], abs=6.0)


def test_exposure_is_not_moved_by_white_balance():
    g = awb.gains(1e6 / 3000, 0.05)
    assert float(np.array([0.18, 0.18, 0.18]) @ g) == pytest.approx(0.18, rel=1e-6) or g @ awb.LUMA == pytest.approx(1.0)


def test_sky_mask_stops_a_blue_sky_reading_as_a_cool_light():
    """Half the frame is blue sky over neutral ground in D65: unmasked it reads cool; masked it reads neutral."""
    n = 48
    ground = scene(6504, n=n)
    sky = np.broadcast_to(np.array([0.3, 0.5, 1.0]) * 0.5, (n, n, 3)).copy()
    mask = np.zeros((n, n), dtype=np.float32)
    mask[: n // 2] = 1.0
    frame = np.where(mask[..., None] > 0, sky, ground)
    plain = awb.cast_of(awb.measure_mean(frame, P), P)[0]
    masked = awb.cast_of(awb.measure_mean(frame, P, mask), P)[0]
    assert abs(masked - awb.D65_MIRED) < abs(plain - awb.D65_MIRED) / 2
    assert abs(masked - awb.D65_MIRED) < 20   # a faint remainder: the sky still counts for sky_weight
