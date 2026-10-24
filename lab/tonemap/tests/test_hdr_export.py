import zlib

import numpy as np

from tmlab import color, hdr_export


def test_cicp_chunk_comes_before_image_data_with_pq_bt2020(tmp_path):
    sig = np.random.default_rng(0).uniform(0, 1, size=(5, 7, 3))
    f = tmp_path / "a.png"
    hdr_export.write_png16_cicp(f, sig)
    chunks = hdr_export.read_png_chunks(f)
    kinds = [k for k, _ in chunks]
    assert kinds == ["IHDR", "cICP", "IDAT", "IEND"]
    assert dict(chunks)["cICP"] == bytes([9, 16, 0, 1])  # BT.2020 primaries, PQ, identity matrix, full range


def test_16_bit_pixels_round_trip_through_the_file(tmp_path):
    sig = np.random.default_rng(1).uniform(0, 1, size=(4, 6, 3))
    f = tmp_path / "b.png"
    hdr_export.write_png16_cicp(f, sig)
    chunks = dict(hdr_export.read_png_chunks(f))
    raw = zlib.decompress(chunks["IDAT"])
    h, w = 4, 6
    rows = [np.frombuffer(raw[y * (1 + w * 6) + 1:(y + 1) * (1 + w * 6)], dtype=">u2").reshape(w, 3) for y in range(h)]
    back = np.stack(rows) / 65535.0
    assert np.allclose(back, sig, atol=1 / 65535.0 + 1e-9)
    ihdr = chunks["IHDR"]
    assert ihdr[8] == 16 and ihdr[9] == 2  # 16 bits, RGB


def test_pq_encode_decodes_back_to_the_nits():
    fb = np.array([[0.01, 1.0, 10.0], [4.0, 0.5, 16.0]])  # up to 1600 nits
    assert np.allclose(color.eotf_st2084(hdr_export.pq_encode(fb)), fb, rtol=1e-3, atol=1e-5)
    assert float(hdr_export.pq_encode(np.array([100.0]))[0]) == 1.0  # 10,000 nits is PQ's top


def test_sips_conversion_keeps_the_pq_tag_and_ten_bits(tmp_path):
    import shutil, subprocess
    import pytest
    if shutil.which("sips") is None:
        pytest.skip("sips is macOS only")
    f = tmp_path / "c.png"
    hdr_export.write_png16_cicp(f, np.random.default_rng(2).uniform(0, 0.75, size=(32, 32, 3)))
    for fmt in ("heic", "avif"):
        dest = hdr_export.convert_with_sips(f, fmt)
        assert dest is not None
        info = subprocess.run(["sips", "-g", "bitsPerSample", "-g", "profile", str(dest)], capture_output=True, text=True).stdout
        assert "BT.2100 PQ" in info and "10" in info
