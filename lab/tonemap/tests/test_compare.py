import numpy as np

from tmlab import compare, hdr_export
from tmlab.gt7 import GT7Params


def _write_pfm(path, rgb):
    h, w, _ = rgb.shape
    with open(path, "wb") as f:
        f.write(f"PF\n{w} {h}\n-1.0\n".encode())
        f.write(np.flipud(rgb).astype("<f4").tobytes())


def test_sheet_is_written_with_hdr_tag_and_the_right_size(tmp_path):
    rng = np.random.default_rng(0)
    # 4x the panel size, so downsampling by 4 gives exactly one panel; bright blob so the baseline clips somewhere
    img = rng.uniform(0.01, 0.4, size=(compare.PH * 4, compare.PW * 4, 3)).astype(np.float32)
    img[200:600, 300:900] = 8.0   # 2000 nits at the 250-nit anchor: twice the peak
    _write_pfm(tmp_path / "s__dim.pfm", img)
    made, rows = compare.build(["s__dim"], 1000.0, GT7Params(alpha=0.085, linear_section=0.39, shoulder_tail=1.013), 250.0, tmp_path, tmp_path / "out", "t")
    assert {"comparison_preview.png", "comparison_hdr.png"} <= set(made)
    chunks = hdr_export.read_png_chunks(tmp_path / "out" / "comparison_hdr.png")
    assert [k for k, _ in chunks][:2] == ["IHDR", "cICP"] and dict(chunks)["cICP"] == bytes([9, 16, 0, 1])
    a = rows[0][1]
    assert a["clip_b"].any()                       # the blob is far above 1000 nits: the baseline pins it
    assert a["dE"].max() > 2.0                     # and the tuned shoulder differs there
    assert np.all(a["dE"][:50, :50] < 1.0)         # untouched shadows/mid-tones agree
