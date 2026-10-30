import numpy as np
import pytest

from tmlab import fit
from tmlab.gt7 import GT7Params


def test_vector_round_trip():
    p = GT7Params()
    assert fit.to_params(fit.to_vector(p)) == p


def test_baseline_is_valid_and_components_finite():
    obj = fit.SdrObjective()
    c = obj.components(fit.to_vector(obj.baseline))
    assert all(np.isfinite(v) for v in c.values())
    assert c["penalty"] == 0.0  # GT7's own parameters are monotone and join their shoulder
    assert c["toe"] == pytest.approx(0.0, abs=1e-9)  # the toe target is the baseline itself


def test_join_penalty_fires_when_shoulder_cannot_join():
    obj = fit.SdrObjective()
    v = fit.to_vector(obj.baseline)
    v[fit.NAMES.index("gray_point")] = 1.5   # 1.5 > linear_section(0.444) * 2.5
    assert obj.components(v)["penalty"] > 0.0


def test_weights_scale_the_total():
    v = fit.to_vector(fit.SdrObjective().baseline)
    base = fit.SdrObjective()(v)
    heavy = fit.SdrObjective(weights={"hue": 10.0})(v)
    assert heavy > base


def test_fit_improves_on_baseline_and_respects_bounds():
    obj = fit.SdrObjective()
    params, _, res = fit.fit(starts=2, seed=0, objective=obj)
    assert res.fun < obj(fit.to_vector(obj.baseline))
    for n, (lo, hi) in zip(fit.NAMES, fit.BOUNDS):
        assert lo - 1e-9 <= getattr(params, n) <= hi + 1e-9
