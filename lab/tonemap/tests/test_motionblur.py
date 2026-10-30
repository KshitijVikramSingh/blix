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


# ---- screen-space approximations vs shutter integration (see motionblur.evaluate) ------------------------------------------

def synthetic_environment(w=512, h=256):
    """An equirect sky: a gradient, a horizon, and a sun 1000x the sky."""
    lon = (np.arange(w) + 0.5) / w * 2 * np.pi - np.pi
    lat = np.pi / 2 - (np.arange(h) + 0.5) / h * np.pi
    lon, lat = np.meshgrid(lon, lat)
    sky = 0.2 + 0.3 * np.clip(np.sin(lat), 0, 1)
    ground = np.where(np.sin(lon * 6) * np.sin(lat * 20) > 0, 0.15, 0.05)
    env = np.where(lat[..., None] >= 0, sky[..., None] * np.array([0.6, 0.8, 1.0]), ground[..., None] * np.ones(3))
    sun = np.exp(-((lon - 0.15) ** 2 + (lat - 0.12) ** 2) / (2 * 0.02 ** 2)) * 400.0
    return env + sun[..., None]


@pytest.fixture(scope="module")
def study():
    env = synthetic_environment()
    return {(fov, sweep): mb.evaluate(env, sweep, fov, w=240, h=135, taps=16) for fov in (40, 100) for sweep in (0.5, 8.0, 20.0)}


def test_reprojecting_the_centre_frame_is_nearly_the_integration(study):
    """Per-tap reprojection is exact but for resampling and the frame's borders: its interior error stays small at every sweep."""
    for r in study.values():
        assert r["reproject_interior"] < 0.06    # the synthetic sun has a hard edge at 400x: harsher than the real HDRIs (0.3 to 0.9%)


def test_the_linear_velocity_buffer_degrades_with_the_sweep(study):
    for fov in (40, 100):
        small, big = study[(fov, 0.5)], study[(fov, 20.0)]
        assert small["linear_interior"] == pytest.approx(small["reproject_interior"], abs=0.002)   # first order is fine for a small turn
        assert big["linear_interior"] > 1.5 * big["reproject_interior"]                               # and is not for a big one


def test_a_screen_space_method_has_no_data_at_the_borders(study):
    for fov in (40, 100):
        assert study[(fov, 0.5)]["border_fraction"] < study[(fov, 8.0)]["border_fraction"] < study[(fov, 20.0)]["border_fraction"]
        assert study[(fov, 8.0)]["reproject_border"] > 1.2 * study[(fov, 8.0)]["reproject_interior"]
