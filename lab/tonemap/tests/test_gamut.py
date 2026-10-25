"""Gamut handling after the tone map (see color.gamut_map, the README's colour-space contract).

Blix's scene-linear primaries are Rec.709/sRGB by construction (sRGB-decoded textures, an sRGB swapchain), and GT7 works in linear
Rec.2020, so the pipeline is 709 -> 2020 -> tone map -> 2020 -> 709. A colour the tone map leaves outside the display's gamut comes out
with a negative channel; these tests pin what the map does with it.
"""
import os
import subprocess
from pathlib import Path

import numpy as np
import pytest

from tmlab import color, pipeline
from tmlab.fit import _HUES709
from tmlab.gt7 import GT7Params, GT7ToneMapping

LAB = Path(__file__).resolve().parents[1]
REPO = LAB.parents[1]
BUILT = (REPO / "src/Demos/Blix.Demos.TonemapLab/bin/Release/net8.0/Blix.Demos.TonemapLab.dll").exists()
HUES = np.vstack([_HUES709, [1.0, 1.0, 1.0]])


def test_p3_matrices_are_consistent():
    assert color.REC709_TO_P3 @ np.ones(3) == pytest.approx(np.ones(3), abs=1e-6)         # same white point
    assert color.P3_TO_REC709 @ color.REC709_TO_P3 == pytest.approx(np.eye(3), abs=1e-9)
    assert color.LUMA_P3.sum() == pytest.approx(1.0, abs=1e-6)
    # the same absolute luminance whichever RGB it is computed in
    c = np.array([0.3, 0.6, 0.1])
    assert (color.REC709_TO_P3 @ c) @ color.LUMA_P3 == pytest.approx(c @ color.LUMA709, abs=1e-5)


def test_in_gamut_colours_are_untouched():
    c = np.random.default_rng(0).random((200, 3)) * 3.0
    assert color.gamut_map_709(c) == pytest.approx(c)
    assert color.gamut_map(c, "p3") == pytest.approx(c)


def test_709_map_keeps_luminance_and_lands_on_the_boundary():
    c = np.array([[-0.05, 0.9, 0.1], [0.4, -0.02, 1.5], [2.0, 1.0, -0.3], [-0.1, 0.05, 0.8]])
    out = color.gamut_map_709(c)
    assert np.all(out >= 0.0)
    assert out @ color.LUMA709 == pytest.approx(c @ color.LUMA709, abs=1e-12)
    assert np.all(np.isclose(out.min(axis=1), 0.0, atol=1e-12))      # the least desaturation that fixes it


def test_p3_map_keeps_p3_colours_and_maps_the_rest_to_the_p3_edge():
    p3_green = color.P3_TO_REC709 @ np.array([0.0, 1.0, 0.0])            # inside P3, outside Rec.709
    assert p3_green.min() < -0.05
    assert color.gamut_map(p3_green, "p3") == pytest.approx(p3_green, abs=1e-9)
    assert color.gamut_map_709(p3_green).min() >= 0.0                        # while the 709 target gives up the chroma
    rec2020_green = color.REC2020_TO_REC709 @ np.array([0.0, 1.0, 0.0])      # outside P3 as well
    out = color.gamut_map(rec2020_green, "p3")
    in_p3 = color.REC709_TO_P3 @ out
    assert in_p3.min() == pytest.approx(0.0, abs=1e-9)
    assert in_p3 @ color.LUMA_P3 == pytest.approx(rec2020_green @ color.LUMA709, abs=1e-7)   # same luminance (the matrices carry 7 digits)


def test_mapping_beats_clipping_on_hue_for_saturated_gt7_output():
    """Saturated hues through GT7 SDR leave Rec.709 by a little; clipping shifts hue, the map shifts it less and keeps luminance."""
    tm = GT7ToneMapping.sdr_display()
    scene = np.array([h * 0.18 * 2.0 ** ev for h in HUES[:6] for ev in np.linspace(-6, 8, 57)])
    raw = color.rec2020_to_rec709(tm(pipeline.to_gt7_input(scene)))
    bad = raw.min(axis=-1) < -1e-9
    assert bad.mean() > 0.1                                   # a real share of fully saturated samples does leave the gamut

    def hue(rgb):
        lab = color.rec709_to_oklab(rgb)
        return np.degrees(np.arctan2(lab[..., 2], lab[..., 1]))

    def shift(img):
        return np.abs((hue(img) - hue(raw) + 180.0) % 360.0 - 180.0)[bad]

    clipped, mapped = np.clip(raw, 0, 1), np.clip(color.gamut_map_709(raw), 0, 1)
    assert shift(mapped).mean() < shift(clipped).mean()
    assert shift(mapped).max() < shift(clipped).max()
    assert np.abs(mapped @ color.LUMA709 - clipped @ color.LUMA709)[bad].max() < 5e-4    # and it is a gamut fix, not a tone change


@pytest.mark.skipif(not BUILT, reason="the TonemapLab demo is not built in Release")
@pytest.mark.parametrize("flag,mapped", [("off", False), ("on", True)])
def test_glsl_gamut_map_matches_python(tmp_path, flag, mapped):
    """The shader's SDR path (hue ramp through exposure 1, GT7 published) with the map off and on, against pipeline.gt7_sdr."""
    from tmlab import capture
    out = tmp_path / f"gamut_{flag}"
    args = ["--gt7", "published", "--pattern", "2", "--exposure", "0", "--capture", str(out), "--capture-frame", "8"]
    if not mapped:
        args += ["--gamut", "off"]
    env = {k: v for k, v in os.environ.items() if k != "BLIX_HDR"}
    done = subprocess.run([str(REPO / "blix"), "run", "Blix.Demos.TonemapLab", *args], cwd=REPO, env={**env, "BLIX_CONFIG": "Release"},
                          capture_output=True, text=True, timeout=300)
    assert done.returncode == 0, done.stdout[-600:] + done.stderr[-600:]
    cap = capture.load(tmp_path / f"gamut_{flag}.pfm")
    got = cap.rgb[::8, ::8].astype(np.float64)
    h, w, _ = cap.rgb.shape
    ys, xs = np.mgrid[0:h:8, 0:w:8]
    u, v = (xs + 0.5) / w, (ys + 0.5) / h
    scene = HUES[np.clip((v * 7).astype(int), 0, 6)] * (10.0 ** (-3.0 + 6.0 * u))[..., None]
    want = pipeline.gt7_sdr(scene, GT7ToneMapping.sdr_display(GT7Params()), gamut_map=mapped)
    assert np.all(np.abs(got - want) <= np.maximum(0.003 * np.abs(want), 0.002)), float(np.abs(got - want).max())
