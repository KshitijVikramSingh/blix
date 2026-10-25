from dataclasses import replace
from pathlib import Path

import numpy as np
import pytest

from tmlab import adaptation as ad
from tmlab import exposure


def _hist(scale=1.0, seed=0, n=64):
    rng = np.random.default_rng(seed)
    f = np.exp(rng.normal(np.log(0.15), 1.1, size=(n, n, 3))) * scale
    return exposure.log_histogram(f)


def _bases():
    night = _hist(0.02)
    night_frame = np.full((40, 40, 3), 0.004)
    night_frame[:2, :2] = 3000.0  # a few lamps
    return {"night": exposure.log_histogram(night_frame * np.exp(np.random.default_rng(1).normal(0, 0.8, (40, 40, 3)))),
            "cuts": {"1": _hist(1.0), "1.7": _hist(2.0), "3": _hist(5.0)}}


def test_constant_scene_is_at_rest():
    S, A = ad.simulate(np.full(500, -3.0), 0.05, ad.TUNED)
    assert np.allclose(S, -3.0) and np.allclose(A, -3.0)


def test_adaptation_is_equivariant_in_the_level_when_the_limit_moves_with_it():
    # The 12 EV cap is absolute, so a global shift of the scene only commutes with the model if the limit shifts too.
    m = np.r_[np.zeros(100), np.full(300, -6.0), np.zeros(200)]
    S0, A0 = ad.simulate(m, 0.05, ad.TUNED)
    S1, A1 = ad.simulate(m + 5.0, 0.05, replace(ad.TUNED, light_limit=ad.TUNED.light_limit + 5.0))
    assert np.allclose(S1, S0 + 5.0) and np.allclose(A1, A0 + 5.0)


def test_the_absolute_cap_is_what_breaks_level_equivariance():
    m = np.r_[np.zeros(100), np.full(600, 6.0)]            # brighter than the light limit allows A to follow
    _, A0 = ad.simulate(m, 0.5, ad.TUNED)
    _, A1 = ad.simulate(m + 5.0, 0.5, ad.TUNED)             # the same scene 5 EV higher, limit left where it was
    assert A0[-1] <= ad.TUNED.light_limit + 1e-9 and A1[-1] <= ad.TUNED.light_limit + 1e-9
    assert not np.allclose(A1 - 5.0, A0)


def test_long_term_level_never_leaves_its_twelve_ev_span_and_the_night_beyond_it_stays_dark():
    p = ad.TUNED
    for level in (-30.0, 30.0):
        m = np.r_[np.zeros(20), np.full(1800, level)]
        S, A = ad.simulate(m, 2.0, p)
        assert A.min() >= p.light_limit - p.long_range - 1e-9 and A.max() <= p.light_limit + 1e-9
    S, A = ad.simulate(np.r_[np.zeros(20), np.full(1800, -30.0)], 2.0, p)
    assert (-30.0 - S[-1]) < -10.0  # 30 EV down: a long way from proper, because only 12 + 2 EV can be followed


def test_short_term_level_stays_inside_the_window_around_the_long_term_one():
    m = np.r_[np.zeros(100), np.full(2000, -20.0)]
    S, A = ad.simulate(m, 0.05, ad.TUNED)
    p = ad.TUNED
    assert np.all(S >= A - p.range_dark - 1e-9) and np.all(S <= A + p.range_light + 1e-9)
    assert S[-1] > m[-1] + 1.0  # a 20 EV drop is NOT fully followed within 100 s: night stays darker than proper


def test_dark_adaptation_is_slower_than_light_adaptation():
    down = np.r_[np.zeros(100), np.full(600, -3.0)]
    up = np.r_[np.zeros(100), np.full(600, 3.0)]
    t_down = ad.first_time(np.arange(700) * 0.05, np.abs(down - ad.simulate(down, 0.05, ad.TUNED)[0]) < 0.5, 5.0)
    t_up = ad.first_time(np.arange(700) * 0.05, np.abs(up - ad.simulate(up, 0.05, ad.TUNED)[0]) < 0.5, 5.0)
    assert t_down > t_up


def test_meter_is_exposure_equivariant_and_trim_ignores_a_small_bright_patch_but_not_a_large_one():
    h = _bases()["night"]
    med = ad.median_level(h)
    assert ad.meter_ev(h, 0.05, gain_ev=2.0) == pytest.approx(ad.meter_ev(h, 0.05) + 2.0)
    base = ad.meter_ev(h, 0.05)
    sign = abs(ad.meter_ev(h, 0.05, extras=[(0.005, med + 12)]) - base)
    untrimmed = abs(ad.meter_ev(h, 0.0, extras=[(0.005, med + 12)]) - base)
    assert sign < 0.5 * untrimmed  # trimming at least halves the sign's pull on the meter
    assert ad.meter_ev(h, 0.05, extras=[(0.2, med + 6)]) - base > 0.4  # a sky is not ignored


def test_two_stage_model_stays_dark_at_dusk_where_smoothing_alone_cannot():
    bases = _bases()
    two, _ = ad.run(ad.TUNED, bases)
    one, _ = ad.run(ad.smoothing_only(ad.TUNED), bases)
    assert two["dusk.lag_at_dark"] <= -1.5 and abs(one["dusk.lag_at_dark"]) < 0.1
    assert two["dusk.night_stays_dark"] <= -0.5 and abs(one["dusk.night_stays_dark"]) < 0.1   # only a cap keeps the night dark
    assert two["snow.stays_blinding"] >= 1.0 and one["snow.stays_blinding"] < 0.2
    assert two["dawn.asymmetry"] <= 0.5 and one["dawn.asymmetry"] > 0.9  # a single smoother is symmetric


@pytest.mark.skipif(not Path("data/metering").exists(), reason="needs the captured metering set")
def test_every_spec_holds_for_the_tuned_constant_on_the_real_frames():
    bases = ad.load_bases(Path("data/metering"))
    values, _ = ad.run(ad.TUNED, bases)
    missed = [(s.name, v, s.lo, s.hi) for s, v, m in ad.violations(values) if m > 1e-9]
    assert not missed, missed
