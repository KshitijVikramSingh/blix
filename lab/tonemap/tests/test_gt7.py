import json
from pathlib import Path

import numpy as np
import pytest

from tmlab import color
from tmlab.gt7 import GT7Params, GT7ToneMapping

GOLDEN = Path(__file__).parent / "golden"


def _cases(ucs):
    doc = json.loads((GOLDEN / f"gt7_{ucs}.json").read_text())
    return doc["points"]


@pytest.mark.parametrize("ucs", ["ictcp", "jzazbz"])
def test_matches_reference_output(ucs):
    """The port against the compiled reference sample, SDR and four HDR peaks."""
    params = GT7Params(ucs=ucs)
    bad = []
    for c in _cases(ucs):
        tm = GT7ToneMapping.sdr_display(params) if c["sdr"] else GT7ToneMapping.hdr_display(c["peak"], params)
        got = tm(np.array(c["in"]))
        # The reference is float32 and goes through PQ pow/exp2 chains: 1e-3 relative is its own noise.
        if not np.allclose(got, c["out"], rtol=2e-3, atol=2e-4):
            bad.append((c["sdr"], c["peak"], c["in"], c["out"], got.tolist()))
    assert not bad, f"{len(bad)} of {len(_cases(ucs))} points differ, first: {bad[0]}"


def test_batched_equals_pointwise():
    tm = GT7ToneMapping.hdr_display(1000.0)
    pts = np.array([c["in"] for c in _cases("ictcp") if c["peak"] == 1000.0 and not c["sdr"]])
    assert np.allclose(tm(pts), np.stack([tm(p) for p in pts]))


@pytest.mark.parametrize("peak", [250.0, 1000.0, 4000.0, 10000.0])
def test_output_bounded_by_peak(peak):
    tm = GT7ToneMapping.hdr_display(peak)
    rng = np.random.default_rng(0)
    rgb = np.exp(rng.uniform(np.log(1e-3), np.log(1e3), size=(2000, 3)))
    out = tm(rgb)
    assert np.all(out >= 0.0) and np.all(out <= peak / 100.0 + 1e-9)


def test_sdr_bounded_zero_one():
    tm = GT7ToneMapping.sdr_display()
    rgb = np.exp(np.random.default_rng(1).uniform(np.log(1e-3), np.log(1e3), size=(2000, 3)))
    out = tm(rgb)
    assert np.all(out >= 0.0) and np.all(out <= 1.0 + 1e-9)


def test_grey_ramp_monotone():
    for tm in (GT7ToneMapping.sdr_display(), GT7ToneMapping.hdr_display(1000.0)):
        x = np.exp(np.linspace(np.log(1e-3), np.log(1e3), 400))
        y = tm(np.stack([x, x, x], axis=-1))[:, 1]
        assert np.all(np.diff(y) >= -1e-6)


def test_curve_is_zero_at_zero_and_converges_above_peak():
    # The shoulder's asymptote is peak * (linear_section + k), above the peak; the operator's final
    # min(., target) is what clips it (the slides: "the shoulder has a sharp clipping point").
    p = GT7Params()
    tm = GT7ToneMapping.hdr_display(1000.0, p)
    k = (p.linear_section - 1.0) / (p.alpha - 1.0)
    assert tm.curve(0.0) == 0.0
    assert abs(float(tm.curve(1e6)) - tm.target * (p.linear_section + k)) < 1e-6
    assert tm.curve(1e6) > tm.target


def test_rejects_hdr_peak_below_paper_white():
    with pytest.raises(ValueError):
        GT7ToneMapping.hdr_display(100.0)


@pytest.mark.parametrize("fwd,inv", [(color.rgb2020_to_ictcp, color.ictcp_to_rgb2020),
                                     (color.rgb2020_to_jzazbz, color.jzazbz_to_rgb2020)])
def test_ucs_round_trip(fwd, inv):
    rng = np.random.default_rng(2)
    rgb = rng.uniform(0.0, 20.0, size=(500, 3))
    assert np.allclose(inv(fwd(rgb)), rgb, rtol=1e-3, atol=1e-4)


def test_shoulder_tail_zero_is_the_published_curve_and_small_tail_approaches_it():
    base = GT7ToneMapping.hdr_display(1000.0)
    x = np.exp(np.linspace(np.log(1e-3), np.log(1e3), 500))
    tiny = base.with_params(shoulder_tail=1e-6)
    assert np.allclose(tiny.curve(x), base.curve(x), atol=1e-4)


@pytest.mark.parametrize("tail", [0.0, 0.3, 1.0, 2.0])
def test_shoulder_joins_knee_with_slope_one_and_stays_monotone(tail):
    tm = GT7ToneMapping.hdr_display(1000.0).with_params(shoulder_tail=tail)
    x0 = tm.params.linear_section * tm.target
    h = 1e-5
    assert abs(float(tm.curve(x0)) - x0) < 1e-9                                  # value continuous at the knee
    assert abs((float(tm.curve(x0 + h)) - float(tm.curve(x0))) / h - 1.0) < 1e-3  # slope 1 on the shoulder side
    x = np.exp(np.linspace(np.log(1e-3), np.log(1e4), 2000))
    assert np.all(np.diff(tm.curve(x)) >= -1e-9)


def test_heavier_tail_stays_below_the_exponential_shoulder():
    # Same knee and asymptote, slower approach: strictly lower beyond the knee.
    base = GT7ToneMapping.hdr_display(1000.0)
    heavy = base.with_params(shoulder_tail=1.0)
    x = np.array([base.params.linear_section * base.target * 1.5, 5.0, 8.0])
    assert np.all(heavy.curve(x) < base.curve(x))
