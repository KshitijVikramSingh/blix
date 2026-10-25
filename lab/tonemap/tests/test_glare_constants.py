"""The glare fit exists in three places (results/glare_fit.json, Glare.cs, present.frag): they must be the same numbers."""
import json
import re
from pathlib import Path

import pytest

from tmlab import glare

DEMO = Path(__file__).resolve().parents[3] / "src/Demos/Blix.Demos.TonemapLab"
FIT = json.loads((glare.RESULTS / "glare_fit.json").read_text())


def floats(text: str):
    return [float(x.rstrip("f")) for x in re.findall(r"[0-9.]+(?:e-?\d+)?f?", text)]


def test_csharp_sigmas_match_the_fit():
    src = (DEMO / "Glare.cs").read_text()
    body = re.search(r"Sigmas = \{([^}]*)\}", src).group(1)
    assert floats(body) == pytest.approx(FIT["sigmas_in_airy_radii"], rel=1e-7)


def test_shader_weights_match_the_fit():
    src = (DEMO / "Shaders/present.frag").read_text()
    body = re.search(r"GLARE_W\[6\] = float\[6\]\(([^)]*)\)", src).group(1)
    assert floats(body) == pytest.approx(FIT["weights"], rel=1e-7)
