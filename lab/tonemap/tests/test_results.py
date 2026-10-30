import json
from pathlib import Path

import numpy as np

from tmlab import fit, fit_hdr
from tmlab.gt7 import GT7Params, GT7ToneMapping

RESULT = json.loads((Path(__file__).resolve().parents[1] / "results" / "hdr_tuned.json").read_text())


def test_chosen_hdr_parameters_are_inside_the_fit_bounds():
    for name, value in RESULT["params"].items():
        lo, hi = fit.BOUNDS_BY[name]
        assert lo <= value <= hi, name


def test_chosen_hdr_parameters_hold_what_they_were_chosen_for():
    p = GT7Params(**RESULT["params"])
    obj = fit_hdr.HdrObjective(names=fit.NAMES_TAIL, floor=RESULT["fit"]["highlight_floor"])
    c = obj.components(fit.to_vector(p, fit.NAMES_TAIL))
    assert c["shoulder"] < 0.01      # no clamp corner at any of the four peaks
    assert c["highlight"] < 0.01     # highlights stay within the floor of baseline GT7
    assert c["hue"] < 0.5 and c["chroma"] < 0.5 and c["penalty"] == 0.0
    # and a scene value at the display peak lands where the file says it does
    for peak in (400.0, 1000.0, 4000.0):
        tm = GT7ToneMapping.hdr_display(peak, p)
        got = float(tm(np.full(3, peak / 100.0))[1] / (peak / 100.0) * 100)
        assert abs(got - RESULT["at_1000_nits"]["grey_percent_of_peak_at_scene_k_x_peak"]["1"]) < 0.5
