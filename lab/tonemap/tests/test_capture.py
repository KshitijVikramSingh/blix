import json

import numpy as np

from tmlab import capture


def _write_pfm(path, rgb):
    h, w, _ = rgb.shape
    with open(path, "wb") as f:
        f.write(f"PF\n{w} {h}\n-1.0\n".encode())
        f.write(np.flipud(rgb).astype("<f4").tobytes())  # bottom row first, as the format (and PfmWriter) has it


def test_pfm_round_trip_preserves_values_and_row_order(tmp_path):
    rgb = np.random.default_rng(0).uniform(0, 50, size=(6, 9, 3)).astype(np.float32)
    _write_pfm(tmp_path / "a.pfm", rgb)
    (tmp_path / "a.pfm.json").write_text(json.dumps({"exposure": 2.0}))
    cap = capture.load(tmp_path / "a.pfm")
    assert np.array_equal(cap.rgb, rgb) and cap.exposure == 2.0 and cap.name == "a"


def test_missing_sidecar_defaults_exposure_to_one(tmp_path):
    _write_pfm(tmp_path / "b.pfm", np.ones((2, 2, 3), np.float32))
    assert capture.load(tmp_path / "b.pfm").exposure == 1.0


def test_downsample_averages(tmp_path):
    rgb = np.zeros((4, 4, 3), np.float32)
    rgb[:2, :2] = 8.0
    _write_pfm(tmp_path / "c.pfm", rgb)
    small = capture.load(tmp_path / "c.pfm").downsampled(2)
    assert small.rgb.shape == (2, 2, 3) and small.rgb[0, 0, 0] == 8.0 and small.rgb[1, 1, 0] == 0.0


def test_summary_flags_nonfinite(tmp_path):
    rgb = np.ones((4, 4, 3), np.float32)
    rgb[0, 0, 0] = np.nan
    _write_pfm(tmp_path / "d.pfm", rgb)
    assert capture.summary(capture.load(tmp_path / "d.pfm"))["nan_or_inf"] == 1
